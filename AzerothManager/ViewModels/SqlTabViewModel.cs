using System.Data;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>Un onglet de l'éditeur SQL : sa requête, sa base cible, son résultat.</summary>
public partial class SqlTabViewModel : ObservableObject
{
    private readonly SqlEditorService _sql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;

    [ObservableProperty] private string _title = "Requête 1";
    [ObservableProperty] private string _sqlText = "";
    [ObservableProperty] private MySqlService.Db _targetDatabase = MySqlService.Db.Characters;
    [ObservableProperty] private DataTable? _result;
    [ObservableProperty] private string _status = "Prêt.";
    [ObservableProperty] private bool _isBusy;

    /// <summary>.reload proposé après une écriture dans world (§9), null sinon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReloadSuggestion))]
    private string? _reloadSuggestion;

    public bool HasReloadSuggestion => !string.IsNullOrEmpty(ReloadSuggestion);

    public Array Databases => Enum.GetValues<MySqlService.Db>();

    public SqlTabViewModel(SqlEditorService sql, GmCommandService gm, ServerContext context)
    {
        _sql = sql;
        _gm = gm;
        _context = context;
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (IsBusy) return;

        // Ne jamais échouer en silence : l'absence de retour a masqué un défaut de liaison.
        if (string.IsNullOrWhiteSpace(SqlText))
        {
            Status = "Requête vide.";
            return;
        }
        if (!_context.HasActive)
        {
            Status = "Aucun serveur actif.";
            return;
        }

        IsBusy = true;
        ReloadSuggestion = null;
        Status = "Exécution…";
        try
        {
            var query = SqlText.Trim();

            // Annonce avant écriture : combien de lignes sont visées (§17).
            if (SqlStatement.Classify(query) == SqlKind.Destructive)
            {
                var estimate = await _sql.EstimateAsync(TargetDatabase, query);
                if (estimate is not null) Status = $"{estimate} ligne(s) concernée(s), exécution…";
            }

            var execution = await _sql.ExecuteAsync(TargetDatabase, query, ConfirmCommitAsync);

            Result = execution.Table;
            Status = execution.Message;

            if (execution.Committed)
                ReloadSuggestion = SqlEditorService.SuggestReload(TargetDatabase, query);
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Validation explicite après exécution en transaction (§15, §21).</summary>
    private Task<bool> ConfirmCommitAsync(int rows)
    {
        var kind = SqlStatement.Classify(SqlText);
        var warning = kind == SqlKind.Structural
            ? "\n\nCette opération modifie la structure de la base et n'est pas réversible."
            : "";

        var answer = System.Windows.MessageBox.Show(
            $"{rows} ligne(s) seront modifiée(s) sur « {_context.DisplayName} » ({TargetDatabase}).{warning}\n\nValider la transaction ?",
            "Confirmation d'écriture",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        return Task.FromResult(answer == System.Windows.MessageBoxResult.Yes);
    }

    [RelayCommand]
    private async Task RunReloadAsync()
    {
        if (string.IsNullOrEmpty(ReloadSuggestion)) return;
        IsBusy = true;
        try
        {
            var r = await _gm.ExecuteAsync(ReloadSuggestion);
            Status = r.Success
                ? $"{ReloadSuggestion} exécuté — {r.Output.ReplaceLineEndings(" ").Trim()}"
                : $"{ReloadSuggestion} a échoué — {r.Output}";
            if (r.Success) ReloadSuggestion = null;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Result is null || Result.Rows.Count == 0)
        {
            Status = "Rien à exporter.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"export_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        SqlEditorService.ExportCsv(Result, dialog.FileName);
        Status = $"Exporté : {dialog.FileName}";
    }
}
