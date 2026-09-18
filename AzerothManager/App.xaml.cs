using System.IO;
using System.Windows;
using AzerothManager.Services;
using AzerothManager.ViewModels;
using Serilog;

namespace AzerothManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(LocalDatabase.FolderPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(LocalDatabase.FolderPath, "logs", "azerothmanager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Exception non gérée");
            MessageBox.Show(args.Exception.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            LocalDatabase.Initialize();
            Log.Information("Base locale prête : {Path}", LocalDatabase.FilePath);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Impossible d'initialiser la base locale");
            MessageBox.Show($"Impossible d'initialiser la base locale :\n{ex.Message}",
                "AzerothManager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Composition : assemblage manuel, suffisant à l'échelle de l'application.
        var profiles = new ServerProfileService();
        var context = new ServerContext();
        var mySql = new MySqlService(context);
        var ssh = new SshService(context);
        var gm = new GmCommandService(context);
        var sqlEditor = new SqlEditorService(context, mySql);
        var itemCatalog = new ItemCatalogService(mySql);
        var accountService = new AccountService(mySql, gm, context);
        var mailService = new MailService(gm, mySql);
        var gameClient = new GameClientService();
        GameClientService.Current = gameClient;
        var armoryService = new ArmoryService(mySql, context, gameClient);
        var supportService = new SupportService(mySql, gm, context, gameClient);
        var playerMapService = new PlayerMapService(mySql, gameClient);
        var characterService = new CharacterService(mySql, gm, context, gameClient);
        var moderationService = new ModerationService(mySql, gm, context);
        var teleportService = new TeleportService(mySql, gm, context, gameClient);
        var worldEditService = new WorldEditService(mySql, context, gameClient);

        // Restauration du serveur actif au démarrage.
        context.Active = profiles.GetActive();
        context.Status = context.HasActive
            ? $"Serveur actif : {context.DisplayName}"
            : "Aucun serveur actif — commencez par créer un profil.";

        var serverConfig = new ServerConfigViewModel(profiles, context, mySql, ssh, gm, gameClient);
        var gmConsole = new GmConsoleViewModel(gm, context);
        var sqlConsole = new SqlEditorViewModel(sqlEditor, gm, context);
        var catalog = new ItemCatalogViewModel(itemCatalog, context, gameClient);
        var accounts = new AccountsViewModel(accountService, context);

        // Le sélecteur d'objets reçoit sa propre instance de catalogue, pour que la
        // recherche du module ne soit pas écrasée par celle d'une boîte de dialogue.
        var mail = new MailViewModel(mailService, context,
            () => new ItemCatalogViewModel(itemCatalog, context, gameClient));
        var armory = new ArmoryViewModel(armoryService, context);
        var tickets = new TicketsViewModel(supportService, context);
        var restore = new RestoreViewModel(supportService, context);
        var playerMap = new PlayerMapViewModel(playerMapService, context);
        var charactersVm = new CharactersViewModel(characterService, context);
        var moderation = new ModerationViewModel(moderationService, context);
        var teleport = new TeleportViewModel(teleportService, context);
        var worldEdit = new WorldEditViewModel(worldEditService, context);
        var main = new MainViewModel(context, serverConfig, gmConsole, sqlConsole, catalog, accounts, mail, armory, tickets, restore, playerMap, charactersVm, moderation, teleport, worldEdit);

        new MainWindow { DataContext = main }.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
