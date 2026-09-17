using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

public sealed record AccountFilter(
    string? Text = null,
    bool OnlyOnline = false,
    bool OnlyBanned = false,
    bool OnlyGm = false,
    int Page = 0,
    int PageSize = 100);

public sealed record AccountSearchResult(IReadOnlyList<AccountSummary> Accounts, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// Module Comptes (§9). Hybride par nécessité, pas par commodité :
///
/// — la recherche, les listes, l'historique et les sanctions se lisent en SQL ;
/// — la création d'un compte et le changement de mot de passe passent obligatoirement
///   par la commande GM, AccountMgr calculant un couple SRP6 salt/verifier qu'un INSERT
///   direct devrait réimplémenter (§9).
/// </summary>
public sealed class AccountService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;

    public AccountService(MySqlService mySql, GmCommandService gm, ServerContext context)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
    }

    /// <summary>
    /// Nom de la base des personnages, échappé pour être injecté comme identifiant :
    /// une jointure entre bases est possible, le serveur MySQL étant le même.
    /// </summary>
    private string CharactersDb => "`" + _context.RequireActive().CharactersDatabase.Replace("`", "") + "`";

    // ------------------------------------------------------------------ lecture

    public async Task<AccountSearchResult> SearchAsync(AccountFilter filter, CancellationToken ct = default)
    {
        var where = new List<string>();
        var parameters = new List<MySqlParameter>();

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            if (int.TryParse(filter.Text.Trim(), out var id))
            {
                where.Add("(a.id = @id OR a.username LIKE @like OR a.email LIKE @like OR a.last_ip LIKE @like)");
                parameters.Add(new MySqlParameter("@id", id));
            }
            else
            {
                where.Add("(a.username LIKE @like OR a.email LIKE @like OR a.last_ip LIKE @like)");
            }
            parameters.Add(new MySqlParameter("@like", "%" + filter.Text.Trim() + "%"));
        }

        if (filter.OnlyOnline) where.Add("a.online = 1");
        if (filter.OnlyBanned) where.Add("ab.active = 1");
        if (filter.OnlyGm) where.Add("aa.gmlevel > 0");

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var joins = """
            LEFT JOIN account_access aa ON aa.id = a.id
            LEFT JOIN account_banned ab ON ab.id = a.id AND ab.active = 1
            """;

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Auth, ct);

        int total;
        await using (var countCmd = cnx.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(DISTINCT a.id) FROM account a {joins} {clause}";
            foreach (var p in parameters) countCmd.Parameters.Add(Clone(p));
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var accounts = new List<AccountSummary>();
        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT a.id, a.username, a.email, a.expansion, a.last_ip, a.last_login, a.joindate,
                       a.online, a.locked, a.mutetime,
                       aa.gmlevel, aa.RealmID,
                       ab.active, ab.banreason, ab.bandate, ab.unbandate,
                       (SELECT COUNT(*) FROM {CharactersDb}.characters c WHERE c.account = a.id) AS perso
                FROM account a
                {joins}
                {clause}
                ORDER BY a.id
                LIMIT @take OFFSET @skip
                """;
            foreach (var p in parameters) cmd.Parameters.Add(Clone(p));
            cmd.Parameters.AddWithValue("@take", filter.PageSize);
            cmd.Parameters.AddWithValue("@skip", filter.Page * filter.PageSize);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                accounts.Add(new AccountSummary(
                    Id: rd.GetInt32(0),
                    Username: rd.GetString(1),
                    Email: rd.IsDBNull(2) ? "" : rd.GetString(2),
                    Expansion: rd.GetInt32(3),
                    LastIp: rd.IsDBNull(4) ? "" : rd.GetString(4),
                    LastLogin: rd.IsDBNull(5) ? null : rd.GetDateTime(5),
                    JoinDate: rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                    Online: rd.GetInt32(7) != 0,
                    Locked: rd.GetInt32(8) != 0,
                    MuteTime: rd.GetInt64(9),
                    GmLevel: rd.IsDBNull(10) ? null : rd.GetInt32(10),
                    RealmId: rd.IsDBNull(11) ? null : rd.GetInt32(11),
                    CharacterCount: rd.GetInt32(16),
                    Banned: !rd.IsDBNull(12) && rd.GetInt32(12) != 0,
                    BanReason: rd.IsDBNull(13) ? "" : rd.GetString(13),
                    BanDate: rd.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(14)).LocalDateTime,
                    UnbanDate: rd.IsDBNull(15) ? null : DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(15)).LocalDateTime));
            }
        }

        return new AccountSearchResult(accounts, total, filter.Page, filter.PageSize);
    }

    /// <summary>Personnages d'un compte, y compris hors ligne — la commande GM ne sait pas les voir.</summary>
    public async Task<IReadOnlyList<AccountCharacter>> CharactersAsync(int accountId, CancellationToken ct = default)
    {
        var list = new List<AccountCharacter>();
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT guid, name, level, race, class, money, online
            FROM characters
            WHERE account = @id AND deleteDate IS NULL
            ORDER BY level DESC, name
            """;
        cmd.Parameters.AddWithValue("@id", accountId);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new AccountCharacter(
                rd.GetInt32(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3),
                rd.GetInt32(4), rd.GetInt64(5), rd.GetInt32(6) != 0));
        }
        return list;
    }

    // ------------------------------------------------------------------ écriture, par commande GM

    public Task<GmCommandResult> CreateAsync(string username, string password, string email = "",
                                             CancellationToken ct = default)
    {
        var command = $"account create {username} {password}";
        if (!string.IsNullOrWhiteSpace(email)) command += " " + email;
        return _gm.ExecuteAsync(command, ct);
    }

    /// <summary>La commande attend le mot de passe suivi de sa confirmation.</summary>
    public Task<GmCommandResult> SetPasswordAsync(string username, string password, CancellationToken ct = default)
        => _gm.ExecuteAsync($"account set password {username} {password} {password}", ct);

    /// <summary>Le niveau GM s'applique à un royaume ; -1 vaut pour tous.</summary>
    public Task<GmCommandResult> SetGmLevelAsync(string username, int level, int realmId = -1,
                                                 CancellationToken ct = default)
        => _gm.ExecuteAsync($"account set gmlevel {username} {level} {realmId}", ct);

    public Task<GmCommandResult> SetExpansionAsync(string username, int expansion, CancellationToken ct = default)
        => _gm.ExecuteAsync($"account set addon {username} {expansion}", ct);

    /// <summary>Durée au format d'AzerothCore : 10m, 2h, 1d… ou 0 pour un bannissement définitif.</summary>
    public Task<GmCommandResult> BanAsync(string username, string duration, string reason,
                                          CancellationToken ct = default)
        => _gm.ExecuteAsync($"ban account {username} {duration} {reason}", ct);

    public Task<GmCommandResult> UnbanAsync(string username, CancellationToken ct = default)
        => _gm.ExecuteAsync($"unban account {username}", ct);

    public Task<GmCommandResult> BanInfoAsync(string username, CancellationToken ct = default)
        => _gm.ExecuteAsync($"baninfo account {username}", ct);

    /// <summary>Le mute vise un personnage, pas un compte, même si l'effet est enregistré sur le compte.</summary>
    public Task<GmCommandResult> MuteAsync(string character, int minutes, string reason,
                                           CancellationToken ct = default)
        => _gm.ExecuteAsync($"mute {character} {minutes} {reason}", ct);

    public Task<GmCommandResult> UnmuteAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"unmute {character}", ct);

    public Task<GmCommandResult> KickAsync(string character, string reason, CancellationToken ct = default)
        => _gm.ExecuteAsync($"kick {character} {reason}".TrimEnd(), ct);

    private static MySqlParameter Clone(MySqlParameter p) => new(p.ParameterName, p.Value);
}
