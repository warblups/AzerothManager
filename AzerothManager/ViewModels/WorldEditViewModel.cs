using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Édition du monde (§10) : recherche et modification des spawns de `world.creature` et
/// `world.gameobject`.
///
/// Le module assume ce qu'il ne peut pas faire. Les commandes de placement (`.npc add`,
/// `.npc move`, `.gobject add`, `.wp add`) sont Console::No et donc hors de portée de SOAP ;
/// et aucun `.reload` ne recharge les spawns, donc une modification n'est visible qu'après
/// redémarrage du worldserver. Les deux points sont écrits dans l'interface.
/// </summary>
public partial class WorldEditViewModel : ObservableObject
{
    private readonly WorldEditService _world;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    public ObservableCollection<Spawn> Spawns { get; } = [];
    public ObservableCollection<NamedMap> Maps { get; } = [];

    /// <summary>Les deux tables ont des colonnes et des tables satellites distinctes (§10).</summary>
    public IReadOnlyList<NamedValue> Kinds { get; } =
    [
        new((int)SpawnKind.Creature, "PNJ (world.creature)"),
        new((int)SpawnKind.GameObject, "Objets (world.gameobject)"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Spawn? _selected;

    [ObservableProperty] private NamedValue? _kind;
    [ObservableProperty] private NamedMap? _map;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Cherchez un PNJ ou un objet par nom, par identifiant de modèle ou par guid.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount = 1;

    // Saisie du spawn sélectionné
    [ObservableProperty] private string _editX = "";
    [ObservableProperty] private string _editY = "";
    [ObservableProperty] private string _editZ = "";
    [ObservableProperty] private string _editOrientation = "";
    [ObservableProperty] private string _editSpawnTime = "";
    [ObservableProperty] private string _editComment = "";

    /// <summary>Passe à vrai après une écriture : le rappel « redémarrage requis » s'affiche.</summary>
    [ObservableProperty] private bool _restartPending;

    public bool HasSelection => Selected is not null;
    public int PageSize { get; } = 200;
    public string PageLabel => $"Page {Page + 1} / {PageCount}";

    private SpawnKind CurrentKind => (SpawnKind)(Kind?.Value ?? (int)SpawnKind.Creature);

    public WorldEditViewModel(WorldEditService world, ServerContext context)
    {
        _world = world;
        _context = context;
        Kind = Kinds[0];
    }

    // ------------------------------------------------------------------ recherche

    /// <summary>Changer de table invalide la liste des cartes et la page courante.</summary>
    partial void OnKindChanged(NamedValue? value)
    {
        Maps.Clear();
        Map = null;
        Spawns.Clear();
        Page = 0;
        PageCount = 1;
        Status = CurrentKind == SpawnKind.Creature
            ? "Table world.creature — 155 085 spawns sur le serveur de référence."
            : "Table world.gameobject — 97 426 spawns sur le serveur de référence.";
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        Page = 0;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (Page + 1 >= PageCount) return;
        Page++;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (Page == 0) return;
        Page--;
        await LoadAsync();
    }

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
        Status = "Recherche…";
        try
        {
            if (Maps.Count == 0)
            {
                foreach (var m in await _world.MapsAsync(CurrentKind, cts.Token)) Maps.Add(m);
            }

            var filter = new SpawnFilter(
                Kind: CurrentKind,
                Text: string.IsNullOrWhiteSpace(Search) ? null : Search,
                MapId: Map?.MapId,
                Page: Page,
                PageSize: PageSize);

            var result = await _world.SearchAsync(filter, cts.Token);
            if (cts.IsCancellationRequested) return;

            var previous = Selected?.Guid;
            Spawns.Clear();
            foreach (var s in result.Spawns) Spawns.Add(s);

            PageCount = result.PageCount;
            Status = result.Total == 0
                ? "Aucun spawn ne correspond."
                : $"{result.Total} spawn(s) — {Spawns.Count} affiché(s)";
            Selected = Spawns.FirstOrDefault(s => s.Guid == previous) ?? Spawns.FirstOrDefault();
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

    partial void OnSelectedChanged(Spawn? value)
    {
        if (value is null) return;
        EditX = value.X.ToString("0.###");
        EditY = value.Y.ToString("0.###");
        EditZ = value.Z.ToString("0.###");
        EditOrientation = value.Orientation.ToString("0.###");
        EditSpawnTime = value.SpawnTimeSecs.ToString();
        EditComment = value.Comment;
    }

    // ------------------------------------------------------------------ écriture

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Selected is null) return;

        if (!float.TryParse(EditX, out var x) ||
            !float.TryParse(EditY, out var y) ||
            !float.TryParse(EditZ, out var z) ||
            !float.TryParse(EditOrientation, out var o))
        {
            Status = "Coordonnées et orientation doivent être numériques.";
            return;
        }
        if (!int.TryParse(EditSpawnTime, out var spawnTime) || spawnTime < 0)
        {
            Status = "Le temps de réapparition doit être un nombre de secondes positif.";
            return;
        }

        var spawn = Selected with
        {
            X = x, Y = y, Z = z, Orientation = o,
            SpawnTimeSecs = spawnTime,
            Comment = EditComment ?? ""
        };

        IsBusy = true;
        try
        {
            var rows = await _world.UpdateAsync(spawn);
            RestartPending = true;
            Status = $"{spawn.Table} guid {spawn.Guid} : {rows} ligne(s) modifiée(s). " +
                     "Effet au prochain redémarrage du worldserver.";
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
        var spawn = Selected;

        var confirm = System.Windows.MessageBox.Show(
            $"Supprimer définitivement le spawn ?\n\n" +
            $"{spawn.KindText} « {spawn.Name} » (modèle {spawn.Entry}, guid {spawn.Guid})\n" +
            $"{spawn.ZoneText} — {spawn.MapName}, {spawn.PositionText}\n\n" +
            "Les lignes liées (addon, formation, respawn lié, événement, pool) sont " +
            "supprimées avec lui : une ligne orpheline provoque une erreur au chargement " +
            "du monde.\n\n" +
            "La créature reste présente en jeu jusqu'au prochain redémarrage du worldserver.",
            "Suppression d'un spawn",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var rows = await _world.DeleteAsync(spawn);
            RestartPending = true;
            Status = $"Spawn {spawn.Guid} supprimé — {rows} ligne(s) au total, " +
                     "tables liées comprises. Effet au prochain redémarrage.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = "Suppression refusée : " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
