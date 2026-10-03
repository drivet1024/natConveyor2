namespace Conveyor.Web.Options;

public sealed class AxisCameraOptions
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "Axis1";
    public string BaseUrl { get; set; } = "http://10.11.5.6";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int VideoSource { get; set; } = 1;
    public int FramesPerSecond { get; set; } = 10;
    public string Resolution { get; set; } = "640x480";
    public bool RecordParcels { get; set; } = true;
    public string RecordingDirectory { get; set; } = "";
    // Keys are physical destinations, never rejection codes.
    public List<int> LineIds { get; set; } = [0, 1];
    public Dictionary<int, double> TravelSeconds { get; set; } = new() { [24] = 96.6, [25] = 93.5, [26] = 90.4 };
    public int RetentionDays { get; set; } = 7;
    public int MaximumStorageGb { get; set; } = 10;
    public const int BeforeSeconds = 10;
    public const int AfterSeconds = 5;

    public string? ValidationError()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60) return "Caméra Axis : saisir un nom de 1 à 60 caractères.";
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "Caméra Axis : saisir une adresse HTTP(S) sans chemin ni identifiants.";
        if (VideoSource is < 1 or > 16 || FramesPerSecond is < 1 or > 25)
            return "Caméra Axis : source de 1 à 16 et cadence de 1 à 25 images/s.";
        if (Resolution is not ("640x360" or "640x480" or "1280x720"))
            return "Caméra Axis : résolution non proposée.";
        if (LineIds is null || LineIds.Count == 0 || LineIds.Any(id => id is < 0 or > 1))
            return "Caméra Axis : choisir au moins une station de lecture.";
        if (TravelSeconds is null || TravelSeconds.Count == 0 || TravelSeconds.Count > 48 ||
            TravelSeconds.Any(pair => pair.Key is < 1 or > 48 || !double.IsFinite(pair.Value) || pair.Value is < 1 or > 600))
            return "Caméra Axis : choisir des chutes de 1 à 48 avec un trajet de 1 à 600 secondes.";
        if (RetentionDays is < 1 or > 90 || MaximumStorageGb is < 1 or > 500)
            return "Caméra Axis : conservation de 1 à 90 jours et espace de 1 à 500 Go.";
        if (Enabled && (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password)))
            return "Caméra Axis : renseigner le compte et le mot de passe avant l’activation.";
        if (!string.IsNullOrWhiteSpace(RecordingDirectory))
        {
            try
            {
                if (!Path.IsPathFullyQualified(RecordingDirectory) ||
                    Path.GetFullPath(RecordingDirectory).TrimEnd('\\', '/') == Path.GetPathRoot(Path.GetFullPath(RecordingDirectory))!.TrimEnd('\\', '/'))
                    return "Caméra Axis : choisir un dossier de sauvegarde absolu, pas la racine d’un disque.";
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { return "Caméra Axis : répertoire de sauvegarde invalide."; }
        }
        if (Enabled && RecordParcels && string.IsNullOrWhiteSpace(RecordingDirectory))
            return "Caméra Axis : configurer le répertoire de sauvegarde avant d’activer les enregistrements.";
        return null;
    }
}
