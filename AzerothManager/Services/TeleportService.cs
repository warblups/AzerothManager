using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

/// <summary>
/// Bibliothèque de téléportation (§10).
///
/// Répartition imposée par le serveur, pas choisie :
/// — `.tele name &lt;joueur&gt; &lt;destination&gt;` est Console::Yes, donc utilisable par SOAP ;
/// — **`.tele add` est Console::No** : elle capture la position du maître de jeu qui
///   l'exécute, ce que SOAP ne peut pas fournir. L'ajout passe donc par SQL, avec des
///   coordonnées saisies — plus précis d'ailleurs qu'un relevé sur place.
///
/// Toute écriture dans world.game_tele exige un `.reload game_tele` pour être prise en
/// compte sans redémarrage : c'est la règle du §9, appliquée ici.
/// </summary>
public sealed class TeleportService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;
    private readonly GameClientService _client;

    public TeleportService(MySqlService mySql, GmCommandService gm, ServerContext context,
                           GameClientService client)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
        _client = client;
    }

    public async Task<IReadOnlyList<TeleportPoint>> SearchAsync(
        string? search = null, int? mapId = null, CancellationToken ct = default)
    {
        var where = new List<string>();
        var parameters = new List<MySqlParameter>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("name LIKE @like");
            parameters.Add(new MySqlParameter("@like", "%" + search.Trim() + "%"));
        }
        if (mapId is { } m)
        {
            where.Add("map = @map");
            parameters.Add(new MySqlParameter("@map", m));
        }

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var list = new List<TeleportPoint>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, map, position_x, position_y, position_z, orientation
            FROM game_tele
            {clause}
            ORDER BY name
            LIMIT 500
            """;
        foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapValue = rd.GetInt32(2);
            list.Add(new TeleportPoint(
                rd.GetInt32(0), rd.GetString(1), mapValue, _client.MapName(mapValue),
                rd.GetFloat(3), rd.GetFloat(4), rd.GetFloat(5), rd.GetFloat(6)));
        }
        return list;
    }

    /// <summary>Cartes présentes dans la bibliothèque, pour filtrer sans deviner.</summary>
    public async Task<IReadOnlyList<NamedMap>> MapsAsync(CancellationToken ct = default)
    {
        var list = new List<NamedMap>();
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT map, COUNT(*) FROM game_tele GROUP BY map ORDER BY COUNT(*) DESC";

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapId = rd.GetInt32(0);
            list.Add(new NamedMap(mapId, _client.MapName(mapId), rd.GetInt32(1)));
        }
        return list;
    }

    public Task<GmCommandResult> TeleportAsync(string character, string destination, CancellationToken ct = default)
        => _gm.ExecuteAsync($"tele name {character} {destination}", ct);

    public Task<GmCommandResult> ReloadAsync(CancellationToken ct = default)
        => _gm.ExecuteAsync("reload game_tele", ct);

    // ------------------------------------------------------------------ écriture SQL

    private void EnsureWritable()
    {
        var profile = _context.RequireActive();
        if (profile.ReadOnly)
            throw new InvalidOperationException($"Le profil « {profile.Name} » est en lecture seule.");
    }

    /// <summary>
    /// Crée un point. Le nom doit être unique et sans espace : la commande `.tele name`
    /// le prend comme un seul mot, un espace la casserait.
    /// </summary>
    public async Task<int> AddAsync(TeleportPoint point, CancellationToken ct = default)
    {
        EnsureWritable();
        if (point.Name.Any(char.IsWhiteSpace))
            throw new ArgumentException("Le nom ne doit pas contenir d'espace : la commande le lit comme un seul mot.");

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);

        await using (var check = cnx.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM game_tele WHERE name = @name";
            check.Parameters.AddWithValue("@name", point.Name);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) > 0)
                throw new InvalidOperationException($"Une destination nommée « {point.Name} » existe déjà.");
        }

        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_tele (id, name, map, position_x, position_y, position_z, orientation)
            VALUES ((SELECT COALESCE(MAX(id), 0) + 1 FROM game_tele t), @name, @map, @x, @y, @z, @o)
            """;
        Bind(cmd, point);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        Log(point, $"ajout de {point.Name}", rows);
        return rows;
    }

    public async Task<int> UpdateAsync(TeleportPoint point, CancellationToken ct = default)
    {
        EnsureWritable();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            UPDATE game_tele
            SET name = @name, map = @map, position_x = @x, position_y = @y,
                position_z = @z, orientation = @o
            WHERE id = @id
            """;
        Bind(cmd, point);
        cmd.Parameters.AddWithValue("@id", point.Id);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        Log(point, $"modification de {point.Name}", rows);
        return rows;
    }

    /// <summary>Suppression par commande : `.tele del` est Console::Yes et tient la table à jour.</summary>
    public Task<GmCommandResult> DeleteAsync(string name, CancellationToken ct = default)
        => _gm.ExecuteAsync($"tele del {name}", ct);

    private static void Bind(MySqlCommand cmd, TeleportPoint p)
    {
        cmd.Parameters.AddWithValue("@name", p.Name);
        cmd.Parameters.AddWithValue("@map", p.MapId);
        cmd.Parameters.AddWithValue("@x", p.X);
        cmd.Parameters.AddWithValue("@y", p.Y);
        cmd.Parameters.AddWithValue("@z", p.Z);
        cmd.Parameters.AddWithValue("@o", p.Orientation);
    }

    private void Log(TeleportPoint p, string what, int rows) =>
        LocalDatabase.LogHistory(_context.Active?.Id, "sql",
            $"game_tele : {what} (carte {p.MapId}, {p.X:0}, {p.Y:0}, {p.Z:0})",
            $"{rows} ligne(s)", rows);
}
