using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using AzerothManager.ViewModels;

namespace AzerothManager.Views;

public partial class LogsView : UserControl
{
    private bool _scrollQueued;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LogsViewModel previous)
            ((INotifyCollectionChanged)previous.Lines).CollectionChanged -= OnLinesChanged;

        if (e.NewValue is LogsViewModel current)
            ((INotifyCollectionChanged)current.Lines).CollectionChanged += OnLinesChanged;
    }

    /// <summary>
    /// Un journal se lit par la fin : après un chargement ou un changement de filtre, la
    /// grille se place sur la ligne la plus récente.
    ///
    /// Le remplissage produit un événement par ligne ; le défilement est donc reporté en
    /// priorité basse et coalescé, sinon on repositionnerait la grille des milliers de fois.
    /// </summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollQueued) return;
        _scrollQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            if (LinesGrid.Items.Count == 0) return;
            LinesGrid.ScrollIntoView(LinesGrid.Items[^1]!);
        });
    }
}
