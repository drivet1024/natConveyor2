using System.Text.Json;
using System.Text.Json.Nodes;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public interface IConfigurationEditor
{
    ConveyorOptions GetEditableCopy();
    Task SaveAsync(ConveyorOptions options, string? newConnectionString, CancellationToken cancellationToken = default);
    Task SaveShiftAsync(int shiftId);
    Task SaveMaintenanceAsync(bool maintenance) => Task.CompletedTask;
    string FilePath { get; }
}

public sealed class ConfigurationEditor(IOptions<ConveyorOptions> current, IWebHostEnvironment environment) : IConfigurationEditor
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string FilePath { get; } = Path.Combine(environment.ContentRootPath, "conveyor.settings.json");

    public ConveyorOptions GetEditableCopy()
    {
        var copy = Clone(current.Value);
        copy.LineCount = current.Value.LineCount;
        if (copy.Lines.Count == 1)
            copy.Lines.Add(new LineOptions
            {
                Id = copy.Lines[0].Id == 0 ? 1 : 0, Name = "Convoyeur secondaire",
                CameraPort = 5102, ScalePort = 5100, DimensionPort = 1801
            });
        copy.ApplyGlobalSorting();
        return copy;
    }

    public async Task SaveAsync(ConveyorOptions options, string? newConnectionString, CancellationToken cancellationToken = default)
    {
        Validate(options);
        options.ApplyGlobalSorting();
        // This operational state is changed by START commands, never by an older settings form.
        options.General!.Maintenance = current.Value.General?.Maintenance == true;
        if (string.IsNullOrWhiteSpace(newConnectionString))
            options.Database.ConnectionString = current.Value.Database.ConnectionString;
        else
            options.Database.ConnectionString = newConnectionString.Trim();

        var document = new Dictionary<string, ConveyorOptions> { [ConveyorOptions.SectionName] = options };
        await _writeGate.WaitAsync(cancellationToken);
        try { await WriteDocumentAsync(document, cancellationToken); }
        finally { _writeGate.Release(); }
    }

    public Task SaveShiftAsync(int shiftId) => SaveGeneralValueAsync("ShiftId", JsonValue.Create(shiftId));
    public Task SaveMaintenanceAsync(bool maintenance) => SaveGeneralValueAsync("Maintenance", JsonValue.Create(maintenance));

    private async Task SaveGeneralValueAsync(string property, JsonNode value)
    {
        await _writeGate.WaitAsync();
        try
        {
            var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
            var document = File.Exists(FilePath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(FilePath), nodeOptions)!.AsObject()
                : new JsonObject(nodeOptions);
            var conveyor = document[ConveyorOptions.SectionName] as JsonObject;
            if (conveyor is null)
                document[ConveyorOptions.SectionName] = conveyor = new JsonObject(nodeOptions);
            var general = conveyor["General"] as JsonObject;
            if (general is null)
                conveyor["General"] = general = JsonSerializer.SerializeToNode(current.Value.General, JsonOptions)!.AsObject();
            // Existing JSON may use camelCase; preserve the original key.
            var key = general.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, property, StringComparison.OrdinalIgnoreCase)) ?? property;
            general[key] = value;
            await WriteDocumentAsync(document, CancellationToken.None);
        }
        finally { _writeGate.Release(); }
    }

    private async Task WriteDocumentAsync<T>(T document, CancellationToken cancellationToken)
    {
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
        if (options.Sms.ValidationError() is { } smsError) throw new InvalidOperationException(smsError);
        if (options.Statistics.ValidationError(options.GetConfiguredLines()) is { } statisticsError)
            throw new InvalidOperationException(statisticsError);
        if (options.Lines.Count is < 1 or > 2) throw new InvalidOperationException("Une ou deux lignes doivent être configurées.");
        if (options.LineCount is < 1 or > 2 || options.LineCount > options.Lines.Count)
            throw new InvalidOperationException("Choisir une ou deux lignes avec leurs paramètres de connexion.");
        if (string.IsNullOrWhiteSpace(options.General?.ConveyorStartTag))
            throw new InvalidOperationException("Le tag de démarrage du convoyeur est obligatoire.");
        options.General.ConveyorStartTag = options.General.ConveyorStartTag.Trim();
        if (options.Lines.Select(line => line.Id).Distinct().Count() != options.Lines.Count) throw new InvalidOperationException("Les identifiants de ligne doivent être uniques.");
        if (options.Lines.Any(line => line.DatabaseLineId is <= 0)) throw new InvalidOperationException("Lorsqu’il est renseigné, le lineId MySQL doit être supérieur à zéro.");
        var databaseLineIds = options.Lines.Where(line => line.DatabaseLineId.HasValue).Select(line => line.DatabaseLineId!.Value).ToArray();
        if (databaseLineIds.Distinct().Count() != databaseLineIds.Length) throw new InvalidOperationException("Les lineId MySQL renseignés doivent être uniques.");
        PlcConfiguration.Validate(options.GetConfiguredLines().First().Plc);
        foreach (var converter in options.Lines.SelectMany(line => new[] { line.ScaleConverter, line.DimensionConverter }))
        {
            if (converter.Type is not ("Old" or "New")) throw new InvalidOperationException("Modèle de convertisseur invalide.");
            if (!string.IsNullOrWhiteSpace(converter.IpAddress) && !System.Net.IPAddress.TryParse(converter.IpAddress.Trim(), out _))
                throw new InvalidOperationException("L’adresse du convertisseur doit être une adresse IP valide.");
        }
        var devices = options.GetConfiguredLines().SelectMany(line => new[]
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
