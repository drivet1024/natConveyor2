namespace Conveyor.Web.Options;

public static class PlcConfiguration
{
    public static void Validate(PlcOptions options)
    {
        if (!new[] { "Dde", "OpcDa", "Tcp" }.Contains(options.Protocol, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choisir DDE, OPC DA ou TCP pour le protocole automate.");
        if (!string.Equals(options.Protocol, "OpcDa", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(options.OpcProgId))
            throw new InvalidOperationException("Le ProgID du serveur OPC DA est obligatoire.");
        if (options.OpcUpdateRateMs is < 50 or > 60_000)
            throw new InvalidOperationException("L’actualisation OPC doit être comprise entre 50 et 60000 ms.");
        if (options.OpcTopic.IndexOfAny(['[', ']']) >= 0)
            throw new InvalidOperationException("Saisir le sujet OPC sans crochets.");
    }
}
