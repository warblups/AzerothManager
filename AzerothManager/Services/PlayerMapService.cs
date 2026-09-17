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
    /// Effectifs par zone, tous continents confondus. La bulle est placée au centre de la
    /// zone lu dans WorldMapArea.dbc ; une zone sans rectangle reste dans la liste mais
    /// n'est pas projetée.
    /// </summary>
    public async Task<IReadOnlyList<ZonePopulation>> ZonePopulationAsync(
        bool includeOffline = false, CancellationToken ct = default)
    {
        var list = new List<ZonePopulation>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        // Les races 1, 3, 4, 7 et 11 sont l'Alliance ; les autres la Horde.
        cmd.CommandText = $"""
            SELECT zone, map,
                   SUM(race IN (1,3,4,7,11)) AS alliance,
                   SUM(race NOT IN (1,3,4,7,11)) AS horde
            FROM characters
            WHERE deleteDate IS NULL {(includeOffline ? "" : "AND online = 1")}
            GROUP BY zone, map
            ORDER BY (alliance + horde) DESC
            """;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var zoneId = rd.GetInt32(0);
            var mapId = rd.GetInt32(1);
            var alliance = rd.GetInt32(2);
            var horde = rd.GetInt32(3);

            double x = 0, y = 0;
            var center = _client.ZoneCenter(zoneId);
            if (center is { } c && Continent.ForMap(c.MapId) is { } continent)
            {
                (x, y) = continent.Project(c.X, c.Y);
                mapId = c.MapId;
            }

            list.Add(new ZonePopulation(zoneId, _client.AreaName(zoneId), mapId, alliance, horde, x, y));
        }
        return list;
    }
}
