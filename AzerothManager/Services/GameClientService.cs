using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AzerothManager.Services;

public sealed record ImportResult(int Icons, int Spells, string Message);

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

    private Dictionary<int, string>? _icons;
    private Dictionary<int, string>? _spells;
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
            return new ImportResult(0, 0, "ItemDisplayInfo.dbc introuvable dans le dossier indiqué.");

        var icons = 0;
        var spells = 0;

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

        tx.Commit();

        _icons = null;
        _spells = null;
        _imageCache.Clear();

        Log.Information("Import DBC : {Icons} icônes, {Spells} sorts", icons, spells);
        return new ImportResult(icons, spells, $"{icons} icônes et {spells} sorts importés.");
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
