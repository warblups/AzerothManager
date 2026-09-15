using AzerothManager.Models;
using Microsoft.Data.Sqlite;

namespace AzerothManager.Services;

/// <summary>
/// CRUD des profils de connexion en base locale. Les mots de passe traversent
/// systématiquement <see cref="CredentialProtector"/> : rien n'est écrit en clair (§21).
/// </summary>
public sealed class ServerProfileService
{
    public List<ServerProfile> GetAll()
    {
        var list = new List<ServerProfile>();
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT * FROM Servers ORDER BY Name";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    public ServerProfile? GetActive() => GetAll().FirstOrDefault(p => p.IsActive);

    public void Save(ServerProfile p)
    {
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();

        cmd.CommandText = p.Id == 0
            ? """
              INSERT INTO Servers
                (Name, Environment, Os, Host, MySqlPort, MySqlUser, MySqlPassword,
                 AuthDatabase, CharactersDatabase, WorldDatabase,
                 SshPort, SshUser, SshPassword, ServerPath, LogsPath, DbcPath, ReadOnly, IsActive)
              VALUES
                ($name, $env, $os, $host, $myport, $myuser, $mypwd,
                 $authdb, $chardb, $worlddb,
                 $sshport, $sshuser, $sshpwd, $path, $logs, $dbc, $ro, $active);
              SELECT last_insert_rowid();
              """
            : """
              UPDATE Servers SET
                Name=$name, Environment=$env, Os=$os, Host=$host,
                MySqlPort=$myport, MySqlUser=$myuser, MySqlPassword=$mypwd,
                AuthDatabase=$authdb, CharactersDatabase=$chardb, WorldDatabase=$worlddb,
                SshPort=$sshport, SshUser=$sshuser, SshPassword=$sshpwd,
                ServerPath=$path, LogsPath=$logs, DbcPath=$dbc, ReadOnly=$ro, IsActive=$active
              WHERE Id=$id;
              SELECT $id;
              """;

        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$env", (int)p.Environment);
        cmd.Parameters.AddWithValue("$os", (int)p.Os);
        cmd.Parameters.AddWithValue("$host", p.Host);
        cmd.Parameters.AddWithValue("$myport", p.MySqlPort);
        cmd.Parameters.AddWithValue("$myuser", p.MySqlUser);
        cmd.Parameters.AddWithValue("$mypwd", CredentialProtector.Protect(p.MySqlPassword));
        cmd.Parameters.AddWithValue("$authdb", p.AuthDatabase);
        cmd.Parameters.AddWithValue("$chardb", p.CharactersDatabase);
        cmd.Parameters.AddWithValue("$worlddb", p.WorldDatabase);
        cmd.Parameters.AddWithValue("$sshport", p.SshPort);
        cmd.Parameters.AddWithValue("$sshuser", p.SshUser);
        cmd.Parameters.AddWithValue("$sshpwd", CredentialProtector.Protect(p.SshPassword));
        cmd.Parameters.AddWithValue("$path", p.ServerPath);
        cmd.Parameters.AddWithValue("$logs", p.LogsPath);
        cmd.Parameters.AddWithValue("$dbc", p.DbcPath);
        cmd.Parameters.AddWithValue("$ro", p.ReadOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$active", p.IsActive ? 1 : 0);

        p.Id = Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Delete(ServerProfile p)
    {
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "DELETE FROM Servers WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Un seul serveur actif à la fois (§13).</summary>
    public void SetActive(ServerProfile p)
    {
        using var cnx = LocalDatabase.Open();
        using var tx = cnx.BeginTransaction();

        using (var clear = cnx.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE Servers SET IsActive=0";
            clear.ExecuteNonQuery();
        }
        using (var set = cnx.CreateCommand())
        {
            set.Transaction = tx;
            set.CommandText = "UPDATE Servers SET IsActive=1 WHERE Id=$id";
            set.Parameters.AddWithValue("$id", p.Id);
            set.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static ServerProfile Read(SqliteDataReader rd) => new()
    {
        Id = rd.GetInt32(rd.GetOrdinal("Id")),
        Name = rd.GetString(rd.GetOrdinal("Name")),
        Environment = (ServerEnvironment)rd.GetInt32(rd.GetOrdinal("Environment")),
        Os = (ServerOs)rd.GetInt32(rd.GetOrdinal("Os")),
        Host = rd.GetString(rd.GetOrdinal("Host")),
        MySqlPort = rd.GetInt32(rd.GetOrdinal("MySqlPort")),
        MySqlUser = rd.GetString(rd.GetOrdinal("MySqlUser")),
        MySqlPassword = CredentialProtector.Unprotect(rd.GetString(rd.GetOrdinal("MySqlPassword"))),
        AuthDatabase = rd.GetString(rd.GetOrdinal("AuthDatabase")),
        CharactersDatabase = rd.GetString(rd.GetOrdinal("CharactersDatabase")),
        WorldDatabase = rd.GetString(rd.GetOrdinal("WorldDatabase")),
        SshPort = rd.GetInt32(rd.GetOrdinal("SshPort")),
        SshUser = rd.GetString(rd.GetOrdinal("SshUser")),
        SshPassword = CredentialProtector.Unprotect(rd.GetString(rd.GetOrdinal("SshPassword"))),
        ServerPath = rd.GetString(rd.GetOrdinal("ServerPath")),
        LogsPath = rd.GetString(rd.GetOrdinal("LogsPath")),
        DbcPath = rd.GetString(rd.GetOrdinal("DbcPath")),
        ReadOnly = rd.GetInt32(rd.GetOrdinal("ReadOnly")) != 0,
        IsActive = rd.GetInt32(rd.GetOrdinal("IsActive")) != 0
    };
}
