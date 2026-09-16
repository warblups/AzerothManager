using System.Data;
using System.Diagnostics;
using System.IO;
using MySqlConnector;

namespace AzerothManager.Services;

public sealed record SqlExecution(
    bool IsRead,
    DataTable? Table,
    int RowsAffected,
    long ElapsedMs,
    string Message,
    bool Committed = true);

/// <summary>
/// Exécution des requêtes de l'éditeur SQL (§15).
///
/// Les écritures passent par une transaction : la requête est exécutée, le nombre de lignes
/// réellement touchées est présenté, et la validation n'a lieu qu'après confirmation. C'est
/// plus sûr qu'une confirmation à l'aveugle avant exécution, et c'est ce qu'un client SQL
/// générique ne fait pas.
/// </summary>
public sealed class SqlEditorService
{
    private readonly ServerContext _context;
    private readonly MySqlService _mySql;

    public SqlEditorService(ServerContext context, MySqlService mySql)
    {
        _context = context;
        _mySql = mySql;
    }

    /// <summary>
    /// Tables de la base world dont la modification exige un rechargement côté worldserver.
    /// Liste volontairement courte : on ne propose un .reload que si l'on est sûr de son nom.
    /// </summary>
    private static readonly HashSet<string> Reloadable = new(StringComparer.OrdinalIgnoreCase)
    {
        "creature_template", "creature_template_locale", "creature_onkill_reputation",
        "gameobject_template", "item_template", "quest_template", "quest_template_locale",
        "npc_vendor", "npc_trainer", "game_tele", "game_graveyard", "graveyard_zone",
        "creature_loot_template", "gameobject_loot_template", "item_loot_template",
        "reference_loot_template", "skinning_loot_template", "disenchant_loot_template",
        "smart_scripts", "waypoint_data", "creature_text", "gossip_menu", "gossip_menu_option",
        "spell_area", "spell_script_names", "conditions", "areatrigger_teleport", "game_event"
    };

    /// <summary>Commande .reload à proposer après une écriture, ou null si aucune ne s'impose.</summary>
    public static string? SuggestReload(MySqlService.Db db, string sql)
    {
        if (db != MySqlService.Db.World || !SqlStatement.IsWrite(sql)) return null;
        var table = SqlStatement.TargetTable(sql);
        if (table is null) return null;

        var bare = table.Contains('.') ? table[(table.LastIndexOf('.') + 1)..] : table;
        return Reloadable.Contains(bare) ? $".reload {bare}" : null;
    }

    public async Task<int?> EstimateAsync(MySqlService.Db db, string sql, CancellationToken ct = default)
    {
        var count = SqlStatement.BuildCountQuery(sql);
        if (count is null) return null;
        try
        {
            await using var cnx = await _mySql.OpenAsync(db, ct);
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = count;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch
        {
            // Forme non reconnue par le serveur : on préfère ne rien annoncer.
            return null;
        }
    }

    /// <summary>
    /// Exécute une requête. En lecture, retourne la grille. En écriture, exécute dans une
    /// transaction puis appelle <paramref name="confirmCommit"/> avec le nombre de lignes
    /// touchées ; un refus provoque un ROLLBACK.
    /// </summary>
    public async Task<SqlExecution> ExecuteAsync(
        MySqlService.Db db, string sql,
        Func<int, Task<bool>>? confirmCommit = null,
        CancellationToken ct = default)
    {
        var profile = _context.RequireActive();
        var sw = Stopwatch.StartNew();

        if (!SqlStatement.IsWrite(sql))
        {
            var table = await _mySql.QueryAsync(db, sql, ct);
            sw.Stop();
            LocalDatabase.LogHistory(profile.Id, "sql", sql, $"{table.Rows.Count} ligne(s) lue(s)");
            return new SqlExecution(true, table, table.Rows.Count, sw.ElapsedMilliseconds,
                $"{table.Rows.Count} ligne(s) — {sw.ElapsedMilliseconds} ms");
        }

        if (profile.ReadOnly)
            throw new InvalidOperationException(
                $"Le profil « {profile.Name} » est en lecture seule : écriture refusée.");

        await using var cnx = await _mySql.OpenAsync(db, ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using var write = cnx.CreateCommand();
        write.Transaction = tx;
        write.CommandText = sql;

        var rows = await write.ExecuteNonQueryAsync(ct);

        var commit = confirmCommit is null || await confirmCommit(rows);
        if (commit) await tx.CommitAsync(ct);
        else await tx.RollbackAsync(ct);

        sw.Stop();
        var verdict = commit ? "validé" : "annulé (ROLLBACK)";
        LocalDatabase.LogHistory(profile.Id, "sql", sql, $"{rows} ligne(s), {verdict}", rows);

        return new SqlExecution(false, null, rows, sw.ElapsedMilliseconds,
            $"{rows} ligne(s) — {verdict} — {sw.ElapsedMilliseconds} ms", commit);
    }

    /// <summary>Export CSV de la grille courante, séparateur point-virgule pour Excel en français.</summary>
    public static void ExportCsv(DataTable table, string path)
    {
        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        writer.WriteLine(string.Join(';', table.Columns.Cast<DataColumn>().Select(c => Escape(c.ColumnName))));
        foreach (DataRow row in table.Rows)
            writer.WriteLine(string.Join(';', row.ItemArray.Select(v => Escape(v?.ToString() ?? ""))));

        static string Escape(string v) =>
            v.Contains(';') || v.Contains('"') || v.Contains('\n')
                ? '"' + v.Replace("\"", "\"\"") + '"'
                : v;
    }
}
