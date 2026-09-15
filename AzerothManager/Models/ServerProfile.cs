using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothManager.Models;

public enum ServerOs
{
    Windows,
    Linux
}

public enum ServerEnvironment
{
    Test,
    Production
}

/// <summary>
/// Profil de connexion à un serveur AzerothCore (cf. cahier des charges §13).
/// Un seul profil est actif à la fois ; en changer reconfigure tous les services.
/// Les mots de passe ne sont en clair qu'en mémoire : le chiffrement DPAPI est appliqué
/// au moment de l'écriture en base locale par <see cref="Services.ServerProfileService"/>.
/// </summary>
public partial class ServerProfile : ObservableObject
{
    public int Id { get; set; }

    [ObservableProperty] private string _name = "Nouveau serveur";
    [ObservableProperty] private ServerEnvironment _environment = ServerEnvironment.Test;
    [ObservableProperty] private ServerOs _os = ServerOs.Windows;
    [ObservableProperty] private string _host = "127.0.0.1";

    // MySQL
    [ObservableProperty] private int _mySqlPort = 3306;
    [ObservableProperty] private string _mySqlUser = "acore";
    [ObservableProperty] private string _mySqlPassword = "";
    [ObservableProperty] private string _authDatabase = "acore_auth";
    [ObservableProperty] private string _charactersDatabase = "acore_characters";
    [ObservableProperty] private string _worldDatabase = "acore_world";

    // SOAP : canal d'exécution des commandes GM (§16).
    // AzerothCore écoute par défaut sur 127.0.0.1:7878 uniquement, et l'authentification
    // HTTP Basic circule en clair : pour un serveur distant, on tunnelle via SSH plutôt
    // que d'ouvrir SOAP.IP sur le réseau.
    [ObservableProperty] private int _soapPort = 7878;
    [ObservableProperty] private string _soapUser = "";
    [ObservableProperty] private string _soapPassword = "";
    [ObservableProperty] private bool _soapThroughSshTunnel = true;

    // SSH / SFTP (serveur Linux, ou serveur Windows distant)
    [ObservableProperty] private int _sshPort = 22;
    [ObservableProperty] private string _sshUser = "";
    [ObservableProperty] private string _sshPassword = "";

    // Chemins sur le serveur
    [ObservableProperty] private string _serverPath = "";
    [ObservableProperty] private string _logsPath = "";
    [ObservableProperty] private string _dbcPath = "";

    /// <summary>Mode lecture seule : les services refusent toute écriture (cf. §21).</summary>
    [ObservableProperty] private bool _readOnly;

    [ObservableProperty] private bool _isActive;

    public bool IsProduction => Environment == ServerEnvironment.Production;

    /// <summary>Vrai si les opérations système passent par SSH plutôt que par l'accès local.</summary>
    public bool UsesSsh => Os == ServerOs.Linux || !string.IsNullOrWhiteSpace(SshUser);

    partial void OnEnvironmentChanged(ServerEnvironment value) => OnPropertyChanged(nameof(IsProduction));

    partial void OnOsChanged(ServerOs value) => OnPropertyChanged(nameof(UsesSsh));

    public ServerProfile Clone() => (ServerProfile)MemberwiseClone();

    public override string ToString() => Name;
}
