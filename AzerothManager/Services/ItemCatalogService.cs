using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

public sealed record ItemFilter(
    string? Text = null,
    int? Quality = null,
    int? Class = null,
    int? MinItemLevel = null,
    int? MaxItemLevel = null,
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

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            // Une saisie purement numérique cherche l'identifiant autant que le nom :
            // l'administrateur connaît souvent l'entry, pas le libellé exact.
            if (int.TryParse(filter.Text.Trim(), out var entry))
            {
                where.Add("(entry = @entry OR name LIKE @like)");
                parameters.Add(new MySqlParameter("@entry", entry));
            }
            else
            {
                where.Add("name LIKE @like");
            }
            parameters.Add(new MySqlParameter("@like", "%" + filter.Text.Trim() + "%"));
        }

        if (filter.Quality is { } q)
        {
            where.Add("Quality = @quality");
            parameters.Add(new MySqlParameter("@quality", q));
        }
        if (filter.Class is { } c)
        {
            where.Add("`class` = @class");
            parameters.Add(new MySqlParameter("@class", c));
        }
        if (filter.MinItemLevel is { } min)
        {
            where.Add("ItemLevel >= @minlvl");
            parameters.Add(new MySqlParameter("@minlvl", min));
        }
        if (filter.MaxItemLevel is { } max)
        {
            where.Add("ItemLevel <= @maxlvl");
            parameters.Add(new MySqlParameter("@maxlvl", max));
        }

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);

        int total;
        await using (var countCmd = cnx.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM item_template {clause}";
            foreach (var p in parameters) countCmd.Parameters.Add(Clone(p));
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var items = new List<ItemSummary>();
        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT entry, name, Quality, ItemLevel, RequiredLevel, `class`, subclass,
                       InventoryType, displayid, SellPrice, BuyPrice, stackable
                FROM item_template
                {clause}
                ORDER BY Quality DESC, ItemLevel DESC, entry
                LIMIT @take OFFSET @skip
                """;
            foreach (var p in parameters) cmd.Parameters.Add(Clone(p));
            cmd.Parameters.AddWithValue("@take", filter.PageSize);
            cmd.Parameters.AddWithValue("@skip", filter.Page * filter.PageSize);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                items.Add(new ItemSummary(
                    rd.GetInt32(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4),
                    rd.GetInt32(5), rd.GetInt32(6), rd.GetInt32(7), rd.GetInt32(8),
                    rd.GetInt64(9), rd.GetInt64(10), rd.GetInt32(11)));
            }
        }

        return new ItemSearchResult(items, total, filter.Page, filter.PageSize);
    }

    /// <summary>Un objet précis par son entry, pour les modules qui en reçoivent l'identifiant.</summary>
    public async Task<ItemSummary?> GetAsync(int entry, CancellationToken ct = default)
    {
        var result = await SearchAsync(new ItemFilter(Text: entry.ToString(), PageSize: 1), ct);
        return result.Items.FirstOrDefault(i => i.Entry == entry);
    }

    // Un MySqlParameter ne peut appartenir qu'à une commande : on le recopie pour la seconde.
    private static MySqlParameter Clone(MySqlParameter p) => new(p.ParameterName, p.Value);
}
