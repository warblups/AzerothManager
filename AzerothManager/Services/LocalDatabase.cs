using System.IO;
using Microsoft.Data.Sqlite;

namespace AzerothManager.Services;

/// <summary>
/// Base SQLite locale (cf. cahier des charges §20). Stocke les profils de connexion,
/// les requêtes favorites, l'historique des actions et des commandes GM, les préférences,
/// le cache d'objets de l'armurerie et les destinations de téléportation personnelles.
/// </summary>
public static class LocalDatabase
{
    public static string FolderPath { get; } = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "AzerothManager");

    public static string FilePath => Path.Combine(FolderPath, "azerothmanager.db");

    public static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = FilePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    public static SqliteConnection Open()
    {
        var cnx = new SqliteConnection(ConnectionString);
        cnx.Open();
        return cnx;
    }

    public static void Initialize()
    {
        Directory.CreateDirectory(FolderPath);

        using var cnx = Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Servers (
                Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                Name               TEXT    NOT NULL,
                Environment        INTEGER NOT NULL DEFAULT 0,
                Os                 INTEGER NOT NULL DEFAULT 0,
                Host               TEXT    NOT NULL DEFAULT '127.0.0.1',
                MySqlPort          INTEGER NOT NULL DEFAULT 3306,
                MySqlUser          TEXT    NOT NULL DEFAULT '',
                MySqlPassword      TEXT    NOT NULL DEFAULT '',
                AuthDatabase       TEXT    NOT NULL DEFAULT 'acore_auth',
                CharactersDatabase TEXT    NOT NULL DEFAULT 'acore_characters',
                WorldDatabase      TEXT    NOT NULL DEFAULT 'acore_world',
                SshPort            INTEGER NOT NULL DEFAULT 22,
                SshUser            TEXT    NOT NULL DEFAULT '',
                SshPassword        TEXT    NOT NULL DEFAULT '',
                ServerPath         TEXT    NOT NULL DEFAULT '',
                LogsPath           TEXT    NOT NULL DEFAULT '',
                DbcPath            TEXT    NOT NULL DEFAULT '',
                ReadOnly           INTEGER NOT NULL DEFAULT 0,
                IsActive           INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Favorites (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT NOT NULL,
                Query     TEXT NOT NULL,
                Database  TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS History (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerId  INTEGER,
                Kind      TEXT NOT NULL,   -- 'sql' | 'gm' | 'action'
                Content   TEXT NOT NULL,
                Result    TEXT NOT NULL DEFAULT '',
                RowsTouched INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ItemCache (
                ItemId    INTEGER PRIMARY KEY,
                Name      TEXT NOT NULL DEFAULT '',
                IconName  TEXT NOT NULL DEFAULT '',
                Quality   INTEGER NOT NULL DEFAULT 0,
                Tooltip   TEXT NOT NULL DEFAULT '',
                UpdatedAt TEXT NOT NULL
            );

            -- Données importées une fois depuis les DBC du client, pour que
            -- l'application n'en dépende plus ensuite.
            CREATE TABLE IF NOT EXISTS DbcIcon (
                DisplayId INTEGER PRIMARY KEY,
                IconName  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DbcSpell (
                SpellId INTEGER PRIMARY KEY,
                Name    TEXT NOT NULL
            );

            -- Centre de chaque zone en coordonnées monde, pour la carte et, plus tard,
            -- la téléportation vers une zone.
            CREATE TABLE IF NOT EXISTS DbcZone (
                AreaId  INTEGER PRIMARY KEY,
                MapId   INTEGER NOT NULL,
                CenterX REAL NOT NULL,
                CenterY REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DbcMap (
                MapId INTEGER PRIMARY KEY,
                Name  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DbcArea (
                AreaId INTEGER PRIMARY KEY,
                Name   TEXT NOT NULL
            );

            -- Image décodée à la première utilisation, conservée en PNG.
            CREATE TABLE IF NOT EXISTS IconImage (
                IconName TEXT PRIMARY KEY,
                Png      BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TeleportPoints (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT NOT NULL,
                Continent TEXT NOT NULL DEFAULT '',
                MapId     INTEGER NOT NULL DEFAULT 0,
                X         REAL NOT NULL DEFAULT 0,
                Y         REAL NOT NULL DEFAULT 0,
                Z         REAL NOT NULL DEFAULT 0,
                Orientation REAL NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        Migrate(cnx);
    }

    /// <summary>
    /// Migrations additives. SQLite ne sait pas faire ADD COLUMN IF NOT EXISTS :
    /// on lit les colonnes existantes et on ajoute ce qui manque.
    /// </summary>
    private static void Migrate(SqliteConnection cnx)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = cnx.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Servers)";
            using var rd = info.ExecuteReader();
            while (rd.Read()) existing.Add(rd.GetString(1));
        }

        (string Column, string Definition)[] additions =
        [
            ("SoapPort", "INTEGER NOT NULL DEFAULT 7878"),
            ("SoapUser", "TEXT NOT NULL DEFAULT ''"),
            ("SoapPassword", "TEXT NOT NULL DEFAULT ''"),
            ("SoapThroughSshTunnel", "INTEGER NOT NULL DEFAULT 1")
        ];

        foreach (var (column, definition) in additions)
        {
            if (existing.Contains(column)) continue;
            using var alter = cnx.CreateCommand();
            alter.CommandText = $"ALTER TABLE Servers ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>Lit une préférence utilisateur (table Settings du §20).</summary>
    public static string GetSetting(string key, string fallback = "")
    {
        using var cnx = Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string ?? fallback;
    }

    public static void SetSetting(string key, string value)
    {
        using var cnx = Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Journalise une action dans l'historique local (cf. §21 : SQL et commandes GM comprises).</summary>
    public static void LogHistory(int? serverId, string kind, string content, string result = "", int rowsTouched = 0)
    {
        using var cnx = Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            INSERT INTO History (ServerId, Kind, Content, Result, RowsTouched, CreatedAt)
            VALUES ($s, $k, $c, $r, $n, $d)
            """;
        cmd.Parameters.AddWithValue("$s", (object?)serverId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$r", result);
        cmd.Parameters.AddWithValue("$n", rowsTouched);
        cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
