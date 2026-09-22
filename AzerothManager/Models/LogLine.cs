namespace AzerothManager.Models;

/// <summary>
/// Niveau d'une ligne de journal. Correspond aux étiquettes de l'appender AzerothCore
/// (FATAL, ERROR, WARN, INFO, DEBUG, TRACE), regroupées comme dans AzerothUpdater pour que
/// les deux applications colorent les journaux de la même façon.
/// </summary>
public enum LogSeverity
{
    /// <summary>Ligne sans étiquette reconnaissable : l'appender peut être configuré sans préfixe.</summary>
    Other,
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>Qui a produit la ligne : le serveur, ou l'administrateur depuis cette application.</summary>
public enum LogOrigin
{
    Server,
    Admin
}

/// <summary>
/// Une ligne de journal, qu'elle vienne d'un fichier du serveur ou de l'historique local.
///
/// Les deux sources sont représentées par le même type pour pouvoir être **entrelacées par
/// l'horodatage** : c'est la corrélation que le §3 attribue au Manager, là où la lecture en
/// direct reste du ressort de l'Updater.
/// </summary>
public sealed record LogLine(
    LogOrigin Origin,
    string Source,
    DateTime? At,
    LogSeverity Severity,
    string Text)
{
    public string TimeText => At?.ToString("dd/MM HH:mm:ss") ?? "—";

    public string SeverityText => Severity switch
    {
        LogSeverity.Error => "ERREUR",
        LogSeverity.Warning => "AVERT.",
        LogSeverity.Info => "INFO",
        LogSeverity.Debug => "DEBUG",
        _ => ""
    };

    /// <summary>Distingue d'un coup d'œil ce que le serveur a dit de ce que l'administrateur a fait.</summary>
    public string OriginIcon => Origin == LogOrigin.Admin ? "▸" : "";

    public bool IsAdmin => Origin == LogOrigin.Admin;
}

/// <summary>Journal disponible sur le serveur, tel que le listage du dossier le rapporte.</summary>
public sealed record LogFileInfo(string Path, string Name, string Directory, long Size, DateTime? Modified)
{
    public string SizeText => Size switch
    {
        >= 1024L * 1024 * 1024 => $"{Size / 1024d / 1024 / 1024:0.0} Go",
        >= 1024 * 1024 => $"{Size / 1024d / 1024:0.0} Mo",
        >= 1024 => $"{Size / 1024d:0} Ko",
        _ => $"{Size} o"
    };

    public string ModifiedText => Modified?.ToString("dd/MM/yyyy HH:mm") ?? "—";

    public string Label => $"{Name}  ({SizeText})";
}

/// <summary>
/// Résultat d'une lecture de journal.
///
/// <see cref="HasTimestamps"/> décide de ce que l'interface peut offrir : sans horodatage
/// dans les lignes, l'entrelacement avec les actions administrateur n'a pas de sens, et il
/// vaut mieux le dire que produire un ordre inventé.
/// </summary>
public sealed record LogReadResult(
    IReadOnlyList<LogLine> Lines,
    string Path,
    long FileSize,
    long BytesRead,
    bool HasTimestamps)
{
    /// <summary>Vrai si le fichier était plus gros que la fenêtre demandée : on n'a que la fin.</summary>
    public bool Truncated => BytesRead < FileSize;

    public DateTime? First => Lines.FirstOrDefault(l => l.At is not null)?.At;
    public DateTime? Last => Lines.LastOrDefault(l => l.At is not null)?.At;
}
