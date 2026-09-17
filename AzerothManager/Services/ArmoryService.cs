using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>Ligne de la liste de personnages, avant ouverture de la fiche.</summary>
public sealed record CharacterListItem(
    int Guid, string Name, int Level, int Race, int Class, bool Online, string AccountName)
{
    public string RaceName => GameReference.RaceName(Race);
    public string ClassName => GameReference.ClassName(Class);
    public string FactionName => GameReference.FactionName(Race);
    public string StateText => Online ? "En ligne" : "—";
}

/// <summary>
/// Armurerie (§12). Lecture seule et entièrement hors ligne : les bases donnent les
/// données, les DBC importés donnent les icônes, les noms de cartes et de zones.
/// </summary>
public sealed class ArmoryService
{
    private readonly MySqlService _mySql;
    private readonly ServerContext _context;
    private readonly GameClientService _client;

    public ArmoryService(MySqlService mySql, ServerContext context, GameClientService client)
    {
        _mySql = mySql;
        _context = context;
        _client = client;
    }

    private string AuthDb => "`" + _context.RequireActive().AuthDatabase.Replace("`", "") + "`";
    private string WorldDb => "`" + _context.RequireActive().WorldDatabase.Replace("`", "") + "`";

    public async Task<IReadOnlyList<CharacterListItem>> SearchAsync(
        string? text, bool onlyOnline = false, int limit = 200, CancellationToken ct = default)
    {
        var list = new List<CharacterListItem>();
        var where = new List<string> { "c.deleteDate IS NULL" };
        if (!string.IsNullOrWhiteSpace(text)) where.Add("c.name LIKE @like");
        if (onlyOnline) where.Add("c.online = 1");

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.guid, c.name, c.level, c.race, c.class, c.online,
                   COALESCE(a.username, '') AS compte
            FROM characters c
            LEFT JOIN {AuthDb}.account a ON a.id = c.account
            WHERE {string.Join(" AND ", where)}
            ORDER BY c.level DESC, c.name
            LIMIT @take
            """;
        if (!string.IsNullOrWhiteSpace(text))
            cmd.Parameters.AddWithValue("@like", "%" + text.Trim() + "%");
        cmd.Parameters.AddWithValue("@take", limit);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new CharacterListItem(
                rd.GetInt32(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3),
                rd.GetInt32(4), rd.GetInt32(5) != 0, rd.GetString(6)));
        }
        return list;
    }

    public async Task<CharacterSheet?> GetSheetAsync(int guid, string? locale = null, CancellationToken ct = default)
    {
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);

        CharacterSheet? sheet;
        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.guid, c.account, COALESCE(a.username, ''), c.name, c.race, c.class, c.gender,
                       c.level, c.money, c.xp, c.online, c.totaltime, c.map, c.zone,
                       c.totalHonorPoints, c.arenaPoints, c.totalKills,
                       COALESCE(g.name, ''), COALESCE(gr.rname, ''),
                       (SELECT COUNT(*) FROM character_achievement ca WHERE ca.guid = c.guid)
                FROM characters c
                LEFT JOIN {AuthDb}.account a ON a.id = c.account
                LEFT JOIN guild_member gm ON gm.guid = c.guid
                LEFT JOIN guild g ON g.guildid = gm.guildid
                LEFT JOIN guild_rank gr ON gr.guildid = gm.guildid AND gr.rid = gm.rank
                WHERE c.guid = @guid
                """;
            cmd.Parameters.AddWithValue("@guid", guid);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;

            var mapId = rd.GetInt32(12);
            var zoneId = rd.GetInt32(13);

            sheet = new CharacterSheet(
                Guid: rd.GetInt32(0),
                AccountId: rd.GetInt32(1),
                AccountName: rd.GetString(2),
                Name: rd.GetString(3),
                Race: rd.GetInt32(4),
                Class: rd.GetInt32(5),
                Gender: rd.GetInt32(6),
                Level: rd.GetInt32(7),
                Money: rd.GetInt64(8),
                Xp: rd.GetInt64(9),
                Online: rd.GetInt32(10) != 0,
                TotalTimeSeconds: rd.GetInt32(11),
                MapId: mapId,
                MapName: _client.MapName(mapId),
                ZoneId: zoneId,
                ZoneName: _client.AreaName(zoneId),
                HonorPoints: rd.GetInt32(14),
                ArenaPoints: rd.GetInt32(15),
                TotalKills: rd.GetInt32(16),
                GuildName: rd.GetString(17),
                GuildRank: rd.GetString(18),
                Stats: null,
                Equipment: [],
                AchievementCount: rd.GetInt32(19));
        }

        var stats = await LoadStatsAsync(cnx, guid, ct);
        var equipment = await LoadEquipmentAsync(cnx, guid, locale, ct);
        return sheet with { Stats = stats, Equipment = equipment };
    }

    private static async Task<CharacterStats?> LoadStatsAsync(
        MySqlConnector.MySqlConnection cnx, int guid, CancellationToken ct)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT maxhealth, maxpower1, strength, agility, stamina, intellect, spirit, armor,
                   resHoly, resFire, resNature, resFrost, resShadow, resArcane,
                   blockPct, dodgePct, parryPct, critPct, rangedCritPct, spellCritPct,
                   attackPower, rangedAttackPower, spellPower, resilience
            FROM character_stats WHERE guid = @guid
            """;
        cmd.Parameters.AddWithValue("@guid", guid);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        return new CharacterStats(
            rd.GetInt32(0), rd.GetInt32(1),
            rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5), rd.GetInt32(6),
            rd.GetInt32(7),
            rd.GetInt32(8), rd.GetInt32(9), rd.GetInt32(10), rd.GetInt32(11), rd.GetInt32(12), rd.GetInt32(13),
            rd.GetFloat(14), rd.GetFloat(15), rd.GetFloat(16), rd.GetFloat(17),
            rd.GetFloat(18), rd.GetFloat(19),
            rd.GetInt32(20), rd.GetInt32(21), rd.GetInt32(22), rd.GetInt32(23));
    }

    /// <summary>
    /// Équipement porté : sac 0, emplacements 0 à 18. La jointure passe par item_instance,
    /// character_inventory ne stockant que le guid de l'objet, pas son entry.
    /// </summary>
    private async Task<IReadOnlyList<EquippedItem>> LoadEquipmentAsync(
        MySqlConnector.MySqlConnection cnx, int guid, string? locale, CancellationToken ct)
    {
        var localized = !string.IsNullOrWhiteSpace(locale);
        var join = localized
            ? $"LEFT JOIN {WorldDb}.item_template_locale l ON l.ID = it.entry AND l.locale = @locale"
            : "";
        var nameExpr = localized ? "COALESCE(NULLIF(l.Name, ''), it.name)" : "it.name";

        var found = new Dictionary<int, EquippedItem>();

        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT ci.slot, it.entry, {nameExpr} AS nom, it.Quality, it.ItemLevel, it.displayid
                FROM character_inventory ci
                JOIN item_instance ii ON ii.guid = ci.item
                JOIN {WorldDb}.item_template it ON it.entry = ii.itemEntry
                {join}
                WHERE ci.guid = @guid AND ci.bag = 0 AND ci.slot < @slots
                """;
            cmd.Parameters.AddWithValue("@guid", guid);
            cmd.Parameters.AddWithValue("@slots", EquipmentSlots.Count);
            if (localized) cmd.Parameters.AddWithValue("@locale", locale);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var slot = rd.GetInt32(0);
                found[slot] = new EquippedItem(slot, rd.GetInt32(1), rd.GetString(2),
                    rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5));
            }
        }

        // Les emplacements vides sont conservés : une fiche doit montrer ce qui manque.
        return Enumerable.Range(0, EquipmentSlots.Count)
            .Select(slot => found.GetValueOrDefault(slot, new EquippedItem(slot, 0, "", 1, 0, 0)))
            .ToList();
    }
}
