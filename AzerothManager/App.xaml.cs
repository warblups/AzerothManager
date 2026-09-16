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

        // Restauration du serveur actif au démarrage.
        context.Active = profiles.GetActive();
        context.Status = context.HasActive
            ? $"Serveur actif : {context.DisplayName}"
            : "Aucun serveur actif — commencez par créer un profil.";

        var serverConfig = new ServerConfigViewModel(profiles, context, mySql, ssh, gm);
        var gmConsole = new GmConsoleViewModel(gm, context);
        var sqlConsole = new SqlEditorViewModel(sqlEditor, gm, context);
        var catalog = new ItemCatalogViewModel(itemCatalog, context);
        var main = new MainViewModel(context, serverConfig, gmConsole, sqlConsole, catalog);

        new MainWindow { DataContext = main }.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
