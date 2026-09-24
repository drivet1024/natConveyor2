namespace Conveyor.Web.Options;

public sealed class RslinxRestartOptions
{
    public string Mode { get; set; } = "Service";
    public bool AutoRestart { get; set; } = true;
    public string ServiceName { get; set; } = "RSLinx";
    public string ExecutablePath { get; set; } = "";

    public string? ValidationError()
    {
        if (Mode is not ("Service" or "Application")) return "RSLinx : choisir Service Windows ou Application.";
        if (string.IsNullOrWhiteSpace(ServiceName) || ServiceName.IndexOfAny(['*', '?', '[', ']']) >= 0)
            return "RSLinx : renseigner le nom exact du service Windows, sans caractères génériques.";
        if (Mode == "Application" && (!Path.IsPathFullyQualified(ExecutablePath) ||
            !string.Equals(Path.GetFileName(ExecutablePath), "RSLINX.exe", StringComparison.OrdinalIgnoreCase)))
            return "RSLinx : renseigner le chemin absolu de RSLINX.exe.";
        return null;
    }
}
