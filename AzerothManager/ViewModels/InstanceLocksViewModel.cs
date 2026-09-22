using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Verrous d'instance (§9).
///
/// Module de repli en SQL : `.instance listbinds` et `.instance unbind` sont `Console::No`,
/// donc hors de portée. Et une levée en base n'agit qu'au redémarrage du worldserver, les
/// liens de tous les personnages étant chargés en mémoire au démarrage. Les deux points sont
/// écrits dans l'interface.
/// </summary>
public partial class InstanceLocksViewModel : ObservableObject
{
    private readonly InstanceLockService _locks;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    public ObservableCollection<InstanceLock> Locks { get; } = [];
    public ObservableCollection<InstanceResetSchedule> Schedules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private InstanceLock? _selected;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyPermanent;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Cherchez un personnage bloqué, ou une carte à rouvrir.";
    [ObservableProperty] private string _output = "";

    /// <summary>Passe à vrai après une levée : le rappel « redémarrage requis » s'affiche.</summary>
    [ObservableProperty] private bool _restartPending;

    public bool HasSelection => Selected is not null;

    public InstanceLocksViewModel(InstanceLockService locks, ServerContext context)
    {
        _locks = locks;
        _context = context;
    }

    private bool Ready()
    {
        if (_context.HasActive) return true;
        Status = "Aucun serveur actif.";
        return false;
    }

    // ------------------------------------------------------------------ lecture

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!Ready()) return;

        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        IsBusy = true;
        Status = "Lecture des verrous…";
        try
        {
            var previous = (Selected?.CharacterGuid, Selected?.InstanceId);
            var found = await _locks.SearchAsync(Search, OnlyPermanent, ct: cts.Token);
            if (cts.IsCancellationRequested) return;

            Locks.Clear();
            foreach (var l in found) Locks.Add(l);

            if (Schedules.Count == 0)
                foreach (var s in await _locks.ResetSchedulesAsync(cts.Token)) Schedules.Add(s);

            Status = Locks.Count == 0
                ? "Aucun verrou. Sur un serveur sans raid en cours, character_instance est vide."
                : $"{Locks.Count} verrou(s) — {Locks.Count(l => l.Permanent)} permanent(s), " +
                  $"{Locks.Select(l => l.CharacterGuid).Distinct().Count()} personnage(s).";

            Selected = Locks.FirstOrDefault(l => l.CharacterGuid == previous.Item1
                                              && l.InstanceId == previous.Item2)
                       ?? Locks.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally
        {
            if (_pending == cts) IsBusy = false;
        }
    }

    partial void OnOnlyPermanentChanged(bool value) => _ = SearchAsync();

    [RelayCommand]
    private async Task StatsAsync()
    {
        if (!Ready()) return;

        IsBusy = true;
        try
        {
            var result = await _locks.StatsAsync();
            Output = result.Output;
            Status = "État des instances relevé.";
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ levée

    private async Task ReleaseAsync(Func<Task<int>> action, string what)
    {
        var confirm = System.Windows.MessageBox.Show(
            $"Lever {what} ?\n\n" +
            "La suppression est immédiate en base, mais sans effet sur le monde en cours : " +
            "le worldserver charge tous les liens d'instance en mémoire à son démarrage et " +
            "les y conserve. Le verrou ne disparaîtra qu'après un redémarrage du worldserver.\n\n" +
            "Cela vaut aussi pour un personnage déconnecté.",
            "Lever un verrou d'instance",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var rows = await action();
            RestartPending = true;
            Status = $"{rows} verrou(s) supprimé(s) en base. " +
                     "Effet au prochain redémarrage du worldserver.";
            await SearchAsync();
        }
        catch (Exception ex)
        {
            Status = "Levée refusée : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ReleaseSelectedAsync()
    {
        if (!Ready() || Selected is not { } loc) return;
        await ReleaseAsync(() => _locks.ReleaseAsync(loc),
                           $"le verrou de {loc.CharacterText} sur {loc.MapText} (difficulté {loc.Difficulty})");
    }

    [RelayCommand]
    private async Task ReleaseCharacterAsync()
    {
        if (!Ready() || Selected is not { } loc) return;

        var count = Locks.Count(l => l.CharacterGuid == loc.CharacterGuid);
        await ReleaseAsync(() => _locks.ReleaseAllForCharacterAsync(loc.CharacterGuid, loc.CharacterText),
                           $"les {count} verrou(s) de {loc.CharacterText}");
    }

    [RelayCommand]
    private async Task ReleaseMapAsync()
    {
        if (!Ready() || Selected is not { } loc) return;

        var count = Locks.Count(l => l.MapId == loc.MapId);
        await ReleaseAsync(() => _locks.ReleaseAllForMapAsync(loc.MapId, loc.MapText),
                           $"les verrous de {loc.MapText} pour tous les joueurs " +
                           $"({count} visible(s) dans la liste actuelle)");
    }
}
