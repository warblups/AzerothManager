using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Annonces, MOTD et arrêt planifié en jeu (§9).
///
/// Module entièrement piloté par commandes GM, et c'est ce qui le rend faisable : les
/// tables `.server`, `.autobroadcast` et celle de `cs_message.cpp` sont **toutes
/// `Console::Yes`** — vérifié dans les sources du serveur de référence. Contrairement aux
/// §10-11, rien ici n'exige une session de jeu.
///
/// **Frontière du §3.** On annonce et on planifie ; on ne gère pas le cycle de vie des
/// binaires, qui reste à l'Updater. La nuance compte sur ce serveur : PM2 tourne avec
/// `autorestart = true`, donc `.server shutdown` **ne l'arrête pas** — le processus sort et
/// PM2 le relance. En pratique, arrêt et redémarrage produisent le même résultat, avec un
/// compte à rebours annoncé aux joueurs. Le module le dit.
/// </summary>
public sealed class AnnounceService
{
    private readonly GmCommandService _gm;
    private readonly MySqlService _mySql;
    private readonly WorldServerConfig _config;
    private readonly ServerContext _context;

    public AnnounceService(GmCommandService gm, MySqlService mySql, WorldServerConfig config, ServerContext context)
    {
        _gm = gm;
        _mySql = mySql;
        _config = config;
        _context = context;
    }

    // ------------------------------------------------------------------ annonces

    /// <summary>
    /// Les six formes d'annonce, avec ce qu'elles produisent vraiment. Les deux dernières
    /// préfixent le message du nom de l'émetteur : `HandleNameAnnounceCommand` lit
    /// `handler->GetSession()`, et **SOAP n'a pas de session** — le nom affiché sera donc
    /// « Console », pas celui d'un personnage. Vérifié aux sources, et annoncé dans l'interface
    /// plutôt que découvert en jeu.
    /// </summary>
    public static IReadOnlyList<AnnounceForm> Forms { get; } =
    [
        new(AnnounceKind.Chat, "announce", "Tous — discussion",
            "Message système dans le canal de discussion de tous les joueurs connectés."),
        new(AnnounceKind.Screen, "notify", "Tous — plein écran",
            "Notification au milieu de l'écran de tous les joueurs connectés."),
        new(AnnounceKind.GmChat, "gmannounce", "Maîtres de jeu — discussion",
            "Discussion, visible des seuls maîtres de jeu connectés."),
        new(AnnounceKind.GmScreen, "gmnotify", "Maîtres de jeu — plein écran",
            "Notification à l'écran des seuls maîtres de jeu connectés."),
        new(AnnounceKind.NamedChat, "nameannounce", "Tous — signé",
            "Discussion à tous, préfixée du nom de l'émetteur. Envoyée d'ici, elle est signée « Console » : SOAP n'a pas de session de jeu."),
        new(AnnounceKind.NamedGmChat, "gmnameannounce", "Maîtres de jeu — signé",
            "Discussion aux maîtres de jeu, préfixée du nom. Signée « Console » depuis l'application."),
    ];

    /// <summary>
    /// Envoie une annonce. Le message est un `Tail` : il prend toute la fin de la ligne, donc
    /// ni guillemets ni échappement — mais un retour à la ligne couperait la commande.
    /// </summary>
    public async Task<GmCommandResult> AnnounceAsync(AnnounceKind kind, string message,
                                                     CancellationToken ct = default)
    {
        var text = SingleLine(message);
        if (text.Length == 0)
            return new GmCommandResult(false, "Le message est vide.", 0);

        var form = Forms.First(f => f.Kind == kind);
        var result = await _gm.ExecuteAsync($"{form.Command} {text}", ct);

        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm",
                $"annonce ({form.Label}) : {text}", result.Output);

        return result;
    }

    // ------------------------------------------------------------------ arrêt planifié

    /// <summary>
    /// Convertit un délai saisi en secondes, **avec la même règle que le serveur**.
    /// `TimeStringToSecs` accepte les suffixes `d`, `h`, `m`, `s`, cumulables (`1h30m`), et
    /// renvoie 0 sur tout autre caractère ; le gestionnaire se rabat alors sur un entier nu,
    /// interprété en secondes. Reproduire la règle ici permet d'afficher la durée réelle
    /// avant d'envoyer, au lieu de la découvrir dans le compte à rebours.
    /// </summary>
    public static int? TryParseDelay(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return null;

        var total = 0;
        var buffer = 0;
        var sawUnit = false;

        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                buffer = buffer * 10 + (c - '0');
                continue;
            }

            var multiplier = char.ToLowerInvariant(c) switch
            {
                'd' => 86400,
                'h' => 3600,
                'm' => 60,
                's' => 1,
                _ => 0
            };
            if (multiplier == 0) return null;   // format refusé par le serveur

            total += buffer * multiplier;
            buffer = 0;
            sawUnit = true;
        }

        // Pas de suffixe : le serveur relit la chaîne comme un nombre de secondes.
        if (!sawUnit) total = buffer;

        return total > 0 ? total : null;
    }

    public static string DescribeDelay(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days} j");
        if (span.Hours > 0) parts.Add($"{span.Hours} h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes} min");
        if (span.Seconds > 0) parts.Add($"{span.Seconds} s");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Vrai si le motif serait avalé par le paramètre `exitCode`.
    ///
    /// La signature est `(std::string time, Optional&lt;int32&gt; exitCode, Tail reason)` :
    /// un motif commençant par un nombre est lu comme code de sortie. Pire, la branche qui
    /// reçoit un code de sortie appelle `ShutdownServ(delay, 0, *exitCode)` **sans le motif**
    /// — le texte est alors perdu en silence. On refuse plutôt que de réécrire le message de
    /// l'utilisateur.
    /// </summary>
    public static bool ReasonWouldBeSwallowed(string reason)
    {
        var first = SingleLine(reason).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && int.TryParse(first, out var n) && n is >= 0 and <= 125;
    }

    /// <summary>
    /// Planifie un arrêt ou un redémarrage avec compte à rebours annoncé.
    ///
    /// **Aucun code de sortie n'est jamais transmis** : il ferait taire le motif (voir
    /// <see cref="ReasonWouldBeSwallowed"/>), et il n'a d'intérêt que pour un script de
    /// supervision — ce qui relève de l'Updater.
    /// </summary>
    public async Task<GmCommandResult> ScheduleShutdownAsync(string delay, string reason, bool restart,
                                                             CancellationToken ct = default)
    {
        if (TryParseDelay(delay) is not { } seconds)
            return new GmCommandResult(false,
                "Délai invalide. Attendu : un nombre de secondes, ou une durée avec suffixes d/h/m/s (par exemple 5m, 1h30m).", 0);

        var text = SingleLine(reason);
        if (ReasonWouldBeSwallowed(text))
            return new GmCommandResult(false,
                "Le motif ne peut pas commencer par un nombre entre 0 et 125 : le serveur le lirait " +
                "comme un code de sortie et n'annoncerait aucun motif. Reformulez.", 0);

        var verb = restart ? "restart" : "shutdown";
        var command = text.Length > 0 ? $"server {verb} {seconds} {text}" : $"server {verb} {seconds}";

        var result = await _gm.ExecuteAsync(command, ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm",
                $"{(restart ? "Redémarrage" : "Arrêt")} planifié dans {DescribeDelay(seconds)}" +
                (text.Length > 0 ? $" — {text}" : ""), result.Output);

        return result;
    }

    public Task<GmCommandResult> CancelShutdownAsync(CancellationToken ct = default)
        => _gm.ExecuteAsync("server shutdown cancel", ct);

    public Task<GmCommandResult> ServerInfoAsync(CancellationToken ct = default)
        => _gm.ExecuteAsync("server info", ct);

    // ------------------------------------------------------------------ MOTD et accès

    /// <summary>
    /// Locales acceptées par `.server set motd`. Le paramètre est **obligatoire** :
    /// `.server set motd Bonjour` échoue avec « locale (Bonjour) is not valid », le premier
    /// mot étant lu comme locale.
    ///
    /// La liste vient de `localeNames` dans `Common.cpp`, **pas du message d'erreur de la
    /// commande** : celui-ci annonce `zhWE`, qui n'existe nulle part ailleurs dans le code —
    /// la vraie locale est `zhTW`. La coquille est dans AzerothCore ; la recopier aurait
    /// proposé une valeur que `IsLocaleValid` refuse. Constaté en comparant la sortie réelle
    /// de `.server motd` à la source.
    /// </summary>
    public static IReadOnlyList<string> MotdLocales { get; } =
        ["frFR", "enUS", "deDE", "esES", "esMX", "ruRU", "koKR", "zhCN", "zhTW"];

    public Task<GmCommandResult> ReadMotdAsync(CancellationToken ct = default)
        => _gm.ExecuteAsync("server motd", ct);

    /// <summary>
    /// Écrit le MOTD. L'identifiant de royaume est facultatif et précède la locale : on ne le
    /// transmet pas, le serveur retenant alors le royaume courant. En `enUS` la valeur va dans
    /// `auth.motd`, sinon dans `auth.motd_localized`.
    /// </summary>
    public async Task<GmCommandResult> SetMotdAsync(string locale, string motd, CancellationToken ct = default)
    {
        var text = SingleLine(motd);
        if (text.Length == 0)
            return new GmCommandResult(false, "Le message du jour est vide.", 0);

        if (!MotdLocales.Contains(locale, StringComparer.Ordinal))
            return new GmCommandResult(false, $"Locale « {locale} » non reconnue par le serveur.", 0);

        var result = await _gm.ExecuteAsync($"server set motd {locale} {text}", ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm", $"MOTD ({locale}) : {text}", result.Output);

        return result;
    }

    /// <summary>`.server set closed on|off` — refuse ou réaccepte les connexions non maître de jeu.</summary>
    public async Task<GmCommandResult> SetClosedAsync(bool closed, CancellationToken ct = default)
    {
        var result = await _gm.ExecuteAsync($"server set closed {(closed ? "on" : "off")}", ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm",
                closed ? "Serveur fermé aux nouvelles connexions" : "Serveur rouvert aux connexions",
                result.Output);

        return result;
    }

    // ------------------------------------------------------------------ annonces récurrentes

    /// <summary>
    /// Liste des annonces récurrentes, lue en SQL plutôt qu'en analysant la sortie de
    /// `.autobroadcast list` : on récupère ainsi l'identifiant et le poids sous forme
    /// exploitable. La table est dans la base d'**authentification**, pas dans `world`.
    /// </summary>
    public async Task<IReadOnlyList<AutoBroadcastEntry>> ListBroadcastsAsync(CancellationToken ct = default)
    {
        var list = new List<AutoBroadcastEntry>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Auth, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT id, realmid, weight, text FROM autobroadcast ORDER BY weight DESC, id";

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new AutoBroadcastEntry(rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetString(3)));

        return list;
    }

    /// <summary>
    /// Ajoute une annonce récurrente. Le poids est un `uint8` : il pondère le tirage, une
    /// valeur plus élevée sortant plus souvent.
    ///
    /// **Pas de `.reload` à proposer ici**, contrairement à la règle du §9 sur les écritures
    /// dans `world` : `HandleAutobroadcastAddCommand` appelle lui-même
    /// `sAutobroadcastMgr->LoadAutobroadcasts()`. La prise en compte est immédiate.
    /// </summary>
    public async Task<GmCommandResult> AddBroadcastAsync(int weight, string text, CancellationToken ct = default)
    {
        var message = SingleLine(text);
        if (message.Length == 0)
            return new GmCommandResult(false, "Le texte de l'annonce est vide.", 0);
        if (weight is < 0 or > 255)
            return new GmCommandResult(false, "Le poids doit être compris entre 0 et 255.", 0);

        var result = await _gm.ExecuteAsync($"autobroadcast add {weight} {message}", ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm",
                $"Annonce récurrente ajoutée (poids {weight}) : {message}", result.Output);

        return result;
    }

    public async Task<GmCommandResult> RemoveBroadcastAsync(int id, CancellationToken ct = default)
    {
        var result = await _gm.ExecuteAsync($"autobroadcast remove {id}", ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm",
                $"Annonce récurrente {id} supprimée", result.Output);

        return result;
    }

    /// <summary>
    /// Lit l'état de la diffusion automatique dans `worldserver.conf`. Sans lui, une annonce
    /// récurrente ajoutée alors que `AutoBroadcast.On = 0` passerait pour un dysfonctionnement
    /// de l'application.
    /// </summary>
    public async Task<AutoBroadcastConfig?> ReadBroadcastConfigAsync(CancellationToken ct = default)
    {
        var read = await _config.ReadAsync("AutoBroadcast.", ct);
        if (read is null) return null;
        var (content, path) = read.Value;

        return new AutoBroadcastConfig(
            Enabled: WorldServerConfig.Option(content, "AutoBroadcast.On") == 1,
            TimerMs: WorldServerConfig.Option(content, "AutoBroadcast.Timer") ?? 0,
            Centered: WorldServerConfig.Option(content, "AutoBroadcast.Center") == 1,
            Source: path);
    }

    /// <summary>
    /// Un message d'annonce est un `Tail` : il court jusqu'à la fin de la ligne. Un retour
    /// chariot couperait donc la commande en deux, la seconde moitié devenant une commande
    /// inconnue.
    /// </summary>
    private static string SingleLine(string value) =>
        string.Join(" ", (value ?? "").Split('\n', '\r')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
}
