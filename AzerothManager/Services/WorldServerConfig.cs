using System.IO;
using System.Text.RegularExpressions;

namespace AzerothManager.Services;

/// <summary>
/// Lecture de réglages dans `worldserver.conf` — localement sur un serveur Windows, par SSH
/// sur un serveur Linux (§14).
///
/// C'est de la consultation de configuration, pas de la gestion : la frontière du §3 tient.
/// Elle sert à expliquer une fonction inerte par la configuration du serveur — une annonce
/// récurrente jamais diffusée, un objet détruit que rien n'a archivé — plutôt que de laisser
/// croire à un défaut de l'application.
/// </summary>
public sealed class WorldServerConfig
{
    private readonly SshService _ssh;
    private readonly ServerContext _context;

    public WorldServerConfig(SshService ssh, ServerContext context)
    {
        _ssh = ssh;
        _context = context;
    }

    /// <summary>Lignes de réglage commençant par <paramref name="prefix"/>, ou null si le fichier est illisible.</summary>
    public async Task<(string Content, string Path)?> ReadAsync(string prefix, CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        var root = p.ServerPath.TrimEnd('/', '\\');
        if (root.Length == 0) return null;

        var separator = p.UsesSsh ? "/" : "\\";
        var path = $"{root}{separator}etc{separator}worldserver.conf";

        string content;
        try
        {
            content = p.UsesSsh
                ? await _ssh.RunAsync($"grep -E '^{Regex.Escape(prefix)}' \"{path}\"", ct)
                : await File.ReadAllTextAsync(path, ct);
        }
        catch
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(content) ? null : (content, path);
    }

    /// <summary>Valeur entière d'un réglage, ou null s'il est absent ou commenté.</summary>
    public static int? Option(string content, string name)
    {
        var match = Regex.Match(content, $@"^\s*{Regex.Escape(name)}\s*=\s*(-?\d+)", RegexOptions.Multiline);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }
}
