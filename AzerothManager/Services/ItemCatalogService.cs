using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

public sealed record ItemFilter(
    string? Text = null,
    int? Quality = null,
    int? Class = null,
    int? MinItemLevel = null,
    int? MaxItemLevel = null,
    string? Locale = null,
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
                ORDER BY t.Quality DESC, t.ItemLevel DESC, t.entry
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

    /// <summary>Un objet précis par son entry, pour les modules qui en reçoivent l'identifiant.</summary>
    public async Task<ItemSummary?> GetAsync(int entry, string? locale = null, CancellationToken ct = default)
    {
        var result = await SearchAsync(new ItemFilter(Text: entry.ToString(), Locale: locale, PageSize: 1), ct);
        return result.Items.FirstOrDefault(i => i.Entry == entry);
    }

    // Un MySqlParameter ne peut appartenir qu'à une commande : on le recopie pour la seconde.
    private static MySqlParameter Clone(MySqlParameter p) => new(p.ParameterName, p.Value);
}
