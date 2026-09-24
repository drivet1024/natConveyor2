using System.Net;
using System.Text;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class SmsAlertsTests
{
    private static ConveyorOptions Configuration() => new()
    {
        Simulation = false,
        General = new() { Name = "Convoyeur principal", DepotId = 2, ConveyorId = 7 },
        Sms = new()
        {
            Enabled = true, AccountSid = "AC" + new string('a', 32), AuthToken = "test-token",
            FromNumber = "+15145550000", Recipients = "+15145550001; +14165550002\n+15145550001"
        }
    };

    [Theory]
    [InlineData("success")]
    [InlineData("http-error")]
    [InlineData("network-error")]
    [InlineData("timeout")]
    public async Task SendsOncePerDistinctRecipientAndContinuesAfterFailures(string firstResult)
    {
        var config = Configuration();
        using var handler = new RecordingHandler(firstResult);
        using var service = CreateService(config, handler);
        service.Notify("Reset compteurs effectué", 1);
        await service.StartAsync(CancellationToken.None);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(shutdown.Token);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { "+15145550001", "+14165550002" }, handler.Requests.Select(request => request.Fields["To"].ToString()));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{config.Sms.AccountSid}/Messages.json", request.Url);
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Sms.AccountSid + ":test-token")), request.Authorization);
            Assert.Equal(config.Sms.FromNumber, request.Fields["From"].ToString());
            var body = request.Fields["Body"].ToString();
            Assert.Contains("Reset compteurs effectué", body);
            Assert.Contains("WARN: Reset compteurs effectué dépôt [Québec] ligne [2]", body);
            Assert.DoesNotContain("Convoyeur principal", body);
            Assert.DoesNotContain("convoyeur [", body);
            Assert.DoesNotContain("dépôt [2]", body);
            Assert.DoesNotContain(config.Sms.AuthToken, body);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrSimulationNeverCallsTwilio(bool enabled, bool simulation)
    {
        var config = Configuration();
        config.Sms.Enabled = enabled;
        config.Simulation = simulation;
        using var handler = new RecordingHandler("success");
        using var service = CreateService(config, handler);
        service.Notify("Reset Data effectué");
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("sid")]
    [InlineData("token")]
    [InlineData("sender")]
    [InlineData("recipient")]
    [InlineData("empty")]
    public async Task InvalidConfigurationIsRejectedWithoutAnHttpCall(string field)
    {
        var config = Configuration();
        switch (field)
        {
            case "sid": config.Sms.AccountSid = "invalid/path"; break;
            case "token": config.Sms.AuthToken = " "; break;
            case "sender": config.Sms.FromNumber = "5145550000"; break;
            case "recipient": config.Sms.Recipients += ";invalid"; break;
            case "empty": config.Sms.Recipients = " ;\n"; break;
        }
        Assert.NotNull(config.Sms.ValidationError());
        using var handler = new RecordingHandler("success");
        using var service = CreateService(config, handler);
        service.Notify("Reset Data effectué");
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Empty(handler.Requests);
        config.Sms.Enabled = false;
        Assert.Null(config.Sms.ValidationError());
    }

    [Fact]
    public async Task CommandsAlertAfterSuccessAndRepeatedConnectionCommandsDoNotDuplicate()
    {
        var config = Configuration();
        config.Simulation = true;
        config.Lines = [new() { Id = 0 }];
        config.ApplyGlobalSorting();
        var repository = new SimulationConveyorRepository();
        var alerts = new RecordingAlerts();
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repository,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance,
            new TestConfigurationEditor(), alerts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetConveyorMotionAsync(true, null));
        Assert.Empty(alerts.Events);
        await supervisor.StartLineAsync(0);
        await supervisor.StartLineAsync(0);
        supervisor.ResetCounters(0);
        await supervisor.SetConveyorMotionAsync(true, null);
        await supervisor.SetConveyorMotionAsync(false, 1);
        await supervisor.StopLineAsync(0);
        await supervisor.StopLineAsync(0);
        Assert.Equal(5, alerts.Events.Count);
        Assert.Contains("Connexion", alerts.Events[0].Action);
        Assert.Equal(("Reset compteurs effectué", (int?)0), alerts.Events[1]);
        Assert.Contains("démarrer", alerts.Events[2].Action);
        Assert.Null(alerts.Events[2].LineId);
        Assert.Contains("JAM", alerts.Events[3].Action);
        Assert.Contains("Déconnexion", alerts.Events[4].Action);
    }

    private static SmsAlerts CreateService(ConveyorOptions config, RecordingHandler handler) =>
        new(Microsoft.Extensions.Options.Options.Create(config), new ClientFactory(handler), NullLogger<SmsAlerts>.Instance);

    private sealed class RecordingAlerts : ISmsAlerts
    {
        public List<(string Action, int? LineId)> Events { get; } = [];
        public void Notify(string action, int? lineId = null) => Events.Add((action, lineId));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Request(HttpMethod Method, string Url, string Authorization,
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Fields);

    private sealed class RecordingHandler(string firstResult) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization!.ToString(),
                QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(token))));
            if (Requests.Count == 1)
            {
                if (firstResult == "network-error") throw new HttpRequestException("Network unavailable");
                if (firstResult == "timeout") throw new TaskCanceledException("Request timed out");
                if (firstResult == "http-error") return new(HttpStatusCode.Unauthorized);
            }
            return new(HttpStatusCode.Created);
        }
    }
}
