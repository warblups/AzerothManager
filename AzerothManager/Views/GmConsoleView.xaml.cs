using System.Windows.Controls;
using System.Windows.Input;
using AzerothManager.ViewModels;

namespace AzerothManager.Views;

public partial class GmConsoleView : UserControl
{
    public GmConsoleView() => InitializeComponent();

    /// <summary>Entrée envoie, les flèches rappellent l'historique (§17 : clavier d'abord).</summary>
    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not GmConsoleViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.RecallHistory(-1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                vm.RecallHistory(+1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
        }
    }
}
