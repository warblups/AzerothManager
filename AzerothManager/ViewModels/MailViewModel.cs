using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Courrier en jeu (§9). Canal de dédommagement après un incident et de récompense
/// d'événement. L'envoi passe par la commande GM, la lecture par SQL.
/// </summary>
public partial class MailViewModel : ObservableObject
{
    private readonly MailService _mail;
    private readonly ServerContext _context;
    private readonly Func<ItemCatalogViewModel> _catalogFactory;

    public ObservableCollection<MailAttachment> Attachments { get; } = [];
    public ObservableCollection<MailMessage> Inbox { get; } = [];

    [ObservableProperty] private string _recipient = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private string _gold = "0";
    [ObservableProperty] private string _silver = "0";
    [ObservableProperty] private string _copper = "0";

    /// <summary>Envoi à tous les personnages plutôt qu'à un seul.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecipientEnabled))]
    private bool _sendToAll;

    [ObservableProperty] private string _minLevel = "1";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Renseignez un destinataire, un sujet et un texte.";
    [ObservableProperty] private string _inboxCharacter = "";

    public bool RecipientEnabled => !SendToAll;

    public int MaxAttachments => MailService.MaxAttachments;

    public long TotalCopper =>
        (long.TryParse(Gold, out var g) ? g : 0) * 10000 +
        (long.TryParse(Silver, out var s) ? s : 0) * 100 +
        (long.TryParse(Copper, out var c) ? c : 0);

    public MailViewModel(MailService mail, ServerContext context, Func<ItemCatalogViewModel> catalogFactory)
    {
        _mail = mail;
        _context = context;
        _catalogFactory = catalogFactory;
    }

    // ------------------------------------------------------------------ pièces jointes

    /// <summary>Ouvre le catalogue d'objets comme sélecteur (§19).</summary>
    [RelayCommand]
    private void AddAttachment()
    {
        if (Attachments.Count >= MailService.MaxAttachments)
        {
            Status = $"Un courrier ne peut porter plus de {MailService.MaxAttachments} pièces jointes.";
            return;
        }

        var picker = new Views.ItemPickerWindow(_catalogFactory())
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (picker.ShowDialog() != true || picker.PickedItem is null) return;

        var item = picker.PickedItem;
        Attachments.Add(new MailAttachment(item.Entry, picker.PickedCount, item.Name, item.Quality, item.DisplayId));
        Status = $"{item.Name} ajouté ({Attachments.Count}/{MailService.MaxAttachments}).";
    }

    [RelayCommand]
    private void RemoveAttachment(MailAttachment? attachment)
    {
        if (attachment is null) return;
        Attachments.Remove(attachment);
    }

    [RelayCommand]
    private void ClearAttachments() => Attachments.Clear();

    // ------------------------------------------------------------------ envoi

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!_context.HasActive)
        {
            Status = "Aucun serveur actif.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Subject))
        {
            Status = "Le sujet est obligatoire.";
            return;
        }

        IsBusy = true;
        try
        {
            if (SendToAll)
            {
                await SendToEveryoneAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(Recipient))
            {
                Status = "Destinataire manquant.";
                return;
            }

            Status = "Envoi…";
            var report = await _mail.SendAsync(Recipient.Trim(), Subject, Body, Attachments, TotalCopper);
            Status = report.Message;
        }
        catch (Exception ex)
        {
            Status = "Échec de l'envoi : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Envoi en masse : une commande par destinataire, le serveur n'offrant rien de groupé.
    /// Confirmation explicite, l'opération étant visible par tous les joueurs.
    /// </summary>
    private async Task SendToEveryoneAsync()
    {
        var minLevel = int.TryParse(MinLevel, out var lvl) ? lvl : 1;
        var names = await _mail.AllCharacterNamesAsync(minLevel);

        if (names.Count == 0)
        {
            Status = "Aucun personnage ne correspond.";
            return;
        }

        var attachmentSummary = Attachments.Count == 0
            ? "sans pièce jointe"
            : $"avec {Attachments.Count} pièce(s) jointe(s)";
        var moneySummary = TotalCopper > 0 ? $" et {ItemReference.Money(TotalCopper)}" : "";

        var message = $"""
            Envoyer ce courrier à {names.Count} personnage(s) de niveau {minLevel} ou plus ?

            Sujet : {Subject}
            Contenu : {attachmentSummary}{moneySummary}

            Une commande est envoyée par destinataire, et rien ne permet de rappeler
            un courrier déjà distribué.
            """;

        var confirm = System.Windows.MessageBox.Show(
            message, "Envoi en masse",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var progress = new Progress<string>(text => Status = text);
        var report = await _mail.SendBulkAsync(names, Subject, Body, Attachments, TotalCopper, progress);
        Status = report.Message;
    }

    // ------------------------------------------------------------------ boîte de réception

    [RelayCommand]
    private async Task LoadInboxAsync()
    {
        if (string.IsNullOrWhiteSpace(InboxCharacter))
        {
            Status = "Indiquez le personnage dont lire la boîte.";
            return;
        }

        IsBusy = true;
        try
        {
            Inbox.Clear();
            foreach (var m in await _mail.InboxAsync(InboxCharacter.Trim())) Inbox.Add(m);
            Status = Inbox.Count == 0
                ? $"Aucun courrier pour {InboxCharacter.Trim()}."
                : $"{Inbox.Count} courrier(s) pour {InboxCharacter.Trim()}.";
        }
        catch (Exception ex)
        {
            Status = "Lecture impossible : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    partial void OnGoldChanged(string value) => OnPropertyChanged(nameof(TotalCopper));
    partial void OnSilverChanged(string value) => OnPropertyChanged(nameof(TotalCopper));
    partial void OnCopperChanged(string value) => OnPropertyChanged(nameof(TotalCopper));
}
