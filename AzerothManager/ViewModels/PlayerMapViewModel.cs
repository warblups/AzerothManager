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
    private IReadOnlyList<ZonePopulation> _zones = [];

    public Continent[] Continents => Continent.All;

    /// <summary>Joueurs du continent affiché.</summary>
    public ObservableCollection<PlayerPosition> Markers { get; } = [];

    /// <summary>Joueurs en instance ou sur une carte sans fond : non projetables.</summary>
    public ObservableCollection<PlayerPosition> Elsewhere { get; } = [];

    /// <summary>Effectifs par zone du continent affiché.</summary>
    public ObservableCollection<ZonePopulation> ZoneBubbles { get; } = [];

    /// <summary>Toutes les zones peuplées, tous continents confondus.</summary>
    public ObservableCollection<ZonePopulation> AllZones { get; } = [];

    /// <summary>Les quatre continents juxtaposés, pour la vue générale.</summary>
    public ObservableCollection<ContinentPanel> Panels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapImage))]
    [NotifyPropertyChangedFor(nameof(MapMissing))]
    private Continent? _continent;

    [ObservableProperty] private bool _includeOffline;

    /// <summary>
    /// Vue générale : les quatre continents côte à côte. Chacun conserve sa propre
    /// projection plutôt que d'en inventer une mondiale, faute de pouvoir la vérifier.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSingle))]
    private bool _overview;

    /// <summary>Bascule entre les joueurs un par un et les effectifs par zone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlayers))]
    private bool _showZones = true;

    /// <summary>Repli du panneau latéral, pour laisser toute la place à la carte.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelWidth))]
    [NotifyPropertyChangedFor(nameof(PanelToggleLabel))]
    private bool _panelCollapsed;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargez la position des joueurs.";

    public bool ShowPlayers => !ShowZones;
    public bool ShowSingle => !Overview;

    public System.Windows.GridLength PanelWidth =>
        PanelCollapsed ? new System.Windows.GridLength(0) : new System.Windows.GridLength(300);

    public string PanelToggleLabel => PanelCollapsed ? "◀  Détails" : "▶  Réduire";

    public double MapWidth => Continent.Width;
    public double MapHeight => Continent.Height;

    public bool MapMissing => Continent is not null && !PlayerMapService.HasMap(Continent);

    public string MapsFolder => PlayerMapService.MapsFolder;

    public BitmapImage? MapImage
    {
        get
        {
            return Continent is null ? null : LoadMap(Continent);
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
            _zones = await _map.ZonePopulationAsync(IncludeOffline);

            Elsewhere.Clear();
            foreach (var p in elsewhere) Elsewhere.Add(p);

            AllZones.Clear();
            foreach (var z in _zones) AllZones.Add(z);

            RefreshMarkers();

            var total = placed.Count + elsewhere.Count;
            Status = total == 0
                ? IncludeOffline ? "Aucun personnage." : "Aucun joueur connecté."
                : $"{total} personnage(s) sur {_zones.Count} zone(s) — " +
                  $"{elsewhere.Count} hors des quatre continents";
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
        ZoneBubbles.Clear();

        if (Continent is not null)
        {
            foreach (var p in _placed.Where(p => p.MapId == Continent.MapId)) Markers.Add(p);

            // Une zone sans rectangle n'est pas projetable : elle reste dans le tableau,
            // mais on ne l'invente pas sur la carte.
            foreach (var z in _zones.Where(z => z.MapId == Continent.MapId && z.Total > 0 && z.X > 0))
                ZoneBubbles.Add(z);
        }

        RefreshPanels();
    }

    private void RefreshPanels()
    {
        Panels.Clear();
        foreach (var continent in Continent.All)
        {
            Panels.Add(new ContinentPanel
            {
                Continent = continent,
                Image = LoadMap(continent),
                Bubbles = [.. _zones.Where(z => z.MapId == continent.MapId && z.Total > 0 && z.X > 0)],
                Players = [.. _placed.Where(p => p.MapId == continent.MapId)]
            });
        }
    }

    private static BitmapImage? LoadMap(Continent continent)
    {
        if (!PlayerMapService.HasMap(continent)) return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(PlayerMapService.MapFile(continent));
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    partial void OnContinentChanged(Continent? value) => RefreshMarkers();

    partial void OnIncludeOfflineChanged(bool value) => _ = LoadAsync();

    partial void OnShowZonesChanged(bool value) => RefreshMarkers();

    partial void OnOverviewChanged(bool value) => RefreshPanels();

    [RelayCommand]
    private void TogglePanel() => PanelCollapsed = !PanelCollapsed;

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
