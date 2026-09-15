using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using AzerothManager.Models;
using Renci.SshNet;
using Serilog;

namespace AzerothManager.Services;

public readonly record struct GmCommandResult(bool Success, string Output, long ElapsedMs);

/// <summary>
/// Exécution des commandes GM via l'interface SOAP du worldserver (§16).
///
/// Vérifié dans les sources AzerothCore (src/server/apps/worldserver/ACSoap) :
/// espace de noms « urn:AC », préfixe ns1, méthode executeCommand(command) -> result,
/// authentification HTTP Basic avec un compte de jeu de niveau SEC_ADMINISTRATOR (gmlevel 3).
///
/// SOAP.IP vaut 127.0.0.1 par défaut et l'authentification circule en clair : pour un serveur
/// distant on ouvre un tunnel SSH plutôt que d'exposer le port sur le réseau. Le tunnel est
/// monté par appel — quelques centaines de millisecondes, sans état partagé à gérer.
/// </summary>
public sealed class GmCommandService
{
    private const string SoapNamespace = "urn:AC";

    /// <summary>
    /// Commandes informatives tolérées en mode lecture seule. Tout le reste est refusé,
    /// une commande GM étant par défaut une écriture (§21).
    /// </summary>
    private static readonly string[] ReadOnlyAllowed =
    [
        "server info", "pinfo", "gps", "guid", "distance", "lookup", "list",
        "account onlinelist", "ticket list", "ticket viewid", "npc info",
        "gobject info", "character check", "baninfo", "banlist", "gm list", "cheat status"
    ];

    private readonly ServerContext _context;

    public GmCommandService(ServerContext context) => _context = context;

    public static bool IsAllowedInReadOnly(string command)
    {
        var c = command.TrimStart('.').Trim().ToLowerInvariant();
        return ReadOnlyAllowed.Any(a => c.StartsWith(a, StringComparison.Ordinal));
    }

    public Task<GmCommandResult> ExecuteAsync(string command, CancellationToken ct = default)
        => ExecuteAsync(_context.RequireActive(), command, ct);

    public async Task<GmCommandResult> ExecuteAsync(ServerProfile p, string command, CancellationToken ct = default)
    {
        command = command.Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(command))
            return new GmCommandResult(false, "Commande vide.", 0);

        if (p.ReadOnly && !IsAllowedInReadOnly(command))
            return new GmCommandResult(false,
                $"Profil « {p.Name} » en lecture seule : seules les commandes informatives sont autorisées.", 0);

        if (string.IsNullOrWhiteSpace(p.SoapUser))
            return new GmCommandResult(false, "Compte SOAP non renseigné (niveau administrateur requis).", 0);

        var sw = Stopwatch.StartNew();
        SshClient? ssh = null;
        ForwardedPortLocal? tunnel = null;
        try
        {
            string host = p.Host;
            int port = p.SoapPort;

            if (p.SoapThroughSshTunnel && p.UsesSsh)
            {
                ssh = new SshClient(new ConnectionInfo(p.Host, p.SshPort, p.SshUser,
                    new PasswordAuthenticationMethod(p.SshUser, p.SshPassword))
                { Timeout = TimeSpan.FromSeconds(8) });
                ssh.Connect();

                // Port local 0 : le système en choisit un libre.
                tunnel = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)p.SoapPort);
                ssh.AddForwardedPort(tunnel);
                tunnel.Start();

                host = "127.0.0.1";
                port = (int)tunnel.BoundPort;
            }

            var output = await PostAsync(host, port, p.SoapUser, p.SoapPassword, command, ct);
            sw.Stop();

            LocalDatabase.LogHistory(p.Id, "gm", "." + command, output);
            Log.Information("Commande GM exécutée : .{Command}", command);
            return new GmCommandResult(true, output, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            LocalDatabase.LogHistory(p.Id, "gm", "." + command, "ÉCHEC : " + ex.Message);
            return new GmCommandResult(false, ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            try { tunnel?.Stop(); tunnel?.Dispose(); } catch { /* tunnel déjà fermé */ }
            try { ssh?.Disconnect(); ssh?.Dispose(); } catch { /* session déjà fermée */ }
        }
    }

    /// <summary>Test de bout en bout : « server info » renvoie la version et l'uptime.</summary>
    public async Task<GmCommandResult> TestAsync(ServerProfile p, CancellationToken ct = default)
        => await ExecuteAsync(p, "server info", ct);

    private static async Task<string> PostAsync(string host, int port, string user, string password,
                                                string command, CancellationToken ct)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <SOAP-ENV:Envelope
                xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/"
                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                xmlns:ns1="{SoapNamespace}">
              <SOAP-ENV:Body>
                <ns1:executeCommand>
                  <command>{SecurityElement.Escape(command)}</command>
                </ns1:executeCommand>
              </SOAP-ENV:Body>
            </SOAP-ENV:Envelope>
            """;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var response = await http.PostAsync($"http://{host}:{port}/", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "Authentification SOAP refusée : vérifiez le compte, le mot de passe, et que son niveau est administrateur (.account set gmlevel <compte> 3 -1).");

        return ParseResult(body);
    }

    /// <summary>Extrait &lt;result&gt;, ou la faute SOAP renvoyée par le serveur.</summary>
    private static string ParseResult(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return xml.Trim(); }

        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "result");
        if (result is not null) return result.Value.Trim();

        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultstring" or "detail");
        if (fault is not null) throw new InvalidOperationException(fault.Value.Trim());

        return xml.Trim();
    }
}
