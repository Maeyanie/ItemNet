using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using ItemNet.App.ViewModels;

namespace ItemNet.App;

public partial class BatchWindow : Window
{
    private readonly BatchViewModel _vm;

    public bool IsRunning => _vm.IsRunning;

    public BatchWindow(BatchViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        // The main list may have been re-filtered while this window was in the background.
        Activated += (_, _) => _vm.UpdateScope();
        // Follow the newest log line.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsRunning)
        {
            var answer = MessageBox.Show(this, "A batch is running. Stop it and close?", "ItemNet batch",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _vm.StopCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
