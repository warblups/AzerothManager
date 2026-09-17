using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>Carte des joueurs connectés, par continent.</summary>
public partial class PlayerMapViewModel : ObservableObject
{
    private readonly PlayerMapService _map;
    private readonly ServerContext _context;
    private IReadOnlyList<PlayerPosition> _placed = [];

    public Continent[] Continents => Continent.All;

    /// <summary>Joueurs du continent affiché.</summary>
    public ObservableCollection<PlayerPosition> Markers { get; } = [];

    /// <summary>Joueurs en instance ou sur une carte sans fond : non projetables.</summary>
    public ObservableCollection<PlayerPosition> Elsewhere { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapImage))]
    [NotifyPropertyChangedFor(nameof(MapMissing))]
    private Continent? _continent;

    [ObservableProperty] private bool _includeOffline;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargez la position des joueurs.";

    public double MapWidth => Continent.Width;
    public double MapHeight => Continent.Height;

    public bool MapMissing => Continent is not null && !PlayerMapService.HasMap(Continent);

    public string MapsFolder => PlayerMapService.MapsFolder;

    public BitmapImage? MapImage
    {
        get
        {
            if (Continent is null || !PlayerMapService.HasMap(Continent)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(PlayerMapService.MapFile(Continent));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }

    public PlayerMapViewModel(PlayerMapService map, ServerContext context)
    {
        _map = map;
        _context = context;
        Continent = Continent.All[0];
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
            var (placed, elsewhere) = await _map.OnlinePlayersAsync(IncludeOffline);
            _placed = placed;

            Elsewhere.Clear();
            foreach (var p in elsewhere) Elsewhere.Add(p);

            RefreshMarkers();

            var total = placed.Count + elsewhere.Count;
            Status = total == 0
                ? IncludeOffline ? "Aucun personnage." : "Aucun joueur connecté."
                : $"{total} personnage(s) — {elsewhere.Count} hors des quatre continents";
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private void RefreshMarkers()
    {
        Markers.Clear();
        if (Continent is null) return;
        foreach (var p in _placed.Where(p => p.MapId == Continent.MapId)) Markers.Add(p);
    }

    partial void OnContinentChanged(Continent? value) => RefreshMarkers();

    partial void OnIncludeOfflineChanged(bool value) => _ = LoadAsync();

    /// <summary>Ouvre le dossier des cartes, pour y déposer celles que le script a extraites.</summary>
    [RelayCommand]
    private void OpenMapsFolder()
    {
        Directory.CreateDirectory(PlayerMapService.MapsFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PlayerMapService.MapsFolder,
            UseShellExecute = true
        });
    }
}
