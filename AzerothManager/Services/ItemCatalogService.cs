using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

public sealed record ItemFilter(
    string? Text = null,
    int? Quality = null,
    int? Class = null,
    int? Subclass = null,
    int? MinItemLevel = null,
    int? MaxItemLevel = null,
    string? Locale = null,
    string? SortColumn = null,
    bool SortDescending = false,
    int Page = 0,
    int PageSize = 100);

public sealed record ItemSearchResult(IReadOnlyList<ItemSummary> Items, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// Catalogue d'objets (§19). Brique transverse : ce service alimentera le courrier,
/// l'inventaire, la banque, le loot, l'hôtel des ventes et l'armurerie.
///
/// item_template dépasse cinquante mille lignes : le filtrage et la pagination se font
/// côté SQL, jamais en mémoire (§17).
/// </summary>
public sealed class ItemCatalogService
{
    private readonly MySqlService _mySql;

    public ItemCatalogService(MySqlService mySql) => _mySql = mySql;

    public async Task<ItemSearchResult> SearchAsync(ItemFilter filter, CancellationToken ct = default)
    {
        var where = new List<string>();
        var parameters = new List<MySqlParameter>();

        // Les noms traduits vivent dans item_template_locale (8 langues sur le serveur de test,
        // 42 544 entrées frFR pour 46 098 objets). COALESCE retombe sur l'anglais quand la
        // traduction manque, plutôt que d'afficher un vide.
        var localized = !string.IsNullOrWhiteSpace(filter.Locale);
        var join = localized
            ? "LEFT JOIN item_template_locale l ON l.ID = t.entry AND l.locale = @locale"
            : "";
        var nameExpr = localized ? "COALESCE(NULLIF(l.Name, ''), t.name)" : "t.name";
        if (localized) parameters.Add(new MySqlParameter("@locale", filter.Locale));

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            // Une saisie purement numérique cherche l'identifiant autant que le nom :
            // l'administrateur connaît souvent l'entry, pas le libellé exact.
            // La recherche porte sur les deux libellés : on cherche parfois avec le nom
            // anglais trouvé sur un site, parfois avec le nom vu en jeu.
            var nameMatch = localized
                ? "(t.name LIKE @like OR l.Name LIKE @like)"
                : "t.name LIKE @like";

            if (int.TryParse(filter.Text.Trim(), out var entry))
            {
                where.Add($"(t.entry = @entry OR {nameMatch})");
                parameters.Add(new MySqlParameter("@entry", entry));
            }
            else
            {
                where.Add(nameMatch);
            }
            parameters.Add(new MySqlParameter("@like", "%" + filter.Text.Trim() + "%"));
        }

        if (filter.Quality is { } q)
        {
            where.Add("t.Quality = @quality");
            parameters.Add(new MySqlParameter("@quality", q));
        }
        if (filter.Class is { } c)
        {
            where.Add("t.`class` = @class");
            parameters.Add(new MySqlParameter("@class", c));
        }
        if (filter.Subclass is { } sub)
        {
            where.Add("t.subclass = @subclass");
            parameters.Add(new MySqlParameter("@subclass", sub));
        }
        if (filter.MinItemLevel is { } min)
        {
            where.Add("t.ItemLevel >= @minlvl");
            parameters.Add(new MySqlParameter("@minlvl", min));
        }
        if (filter.MaxItemLevel is { } max)
        {
            where.Add("t.ItemLevel <= @maxlvl");
            parameters.Add(new MySqlParameter("@maxlvl", max));
        }

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);

        int total;
        await using (var countCmd = cnx.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM item_template t {join} {clause}";
            foreach (var p in parameters) countCmd.Parameters.Add(Clone(p));
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var items = new List<ItemSummary>();
        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT t.entry, {nameExpr} AS display_name, t.name, t.Quality, t.ItemLevel,
                       t.RequiredLevel, t.`class`, t.subclass, t.InventoryType, t.displayid,
                       t.SellPrice, t.BuyPrice, t.stackable
                FROM item_template t
                {join}
                {clause}
                ORDER BY {OrderBy(filter, nameExpr)}
                LIMIT @take OFFSET @skip
                """;
            foreach (var p in parameters) cmd.Parameters.Add(Clone(p));
            cmd.Parameters.AddWithValue("@take", filter.PageSize);
            cmd.Parameters.AddWithValue("@skip", filter.Page * filter.PageSize);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                items.Add(new ItemSummary(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetInt32(4),
                    rd.GetInt32(5), rd.GetInt32(6), rd.GetInt32(7), rd.GetInt32(8), rd.GetInt32(9),
                    rd.GetInt64(10), rd.GetInt64(11), rd.GetInt32(12)));
            }
        }

        return new ItemSearchResult(items, total, filter.Page, filter.PageSize);
    }

    /// <summary>
    /// Le tri se fait côté SQL. Trier la page courante en mémoire donnerait un classement
    /// faux : il ne porterait que sur cent lignes parmi quarante-six mille.
    /// </summary>
    private static string OrderBy(ItemFilter filter, string nameExpr)
    {
        var column = filter.SortColumn switch
        {
            nameof(ItemSummary.Entry) => "t.entry",
            nameof(ItemSummary.Name) => nameExpr,
            nameof(ItemSummary.NameEn) => "t.name",
            nameof(ItemSummary.Quality) => "t.Quality",
            nameof(ItemSummary.ItemLevel) => "t.ItemLevel",
            nameof(ItemSummary.RequiredLevel) => "t.RequiredLevel",
            nameof(ItemSummary.Class) => "t.`class`",
            nameof(ItemSummary.Subclass) => "t.subclass",
            nameof(ItemSummary.InventoryType) => "t.InventoryType",
            nameof(ItemSummary.SellPrice) => "t.SellPrice",
            nameof(ItemSummary.Stackable) => "t.stackable",
            _ => null
        };

        if (column is null) return "t.Quality DESC, t.ItemLevel DESC, t.entry";
        var direction = filter.SortDescending ? "DESC" : "ASC";
        return $"{column} {direction}, t.entry";
    }

    /// <summary>
    /// Fiche complète d'un objet. Chargée à la demande sur un seul entry : ces colonnes
    /// n'ont pas leur place dans une liste de cent lignes.
    /// </summary>
    public async Task<ItemDetail?> GetDetailAsync(int entry, string? locale = null, CancellationToken ct = default)
    {
        var localized = !string.IsNullOrWhiteSpace(locale);
        var join = localized
            ? "LEFT JOIN item_template_locale l ON l.ID = t.entry AND l.locale = @locale"
            : "";
        var nameExpr = localized ? "COALESCE(NULLIF(l.Name, ''), t.name)" : "t.name";

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.entry, {nameExpr} AS display_name, t.name, t.Quality, t.ItemLevel, t.RequiredLevel,
                   t.`class`, t.subclass, t.InventoryType, t.displayid, t.SellPrice, t.BuyPrice, t.stackable,
                   t.bonding, t.MaxDurability, t.armor, t.dmg_min1, t.dmg_max1, t.dmg_type1, t.delay,
                   t.description,
                   t.stat_type1, t.stat_value1, t.stat_type2, t.stat_value2, t.stat_type3, t.stat_value3,
                   t.stat_type4, t.stat_value4, t.stat_type5, t.stat_value5, t.stat_type6, t.stat_value6,
                   t.stat_type7, t.stat_value7, t.stat_type8, t.stat_value8, t.stat_type9, t.stat_value9,
                   t.stat_type10, t.stat_value10,
                   t.spellid_1, t.spelltrigger_1, t.spellid_2, t.spelltrigger_2,
                   t.socketColor_1, t.socketColor_2, t.socketColor_3,
                   t.holy_res, t.fire_res, t.nature_res, t.frost_res, t.shadow_res, t.arcane_res
            FROM item_template t
            {join}
            WHERE t.entry = @entry
            """;
        if (localized) cmd.Parameters.AddWithValue("@locale", locale);
        cmd.Parameters.AddWithValue("@entry", entry);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        var summary = new ItemSummary(
            rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetInt32(4),
            rd.GetInt32(5), rd.GetInt32(6), rd.GetInt32(7), rd.GetInt32(8), rd.GetInt32(9),
            rd.GetInt64(10), rd.GetInt64(11), rd.GetInt32(12));

        var stats = new List<ItemStat>();
        for (var i = 0; i < 10; i++)
        {
            var type = rd.GetInt32(21 + i * 2);
            var value = rd.GetInt32(22 + i * 2);
            if (value != 0) stats.Add(new ItemStat(type, value));
        }

        var spells = new List<ItemSpell>();
        foreach (var (idIndex, triggerIndex) in new[] { (41, 42), (43, 44) })
        {
            var id = rd.GetInt32(idIndex);
            if (id > 0) spells.Add(new ItemSpell(id, rd.GetInt32(triggerIndex)));
        }

        var sockets = new List<int>();
        for (var i = 45; i <= 47; i++)
        {
            var color = rd.GetInt32(i);
            if (color > 0) sockets.Add(color);
        }

        return new ItemDetail(
            summary,
            Bonding: rd.GetInt32(13),
            Durability: rd.GetInt32(14),
            Armor: rd.GetInt32(15),
            DamageMin: rd.GetFloat(16),
            DamageMax: rd.GetFloat(17),
            DamageType: rd.GetInt32(18),
            Delay: rd.GetInt32(19),
            Description: rd.IsDBNull(20) ? "" : rd.GetString(20),
            Stats: stats,
            Spells: spells,
            Sockets: sockets,
            HolyRes: rd.GetInt32(48), FireRes: rd.GetInt32(49), NatureRes: rd.GetInt32(50),
            FrostRes: rd.GetInt32(51), ShadowRes: rd.GetInt32(52), ArcaneRes: rd.GetInt32(53));
    }

    /// <summary>Un objet précis par son entry, pour les modules qui en reçoivent l'identifiant.</summary>
    public async Task<ItemSummary?> GetAsync(int entry, string? locale = null, CancellationToken ct = default)
    {
        var result = await SearchAsync(new ItemFilter(Text: entry.ToString(), Locale: locale, PageSize: 1), ct);
        return result.Items.FirstOrDefault(i => i.Entry == entry);
    }

    // Un MySqlParameter ne peut appartenir qu'à une commande : on le recopie pour la seconde.
    private static MySqlParameter Clone(MySqlParameter p) => new(p.ParameterName, p.Value);
}
