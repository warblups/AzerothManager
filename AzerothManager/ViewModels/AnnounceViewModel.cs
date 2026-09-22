using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Annonces, message du jour et arrêt planifié (§9).
///
/// Tout passe par des commandes `Console::Yes`, donc par SOAP, sans personnage connecté.
/// Le module dit ce qui va réellement se produire avant d'agir : la durée effective du
/// compte à rebours, la signature « Console » des annonces nominatives, et le fait que
/// PM2 relance le worldserver après un arrêt.
/// </summary>
public partial class AnnounceViewModel : ObservableObject
{
    private readonly AnnounceService _announce;
    private readonly ServerContext _context;

    public IReadOnlyList<AnnounceForm> Forms { get; } = AnnounceService.Forms;
    public IReadOnlyList<string> Locales { get; } = AnnounceService.MotdLocales;
    public ObservableCollection<AutoBroadcastEntry> Broadcasts { get; } = [];

    // Annonce ponctuelle
    [ObservableProperty] private AnnounceForm? _form;
    [ObservableProperty] private string _message = "";

    // Arrêt planifié
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelayPreview))]
    private string _delay = "5m";

    [ObservableProperty] private string _reason = "Maintenance";
    [ObservableProperty] private bool _restart = true;

    // Message du jour
    [ObservableProperty] private string _motdLocale = "frFR";
    [ObservableProperty] private string _motd = "";

    // Annonces récurrentes
    [ObservableProperty] private AutoBroadcastEntry? _selectedBroadcast;
    [ObservableProperty] private string _broadcastText = "";
    [ObservableProperty] private string _broadcastWeight = "1";
    [ObservableProperty] private AutoBroadcastConfig? _broadcastConfig;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Les annonces partent immédiatement aux joueurs connectés.";
    [ObservableProperty] private string _output = "";

    /// <summary>
    /// Durée réellement retenue par le serveur, calculée avec sa propre règle. « 5m » vaut
    /// 5 minutes, « 5 » vaut 5 secondes : la différence mérite d'être visible avant l'envoi.
    /// </summary>
    public string DelayPreview => AnnounceService.TryParseDelay(Delay) is { } seconds
        ? $"= {AnnounceService.DescribeDelay(seconds)} ({seconds} s)"
        : "durée non reconnue — attendu 300, 5m, 1h30m…";

    public AnnounceViewModel(AnnounceService announce, ServerContext context)
    {
        _announce = announce;
        _context = context;
        Form = Forms[0];
    }

    private bool Ready()
    {
        if (_context.HasActive) return true;
        Status = "Aucun serveur actif.";
        return false;
    }

    private void Report(GmCommandResult result, string done)
    {
        Output = result.Output;
        Status = result.Success ? $"{done} ({result.ElapsedMs} ms)" : "Refusé : " + result.Output;
    }

    // ------------------------------------------------------------------ annonces

    [RelayCommand]
    private async Task SendAnnounceAsync()
    {
        if (!Ready() || Form is null) return;

        IsBusy = true;
        try
        {
            var result = await _announce.AnnounceAsync(Form.Kind, Message);
            Report(result, $"Annonce envoyée — {Form.Label}.");
            if (result.Success) Message = "";
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ arrêt planifié

    [RelayCommand]
    private async Task ScheduleAsync()
    {
        if (!Ready()) return;

        if (AnnounceService.TryParseDelay(Delay) is not { } seconds)
        {
            Status = "Durée non reconnue. Attendu : 300, ou 5m, ou 1h30m.";
            return;
        }

        var action = Restart ? "redémarrage" : "arrêt";
        var confirm = System.Windows.MessageBox.Show(
            $"Planifier un {action} du worldserver dans {AnnounceService.DescribeDelay(seconds)} ?\n\n" +
            $"Serveur : {_context.DisplayName}\n" +
            (string.IsNullOrWhiteSpace(Reason) ? "Sans motif annoncé.\n" : $"Motif annoncé : {Reason}\n") +
            "\nLes joueurs connectés verront le compte à rebours et seront déconnectés à son terme.\n\n" +
            "Sous PM2 avec autorestart, le worldserver est relancé aussitôt après : l'arrêt se " +
            "comporte alors comme un redémarrage. Pour l'arrêter vraiment, passez par AzerothUpdater.",
            $"Planifier un {action}",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var result = await _announce.ScheduleShutdownAsync(Delay, Reason, Restart);
            Report(result, $"{char.ToUpperInvariant(action[0]) + action[1..]} planifié dans {AnnounceService.DescribeDelay(seconds)}.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CancelShutdownAsync()
    {
        if (!Ready()) return;

        IsBusy = true;
        try
        {
            Report(await _announce.CancelShutdownAsync(), "Compte à rebours annulé.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ServerInfoAsync()
    {
        if (!Ready()) return;

        IsBusy = true;
        try
        {
            Report(await _announce.ServerInfoAsync(), "État du serveur relevé.");
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ MOTD et accès

    [RelayCommand]
    private async Task ReadMotdAsync()
    {
        if (!Ready()) return;

        IsBusy = true;
        try
        {
            Report(await _announce.ReadMotdAsync(), "Message du jour relu.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SetMotdAsync()
    {
        if (!Ready()) return;

        IsBusy = true;
        try
        {
            Report(await _announce.SetMotdAsync(MotdLocale, Motd), $"Message du jour enregistré en {MotdLocale}.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CloseServerAsync()
    {
        if (!Ready()) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Fermer « {_context.DisplayName} » aux nouvelles connexions ?\n\n" +
            "Les joueurs déjà connectés restent en jeu ; seuls les maîtres de jeu pourront " +
            "encore se connecter. L'état n'est pas persistant : il retombe au redémarrage.",
            "Fermer le serveur",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            Report(await _announce.SetClosedAsync(true), "Serveur fermé aux nouvelles connexions.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenServerAsync()
    {
        if (!Ready()) return;

        IsBusy = true;
        try
        {
            Report(await _announce.SetClosedAsync(false), "Serveur rouvert aux connexions.");
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ annonces récurrentes

    [RelayCommand]
    private async Task RefreshBroadcastsAsync()
    {
        if (!Ready()) return;

        IsBusy = true;
        try
        {
            Broadcasts.Clear();
            foreach (var b in await _announce.ListBroadcastsAsync()) Broadcasts.Add(b);

            // Lue à chaque rafraîchissement : ajouter une annonce pendant que la diffusion
            // est désactivée passerait sinon pour une panne de l'application.
            BroadcastConfig = await _announce.ReadBroadcastConfigAsync();

            Status = Broadcasts.Count == 0
                ? "Aucune annonce récurrente enregistrée."
                : $"{Broadcasts.Count} annonce(s) récurrente(s).";

            if (BroadcastConfig is { Enabled: false })
                Status += "  Attention : la diffusion automatique est désactivée dans worldserver.conf.";
        }
        catch (Exception ex)
        {
            Status = "Lecture impossible : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddBroadcastAsync()
    {
        if (!Ready()) return;
        if (!int.TryParse(BroadcastWeight, out var weight))
        {
            Status = "Le poids doit être un nombre entre 0 et 255.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _announce.AddBroadcastAsync(weight, BroadcastText);
            Report(result, "Annonce récurrente ajoutée — prise en compte immédiate, sans .reload.");
            if (result.Success) BroadcastText = "";
        }
        finally { IsBusy = false; }

        await RefreshBroadcastsAsync();
    }

    [RelayCommand]
    private async Task RemoveBroadcastAsync()
    {
        if (!Ready() || SelectedBroadcast is not { } entry) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Supprimer l'annonce récurrente {entry.Id} ?\n\n« {entry.Text} »",
            "Supprimer une annonce récurrente",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            Report(await _announce.RemoveBroadcastAsync(entry.Id), $"Annonce récurrente {entry.Id} supprimée.");
        }
        finally { IsBusy = false; }

        await RefreshBroadcastsAsync();
    }
}
