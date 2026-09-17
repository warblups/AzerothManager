using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Bibliothèque de téléportation (§10). Gère les points ; téléporter un joueur reste
/// dans le module Personnages, qui a la liste des personnages sous la main.
/// </summary>
public partial class TeleportViewModel : ObservableObject
{
    private readonly TeleportService _teleport;
    private readonly ServerContext _context;

    public ObservableCollection<TeleportPoint> Points { get; } = [];
    public ObservableCollection<NamedMap> Maps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private TeleportPoint? _selected;

    [ObservableProperty] private NamedMap? _map;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargez la bibliothèque.";

    /// <summary>Un .reload est dû après toute écriture dans world.game_tele (§9).</summary>
    [ObservableProperty] private bool _reloadPending;

    // Saisie du point en cours d'édition
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editMap = "0";
    [ObservableProperty] private string _editX = "";
    [ObservableProperty] private string _editY = "";
    [ObservableProperty] private string _editZ = "";
    [ObservableProperty] private string _editOrientation = "0";

    [ObservableProperty] private string _teleportTarget = "";

    public bool HasSelection => Selected is not null;

    public TeleportViewModel(TeleportService teleport, ServerContext context)
    {
        _teleport = teleport;
        _context = context;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!_context.HasActive)
        {
            Status = "Aucun serveur actif.";
            return;
        }

        IsBusy = true;
        try
        {
            if (Maps.Count == 0)
            {
                foreach (var m in await _teleport.MapsAsync()) Maps.Add(m);
            }

            var previous = Selected?.Id;
            Points.Clear();
            foreach (var p in await _teleport.SearchAsync(Search, Map?.MapId)) Points.Add(p);

            Status = Points.Count == 0
                ? "Aucune destination ne correspond."
                : $"{Points.Count} destination(s) — 500 au maximum par recherche";
            Selected = Points.FirstOrDefault(p => p.Id == previous) ?? Points.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedChanged(TeleportPoint? value)
    {
        if (value is null) return;
        EditName = value.Name;
        EditMap = value.MapId.ToString();
        EditX = value.X.ToString("0.###");
        EditY = value.Y.ToString("0.###");
        EditZ = value.Z.ToString("0.###");
        EditOrientation = value.Orientation.ToString("0.###");
    }

    private bool TryBuild(int id, out TeleportPoint point)
    {
        point = null!;
        if (string.IsNullOrWhiteSpace(EditName))
        {
            Status = "Le nom est obligatoire.";
            return false;
        }
        if (!int.TryParse(EditMap, out var map) ||
            !float.TryParse(EditX, out var x) ||
            !float.TryParse(EditY, out var y) ||
            !float.TryParse(EditZ, out var z))
        {
            Status = "Carte et coordonnées doivent être numériques.";
            return false;
        }
        float.TryParse(EditOrientation, out var o);

        point = new TeleportPoint(id, EditName.Trim(), map, "", x, y, z, o);
        return true;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!TryBuild(0, out var point)) return;

        IsBusy = true;
        try
        {
            await _teleport.AddAsync(point);
            ReloadPending = true;
            Status = $"« {point.Name} » ajouté. Un .reload game_tele est nécessaire pour l'utiliser.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = "Ajout refusé : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (Selected is null || !TryBuild(Selected.Id, out var point)) return;

        IsBusy = true;
        try
        {
            var rows = await _teleport.UpdateAsync(point);
            ReloadPending = true;
            Status = $"{rows} ligne(s) modifiée(s). Un .reload game_tele est nécessaire.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = "Modification refusée : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;

        var confirm = System.Windows.MessageBox.Show(
            $"Supprimer la destination « {name} » ?",
            "Confirmation", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var r = await _teleport.DeleteAsync(name);
            Status = r.Success
                ? $"« {name} » supprimée : {r.Output.ReplaceLineEndings(" ").Trim()}"
                : $"Suppression refusée — {r.Output}";
            if (r.Success) await LoadAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var r = await _teleport.ReloadAsync();
            Status = r.Success
                ? $".reload game_tele : {r.Output.ReplaceLineEndings(" ").Trim()}"
                : $".reload game_tele a échoué — {r.Output}";
            if (r.Success) ReloadPending = false;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(TeleportTarget))
        {
            Status = "Indiquez le personnage à téléporter.";
            return;
        }

        var target = TeleportTarget.Trim();
        var destination = Selected.Name;

        IsBusy = true;
        try
        {
            var r = await _teleport.TeleportAsync(target, destination);
            Status = r.Success
                ? $"{target} téléporté vers {destination}."
                : $"Téléportation refusée — {r.Output}";
        }
        finally { IsBusy = false; }
    }
}
