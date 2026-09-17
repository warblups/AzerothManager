using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>Module Comptes (§9) : recherche et listes en SQL, écritures par commande GM.</summary>
public partial class AccountsViewModel : ObservableObject
{
    private readonly AccountService _accounts;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    public ObservableCollection<AccountSummary> Accounts { get; } = [];
    public ObservableCollection<AccountCharacter> Characters { get; } = [];

    public NamedValue[] GmLevels { get; } =
    [
        new(0, "0 — Joueur"), new(1, "1 — Modérateur"),
        new(2, "2 — Maître de jeu"), new(3, "3 — Administrateur")
    ];

    public NamedValue[] Expansions { get; } =
    [
        new(0, "Classic"), new(1, "Burning Crusade"), new(2, "Wrath of the Lich King")
    ];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyOnline;
    [ObservableProperty] private bool _onlyBanned;
    [ObservableProperty] private bool _onlyGm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AccountSummary? _selected;

    [ObservableProperty] private AccountCharacter? _selectedCharacter;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Lancez une recherche, ou laissez vide pour tout lister.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount = 1;

    // Champs de saisie des actions
    [ObservableProperty] private string _newUsername = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _newEmail = "";
    [ObservableProperty] private string _passwordChange = "";
    [ObservableProperty] private NamedValue? _gmLevel;
    [ObservableProperty] private NamedValue? _expansion;
    [ObservableProperty] private string _banDuration = "1d";
    [ObservableProperty] private string _banReason = "";
    [ObservableProperty] private string _muteMinutes = "60";
    [ObservableProperty] private string _muteReason = "";

    public bool HasSelection => Selected is not null;
    public int PageSize { get; } = 100;
    public string PageLabel => $"Page {Page + 1} / {PageCount}";

    public AccountsViewModel(AccountService accounts, ServerContext context)
    {
        _accounts = accounts;
        _context = context;
        GmLevel = GmLevels[0];
        Expansion = Expansions[2];
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
            var filter = new AccountFilter(
                Text: string.IsNullOrWhiteSpace(Search) ? null : Search,
                OnlyOnline: OnlyOnline, OnlyBanned: OnlyBanned, OnlyGm: OnlyGm,
                Page: Page, PageSize: PageSize);

            var result = await _accounts.SearchAsync(filter, cts.Token);
            if (cts.IsCancellationRequested) return;

            var previous = Selected?.Id;
            Accounts.Clear();
            foreach (var a in result.Accounts) Accounts.Add(a);

            PageCount = result.PageCount;
            Status = result.Total == 0 ? "Aucun compte ne correspond." : $"{result.Total} compte(s)";

            // Conserver la sélection après une action, pour ne pas perdre le contexte.
            Selected = Accounts.FirstOrDefault(a => a.Id == previous) ?? Accounts.FirstOrDefault();
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

    partial void OnSelectedChanged(AccountSummary? value) => _ = LoadCharactersAsync();

    private async Task LoadCharactersAsync()
    {
        Characters.Clear();
        if (Selected is null) return;
        try
        {
            foreach (var c in await _accounts.CharactersAsync(Selected.Id)) Characters.Add(c);
        }
        catch (Exception ex)
        {
            Status = "Personnages indisponibles : " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Exécute une commande GM, rend compte, puis rafraîchit la liste.</summary>
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
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewPassword))
        {
            Status = "Nom et mot de passe sont requis.";
            return;
        }
        if (NewUsername.Length > 16 || NewPassword.Length > 16)
        {
            Status = "Le serveur limite le nom à 16 caractères et le mot de passe à 16.";
            return;
        }

        var name = NewUsername.Trim();
        await RunAsync(() => _accounts.CreateAsync(name, NewPassword.Trim(), NewEmail.Trim()),
                       $"Création du compte {name}");
        NewPassword = "";
    }

    /// <summary>
    /// Suppression définitive. La commande détruit aussi les personnages, en dur : ni la
    /// restauration ciblée ni une transaction ne peuvent revenir en arrière. La confirmation
    /// annonce donc précisément ce qui sera perdu.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;

        var name = Selected.Username;
        var count = Selected.CharacterCount;
        var characters = count == 0
            ? "Ce compte n'a aucun personnage."
            : count == 1
                ? "Son personnage sera détruit définitivement."
                : $"Ses {count} personnages seront détruits définitivement.";

        var message = $"""
            Supprimer le compte « {name} » ?

            {characters}

            La suppression est immédiate et irréversible : les personnages sont effacés en dur,
            sans passer par la suppression différée, donc sans restauration possible.
            """;

        var confirm = System.Windows.MessageBox.Show(
            message,
            "Suppression définitive",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await RunAsync(() => _accounts.DeleteAsync(name), $"Suppression du compte {name}");
        Selected = null;
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(PasswordChange)) return;
        var name = Selected.Username;
        await RunAsync(() => _accounts.SetPasswordAsync(name, PasswordChange.Trim()),
                       $"Mot de passe de {name}");
        PasswordChange = "";
    }

    [RelayCommand]
    private async Task ApplyGmLevelAsync()
    {
        if (Selected is null || GmLevel?.Value is not { } level) return;
        var name = Selected.Username;
        await RunAsync(() => _accounts.SetGmLevelAsync(name, level), $"Niveau GM de {name}");
    }

    [RelayCommand]
    private async Task ApplyExpansionAsync()
    {
        if (Selected is null || Expansion?.Value is not { } expansion) return;
        var name = Selected.Username;
        await RunAsync(() => _accounts.SetExpansionAsync(name, expansion), $"Extension de {name}");
    }

    [RelayCommand]
    private async Task BanAsync()
    {
        if (Selected is null) return;
        var name = Selected.Username;
        var duration = string.IsNullOrWhiteSpace(BanDuration) ? "0" : BanDuration.Trim();
        var reason = string.IsNullOrWhiteSpace(BanReason) ? "Sans motif" : BanReason.Trim();

        var permanent = duration is "0" or "-1";
        var confirm = System.Windows.MessageBox.Show(
            permanent
                ? $"Bannir définitivement le compte « {name} » ?\n\nMotif : {reason}"
                : $"Bannir le compte « {name} » pour {duration} ?\n\nMotif : {reason}",
            "Confirmation de sanction",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await RunAsync(() => _accounts.BanAsync(name, duration, reason), $"Bannissement de {name}");
    }

    [RelayCommand]
    private async Task UnbanAsync()
    {
        if (Selected is null) return;
        var name = Selected.Username;
        await RunAsync(() => _accounts.UnbanAsync(name), $"Levée du bannissement de {name}");
    }

    [RelayCommand]
    private async Task KickAsync()
    {
        if (SelectedCharacter is null) return;
        var name = SelectedCharacter.Name;
        await RunAsync(() => _accounts.KickAsync(name, MuteReason.Trim()), $"Expulsion de {name}");
    }

    [RelayCommand]
    private async Task MuteAsync()
    {
        if (SelectedCharacter is null) return;
        if (!int.TryParse(MuteMinutes, out var minutes) || minutes <= 0)
        {
            Status = "Durée de mute invalide.";
            return;
        }
        var name = SelectedCharacter.Name;
        var reason = string.IsNullOrWhiteSpace(MuteReason) ? "Sans motif" : MuteReason.Trim();
        await RunAsync(() => _accounts.MuteAsync(name, minutes, reason), $"Mute de {name}");
    }

    [RelayCommand]
    private async Task UnmuteAsync()
    {
        if (SelectedCharacter is null) return;
        var name = SelectedCharacter.Name;
        await RunAsync(() => _accounts.UnmuteAsync(name), $"Levée du mute de {name}");
    }
}
