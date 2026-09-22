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

    /// <summary>
    /// Chargement en cours, annulable.
    ///
    /// Cocher « inclure les hors ligne » lance un chargement sans l'attendre : sans ce
    /// garde, un second chargement demandé entre-temps vide et remplit les mêmes
    /// collections pendant que le premier les parcourt encore.
    /// </summary>
    private CancellationTokenSource? _pending;

    public Continent[] Continents => Continent.All;

    /// <summary>Joueurs du continent affiché.</summary>
    public ObservableCollection<PlayerPosition> Markers { get; } = [];

    /// <summary>Joueurs en instance ou sur une carte sans fond : non projetables.</summary>
    public ObservableCollection<PlayerPosition> Elsewhere { get; } = [];

    /// <summary>Effectifs par zone du continent affiché.</summary>
    public ObservableCollection<ZonePopulation> ZoneBubbles { get; } = [];

    /// <summary>Toutes les zones peuplées, tous continents confondus.</summary>
    public ObservableCollection<ZonePopulation> AllZones { get; } = [];

    /// <summary>Joueurs de la zone retenue : le détail derrière le compteur d'une bulle.</summary>
    public ObservableCollection<PlayerPosition> ZonePlayers { get; } = [];

    /// <summary>Les quatre continents juxtaposés, pour la vue générale.</summary>
    public ObservableCollection<ContinentPanel> Panels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapImage))]
    [NotifyPropertyChangedFor(nameof(MapMissing))]
    private Continent? _continent;

    [ObservableProperty] private bool _includeOffline;

    /// <summary>
    /// Zone dont on regarde le détail. Une bulle annonce un effectif ; cliquer dessus
    /// répond à la question suivante, qui est toujours « qui ? ».
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZonePlayersHeight))]
    [NotifyPropertyChangedFor(nameof(ZonePlayersTitle))]
    [NotifyPropertyChangedFor(nameof(HasSelectedZone))]
    private ZonePopulation? _selectedZone;

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
    public bool HasSelectedZone => SelectedZone is not null;

    /// <summary>La section ne prend de la place que lorsqu'une zone est retenue.</summary>
    public System.Windows.GridLength ZonePlayersHeight => SelectedZone is null
        ? new System.Windows.GridLength(0)
        : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);

    public string ZonePlayersTitle => SelectedZone is null
        ? ""
        : $"{SelectedZone.ZoneName} — {SelectedZone.Total} joueur(s)";

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

        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        IsBusy = true;
        try
        {
            var (placed, elsewhere) = await _map.OnlinePlayersAsync(IncludeOffline);
            var zones = await _map.ZonePopulationAsync(IncludeOffline);

            // Un chargement plus récent a été demandé : le sien fait foi, et toucher aux
            // collections ici les laisserait à moitié remplies.
            if (cts.IsCancellationRequested) return;

            _placed = placed;
            _zones = zones;

            Elsewhere.Clear();
            foreach (var p in elsewhere) Elsewhere.Add(p);

            AllZones.Clear();
            foreach (var z in _zones) AllZones.Add(z);

            RefreshMarkers();

            // La zone retenue est reprise sur les données fraîches : garder l'ancien objet
            // afficherait des effectifs périmés à côté d'une liste à jour.
            SelectedZone = SelectedZone is { } previous
                ? _zones.FirstOrDefault(z => z.ZoneId == previous.ZoneId)
                : null;

            var total = placed.Count + elsewhere.Count;
            Status = total == 0
                ? IncludeOffline ? "Aucun personnage." : "Aucun joueur connecté."
                : $"{total} personnage(s) sur {_zones.Count} zone(s) — " +
                  $"{elsewhere.Count} hors des quatre continents";
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

    private void RefreshMarkers()
    {
        Markers.Clear();
        ZoneBubbles.Clear();

        if (Continent is not null)
        {
            foreach (var p in _placed.Where(p => p.MapId == Continent.MapId)) Markers.Add(p);

            // Une zone sans rectangle n'est pas projetable : elle reste dans le tableau,
            // mais on ne l'invente pas sur la carte.
            foreach (var z in _zones.Where(z => z.MapId == Continent.MapId && z.Total > 0 && z.Projected))
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
                Bubbles = [.. _zones.Where(z => z.MapId == continent.MapId && z.Total > 0 && z.Projected)],
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

    /// <summary>
    /// Les joueurs sont cherchés dans les deux collections : une zone peuplée peut être
    /// non projetable — les départs draeneï et elfe de sang, par exemple —, et ses joueurs
    /// se trouvent alors avec les autres, pas parmi les marqueurs de la carte.
    /// </summary>
    partial void OnSelectedZoneChanged(ZonePopulation? value)
    {
        ZonePlayers.Clear();
        if (value is null) return;

        foreach (var p in _placed.Concat(Elsewhere)
                                 .Where(p => p.ZoneId == value.ZoneId)
                                 .OrderByDescending(p => p.Level)
                                 .ThenBy(p => p.Name))
            ZonePlayers.Add(p);
    }

    [RelayCommand]
    private void SelectZone(ZonePopulation? zone) => SelectedZone = zone;

    [RelayCommand]
    private void ClearZone() => SelectedZone = null;

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
