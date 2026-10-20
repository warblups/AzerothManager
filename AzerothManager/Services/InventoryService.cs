using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

/// <summary>
/// Inventaire et banque d'un personnage (§8).
///
/// Module hybride selon le §5 : **SQL pour la consultation**, **commandes GM pour les
/// écritures**. La consultation couvre tout ce qui appartient au personnage, pas seulement
/// ses poches : un objet « perdu » attend souvent au courrier ou à l'hôtel des ventes, et ne
/// pas l'y chercher ferait conclure à une disparition.
///
/// Écritures, vérifiées dans `cs_misc.cpp` et `cs_item.cpp`, toutes `Console::Yes` :
///
/// - **`.additem <joueur> <id> <n>` ajoute, mais seulement à un joueur connecté** — le
///   gestionnaire répond `LANG_PLAYER_NOT_FOUND` sinon. Hors ligne, l'objet part par courrier.
/// - **`.additem <joueur> <id> -<n>` retire, connecté ou non.** Hors ligne, le serveur écrit
///   lui-même en base et nettoie les tables satellites (cadeau, remboursement, échange lié,
///   contenu). Le retrait porte **sur le modèle, pas sur une pile** : impossible de viser un
///   exemplaire précis.
/// - **`.item restore <id> <joueur>`** renvoie par courrier un objet de `recovery_item`.
///
/// Rien ne s'écrit ici en SQL : un `DELETE` laisserait l'objet en mémoire chez un joueur
/// connecté, et le serveur le réécrirait à la sauvegarde suivante.
/// </summary>
public sealed class InventoryService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly MailService _mail;
    private readonly WorldServerConfig _config;
    private readonly ServerContext _context;

    private const string Locale = "frFR";

    /// <summary>
    /// Plafond de la section hôtel des ventes. Un personnage de l'AHbot en porte des dizaines
    /// de milliers — 45 481 sur le serveur de référence — que personne ne lira ligne à ligne.
    /// </summary>
    public const int AuctionLimit = 500;

    public InventoryService(MySqlService mySql, GmCommandService gm, MailService mail,
                            WorldServerConfig config, ServerContext context)
    {
        _mySql = mySql;
        _gm = gm;
        _mail = mail;
        _config = config;
        _context = context;
    }

    private string WorldDb => "`" + _context.RequireActive().WorldDatabase.Replace("`", "") + "`";

    private static DateTime FromUnix(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime;

    /// <summary>Colonnes communes aux trois lectures, à partir d'`item_instance ii`.</summary>
    private const string ItemColumns = """
        ii.guid, COALESCE(ii.itemEntry, 0),
        COALESCE(NULLIF(l.Name, ''), it.name, CONCAT('objet ', ii.itemEntry)),
        COALESCE(it.Quality, 1), COALESCE(it.ItemLevel, 0), COALESCE(it.displayid, 0),
        ii.count, COALESCE(ii.flags, 0), ii.durability, COALESCE(it.MaxDurability, 0),
        COALESCE(cr.name, ''), COALESCE(it.ContainerSlots, 0)
        """;

    /// <summary>
    /// Jointures des colonnes communes. `item_template` est en `LEFT JOIN` : un objet dont le
    /// modèle a été retiré de `world` doit rester visible, c'est justement une anomalie.
    /// </summary>
    private string ItemJoins => $"""
        LEFT JOIN {WorldDb}.item_template it ON it.entry = ii.itemEntry
        LEFT JOIN {WorldDb}.item_template_locale l ON l.ID = ii.itemEntry AND l.locale = @locale
        LEFT JOIN characters cr ON ii.creatorGuid <> 0 AND cr.guid = ii.creatorGuid
        """;

    private static InventoryItem ReadItem(MySqlDataReader rd, int offset,
                                          InventorySection section, string place, int sortKey) =>
        new(section, place, sortKey,
            ItemGuid: rd.GetInt32(offset),
            Entry: rd.GetInt32(offset + 1),
            Name: rd.GetString(offset + 2),
            Quality: rd.GetInt32(offset + 3),
            ItemLevel: rd.GetInt32(offset + 4),
            DisplayId: rd.GetInt32(offset + 5),
            Count: rd.GetInt32(offset + 6),
            // ITEM_FIELD_FLAG_SOULBOUND (ItemTemplate.h)
            Soulbound: (rd.GetInt64(offset + 7) & 0x1) != 0,
            Durability: rd.GetInt32(offset + 8),
            MaxDurability: rd.GetInt32(offset + 9),
            CreatorName: rd.GetString(offset + 10),
            ContainerSlots: rd.GetInt32(offset + 11));

    /// <summary>
    /// Tout ce qui appartient au personnage : poches et banque, courrier, hôtel des ventes.
    /// Le nombre total d'enchères est rendu à part, la section étant plafonnée.
    /// </summary>
    public async Task<(IReadOnlyList<InventoryItem> Items, int AuctionTotal)> LoadAsync(
        int characterGuid, CancellationToken ct = default)
    {
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);

        var items = await CarriedAsync(cnx, characterGuid, ct);
        items.AddRange(await PendingMailAsync(cnx, characterGuid, ct));

        var (auctions, total) = await AuctionsAsync(cnx, characterGuid, ct);
        items.AddRange(auctions);

        return (items, total);
    }

    private async Task<List<InventoryItem>> CarriedAsync(MySqlConnection cnx, int guid, CancellationToken ct)
    {
        var rows = new List<(int Bag, int Slot, InventoryItem Item)>();

        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT ci.bag, ci.slot, {ItemColumns}
                FROM character_inventory ci
                JOIN item_instance ii ON ii.guid = ci.item
                {ItemJoins}
                WHERE ci.guid = @guid
                """;
            cmd.Parameters.AddWithValue("@guid", guid);
            cmd.Parameters.AddWithValue("@locale", Locale);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add((rd.GetInt32(0), rd.GetInt32(1), ReadItem(rd, 2, InventorySection.Other, "", 0)));
        }

        // `bag` est le guid du sac, pas son emplacement : on relève d'abord où chaque sac est
        // rangé, pour pouvoir dire ensuite si son contenu est sur le personnage ou en banque.
        var bagSlots = rows.Where(r => r.Bag == 0 && r.Item.IsContainer)
                           .ToDictionary(r => r.Item.ItemGuid, r => r.Slot);
        var contents = rows.Where(r => r.Bag != 0)
                           .GroupBy(r => r.Bag)
                           .ToDictionary(g => g.Key, g => g.Count());

        return rows.Select(r =>
            {
                var (section, place, key) = InventoryLayout.Locate(r.Bag, r.Slot, bagSlots);
                return r.Item with
                {
                    Section = section,
                    Place = place,
                    SortKey = key,
                    ContentCount = contents.GetValueOrDefault(r.Item.ItemGuid)
                };
            })
            .ToList();
    }

    /// <summary>
    /// Pièces jointes en attente. `mail_items.receiver` est dénormalisé, ce qui évite de
    /// passer par `mail` pour filtrer ; `mail` donne l'expéditeur et l'échéance.
    /// </summary>
    private async Task<List<InventoryItem>> PendingMailAsync(MySqlConnection cnx, int guid, CancellationToken ct)
    {
        var list = new List<InventoryItem>();

        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.messageType, COALESCE(s.name, ''), m.expire_time, {ItemColumns}
            FROM mail_items mi
            JOIN mail m ON m.id = mi.mail_id
            JOIN item_instance ii ON ii.guid = mi.item_guid
            LEFT JOIN characters s ON m.messageType = 0 AND s.guid = m.sender
            {ItemJoins}
            WHERE mi.receiver = @guid
            ORDER BY m.expire_time
            """;
        cmd.Parameters.AddWithValue("@guid", guid);
        cmd.Parameters.AddWithValue("@locale", Locale);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var order = 0;
        while (await rd.ReadAsync(ct))
        {
            var sender = InventoryLayout.MailSender(rd.GetInt32(0), rd.GetString(1));
            var expires = FromUnix(rd.GetInt64(2));
            list.Add(ReadItem(rd, 3, InventorySection.Mail,
                $"Courrier de {sender}, expire le {expires:dd/MM/yyyy}", order++));
        }
        return list;
    }

    /// <summary>`auctionhouse.time` est l'échéance de l'enchère, en horodatage Unix.</summary>
    private async Task<(List<InventoryItem>, int)> AuctionsAsync(MySqlConnection cnx, int guid, CancellationToken ct)
    {
        int total;
        await using (var count = cnx.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM auctionhouse WHERE itemowner = @guid";
            count.Parameters.AddWithValue("@guid", guid);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }
        if (total == 0) return ([], 0);

        var list = new List<InventoryItem>();
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.time, a.buyoutprice, {ItemColumns}
            FROM auctionhouse a
            JOIN item_instance ii ON ii.guid = a.itemguid
            {ItemJoins}
            WHERE a.itemowner = @guid
            ORDER BY a.time
            LIMIT @take
            """;
        cmd.Parameters.AddWithValue("@guid", guid);
        cmd.Parameters.AddWithValue("@locale", Locale);
        cmd.Parameters.AddWithValue("@take", AuctionLimit);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var order = 0;
        while (await rd.ReadAsync(ct))
        {
            var ends = FromUnix(rd.GetInt64(0));
            var buyout = rd.GetInt64(1);
            var place = $"En vente jusqu'au {ends:dd/MM/yyyy HH:mm}" +
                        (buyout > 0 ? $", achat immédiat {ItemReference.Money(buyout)}" : "");
            list.Add(ReadItem(rd, 2, InventorySection.Auction, place, order++));
        }
        return (list, total);
    }

    /// <summary>Objets archivés pour ce personnage, les plus récents d'abord — l'ordre de `.item restore list`.</summary>
    public async Task<IReadOnlyList<RecoveryItem>> RecoveryAsync(int characterGuid, CancellationToken ct = default)
    {
        var list = new List<RecoveryItem>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT r.Id, COALESCE(r.ItemEntry, 0), r.Count, r.DeleteDate,
                   COALESCE(NULLIF(l.Name, ''), it.name, CONCAT('objet ', r.ItemEntry)),
                   COALESCE(it.Quality, 1), COALESCE(it.displayid, 0)
            FROM recovery_item r
            LEFT JOIN {WorldDb}.item_template it ON it.entry = r.ItemEntry
            LEFT JOIN {WorldDb}.item_template_locale l ON l.ID = r.ItemEntry AND l.locale = @locale
            WHERE r.Guid = @guid
            ORDER BY r.Id DESC
            """;
        cmd.Parameters.AddWithValue("@guid", characterGuid);
        cmd.Parameters.AddWithValue("@locale", Locale);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new RecoveryItem(
                rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2),
                rd.IsDBNull(3) ? null : FromUnix(rd.GetInt64(3)),
                rd.GetString(4), rd.GetInt32(5), rd.GetInt32(6)));
        }
        return list;
    }

    public async Task<ItemDeleteConfig?> ReadItemDeleteConfigAsync(CancellationToken ct = default)
    {
        var read = await _config.ReadAsync("ItemDelete.", ct);
        if (read is null) return null;
        var (content, path) = read.Value;

        // Valeurs par défaut de WorldConfig.cpp, pour un réglage absent du fichier.
        return new ItemDeleteConfig(
            Method: WorldServerConfig.Option(content, "ItemDelete.Method") == 1,
            Vendor: WorldServerConfig.Option(content, "ItemDelete.Vendor") == 1,
            MinQuality: WorldServerConfig.Option(content, "ItemDelete.Quality") ?? 3,
            MinItemLevel: WorldServerConfig.Option(content, "ItemDelete.ItemLevel") ?? 80,
            KeepDays: WorldServerConfig.Option(content, "ItemDelete.KeepDays") ?? 0,
            Source: path);
    }

    // ------------------------------------------------------------------ écritures

    /// <summary>`.additem <joueur> <id> <n>` — le joueur doit être connecté.</summary>
    public Task<GmCommandResult> AddAsync(string character, int entry, int count, string itemName,
                                          CancellationToken ct = default)
        => Run($"additem {character.Trim()} {entry} {count}",
               $"{count} × {itemName} ({entry}) ajouté(s) à {character}", ct);

    /// <summary>`.additem <joueur> <id> -<n>` — connecté ou non, sur toutes les poches.</summary>
    public Task<GmCommandResult> RemoveAsync(string character, int entry, int count, string itemName,
                                             CancellationToken ct = default)
        => Run($"additem {character.Trim()} {entry} -{count}",
               $"{count} × {itemName} ({entry}) retiré(s) à {character}", ct);

    /// <summary>`.item restore <id> <joueur>` — l'objet part par courrier, connecté ou non.</summary>
    public Task<GmCommandResult> RestoreAsync(string character, RecoveryItem item, CancellationToken ct = default)
        => Run($"item restore {item.Id} {character.Trim()}",
               $"{item.Count} × {item.Name} ({item.Entry}) restauré(s) pour {character}", ct);

    /// <summary>Envoi par courrier, pour un personnage déconnecté que `.additem` refuserait.</summary>
    public async Task<MailSendReport> MailAsync(string character, int entry, int count, string itemName,
                                                int quality, int displayId, CancellationToken ct = default)
    {
        var report = await _mail.SendAsync(character, "Objet restitué",
            "L'équipe du serveur vous fait parvenir cet objet.",
            [new MailAttachment(entry, count, itemName, quality, displayId)], 0, ct);

        if (report.Failed == 0)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm",
                $"{count} × {itemName} ({entry}) envoyé(s) par courrier à {character}", report.Message);
        return report;
    }

    private async Task<GmCommandResult> Run(string command, string description, CancellationToken ct)
    {
        var result = await _gm.ExecuteAsync(command, ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm", description, result.Output);
        return result;
    }
}
