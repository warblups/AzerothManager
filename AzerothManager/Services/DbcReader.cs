using System.IO;
using System.Text;

namespace AzerothManager.Services;

/// <summary>
/// Lecteur des fichiers DBC du client 3.3.5a (format WDBC).
///
/// Structure vérifiée sur les fichiers réels : en-tête de 20 octets — magie « WDBC »,
/// nombre d'enregistrements, nombre de champs, taille d'un enregistrement, taille du
/// bloc de chaînes — suivi des enregistrements puis du bloc de chaînes. Tous les champs
/// font quatre octets ; une chaîne est un décalage dans le bloc final.
/// </summary>
public sealed class DbcReader
{
    private readonly byte[] _data;
    private readonly int _recordsOffset;
    private readonly int _stringsOffset;

    public int RecordCount { get; }
    public int FieldCount { get; }
    public int RecordSize { get; }
    public int StringBlockSize { get; }

    public DbcReader(string path)
    {
        _data = File.ReadAllBytes(path);

        if (_data.Length < 20 || Encoding.ASCII.GetString(_data, 0, 4) != "WDBC")
            throw new InvalidDataException($"{Path.GetFileName(path)} n'est pas un fichier DBC.");

        RecordCount = BitConverter.ToInt32(_data, 4);
        FieldCount = BitConverter.ToInt32(_data, 8);
        RecordSize = BitConverter.ToInt32(_data, 12);
        StringBlockSize = BitConverter.ToInt32(_data, 16);

        _recordsOffset = 20;
        _stringsOffset = _recordsOffset + RecordCount * RecordSize;
    }

    public uint GetUInt(int row, int field)
    {
        if ((uint)row >= (uint)RecordCount || (uint)field >= (uint)FieldCount) return 0;
        return BitConverter.ToUInt32(_data, _recordsOffset + row * RecordSize + field * 4);
    }

    public int GetInt(int row, int field) => unchecked((int)GetUInt(row, field));

    /// <summary>Chaîne référencée par un champ. Un décalage nul ou hors bloc rend une chaîne vide.</summary>
    public string GetString(int row, int field)
    {
        var offset = GetUInt(row, field);
        if (offset == 0 || offset >= (uint)StringBlockSize) return "";

        var start = _stringsOffset + (int)offset;
        var end = start;
        while (end < _data.Length && _data[end] != 0) end++;
        return Encoding.UTF8.GetString(_data, start, end - start);
    }

    /// <summary>
    /// Première chaîne non vide parmi une plage de champs. Les DBC réservent seize champs
    /// consécutifs aux langues, et un client localisé ne remplit que le sien : balayer la
    /// plage évite de coder en dur un créneau qui dépend de la langue du client.
    /// </summary>
    public string GetFirstNonEmptyString(int row, int firstField, int count = 16)
    {
        for (var i = 0; i < count; i++)
        {
            var s = GetString(row, firstField + i);
            if (s.Length > 0) return s;
        }
        return "";
    }
}
