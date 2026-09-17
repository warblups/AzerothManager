using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

public sealed record CharacterFilter(
    string? Text = null,
    bool OnlyOnline = false,
    int? MinLevel = null,
    int? MaxLevel = null,
    int Page = 0,
    int PageSize = 200);

public sealed record CharacterSearchResult(
    IReadOnlyList<CharacterRow> Characters, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// Module Personnages (§8 et §11). Répartition des canaux, conforme au §5 :
///
/// — recherche et listes en SQL, y compris pour les personnages hors ligne ;
/// — niveau, drapeaux at_login et téléportation par commande GM, qui acceptent un nom ;
/// — l'or fait exception : « .modify money » n'accepte pas de nom et agit sur la cible
///   sélectionnée en jeu. Pour un personnage hors ligne, seule l'écriture SQL reste, et
///   elle ne vaut que hors ligne : le serveur écraserait la valeur à la sauvegarde d'un
///   joueur connecté.
/// </summary>
public sealed class CharacterService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;
    private readonly GameClientService _client;

    public CharacterService(MySqlService mySql, GmCommandService gm, ServerContext context,
                            GameClientService client)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
        _client = client;
    }

    private string AuthDb => "`" + _context.RequireActive().AuthDatabase.Replace("`", "") + "`";
    private string WorldDb => "`" + _context.RequireActive().WorldDatabase.Replace("`", "") + "`";

    // ------------------------------------------------------------------ lecture

    public async Task<CharacterSearchResult> SearchAsync(CharacterFilter filter, CancellationToken ct = default)
    {
        var where = new List<string> { "c.deleteDate IS NULL" };
        var parameters = new List<MySqlParameter>();

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            where.Add("(c.name LIKE @like OR a.username LIKE @like)");
            parameters.Add(new MySqlParameter("@like", "%" + filter.Text.Trim() + "%"));
        }
        if (filter.OnlyOnline) where.Add("c.online = 1");
        if (filter.MinLevel is { } min)
        {
            where.Add("c.level >= @minlvl");
            parameters.Add(new MySqlParameter("@minlvl", min));
        }
        if (filter.MaxLevel is { } max)
        {
            where.Add("c.level <= @maxlvl");
            parameters.Add(new MySqlParameter("@maxlvl", max));
        }

        var clause = "WHERE " + string.Join(" AND ", where);
        var join = $"LEFT JOIN {AuthDb}.account a ON a.id = c.account";

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);

        int total;
        await using (var countCmd = cnx.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM characters c {join} {clause}";
            foreach (var p in parameters) countCmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var list = new List<CharacterRow>();
        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.guid, c.name, c.account, COALESCE(a.username, ''), c.level, c.race, c.class,
                       c.gender, c.money, c.map, c.zone, c.online, c.at_login, c.totaltime, c.logout_time
                FROM characters c
                {join}
                {clause}
                ORDER BY c.level DESC, c.name
                LIMIT @take OFFSET @skip
                """;
            foreach (var p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
            cmd.Parameters.AddWithValue("@take", filter.PageSize);
            cmd.Parameters.AddWithValue("@skip", filter.Page * filter.PageSize);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var mapId = rd.GetInt32(9);
                var zoneId = rd.GetInt32(10);
                var logout = rd.GetInt64(14);

                list.Add(new CharacterRow(
                    Guid: rd.GetInt32(0),
                    Name: rd.GetString(1),
                    AccountId: rd.GetInt32(2),
                    AccountName: rd.GetString(3),
                    Level: rd.GetInt32(4),
                    Race: rd.GetInt32(5),
                    Class: rd.GetInt32(6),
                    Gender: rd.GetInt32(7),
                    Money: rd.GetInt64(8),
                    MapId: mapId,
                    ZoneId: zoneId,
                    ZoneName: _client.AreaName(zoneId),
                    Online: rd.GetInt32(11) != 0,
                    AtLogin: rd.GetInt32(12),
                    TotalTimeSeconds: rd.GetInt32(13),
                    LogoutTime: logout > 0 ? DateTimeOffset.FromUnixTimeSeconds(logout).LocalDateTime : null));
            }
        }

        return new CharacterSearchResult(list, total, filter.Page, filter.PageSize);
    }

    /// <summary>Destinations de téléportation du serveur (1 989 sur le serveur de référence).</summary>
    public async Task<IReadOnlyList<TeleportPoint>> DestinationsAsync(
        string? search = null, CancellationToken ct = default)
    {
        var list = new List<TeleportPoint>();
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.World, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(search)
            ? "SELECT id, name, map, position_x, position_y, position_z FROM game_tele ORDER BY name LIMIT 300"
            : "SELECT id, name, map, position_x, position_y, position_z FROM game_tele " +
              "WHERE name LIKE @like ORDER BY name LIMIT 300";
        if (!string.IsNullOrWhiteSpace(search))
            cmd.Parameters.AddWithValue("@like", "%" + search.Trim() + "%");

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapId = rd.GetInt32(2);
            list.Add(new TeleportPoint(rd.GetInt32(0), rd.GetString(1), mapId, _client.MapName(mapId),
                rd.GetFloat(3), rd.GetFloat(4), rd.GetFloat(5), 0));
        }
        return list;
    }

    // ------------------------------------------------------------------ écriture par commande GM

    public Task<GmCommandResult> SetLevelAsync(string character, int level, CancellationToken ct = default)
        => _gm.ExecuteAsync($"character level {character} {level}", ct);

    public Task<GmCommandResult> RenameAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"character rename {character}", ct);

    public Task<GmCommandResult> CustomizeAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"character customize {character}", ct);

    public Task<GmCommandResult> ChangeFactionAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"character changefaction {character}", ct);

    public Task<GmCommandResult> ChangeRaceAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"character changerace {character}", ct);

    /// <summary>Téléporte un personnage nommé vers une destination de game_tele.</summary>
    public Task<GmCommandResult> TeleportAsync(string character, string destination, CancellationToken ct = default)
        => _gm.ExecuteAsync($"tele name {character} {destination}", ct);

    /// <summary>Ramène le personnage à son point de liaison.</summary>
    public Task<GmCommandResult> SendHomeAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"tele name {character} $home", ct);

    /// <summary>Annule la dernière téléportation : le filet de sécurité du §10.</summary>
    public Task<GmCommandResult> RecallAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"recall {character}", ct);

    public Task<GmCommandResult> SummonAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"summon {character}", ct);

    public Task<GmCommandResult> AppearAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"appear {character}", ct);

    public Task<GmCommandResult> ReviveAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"revive {character}", ct);

    public Task<GmCommandResult> KickAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"kick {character}", ct);

    // ------------------------------------------------------------------ écriture SQL

    /// <summary>
    /// Fixe l'or d'un personnage hors ligne. Refusé pour un joueur connecté : le serveur
    /// garde son état en mémoire et écraserait l'écriture à la prochaine sauvegarde.
    /// Pour un joueur connecté, il faut le cibler en jeu et utiliser « .modify money ».
    /// </summary>
    public async Task<int> SetMoneyAsync(CharacterRow character, long copper, CancellationToken ct = default)
    {
        if (character.Online)
            throw new InvalidOperationException(
                $"{character.Name} est connecté : le serveur écraserait l'écriture. " +
                "Ciblez-le en jeu et utilisez « .modify money », ou attendez sa déconnexion.");

        if (copper < 0) throw new ArgumentOutOfRangeException(nameof(copper));

        var profile = _context.RequireActive();
        if (profile.ReadOnly)
            throw new InvalidOperationException($"Le profil « {profile.Name} » est en lecture seule.");

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = "UPDATE characters SET money = @money WHERE guid = @guid";
        cmd.Parameters.AddWithValue("@money", copper);
        cmd.Parameters.AddWithValue("@guid", character.Guid);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        LocalDatabase.LogHistory(profile.Id, "sql",
            $"UPDATE characters SET money = {copper} WHERE guid = {character.Guid}",
            $"{rows} ligne(s) — {character.Name}", rows);
        return rows;
    }
}
