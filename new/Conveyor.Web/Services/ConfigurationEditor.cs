using System.Text.Json;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public interface IConfigurationEditor
{
    ConveyorOptions GetEditableCopy();
    Task SaveAsync(ConveyorOptions options, string? newConnectionString, CancellationToken cancellationToken = default);
    string FilePath { get; }
}

public sealed class ConfigurationEditor(IOptions<ConveyorOptions> current, IWebHostEnvironment environment) : IConfigurationEditor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string FilePath { get; } = Path.Combine(environment.ContentRootPath, "conveyor.settings.json");

    public ConveyorOptions GetEditableCopy() => Clone(current.Value);

    public async Task SaveAsync(ConveyorOptions options, string? newConnectionString, CancellationToken cancellationToken = default)
    {
        Validate(options);
        if (string.IsNullOrWhiteSpace(newConnectionString))
            options.Database.ConnectionString = current.Value.Database.ConnectionString;
        else
            options.Database.ConnectionString = newConnectionString.Trim();

        var document = new Dictionary<string, ConveyorOptions> { [ConveyorOptions.SectionName] = options };
        var temporaryPath = FilePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        File.Move(temporaryPath, FilePath, true);
    }

    private static ConveyorOptions Clone(ConveyorOptions value) =>
        JsonSerializer.Deserialize<ConveyorOptions>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException("Impossible de copier la configuration.");

    private static void Validate(ConveyorOptions options)
    {
        if (options.Lines.Count is < 1 or > 2) throw new InvalidOperationException("Une ou deux lignes doivent être configurées.");
        if (options.Lines.Select(line => line.Id).Distinct().Count() != options.Lines.Count) throw new InvalidOperationException("Les identifiants de ligne doivent être uniques.");
        var devices = options.Lines.SelectMany(line => new[]
        {
            (Name: "Caméras", Port: line.CameraPort, Client: line.CameraConnectMode, Host: line.CameraHost, line.Enabled),
            (Name: "Dimensionneur", Port: line.DimensionPort, Client: line.DimensionConnectMode, Host: line.DimensionHost, line.Enabled),
            (Name: "Balance", Port: line.ScalePort, Client: line.ScaleConnectMode, Host: line.ScaleHost, line.Enabled)
        }).ToArray();
        var ports = devices.Select(device => device.Port).ToArray();
        if (ports.Any(port => port is < 1 or > 65535)) throw new InvalidOperationException("Les ports doivent être compris entre 1 et 65535.");
        foreach (var device in devices.Where(device => device.Client))
            if (string.IsNullOrWhiteSpace(device.Host))
                throw new InvalidOperationException($"{device.Name} : l’adresse du serveur est obligatoire en mode client.");
        var listeningPorts = devices.Where(device => device.Enabled && !device.Client).Select(device => device.Port).ToArray();
        if (listeningPorts.Distinct().Count() != listeningPorts.Length)
            throw new InvalidOperationException("Chaque appareil en mode serveur sur une ligne activée doit utiliser un port distinct.");
    }
}
