using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Guildes (§8) et banque de guilde (§9).
///
/// Module hybride, comme le veut le §5 : **SQL pour la consultation**, qui est l'essentiel de
/// la valeur ici — le §9 range la banque de guilde parmi les motifs de ticket récurrents, et
/// un litige de vol s'arbitre en lisant le journal, pas en lançant une commande — et
/// **commandes GM pour les écritures**, qui tiennent les structures en mémoire du serveur à
/// jour. Insérer dans `guild_member` à la main laisserait le worldserver ignorer le nouveau
/// membre jusqu'au redémarrage.
///
/// Les sept commandes `.guild` sont `Console::Yes` (vérifié dans `cs_guild.cpp`), donc
/// disponibles par SOAP. Une seule exception de fond : **`.guild create` exige un joueur
/// connecté** (`if (!target || !target->IsConnected())`), là où invite, uninvite et rank
/// fonctionnent hors ligne en passant par `sCharacterCache`.
/// </summary>
public sealed class GuildService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;

    /// <summary>Langue des noms localisés, comme dans l'édition du monde.</summary>
    private const string Locale = "frFR";

    public GuildService(MySqlService mySql, GmCommandService gm, ServerContext context)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
    }

    private string WorldDb => "`" + _context.RequireActive().WorldDatabase.Replace("`", "") + "`";

    /// <summary>
    /// Les noms de guilde sont des `QuotedString` côté serveur : sans guillemets, un nom en
    /// deux mots serait coupé. Les guillemets internes casseraient l'analyse, on les
    /// neutralise — même convention que <c>MailService</c>.
    /// </summary>
    private static string Quote(string value) =>
        "\"" + (value ?? "").Replace("\"", "'").ReplaceLineEndings(" ").Trim() + "\"";

    private static DateTime FromUnix(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime;

    // ------------------------------------------------------------------ consultation

    /// <summary>
    /// Liste des guildes, avec leur meneur et leur effectif. Le nombre de membres vient d'une
    /// sous-requête plutôt que d'un GROUP BY sur une jointure, pour que les guildes vides
    /// apparaissent tout de même — ce sont justement celles qu'on cherche à dissoudre.
    /// </summary>
    public async Task<IReadOnlyList<GuildSummary>> SearchAsync(string? text, int limit = 200,
                                                               CancellationToken ct = default)
    {
        var list = new List<GuildSummary>();
        var filter = string.IsNullOrWhiteSpace(text) ? "" : "WHERE g.name LIKE @like";

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT g.guildid, g.name, g.leaderguid, COALESCE(c.name, ''),
                   (SELECT COUNT(*) FROM guild_member m WHERE m.guildid = g.guildid),
                   g.BankMoney, g.createdate, COALESCE(g.motd, ''), COALESCE(g.info, '')
            FROM guild g
            LEFT JOIN characters c ON c.guid = g.leaderguid
            {filter}
            ORDER BY g.name
            LIMIT @limit
            """;
        if (filter.Length > 0) cmd.Parameters.AddWithValue("@like", "%" + text!.Trim() + "%");
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var created = rd.GetInt64(6);
            list.Add(new GuildSummary(
                Id: rd.GetInt32(0),
                Name: rd.GetString(1),
                LeaderGuid: rd.GetInt32(2),
                LeaderName: rd.GetString(3),
                MemberCount: rd.GetInt32(4),
                BankMoney: rd.GetInt64(5),
                CreatedAt: created > 0 ? FromUnix(created) : null,
                Motd: rd.GetString(7),
                Info: rd.GetString(8)));
        }
        return list;
    }

    /// <summary>
    /// Membres, avec le nom de leur rang. La jointure sur `guild_rank` porte **sur la guilde
    /// et le rang** : `rid` est numéroté par guilde, pas globalement.
    /// </summary>
    public async Task<IReadOnlyList<GuildMember>> MembersAsync(int guildId, CancellationToken ct = default)
    {
        var list = new List<GuildMember>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT m.guid, COALESCE(c.name, ''), COALESCE(c.level, 0), COALESCE(c.class, 0),
                   m.rank, COALESCE(r.rname, ''), COALESCE(c.online, 0),
                   COALESCE(m.pnote, ''), COALESCE(m.offnote, '')
            FROM guild_member m
            LEFT JOIN characters c ON c.guid = m.guid
            LEFT JOIN guild_rank r ON r.guildid = m.guildid AND r.rid = m.rank
            WHERE m.guildid = @guild
            ORDER BY m.rank, c.name
            """;
        cmd.Parameters.AddWithValue("@guild", guildId);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new GuildMember(
                Guid: rd.GetInt32(0),
                Name: rd.GetString(1),
                Level: rd.GetInt32(2),
                ClassId: rd.GetInt32(3),
                RankId: rd.GetInt32(4),
                RankName: rd.GetString(5),
                Online: rd.GetInt32(6) != 0,
                PublicNote: rd.GetString(7),
                OfficerNote: rd.GetString(8)));
        }
        return list;
    }

    public async Task<IReadOnlyList<GuildRank>> RanksAsync(int guildId, CancellationToken ct = default)
    {
        var list = new List<GuildRank>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT rid, COALESCE(rname, ''), rights, BankMoneyPerDay
            FROM guild_rank WHERE guildid = @guild ORDER BY rid
            """;
        cmd.Parameters.AddWithValue("@guild", guildId);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new GuildRank(rd.GetInt32(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt64(3)));

        return list;
    }

    public async Task<IReadOnlyList<GuildBankTab>> BankTabsAsync(int guildId, CancellationToken ct = default)
    {
        var list = new List<GuildBankTab>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT TabId, COALESCE(TabName, ''), COALESCE(TabIcon, ''), COALESCE(TabText, '')
            FROM guild_bank_tab WHERE guildid = @guild ORDER BY TabId
            """;
        cmd.Parameters.AddWithValue("@guild", guildId);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new GuildBankTab(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)));

        return list;
    }

    /// <summary>
    /// Contenu d'un onglet. `guild_bank_item` ne porte que le guid de l'objet : il faut passer
    /// par `item_instance` pour obtenir l'`itemEntry`, puis `world.item_template` — exactement
    /// comme l'inventaire d'un personnage dans l'armurerie.
    /// </summary>
    public async Task<IReadOnlyList<GuildBankItem>> BankItemsAsync(int guildId, int? tabId,
                                                                   CancellationToken ct = default)
    {
        var list = new List<GuildBankItem>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT b.TabId, b.SlotId, b.item_guid, it.entry,
                   COALESCE(NULLIF(l.Name, ''), it.name),
                   it.Quality, COALESCE(ii.count, 1), it.displayid
            FROM guild_bank_item b
            JOIN item_instance ii ON ii.guid = b.item_guid
            JOIN {WorldDb}.item_template it ON it.entry = ii.itemEntry
            LEFT JOIN {WorldDb}.item_template_locale l ON l.ID = it.entry AND l.locale = @locale
            WHERE b.guildid = @guild {(tabId is null ? "" : "AND b.TabId = @tab")}
            ORDER BY b.TabId, b.SlotId
            """;
        cmd.Parameters.AddWithValue("@guild", guildId);
        if (tabId is not null) cmd.Parameters.AddWithValue("@tab", tabId);
        cmd.Parameters.AddWithValue("@locale", Locale);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new GuildBankItem(
                TabId: rd.GetInt32(0),
                SlotId: rd.GetInt32(1),
                ItemGuid: rd.GetInt32(2),
                Entry: rd.GetInt32(3),
                Name: rd.GetString(4),
                Quality: rd.GetInt32(5),
                Count: rd.GetInt32(6),
                DisplayId: rd.GetInt32(7)));
        }
        return list;
    }

    /// <summary>
    /// Journal de la banque — la pièce qui rend un litige de vol arbitrable.
    ///
    /// Le nom de l'objet n'est résolu que pour les événements d'objet : sur un mouvement
    /// d'argent, `ItemOrMoney` est un **montant en cuivre**, et le joindre à `item_template`
    /// afficherait le nom de l'objet dont l'identifiant vaut ce montant. C'est le piège
    /// principal de cette table.
    /// </summary>
    public async Task<IReadOnlyList<GuildBankEvent>> BankLogAsync(int guildId, int? tabId, int limit = 500,
                                                                  CancellationToken ct = default)
    {
        var list = new List<GuildBankEvent>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.LogGuid, e.TabId, e.EventType, e.PlayerGuid, COALESCE(c.name, ''),
                   e.ItemOrMoney, e.ItemStackCount, e.DestTabId, e.TimeStamp,
                   COALESCE(CASE WHEN e.EventType IN (4, 5, 6) THEN '' ELSE it.name END, '')
            FROM guild_bank_eventlog e
            LEFT JOIN characters c ON c.guid = e.PlayerGuid
            LEFT JOIN {WorldDb}.item_template it
                   ON e.EventType NOT IN (4, 5, 6) AND it.entry = e.ItemOrMoney
            WHERE e.guildid = @guild {(tabId is null ? "" : "AND e.TabId = @tab")}
            ORDER BY e.TimeStamp DESC, e.LogGuid DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@guild", guildId);
        if (tabId is not null) cmd.Parameters.AddWithValue("@tab", tabId);
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new GuildBankEvent(
                LogGuid: rd.GetInt32(0),
                TabId: rd.GetInt32(1),
                Type: (GuildBankEventType)rd.GetInt32(2),
                PlayerGuid: rd.GetInt32(3),
                PlayerName: rd.GetString(4),
                ItemOrMoney: rd.GetInt64(5),
                StackCount: rd.GetInt32(6),
                DestTabId: rd.GetInt32(7),
                At: FromUnix(rd.GetInt64(8)),
                ItemName: rd.GetString(9)));
        }
        return list;
    }

    /// <summary>
    /// Journal de la guilde : arrivées, départs, promotions et exclusions. `PlayerGuid1` est
    /// la cible, `PlayerGuid2` l'auteur quand l'action en a un.
    /// </summary>
    public async Task<IReadOnlyList<GuildEvent>> EventLogAsync(int guildId, int limit = 500,
                                                               CancellationToken ct = default)
    {
        var list = new List<GuildEvent>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT e.LogGuid, e.EventType, e.PlayerGuid1, COALESCE(c1.name, ''),
                   e.PlayerGuid2, COALESCE(c2.name, ''), e.NewRank, e.TimeStamp
            FROM guild_eventlog e
            LEFT JOIN characters c1 ON c1.guid = e.PlayerGuid1
            LEFT JOIN characters c2 ON c2.guid = e.PlayerGuid2
            WHERE e.guildid = @guild
            ORDER BY e.TimeStamp DESC, e.LogGuid DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@guild", guildId);
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new GuildEvent(
                LogGuid: rd.GetInt32(0),
                Type: (GuildEventType)rd.GetInt32(1),
                PlayerGuid1: rd.GetInt32(2),
                PlayerName1: rd.GetString(3),
                PlayerGuid2: rd.GetInt32(4),
                PlayerName2: rd.GetString(5),
                NewRank: rd.GetInt32(6),
                At: FromUnix(rd.GetInt64(7))));
        }
        return list;
    }

    // ------------------------------------------------------------------ écritures

    /// <summary>
    /// `.guild create <joueur> "Nom"` — **le joueur doit être connecté** : le gestionnaire
    /// refuse sur `!target->IsConnected()`, la guilde étant construite à partir de l'objet
    /// Player en mémoire. C'est la seule commande de guilde qui l'exige.
    /// </summary>
    public Task<GmCommandResult> CreateAsync(string leader, string name, CancellationToken ct = default)
        => Run($"guild create {leader.Trim()} {Quote(name)}",
               $"Guilde « {name} » créée avec {leader} pour meneur", ct);

    /// <summary>
    /// `.guild delete "Nom"` — appelle `Disband()`. La guilde, ses rangs, ses membres et le
    /// contenu de sa banque disparaissent : c'est irréversible depuis l'application.
    /// </summary>
    public Task<GmCommandResult> DeleteAsync(string name, CancellationToken ct = default)
        => Run($"guild delete {Quote(name)}", $"Guilde « {name} » dissoute", ct);

    public Task<GmCommandResult> RenameAsync(string oldName, string newName, CancellationToken ct = default)
        => Run($"guild rename {Quote(oldName)} {Quote(newName)}",
               $"Guilde « {oldName} » renommée en « {newName} »", ct);

    /// <summary>`.guild invite <joueur> "Nom"` — fonctionne sur un personnage déconnecté.</summary>
    public Task<GmCommandResult> InviteAsync(string player, string guildName, CancellationToken ct = default)
        => Run($"guild invite {player.Trim()} {Quote(guildName)}",
               $"{player} ajouté à « {guildName} »", ct);

    /// <summary>`.guild uninvite <joueur>` — la guilde est déduite du personnage.</summary>
    public Task<GmCommandResult> UninviteAsync(string player, CancellationToken ct = default)
        => Run($"guild uninvite {player.Trim()}", $"{player} exclu de sa guilde", ct);

    /// <summary>
    /// `.guild rank <joueur> <rang>` — le rang est un `rid` **de la guilde du joueur**, et
    /// `ChangeMemberRank` refuse une valeur qui n'y existe pas.
    /// </summary>
    public Task<GmCommandResult> SetRankAsync(string player, int rank, CancellationToken ct = default)
        => Run($"guild rank {player.Trim()} {rank}", $"{player} placé au rang {rank}", ct);

    public Task<GmCommandResult> InfoAsync(string guildName, CancellationToken ct = default)
        => _gm.ExecuteAsync($"guild info {Quote(guildName)}", ct);

    private async Task<GmCommandResult> Run(string command, string description, CancellationToken ct)
    {
        var result = await _gm.ExecuteAsync(command, ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm", description, result.Output);
        return result;
    }
}
