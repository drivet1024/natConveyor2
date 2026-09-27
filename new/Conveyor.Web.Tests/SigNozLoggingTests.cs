using System.Net;
using System.Net.Sockets;
using System.Text;
using Conveyor.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace Conveyor.Web.Tests;

public sealed class SigNozLoggingTests
{
    [Fact]
    public void MissingConfigurationDoesNotRegisterExporter()
    {
        using var services = new ServiceCollection().AddLogging(builder =>
            builder.AddSigNoz(new ConfigurationBuilder().Build(), "Test")).BuildServiceProvider();
        Assert.DoesNotContain(services.GetServices<ILoggerProvider>(), provider => provider is OpenTelemetryLoggerProvider);
    }

    [Fact]
    public async Task ExportsStructuredLogAndExceptionOverOtlpHttp()
    {
        // Reserve a loopback port for a local OTLP receiver; no production logs leave this test.
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var receiver = new HttpListener();
        receiver.Prefixes.Add($"http://127.0.0.1:{port}/");
        receiver.Start();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SigNoz:Enabled"] = "true",
            ["SigNoz:Endpoint"] = $"http://127.0.0.1:{port}/v1/logs",
            ["SigNoz:Protocol"] = "http/protobuf",
            ["SigNoz:ServiceName"] = "conveyor-test"
        }).Build();
        using var services = new ServiceCollection().AddLogging(builder =>
            { builder.SetMinimumLevel(LogLevel.Trace); builder.AddSigNoz(configuration, "Test"); })
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Conveyor.Test");
        logger.LogError(new InvalidOperationException("test-disk-denied"), "Test chute {Chute}", 21);
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Critical })
            logger.Log(level, "severity-check");
        Assert.Single(services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>());
        var request = await receiver.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var body = new MemoryStream();
        await request.Request.InputStream.CopyToAsync(body);
        request.Response.StatusCode = 200;
        request.Response.ContentType = "application/x-protobuf";
        request.Response.ContentLength64 = 0;
        request.Response.Close();
        Assert.Equal("/v1/logs", request.Request.Url!.AbsolutePath);
        var payload = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("Test chute 21", payload);
        Assert.Contains("test-disk-denied", payload);
        Assert.Contains("conveyor-test", payload);
        Assert.Contains(Environment.MachineName, payload);
        // OTLP LogRecord field 2 = numeric severity, field 3 = severity_text.
        foreach (var (number, label) in new[] { (1, "TRACE"), (5, "DEBUG"), (9, "INFO"), (13, "WARN"), (17, "ERROR"), (21, "FATAL") })
            Assert.Contains($"\u0010{(char)number}\u001a{(char)label.Length}{label}", payload);
    }
}
