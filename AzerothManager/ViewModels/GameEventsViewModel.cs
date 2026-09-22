using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Événements mondiaux (§9).
///
/// Le module tient les deux mécanismes séparés dans l'interface, parce qu'ils n'ont ni la
/// même portée ni la même persistance : démarrer ou arrêter agit tout de suite et ne survit
/// pas au redémarrage ; modifier la planification survit au redémarrage mais n'agit qu'à
/// partir de lui.
/// </summary>
public partial class GameEventsViewModel : ObservableObject
{
    private readonly GameEventService _events;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    /// <summary>Tous les événements lus, avant filtrage d'affichage.</summary>
    private readonly List<GameEventInfo> _loaded = [];

    public ObservableCollection<GameEventInfo> Events { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private GameEventInfo? _selected;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargez la liste des événements du serveur.";
    [ObservableProperty] private string _output = "";

    // Planification de l'événement sélectionné
    [ObservableProperty] private string _occurrence = "";
    [ObservableProperty] private string _length = "";
    [ObservableProperty] private DateTime? _start;
    [ObservableProperty] private DateTime? _end;

    /// <summary>Passe à vrai après une écriture : le rappel « redémarrage requis » s'affiche.</summary>
    [ObservableProperty] private bool _restartPending;

    public bool HasSelection => Selected is not null;

    public GameEventsViewModel(GameEventService events, ServerContext context)
    {
        _events = events;
        _context = context;
    }

    // ------------------------------------------------------------------ lecture

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_context.HasActive)
        {
            Status = "Aucun serveur actif.";
            return;
        }

        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        IsBusy = true;
        Status = "Lecture des événements…";
        try
        {
            var previous = Selected?.Id;
            var found = await _events.ListAsync(ct: cts.Token);
            if (cts.IsCancellationRequested) return;

            _loaded.Clear();
            _loaded.AddRange(found);
            ApplyFilter();

            var activeCount = _loaded.Count(e => e.IsActive);
            Status = $"{_loaded.Count} événement(s), dont {activeCount} actif(s) sur le serveur.";
            Selected = Events.FirstOrDefault(e => e.Id == previous) ?? Events.FirstOrDefault();
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

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnOnlyActiveChanged(bool value) => ApplyFilter();

    /// <summary>
    /// Filtrage en mémoire : 182 événements tiennent sans peine, et relire relancerait un
    /// `.event activelist` par frappe.
    /// </summary>
    private void ApplyFilter()
    {
        var text = Search?.Trim() ?? "";
        var kept = _loaded.Where(e =>
            (!OnlyActive || e.IsActive) &&
            (text.Length == 0
             || e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
             || e.Id.ToString() == text));

        var previous = Selected?.Id;
        Events.Clear();
        foreach (var e in kept) Events.Add(e);
        Selected = Events.FirstOrDefault(e => e.Id == previous) ?? Events.FirstOrDefault();
    }

    partial void OnSelectedChanged(GameEventInfo? value)
    {
        if (value is null) return;
        Occurrence = value.OccurrenceMinutes.ToString();
        Length = value.LengthMinutes.ToString();
        Start = value.Start;
        End = value.End;
    }

    // ------------------------------------------------------------------ actions immédiates

    [RelayCommand]
    private async Task StartAsync()
    {
        if (Selected is not { } e) return;

        IsBusy = true;
        try
        {
            var result = await _events.StartAsync(e.Id, e.Name);
            Output = result.Output;
            Status = result.Success
                ? $"Événement {e.Id} démarré. Effet immédiat, non enregistré : la planification " +
                  "reprendra la main au prochain redémarrage du worldserver."
                : "Refusé : " + result.Output;
        }
        finally { IsBusy = false; }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Selected is not { } e) return;

        IsBusy = true;
        try
        {
            var result = await _events.StopAsync(e.Id, e.Name);
            Output = result.Output;
            Status = result.Success
                ? $"Événement {e.Id} arrêté. Effet immédiat, non enregistré."
                : "Refusé : " + result.Output;
        }
        finally { IsBusy = false; }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task InfoAsync()
    {
        if (Selected is not { } e) return;

        IsBusy = true;
        try
        {
            var result = await _events.InfoAsync(e.Id);
            Output = result.Output;
            Status = result.Success ? $"Fiche de l'événement {e.Id}." : "Refusé : " + result.Output;
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ planification

    [RelayCommand]
    private async Task SaveScheduleAsync()
    {
        if (Selected is not { } e) return;

        if (!long.TryParse(Occurrence, out var occurrence) || !long.TryParse(Length, out var length))
        {
            Status = "La période et la durée sont des nombres de minutes.";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Enregistrer la planification de « {e.Name} » (événement {e.Id}) ?\n\n" +
            $"Période : {Duration.FromMinutes(occurrence)}\n" +
            $"Durée : {Duration.FromMinutes(length)}\n\n" +
            "L'écriture est immédiate en base, mais sans effet sur le monde en cours : " +
            "il n'existe aucune commande rechargeant game_event. Le worldserver ne la prendra " +
            "en compte qu'à son prochain démarrage.",
            "Modifier la planification",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var rows = await _events.UpdateScheduleAsync(e.Id, Start, End, occurrence, length);
            RestartPending = true;
            Status = $"Événement {e.Id} : {rows} ligne(s) modifiée(s). " +
                     "Effet au prochain redémarrage du worldserver.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "Modification refusée : " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
