using System.Collections.ObjectModel;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothManager.ViewModels;

/// <summary>Entrée de navigation du rail de gauche. Les modules non encore construits restent visibles mais inactifs.</summary>
public partial class NavigationItem : ObservableObject
{
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
                         GmConsoleViewModel gmConsole)
    {
        Context = context;

        Items.Add(new NavigationItem
        {
            Title = "Configuration des serveurs",
            Version = "v1.0",
            IsAvailable = true,
            Content = serverConfig
        });

        Items.Add(new NavigationItem
        {
            Title = "Console GM",
            Version = "v1.0",
            IsAvailable = true,
            Content = gmConsole
        });

        // Modules planifiés : affichés dès maintenant pour que l'ordre de construction
        // du cahier des charges (§22) reste lisible dans l'application elle-même.
        foreach (var (title, version) in new (string, string)[]
        {
            ("Console SQL", "v1.0"),
            ("Comptes", "v1.0"),
            ("Personnages", "v1.0"),
            ("Catalogue d'objets", "v1.0"),
            ("Courrier en jeu", "v1.0"),
            ("Modération", "v1.0"),
            ("Tickets GM", "v1.1"),
            ("Téléportation", "v1.1"),
            ("Armurerie", "v1.1"),
            ("Édition du monde", "v1.2"),
            ("Logs", "v1.2")
        })
        {
            Items.Add(new NavigationItem { Title = title, Version = version, IsAvailable = false });
        }

        SelectedItem = Items[0];
    }
}
