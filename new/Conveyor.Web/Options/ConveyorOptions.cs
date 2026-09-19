using System.ComponentModel.DataAnnotations;

namespace Conveyor.Web.Options;

public sealed class ConveyorOptions
{
    public const string SectionName = "Conveyor";
    public bool Simulation { get; set; } = true;
    public DatabaseOptions Database { get; set; } = new();
    [MinLength(1), MaxLength(2)] public List<LineOptions> Lines { get; set; } = [];
}

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class LineOptions
{
    [Range(0, 1)] public int Id { get; set; }
    public string Name { get; set; } = "Ligne";
    public bool Enabled { get; set; } = true;
    public int DepotId { get; set; }
    public int ShiftId { get; set; }
    public int CameraPort { get; set; }
    public int ScalePort { get; set; }
    public int DimensionPort { get; set; }
    public string DimensionHost { get; set; } = "";
    public bool DimensionConnectMode { get; set; }
    public string ScaleProtocol { get; set; } = "Delimited";
    public int RejectedChute { get; set; } = 16;
    public int NoReadChute { get; set; } = 1;
    public bool PostalCodeSort { get; set; } = true;
    public bool ValidateDimensionsAndWeight { get; set; } = true;
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
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7000;
    public string ChuteTag { get; set; } = "COLISDDE";
    public int SendCount { get; set; } = 1;
}
