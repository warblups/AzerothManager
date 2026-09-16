using System.Text.RegularExpressions;

namespace AzerothManager.Services;

public enum SqlKind
{
    /// <summary>SELECT, SHOW, DESCRIBE… : retourne une grille.</summary>
    Read,
    /// <summary>INSERT : écriture non destructrice.</summary>
    Insert,
    /// <summary>UPDATE, DELETE : écriture réversible seulement si on la voit venir.</summary>
    Destructive,
    /// <summary>DROP, TRUNCATE, ALTER : irréversible, aucune estimation possible.</summary>
    Structural
}

/// <summary>
/// Analyse sommaire d'une requête, pour décider du garde-fou à appliquer (§15)
/// et du <c>.reload</c> à proposer après une écriture dans world (§9).
/// </summary>
public static partial class SqlStatement
{
    public static SqlKind Classify(string sql)
    {
        var s = StripComments(sql).TrimStart();
        if (s.StartsWith("update", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("delete", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("replace", StringComparison.OrdinalIgnoreCase))
            return SqlKind.Destructive;

        if (s.StartsWith("drop", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("truncate", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("alter", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("create", StringComparison.OrdinalIgnoreCase))
            return SqlKind.Structural;

        if (s.StartsWith("insert", StringComparison.OrdinalIgnoreCase))
            return SqlKind.Insert;

        return SqlKind.Read;
    }

    public static bool IsWrite(string sql) => Classify(sql) != SqlKind.Read;

    /// <summary>Table principale visée, pour proposer le .reload correspondant.</summary>
    public static string? TargetTable(string sql)
    {
        var s = StripComments(sql).Trim();
        var m = TableRegex().Match(s);
        return m.Success ? m.Groups["table"].Value.Trim('`', '"', '\'') : null;
    }

    /// <summary>
    /// Construit un COUNT équivalent à un UPDATE ou DELETE, pour annoncer le nombre de lignes
    /// avant de les toucher. Retourne null si la forme n'est pas reconnue : mieux vaut pas
    /// d'estimation qu'une estimation fausse.
    /// </summary>
    public static string? BuildCountQuery(string sql)
    {
        var s = StripComments(sql).Trim().TrimEnd(';');

        var del = DeleteRegex().Match(s);
        if (del.Success)
            return $"SELECT COUNT(*) FROM {del.Groups["table"].Value} {del.Groups["rest"].Value}".Trim();

        var upd = UpdateRegex().Match(s);
        if (upd.Success)
        {
            var where = upd.Groups["where"].Success ? "WHERE " + upd.Groups["where"].Value : "";
            return $"SELECT COUNT(*) FROM {upd.Groups["table"].Value} {where}".Trim();
        }

        return null;
    }

    private static string StripComments(string sql) =>
        LineCommentRegex().Replace(BlockCommentRegex().Replace(sql, " "), " ");

    [GeneratedRegex(@"^\s*(?:update|delete\s+from|insert\s+into|replace\s+into|truncate(?:\s+table)?|drop\s+table|alter\s+table)\s+(?<table>[`""\w\.]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"^delete\s+from\s+(?<table>[`""\w\.]+)\s*(?<rest>where\b.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DeleteRegex();

    [GeneratedRegex(@"^update\s+(?<table>[`""\w\.]+)\s+set\s+.*?(?:\bwhere\b(?<where>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UpdateRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineCommentRegex();
}
