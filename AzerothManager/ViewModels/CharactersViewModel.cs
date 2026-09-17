using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>Module Personnages (§8 et §11) : recherche en SQL, actions par commande GM.</summary>
public partial class CharactersViewModel : ObservableObject
{
    private readonly CharacterService _characters;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    public ObservableCollection<CharacterRow> Characters { get; } = [];
    public ObservableCollection<TeleportPoint> Destinations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(MoneyEditable))]
    [NotifyPropertyChangedFor(nameof(MoneyHint))]
    private CharacterRow? _selected;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyOnline;
    [ObservableProperty] private string _minLevel = "";
    [ObservableProperty] private string _maxLevel = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Lancez une recherche, ou laissez vide pour tout lister.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount = 1;

    // Saisies des actions
    [ObservableProperty] private string _newLevel = "";
    [ObservableProperty] private string _destinationSearch = "";
    [ObservableProperty] private TeleportPoint? _destination;
    [ObservableProperty] private string _gold = "";
    [ObservableProperty] private string _silver = "";
    [ObservableProperty] private string _copper = "";

    public bool HasSelection => Selected is not null;
    public int PageSize { get; } = 200;
    public string PageLabel => $"Page {Page + 1} / {PageCount}";

    /// <summary>L'or ne s'écrit qu'hors ligne : le serveur écraserait la valeur autrement.</summary>
    public bool MoneyEditable => Selected is { Online: false };

    public string MoneyHint => Selected is null
        ? ""
        : Selected.Online
            ? "Personnage connecté : le serveur écraserait l'écriture à sa prochaine sauvegarde. " +
              "Ciblez-le en jeu et utilisez « .modify money », ou attendez sa déconnexion."
            : $"Actuellement {Selected.MoneyText}.";

    public CharactersViewModel(CharacterService characters, ServerContext context)
    {
        _characters = characters;
        _context = context;
    }

    // ------------------------------------------------------------------ recherche

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
            var filter = new CharacterFilter(
                Text: string.IsNullOrWhiteSpace(Search) ? null : Search,
                OnlyOnline: OnlyOnline,
                MinLevel: int.TryParse(MinLevel, out var min) ? min : null,
                MaxLevel: int.TryParse(MaxLevel, out var max) ? max : null,
                Page: Page, PageSize: PageSize);

            var result = await _characters.SearchAsync(filter, cts.Token);
            if (cts.IsCancellationRequested) return;

            var previous = Selected?.Guid;
            Characters.Clear();
            foreach (var c in result.Characters) Characters.Add(c);

            PageCount = result.PageCount;
            Status = result.Total == 0 ? "Aucun personnage ne correspond." : $"{result.Total} personnage(s)";
            Selected = Characters.FirstOrDefault(c => c.Guid == previous) ?? Characters.FirstOrDefault();
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

    [RelayCommand]
    private async Task LoadDestinationsAsync()
    {
        try
        {
            Destinations.Clear();
            foreach (var d in await _characters.DestinationsAsync(DestinationSearch)) Destinations.Add(d);
            Status = $"{Destinations.Count} destination(s)";
        }
        catch (Exception ex)
        {
            Status = "Destinations indisponibles : " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ actions

    private async Task RunAsync(Func<Task<GmCommandResult>> action, string label)
    {
        IsBusy = true;
        Status = label + "…";
        try
        {
            var r = await action();
            Status = r.Success
                ? $"{label} : {r.Output.ReplaceLineEndings(" ").Trim()}"
                : $"{label} a échoué — {r.Output}";
            if (r.Success) await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = $"{label} a échoué — {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyLevelAsync()
    {
        if (Selected is null) return;
        if (!int.TryParse(NewLevel, out var level) || level < 1 || level > 80)
        {
            Status = "Niveau invalide : indiquez une valeur entre 1 et 80.";
            return;
        }
        var name = Selected.Name;
        await RunAsync(() => _characters.SetLevelAsync(name, level), $"Niveau {level} pour {name}");
    }

    [RelayCommand]
    private async Task FlagAsync(string? which)
    {
        if (Selected is null || string.IsNullOrEmpty(which)) return;
        var name = Selected.Name;

        Func<Task<GmCommandResult>> action = which switch
        {
            "rename" => () => _characters.RenameAsync(name),
            "customize" => () => _characters.CustomizeAsync(name),
            "faction" => () => _characters.ChangeFactionAsync(name),
            "race" => () => _characters.ChangeRaceAsync(name),
            _ => () => Task.FromResult(new GmCommandResult(false, "Drapeau inconnu.", 0))
        };

        var label = which switch
        {
            "rename" => "Renommage",
            "customize" => "Customisation",
            "faction" => "Changement de faction",
            "race" => "Changement de race",
            _ => which
        };

        await RunAsync(action, $"{label} pour {name}");
    }

    [RelayCommand]
    private async Task TeleportAsync()
    {
        if (Selected is null || Destination is null)
        {
            Status = "Choisissez une destination.";
            return;
        }
        var name = Selected.Name;
        var target = Destination.Name;
        await RunAsync(() => _characters.TeleportAsync(name, target), $"Téléportation de {name} vers {target}");
    }

    [RelayCommand]
    private async Task SendHomeAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        await RunAsync(() => _characters.SendHomeAsync(name), $"Retour au point de liaison pour {name}");
    }

    /// <summary>Annule la dernière téléportation : le filet de sécurité du §10.</summary>
    [RelayCommand]
    private async Task RecallAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        await RunAsync(() => _characters.RecallAsync(name), $"Rappel de {name} à sa position précédente");
    }

    [RelayCommand]
    private async Task SummonAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        await RunAsync(() => _characters.SummonAsync(name), $"Invocation de {name}");
    }

    [RelayCommand]
    private async Task AppearAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        await RunAsync(() => _characters.AppearAsync(name), $"Déplacement vers {name}");
    }

    [RelayCommand]
    private async Task ReviveAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        await RunAsync(() => _characters.ReviveAsync(name), $"Réanimation de {name}");
    }

    [RelayCommand]
    private async Task KickAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        await RunAsync(() => _characters.KickAsync(name), $"Expulsion de {name}");
    }

    [RelayCommand]
    private async Task ApplyMoneyAsync()
    {
        if (Selected is null) return;

        var copper = (long.TryParse(Gold, out var g) ? g : 0) * 10000
                   + (long.TryParse(Silver, out var s) ? s : 0) * 100
                   + (long.TryParse(Copper, out var c) ? c : 0);

        var name = Selected.Name;
        var confirm = System.Windows.MessageBox.Show(
            $"Fixer l'or de « {name} » à {ItemReference.Money(copper)} ?\n\n" +
            $"Valeur actuelle : {Selected.MoneyText}",
            "Modification de l'or",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var rows = await _characters.SetMoneyAsync(Selected, copper);
            Status = $"{name} : {rows} ligne(s) modifiée(s), or fixé à {ItemReference.Money(copper)}.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = "Modification refusée : " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
