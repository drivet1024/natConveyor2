using System.Text.RegularExpressions;

namespace Conveyor.Web.Options;

public sealed class SmsOptions
{
    public bool Enabled { get; set; }
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string FromNumber { get; set; } = "";
    public string Recipients { get; set; } = "";

    public string[] GetRecipients() => Recipients.Split([';', ',', '\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();

    public string? ValidationError()
    {
        if (!Enabled) return null;
        if (!Regex.IsMatch(AccountSid.Trim(), @"\AAC[0-9a-fA-F]{32}\z"))
            return "SMS : renseigner un AccountSid Twilio valide (AC suivi de 32 caractères hexadécimaux).";
        if (string.IsNullOrWhiteSpace(AuthToken)) return "SMS : renseigner l’AuthToken Twilio.";
        if (!IsPhoneNumber(FromNumber.Trim())) return "SMS : le numéro Twilio émetteur doit être au format international, par exemple +15145551234.";
        var recipients = GetRecipients();
        if (recipients.Length == 0 || recipients.Any(number => !IsPhoneNumber(number)))
            return "SMS : renseigner les destinataires au format international (+15145551234), séparés par un point-virgule, une virgule ou un saut de ligne.";
        return null;
    }

    private static bool IsPhoneNumber(string value) => Regex.IsMatch(value, @"\A\+[1-9][0-9]{1,14}\z");
}
