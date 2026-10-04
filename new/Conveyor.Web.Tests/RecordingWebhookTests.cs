using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Conveyor.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace Conveyor.Web.Tests;

public sealed class RecordingWebhookTests
{
    [Fact]
    public void TemporarySimulationRunsWhileStoppedAndUsesExistingWebhookWindow()
    {
        var options = new ConveyorOptions { Simulation = false, Lines = [new() { Id = 0 }], RecordingWebhook = new() { Enabled = true, WebhookKey = new string('x', 32) } };
        options.ApplyGlobalSorting();
        var settings = Microsoft.Extensions.Options.Options.Create(options);
        var events = new ParcelRecordingEvents(); var repository = new SimulationConveyorRepository();
        using var supervisor = new ConveyorSupervisor(settings, repository, new SortEngine(repository, NullLogger<SortEngine>.Instance),
            NullLoggerFactory.Instance, new TestConfigurationEditor());
        using var sender = new RecordingWebhookService(settings, supervisor, events);
        var now = DateTimeOffset.UtcNow;
        var previousMotion = supervisor.ConveyorRunning;
        var test = sender.SimulateChute24(now);
        Assert.StartsWith("SIM-24-", test.Barcode);
        Assert.Equal(now.AddSeconds(15), test.ArrivalAt);
        Assert.Empty(sender.NextMessage(now).Jobs);
        var approaching = Assert.Single(sender.NextMessage(now.AddSeconds(3)).Jobs);
        Assert.Equal(24, approaching.Parcel.Chute);
        Assert.Equal(test.Barcode, approaching.Parcel.Barcode);
        Assert.True(approaching.Simulated); Assert.False(approaching.Arrived);
        Assert.Throws<InvalidOperationException>(() => sender.SimulateChute24(now.AddSeconds(4)));
        var arrived = Assert.Single(sender.NextMessage(now.AddSeconds(15)).Jobs);
        Assert.True(arrived.Arrived); Assert.True(arrived.Simulated); Assert.Equal(approaching.Id, arrived.Id);
        Assert.Single(sender.NextMessage(now.AddSeconds(20)).Jobs);
        Assert.Empty(sender.NextMessage(now.AddSeconds(21)).Jobs);
        Assert.Equal(previousMotion, supervisor.ConveyorRunning);
        Assert.Equal(96.6, options.RecordingWebhook.TravelSeconds[24]);
        sender.SimulateChute24(now.AddSeconds(22));
        sender.SetRecordingEnabled(false);
        Assert.Empty(sender.NextMessage(now.AddSeconds(26)).Jobs);
    }

    [Fact]
    public void TemporarySimulationRequiresEnabledWebhookAndConfiguredChute24()
    {
        var options = new ConveyorOptions { Simulation = true, Lines = [new() { Id = 0 }] }; options.ApplyGlobalSorting();
        var settings = Microsoft.Extensions.Options.Options.Create(options);
        var repository = new SimulationConveyorRepository();
        using var supervisor = new ConveyorSupervisor(settings, repository, new SortEngine(repository, NullLogger<SortEngine>.Instance),
            NullLoggerFactory.Instance, new TestConfigurationEditor());
        using var sender = new RecordingWebhookService(settings, supervisor, new());
        Assert.Throws<InvalidOperationException>(() => sender.SimulateChute24());
        options.RecordingWebhook.Enabled = true;
        Assert.Throws<InvalidOperationException>(() => sender.SimulateChute24());
        options.RecordingWebhook.WebhookKey = new string('x', 32);
        options.RecordingWebhook.TravelSeconds.Remove(24);
        Assert.Throws<InvalidOperationException>(() => sender.SimulateChute24());
    }

    [Fact]
    public async Task SenderPostsAuthenticatedScheduleAndCancellationWithoutBlockingSorting()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = builder.Build();
        var messages = new ConcurrentQueue<RecordingWebhookMessage>();
        var key = new string('x', 32);
        receiver.MapPost("/api/events", async (HttpContext context) =>
        {
            Assert.Equal(key, context.Request.Headers["X-Conveyor-Key"].ToString());
            messages.Enqueue((await context.Request.ReadFromJsonAsync<RecordingWebhookMessage>())!);
            return Results.Accepted();
        });
        await receiver.StartAsync();
        var configuration = new ConveyorOptions { Simulation = true, Lines = [new() { Id = 0 }],
            RecordingWebhook = new() { Enabled = true, WebhookKey = key, WebhookUrl = receiver.Urls.Single() + "/api/events", TravelSeconds = new() { [26] = 14 } } };
        configuration.ApplyGlobalSorting();
        var settings = Microsoft.Extensions.Options.Options.Create(configuration);
        var events = new ParcelRecordingEvents(); var repository = new SimulationConveyorRepository();
        using var supervisor = new ConveyorSupervisor(settings, repository, new SortEngine(repository, NullLogger<SortEngine>.Instance),
            NullLoggerFactory.Instance, new TestConfigurationEditor(), recordingEvents: events);
        using var sender = new RecordingWebhookService(settings, supervisor, events);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await sender.StartAsync(CancellationToken.None);
            supervisor.RecordPlcTagChange("DEPART_SYSTEMES", "1");
            events.Publish(new(0, 1, "12345678901", 26, DateTimeOffset.UtcNow));
            while (!messages.Any(message => message.Jobs.Count > 0)) await Task.Delay(100, deadline.Token);
            var message = messages.First(message => message.Jobs.Count > 0);
            var job = Assert.Single(message.Jobs); Assert.Equal("12345678901", job.Parcel.Barcode);
            Assert.False(job.Arrived); Assert.Equal(26, job.Parcel.Chute);
            Assert.NotNull(sender.Status.LastSentAt);
            sender.SetRecordingEnabled(false);
            while (!messages.Any(message => !message.Enabled && message.Jobs.Count == 0)) await Task.Delay(100, deadline.Token);
            Assert.Equal(0, sender.Status.WaitingParcels);
            Assert.Equal(messages.Select(message => message.Sequence).Distinct().Count(), messages.Count);
        }
        finally { await sender.StopAsync(CancellationToken.None); await receiver.StopAsync(); }
    }
    [Fact]
    public void ScheduleOpensCaptureOnlyNearArrivalAndWithdrawsOnStop()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new RecordingWebhookOptions { TravelSeconds = new() { [24] = 96 } };
        var planner = new ParcelArrivalPlanner(now);
        planner.SetRunning(true, now);
        planner.Schedule(new(0, 1, "123", 24, now), options);
        Assert.Empty(planner.Upcoming(now.AddSeconds(80), options));
        Assert.Equal(now.AddSeconds(96), Assert.Single(planner.Upcoming(now.AddSeconds(84), options)).At);
        planner.SetRunning(false, now.AddSeconds(85));
        Assert.Empty(planner.Upcoming(now.AddSeconds(90), options));
        planner.SetRunning(true, now.AddSeconds(100));
        Assert.Equal(now.AddSeconds(111), Assert.Single(planner.Upcoming(now.AddSeconds(100), options)).At);
        Assert.Single(planner.Arrivals(now.AddSeconds(111), options));
        Assert.Empty(planner.Upcoming(now.AddSeconds(112), options));
    }
    [Fact]
    public void WebhookDoesNotRequireCameraCredentialsOrLocalVideoStorage()
    {
        var options = new RecordingWebhookOptions { Enabled = true, WebhookKey = new string('x', 32) };
        Assert.Null(options.ValidationError());
        options.WebhookKey = "short";
        Assert.NotNull(options.ValidationError());
        options.WebhookKey = new string('x', 32); options.WebhookUrl = "file:///temp";
        Assert.NotNull(options.ValidationError());
    }
}
