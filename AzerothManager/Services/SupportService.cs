using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>
/// Support joueur (§9) : tickets GM et restauration ciblée.
///
/// Les deux modules partagent la même logique de canal : la liste se lit en SQL — un
/// ticket clos ou un personnage supprimé n'est plus adressable en jeu —, et les actions
/// passent par la commande GM, qui seule met à jour l'état vivant du serveur.
/// </summary>
public sealed class SupportService
{
    private readonly MySqlService _mySql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;
    private readonly GameClientService _client;

    public SupportService(MySqlService mySql, GmCommandService gm, ServerContext context,
                          GameClientService client)
    {
        _mySql = mySql;
        _gm = gm;
        _context = context;
        _client = client;
    }

    private string AuthDb => "`" + _context.RequireActive().AuthDatabase.Replace("`", "") + "`";

    // ------------------------------------------------------------------ tickets

    public async Task<IReadOnlyList<GmTicket>> TicketsAsync(bool openOnly = true, CancellationToken ct = default)
    {
        var list = new List<GmTicket>();
        // type fait foi pour l'état : un ticket clos en console garde closedBy à zéro.
        var where = openOnly ? "WHERE t.type = 0" : "";

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.id, t.type, t.playerGuid, t.name, t.description, t.createTime, t.lastModifiedTime,
                   t.mapId, t.posX, t.posY, t.posZ,
                   COALESCE(ga.name, '') AS assigne,
                   COALESCE(gc.name, '') AS clospar,
                   t.comment, t.response,
                   t.completed, t.escalated, t.viewed, t.needMoreHelp,
                   COALESCE(c.online, 0) AS enligne
            FROM gm_ticket t
            LEFT JOIN characters c  ON c.guid  = t.playerGuid
            LEFT JOIN characters ga ON ga.guid = t.assignedTo
            LEFT JOIN characters gc ON gc.guid = t.closedBy
            {where}
            ORDER BY t.id DESC
            LIMIT 500
            """;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mapId = rd.GetInt32(7);
            list.Add(new GmTicket(
                Id: rd.GetInt32(0),
                Type: rd.GetInt32(1),
                PlayerGuid: rd.GetInt32(2),
                PlayerName: rd.IsDBNull(3) ? "" : rd.GetString(3),
                Description: rd.IsDBNull(4) ? "" : rd.GetString(4),
                CreateTime: DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(5)).LocalDateTime,
                LastModifiedTime: DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(6)).LocalDateTime,
                MapId: mapId,
                MapName: _client.MapName(mapId),
                X: rd.GetFloat(8), Y: rd.GetFloat(9), Z: rd.GetFloat(10),
                AssignedTo: rd.GetString(11),
                ClosedBy: rd.GetString(12),
                Comment: rd.IsDBNull(13) ? "" : rd.GetString(13),
                Response: rd.IsDBNull(14) ? "" : rd.GetString(14),
                Completed: rd.GetInt32(15) != 0,
                Escalated: rd.GetInt32(16) != 0,
                Viewed: rd.GetInt32(17) != 0,
                NeedMoreHelp: rd.GetInt32(18) != 0,
                PlayerOnline: rd.GetInt32(19) != 0));
        }
        return list;
    }

    public Task<GmCommandResult> AssignTicketAsync(int id, string gmName, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket assign {id} {gmName}", ct);

    public Task<GmCommandResult> UnassignTicketAsync(int id, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket unassign {id}", ct);

    public Task<GmCommandResult> CommentTicketAsync(int id, string comment, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket comment {id} {comment}", ct);

    /// <summary>Réponse visible par le joueur, ajoutée au fil du ticket.</summary>
    public Task<GmCommandResult> RespondTicketAsync(int id, string response, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket response append {id} {response}", ct);

    /// <summary>Marque le ticket traité ; la réponse facultative est envoyée au joueur.</summary>
    public Task<GmCommandResult> CompleteTicketAsync(int id, string? response = null, CancellationToken ct = default)
        => _gm.ExecuteAsync(string.IsNullOrWhiteSpace(response)
            ? $"ticket complete {id}"
            : $"ticket complete {id} {response}", ct);

    public Task<GmCommandResult> CloseTicketAsync(int id, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket close {id}", ct);

    public Task<GmCommandResult> DeleteTicketAsync(int id, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket delete {id}", ct);

    public Task<GmCommandResult> EscalateTicketAsync(int id, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket escalate {id}", ct);

    public Task<GmCommandResult> ViewTicketAsync(int id, CancellationToken ct = default)
        => _gm.ExecuteAsync($"ticket viewid {id}", ct);

    /// <summary>
    /// Se rendre sur le lieu signalé. Nécessite un personnage maître de jeu connecté :
    /// la commande téléporte celui qui l'exécute.
    /// </summary>
    public Task<GmCommandResult> GoToTicketAsync(int id, CancellationToken ct = default)
        => _gm.ExecuteAsync($"go ticket {id}", ct);

    public Task<GmCommandResult> SummonAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"summon {character}", ct);

    public Task<GmCommandResult> AppearAsync(string character, CancellationToken ct = default)
        => _gm.ExecuteAsync($"appear {character}", ct);

    // ------------------------------------------------------------------ restauration

    /// <summary>
    /// Personnages supprimés encore récupérables. AzerothCore conserve la ligne et
    /// bascule le nom dans deleteInfos_Name : la lecture ne peut donc se faire qu'en SQL.
    /// </summary>
    public async Task<IReadOnlyList<DeletedCharacter>> DeletedCharactersAsync(
        string? search = null, CancellationToken ct = default)
    {
        var list = new List<DeletedCharacter>();
        var where = new List<string> { "c.deleteDate IS NOT NULL" };
        if (!string.IsNullOrWhiteSpace(search)) where.Add("c.deleteInfos_Name LIKE @like");

        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.guid, COALESCE(c.deleteInfos_Name, ''), c.deleteInfos_Account,
                   COALESCE(a.username, ''), c.deleteDate, c.level, c.race, c.class
            FROM characters c
            LEFT JOIN {AuthDb}.account a ON a.id = c.deleteInfos_Account
            WHERE {string.Join(" AND ", where)}
            ORDER BY c.deleteDate DESC
            LIMIT 500
            """;
        if (!string.IsNullOrWhiteSpace(search))
            cmd.Parameters.AddWithValue("@like", "%" + search.Trim() + "%");

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new DeletedCharacter(
                Guid: rd.GetInt32(0),
                Name: rd.GetString(1),
                AccountId: rd.GetInt32(2),
                AccountName: rd.GetString(3),
                DeleteDate: DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(4)).LocalDateTime,
                Level: rd.GetInt32(5),
                Race: rd.GetInt32(6),
                Class: rd.GetInt32(7)));
        }
        return list;
    }

    /// <summary>
    /// Restauration. Un nouveau nom est nécessaire si l'ancien a été repris entre-temps ;
    /// un nouveau compte permet de récupérer un personnage dont le compte a disparu.
    /// </summary>
    public Task<GmCommandResult> RestoreCharacterAsync(
        string name, string? newName = null, string? newAccount = null, CancellationToken ct = default)
    {
        var command = $"character deleted restore {name}";
        if (!string.IsNullOrWhiteSpace(newName)) command += " " + newName.Trim();
        if (!string.IsNullOrWhiteSpace(newAccount)) command += " " + newAccount.Trim();
        return _gm.ExecuteAsync(command, ct);
    }

    /// <summary>Suppression définitive d'un personnage déjà supprimé : plus aucun retour possible.</summary>
    public Task<GmCommandResult> PurgeCharacterAsync(string name, CancellationToken ct = default)
        => _gm.ExecuteAsync($"character deleted delete {name}", ct);
}
