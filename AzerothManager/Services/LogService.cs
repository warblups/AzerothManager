using System.IO;
using System.Text;
using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Lecture des journaux du serveur (§14) et corrélation avec les actions administrateur (§3).
///
/// **Partage des rôles avec AzerothUpdater.** Le §3 donne à l'Updater la lecture *en direct*
/// d'Auth.log et Server.log — il la fait déjà, avec son propre suivi incrémental. Ce module
/// prend l'autre moitié : la *consultation* d'une fenêtre de journal, son filtrage, et
/// l'entrelacement avec l'historique local des requêtes SQL et des commandes GM. C'est ce
/// qu'un tail ne sait pas faire, et c'est ce qui répond à la question réelle : « qu'est-ce
/// que le serveur a dit quand j'ai lancé cette commande ? ». Il n'y a donc volontairement
/// pas de rafraîchissement automatique ici.
///
/// **Deux implémentations derrière une abstraction, comme le veut le §14** : lecture locale
/// sur serveur Windows, SFTP sur serveur Linux. Dans les deux cas on ne lit que la **fin**
/// du fichier : un Server.log dépasse couramment la dizaine de mégaoctets.
///
/// **Les noms de fichiers ne sont pas codés en dur.** Le dossier est listé et l'utilisateur
/// choisit : selon la configuration des appenders, un serveur écrit Server.log et Auth.log,
/// mais aussi DBErrors.log, Errors.log, des gm_*.log, et sous PM2 les
/// worldserver-error.log / worldserver-out.log qui sont les seuls à contenir un plantage au
/// démarrage. Lister vaut mieux que deviner.
/// </summary>
public sealed class LogService
{
    private readonly ServerContext _context;
    private readonly SshService _ssh;

    public LogService(ServerContext context, SshService ssh)
    {
        _context = context;
        _ssh = ssh;
    }

    // ------------------------------------------------------------------ découverte

    /// <summary>
    /// Emplacements où chercher un journal, par ordre de vraisemblance. Le dossier déclaré
    /// dans le profil d'abord ; les suivants couvrent les installations qui ne le
    /// renseignent pas, et le dossier de PM2, où atterrit la sortie du processus lui-même.
    /// </summary>
    public IReadOnlyList<string> CandidateDirectories()
    {
        var p = _context.RequireActive();
        var separator = p.UsesSsh ? '/' : '\\';
        var root = p.ServerPath.TrimEnd('/', '\\');

        var dirs = new List<string> { p.LogsPath.TrimEnd('/', '\\') };

        if (root.Length > 0)
        {
            dirs.Add(root + separator + "logs");
            dirs.Add(root + separator + "bin");
            dirs.Add(root);
        }

        // PM2 garde la sortie standard du worldserver dans le dossier de l'utilisateur, et
        // c'est le seul endroit où figure un plantage survenu avant l'ouverture de
        // Server.log (cf. le diagnostic SOAP : « Failed to find map files »). SFTP n'a pas
        // de shell, donc pas d'expansion de « ~ » : le chemin est construit explicitement.
        if (p.UsesSsh && !string.IsNullOrWhiteSpace(p.SshUser))
            dirs.Add($"/home/{p.SshUser}/.pm2/logs");

        return dirs.Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Journaux présents dans les emplacements candidats. Un dossier absent est ignoré :
    /// la liste est une exploration, pas une vérification de configuration.
    /// </summary>
    public async Task<IReadOnlyList<LogFileInfo>> ListAsync(CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        var found = new List<LogFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in CandidateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (p.UsesSsh)
                {
                    foreach (var f in await _ssh.ListFilesAsync(dir, ct))
                    {
                        if (!f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(f.Path))
                            found.Add(new LogFileInfo(f.Path, f.Name, dir, f.Size, f.Modified));
                    }
                }
                else
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var path in Directory.EnumerateFiles(dir, "*.log"))
                    {
                        var info = new FileInfo(path);
                        if (seen.Add(path))
                            found.Add(new LogFileInfo(path, info.Name, dir, info.Length, info.LastWriteTime));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Dossier inexistant, droits insuffisants : on passe au candidat suivant.
            }
        }

        // Les journaux du worldserver et de l'authserver en tête — ce sont ceux que l'on
        // ouvre neuf fois sur dix —, puis les plus récemment écrits.
        return found
            .OrderByDescending(f => Rank(f.Name))
            .ThenByDescending(f => f.Modified ?? DateTime.MinValue)
            .ToList();
    }

    private static int Rank(string name) => name.ToLowerInvariant() switch
    {
        "server.log" => 3,
        "auth.log" => 2,
        _ => name.Contains("error", StringComparison.OrdinalIgnoreCase) ? 1 : 0
    };

    // ------------------------------------------------------------------ lecture

    /// <summary>
    /// Lit au plus <paramref name="maxBytes"/> octets à la fin du journal et en analyse les
    /// lignes. Windows : lecture locale ; Linux : SFTP (§14).
    /// </summary>
    public async Task<LogReadResult> ReadTailAsync(LogFileInfo file, long maxBytes,
                                                   CancellationToken ct = default)
    {
        var p = _context.RequireActive();

        byte[] data;
        long size;

        if (p.UsesSsh)
        {
            (data, size) = await _ssh.ReadTailAsync(file.Path, maxBytes, ct);
        }
        else
        {
            (data, size) = await Task.Run(() =>
            {
                // FileShare.ReadWrite : le serveur garde son journal ouvert en écriture.
                using var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite);
                var length = fs.Length;
                var start = Math.Max(0, length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);

                using var buffer = new MemoryStream();
                fs.CopyTo(buffer, 64 * 1024);
                return (buffer.ToArray(), length);
            }, ct);
        }

        var text = Decode(data, partial: size > data.Length);
        var lines = LogLineParser.Parse(text, file.Name);

        return new LogReadResult(
            Lines: lines,
            Path: file.Path,
            FileSize: size,
            BytesRead: data.Length,
            HasTimestamps: lines.Any(l => l.At is not null),
            HasLevels: lines.Any(l => l.Severity != LogSeverity.Other));
    }

    /// <summary>
    /// Décode en UTF-8. Quand la lecture a commencé au milieu du fichier, le premier saut de
    /// ligne est la seule frontière sûre : avant lui on a une ligne tronquée, et peut-être un
    /// caractère accentué coupé en plein milieu de sa séquence d'octets.
    /// </summary>
    private static string Decode(byte[] data, bool partial)
    {
        var offset = 0;
        if (partial)
        {
            var newline = Array.IndexOf(data, (byte)'\n');
            if (newline >= 0) offset = newline + 1;
        }
        return Encoding.UTF8.GetString(data, offset, data.Length - offset);
    }

    // ------------------------------------------------------------------ corrélation

    /// <summary>
    /// Actions de l'administrateur enregistrées dans la base locale sur l'intervalle donné :
    /// requêtes SQL, commandes GM, opérations système. C'est la moitié Manager du §3 — le
    /// journal du serveur dit ce qui s'est passé, l'historique dit qui l'a demandé.
    ///
    /// L'intervalle est élargi d'une minute de chaque côté : l'horodatage de l'historique est
    /// pris sur cette machine, celui du journal sur le serveur, et les deux horloges ne sont
    /// pas synchronisées à la seconde.
    /// </summary>
    public IReadOnlyList<LogLine> ReadAdminActions(DateTime from, DateTime to, int limit = 500)
    {
        var margin = TimeSpan.FromMinutes(1);
        var lines = new List<LogLine>();

        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT Kind, Content, Result, RowsTouched, CreatedAt
            FROM History
            WHERE ($server IS NULL OR ServerId = $server)
              AND CreatedAt BETWEEN $from AND $to
            ORDER BY CreatedAt
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$server", (object?)_context.Active?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (from - margin).ToString("O"));
        cmd.Parameters.AddWithValue("$to", (to + margin).ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);

        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var kind = rd.GetString(0);
            var content = rd.GetString(1);
            var resultText = rd.GetString(2);
            var rows = rd.GetInt32(3);

            var at = DateTime.TryParse(rd.GetString(4), out var parsed) ? parsed : (DateTime?)null;

            var detail = new List<string>();
            if (rows > 0) detail.Add($"{rows} ligne(s)");
            if (!string.IsNullOrWhiteSpace(resultText)) detail.Add(Flatten(resultText));

            var text = detail.Count > 0
                ? $"{content}  →  {string.Join(" · ", detail)}"
                : content;

            lines.Add(new LogLine(LogOrigin.Admin, KindLabel(kind), at, LogSeverity.Other, text));
        }

        return lines;
    }

    private static string KindLabel(string kind) => kind switch
    {
        "sql" => "SQL",
        "gm" => "Commande GM",
        _ => "Action"
    };

    /// <summary>
    /// Le résultat d'une commande GM est multi-ligne : réduit à une ligne, et tronqué, pour
    /// qu'une entrée d'historique reste une entrée dans la chronologie.
    /// </summary>
    private static string Flatten(string value)
    {
        var single = string.Join(" ", value.Split('\n', '\r')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
        return single.Length > 160 ? single[..160] + "…" : single;
    }
}
