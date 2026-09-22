using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Guildes et banque de guilde (§8 et §9).
///
/// La consultation prime : le §9 range les litiges de vol de banque parmi les motifs de
/// ticket récurrents, et ce qui les tranche est le **journal**, pas une commande. Les
/// écritures passent toutes par des commandes `.guild`, pour que le worldserver reste
/// cohérent avec la base.
/// </summary>
public partial class GuildsViewModel : ObservableObject
{
    private readonly GuildService _guilds;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    public ObservableCollection<GuildSummary> Guilds { get; } = [];
    public ObservableCollection<GuildMember> Members { get; } = [];
    public ObservableCollection<GuildRank> Ranks { get; } = [];
    public ObservableCollection<GuildBankTab> Tabs { get; } = [];
    public ObservableCollection<GuildBankItem> BankItems { get; } = [];
    public ObservableCollection<GuildBankEvent> BankLog { get; } = [];
    public ObservableCollection<GuildEvent> EventLog { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private GuildSummary? _selected;

    [ObservableProperty] private GuildMember? _selectedMember;

    /// <summary>Onglet de banque filtré. `null` signifie « tous les onglets ».</summary>
    [ObservableProperty] private GuildBankTab? _selectedTab;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Cherchez une guilde par son nom, ou laissez vide pour toutes les lister.";
    [ObservableProperty] private string _output = "";

    // Saisie des actions
    [ObservableProperty] private string _playerName = "";
    [ObservableProperty] private string _rank = "4";
    [ObservableProperty] private string _newName = "";

    public bool HasSelection => Selected is not null;

    public GuildsViewModel(GuildService guilds, ServerContext context)
    {
        _guilds = guilds;
        _context = context;
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

        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        IsBusy = true;
        Status = "Recherche…";
        try
        {
            var previous = Selected?.Id;
            var found = await _guilds.SearchAsync(Search, ct: cts.Token);
            if (cts.IsCancellationRequested) return;

            Guilds.Clear();
            foreach (var g in found) Guilds.Add(g);

            Status = Guilds.Count == 0
                ? "Aucune guilde. Sur un serveur neuf, les tables de guilde sont vides tant qu'aucun joueur n'en a fondé une."
                : $"{Guilds.Count} guilde(s).";

            Selected = Guilds.FirstOrDefault(g => g.Id == previous) ?? Guilds.FirstOrDefault();
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

    partial void OnSelectedChanged(GuildSummary? value)
    {
        NewName = value?.Name ?? "";
        if (value is not null) _ = LoadDetailAsync();
        else ClearDetail();
    }

    /// <summary>Changer d'onglet ne relit que la banque : membres et rangs n'en dépendent pas.</summary>
    partial void OnSelectedTabChanged(GuildBankTab? value)
    {
        if (Selected is not null) _ = LoadBankAsync();
    }

    private void ClearDetail()
    {
        Members.Clear();
        Ranks.Clear();
        Tabs.Clear();
        BankItems.Clear();
        BankLog.Clear();
        EventLog.Clear();
    }

    [RelayCommand]
    private async Task LoadDetailAsync()
    {
        if (Selected is not { } guild || !Ready()) return;

        IsBusy = true;
        try
        {
            Members.Clear();
            foreach (var m in await _guilds.MembersAsync(guild.Id)) Members.Add(m);

            Ranks.Clear();
            foreach (var r in await _guilds.RanksAsync(guild.Id)) Ranks.Add(r);

            var tabs = await _guilds.BankTabsAsync(guild.Id);
            Tabs.Clear();
            foreach (var t in tabs) Tabs.Add(t);

            EventLog.Clear();
            foreach (var e in await _guilds.EventLogAsync(guild.Id)) EventLog.Add(e);

            // SelectedTab reste nul : par défaut on montre toute la banque, un vol ne se
            // cantonne pas à un onglet.
            SelectedTab = null;
            await LoadBankAsync();

            Status = $"« {guild.Name} » — {Members.Count} membre(s), {Ranks.Count} rang(s), " +
                     $"{Tabs.Count} onglet(s) de banque, {guild.BankMoneyText} en caisse.";
        }
        catch (Exception ex)
        {
            Status = "Lecture impossible : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadBankAsync()
    {
        if (Selected is not { } guild) return;

        var tabId = SelectedTab?.TabId;

        BankItems.Clear();
        foreach (var i in await _guilds.BankItemsAsync(guild.Id, tabId)) BankItems.Add(i);

        BankLog.Clear();
        foreach (var e in await _guilds.BankLogAsync(guild.Id, tabId)) BankLog.Add(e);
    }

    [RelayCommand]
    private void ShowAllTabs() => SelectedTab = null;

    // ------------------------------------------------------------------ écritures

    private async Task RunAsync(Func<Task<GmCommandResult>> action, string done)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            Output = result.Output;
            Status = result.Success ? done : "Refusé : " + result.Output;
            if (result.Success) await SearchAsync();
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!Ready()) return;
        if (string.IsNullOrWhiteSpace(PlayerName) || string.IsNullOrWhiteSpace(NewName))
        {
            Status = "Indiquez le personnage meneur et le nom de la guilde.";
            return;
        }

        await RunAsync(() => _guilds.CreateAsync(PlayerName, NewName),
                       $"Guilde « {NewName} » créée.");
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (!Ready() || Selected is not { } guild) return;
        if (string.IsNullOrWhiteSpace(NewName) || NewName == guild.Name)
        {
            Status = "Saisissez un nouveau nom, différent de l'actuel.";
            return;
        }

        await RunAsync(() => _guilds.RenameAsync(guild.Name, NewName),
                       $"Guilde renommée en « {NewName} ».");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!Ready() || Selected is not { } guild) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Dissoudre définitivement la guilde « {guild.Name} » ?\n\n" +
            $"{guild.MemberCount} membre(s), {guild.BankMoneyText} en caisse, " +
            $"{BankItems.Count} objet(s) visible(s) en banque.\n\n" +
            "La guilde, ses rangs, ses membres et le contenu de sa banque sont supprimés. " +
            "Rien ici ne permet de les restaurer : seule une sauvegarde de base le pourrait, " +
            "et c'est AzerothUpdater qui les gère.",
            "Dissoudre une guilde",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await RunAsync(() => _guilds.DeleteAsync(guild.Name), $"Guilde « {guild.Name} » dissoute.");
    }

    [RelayCommand]
    private async Task InviteAsync()
    {
        if (!Ready() || Selected is not { } guild) return;
        if (string.IsNullOrWhiteSpace(PlayerName))
        {
            Status = "Indiquez le personnage à ajouter.";
            return;
        }

        await RunAsync(() => _guilds.InviteAsync(PlayerName, guild.Name),
                       $"{PlayerName} ajouté à « {guild.Name} ».");
        await LoadDetailAsync();
    }

    /// <summary>Exclut le membre sélectionné, ou le personnage saisi à défaut.</summary>
    [RelayCommand]
    private async Task UninviteAsync()
    {
        if (!Ready()) return;

        var target = SelectedMember?.Name is { Length: > 0 } name ? name : PlayerName;
        if (string.IsNullOrWhiteSpace(target))
        {
            Status = "Sélectionnez un membre, ou saisissez un nom de personnage.";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Exclure {target} de sa guilde ?",
            "Exclure un membre",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await RunAsync(() => _guilds.UninviteAsync(target), $"{target} exclu de sa guilde.");
        await LoadDetailAsync();
    }

    [RelayCommand]
    private async Task SetRankAsync()
    {
        if (!Ready()) return;

        var target = SelectedMember?.Name is { Length: > 0 } name ? name : PlayerName;
        if (string.IsNullOrWhiteSpace(target))
        {
            Status = "Sélectionnez un membre, ou saisissez un nom de personnage.";
            return;
        }
        if (!int.TryParse(Rank, out var rank) || rank < 0)
        {
            Status = "Le rang doit être un nombre positif.";
            return;
        }
        if (Ranks.Count > 0 && Ranks.All(r => r.RankId != rank))
        {
            Status = $"Le rang {rank} n'existe pas dans cette guilde : " +
                     $"rangs définis {string.Join(", ", Ranks.Select(r => r.RankId))}.";
            return;
        }

        await RunAsync(() => _guilds.SetRankAsync(target, rank), $"{target} placé au rang {rank}.");
        await LoadDetailAsync();
    }

    [RelayCommand]
    private async Task InfoAsync()
    {
        if (!Ready() || Selected is not { } guild) return;

        IsBusy = true;
        try
        {
            var result = await _guilds.InfoAsync(guild.Name);
            Output = result.Output;
            Status = result.Success ? "Fiche de guilde relevée." : "Refusé : " + result.Output;
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedMemberChanged(GuildMember? value)
    {
        if (value is null) return;
        PlayerName = value.Name;
        Rank = value.RankId.ToString();
    }
}
