using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Conveyor.Web.Infrastructure;

public static class SigNozLogging
{
    public static void AddSigNoz(this ILoggingBuilder logging, IConfiguration configuration, string environmentName)
    {
        var section = configuration.GetSection("SigNoz");
        if (!section.GetValue<bool>("Enabled")) return;

        if (!Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != "http" && endpoint.Scheme != "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("SigNoz:Endpoint doit être une adresse OTLP HTTP ou HTTPS sans identifiants.");

        var protocol = section["Protocol"]?.ToLowerInvariant() switch
        {
            null or "grpc" => OtlpExportProtocol.Grpc,
            "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            _ => throw new InvalidOperationException("SigNoz:Protocol doit être grpc ou http/protobuf.")
        };

        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(section["ServiceName"] ?? "Conveyor.Web",
                    serviceVersion: typeof(SigNozLogging).Assembly.GetName().Version?.ToString(),
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["host.name"] = Environment.MachineName,
                    ["deployment.environment.name"] = environmentName
                }));
            options.AddProcessor(new SigNozSeverityProcessor());
            options.AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = endpoint;
                exporter.Protocol = protocol;
                // Optional secret supplied locally or through SigNoz__Headers, never logged.
                if (!string.IsNullOrWhiteSpace(section["Headers"])) exporter.Headers = section["Headers"];
                exporter.TimeoutMilliseconds = 5000;
                exporter.ExportProcessorType = OpenTelemetry.ExportProcessorType.Batch;
            });
        });
    }
}

internal sealed class SigNozSeverityProcessor : OpenTelemetry.BaseProcessor<LogRecord>
{
    // OpenTelemetry 1.19.1 keeps SeverityText internal in its stable build.
    // Cache the setter once; the OTLP integration test guards this version-bound bridge.
    // Changing only the text preserves the standard numeric severity and filtering.
    private static readonly Action<LogRecord, string?> SetSeverityText = typeof(LogRecord)
        .GetProperty("SeverityText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetSetMethod(nonPublic: true)!.CreateDelegate<Action<LogRecord, string?>>();

    public override void OnEnd(LogRecord record)
    {
        var text = record.LogLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => null
        };
        if (text is not null) SetSeverityText(record, text);
    }
}
