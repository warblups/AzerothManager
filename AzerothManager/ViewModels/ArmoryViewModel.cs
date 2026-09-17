using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>Armurerie (§12) : fiche de personnage complète, en lecture seule.</summary>
public partial class ArmoryViewModel : ObservableObject
{
    private readonly ArmoryService _armory;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    public ObservableCollection<CharacterListItem> Characters { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CharacterListItem? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    [NotifyPropertyChangedFor(nameof(StatsWarning))]
    private CharacterSheet? _sheet;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Recherchez un personnage, ou laissez vide pour tout lister.";

    public bool HasSelection => Selected is not null;
    public bool HasStats => Sheet?.Stats is not null;

    /// <summary>
    /// character_stats n'est écrite qu'à la déconnexion du personnage : elle peut être
    /// absente ou périmée. Mieux vaut le dire que d'afficher des zéros trompeurs.
    /// </summary>
    public string StatsWarning => Sheet is null
        ? ""
        : Sheet.Stats is null
            ? "Aucune statistique enregistrée : la table character_stats n'est écrite qu'à la déconnexion du personnage."
            : Sheet.Online
                ? "Statistiques datant de la dernière déconnexion : le personnage est connecté, les valeurs vivantes peuvent différer."
                : "";

    public ArmoryViewModel(ArmoryService armory, ServerContext context)
    {
        _armory = armory;
        _context = context;
    }

    [RelayCommand]
    private async Task SearchAsync()
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
        Status = "Recherche…";
        try
        {
            var list = await _armory.SearchAsync(Search, OnlyOnline, ct: cts.Token);
            if (cts.IsCancellationRequested) return;

            Characters.Clear();
            foreach (var c in list) Characters.Add(c);

            Status = list.Count == 0 ? "Aucun personnage ne correspond." : $"{list.Count} personnage(s)";
            Selected = Characters.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally
        {
            if (_pending == cts) IsBusy = false;
        }
    }

    partial void OnSelectedChanged(CharacterListItem? value) => _ = LoadSheetAsync();

    private async Task LoadSheetAsync()
    {
        if (Selected is null)
        {
            Sheet = null;
            return;
        }

        try
        {
            Sheet = await _armory.GetSheetAsync(Selected.Guid, "frFR");
        }
        catch (Exception ex)
        {
            Sheet = null;
            Status = "Fiche indisponible : " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadSheetAsync();
}
