using System.IO;
using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Carte des joueurs. Les positions viennent de la base, les fonds de carte des archives
/// du client extraites par tools/extract_worldmaps.py.
///
/// Les images restent sur le poste : ce sont des ressources du jeu, elles n'ont rien à
/// faire dans le dépôt. Le script les régénère à la demande.
/// </summary>
public sealed class PlayerMapService
{
    private readonly MySqlService _mySql;
    private readonly GameClientService _client;

    public PlayerMapService(MySqlService mySql, GameClientService client)
    {
        _mySql = mySql;
        _client = client;
    }

    public static string MapsFolder => Path.Combine(LocalDatabase.FolderPath, "maps");

    public static string MapFile(Continent continent) =>
        Path.Combine(MapsFolder, continent.Key + ".png");

    public static bool HasMap(Continent continent) => File.Exists(MapFile(continent));

    public static bool HasAnyMap() => Continent.All.Any(HasMap);

    /// <summary>
    /// Joueurs connectés, projetés sur leur continent. Les personnages situés sur une carte
    /// d'instance ne sont pas projetables : ils sont retournés à part.
    /// </summary>
    public async Task<(IReadOnlyList<PlayerPosition> Placed, IReadOnlyList<PlayerPosition> Elsewhere)>
        OnlinePlayersAsync(bool includeOffline = false, CancellationToken ct = default)
    {
        var placed = new List<PlayerPosition>();
        var elsewhere = new List<PlayerPosition>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT guid, name, level, race, class, map, zone, position_x, position_y
            FROM characters
            WHERE deleteDate IS NULL {(includeOffline ? "" : "AND online = 1")}
            ORDER BY name
            """;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapId = rd.GetInt32(5);
            var zoneId = rd.GetInt32(6);
            var worldX = rd.GetFloat(7);
            var worldY = rd.GetFloat(8);

            var continent = Continent.ForMap(mapId);
            var (x, y) = continent?.Project(worldX, worldY) ?? (0, 0);

            var player = new PlayerPosition(
                rd.GetInt32(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4),
                mapId, zoneId, _client.AreaName(zoneId), worldX, worldY, x, y);

            if (continent is null) elsewhere.Add(player);
            else placed.Add(player);
        }

        return (placed, elsewhere);
    }

    /// <summary>
    /// Effectifs par zone, déduits de la position et non de characters.zone.
    ///
    /// Cette colonne n'est écrite qu'à la déconnexion : sur un serveur peuplé de bots elle
    /// vaut zéro pour la quasi-totalité des personnages, et un regroupement dessus produit
    /// un seul tas inutile. La position, elle, est toujours juste : on cherche donc la zone
    /// qui la contient dans les rectangles de WorldMapArea.
    /// </summary>
    public async Task<IReadOnlyList<ZonePopulation>> ZonePopulationAsync(
        bool includeOffline = false, CancellationToken ct = default)
    {
        var (placed, elsewhere) = await OnlinePlayersAsync(includeOffline, ct);

        var buckets = new Dictionary<int, Bucket>();
        var unknownAlliance = 0;
        var unknownHorde = 0;

        foreach (var player in placed.Concat(elsewhere))
        {
            var zone = _client.ZoneAt(player.MapId, player.WorldX, player.WorldY);
            if (zone is null)
            {
                if (player.IsAlliance) unknownAlliance++; else unknownHorde++;
                continue;
            }

            if (!buckets.TryGetValue(zone.AreaId, out var bucket))
            {
                var continent = Continent.ForMap(zone.MapId);
                var (x, y) = continent?.Project(zone.X, zone.Y) ?? (0, 0);
                bucket = new Bucket(_client.AreaName(zone.AreaId), zone.MapId, x, y);
                buckets[zone.AreaId] = bucket;
            }

            if (player.IsAlliance) bucket.Alliance++; else bucket.Horde++;
        }

        var list = buckets
            .Select(kv => new ZonePopulation(kv.Key, kv.Value.Name, kv.Value.MapId,
                                             kv.Value.Alliance, kv.Value.Horde, kv.Value.X, kv.Value.Y))
            .OrderByDescending(z => z.Total)
            .ToList();

        // Les positions hors de tout rectangle connu — donjons, instances — sont annoncées
        // plutôt que réparties au hasard.
        if (unknownAlliance + unknownHorde > 0)
            list.Add(new ZonePopulation(0, "Hors zone cartographiée", -1,
                                        unknownAlliance, unknownHorde, 0, 0));

        return list;
    }

    /// <summary>Accumulateur par zone, mutable pour éviter de recopier un tuple à chaque joueur.</summary>
    private sealed class Bucket(string name, int mapId, double x, double y)
    {
        public string Name { get; } = name;
        public int MapId { get; } = mapId;
        public double X { get; } = x;
        public double Y { get; } = y;
        public int Alliance { get; set; }
        public int Horde { get; set; }
    }
}
