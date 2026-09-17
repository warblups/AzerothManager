using AzerothManager.Models;
using Renci.SshNet;
using Serilog;

namespace AzerothManager.Services;

/// <summary>État du serveur tel qu'observé depuis la machine hôte, via SSH.</summary>
public sealed record ServerDiagnostic(
    string WorldserverState,
    int Restarts,
    bool WorldPortOpen,
    string SoapListenAddress,
    string LastError)
{
    public bool WorldserverRunning => WorldserverState == "online" || WorldPortOpen;
    public bool SoapListening => SoapListenAddress.Length > 0;
    public bool SoapLoopbackOnly => SoapListenAddress.StartsWith("127.");
}

/// <summary>
/// Explique une panne au lieu de la constater.
///
/// « SOAP injoignable » est vrai mais inutile : la cause est presque toujours en amont —
/// worldserver arrêté, en boucle de plantage, ou SOAP non activé. Trois commandes SSH
/// suffisent à trancher, et c'est du diagnostic, pas de la gestion de processus : la
/// frontière du §3 est respectée.
/// </summary>
public sealed class ServerDiagnosticService
{
    /// <summary>
    /// Sortie volontairement structurée en clé=valeur : plus robuste à analyser que la
    /// mise en forme de pm2, qui varie selon les versions.
    /// </summary>
    private const string Probe = """
        state=$(pm2 describe worldserver 2>/dev/null | grep -i '│ status' | head -1 | awk -F'│' '{print $3}' | tr -d ' ')
        echo "STATE=${state:-inconnu}"
        restarts=$(pm2 describe worldserver 2>/dev/null | grep -i '│ restarts' | head -1 | awk -F'│' '{print $3}' | tr -d ' ')
        echo "RESTARTS=${restarts:-0}"
        ss -tln 2>/dev/null | grep -q ':8085' && echo "WORLDPORT=1" || echo "WORLDPORT=0"
        echo "SOAPADDR=$(ss -tln 2>/dev/null | grep ':SOAPPORT ' | awk '{print $4}' | head -1)"
        echo "ERROR=$(tail -n 40 ~/.pm2/logs/worldserver-error.log 2>/dev/null | grep -viE 'priority class|^$' | tail -n 1)"
        """;

    public async Task<ServerDiagnostic?> InspectAsync(ServerProfile p, CancellationToken ct = default)
    {
        if (!p.UsesSsh || string.IsNullOrWhiteSpace(p.SshUser)) return null;

        try
        {
            return await Task.Run(() =>
            {
                using var ssh = new SshClient(new ConnectionInfo(p.Host, p.SshPort, p.SshUser,
                    new PasswordAuthenticationMethod(p.SshUser, p.SshPassword))
                { Timeout = TimeSpan.FromSeconds(8) });
                ssh.Connect();

                using var cmd = ssh.CreateCommand(Probe.Replace("SOAPPORT", p.SoapPort.ToString()));
                var output = cmd.Execute() ?? "";
                ssh.Disconnect();

                var values = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

                return new ServerDiagnostic(
                    WorldserverState: values.GetValueOrDefault("STATE", "inconnu"),
                    Restarts: int.TryParse(values.GetValueOrDefault("RESTARTS"), out var r) ? r : 0,
                    WorldPortOpen: values.GetValueOrDefault("WORLDPORT") == "1",
                    SoapListenAddress: values.GetValueOrDefault("SOAPADDR", ""),
                    LastError: values.GetValueOrDefault("ERROR", ""));
            }, ct);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Diagnostic serveur indisponible");
            return null;
        }
    }

    /// <summary>Formule le diagnostic en une phrase qui dit quoi faire.</summary>
    public static string Explain(ServerDiagnostic d, ServerProfile p, bool tunnelUsed)
    {
        if (!d.WorldserverRunning)
        {
            var state = d.WorldserverState == "inconnu"
                ? "le worldserver ne tourne pas"
                : $"le worldserver est dans l'état « {d.WorldserverState} »";
            var restarts = d.Restarts > 5 ? $", après {d.Restarts} redémarrages" : "";
            var error = string.IsNullOrWhiteSpace(d.LastError)
                ? ""
                : $" Dernière erreur : {d.LastError}";
            return $"Ce n'est pas SOAP : {state}{restarts}.{error}";
        }

        if (!d.SoapListening)
            return $"Le worldserver tourne, mais rien n'écoute sur le port {p.SoapPort} : " +
                   "vérifiez SOAP.Enabled dans worldserver.conf, puis redémarrez le worldserver.";

        if (d.SoapLoopbackOnly && !tunnelUsed)
            return $"SOAP n'écoute que sur {d.SoapListenAddress}, donc il n'est joignable " +
                   "que depuis le serveur : cochez « Passer par un tunnel SSH » dans le profil.";

        if (!d.SoapLoopbackOnly && tunnelUsed)
            return $"SOAP écoute sur {d.SoapListenAddress} et le worldserver tourne : " +
                   "l'échec vient probablement du compte SOAP ou de son niveau administrateur.";

        return $"Le worldserver tourne et SOAP écoute sur {d.SoapListenAddress} : " +
               "l'échec vient probablement du compte SOAP ou de son niveau administrateur.";
    }
}
