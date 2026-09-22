using System.Text.RegularExpressions;
using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Événements de jeu (§9) : `world.game_event` et les commandes `.event`.
///
/// Les quatre commandes — `activelist`, `start`, `stop`, `info` — sont `Console::Yes`.
///
/// **Deux mécanismes distincts, à ne pas confondre**, et c'est tout l'intérêt du module :
///
/// — `.event start` et `.event stop` agissent **en mémoire seulement**. `GameEventMgr`
///   modifie son `_gameEvent[id]` et n'écrit rien dans `world.game_event` : au prochain
///   redémarrage du worldserver, la planification reprend la main. C'est l'outil de
///   l'animation du jour.
///
/// — Modifier la planification en base est **persistant mais inerte à chaud** : il n'existe
///   pas de `.reload game_event` (`cs_reload.cpp` ne recharge que `game_event_npc_vendor`).
///   L'effet n'arrive qu'au redémarrage — même situation que les spawns de l'édition du
///   monde, et la règle « écriture dans `world` → `.reload` » du §9 ne s'applique donc pas.
///
/// L'état actif ne se recalcule pas depuis la base : ce serait réimplémenter
/// `GameEventMgr::IsActiveEvent` et se tromper dès qu'un événement a été lancé à la main.
/// Il est demandé au serveur.
/// </summary>
public sealed class GameEventService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;

    public GameEventService(MySqlService mySql, GmCommandService gm, ServerContext context)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
    }

    // ------------------------------------------------------------------ lecture

    /// <summary>
    /// Identifiants des événements que le serveur considère actifs, extraits de la sortie de
    /// `.event activelist`. Chaque ligne commence par l'identifiant, suivi d'un tiret :
    /// « 24 - Brewfest  [active] ».
    /// </summary>
    internal static IReadOnlySet<int> ParseActiveIds(string output)
    {
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(output)) return ids;

        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*\[?(\d+)\]?\s*[-–]");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }

    public async Task<IReadOnlySet<int>> ActiveIdsAsync(CancellationToken ct = default)
    {
        var result = await _gm.ExecuteAsync("event activelist", ct);
        return result.Success ? ParseActiveIds(result.Output) : new HashSet<int>();
    }

    /// <summary>
    /// Les 182 événements de la base, enrichis de leur état vivant et du nombre de spawns
    /// qu'ils ajoutent ou retirent.
    ///
    /// Les décomptes de spawns viennent de deux agrégats séparés plutôt que de sous-requêtes
    /// corrélées : `game_event_creature` compte 11 460 lignes et `game_event_gameobject`
    /// 32 363 sur le serveur de référence, et une sous-requête par événement les relirait
    /// 182 fois.
    /// </summary>
    public async Task<IReadOnlyList<GameEventInfo>> ListAsync(string? text = null,
                                                              CancellationToken ct = default)
    {
        var active = await ActiveIdsAsync(ct);
        var added = new Dictionary<int, int>();
        var removed = new Dictionary<int, int>();

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);

        // Un eventEntry négatif désigne un spawn retiré pendant l'événement, pas ajouté.
        foreach (var table in new[] { "game_event_creature", "game_event_gameobject" })
        {
            if (!await ExistsAsync(cnx, table, ct)) continue;

            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"SELECT eventEntry, COUNT(*) FROM `{table}` GROUP BY eventEntry";

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var entry = rd.GetInt32(0);
                var count = rd.GetInt32(1);
                var target = entry < 0 ? removed : added;
                var id = Math.Abs(entry);
                target[id] = target.GetValueOrDefault(id) + count;
            }
        }

        var list = new List<GameEventInfo>();
        var filter = string.IsNullOrWhiteSpace(text) ? "" : "WHERE description LIKE @like OR eventEntry = @id";

        await using (var cmd = cnx.CreateCommand())
        {
            // start_time et end_time sont des colonnes TIMESTAMP : la conversion est faite
            // par MySQL, une date nulle ou à zéro revenant en NULL.
            cmd.CommandText = $"""
                SELECT eventEntry, COALESCE(description, ''),
                       UNIX_TIMESTAMP(start_time), UNIX_TIMESTAMP(end_time),
                       occurence, length, holiday, holidayStage, world_event, announce
                FROM game_event
                {filter}
                ORDER BY eventEntry
                """;
            if (filter.Length > 0)
            {
                cmd.Parameters.AddWithValue("@like", "%" + text!.Trim() + "%");
                cmd.Parameters.AddWithValue("@id", int.TryParse(text!.Trim(), out var n) ? n : -1);
            }

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetInt32(0);
                list.Add(new GameEventInfo(
                    Id: id,
                    Description: rd.GetString(1),
                    Start: ToDate(rd, 2),
                    End: ToDate(rd, 3),
                    OccurrenceMinutes: rd.GetInt64(4),
                    LengthMinutes: rd.GetInt64(5),
                    HolidayId: rd.GetInt32(6),
                    HolidayStage: rd.GetInt32(7),
                    WorldEvent: rd.GetInt32(8) != 0,
                    Announce: rd.GetInt32(9),
                    SpawnsAdded: added.GetValueOrDefault(id),
                    SpawnsRemoved: removed.GetValueOrDefault(id),
                    IsActive: active.Contains(id)));
            }
        }

        return list;
    }

    private static DateTime? ToDate(MySqlConnector.MySqlDataReader rd, int index)
    {
        if (rd.IsDBNull(index)) return null;
        var seconds = rd.GetInt64(index);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime : null;
    }

    private static async Task<bool> ExistsAsync(MySqlConnector.MySqlConnection cnx, string table,
                                                CancellationToken ct)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = @name
            """;
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    // ------------------------------------------------------------------ actions immédiates

    /// <summary>
    /// `.event start <id>` — effet immédiat, **non persistant**. Le serveur refuse si
    /// l'événement est déjà actif ou si l'identifiant n'existe pas.
    /// </summary>
    public Task<GmCommandResult> StartAsync(int id, string name, CancellationToken ct = default)
        => Run($"event start {id}", $"Événement {id} « {name} » démarré", ct);

    public Task<GmCommandResult> StopAsync(int id, string name, CancellationToken ct = default)
        => Run($"event stop {id}", $"Événement {id} « {name} » arrêté", ct);

    public Task<GmCommandResult> InfoAsync(int id, CancellationToken ct = default)
        => _gm.ExecuteAsync($"event info {id}", ct);

    private async Task<GmCommandResult> Run(string command, string description, CancellationToken ct)
    {
        var result = await _gm.ExecuteAsync(command, ct);
        if (result.Success)
            LocalDatabase.LogHistory(_context.Active?.Id, "gm", description, result.Output);
        return result;
    }

    // ------------------------------------------------------------------ planification

    /// <summary>
    /// Écrit la planification d'un événement. Persistant, mais **sans effet avant le
    /// redémarrage du worldserver** : aucune commande ne recharge `game_event`.
    ///
    /// La durée ne peut pas dépasser la période, sinon l'événement ne s'arrêterait jamais —
    /// `IsActiveEvent` teste `(temps − début) % (période × MINUTE) &lt; durée × MINUTE`.
    ///
    /// **Sans effet du tout si `holidayStage` n'est pas nul** : `SetHolidayEventTime` écrase
    /// alors période et durée avec celles de `Holidays.dbc` à chaque chargement. L'écriture
    /// est autorisée — la base reste la base — mais l'interface prévient, faute de quoi on
    /// modifierait, redémarrerait, et constaterait que rien n'a changé.
    /// </summary>
    public async Task<int> UpdateScheduleAsync(int id, DateTime? start, DateTime? end,
                                               long occurrenceMinutes, long lengthMinutes,
                                               CancellationToken ct = default)
    {
        var profile = _context.RequireActive();
        if (profile.ReadOnly)
            throw new InvalidOperationException($"Le profil « {profile.Name} » est en lecture seule.");

        if (occurrenceMinutes <= 0 || lengthMinutes <= 0)
            throw new ArgumentException("La période et la durée doivent être strictement positives.");
        if (lengthMinutes > occurrenceMinutes)
            throw new ArgumentException(
                "La durée ne peut pas dépasser la période : l'événement ne s'arrêterait jamais.");

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            UPDATE game_event
            SET start_time = FROM_UNIXTIME(@start), end_time = FROM_UNIXTIME(@end),
                occurence = @occurrence, length = @length
            WHERE eventEntry = @id
            """;
        cmd.Parameters.AddWithValue("@start", ToUnix(start));
        cmd.Parameters.AddWithValue("@end", ToUnix(end));
        cmd.Parameters.AddWithValue("@occurrence", occurrenceMinutes);
        cmd.Parameters.AddWithValue("@length", lengthMinutes);
        cmd.Parameters.AddWithValue("@id", id);

        var rows = await cmd.ExecuteNonQueryAsync(ct);

        LocalDatabase.LogHistory(_context.Active?.Id, "sql",
            $"game_event {id} : planification modifiée (période {Duration.FromMinutes(occurrenceMinutes)}, " +
            $"durée {Duration.FromMinutes(lengthMinutes)})", $"{rows} ligne(s)", rows);

        return rows;
    }

    private static long ToUnix(DateTime? value) =>
        value is null ? 0 : new DateTimeOffset(value.Value.ToUniversalTime()).ToUnixTimeSeconds();
}
