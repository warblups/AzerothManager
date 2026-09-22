using System.Diagnostics;
using System.IO;
using AzerothManager.Models;
using Renci.SshNet;

namespace AzerothManager.Services;

/// <summary>Fichier distant tel que le listage SFTP le rapporte.</summary>
public sealed record RemoteFile(string Path, string Name, long Size, DateTime Modified);

/// <summary>
/// Accès système au serveur : exécution de commandes shell et transfert de fichiers.
/// Sert la lecture des logs, des .conf et des DBC sur serveur Linux (§14).
/// </summary>
public sealed class SshService
{
    private readonly ServerContext _context;

    public SshService(ServerContext context) => _context = context;

    private static ConnectionInfo BuildConnectionInfo(ServerProfile p) =>
        new(p.Host, p.SshPort, p.SshUser, new PasswordAuthenticationMethod(p.SshUser, p.SshPassword))
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

    public async Task<ConnectionTestResult> TestAsync(ServerProfile p, CancellationToken ct = default)
    {
        if (!p.UsesSsh)
            return new ConnectionTestResult(true, "Serveur local Windows : SSH non requis.", 0);

        if (string.IsNullOrWhiteSpace(p.SshUser))
            return new ConnectionTestResult(false, "Utilisateur SSH non renseigné.", 0);

        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() =>
            {
                using var client = new SshClient(BuildConnectionInfo(p));
                client.Connect();
                using var cmd = client.CreateCommand("uname -a || ver");
                var banner = (cmd.Execute() ?? "").Trim();
                client.Disconnect();
                sw.Stop();
                return new ConnectionTestResult(true,
                    string.IsNullOrEmpty(banner) ? "Connexion SSH établie." : banner,
                    sw.ElapsedMilliseconds);
            }, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionTestResult(false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Exécute une commande shell sur le serveur et retourne sa sortie.</summary>
    public async Task<string> RunAsync(string command, CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        return await Task.Run(() =>
        {
            using var client = new SshClient(BuildConnectionInfo(p));
            client.Connect();
            using var cmd = client.CreateCommand(command);
            var output = cmd.Execute() ?? "";
            client.Disconnect();
            LocalDatabase.LogHistory(p.Id, "action", $"ssh: {command}");
            return output;
        }, ct);
    }

    /// <summary>
    /// Contenu d'un dossier distant, lu par SFTP plutôt que par un `ls` analysé au texte :
    /// la taille et la date arrivent déjà typées, et la mise en forme de `ls` varie d'une
    /// distribution à l'autre.
    /// </summary>
    public async Task<IReadOnlyList<RemoteFile>> ListFilesAsync(string directory, CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        return await Task.Run(() =>
        {
            using var client = new SftpClient(BuildConnectionInfo(p));
            client.Connect();
            var files = client.ListDirectory(directory)
                .Where(f => f.IsRegularFile)
                .Select(f => new RemoteFile(f.FullName, f.Name, f.Length, f.LastWriteTime))
                .ToList();
            client.Disconnect();
            return (IReadOnlyList<RemoteFile>)files;
        }, ct);
    }

    /// <summary>
    /// Fin d'un fichier distant, au plus <paramref name="maxBytes"/> octets.
    ///
    /// Un Server.log pèse couramment des dizaines de mégaoctets : on se positionne par
    /// décalage au lieu de tout télécharger, ce que <see cref="DownloadAsync"/> ferait.
    /// </summary>
    public async Task<(byte[] Data, long FileSize)> ReadTailAsync(string remotePath, long maxBytes,
                                                                  CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        return await Task.Run(() =>
        {
            using var client = new SftpClient(BuildConnectionInfo(p));
            client.Connect();
            try
            {
                var size = client.GetAttributes(remotePath).Size;
                var start = Math.Max(0, size - maxBytes);

                using var stream = client.OpenRead(remotePath);
                stream.Seek(start, SeekOrigin.Begin);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer, 64 * 1024);
                return (buffer.ToArray(), size);
            }
            finally
            {
                client.Disconnect();
            }
        }, ct);
    }

    /// <summary>Télécharge un fichier distant via SFTP (logs, .conf, DBC pour l'armurerie).</summary>
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        var p = _context.RequireActive();
        await Task.Run(() =>
        {
            using var client = new SftpClient(BuildConnectionInfo(p));
            client.Connect();
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            using var fs = File.Create(localPath);
            client.DownloadFile(remotePath, fs);
            client.Disconnect();
        }, ct);
    }
}
