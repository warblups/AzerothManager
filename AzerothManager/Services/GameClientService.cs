using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AzerothManager.Services;

public sealed record ImportResult(int Icons, int Spells, int Maps, int Areas, int Zones, string Message);

/// <summary>
/// Icônes d'objets et noms de sorts, issus des DBC du client 3.3.5a.
///
/// Le client n'est requis qu'une fois, à l'import : les correspondances sont recopiées dans
/// la base SQLite locale, et les images décodées à la première utilisation y sont conservées
/// en PNG. L'application fonctionne ensuite sans le client, sans service externe et sans
/// condition d'utilisation — c'est le rang 2 de la stratégie de l'armurerie (§12), poussé
/// jusqu'à l'autonomie complète.
///
/// Chaîne d'origine : item_template.displayid -> ItemDisplayInfo.dbc champ 5 -> nom d'icône
/// -> Interface\Icons\&lt;nom&gt;.tga.
/// </summary>
public sealed class GameClientService
{
    private const string PathSetting = "client.path";

    /// <summary>Champ 5 d'ItemDisplayInfo, vérifié sur les fichiers réels.</summary>
    private const int IconField = 5;

    /// <summary>Premier des seize créneaux de langue du nom de sort.</summary>
    private const int SpellNameFirstField = 136;

    /// <summary>Premiers créneaux de langue, vérifiés sur les fichiers réels.</summary>
    private const int MapNameFirstField = 5;
    private const int AreaNameFirstField = 11;

    private Dictionary<int, string>? _icons;
    private Dictionary<int, string>? _spells;
    private Dictionary<int, string>? _maps;
    private Dictionary<int, string>? _areas;
    private List<ZoneRect>? _zoneRects;
    private readonly Dictionary<int, ImageSource?> _imageCache = [];

    /// <summary>
    /// Accès statique, assumé : les convertisseurs XAML n'ont pas de mécanisme d'injection,
    /// et l'application n'a qu'une instance.
    /// </summary>
    public static GameClientService? Current { get; set; }

    public string ClientPath
    {
        get => LocalDatabase.GetSetting(PathSetting);
        set => LocalDatabase.SetSetting(PathSetting, value ?? "");
    }

    public string DbcPath => Path.Combine(ClientPath, "dbc");
    public string IconsPath => Path.Combine(ClientPath, "Interface", "Icons");

    public bool ClientHasDbc =>
        !string.IsNullOrWhiteSpace(ClientPath) && File.Exists(Path.Combine(DbcPath, "ItemDisplayInfo.dbc"));

    public bool ClientHasIcons => !string.IsNullOrWhiteSpace(ClientPath) && Directory.Exists(IconsPath);

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Recopie ItemDisplayInfo.dbc et Spell.dbc dans la base locale. Une seule transaction :
    /// cent mille insertions ligne à ligne prendraient des minutes.
    /// </summary>
    public ImportResult Import()
    {
        if (!ClientHasDbc)
            return new ImportResult(0, 0, 0, 0, 0, "ItemDisplayInfo.dbc introuvable dans le dossier indiqué.");

        var icons = 0;
        var spells = 0;
        var maps = 0;
        var areas = 0;
        var zones = 0;

        using var cnx = LocalDatabase.Open();
        using var tx = cnx.BeginTransaction();

        using (var cmd = cnx.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO DbcIcon (DisplayId, IconName) VALUES ($d, $n) " +
                              "ON CONFLICT(DisplayId) DO UPDATE SET IconName = $n";
            var pd = cmd.Parameters.Add("$d", SqliteType.Integer);
            var pn = cmd.Parameters.Add("$n", SqliteType.Text);

            var dbc = new DbcReader(Path.Combine(DbcPath, "ItemDisplayInfo.dbc"));
            for (var row = 0; row < dbc.RecordCount; row++)
            {
                var name = dbc.GetString(row, IconField);
                if (name.Length == 0) continue;
                pd.Value = dbc.GetInt(row, 0);
                pn.Value = name;
                cmd.ExecuteNonQuery();
                icons++;
            }
        }

        var spellFile = Path.Combine(DbcPath, "Spell.dbc");
        if (File.Exists(spellFile))
        {
            using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO DbcSpell (SpellId, Name) VALUES ($i, $n) " +
                              "ON CONFLICT(SpellId) DO UPDATE SET Name = $n";
            var pi = cmd.Parameters.Add("$i", SqliteType.Integer);
            var pn = cmd.Parameters.Add("$n", SqliteType.Text);

            var dbc = new DbcReader(spellFile);
            for (var row = 0; row < dbc.RecordCount; row++)
            {
                var name = dbc.GetFirstNonEmptyString(row, SpellNameFirstField);
                if (name.Length == 0) continue;
                pi.Value = dbc.GetInt(row, 0);
                pn.Value = name;
                cmd.ExecuteNonQuery();
                spells++;
            }
        }

        maps = ImportNames(cnx, tx, "Map.dbc", MapNameFirstField, "DbcMap", "MapId");
        areas = ImportNames(cnx, tx, "AreaTable.dbc", AreaNameFirstField, "DbcArea", "AreaId");
        zones = ImportZones(cnx, tx);

        tx.Commit();

        _icons = null;
        _spells = null;
        _maps = null;
        _areas = null;
        _zoneRects = null;
        _imageCache.Clear();

        Log.Information("Import DBC : {Icons} icônes, {Spells} sorts, {Maps} cartes, {Areas} zones, {Rects} rectangles",
            icons, spells, maps, areas, zones);
        return new ImportResult(icons, spells, maps, areas, zones,
            $"{icons} icônes, {spells} sorts, {maps} cartes, {areas} zones et {zones} rectangles importés.");
    }

    /// <summary>
    /// Rectangles de WorldMapArea.dbc, réduits au centre de chaque zone. Sert à placer
    /// une bulle de population, et fournira le point de chute d'une téléportation.
    ///
    /// Attention au sens des bornes : les champs 4 et 5 encadrent position_y, les champs
    /// 6 et 7 encadrent position_x — l'inverse de ce que « Left » et « Top » suggèrent.
    /// Les lignes dont areaID vaut zéro décrivent le continent entier et sont écartées.
    /// </summary>
    private int ImportZones(SqliteConnection cnx, SqliteTransaction tx)
    {
        var path = Path.Combine(DbcPath, "WorldMapArea.dbc");
        if (!File.Exists(path)) return 0;

        var count = 0;
        try
        {
            using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO DbcZone (AreaId, MapId, CenterX, CenterY, MinX, MaxX, MinY, MaxY) " +
                "VALUES ($a, $m, $x, $y, $x0, $x1, $y0, $y1) ON CONFLICT(AreaId) DO UPDATE SET " +
                "MapId = $m, CenterX = $x, CenterY = $y, MinX = $x0, MaxX = $x1, MinY = $y0, MaxY = $y1";
            var pa = cmd.Parameters.Add("$a", SqliteType.Integer);
            var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
            var px = cmd.Parameters.Add("$x", SqliteType.Real);
            var py = cmd.Parameters.Add("$y", SqliteType.Real);
            var px0 = cmd.Parameters.Add("$x0", SqliteType.Real);
            var px1 = cmd.Parameters.Add("$x1", SqliteType.Real);
            var py0 = cmd.Parameters.Add("$y0", SqliteType.Real);
            var py1 = cmd.Parameters.Add("$y1", SqliteType.Real);

            var dbc = new DbcReader(path);
            for (var row = 0; row < dbc.RecordCount; row++)
            {
                var areaId = dbc.GetInt(row, 2);
                if (areaId == 0) continue;

                var yHigh = dbc.GetFloat(row, 4);
                var yLow = dbc.GetFloat(row, 5);
                var xHigh = dbc.GetFloat(row, 6);
                var xLow = dbc.GetFloat(row, 7);
                if (yHigh == yLow || xHigh == xLow) continue;

                pa.Value = areaId;
                pm.Value = dbc.GetInt(row, 1);
                px.Value = (xHigh + xLow) / 2.0;
                py.Value = (yHigh + yLow) / 2.0;
                px0.Value = Math.Min(xHigh, xLow);
                px1.Value = Math.Max(xHigh, xLow);
                py0.Value = Math.Min(yHigh, yLow);
                py1.Value = Math.Max(yHigh, yLow);
                cmd.ExecuteNonQuery();
                count++;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Lecture de WorldMapArea.dbc impossible");
        }
        return count;
    }

    /// <summary>Import générique d'un DBC identifiant -> nom localisé.</summary>
    private int ImportNames(SqliteConnection cnx, SqliteTransaction tx, string file,
                            int firstNameField, string table, string keyColumn)
    {
        var path = Path.Combine(DbcPath, file);
        if (!File.Exists(path)) return 0;

        var count = 0;
        try
        {
            using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {table} ({keyColumn}, Name) VALUES ($k, $n) " +
                              $"ON CONFLICT({keyColumn}) DO UPDATE SET Name = $n";
            var pk = cmd.Parameters.Add("$k", SqliteType.Integer);
            var pn = cmd.Parameters.Add("$n", SqliteType.Text);

            var dbc = new DbcReader(path);
            for (var row = 0; row < dbc.RecordCount; row++)
            {
                var name = dbc.GetFirstNonEmptyString(row, firstNameField);
                if (name.Length == 0) continue;
                pk.Value = dbc.GetInt(row, 0);
                pn.Value = name;
                cmd.ExecuteNonQuery();
                count++;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Lecture de {File} impossible", file);
        }
        return count;
    }

    // ------------------------------------------------------------------ lecture

    public (int Icons, int Spells, int Images) CachedCounts()
    {
        using var cnx = LocalDatabase.Open();
        return (Count(cnx, "DbcIcon"), Count(cnx, "DbcSpell"), Count(cnx, "IconImage"));

        static int Count(SqliteConnection c, string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public string StatusText
    {
        get
        {
            var (icons, spells, images) = CachedCounts();
            if (icons == 0 && spells == 0)
                return string.IsNullOrWhiteSpace(ClientPath)
                    ? "Aucune donnée importée. Indiquez le dossier du client puis lancez l'import."
                    : "Aucune donnée importée. Lancez l'import.";

            return $"{icons} icônes et {spells} sorts en base · {images} images en cache" +
                   (ClientHasIcons ? "" : " · dossier d'icônes indisponible, seules les images déjà en cache s'afficheront");
        }
    }

    private Dictionary<int, string> Icons()
    {
        if (_icons is not null) return _icons;
        _icons = [];
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT DisplayId, IconName FROM DbcIcon";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) _icons[rd.GetInt32(0)] = rd.GetString(1);
        return _icons;
    }

    private Dictionary<int, string> Spells()
    {
        if (_spells is not null) return _spells;
        _spells = [];
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT SpellId, Name FROM DbcSpell";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) _spells[rd.GetInt32(0)] = rd.GetString(1);
        return _spells;
    }

    private Dictionary<int, string> Names(string table, string keyColumn)
    {
        var map = new Dictionary<int, string>();
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT {keyColumn}, Name FROM {table}";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) map[rd.GetInt32(0)] = rd.GetString(1);
        return map;
    }

    public string MapName(int mapId) =>
        (_maps ??= Names("DbcMap", "MapId")).GetValueOrDefault(mapId, $"carte {mapId}");

    public string AreaName(int areaId) =>
        (_areas ??= Names("DbcArea", "AreaId")).GetValueOrDefault(areaId, areaId == 0 ? "—" : $"zone {areaId}");

    /// <summary>Rectangle d'une zone en coordonnées monde, avec son centre.</summary>
    public sealed record ZoneRect(int AreaId, int MapId, float X, float Y,
                                  float MinX, float MaxX, float MinY, float MaxY)
    {
        public double Area => (double)(MaxX - MinX) * (MaxY - MinY);
        public bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
    }

    private List<ZoneRect> ZoneRects()
    {
        if (_zoneRects is not null) return _zoneRects;

        _zoneRects = [];
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT AreaId, MapId, CenterX, CenterY, MinX, MaxX, MinY, MaxY FROM DbcZone";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            _zoneRects.Add(new ZoneRect(
                rd.GetInt32(0), rd.GetInt32(1),
                (float)rd.GetDouble(2), (float)rd.GetDouble(3),
                (float)rd.GetDouble(4), (float)rd.GetDouble(5),
                (float)rd.GetDouble(6), (float)rd.GetDouble(7)));
        }
        return _zoneRects;
    }

    public ZoneRect? ZoneCenter(int areaId) => ZoneRects().FirstOrDefault(z => z.AreaId == areaId);

    /// <summary>
    /// Zone déduite d'une position. Indispensable ici : characters.zone n'est écrite qu'à
    /// la déconnexion, et vaut zéro pour la quasi-totalité des personnages d'un serveur
    /// peuplé de bots. La position, elle, est toujours juste.
    ///
    /// Les zones s'emboîtent — une ville dans une région —, donc la plus petite l'emporte.
    /// </summary>
    public ZoneRect? ZoneAt(int mapId, float x, float y) =>
        ZoneRects()
            .Where(z => z.MapId == mapId && z.Contains(x, y))
            .OrderBy(z => z.Area)
            .FirstOrDefault();

    public string? IconName(int displayId) => Icons().GetValueOrDefault(displayId);

    public string? SpellName(int spellId) => Spells().GetValueOrDefault(spellId);

    /// <summary>
    /// Icône d'un objet. Servie depuis le cache mémoire, sinon depuis la base, sinon
    /// décodée depuis le client et rangée en base pour les fois suivantes.
    /// </summary>
    public ImageSource? Icon(int displayId)
    {
        if (_imageCache.TryGetValue(displayId, out var cached)) return cached;

        ImageSource? image = null;
        var name = IconName(displayId);

        if (name is not null)
        {
            var png = LoadPngFromCache(name);
            if (png is not null)
            {
                image = Decode(png);
            }
            else if (ClientHasIcons)
            {
                var file = Path.Combine(IconsPath, name + ".tga");
                if (File.Exists(file))
                {
                    var bitmap = LoadTga(file);
                    if (bitmap is not null)
                    {
                        SavePngToCache(name, Encode(bitmap));
                        image = bitmap;
                    }
                }
            }
        }

        _imageCache[displayId] = image;
        return image;
    }

    private static byte[]? LoadPngFromCache(string iconName)
    {
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT Png FROM IconImage WHERE IconName = $n";
        cmd.Parameters.AddWithValue("$n", iconName);
        return cmd.ExecuteScalar() as byte[];
    }

    private static void SavePngToCache(string iconName, byte[] png)
    {
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "INSERT INTO IconImage (IconName, Png) VALUES ($n, $p) " +
                          "ON CONFLICT(IconName) DO UPDATE SET Png = $p";
        cmd.Parameters.AddWithValue("$n", iconName);
        cmd.Parameters.AddWithValue("$p", png);
        cmd.ExecuteNonQuery();
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static ImageSource Decode(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Décodeur TGA réduit au cas des icônes du client : non compressé, 32 bits BGRA,
    /// origine en bas à gauche. WPF ne lit pas le TGA nativement, mais ce format-là tient
    /// en quelques lignes et évite toute dépendance.
    /// </summary>
    private static BitmapSource? LoadTga(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 18) return null;

            int idLength = data[0];
            int imageType = data[2];
            int width = data[12] | (data[13] << 8);
            int height = data[14] | (data[15] << 8);
            int bpp = data[16];
            int descriptor = data[17];

            // Seul le cas réellement rencontré est géré ; toute autre variante est ignorée
            // plutôt que mal décodée.
            if (imageType != 2 || bpp != 32 || width <= 0 || height <= 0) return null;

            var start = 18 + idLength;
            var stride = width * 4;
            if (data.Length < start + stride * height) return null;

            var pixels = new byte[stride * height];
            var topDown = (descriptor & 0x20) != 0;
            for (var y = 0; y < height; y++)
            {
                var source = start + (topDown ? y : height - 1 - y) * stride;
                Buffer.BlockCopy(data, source, pixels, y * stride, stride);
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
