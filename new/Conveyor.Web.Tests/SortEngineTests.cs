using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Conveyor.Web.Tests;

public sealed class SortEngineTests
{
    [Fact]
    public void PlcDisplayIgnoresValue68AndKeepsThePreviousValue()
    {
        var line = Line();
        var repository = new FakeRepository();
        var controller = new LineController(line, false, repository, new MotionPlc(), false,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLogger.Instance, () => { });

        controller.RecordPlcReception("39");
        controller.RecordPlcReception(" 68\0");

        var input = controller.Snapshot().PlcInput;
        Assert.NotNull(input);
        Assert.Equal("39", input.Raw);
        Assert.Equal(1, input.Sequence);
    }

    [Fact]
    public async Task PersistentTransferCode68CountsEveryNewCameraParcelOnEachLine()
    {
        var config = new ConveyorOptions
        {
            Simulation = true,
            Lines =
            [
                new() { Id = 0, CorrelationDelayMs = 0, Plc = new() { TransferTag = "TRANSFER_1" } },
                new() { Id = 1, CorrelationDelayMs = 0, Plc = new() { TransferTag = "TRANSFER_2" } }
            ]
        };
        config.ApplyGlobalSorting();
        var repository = new FakeRepository();
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repository,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance, new TestConfigurationEditor());
        try
        {
            supervisor.RecordPlcTagChange("TRANSFER_1", "68");
            supervisor.RecordPlcTagChange("TRANSFER_2", "67");
            await supervisor.SimulateParcelAsync(0, "12345678901", new Dimension(12, 8, 5), 4.75m);
            await supervisor.SimulateParcelAsync(0, "12345678902", new Dimension(12, 8, 5), 4.75m);
            await supervisor.SimulateParcelAsync(1, "12345678901", new Dimension(12, 8, 5), 4.75m);
            Assert.Equal(2, supervisor.GetSnapshots()[0].Counters.Code68);
            Assert.Equal(0, supervisor.GetSnapshots()[1].Counters.Code68);

            supervisor.RecordPlcTagChange("TRANSFER_2", "68");
            await supervisor.SimulateParcelAsync(1, "12345678902", new Dimension(12, 8, 5), 4.75m);
            Assert.Equal(1, supervisor.GetSnapshots()[1].Counters.Code68);

            supervisor.ResetCounters(0);
            await supervisor.SimulateParcelAsync(0, "12345678901", new Dimension(12, 8, 5), 4.75m);
            Assert.Equal(1, supervisor.GetSnapshots()[0].Counters.Code68);
        }
        finally
        {
            await supervisor.StopLineAsync(0);
            await supervisor.StopLineAsync(1);
        }
    }

    [Fact]
    public async Task Camera_uses_previously_received_weight_and_dimensions_then_sends_chute()
    {
        var line = Line();
        var ports = GetAvailablePorts(3);
        line.CameraPort = ports[0];
        line.DimensionPort = ports[1];
        line.ScalePort = ports[2];
        line.CorrelationDelayMs = 0;
        line.CorrelationWindowMs = 2_000;
        var repository = new FakeRepository();
        var plc = new MotionPlc();
        var controller = new LineController(line, false, repository, plc, false,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLogger.Instance, () => { });

        await controller.StartAsync();
        using var camera = new TcpClient();
        using var dimensioner = new TcpClient();
        using var scale = new TcpClient();
        try
        {
            await camera.ConnectAsync(IPAddress.Loopback, line.CameraPort);
            await dimensioner.ConnectAsync(IPAddress.Loopback, line.DimensionPort);
            await scale.ConnectAsync(IPAddress.Loopback, line.ScalePort);

            await dimensioner.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\u00020000012400810052\u0003"));
            await scale.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\u0002028.85LB\r\n"));
            await Task.Delay(100);
            await camera.GetStream().WriteAsync(Encoding.ASCII.GetBytes("12345678901\r"));

            SortDecision? decision = null;
            for (var attempt = 0; attempt < 100 && decision is null; attempt++)
            {
                decision = controller.Snapshot().LastDecision;
                if (decision is null) await Task.Delay(25);
            }

            Assert.NotNull(decision);
            Assert.Equal(28.85m, decision.Weight);
            Assert.Equal(new Dimension(12.4m, 8.1m, 5.2m), decision.Dimension);
            Assert.Contains(plc.Commands, command => command.Tag == line.Plc.ChuteTag && command.Value == 4);

            await camera.GetStream().WriteAsync(Encoding.ASCII.GetBytes("12345678902\r"));
            SortDecision? nextDecision = null;
            for (var attempt = 0; attempt < 100 && nextDecision?.Barcode != "12345678902"; attempt++)
            {
                nextDecision = controller.Snapshot().LastDecision;
                if (nextDecision?.Barcode != "12345678902") await Task.Delay(25);
            }
            Assert.NotNull(nextDecision);
            Assert.Equal(-1, nextDecision.Weight);
            Assert.Equal(Dimension.Missing, nextDecision.Dimension);
        }
        finally
        {
            await controller.StopAsync();
        }
    }

    [Fact]
    public async Task Three_missing_scale_readings_count_fault_and_manual_test_sends_direct_pulse()
    {
        var line = Line();
        line.Id = 1;
        line.CorrelationDelayMs = 0;
        line.Plc.ScaleFaultTag = "FAUTE_M30";
        line.Plc.ScaleFaultParcelThreshold = 3;
        line.Plc.ScaleFaultPulseMs = 10;
        var repository = new FakeRepository();
        var plc = new MotionPlc();
        var controller = new LineController(line, true, repository, plc, false,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLogger.Instance, () => { });

        await controller.StartAsync();
        try
        {
            controller.RecordScalePresenceForParcel(false);
            controller.RecordScalePresenceForParcel(false);
            Assert.Equal(0, controller.Snapshot().Counters.ScaleFaults);

            controller.RecordScalePresenceForParcel(false);
            for (var attempt = 0; attempt < 100 && plc.Commands.Count(command => command.Tag == "FAUTE_M30") < 2; attempt++)
                await Task.Delay(10);

            Assert.Equal(1, controller.Snapshot().Counters.ScaleFaults);
            Assert.Equal([1, 0], plc.Commands.Where(command => command.Tag == "FAUTE_M30").Select(command => command.Value).ToArray());

            await controller.TriggerScaleFaultTestAsync();
            Assert.Equal(1, controller.Snapshot().Counters.ScaleFaults);
            Assert.Equal([1, 0, 1, 0], plc.Commands.Where(command => command.Tag == "FAUTE_M30").Select(command => command.Value).ToArray());
        }
        finally
        {
            await controller.StopAsync();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Line_count_limits_supervision_without_removing_saved_configuration(int count)
    {
        var config = new ConveyorOptions { LineCount = count, Simulation = true,
            Lines = [new() { Id = 0 }, new() { Id = 1, CameraHost = "saved-camera" }] };
        var repository = new FakeRepository();
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repository,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance, new TestConfigurationEditor());
        Assert.Equal(count, supervisor.GetSnapshots().Count);
        Assert.Equal(2, config.Lines.Count);
        Assert.Equal("saved-camera", config.Lines[1].CameraHost);
        if (count == 1) Assert.Throws<KeyNotFoundException>(() => supervisor.SetCode98Enabled(1, false));
        var saved = System.Text.Json.JsonSerializer.Serialize(config);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ConveyorOptions>(saved)!;
        Assert.Equal(count, restored.LineCount);
        restored.LineCount = 2;
        Assert.Equal(2, restored.GetConfiguredLines().Count());
        Assert.Equal("saved-camera", restored.Lines[1].CameraHost);
    }

    [Fact]
    public void Legacy_line_count_defaults_to_number_of_configured_lines()
    {
        Assert.Equal(1, new ConveyorOptions { Lines = [new() { Id = 0 }] }.LineCount);
        Assert.Equal(2, new ConveyorOptions { Lines = [new() { Id = 0 }, new() { Id = 1 }] }.LineCount);
    }

    [Fact]
    public async Task Pending_alert_updates_and_clears_after_old_scans_are_removed()
    {
        var repository = new FakeRepository { ScanCount = 7, HasOverdueScans = true };
        using var metrics = new DatabaseMetricsService(repository, NullLogger<DatabaseMetricsService>.Instance);
        await metrics.RefreshAsync();
        Assert.Equal(7, metrics.Current.Scans);
        Assert.True(metrics.Current.HasOverdueScans);
        repository.FailCounts = true;
        await metrics.RefreshAsync();
        Assert.True(metrics.Current.HasOverdueScans);
        Assert.False(metrics.Current.Connected);
        repository.FailCounts = false;
        repository.ScanCount = 2;
        repository.HasOverdueScans = false;
        await metrics.RefreshAsync();
        Assert.Equal(2, metrics.Current.Scans);
        Assert.False(metrics.Current.HasOverdueScans);
        Assert.True(metrics.Current.Connected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_contains_shipment_metadata_not_camera_postal_code(bool disabled)
    {
        var engine = new SortEngine(new FakeRepository { Disable98 = disabled }, NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("12345678901,H2X1Y4,99999999999"), CancellationToken.None);
        Assert.Equal("12345678901", result.Barcode);
        Assert.Equal("G1K 3X2", result.DestinationPostalCode);
        Assert.Equal(10, result.RouteId);
        Assert.Equal(disabled, result.DisableCode98);
    }

    [Theory]
    [InlineData("99999999999,H2X1Y4")]
    [InlineData("12345678901,12345678902")]
    public async Task Missing_or_ambiguous_shipment_does_not_display_invented_metadata(string camera)
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel(camera), CancellationToken.None);
        Assert.Null(result.DestinationPostalCode);
        Assert.Null(result.RouteId);
        Assert.Null(result.DisableCode98);
    }

    [Fact]
    public async Task Database_counter_reads_current_history_total_including_decreases()
    {
        var repository = new FakeRepository { ScanCount = 1234 };
        using var metrics = new DatabaseMetricsService(repository, NullLogger<DatabaseMetricsService>.Instance);
        await metrics.RefreshAsync();
        Assert.Equal(1234, metrics.Current.Scans);
        repository.ScanCount = 12;
        await metrics.RefreshAsync();
        Assert.Equal(12, metrics.Current.Scans);
    }

    [Fact]
    public async Task Database_counter_keeps_last_total_when_read_fails()
    {
        var repository = new FakeRepository { ScanCount = 1234 };
        using var metrics = new DatabaseMetricsService(repository, NullLogger<DatabaseMetricsService>.Instance);
        await metrics.RefreshAsync();
        repository.FailCounts = true;
        await metrics.RefreshAsync();
        Assert.Equal(1234, metrics.Current.Scans);
        Assert.False(metrics.Current.Connected);
    }

    [Fact]
    public async Task Known_waybill_uses_configured_route()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("12345678901,H2X1Y4"), CancellationToken.None);
        Assert.Equal(4, result.Chute);
        Assert.Equal("Route de l'expédition", result.Reason);
    }

    [Fact]
    public async Task Known_waybill_without_configured_route_is_not_reported_as_missing()
    {
        var engine = new SortEngine(new FakeRepository { RouteChute = null }, NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("12345678901"), CancellationToken.None);
        Assert.Equal(16, result.Chute);
        Assert.Equal("Route de l'expédition non configurée", result.Reason);
    }

    [Fact]
    public async Task Unknown_waybill_can_fall_back_to_postal_code()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("99999999999,H2X1Y4"), CancellationToken.None);
        Assert.Equal(7, result.Chute);
        Assert.Equal("Route du code postal", result.Reason);
    }

    [Fact]
    public async Task Multiple_known_waybills_use_safety_chute_99_and_plc_reject()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("12345678901,12345678902"), CancellationToken.None);
        Assert.Equal(99, result.Chute);
        Assert.Equal(16, result.PlcChute);
    }

    [Fact]
    public async Task Invalid_measurements_use_code_98()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var parcel = Parcel("12345678901") with { Weight = -1, Dimension = Dimension.Missing };
        var result = await engine.DecideAsync(Line(), parcel, CancellationToken.None);
        Assert.Equal(98, result.Chute);
    }

    private static LineOptions Line() => new() { Id = 0, ShiftId = 1, RejectedChute = 16, NoReadChute = 1 };
    [Theory]
    [InlineData(true, "12345678901", 0, 12, 98)]
    [InlineData(true, "12345678901", 5, 0, 98)]
    [InlineData(true, "99999999999,H2X1Y4", 0, 12, 98)]
    [InlineData(true, "?", 0, 0, 98)]
    [InlineData(false, "12345678901", 0, 0, 4)]
    [InlineData(false, "99999999999,H2X1Y4", 0, 0, 7)]
    [InlineData(false, "?", 0, 0, 1)]
    [InlineData(true, "12345678901", 5, 12, 4)]
    [InlineData(true, "12345678901,12345678902", 0, 0, 99)]
    public async Task Code98_switch_preserves_original_route_when_disabled(bool enabled, string camera, int weight, int length, int expected)
    {
        var line = Line();
        line.ValidateDimensionsAndWeight = enabled;
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var parcel = Parcel(camera) with { Weight = weight, Dimension = new Dimension(length, 8, 5) };
        var result = await engine.DecideAsync(line, parcel, CancellationToken.None);
        Assert.Equal(expected, result.Chute);
        Assert.Equal(expected == 99 ? line.RejectedChute : expected, result.PlcChute);
    }

    [Fact]
    public async Task Code98_toggle_is_independent_reactivates_and_counter_resets()
    {
        var config = new ConveyorOptions { Simulation = true, Lines = [new() { Id = 0, CorrelationDelayMs = 0 }, new() { Id = 1 }] };
        config.ApplyGlobalSorting();
        var repo = new FakeRepository();
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repo,
            new SortEngine(repo, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance, new TestConfigurationEditor());
        try
        {
            supervisor.SetCode98Enabled(0, false);
            Assert.False(supervisor.GetSnapshots()[0].Code98Enabled);
            Assert.True(supervisor.GetSnapshots()[1].Code98Enabled);
            await supervisor.SimulateParcelAsync(0, "12345678901", Dimension.Missing, -1);
            Assert.Equal(0, supervisor.GetSnapshots()[0].Counters.Code98);
            supervisor.SetCode98Enabled(0, true);
            Assert.All(supervisor.GetSnapshots(), line => Assert.True(line.Code98Enabled));
            await supervisor.SimulateParcelAsync(0, "12345678901", Dimension.Missing, -1);
            Assert.Equal(1, supervisor.GetSnapshots()[0].Counters.Code98);
            supervisor.SetCode98Enabled(1, false);
            Assert.True(supervisor.GetSnapshots()[0].Code98Enabled);
            Assert.False(supervisor.GetSnapshots()[1].Code98Enabled);
            await supervisor.SimulateParcelAsync(1, "12345678901", Dimension.Missing, -1);
            Assert.Equal(4, supervisor.GetSnapshots()[1].LastDecision!.Chute);
            supervisor.SetCode98Enabled(1, true);
            await supervisor.SimulateParcelAsync(1, "12345678901", Dimension.Missing, -1);
            Assert.Equal(98, supervisor.GetSnapshots()[1].LastDecision!.Chute);
            Assert.NotNull(supervisor.GetSnapshots()[0].LastDecision);
            Assert.NotNull(supervisor.GetSnapshots()[0].CameraInput);
            Assert.NotNull(supervisor.GetSnapshots()[0].DimensionInput);
            Assert.NotNull(supervisor.GetSnapshots()[0].ScaleInput);
            supervisor.ResetCounters(0);
            Assert.Null(supervisor.GetSnapshots()[0].LastDecision);
            Assert.Null(supervisor.GetSnapshots()[0].CameraInput);
            Assert.Null(supervisor.GetSnapshots()[0].DimensionInput);
            Assert.Null(supervisor.GetSnapshots()[0].ScaleInput);
            Assert.NotNull(supervisor.GetSnapshots()[1].CameraInput);
            Assert.NotNull(supervisor.GetSnapshots()[1].DimensionInput);
            Assert.NotNull(supervisor.GetSnapshots()[1].ScaleInput);
            Assert.Equal(98, supervisor.GetSnapshots()[1].LastDecision!.Chute);
            Assert.Equal(0, supervisor.GetSnapshots()[0].Counters.Code98);
        }
        finally { await supervisor.StopLineAsync(0); await supervisor.StopLineAsync(1); }
    }
    [Theory]
    [InlineData("98765432101", 1, false, 1)]
    [InlineData("98765432101", 1, true, 1)]
    [InlineData("12345678901", 1, false, 0)]
    [InlineData("12345678901", 1, true, 1)]
    [InlineData("12345678901", 4, false, 1)]
    [InlineData("12345678901,12345678902", 1, false, 1)]
    public async Task RejectionsCountSentChuteEvenWhenDatabaseInsertFails(string barcode, int rejectedChute, bool failSave, long expected)
    {
        var config = new ConveyorOptions { Simulation = true,
            Lines = [new() { Id = 0, CorrelationDelayMs = 0, RejectedChute = rejectedChute }] };
        config.ApplyGlobalSorting();
        var repo = new FakeRepository { FailSave = failSave };
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repo,
            new SortEngine(repo, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance, new TestConfigurationEditor());
        try
        {
            await supervisor.SimulateParcelAsync(0, barcode, new Dimension(12, 8, 5), 4.75m);
            var counters = supervisor.GetSnapshots()[0].Counters;
            Assert.Equal(1, counters.TotalParcels);
            Assert.Equal(expected, counters.Rejected);
            Assert.Equal(failSave ? 0 : 1, counters.DatabaseInserts);
        }
        finally { await supervisor.StopLineAsync(0); }
    }

    [Fact]
    public async Task NoReadIsNotAlsoCountedAsRejectedWhenBothUseTheSameChute()
    {
        var config = new ConveyorOptions { Simulation = true,
            Lines = [new() { Id = 0, CorrelationDelayMs = 0, RejectedChute = 1, NoReadChute = 1,
                ValidateDimensionsAndWeight = false }] };
        config.ApplyGlobalSorting();
        var repo = new FakeRepository();
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repo,
            new SortEngine(repo, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance, new TestConfigurationEditor());
        try
        {
            await supervisor.SimulateParcelAsync(0, "?", Dimension.Missing, -1);
            var counters = supervisor.GetSnapshots()[0].Counters;
            Assert.Equal(1, counters.TotalParcels);
            Assert.Equal(1, counters.NoReads);
            Assert.Equal(0, counters.Rejected);
        }
        finally { await supervisor.StopLineAsync(0); }
    }

    [Fact]
    public async Task ShipmentUpdateIsCachedAndReloadedAfterDataReset()
    {
        var updated = DateTimeOffset.Now.AddHours(-2);
        var repo = new FakeRepository { ParcelCount = 10, ShipmentUpdate = updated };
        using var metrics = new DatabaseMetricsService(repo, NullLogger<DatabaseMetricsService>.Instance);
        await metrics.RefreshAsync();
        Assert.Equal(updated, metrics.Current.LastShipmentUpdate);
        await metrics.RefreshAsync();
        Assert.Equal(1, repo.ShipmentDateReads);
        repo.ParcelCount = 0;
        await metrics.RefreshAsync();
        Assert.Null(metrics.Current.LastShipmentUpdate);
        repo.ParcelCount = 20;
        repo.ShipmentUpdate = updated.AddHours(1);
        await metrics.RefreshAsync();
        Assert.Equal(updated.AddHours(1), metrics.Current.LastShipmentUpdate);
        Assert.Equal(2, repo.ShipmentDateReads);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    public async Task MotionWritesStartTagOnceAndRecordsGlobalConveyorId(bool start, int? cause)
    {
        var plc = new MotionPlc();
        var repo = new FakeRepository { IsSimulation = false };
        var result = await ConveyorMotion.ExecuteAsync(plc, repo, 42, false, start, cause, NullLogger.Instance);
        Assert.True(result.Recorded);
        Assert.Equal(("DEPART_SYSTEMES", start ? 1 : 0, 1), Assert.Single(plc.Commands));
        Assert.Equal((42, start, cause), Assert.Single(repo.Actions));
    }

    [Fact]
    public async Task MotionDoesNotSendWithoutIdOrValidStopCause()
    {
        var plc = new MotionPlc();
        var repo = new FakeRepository { IsSimulation = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConveyorMotion.ExecuteAsync(plc, repo, null, false, true, null, NullLogger.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConveyorMotion.ExecuteAsync(plc, repo, 42, false, false, null, NullLogger.Instance));
        Assert.Empty(plc.Commands);
        Assert.Empty(repo.Actions);
    }

    [Fact]
    public async Task MotionInsertFailureDoesNotResendCommand()
    {
        var plc = new MotionPlc();
        var repo = new FakeRepository { IsSimulation = false, FailActionSave = true };
        var result = await ConveyorMotion.ExecuteAsync(plc, repo, 42, false, false, 1, NullLogger.Instance);
        Assert.False(result.Recorded);
        Assert.Single(plc.Commands);
    }

    [Fact]
    public async Task DisconnectedPlcDoesNotRecordAnAction()
    {
        var plc = new MotionPlc { IsConnected = false };
        var repo = new FakeRepository { IsSimulation = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConveyorMotion.ExecuteAsync(plc, repo, 42, false, true, null, NullLogger.Instance));
        Assert.Empty(repo.Actions);
        Assert.Empty(plc.Commands);
    }

    private sealed class MotionPlc : IPlcGateway
    {
        public bool IsConnected { get; set; } = true;
        public List<(string Tag, int Value, int Repeat)> Commands { get; } = [];
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(IsConnected);
        public Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
        {
            Commands.Add((tag, chute, repeat));
            return Task.CompletedTask;
        }
    }

    private static ParcelContext Parcel(string camera) => new(camera, DateTimeOffset.Now, new Dimension(12, 8, 5), DateTimeOffset.Now, 4.75m, DateTimeOffset.Now);

    private static int[] GetAvailablePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }
            return listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var listener in listeners) listener.Stop();
        }
    }

    private sealed class FakeRepository : IConveyorRepository
    {
        public List<(int Id, bool Start, int? Cause)> Actions { get; } = [];
        public bool FailActionSave { get; set; }
        public Task SaveConveyorActionAsync(int conveyorId, bool start, int? cause, CancellationToken cancellationToken)
        {
            if (FailActionSave) throw new IOException("Action insert failed");
            Actions.Add((conveyorId, start, cause));
            return Task.CompletedTask;
        }
        public DateTimeOffset? ShipmentUpdate { get; set; }
        public long ParcelCount { get; set; }
        public int ShipmentDateReads { get; private set; }
        public Task<DateTimeOffset?> GetLastShipmentUpdateAsync(CancellationToken cancellationToken)
        {
            ShipmentDateReads++;
            return Task.FromResult(ShipmentUpdate);
        }
        public Task ResetDataAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ConveyorShift>> GetShiftsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConveyorShift>>([new(1, "Jour"), new(2, "Soir")]);
        public long ScanCount { get; set; }
        public bool HasOverdueScans { get; set; }
        public bool FailCounts { get; set; }
        public bool FailSave { get; set; }
        public bool Disable98 { get; set; }
        public int? RouteChute { get; set; } = 4;
        public bool IsSimulation { get; set; } = true;
        public Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken token) => Task.FromResult<Shipment?>(
            barcode.StartsWith("123456789", StringComparison.Ordinal) ? new Shipment(barcode[..9], 1, 10, Disable98, "G1K 3X2") : null);
        public Task<int?> FindChuteForRouteAsync(int shiftId, int routeId, CancellationToken token) => Task.FromResult(RouteChute);
        public Task<int?> FindChuteForPostalCodeAsync(int shiftId, string postalCode, CancellationToken token) => Task.FromResult<int?>(7);
        public Task<bool> ShouldUseExceptionChuteAsync(string codeType, string barcode, int retryLimit, CancellationToken token) => Task.FromResult(true);
        public Task ClearExceptionCodeAsync(string codeType, string barcode, CancellationToken token) => Task.CompletedTask;
        public Task SaveScanAsync(int lineId, ParcelContext parcel, SortDecision decision, CancellationToken token) =>
            FailSave ? Task.FromException(new IOException("Insert failed")) : Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
        public Task<(long Parcels, long PostalCodes, long Scans, bool HasOverdueScans)> GetReferenceCountsAsync(CancellationToken token) =>
            FailCounts ? Task.FromException<(long, long, long, bool)>(new IOException("Database unavailable"))
                : Task.FromResult((ParcelCount, 0L, ScanCount, HasOverdueScans));
    }
}
