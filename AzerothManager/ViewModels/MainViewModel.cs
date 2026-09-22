using System.Collections.ObjectModel;
using System.Windows.Data;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothManager.ViewModels;

/// <summary>
/// Regroupement du rail de navigation.
///
/// Dix-neuf modules ne se parcourent plus d'un coup d'œil : ils sont rangés par nature de
/// travail, et **tous les groupes sont repliés par défaut** — le rail tient alors en cinq
/// lignes. Ce que l'on déplie est conservé dans la base locale, au même titre que les autres
/// préférences d'affichage (§20).
///
/// La clé de groupe est l'objet lui-même, pas son intitulé : c'est ce qui permet à l'en-tête
/// de lier directement <see cref="IsExpanded"/>, WPF exposant la clé sous le nom <c>Name</c>.
/// </summary>
public partial class NavigationCategory : ObservableObject
{
    public string Title { get; init; } = "";

    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// Vrai pour le groupe qui contient le module affiché. Tout étant replié par défaut,
    /// rien n'indiquerait sinon où l'on se trouve : le repère remplace la surbrillance de
    /// l'entrée, devenue invisible.
    /// </summary>
    [ObservableProperty] private bool _isCurrent;

    public override string ToString() => Title;
}

/// <summary>Entrée de navigation du rail de gauche. Les modules non encore construits restent visibles mais inactifs.</summary>
public partial class NavigationItem : ObservableObject
{
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    public bool IsAvailable { get; init; }
    public object? Content { get; init; }
    public NavigationCategory Category { get; init; } = new();
}

/// <summary>Coquille de l'application : navigation et barre d'état (§17).</summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>
    /// Clé de la préférence qui retient les groupes **dépliés**. Enregistrer l'ouverture
    /// plutôt que la fermeture donne le bon défaut des deux côtés : rien n'est déplié au
    /// premier lancement, et un groupe ajouté plus tard arrive replié comme les autres.
    /// </summary>
    private const string ExpandedSetting = "nav.expanded";

    public ServerContext Context { get; }

    public ObservableCollection<NavigationItem> Items { get; } = [];

    [ObservableProperty] private NavigationItem? _selectedItem;

    /// <summary>Toutes les catégories, dans l'ordre du rail.</summary>
    private readonly List<NavigationCategory> _categories = [];

    partial void OnSelectedItemChanged(NavigationItem? value)
    {
        foreach (var category in _categories)
            category.IsCurrent = value is not null && ReferenceEquals(category, value.Category);
    }

    public MainViewModel(ServerContext context, ServerConfigViewModel serverConfig,
                         GmConsoleViewModel gmConsole, SqlEditorViewModel sqlConsole,
                         ItemCatalogViewModel itemCatalog,
                         AccountsViewModel accounts,
                         MailViewModel mail,
                         ArmoryViewModel armory,
                         TicketsViewModel tickets, RestoreViewModel restore,
                         PlayerMapViewModel playerMap,
                         CharactersViewModel characters,
                         ModerationViewModel moderation,
                         TeleportViewModel teleport,
                         WorldEditViewModel worldEdit,
                         LogsViewModel logs,
                         AnnounceViewModel announce,
                         GuildsViewModel guilds,
                         GameEventsViewModel gameEvents,
                         InstanceLocksViewModel instanceLocks)
    {
        Context = context;

        // Les catégories suivent la nature du travail, pas le découpage en versions du
        // cahier des charges : on ouvre « Support » parce qu'un joueur a un problème, pas
        // parce que le module est en v1.1.
        var server = new NavigationCategory { Title = "Serveur" };
        var players = new NavigationCategory { Title = "Comptes et personnages" };
        var support = new NavigationCategory { Title = "Support" };
        var world = new NavigationCategory { Title = "Monde" };
        var tools = new NavigationCategory { Title = "Outils" };

        Add(server, "🖥", "Configuration des serveurs", "v1.0", serverConfig);
        Add(server, "📢", "Annonces et arrêt", "v1.2", announce);
        Add(server, "📜", "Logs", "v1.2", logs);

        Add(players, "👤", "Comptes", "v1.0", accounts);
        Add(players, "🧙", "Personnages", "v1.0", characters);
        Add(players, "⚔", "Armurerie", "v1.1", armory);
        Add(players, "🏰", "Guildes", "v1.1", guilds);
        Add(players, "🛡", "Modération", "v1.0", moderation);

        Add(support, "🎫", "Tickets GM", "v1.1", tickets);
        Add(support, "✉", "Courrier en jeu", "v1.0", mail);
        Add(support, "♻", "Restauration", "v1.1", restore);

        Add(world, "🗺", "Édition du monde", "v1.2", worldEdit);
        Add(world, "🌀", "Téléportation", "v1.1", teleport);
        Add(world, "🎉", "Événements de jeu", "v1.2", gameEvents);
        Add(world, "🔒", "Verrous d'instance", "v1.2", instanceLocks);
        // 🧭 plutôt que 🗺 : l'édition du monde porte déjà la carte, et deux entrées du même
        // groupe avec la même icône se confondent au coup d'œil.
        Add(world, "🧭", "Carte des joueurs", "v2.0", playerMap);

        Add(tools, "🎒", "Catalogue d'objets", "v1.0", itemCatalog);
        Add(tools, "🗄", "Console SQL", "v1.0", sqlConsole);
        Add(tools, "⌨", "Console GM", "v1.0", gmConsole);

        // Le regroupement est posé sur la vue par défaut de la collection : la liaison de la
        // vue reste `Items`, et la sélection unique continue de fonctionner d'elle-même —
        // ce qu'une liste par groupe aurait fait perdre.
        var view = CollectionViewSource.GetDefaultView(Items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationItem.Category)));

        _categories.AddRange([server, players, support, world, tools]);
        RestoreExpandedState(_categories);

        SelectedItem = Items[0];
    }

    private void Add(NavigationCategory category, string icon, string title, string version, object content) =>
        Items.Add(new NavigationItem
        {
            Icon = icon,
            Title = title,
            Version = version,
            IsAvailable = true,
            Content = content,
            Category = category
        });

    /// <summary>Rouvre les groupes que l'utilisateur avait dépliés, puis suit leurs changements.</summary>
    private static void RestoreExpandedState(IReadOnlyList<NavigationCategory> categories)
    {
        var expanded = LocalDatabase.GetSetting(ExpandedSetting)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            category.IsExpanded = expanded.Contains(category.Title);
            category.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NavigationCategory.IsExpanded))
                    SaveExpandedState(categories);
            };
        }
    }

    private static void SaveExpandedState(IReadOnlyList<NavigationCategory> categories)
    {
        try
        {
            LocalDatabase.SetSetting(ExpandedSetting,
                string.Join(';', categories.Where(c => c.IsExpanded).Select(c => c.Title)));
        }
        catch
        {
            // Une préférence d'affichage ne vaut pas d'interrompre la navigation.
        }
    }
}
