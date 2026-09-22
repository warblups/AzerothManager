using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Verrous d'instance (§9) : consultation et levée des liens de `character_instance`.
///
/// **Le canal est le SQL, faute de mieux.** Les deux commandes qui feraient le travail,
/// `.instance listbinds` et `.instance unbind`, sont **`Console::No`** : elles agissent sur
/// le joueur sélectionné ou sur la session du maître de jeu, que SOAP n'a pas. Même
/// situation qu'au §11, et même repli qu'à l'édition du monde. Seule `.instance stats`
/// est `Console::Yes`.
///
/// **Une levée en base ne prend effet qu'au redémarrage du worldserver**, et c'est plus fort
/// que la règle habituelle : `InstanceSaveMgr::LoadCharacterBinds` lit **toute** la table
/// `character_instance` au démarrage et garde les liens en mémoire pour la durée de vie du
/// serveur — pour tous les personnages, connectés ou non. Supprimer la ligne d'un joueur
/// déconnecté ne suffit donc pas, contrairement à ce qui vaut pour l'or d'un personnage.
/// L'écriture est bien persistante ; c'est son effet qui est différé.
///
/// Les lignes d'`instance` devenues orphelines ne sont pas supprimées ici : le serveur les
/// nettoie lui-même au démarrage suivant (`DELETE i.* FROM instance AS i LEFT JOIN
/// character_instance …`). Les effacer nous-mêmes risquerait d'emporter une instance encore
/// référencée.
/// </summary>
public sealed class InstanceLockService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;
    private readonly GameClientService _client;

    public InstanceLockService(MySqlService mySql, GmCommandService gm, ServerContext context,
                               GameClientService client)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
        _client = client;
    }

    private static DateTime? FromUnix(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime : null;

    // ------------------------------------------------------------------ lecture

    /// <summary>
    /// Verrous en cours. Le filtre porte sur le nom du personnage ou sur l'identifiant de
    /// carte, les deux façons d'arriver ici : « untel est bloqué », ou « il faut rouvrir
    /// Naxxramas ».
    /// </summary>
    public async Task<IReadOnlyList<InstanceLock>> SearchAsync(string? text, bool onlyPermanent = false,
                                                               int limit = 500,
                                                               CancellationToken ct = default)
    {
        var list = new List<InstanceLock>();
        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(text))
            where.Add("(c.name LIKE @like OR i.map = @map)");
        if (onlyPermanent)
            where.Add("ci.permanent = 1");

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT ci.guid, COALESCE(c.name, ''), COALESCE(c.level, 0), ci.instance,
                   i.map, i.difficulty, i.resettime, ci.permanent, ci.extended,
                   i.completedEncounters
            FROM character_instance ci
            JOIN instance i ON i.id = ci.instance
            LEFT JOIN characters c ON c.guid = ci.guid
            {clause}
            ORDER BY c.name, i.map, i.difficulty
            LIMIT @limit
            """;
        if (!string.IsNullOrWhiteSpace(text))
        {
            cmd.Parameters.AddWithValue("@like", "%" + text.Trim() + "%");
            cmd.Parameters.AddWithValue("@map", int.TryParse(text.Trim(), out var m) ? m : -1);
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapId = rd.GetInt32(4);
            list.Add(new InstanceLock(
                CharacterGuid: rd.GetInt32(0),
                CharacterName: rd.GetString(1),
                Level: rd.GetInt32(2),
                InstanceId: rd.GetInt32(3),
                MapId: mapId,
                MapName: _client.MapName(mapId),
                Difficulty: rd.GetInt32(5),
                ResetAt: FromUnix(rd.GetInt64(6)),
                Permanent: rd.GetInt32(7) != 0,
                Extended: rd.GetInt32(8) != 0,
                CompletedEncounters: rd.GetInt64(9)));
        }
        return list;
    }

    /// <summary>
    /// Réinitialisations programmées, par carte et difficulté. C'est la réponse à « quand
    /// ce raid rouvre-t-il ? », qui évite souvent d'avoir à lever quoi que ce soit.
    /// </summary>
    public async Task<IReadOnlyList<InstanceResetSchedule>> ResetSchedulesAsync(CancellationToken ct = default)
    {
        var list = new List<InstanceResetSchedule>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT mapid, difficulty, resettime FROM instance_reset ORDER BY resettime, mapid";

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapId = rd.GetInt32(0);
            list.Add(new InstanceResetSchedule(mapId, _client.MapName(mapId),
                                               rd.GetInt32(1), FromUnix(rd.GetInt64(2))));
        }
        return list;
    }

    /// <summary>`.instance stats` — la seule commande du groupe qui soit `Console::Yes`.</summary>
    public Task<GmCommandResult> StatsAsync(CancellationToken ct = default)
        => _gm.ExecuteAsync("instance stats", ct);

    // ------------------------------------------------------------------ levée

    private void EnsureWritable()
    {
        var profile = _context.RequireActive();
        if (profile.ReadOnly)
            throw new InvalidOperationException($"Le profil « {profile.Name} » est en lecture seule.");
    }

    /// <summary>Lève un verrou précis.</summary>
    public Task<int> ReleaseAsync(InstanceLock loc, CancellationToken ct = default)
        => DeleteAsync("guid = @guid AND instance = @instance",
                       cmd =>
                       {
                           cmd.Parameters.AddWithValue("@guid", loc.CharacterGuid);
                           cmd.Parameters.AddWithValue("@instance", loc.InstanceId);
                       },
                       $"verrou {loc.MapText} (difficulté {loc.Difficulty}) de {loc.CharacterText}",
                       ct);

    /// <summary>
    /// Lève tous les verrous d'un personnage — le geste habituel quand un joueur est bloqué
    /// hors d'un raid par un lien devenu incohérent.
    /// </summary>
    public Task<int> ReleaseAllForCharacterAsync(int guid, string name, CancellationToken ct = default)
        => DeleteAsync("guid = @guid",
                       cmd => cmd.Parameters.AddWithValue("@guid", guid),
                       $"tous les verrous de {name}",
                       ct);

    /// <summary>
    /// Lève tous les verrous d'une carte, toutes difficultés et tous joueurs confondus :
    /// la réouverture d'un raid après un incident.
    /// </summary>
    public Task<int> ReleaseAllForMapAsync(int mapId, string mapName, CancellationToken ct = default)
        => DeleteAsync("instance IN (SELECT id FROM instance WHERE map = @map)",
                       cmd => cmd.Parameters.AddWithValue("@map", mapId),
                       $"tous les verrous de {mapName}",
                       ct);

    private async Task<int> DeleteAsync(string where, Action<MySqlConnector.MySqlCommand> bind,
                                        string description, CancellationToken ct)
    {
        EnsureWritable();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"DELETE FROM character_instance WHERE {where}";
        bind(cmd);

        var rows = await cmd.ExecuteNonQueryAsync(ct);

        LocalDatabase.LogHistory(_context.Active?.Id, "sql",
            $"Levée de {description}", $"{rows} ligne(s)", rows);

        return rows;
    }
}
