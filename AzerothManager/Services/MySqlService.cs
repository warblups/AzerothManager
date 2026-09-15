using System.Data;
using System.Diagnostics;
using AzerothManager.Models;
using MySqlConnector;

namespace AzerothManager.Services;

/// <summary>Résultat d'un test de connexion, destiné à la barre d'état et aux journaux.</summary>
public readonly record struct ConnectionTestResult(bool Success, string Message, long ElapsedMs);

/// <summary>
/// Accès aux trois bases AzerothCore (auth, characters, world).
/// Toutes les opérations sont asynchrones : elles traversent le réseau (§17).
/// </summary>
public sealed class MySqlService
{
    public enum Db { Auth, Characters, World }

    private readonly ServerContext _context;

    public MySqlService(ServerContext context) => _context = context;

    public static string BuildConnectionString(ServerProfile p, string database) =>
        new MySqlConnectionStringBuilder
        {
            Server = p.Host,
            Port = (uint)p.MySqlPort,
            UserID = p.MySqlUser,
            Password = p.MySqlPassword,
            Database = database,
            ConnectionTimeout = 8,
            DefaultCommandTimeout = 60
        }.ConnectionString;

    private string DatabaseName(ServerProfile p, Db db) => db switch
    {
        Db.Auth => p.AuthDatabase,
        Db.Characters => p.CharactersDatabase,
        Db.World => p.WorldDatabase,
        _ => p.CharactersDatabase
    };

    public async Task<MySqlConnection> OpenAsync(Db db, CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        var cnx = new MySqlConnection(BuildConnectionString(p, DatabaseName(p, db)));
        await cnx.OpenAsync(ct);
        return cnx;
    }

    /// <summary>Teste la connexion et vérifie que les trois bases sont visibles.</summary>
    public async Task<ConnectionTestResult> TestAsync(ServerProfile p, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var cnx = new MySqlConnection(BuildConnectionString(p, p.AuthDatabase));
            await cnx.OpenAsync(ct);

            var missing = new List<string>();
            foreach (var name in new[] { p.AuthDatabase, p.CharactersDatabase, p.WorldDatabase })
            {
                await using var cmd = cnx.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @n";
                cmd.Parameters.AddWithValue("@n", name);
                var found = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
                if (!found) missing.Add(name);
            }
            sw.Stop();

            return missing.Count == 0
                ? new ConnectionTestResult(true, $"Connecté à MySQL {cnx.ServerVersion}, les trois bases sont visibles.", sw.ElapsedMilliseconds)
                : new ConnectionTestResult(false, $"Connecté, mais base(s) introuvable(s) : {string.Join(", ", missing)}.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionTestResult(false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Exécute une requête de lecture et retourne la table résultante.</summary>
    public async Task<DataTable> QueryAsync(Db db, string sql, CancellationToken ct = default)
    {
        await using var cnx = await OpenAsync(db, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var table = new DataTable();
        table.Load(rd);
        return table;
    }

    /// <summary>
    /// Exécute une écriture. Refusée si le profil actif est en lecture seule :
    /// le blocage est au niveau du service, pas de l'interface (§21).
    /// </summary>
    public async Task<int> ExecuteAsync(Db db, string sql, CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        if (p.ReadOnly)
            throw new InvalidOperationException($"Le profil « {p.Name} » est en lecture seule : écriture refusée.");

        await using var cnx = await OpenAsync(db, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        LocalDatabase.LogHistory(p.Id, "sql", sql, $"{rows} ligne(s)", rows);
        return rows;
    }
}
