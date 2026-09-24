using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Conveyor.Web.Options;

public sealed class ConveyorOptions
{
    public const string SectionName = "Conveyor";
    private int? _lineCount;
    [Range(1, 2)] public int LineCount { get => _lineCount ?? Math.Clamp(Lines.Count, 1, 2); set => _lineCount = value; }
    public bool Simulation { get; set; } = true;
    public DatabaseOptions Database { get; set; } = new();
    public SmsOptions Sms { get; set; } = new();
    public StatisticsOptions Statistics { get; set; } = new();
    public RslinxRestartOptions RslinxRestart { get; set; } = new();
    public GeneralOptions? General { get; set; }
    public SortingOptions? Sorting { get; set; }
    [MinLength(1), MaxLength(2)] public List<LineOptions> Lines { get; set; } = [];
    public IEnumerable<LineOptions> GetConfiguredLines() => Lines.OrderBy(line => line.Id).Take(LineCount);

    public void ApplyGlobalSorting()
    {
        // Older installations only have per-line settings. Use the primary line
        // once; after saving, the global section is authoritative.
        var primary = Lines.OrderBy(line => line.Id).FirstOrDefault() ?? new LineOptions();
        General ??= new GeneralOptions { Name = primary.Name, DepotId = primary.DepotId, ShiftId = primary.ShiftId };
        Sorting ??= new SortingOptions
        {
            RejectedChute = primary.RejectedChute,
            NoReadChute = primary.NoReadChute,
            CorrelationDelayMs = primary.CorrelationDelayMs,
            CorrelationWindowMs = primary.CorrelationWindowMs,
            MaximumWeight = primary.MaximumWeight,
            MaximumDimension = primary.MaximumDimension,
            PostalCodeSort = primary.PostalCodeSort,
            // Code 98 is opt-in. Legacy configurations without a global
            // sorting section restart with the safe global default: OFF.
            ValidateDimensionsAndWeight = false,
            EnableCode86 = primary.EnableCode86
        };
        foreach (var line in Lines)
        {
            // Preserve values saved briefly under the incorrect SourceId name.
            // Only the setting name changes; its numeric value is not translated.
            if (line.DatabaseLineId is null && line.SourceId is not null) line.DatabaseLineId = line.SourceId;
            line.SourceId = null;
            line.Name = General.Name;
            line.DepotId = General.DepotId;
            line.ShiftId = General.ShiftId;
            line.RejectedChute = Sorting.RejectedChute;
            line.NoReadChute = Sorting.NoReadChute;
            line.CorrelationDelayMs = Sorting.CorrelationDelayMs;
            line.CorrelationWindowMs = Sorting.CorrelationWindowMs;
            line.MaximumWeight = Sorting.MaximumWeight;
            line.MaximumDimension = Sorting.MaximumDimension;
            line.PostalCodeSort = Sorting.PostalCodeSort;
            line.ValidateDimensionsAndWeight = Sorting.ValidateDimensionsAndWeight;
            line.EnableCode86 = Sorting.EnableCode86;
        }
    }
}

public sealed class GeneralOptions
{
    public const string DefaultConveyorStartTag = "DEPART_SYSTEMES";
    public int? ConveyorId { get; set; }
    public bool Maintenance { get; set; }
    public string Name { get; set; } = "Convoyeur";
    public int DepotId { get; set; }
    public string DepotName { get; set; } = "";
    public string GetDepotDisplayName() => !string.IsNullOrWhiteSpace(DepotName) ? DepotName.Trim() : DepotId switch
    {
        1 => "Saint-Hubert",
        28 => "Gilmore",
        2 => "Québec",
        12 => "Toronto",
        _ => "Nom non configuré"
    };
    public int ShiftId { get; set; }
    [Required] public string ConveyorStartTag { get; set; } = DefaultConveyorStartTag;
}

public sealed class SortingOptions
{
    public int RejectedChute { get; set; } = 16;
    public int NoReadChute { get; set; } = 1;
    public int CorrelationDelayMs { get; set; } = 120;
    public int CorrelationWindowMs { get; set; } = 1_500;
    public decimal MaximumWeight { get; set; } = 150;
    public decimal MaximumDimension { get; set; } = 100;
    public bool PostalCodeSort { get; set; } = true;
    public bool ValidateDimensionsAndWeight { get; set; }
    public bool EnableCode86 { get; set; }
}

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class LineOptions
{
    [Range(0, 1)] public int Id { get; set; }
    [Range(1, int.MaxValue)] public int? DatabaseLineId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? SourceId { get; set; }
    public string Name { get; set; } = "Ligne";
    public bool Enabled { get; set; } = true;
    public int DepotId { get; set; }
    public int ShiftId { get; set; }
    public int CameraPort { get; set; }
    public string CameraHost { get; set; } = "";
    public bool CameraConnectMode { get; set; }
    public int ScalePort { get; set; }
    public string ScaleHost { get; set; } = "";
    public bool ScaleConnectMode { get; set; }
    public int DimensionPort { get; set; }
    public string DimensionHost { get; set; } = "";
    public bool DimensionConnectMode { get; set; }
    public ConverterOptions ScaleConverter { get; set; } = new();
    public ConverterOptions DimensionConverter { get; set; } = new();
    public string ScaleProtocol { get; set; } = "Delimited";
    public int RejectedChute { get; set; } = 16;
    public int NoReadChute { get; set; } = 1;
    public bool PostalCodeSort { get; set; } = true;
    public bool ValidateDimensionsAndWeight { get; set; }
    public bool EnableCode86 { get; set; }
    public int Code86Retry { get; set; } = 3;
    public int CorrelationDelayMs { get; set; } = 120;
    public int CorrelationWindowMs { get; set; } = 1_500;
    public decimal MaximumWeight { get; set; } = 150;
    public decimal MaximumDimension { get; set; } = 100;
    public TimeOnly EndOfDay { get; set; } = new(8, 25);
    public PlcOptions Plc { get; set; } = new();
}

public sealed class PlcOptions
{
    public string Protocol { get; set; } = "Dde";
    public string DdeService { get; set; } = "RSLinx";
    public string DdeTopic { get; set; } = "NATIONEX";
    public string OpcProgId { get; set; } = "RSLinx OPC Server";
    public string OpcHost { get; set; } = "";
    public string OpcTopic { get; set; } = "NATIONEX";
    [Range(50, 60_000)] public int OpcUpdateRateMs { get; set; } = 100;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7000;
    public string ChuteTag { get; set; } = "COLISDDE";
    public string TransferTag { get; set; } = "";
    public string CloseChute39Tag { get; set; } = "CLOSE_CHUTE_39";
    public string ScaleFaultTag { get; set; } = "";
    [Range(1, 100)] public int ScaleFaultParcelThreshold { get; set; } = 3;
    [Range(1, 60_000)] public int ScaleFaultPulseMs { get; set; } = 7_000;
    public int SendCount { get; set; } = 1;
}

public sealed class ConverterOptions
{
    public string Type { get; set; } = "New";
    public string IpAddress { get; set; } = "";
}
