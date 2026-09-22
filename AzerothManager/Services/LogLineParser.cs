using System.Text.RegularExpressions;
using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Analyse d'une ligne de journal AzerothCore.
///
/// Isolée du reste du service, et sans aucune dépendance : c'est la partie qu'on ne peut pas
/// vérifier contre le serveur tant qu'il est éteint, donc celle qui doit pouvoir être
/// éprouvée seule.
///
/// Le préfixe écrit par l'appender dépend de ses **drapeaux** (1 = horodatage, 2 = niveau,
/// 4 = type de filtre) : les trois parties sont optionnelles et un journal peut n'avoir
/// aucun préfixe du tout. Le format de l'horodatage a par ailleurs varié selon les versions.
/// L'analyse accepte donc plusieurs formes au lieu d'en figer une de mémoire, et se rabat
/// proprement sur « ligne sans niveau » quand rien n'est reconnu.
/// </summary>
public static class LogLineParser
{
    /// <summary>
    /// Horodatage en tête de ligne. Date et heure peuvent être séparées par un espace, un
    /// tiret bas ou un T, et l'heure elle-même par des deux-points ou des tirets ; le tout
    /// éventuellement entre crochets et suivi de fractions de seconde.
    /// </summary>
    private static readonly Regex StampRegex = new(
        @"^\s*\[?(?<date>\d{4}-\d{2}-\d{2})[ _T](?<time>\d{2}[:\-]\d{2}[:\-]\d{2})(?:[.,]\d+)?\]?\s*",
        RegexOptions.Compiled);

    /// <summary>Étiquette de niveau telle que l'appender l'écrit : juste après l'horodatage.</summary>
    private static readonly Regex LeadingTagRegex = new(
        @"^\[?(?<level>FATAL|ERROR|WARN(?:ING)?|INFO|DEBUG|TRACE)\]?\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Étiquette cherchée n'importe où dans la ligne, en mot entier — repli quand l'appender
    /// n'écrit pas de préfixe. Le mot entier évite les faux positifs (« information »,
    /// « warnaccount »), comme dans AzerothUpdater.
    /// </summary>
    private static readonly Regex AnyTagRegex = new(
        @"\b(FATAL|ERROR|WARN(?:ING)?|INFO|DEBUG|TRACE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Découpe et analyse un bloc de texte. L'horodatage et l'étiquette de niveau sont
    /// retirés du message : ils ont leur propre colonne, les répéter serait du bruit.
    /// </summary>
    public static IReadOnlyList<LogLine> Parse(string text, string source)
    {
        var result = new List<LogLine>();
        DateTime? previous = null;
        var previousSeverity = LogSeverity.Other;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            DateTime? at = null;
            var body = line;

            var stamp = StampRegex.Match(body);
            if (stamp.Success)
            {
                // L'heure peut s'écrire avec des tirets selon la version : ramenée à la
                // forme que DateTime sait lire.
                var time = stamp.Groups["time"].Value.Replace('-', ':');
                if (DateTime.TryParse($"{stamp.Groups["date"].Value} {time}", out var parsed))
                {
                    at = parsed;
                    body = body[stamp.Length..];
                }
            }

            // La suite d'un message multi-ligne — une pile d'appels, un bloc SQL — n'a ni
            // horodatage ni niveau : elle hérite de ceux de la ligne qui l'a ouverte, sinon
            // elle serait classée « autre » et disparaîtrait au premier filtre sur le niveau.
            //
            // L'héritage est conditionné à un horodatage **déjà vu** : dans un journal écrit
            // sans préfixe du tout, aucune ligne n'ouvre de message, et le niveau de la
            // première contaminerait tout le fichier. Chaque ligne est alors jugée seule.
            var inherited = at is null && previous is not null;
            at ??= previous;

            LogSeverity severity;
            var tag = LeadingTagRegex.Match(body);
            if (tag.Success)
            {
                severity = ToSeverity(tag.Groups["level"].Value);
                body = body[tag.Length..];
            }
            else
            {
                var any = AnyTagRegex.Match(body);
                severity = any.Success ? ToSeverity(any.Groups[1].Value)
                         : inherited ? previousSeverity
                         : LogSeverity.Other;
            }

            previous = at;
            previousSeverity = severity;

            result.Add(new LogLine(LogOrigin.Server, source, at, severity, body));
        }

        return result;
    }

    private static LogSeverity ToSeverity(string tag) => tag.ToUpperInvariant() switch
    {
        "FATAL" or "ERROR" => LogSeverity.Error,
        "WARN" or "WARNING" => LogSeverity.Warning,
        "INFO" => LogSeverity.Info,
        "DEBUG" or "TRACE" => LogSeverity.Debug,
        _ => LogSeverity.Other
    };
}
