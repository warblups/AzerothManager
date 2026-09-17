using System.Collections.ObjectModel;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothManager.ViewModels;

/// <summary>Entrée de navigation du rail de gauche. Les modules non encore construits restent visibles mais inactifs.</summary>
public partial class NavigationItem : ObservableObject
{
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    public bool IsAvailable { get; init; }
    public object? Content { get; init; }
}

/// <summary>Coquille de l'application : navigation et barre d'état (§17).</summary>
public partial class MainViewModel : ObservableObject
{
    public ServerContext Context { get; }

    public ObservableCollection<NavigationItem> Items { get; } = [];

    [ObservableProperty] private NavigationItem? _selectedItem;

    public MainViewModel(ServerContext context, ServerConfigViewModel serverConfig,
                         GmConsoleViewModel gmConsole, SqlEditorViewModel sqlConsole,
                         ItemCatalogViewModel itemCatalog,
                         AccountsViewModel accounts,
                         MailViewModel mail,
                         ArmoryViewModel armory,
                         TicketsViewModel tickets, RestoreViewModel restore,
                         PlayerMapViewModel playerMap,
                         CharactersViewModel characters,
                         ModerationViewModel moderation)
    {
        Context = context;

        Items.Add(new NavigationItem
        {
            Icon = "🖥",
            Title = "Configuration des serveurs",
            Version = "v1.0",
            IsAvailable = true,
            Content = serverConfig
        });

        Items.Add(new NavigationItem
        {
            Icon = "🗄",
            Title = "Console SQL",
            Version = "v1.0",
            IsAvailable = true,
            Content = sqlConsole
        });

        Items.Add(new NavigationItem
        {
            Icon = "👤",
            Title = "Comptes",
            Version = "v1.0",
            IsAvailable = true,
            Content = accounts
        });

        Items.Add(new NavigationItem
        {
            Icon = "✉",
            Title = "Courrier en jeu",
            Version = "v1.0",
            IsAvailable = true,
            Content = mail
        });

        Items.Add(new NavigationItem
        {
            Icon = "🧙",
            Title = "Personnages",
            Version = "v1.0",
            IsAvailable = true,
            Content = characters
        });

        Items.Add(new NavigationItem
        {
            Icon = "🎒",
            Title = "Catalogue d'objets",
            Version = "v1.0",
            IsAvailable = true,
            Content = itemCatalog
        });

        Items.Add(new NavigationItem
        {
            Icon = "⌨",
            Title = "Console GM",
            Version = "v1.0",
            IsAvailable = true,
            Content = gmConsole
        });

        Items.Add(new NavigationItem
        {
            Icon = "⚔",
            Title = "Armurerie",
            Version = "v1.1",
            IsAvailable = true,
            Content = armory
        });

        Items.Add(new NavigationItem
        {
            Icon = "🗺",
            Title = "Carte des joueurs",
            Version = "v2.0",
            IsAvailable = true,
            Content = playerMap
        });

        Items.Add(new NavigationItem
        {
            Icon = "🎫",
            Title = "Tickets GM",
            Version = "v1.1",
            IsAvailable = true,
            Content = tickets
        });

        Items.Add(new NavigationItem
        {
            Icon = "♻",
            Title = "Restauration",
            Version = "v1.1",
            IsAvailable = true,
            Content = restore
        });

        Items.Add(new NavigationItem
        {
            Icon = "🛡",
            Title = "Modération",
            Version = "v1.0",
            IsAvailable = true,
            Content = moderation
        });

        // Modules planifiés : affichés dès maintenant pour que l'ordre de construction
        // du cahier des charges (§22) reste lisible dans l'application elle-même.
        foreach (var (icon, title, version) in new (string, string, string)[]
        {
            ("🌀", "Téléportation", "v1.1"),
            ("🗺", "Édition du monde", "v1.2"),
            ("📜", "Logs", "v1.2")
        })
        {
            Items.Add(new NavigationItem { Icon = icon, Title = title, Version = version, IsAvailable = false });
        }

        SelectedItem = Items[0];
    }
}
