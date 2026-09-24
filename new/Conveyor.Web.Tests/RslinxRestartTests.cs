using System.Text;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class RslinxRestartTests
{
    [Theory]
    [InlineData("simulation")]
    [InlineData("running")]
    [InlineData("tcp")]
    [InlineData("remote")]
    public async Task RejectsUnsupportedRestartBeforeInvokingWindows(string condition)
    {
        var config = Configuration();
        if (condition == "simulation") config.Simulation = true;
        if (condition == "tcp") config.Lines[0].Plc.Protocol = "Tcp";
        if (condition == "remote") { config.Lines[0].Plc.Protocol = "OpcDa"; config.Lines[0].Plc.OpcHost = "remote-server"; }
        var restarter = new FakeRestarter();
        using var supervisor = Create(config, restarter);
        if (condition == "running") supervisor.RecordPlcTagChange(config.General!.ConveyorStartTag, "1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.RestartRslinxAsync());
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task RestartRejectsConcurrentCommandsAndDoesNotStartDisabledConnections()
    {
        var restarter = new FakeRestarter { Block = true };
        using var supervisor = Create(Configuration(), restarter);
        var restart = supervisor.RestartRslinxAsync();
        await restarter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.RestartRslinxAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetConveyorMotionAsync(true, null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartLineAsync(0));
        }
        finally { restarter.Release.TrySetResult(); }
        Assert.Contains("désactivées", await restart);
        Assert.Equal(1, restarter.Calls);
        Assert.All(supervisor.GetSnapshots(), line => Assert.False(line.Running));
        Assert.Null(supervisor.ConveyorRunning);
    }

    [Fact]
    public async Task FailureIsReportedAndGateIsReleasedForRetry()
    {
        var restarter = new FakeRestarter { Fail = true };
        using var supervisor = Create(Configuration(), restarter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.RestartRslinxAsync());
        restarter.Fail = false;
        await supervisor.RestartRslinxAsync();
        Assert.Equal(2, restarter.Calls);
    }

    [Fact]
    public void HelperRunsHiddenAndConfigurationCannotBecomeShellCode()
    {
        var options = new RslinxRestartOptions { ServiceName = "RSLinx'; exit 99; '" };
        var info = RslinxRestarter.CreateStartInfo(options);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, info.WindowStyle);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(info.ArgumentList.Last()));
        Assert.DoesNotContain(options.ServiceName, script);
        Assert.Equal(options.ServiceName, info.Environment["CONVEYOR_RSLINX_SERVICE"]);
        Assert.Contains("WaitForStatus", script);
    }

    [Fact]
    public void ApplicationModeRequiresAnExactRslinxExecutablePath()
    {
        Assert.Null(new RslinxRestartOptions().ValidationError());
        Assert.NotNull(new RslinxRestartOptions { ServiceName = "RSL*" }.ValidationError());
        Assert.NotNull(new RslinxRestartOptions { Mode = "Application", ExecutablePath = "RSLINX.exe" }.ValidationError());
        Assert.NotNull(new RslinxRestartOptions { Mode = "Application", ExecutablePath = @"C:\Windows\cmd.exe" }.ValidationError());
        Assert.Null(new RslinxRestartOptions { Mode = "Application", ExecutablePath = @"C:\Rockwell\RSLINX.exe" }.ValidationError());
    }

    private static ConveyorOptions Configuration() => new()
    {
        Simulation = false, General = new() { ConveyorId = 7 }, Lines = [new() { Id = 0, Enabled = false }]
    };
    private static ConveyorSupervisor Create(ConveyorOptions config, IRslinxRestarter restarter)
    {
        var repository = new SimulationConveyorRepository();
        return new(Microsoft.Extensions.Options.Options.Create(config), repository,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance,
            new TestConfigurationEditor(), rslinxRestarter: restarter);
    }

    private sealed class FakeRestarter : IRslinxRestarter
    {
        public int Calls { get; private set; }
        public bool Block { get; set; }
        public bool Fail { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RestartAsync(RslinxRestartOptions options, CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(token);
            if (Fail) throw new InvalidOperationException("Test failure");
        }
    }
}
