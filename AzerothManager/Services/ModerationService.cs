using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Modération (§9) : la vue d'ensemble des sanctions, que le module Comptes ne donne pas.
///
/// Celui-ci agit compte par compte ; celui-là répond à « qui est sanctionné en ce moment,
/// par qui, et pourquoi ». D'où la lecture consolidée des quatre tables, et les sanctions
/// par adresse IP, qui n'ont de sens qu'ici.
///
/// Les écritures passent par les commandes GM, qui tiennent les tables à jour et
/// déconnectent le joueur visé — un INSERT laisserait un banni connecté.
/// </summary>
public sealed class ModerationService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;

    public ModerationService(MySqlService mySql, GmCommandService gm, ServerContext context)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
    }

    private string AuthDb => "`" + _context.RequireActive().AuthDatabase.Replace("`", "") + "`";
    private string CharDb => "`" + _context.RequireActive().CharactersDatabase.Replace("`", "") + "`";

    // ------------------------------------------------------------------ lecture

    /// <summary>
    /// Sanctions des quatre tables, ramenées à une forme commune. Les dates sont des
    /// horodatages Unix, sauf dans account_muted où mutedate l'est aussi mais mutetime
    /// exprime une durée.
    /// </summary>
    public async Task<IReadOnlyList<Sanction>> SanctionsAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        var list = new List<Sanction>();
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Auth, ct);

        var activeClause = activeOnly ? "WHERE ab.active = 1" : "";
        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT a.username, ab.bandate, ab.unbandate, ab.bannedby, ab.banreason, ab.active
                FROM account_banned ab
                JOIN account a ON a.id = ab.id
                {activeClause}
                ORDER BY ab.bandate DESC
                LIMIT 500
                """;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                list.Add(Read(SanctionKind.Account, rd));
        }

        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT ip, bandate, unbandate, bannedby, banreason, 1
                FROM ip_banned
                {(activeOnly ? "WHERE unbandate > UNIX_TIMESTAMP() OR unbandate = bandate" : "")}
                ORDER BY bandate DESC
                LIMIT 500
                """;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                list.Add(Read(SanctionKind.Ip, rd));
        }

        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.name, cb.bandate, cb.unbandate, cb.bannedby, cb.banreason, cb.active
                FROM {CharDb}.character_banned cb
                JOIN {CharDb}.characters c ON c.guid = cb.guid
                {(activeOnly ? "WHERE cb.active = 1" : "")}
                ORDER BY cb.bandate DESC
                LIMIT 500
                """;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                list.Add(Read(SanctionKind.Character, rd));
        }

        // Le mute vit dans account_muted, avec une durée plutôt qu'une date de fin.
        await using (var cmd = cnx.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT a.username, am.mutedate, am.mutetime, am.mutedby, am.mutereason
                FROM account_muted am
                JOIN account a ON a.id = am.guid
                ORDER BY am.mutedate DESC
                LIMIT 500
                """;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(1)).LocalDateTime;
                var until = date.AddSeconds(rd.GetInt64(2));
                var sanction = new Sanction(SanctionKind.Mute, rd.GetString(0), date, until,
                    rd.IsDBNull(3) ? "" : rd.GetString(3),
                    rd.IsDBNull(4) ? "" : rd.GetString(4),
                    Active: until > DateTime.Now);

                if (!activeOnly || sanction.Active) list.Add(sanction);
            }
        }

        return [.. list.OrderByDescending(s => s.Date)];
    }

    private static Sanction Read(SanctionKind kind, MySqlConnector.MySqlDataReader rd)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(1)).LocalDateTime;
        var until = DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(2)).LocalDateTime;
        return new Sanction(
            kind,
            rd.GetString(0),
            date,
            until,
            rd.IsDBNull(3) ? "" : rd.GetString(3),
            rd.IsDBNull(4) ? "" : rd.GetString(4),
            rd.GetInt32(5) != 0);
    }

    // ------------------------------------------------------------------ écriture

    /// <summary>Durée au format d'AzerothCore : 10m, 2h, 1d… ou 0 pour un bannissement définitif.</summary>
    public Task<GmCommandResult> BanAccountAsync(string account, string duration, string reason,
                                                 CancellationToken ct = default)
        => _gm.ExecuteAsync($"ban account {account} {duration} {reason}", ct);

    public Task<GmCommandResult> BanCharacterAsync(string character, string duration, string reason,
                                                   CancellationToken ct = default)
        => _gm.ExecuteAsync($"ban character {character} {duration} {reason}", ct);

    /// <summary>
    /// Bannit une adresse IP. Utile contre un contournement par création de comptes, mais
    /// à manier avec précaution : plusieurs joueurs partagent souvent une même adresse.
    /// </summary>
    public Task<GmCommandResult> BanIpAsync(string ip, string duration, string reason,
                                            CancellationToken ct = default)
        => _gm.ExecuteAsync($"ban ip {ip} {duration} {reason}", ct);

    /// <summary>Bannit le compte auquel appartient un personnage, sans avoir à le chercher.</summary>
    public Task<GmCommandResult> BanPlayerAccountAsync(string character, string duration, string reason,
                                                       CancellationToken ct = default)
        => _gm.ExecuteAsync($"ban playeraccount {character} {duration} {reason}", ct);

    public Task<GmCommandResult> UnbanAsync(SanctionKind kind, string target, CancellationToken ct = default)
    {
        var command = kind switch
        {
            SanctionKind.Account => $"unban account {target}",
            SanctionKind.Character => $"unban character {target}",
            SanctionKind.Ip => $"unban ip {target}",
            SanctionKind.Mute => $"unmute {target}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return _gm.ExecuteAsync(command, ct);
    }

    public Task<GmCommandResult> BanInfoAsync(SanctionKind kind, string target, CancellationToken ct = default)
    {
        var command = kind switch
        {
            SanctionKind.Account => $"baninfo account {target}",
            SanctionKind.Character => $"baninfo character {target}",
            SanctionKind.Ip => $"baninfo ip {target}",
            _ => $"baninfo account {target}"
        };
        return _gm.ExecuteAsync(command, ct);
    }
}
