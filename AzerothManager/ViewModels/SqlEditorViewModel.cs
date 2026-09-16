using System.Collections.ObjectModel;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;

namespace AzerothManager.ViewModels;

public sealed record SqlFavorite(int Id, string Name, string Query);

/// <summary>Éditeur SQL multi-onglets, avec favoris et historique (§15).</summary>
public partial class SqlEditorViewModel : ObservableObject
{
    private readonly SqlEditorService _sql;
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;
    private int _counter;

    public ObservableCollection<SqlTabViewModel> Tabs { get; } = [];
    public ObservableCollection<SqlFavorite> Favorites { get; } = [];
    public ObservableCollection<string> History { get; } = [];

    [ObservableProperty] private SqlTabViewModel? _selectedTab;

    public SqlEditorViewModel(SqlEditorService sql, GmCommandService gm, ServerContext context)
    {
        _sql = sql;
        _gm = gm;
        _context = context;
        NewTab();
        LoadFavorites();
        LoadHistory();
    }

    [RelayCommand]
    private void NewTab()
    {
        var tab = new SqlTabViewModel(_sql, _gm, _context) { Title = $"Requête {++_counter}" };
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(SqlTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        Tabs.Remove(tab);
        if (Tabs.Count == 0) NewTab();
        else SelectedTab ??= Tabs[^1];
    }

    [RelayCommand]
    private void Refresh()
    {
        LoadFavorites();
        LoadHistory();
    }

    [RelayCommand]
    private void SaveFavorite()
    {
        if (SelectedTab is null || string.IsNullOrWhiteSpace(SelectedTab.SqlText)) return;

        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Favorites (Name, Query, Database, CreatedAt)
            VALUES ($n, $q, $d, $c)
            """;
        cmd.Parameters.AddWithValue("$n", SelectedTab.Title);
        cmd.Parameters.AddWithValue("$q", SelectedTab.SqlText.Trim());
        cmd.Parameters.AddWithValue("$d", SelectedTab.TargetDatabase.ToString());
        cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();

        LoadFavorites();
        SelectedTab.Status = $"Favori « {SelectedTab.Title} » enregistré.";
    }

    [RelayCommand]
    private void DeleteFavorite(SqlFavorite? favorite)
    {
        if (favorite is null) return;
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "DELETE FROM Favorites WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", favorite.Id);
        cmd.ExecuteNonQuery();
        LoadFavorites();
    }

    /// <summary>Charge une requête (favori ou historique) dans un nouvel onglet.</summary>
    [RelayCommand]
    private void Load(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        NewTab();
        SelectedTab!.SqlText = query;
    }

    private void LoadFavorites()
    {
        Favorites.Clear();
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Query FROM Favorites ORDER BY Name";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) Favorites.Add(new SqlFavorite(rd.GetInt32(0), rd.GetString(1), rd.GetString(2)));
    }

    private void LoadHistory()
    {
        History.Clear();
        using var cnx = LocalDatabase.Open();
        using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT Content FROM History WHERE Kind='sql' ORDER BY Id DESC LIMIT 30";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) History.Add(rd.GetString(0));
    }
}
