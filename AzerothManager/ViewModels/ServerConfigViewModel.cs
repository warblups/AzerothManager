using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AzerothManager.ViewModels;

/// <summary>Configuration des serveurs (§13) : première verticale complète de la v1.0.</summary>
public partial class ServerConfigViewModel : ObservableObject
{
    private readonly ServerProfileService _profiles;
    private readonly ServerContext _context;
    private readonly MySqlService _mySql;
    private readonly SshService _ssh;
    private readonly GmCommandService _gm;
    private readonly GameClientService _client;

    public ObservableCollection<ServerProfile> Profiles { get; } = [];

    public Array Environments => Enum.GetValues<ServerEnvironment>();
    public Array OperatingSystems => Enum.GetValues<ServerOs>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ServerProfile? _selected;

    [ObservableProperty] private string _testOutput = "";

    /// <summary>Dossier du client WoW local, source des icônes et des noms de sorts (§12).</summary>
    [ObservableProperty] private string _clientPath = "";
    [ObservableProperty] private string _clientStatus = "";
    [ObservableProperty] private bool _isBusy;

    public bool HasSelection => Selected is not null;

    public ServerConfigViewModel(ServerProfileService profiles, ServerContext context,
                                 MySqlService mySql, SshService ssh, GmCommandService gm,
                                 GameClientService client)
    {
        _profiles = profiles;
        _context = context;
        _mySql = mySql;
        _ssh = ssh;
        _gm = gm;
        _client = client;
        ClientPath = client.ClientPath;
        ClientStatus = client.StatusText;
        Reload();
    }

    public void Reload()
    {
        Profiles.Clear();
        foreach (var p in _profiles.GetAll()) Profiles.Add(p);
        Selected = Profiles.FirstOrDefault(p => p.IsActive) ?? Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void New()
    {
        var p = new ServerProfile { Name = "Nouveau serveur" };
        Profiles.Add(p);
        Selected = p;
        TestOutput = "Profil créé. Renseignez les paramètres puis enregistrez.";
    }

    [RelayCommand]
    private void Save()
    {
        if (Selected is null) return;
        _profiles.Save(Selected);
        Log.Information("Profil enregistré : {Name} (#{Id})", Selected.Name, Selected.Id);
        TestOutput = $"Profil « {Selected.Name} » enregistré.";

        // Si c'est le profil actif, les services doivent repartir sur les nouvelles valeurs.
        if (Selected.IsActive) _context.Active = Selected;
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Supprimer définitivement le profil « {Selected.Name} » ?",
            "Confirmation", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (Selected.Id != 0) _profiles.Delete(Selected);
        if (_context.Active?.Id == Selected.Id) _context.Active = null;

        Profiles.Remove(Selected);
        Selected = Profiles.FirstOrDefault();
        TestOutput = "Profil supprimé.";
    }

    [RelayCommand]
    private void Activate()
    {
        if (Selected is null) return;
        if (Selected.Id == 0) Save();

        _profiles.SetActive(Selected);
        foreach (var p in Profiles) p.IsActive = p.Id == Selected.Id;

        _context.Active = Selected;
        _context.Status = $"Serveur actif : {Selected.Name}";
        Log.Information("Serveur actif : {Name}", Selected.Name);
        TestOutput = $"« {Selected.Name} » est maintenant le serveur actif.";
    }

    [RelayCommand]
    private void BrowseClient()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Dossier racine du client WoW 3.3.5a"
        };
        if (dialog.ShowDialog() != true) return;
        ClientPath = dialog.FolderName;
        _client.ClientPath = ClientPath;
        ClientStatus = _client.StatusText;
    }

    /// <summary>Import unique des DBC. Ensuite, l'application n'a plus besoin du client.</summary>
    [RelayCommand]
    private async Task ImportClientAsync()
    {
        _client.ClientPath = ClientPath;
        IsBusy = true;
        ClientStatus = "Import en cours, patientez…";
        try
        {
            var result = await Task.Run(_client.Import);
            ClientStatus = result.Message + " " + _client.StatusText;
        }
        catch (Exception ex)
        {
            ClientStatus = "Échec de l'import : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestMySqlAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        TestOutput = "Connexion à MySQL…";
        try
        {
            var r = await _mySql.TestAsync(Selected);
            TestOutput = r.Success
                ? $"MySQL OK ({r.ElapsedMs} ms) — {r.Message}"
                : $"MySQL ÉCHEC ({r.ElapsedMs} ms) — {r.Message}";
            if (Selected.IsActive) _context.LatencyMs = r.ElapsedMs;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestSoapAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        TestOutput = "Commande GM « server info » via SOAP…";
        try
        {
            var r = await _gm.TestAsync(Selected);
            TestOutput = r.Success
                ? $"SOAP OK ({r.ElapsedMs} ms) — {r.Output.ReplaceLineEndings(" / ").Trim()}"
                : $"SOAP ÉCHEC ({r.ElapsedMs} ms) — {r.Output}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestSshAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        TestOutput = "Connexion SSH…";
        try
        {
            var r = await _ssh.TestAsync(Selected);
            TestOutput = r.Success
                ? $"SSH OK ({r.ElapsedMs} ms) — {r.Message}"
                : $"SSH ÉCHEC ({r.ElapsedMs} ms) — {r.Message}";
        }
        finally { IsBusy = false; }
    }
}
