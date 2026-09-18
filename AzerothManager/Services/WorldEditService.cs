using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

/// <summary>
/// Édition du monde (§10), partie réellement faisable à distance : les spawns.
///
/// Le §10 décrit surtout des commandes de maître de jeu — `.npc add`, `.npc move`,
/// `.gobject add`, `.wp add`. **Toutes sont Console::No** : elles relèvent la position du
/// personnage qui les exécute et exigent donc une session en jeu, que SOAP ne fournit pas.
/// Ce module prend donc l'autre canal du §5 : le SQL, qui couvre la masse, la recherche et
/// la modification hors ligne — ce que la saisie en jeu ne sait pas faire.
///
/// Deux pièges vérifiés sur le serveur réel :
///
/// — **`creature.zoneId` et `areaId` ne sont pas fiables** : 149 887 des 155 085 lignes de
///   `creature` ont `zoneId = 0`, et 58 415 des 97 426 de `gameobject`. Ce sont des colonnes
///   de cache, remplies au besoin par le serveur. La zone est donc déduite de la position,
///   comme dans la carte des joueurs et la bibliothèque de téléportation.
///
/// — **il n'existe ni `.reload creature` ni `.reload gameobject`.** `cs_reload.cpp` recharge
///   les *modèles* (`creature_template`, `gameobject_template`…), pas les *spawns*. Une
///   position ou un temps de réapparition modifié ici ne prendra effet qu'au redémarrage du
///   worldserver. L'interface le dit franchement plutôt que de proposer un `.reload` inerte.
/// </summary>
public sealed class WorldEditService
{
    private readonly MySqlService _mySql;
    private readonly ServerContext _context;
    private readonly GameClientService _client;

    /// <summary>Langue des noms localisés dans `*_template_locale` (8 locales présentes).</summary>
    private const string Locale = "frFR";

    public WorldEditService(MySqlService mySql, ServerContext context, GameClientService client)
    {
        _mySql = mySql;
        _context = context;
        _client = client;
    }

    // ------------------------------------------------------------------ lecture

    public async Task<SpawnPage> SearchAsync(SpawnFilter filter, CancellationToken ct = default)
    {
        var creature = filter.Kind == SpawnKind.Creature;
        var table = creature ? "creature" : "gameobject";
        var template = creature ? "creature_template" : "gameobject_template";
        var locale = creature ? "creature_template_locale" : "gameobject_template_locale";
        // La colonne du nom localisé diffère : `Name` pour les créatures, `name` pour les
        // objets. MySQL ignore la casse des identifiants, mais autant écrire le vrai nom.
        var localeName = creature ? "l.Name" : "l.name";

        var where = new List<string>();
        var parameters = new List<MySqlParameter>();

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var text = filter.Text.Trim();
            // Un nombre est cherché comme identifiant de modèle *ou* comme guid : c'est ce
            // qu'on tape quand on vient d'un log ou d'un `.npc info`.
            if (int.TryParse(text, out var number))
            {
                where.Add($"(s.id = @num OR s.guid = @num OR t.name LIKE @like OR {localeName} LIKE @like)");
                parameters.Add(new MySqlParameter("@num", number));
            }
            else
            {
                where.Add($"(t.name LIKE @like OR {localeName} LIKE @like)");
            }
            parameters.Add(new MySqlParameter("@like", "%" + text + "%"));
        }

        if (filter.MapId is { } map)
        {
            where.Add("s.map = @map");
            parameters.Add(new MySqlParameter("@map", map));
        }

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var from = $"""
            FROM {table} s
            JOIN {template} t ON t.entry = s.id
            LEFT JOIN {locale} l ON l.entry = s.id AND l.locale = @locale
            {clause}
            """;

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);

        // Comptage séparé : 155 085 lignes dans `creature`, on ne ramène jamais tout (§ densité).
        int total;
        await using (var count = cnx.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) " + from;
            Add(count, parameters);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }

        var list = new List<Spawn>();
        if (total > 0)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"""
                SELECT s.guid, s.id, t.name, COALESCE(NULLIF({localeName}, ''), t.name),
                       s.map, s.position_x, s.position_y, s.position_z, s.orientation,
                       s.spawntimesecs, COALESCE(s.Comment, '')
                {from}
                ORDER BY s.guid
                LIMIT @take OFFSET @skip
                """;
            Add(cmd, parameters);
            cmd.Parameters.AddWithValue("@take", filter.PageSize);
            cmd.Parameters.AddWithValue("@skip", filter.Page * filter.PageSize);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var mapId = rd.GetInt32(4);
                var x = rd.GetFloat(5);
                var y = rd.GetFloat(6);

                // zoneId en base vaut 0 dans la grande majorité des lignes : on déduit.
                var zone = _client.ZoneAt(mapId, x, y);

                list.Add(new Spawn(
                    Kind: filter.Kind,
                    Guid: rd.GetInt32(0),
                    Entry: rd.GetInt32(1),
                    Name: rd.GetString(3),
                    NameEnglish: rd.GetString(2),
                    MapId: mapId,
                    MapName: _client.MapName(mapId),
                    ZoneId: zone?.AreaId ?? 0,
                    ZoneName: zone is null ? "" : _client.AreaName(zone.AreaId),
                    X: x, Y: y, Z: rd.GetFloat(7),
                    Orientation: rd.GetFloat(8),
                    SpawnTimeSecs: rd.GetInt32(9),
                    Comment: rd.GetString(10)));
            }
        }

        var pageCount = Math.Max(1, (total + filter.PageSize - 1) / filter.PageSize);
        return new SpawnPage(list, total, pageCount);
    }

    /// <summary>Cartes où le type de spawn est présent, pour filtrer sans deviner.</summary>
    public async Task<IReadOnlyList<NamedMap>> MapsAsync(SpawnKind kind, CancellationToken ct = default)
    {
        var table = kind == SpawnKind.Creature ? "creature" : "gameobject";
        var list = new List<NamedMap>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT map, COUNT(*) FROM {table} GROUP BY map ORDER BY COUNT(*) DESC";

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapId = rd.GetInt32(0);
            list.Add(new NamedMap(mapId, _client.MapName(mapId), rd.GetInt32(1)));
        }
        return list;
    }

    // ------------------------------------------------------------------ écriture

    private void EnsureWritable()
    {
        var profile = _context.RequireActive();
        if (profile.ReadOnly)
            throw new InvalidOperationException($"Le profil « {profile.Name} » est en lecture seule.");
    }

    /// <summary>
    /// Modifie position, orientation, temps de réapparition et commentaire d'un spawn.
    /// Le guid et l'identifiant de modèle ne sont pas modifiables : changer l'un revient à
    /// créer un autre spawn, changer l'autre à en faire une créature différente.
    /// </summary>
    public async Task<int> UpdateAsync(Spawn spawn, CancellationToken ct = default)
    {
        EnsureWritable();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {spawn.Table}
            SET position_x = @x, position_y = @y, position_z = @z, orientation = @o,
                spawntimesecs = @spawntime, Comment = @comment
            WHERE guid = @guid
            """;
        cmd.Parameters.AddWithValue("@x", spawn.X);
        cmd.Parameters.AddWithValue("@y", spawn.Y);
        cmd.Parameters.AddWithValue("@z", spawn.Z);
        cmd.Parameters.AddWithValue("@o", spawn.Orientation);
        cmd.Parameters.AddWithValue("@spawntime", spawn.SpawnTimeSecs);
        cmd.Parameters.AddWithValue("@comment", spawn.Comment);
        cmd.Parameters.AddWithValue("@guid", spawn.Guid);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        Log(spawn, "modification", rows);
        return rows;
    }

    /// <summary>
    /// Supprime le spawn et ses lignes satellites. `.npc delete` serait plus propre, mais
    /// elle est Console::No : elle agit sur la créature sélectionnée en jeu.
    ///
    /// Les tables dépendantes sont nettoyées ici parce que le serveur ne le fera pas :
    /// une ligne d'addon ou de waypoint orpheline provoque une erreur au chargement.
    /// </summary>
    public async Task<int> DeleteAsync(Spawn spawn, CancellationToken ct = default)
    {
        EnsureWritable();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);

        // Les colonnes ne s'appellent pas toutes `guid` — vérifié sur le serveur réel.
        // `creature_formations` référence le spawn comme meneur *ou* comme membre.
        //
        // `linked_respawn` est le cas délicat : ses deux colonnes portent un guid de
        // créature **ou** de gameobject selon `linkType` (0 créature→créature,
        // 1 créature→objet, 2 objet→objet, 3 objet→créature). Les deux espaces de guid
        // étant indépendants, filtrer sans le `linkType` supprimerait le lien d'une
        // créature qui porte le même numéro.
        var satellites = spawn.Kind == SpawnKind.Creature
            ? new (string Table, string Where)[]
              {
                  ("creature_addon", "guid = @guid"),
                  ("creature_formations", "leaderGUID = @guid OR memberGUID = @guid"),
                  ("linked_respawn", "(guid = @guid AND linkType IN (0, 1)) OR (linkedGuid = @guid AND linkType IN (0, 3))"),
                  ("game_event_creature", "guid = @guid"),
                  ("pool_creature", "guid = @guid"),
              }
            : [
                  ("gameobject_addon", "guid = @guid"),
                  ("linked_respawn", "(guid = @guid AND linkType IN (2, 3)) OR (linkedGuid = @guid AND linkType IN (1, 2))"),
                  ("game_event_gameobject", "guid = @guid"),
                  ("pool_gameobject", "guid = @guid"),
              ];

        var rows = 0;
        try
        {
            foreach (var (t, filter) in satellites)
            {
                // Une table peut manquer selon les modules installés : on l'ignore.
                if (!await ExistsAsync(cnx, tx, t, ct)) continue;

                await using var del = cnx.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {t} WHERE {filter}";
                del.Parameters.AddWithValue("@guid", spawn.Guid);
                rows += await del.ExecuteNonQueryAsync(ct);
            }

            await using (var main = cnx.CreateCommand())
            {
                main.Transaction = tx;
                main.CommandText = $"DELETE FROM {spawn.Table} WHERE guid = @guid";
                main.Parameters.AddWithValue("@guid", spawn.Guid);
                rows += await main.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        Log(spawn, "suppression", rows);
        return rows;
    }

    private static async Task<bool> ExistsAsync(MySqlConnection cnx, MySqlTransaction tx,
                                                string table, CancellationToken ct)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = @name
            """;
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static void Add(MySqlCommand cmd, IEnumerable<MySqlParameter> parameters)
    {
        cmd.Parameters.AddWithValue("@locale", Locale);
        foreach (var p in parameters)
            cmd.Parameters.AddWithValue(p.ParameterName, p.Value);
    }

    private void Log(Spawn spawn, string what, int rows) =>
        LocalDatabase.LogHistory(_context.Active?.Id, "sql",
            $"{spawn.Table} : {what} du guid {spawn.Guid} ({spawn.Name}, modèle {spawn.Entry})",
            $"{rows} ligne(s)", rows);
}
