namespace Conveyor.Web.Options;

// Only parcel metadata is sent to the external recorder; no video configuration belongs here.
public sealed class RecordingWebhookOptions
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "Axis1";
    public string WebhookUrl { get; set; } = "http://192.168.1.236:5180/api/events";
    public string WebhookKey { get; set; } = "";
    public bool RecordParcels { get; set; } = true;
    public List<int> LineIds { get; set; } = [0, 1];
    public Dictionary<int, double> TravelSeconds { get; set; } = new() { [24] = 96.6, [25] = 93.5, [26] = 90.4 };

    public string? ValidationError()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60) return "Webhook : saisir un nom de caméra de 1 à 60 caractères.";
        if (LineIds is null || LineIds.Count == 0 || LineIds.Any(id => id is < 0 or > 1))
            return "Webhook : choisir au moins une station de lecture.";
        if (TravelSeconds is null || TravelSeconds.Count == 0 || TravelSeconds.Count > 48 ||
            TravelSeconds.Any(pair => pair.Key is < 1 or > 48 || !double.IsFinite(pair.Value) || pair.Value is < 1 or > 600))
            return "Webhook : choisir des chutes de 1 à 48 avec un trajet de 1 à 600 secondes.";
        if (Enabled && (!Uri.TryCreate(WebhookUrl, UriKind.Absolute, out var webhook) ||
            webhook.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(webhook.UserInfo) ||
            !string.IsNullOrEmpty(webhook.Fragment) || WebhookKey.Length < 32))
            return "Webhook : configurer l’URL du récepteur et une clé partagée d’au moins 32 caractères.";
        return null;
    }
}
