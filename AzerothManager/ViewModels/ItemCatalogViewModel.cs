using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

public sealed record NamedValue(int? Value, string Label);

/// <summary>
/// Catalogue d'objets (§19). Conçu pour servir aussi de sélecteur réutilisable :
/// le courrier, l'inventaire, la banque, le loot et l'armurerie hébergeront la même
/// vue et liront <see cref="Selected"/>.
/// </summary>
public partial class ItemCatalogViewModel : ObservableObject
{
    private readonly ItemCatalogService _catalog;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    public ObservableCollection<ItemSummary> Items { get; } = [];

    public NamedValue[] Qualities { get; } =
    [
        new(null, "Toutes qualités"),
        new(0, "Médiocre"), new(1, "Commun"), new(2, "Inhabituel"), new(3, "Rare"),
        new(4, "Épique"), new(5, "Légendaire"), new(6, "Artefact"), new(7, "Héritage")
    ];

    public NamedValue[] Classes { get; } =
    [
        new(null, "Toutes catégories"),
        new(0, "Consommable"), new(1, "Conteneur"), new(2, "Arme"), new(3, "Gemme"),
        new(4, "Armure"), new(5, "Composant"), new(6, "Projectile"), new(7, "Marchandise"),
        new(8, "Générique"), new(9, "Recette"), new(11, "Carquois"), new(12, "Quête"),
        new(13, "Clé"), new(15, "Divers"), new(16, "Glyphe")
    ];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private NamedValue? _quality;
    [ObservableProperty] private NamedValue? _class;

    /// <summary>Sous-types de la classe choisie : « Épée à deux mains » plutôt que « Arme ».</summary>
    public ObservableCollection<NamedValue> Subclasses { get; } = [];
    [ObservableProperty] private NamedValue? _subclass;
    [ObservableProperty] private string _minLevel = "";

    /// <summary>Noms traduits depuis item_template_locale. Choix persisté dans les préférences.</summary>
    [ObservableProperty] private bool _frenchNames = true;
    [ObservableProperty] private string _maxLevel = "";
    [ObservableProperty] private ItemSummary? _selected;

    /// <summary>Fiche détaillée, ouverte au double-clic et mise à jour tant qu'elle reste visible.</summary>
    [ObservableProperty] private ItemDetail? _detail;
    [ObservableProperty] private bool _detailVisible;

    [ObservableProperty] private string? _sortColumn;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Saisissez un nom ou un identifiant, puis lancez la recherche.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount = 1;

    public int PageSize { get; } = 100;
    public string PageLabel => $"Page {Page + 1} / {PageCount}";

    public ItemCatalogViewModel(ItemCatalogService catalog, ServerContext context)
    {
        _catalog = catalog;
        _context = context;
        Quality = Qualities[0];
        Class = Classes[0];
        FrenchNames = LocalDatabase.GetSetting("catalog.locale", "frFR") == "frFR";
        RefreshSubclasses();
    }

    partial void OnClassChanged(NamedValue? value) => RefreshSubclasses();

    /// <summary>
    /// La liste des sous-types dépend de la classe : les valeurs numériques n'ont pas
    /// le même sens d'une classe à l'autre, 7 vaut « Épée » pour une arme et « Libram »
    /// pour une armure.
    /// </summary>
    private void RefreshSubclasses()
    {
        Subclasses.Clear();
        Subclasses.Add(new NamedValue(null, "Tous les types"));

        if (Class?.Value is { } c)
            foreach (var (value, label) in ItemReference.Subclasses(c))
                Subclasses.Add(new NamedValue(value, label));

        Subclass = Subclasses[0];
    }

    partial void OnFrenchNamesChanged(bool value) =>
        LocalDatabase.SetSetting("catalog.locale", value ? "frFR" : "");

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

        // Une frappe rapide ne doit pas empiler les requêtes sur une table de 46 000 lignes.
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        IsBusy = true;
        Status = "Recherche…";
        try
        {
            var filter = new ItemFilter(
                Text: string.IsNullOrWhiteSpace(Search) ? null : Search,
                Quality: Quality?.Value,
                Class: Class?.Value,
                Subclass: Subclass?.Value,
                MinItemLevel: int.TryParse(MinLevel, out var min) ? min : null,
                MaxItemLevel: int.TryParse(MaxLevel, out var max) ? max : null,
                Locale: FrenchNames ? "frFR" : null,
                SortColumn: SortColumn,
                SortDescending: SortDescending,
                Page: Page,
                PageSize: PageSize);

            var result = await _catalog.SearchAsync(filter, cts.Token);
            if (cts.IsCancellationRequested) return;

            Items.Clear();
            foreach (var i in result.Items) Items.Add(i);

            PageCount = result.PageCount;
            Status = result.Total == 0
                ? "Aucun objet ne correspond."
                : $"{result.Total} objet(s) — {result.Items.Count} affiché(s)";
        }
        catch (OperationCanceledException)
        {
            // Recherche remplacée par une plus récente : rien à signaler.
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

    /// <summary>Tri demandé par un clic sur un en-tête. Relancé côté SQL : trier la page
    /// courante en mémoire ne classerait que cent lignes sur quarante-six mille.</summary>
    public async Task SortByAsync(string column)
    {
        if (SortColumn == column) SortDescending = !SortDescending;
        else { SortColumn = column; SortDescending = false; }
        Page = 0;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ShowDetailAsync()
    {
        if (Selected is null) return;
        DetailVisible = true;
        await LoadDetailAsync();
    }

    [RelayCommand]
    private void CloseDetail() => DetailVisible = false;

    partial void OnSelectedChanged(ItemSummary? value)
    {
        if (DetailVisible) _ = LoadDetailAsync();
    }

    private async Task LoadDetailAsync()
    {
        if (Selected is null) { Detail = null; return; }
        try
        {
            Detail = await _catalog.GetDetailAsync(Selected.Entry, FrenchNames ? "frFR" : null);
        }
        catch (Exception ex)
        {
            Status = "Fiche indisponible : " + ex.Message;
        }
    }

    /// <summary>Copie l'identifiant, le geste le plus fréquent une fois l'objet trouvé.</summary>
    [RelayCommand]
    private void CopyEntry()
    {
        if (Selected is null) return;
        System.Windows.Clipboard.SetText(Selected.Entry.ToString());
        Status = $"Identifiant {Selected.Entry} copié.";
    }
}
