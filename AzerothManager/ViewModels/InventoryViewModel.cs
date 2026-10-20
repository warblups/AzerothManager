using System.Collections.ObjectModel;
using System.Windows;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>Filtre de section ; `Section` nul signifie « tout ».</summary>
public sealed record SectionFilter(string Label, InventorySection? Section);

/// <summary>
/// Inventaire et banque (§8). Répond à « j'ai perdu mon objet » : où est-il — poches, banque,
/// courrier, hôtel des ventes —, et s'il n'est nulle part, le serveur l'a-t-il archivé ?
/// </summary>
public partial class InventoryViewModel : ObservableObject
{
    private readonly ArmoryService _armory;
    private readonly InventoryService _inventory;
    private readonly ServerContext _context;
    private readonly Func<ItemCatalogViewModel> _catalogFactory;

    private CancellationTokenSource? _searching;
    private CancellationTokenSource? _loading;
    private List<InventoryItem> _all = [];

    public ObservableCollection<CharacterListItem> Characters { get; } = [];
    public ObservableCollection<InventoryItem> Items { get; } = [];
    public ObservableCollection<RecoveryItem> Recoveries { get; } = [];

    public IReadOnlyList<SectionFilter> Sections { get; } =
    [
        new("Tout", null),
        .. Enum.GetValues<InventorySection>().Select(s => new SectionFilter(InventoryLayout.SectionName(s), s))
    ];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(OnlineText))]
    private CharacterListItem? _selected;

    [ObservableProperty] private InventoryItem? _selectedItem;
    [ObservableProperty] private RecoveryItem? _selectedRecovery;
    [ObservableProperty] private SectionFilter? _selectedSection;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _quantity = "1";

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _recoveryConfig = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Recherchez un personnage, ou laissez vide pour tout lister.";

    public bool HasSelection => Selected is not null;

    public string OnlineText => Selected is null
        ? ""
        : Selected.Online
            ? "Connecté : un objet donné arrive directement dans ses sacs."
            : "Déconnecté : un objet donné part par courrier, .additem exigeant un joueur connecté.";

    public InventoryViewModel(ArmoryService armory, InventoryService inventory, ServerContext context,
                              Func<ItemCatalogViewModel> catalogFactory)
    {
        _armory = armory;
        _inventory = inventory;
        _context = context;
        _catalogFactory = catalogFactory;
        _selectedSection = Sections[0];
    }

    private bool Ready()
    {
        if (_context.HasActive) return true;
        Status = "Aucun serveur actif.";
        return false;
    }

    // ------------------------------------------------------------------ consultation

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!Ready()) return;

        _searching?.Cancel();
        var cts = new CancellationTokenSource();
        _searching = cts;

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
            _ = LoadConfigAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally
        {
            if (_searching == cts) IsBusy = false;
        }
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            var config = await _inventory.ReadItemDeleteConfigAsync();
            RecoveryConfig = config?.Summary
                ?? "Configuration du serveur illisible : renseignez le chemin du serveur dans le profil pour " +
                   "savoir si les objets détruits sont archivés (réglages ItemDelete.* de worldserver.conf).";
        }
        catch (Exception ex)
        {
            RecoveryConfig = "Configuration du serveur illisible : " + ex.Message;
        }
    }

    partial void OnSelectedChanged(CharacterListItem? value) => _ = LoadAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadAsync();
        await LoadConfigAsync();
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();

        if (Selected is not { } character)
        {
            _all = [];
            ApplyFilter();
            Recoveries.Clear();
            Summary = "";
            return;
        }

        var cts = new CancellationTokenSource();
        _loading = cts;

        IsBusy = true;
        try
        {
            var (items, auctionTotal) = await _inventory.LoadAsync(character.Guid, cts.Token);
            var recoveries = await _inventory.RecoveryAsync(character.Guid, cts.Token);

            // Un autre personnage a été choisi entre-temps : c'est son chargement qui fait foi.
            if (cts.IsCancellationRequested) return;

            _all = [.. items.OrderBy(i => i.Section).ThenBy(i => i.SortKey)];
            ApplyFilter();

            Recoveries.Clear();
            foreach (var r in recoveries) Recoveries.Add(r);

            Summary = BuildSummary(auctionTotal);
            Status = $"Inventaire de {character.Name} lu.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "Lecture impossible : " + ex.Message;
        }
        finally
        {
            if (_loading == cts) IsBusy = false;
        }
    }

    private string BuildSummary(int auctionTotal)
    {
        int Count(InventorySection s) => _all.Count(i => i.Section == s);

        var auctions = Count(InventorySection.Auction);
        var parts = new List<string>
        {
            $"{Count(InventorySection.Equipped)} équipé(s)",
            $"{Count(InventorySection.Bags)} dans les sacs",
            $"{Count(InventorySection.Bank)} en banque",
            $"{Count(InventorySection.Mail)} au courrier",
            auctionTotal > auctions
                ? $"{auctionTotal} en vente, {auctions} affichées"
                : $"{auctions} en vente"
        };
        var keys = Count(InventorySection.Keyring) + Count(InventorySection.Currency);
        if (keys > 0) parts.Add($"{keys} au trousseau et en monnaies");
        return string.Join(" · ", parts);
    }

    partial void OnSelectedSectionChanged(SectionFilter? value) => ApplyFilter();
    partial void OnFilterChanged(string value) => ApplyFilter();

    /// <summary>Filtre la fenêtre déjà lue : un inventaire tient en quelques centaines de lignes.</summary>
    private void ApplyFilter()
    {
        var section = SelectedSection?.Section;
        var text = Filter.Trim();

        Items.Clear();
        foreach (var item in _all)
        {
            if (section is not null && item.Section != section) continue;
            if (text.Length > 0
                && !item.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                && item.Entry.ToString() != text) continue;
            Items.Add(item);
        }
    }

    partial void OnSelectedItemChanged(InventoryItem? value)
    {
        if (value is not null) Quantity = value.Count.ToString();
    }

    // ------------------------------------------------------------------ écritures

    /// <summary>
    /// Retrait par `.additem <joueur> <id> -<n>`. Le serveur retire **par modèle** : la pile
    /// sélectionnée ne sert qu'à choisir l'objet et une quantité par défaut, et la
    /// confirmation dit combien d'exemplaires sont concernés en tout.
    /// </summary>
    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (!Ready() || Selected is not { } character) return;
        if (SelectedItem is not { } item)
        {
            Status = "Sélectionnez l'objet à retirer.";
            return;
        }
        if (!item.IsCarried)
        {
            Status = "Cet objet n'est pas dans les poches du personnage : .additem ne touche ni au " +
                     "courrier ni à l'hôtel des ventes.";
            return;
        }
        if (item.IsContainer && item.ContentCount > 0)
        {
            Status = $"Ce sac contient {item.ContentCount} objet(s). Hors ligne, le serveur refuse de retirer " +
                     "un sac non vide : faites-le vider d'abord, pour que le résultat ne dépende pas de " +
                     "l'état du joueur.";
            return;
        }

        var held = _all.Where(i => i.IsCarried && i.Entry == item.Entry).Sum(i => i.Count);
        if (!int.TryParse(Quantity, out var count) || count < 1 || count > held)
        {
            Status = $"Quantité invalide : entre 1 et {held}, le nombre d'exemplaires dans les poches.";
            return;
        }

        var order = character.Online
            ? "Le personnage est connecté : le serveur prend dans l'ordre sac à dos, trousseau, sacs, " +
              "équipement, puis banque."
            : "Le personnage est déconnecté : le serveur écrit directement en base, sans ordre garanti " +
              "entre les piles.";

        var confirm = MessageBox.Show(
            $"Retirer {count} × {item.Name} (modèle {item.Entry}) à {character.Name} ?\n\n" +
            $"Le serveur retire par modèle, pas par pile : {held} exemplaire(s) au total dans les " +
            $"poches et la banque. {order}\n\n" +
            "Le courrier et l'hôtel des ventes ne sont pas touchés. Aucune annulation possible : " +
            "seul un renvoi par courrier rendrait l'objet.",
            "Retirer un objet",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        await RunAsync(() => _inventory.RemoveAsync(character.Name, item.Entry, count, item.Name),
                       $"{count} × {item.Name} retiré(s) à {character.Name}.");
    }

    /// <summary>
    /// Donne un objet choisi dans le catalogue. `.additem` exige un joueur connecté ;
    /// hors ligne, l'objet part par courrier plutôt que d'échouer.
    /// </summary>
    [RelayCommand]
    private async Task GiveAsync()
    {
        if (!Ready() || Selected is not { } character) return;

        var picker = new Views.ItemPickerWindow(_catalogFactory())
        {
            Owner = Application.Current.MainWindow
        };
        if (picker.ShowDialog() != true || picker.PickedItem is not { } picked) return;

        var count = picker.PickedCount;

        if (character.Online)
        {
            await RunAsync(() => _inventory.AddAsync(character.Name, picked.Entry, count, picked.Name),
                           $"{count} × {picked.Name} ajouté(s) dans les sacs de {character.Name}.",
                           "S'il vient de se déconnecter, relancez la recherche : l'objet partira par courrier.");
            return;
        }

        IsBusy = true;
        try
        {
            var report = await _inventory.MailAsync(character.Name, picked.Entry, count, picked.Name,
                                                    picked.Quality, picked.DisplayId);
            Status = report.Failed == 0
                ? $"{character.Name} est déconnecté : {count} × {picked.Name} envoyé(s) par courrier."
                : "Refusé : " + report.Message;
            if (report.Failed == 0) await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!Ready() || Selected is not { } character) return;
        if (SelectedRecovery is not { } item)
        {
            Status = "Sélectionnez l'objet archivé à restaurer.";
            return;
        }

        await RunAsync(() => _inventory.RestoreAsync(character.Name, item),
                       $"{item.Count} × {item.Name} renvoyé(s) par courrier à {character.Name}.");
    }

    private async Task RunAsync(Func<Task<GmCommandResult>> action, string done, string? hintOnFailure = null)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            Status = result.Success
                ? done
                : "Refusé : " + result.Output + (hintOnFailure is null ? "" : " " + hintOnFailure);
            if (result.Success) await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
