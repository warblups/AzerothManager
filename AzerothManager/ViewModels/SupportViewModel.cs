using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>Tickets GM (§9) : consulter, assigner, répondre, clore, et se rendre sur place.</summary>
public partial class TicketsViewModel : ObservableObject
{
    private readonly SupportService _support;
    private readonly ServerContext _context;

    public ObservableCollection<GmTicket> Tickets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private GmTicket? _selected;

    [ObservableProperty] private bool _openOnly = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargez la liste des tickets.";
    [ObservableProperty] private string _responseText = "";
    [ObservableProperty] private string _commentText = "";
    [ObservableProperty] private string _assignTo = "";

    public bool HasSelection => Selected is not null;

    public TicketsViewModel(SupportService support, ServerContext context)
    {
        _support = support;
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
            var previous = Selected?.Id;
            Tickets.Clear();
            foreach (var t in await _support.TicketsAsync(OpenOnly)) Tickets.Add(t);

            Status = Tickets.Count == 0
                ? OpenOnly ? "Aucun ticket ouvert." : "Aucun ticket."
                : $"{Tickets.Count} ticket(s)";
            Selected = Tickets.FirstOrDefault(t => t.Id == previous) ?? Tickets.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task RunAsync(Func<Task<GmCommandResult>> action, string label, bool reload = true)
    {
        IsBusy = true;
        Status = label + "…";
        try
        {
            var r = await action();
            Status = r.Success
                ? $"{label} : {r.Output.ReplaceLineEndings(" ").Trim()}"
                : $"{label} a échoué — {r.Output}";
            if (r.Success && reload) await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = $"{label} a échoué — {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AssignAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(AssignTo))
        {
            Status = "Indiquez le maître de jeu à qui assigner le ticket.";
            return;
        }
        var id = Selected.Id;
        await RunAsync(() => _support.AssignTicketAsync(id, AssignTo.Trim()), $"Assignation du ticket {id}");
    }

    [RelayCommand]
    private async Task UnassignAsync()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        await RunAsync(() => _support.UnassignTicketAsync(id), $"Désassignation du ticket {id}");
    }

    /// <summary>Réponse visible par le joueur.</summary>
    [RelayCommand]
    private async Task RespondAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(ResponseText))
        {
            Status = "Saisissez une réponse.";
            return;
        }
        var id = Selected.Id;
        var text = ResponseText.Trim();
        await RunAsync(() => _support.RespondTicketAsync(id, text), $"Réponse au ticket {id}");
        ResponseText = "";
    }

    /// <summary>Commentaire interne, invisible pour le joueur.</summary>
    [RelayCommand]
    private async Task CommentAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(CommentText))
        {
            Status = "Saisissez un commentaire.";
            return;
        }
        var id = Selected.Id;
        var text = CommentText.Trim();
        await RunAsync(() => _support.CommentTicketAsync(id, text), $"Commentaire sur le ticket {id}");
        CommentText = "";
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        var response = string.IsNullOrWhiteSpace(ResponseText) ? null : ResponseText.Trim();
        await RunAsync(() => _support.CompleteTicketAsync(id, response), $"Ticket {id} marqué traité");
        ResponseText = "";
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        await RunAsync(() => _support.CloseTicketAsync(id), $"Clôture du ticket {id}");
    }

    [RelayCommand]
    private async Task EscalateAsync()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        await RunAsync(() => _support.EscalateTicketAsync(id), $"Escalade du ticket {id}");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var id = Selected.Id;

        var confirm = System.Windows.MessageBox.Show(
            $"Supprimer le ticket {id} ? Le joueur n'en verra plus trace, et rien ne le rétablira.",
            "Suppression de ticket",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await RunAsync(() => _support.DeleteTicketAsync(id), $"Suppression du ticket {id}");
    }

    /// <summary>Se rendre sur le lieu signalé : exige un personnage GM connecté.</summary>
    [RelayCommand]
    private async Task GoToAsync()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        await RunAsync(() => _support.GoToTicketAsync(id), $"Téléportation vers le ticket {id}", reload: false);
    }

    [RelayCommand]
    private async Task SummonAsync()
    {
        if (Selected is null) return;
        var name = Selected.PlayerName;
        await RunAsync(() => _support.SummonAsync(name), $"Invocation de {name}", reload: false);
    }

    [RelayCommand]
    private async Task AppearAsync()
    {
        if (Selected is null) return;
        var name = Selected.PlayerName;
        await RunAsync(() => _support.AppearAsync(name), $"Déplacement vers {name}", reload: false);
    }

    partial void OnOpenOnlyChanged(bool value) => _ = LoadAsync();
}

/// <summary>Restauration ciblée (§9) : personnages supprimés encore récupérables.</summary>
public partial class RestoreViewModel : ObservableObject
{
    private readonly SupportService _support;
    private readonly ServerContext _context;

    public ObservableCollection<DeletedCharacter> Deleted { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DeletedCharacter? _selected;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newAccount = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargez la liste des personnages supprimés.";

    public bool HasSelection => Selected is not null;

    public RestoreViewModel(SupportService support, ServerContext context)
    {
        _support = support;
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
            Deleted.Clear();
            foreach (var d in await _support.DeletedCharactersAsync(Search)) Deleted.Add(d);

            Status = Deleted.Count == 0
                ? "Aucun personnage supprimé récupérable."
                : $"{Deleted.Count} personnage(s) supprimé(s)";
            Selected = Deleted.FirstOrDefault();
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
        if (Selected is null) return;

        var name = Selected.Name;
        var target = string.IsNullOrWhiteSpace(NewAccount) ? Selected.AccountName : NewAccount.Trim();

        if (Selected.AccountMissing && string.IsNullOrWhiteSpace(NewAccount))
        {
            Status = "Le compte d'origine n'existe plus : indiquez un compte de destination.";
            return;
        }

        var renamed = string.IsNullOrWhiteSpace(NewName) ? "" : $"\nNouveau nom : {NewName.Trim()}";
        var confirm = System.Windows.MessageBox.Show(
            $"Restaurer « {name} » (niveau {Selected.Level}) sur le compte {target} ?{renamed}",
            "Restauration",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var r = await _support.RestoreCharacterAsync(name, NewName, NewAccount);
            Status = r.Success
                ? $"Restauration de {name} : {r.Output.ReplaceLineEndings(" ").Trim()}"
                : $"Restauration de {name} a échoué — {r.Output}";
            if (r.Success)
            {
                NewName = "";
                NewAccount = "";
                await LoadAsync();
            }
        }
        finally { IsBusy = false; }
    }

    /// <summary>Purge : la ligne disparaît pour de bon, aucune restauration ultérieure.</summary>
    [RelayCommand]
    private async Task PurgeAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;

        var confirm = System.Windows.MessageBox.Show(
            $"Effacer définitivement « {name} » ?\n\nCe personnage est aujourd'hui récupérable ; " +
            "après cette opération il ne le sera plus.",
            "Effacement définitif",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var r = await _support.PurgeCharacterAsync(name);
            Status = r.Success ? $"{name} effacé définitivement." : $"Échec — {r.Output}";
            if (r.Success) await LoadAsync();
        }
        finally { IsBusy = false; }
    }
}
