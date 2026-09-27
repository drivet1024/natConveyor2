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
