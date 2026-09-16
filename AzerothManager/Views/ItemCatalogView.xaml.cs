using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using AzerothManager.ViewModels;

namespace AzerothManager.Views;

public partial class ItemCatalogView : UserControl
{
    public ItemCatalogView() => InitializeComponent();

    /// <summary>
    /// Le tri est délégué au serveur : WPF trierait la page affichée, soit cent lignes
    /// sur quarante-six mille, et donnerait un classement faux.
    /// </summary>
    private async void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ItemCatalogViewModel vm) return;

        var column = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(column)) return;

        await vm.SortByAsync(column);

        // L'indicateur visuel doit refléter le tri réellement appliqué par le serveur.
        foreach (var c in ((DataGrid)sender).Columns) c.SortDirection = null;
        e.Column.SortDirection = vm.SortDescending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ItemCatalogViewModel vm && vm.ShowDetailCommand.CanExecute(null))
            vm.ShowDetailCommand.Execute(null);
    }
}
