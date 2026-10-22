using System.Collections.Specialized;
using Avalonia.Controls;
using ZipP2P.App.ViewModels;

namespace ZipP2P.App.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LogsViewModel vm)
                vm.Entries.CollectionChanged += OnEntriesChanged;
        };
    }

    // Acompanha o fim do log (como um terminal) quando novas entradas chegam.
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && sender is System.Collections.IList { Count: > 0 } list)
            EntriesList.ScrollIntoView(list.Count - 1);
    }
}
