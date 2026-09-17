using System.Windows;
using AzerothManager.Models;
using AzerothManager.ViewModels;

namespace AzerothManager.Views;

/// <summary>
/// Sélecteur d'objets, qui héberge la vue du catalogue sans la réécrire (§19).
/// Rend l'objet choisi et la quantité saisie.
/// </summary>
public partial class ItemPickerWindow : Window
{
    public ItemSummary? PickedItem { get; private set; }
    public int PickedCount { get; private set; } = 1;

    public ItemPickerWindow(ItemCatalogViewModel catalog)
    {
        InitializeComponent();
        DataContext = catalog;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ItemCatalogViewModel vm || vm.Selected is null)
        {
            MessageBox.Show("Sélectionnez un objet dans la liste.", "Choix requis",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PickedItem = vm.Selected;
        PickedCount = int.TryParse(QuantityBox.Text, out var n) && n > 0 ? n : 1;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
