using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Consultation des journaux du serveur, et mise en regard des actions de l'administrateur.
///
/// Ce module ne suit pas les journaux en direct : c'est l'Updater qui le fait (§3). Il lit
/// une fenêtre de la fin du fichier, la filtre, et **y entrelace l'historique local** des
/// requêtes SQL et des commandes GM. La valeur est là : voir la réponse du serveur à côté de
/// l'action qui l'a provoquée, trois jours plus tard.
///
/// Le filtrage se fait sur la fenêtre déjà chargée, pas en relisant le fichier : la taille de
/// cette fenêtre est explicite dans l'interface, et le nombre de lignes affichées est plafonné
/// — un Server.log de plusieurs dizaines de mégaoctets ne rentre pas dans une grille.
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    private readonly LogService _logs;
    private readonly ServerContext _context;
    private CancellationTokenSource? _pending;

    /// <summary>Tout ce qui a été lu, avant filtrage : journal et actions confondus.</summary>
    private readonly List<LogLine> _loaded = [];

    /// <summary>Plafond d'affichage. Au-delà, on garde la fin : c'est elle qui intéresse.</summary>
    private const int MaxDisplayedLines = 5000;

    public ObservableCollection<LogFileInfo> Files { get; } = [];
    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>
    /// Quantité lue à la fin du fichier. Explicite plutôt que devinée : l'utilisateur doit
    /// savoir que ce qu'il filtre n'est pas tout le journal.
    /// </summary>
    public IReadOnlyList<NamedValue> Windows { get; } =
    [
        new(200 * 1024, "Fin : 200 Ko"),
        new(1024 * 1024, "Fin : 1 Mo"),
        new(4 * 1024 * 1024, "Fin : 4 Mo"),
    ];

    [ObservableProperty] private LogFileInfo? _selectedFile;
    [ObservableProperty] private NamedValue? _window;
    [ObservableProperty] private LogLine? _selectedLine;

    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showDebug;
    [ObservableProperty] private bool _showOther = true;
    [ObservableProperty] private bool _showAdminActions = true;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status =
        "« Chercher les journaux » liste les fichiers .log du serveur actif.";
    [ObservableProperty] private string _fileHeader = "";

    /// <summary>
    /// Faux quand aucune ligne lue ne porte d'horodatage : l'appender peut être configuré
    /// sans le drapeau 1. La corrélation est alors impossible, et l'interface le dit au lieu
    /// de produire un ordre inventé.
    /// </summary>
    [ObservableProperty] private bool _canCorrelate = true;

    /// <summary>Emplacements sondés, montrés quand la recherche de journaux ne donne rien.</summary>
    [ObservableProperty] private string _searchedDirectories = "";

    public LogsViewModel(LogService logs, ServerContext context)
    {
        _logs = logs;
        _context = context;
        Window = Windows[1];
    }

    // ------------------------------------------------------------------ découverte

    [RelayCommand]
    private async Task RefreshFilesAsync()
    {
        if (!_context.HasActive)
        {
            Status = "Aucun serveur actif. Sélectionnez un profil dans Configuration des serveurs.";
            return;
        }

        var previous = SelectedFile?.Path;

        IsBusy = true;
        Status = "Recherche des journaux…";
        try
        {
            SearchedDirectories = string.Join("   ·   ", _logs.CandidateDirectories());

            var found = await _logs.ListAsync();

            Files.Clear();
            foreach (var f in found) Files.Add(f);

            Status = Files.Count == 0
                ? "Aucun fichier .log trouvé. Vérifiez le dossier des journaux du profil."
                : $"{Files.Count} journal(aux) trouvé(s).";
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }

        // Assignée hors du bloc précédent : elle déclenche la lecture du journal, qui gère
        // son propre IsBusy. L'y laisser ferait retomber l'indicateur avant la fin.
        if (Files.Count > 0)
            SelectedFile = Files.FirstOrDefault(f => f.Path == previous) ?? Files[0];
    }

    partial void OnSelectedFileChanged(LogFileInfo? value)
    {
        if (value is not null) _ = LoadAsync();
    }

    partial void OnWindowChanged(NamedValue? value)
    {
        if (SelectedFile is not null) _ = LoadAsync();
    }

    // ------------------------------------------------------------------ lecture

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (SelectedFile is not { } file) return;
        if (!_context.HasActive)
        {
            Status = "Aucun serveur actif.";
            return;
        }

        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        IsBusy = true;
        Status = $"Lecture de {file.Name}…";
        try
        {
            var maxBytes = Window?.Value ?? 1024 * 1024;
            var result = await _logs.ReadTailAsync(file, maxBytes, cts.Token);
            if (cts.IsCancellationRequested) return;

            CanCorrelate = result.HasTimestamps;

            _loaded.Clear();
            _loaded.AddRange(result.Lines);

            // Les actions administrateur ne sont replacées que si le journal est horodaté ;
            // sinon on n'a aucun point commun pour les situer.
            if (result is { HasTimestamps: true, First: { } first, Last: { } last })
            {
                var actions = _logs.ReadAdminActions(first, last);

                // OrderBy est stable : à horodatage égal, la ligne du journal reste avant
                // l'action, et l'ordre du fichier est préservé.
                var merged = _loaded.Concat(actions)
                    .OrderBy(l => l.At ?? first)
                    .ToList();

                _loaded.Clear();
                _loaded.AddRange(merged);
            }

            var window = result.Truncated
                ? $"{Bytes(result.BytesRead)} lus en fin de fichier"
                : "fichier entier";
            FileHeader = $"{file.Name} — {file.SizeText}, écrit le {file.ModifiedText} — {window}";

            ApplyFilters();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "Lecture impossible : " + ex.Message;
        }
        finally
        {
            if (_pending == cts) IsBusy = false;
        }
    }

    private static string Bytes(long value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024d / 1024:0.0} Mo",
        >= 1024 => $"{value / 1024d:0} Ko",
        _ => $"{value} o"
    };

    // ------------------------------------------------------------------ filtrage

    partial void OnShowErrorChanged(bool value) => ApplyFilters();
    partial void OnShowWarningChanged(bool value) => ApplyFilters();
    partial void OnShowInfoChanged(bool value) => ApplyFilters();
    partial void OnShowDebugChanged(bool value) => ApplyFilters();
    partial void OnShowOtherChanged(bool value) => ApplyFilters();
    partial void OnShowAdminActionsChanged(bool value) => ApplyFilters();
    partial void OnSearchChanged(string value) => ApplyFilters();

    /// <summary>
    /// Réapplique les filtres sur la fenêtre déjà lue, sans retoucher au fichier : changer un
    /// niveau ou un mot cherché ne doit pas relancer un transfert SFTP.
    /// </summary>
    private void ApplyFilters()
    {
        if (_loaded.Count == 0)
        {
            Lines.Clear();
            return;
        }

        var text = Search?.Trim() ?? "";
        var kept = _loaded.Where(l => Passes(l, text)).ToList();

        // On garde la fin : dans un journal, c'est le plus récent qui intéresse.
        var overflow = kept.Count - MaxDisplayedLines;
        if (overflow > 0) kept = kept.GetRange(overflow, MaxDisplayedLines);

        Lines.Clear();
        foreach (var l in kept) Lines.Add(l);

        var errors = kept.Count(l => l.Severity == LogSeverity.Error);
        var actions = kept.Count(l => l.IsAdmin);

        var parts = new List<string> { $"{kept.Count} ligne(s) sur {_loaded.Count} lue(s)" };
        if (errors > 0) parts.Add($"{errors} erreur(s)");
        if (actions > 0) parts.Add($"{actions} action(s) administrateur");
        if (overflow > 0) parts.Add($"{overflow} ligne(s) plus ancienne(s) masquée(s)");

        Status = string.Join("  ·  ", parts);
    }

    private bool Passes(LogLine line, string text)
    {
        if (line.IsAdmin)
        {
            if (!ShowAdminActions) return false;
        }
        else
        {
            var levelOk = line.Severity switch
            {
                LogSeverity.Error => ShowError,
                LogSeverity.Warning => ShowWarning,
                LogSeverity.Info => ShowInfo,
                LogSeverity.Debug => ShowDebug,
                _ => ShowOther
            };
            if (!levelOk) return false;
        }

        return text.Length == 0
            || line.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
            || line.Source.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ commandes

    [RelayCommand]
    private void CopySelection()
    {
        if (SelectedLine is not { } line) return;
        try
        {
            System.Windows.Clipboard.SetText($"{line.TimeText}  {line.Source}  {line.Text}");
            Status = "Ligne copiée dans le presse-papiers.";
        }
        catch (Exception ex)
        {
            Status = "Copie impossible : " + ex.Message;
        }
    }

    /// <summary>Ne montre que les erreurs et les avertissements : le premier geste après un incident.</summary>
    [RelayCommand]
    private void ShowProblemsOnly()
    {
        ShowError = true;
        ShowWarning = true;
        ShowInfo = false;
        ShowDebug = false;
        ShowOther = false;
    }

    [RelayCommand]
    private void ShowEverything()
    {
        ShowError = true;
        ShowWarning = true;
        ShowInfo = true;
        ShowDebug = true;
        ShowOther = true;
    }
}
