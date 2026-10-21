using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ItemNet.App.Services;
using ItemNet.App.ViewModels;

namespace ItemNet.App;

public partial class MainWindow : Window
{
    private static readonly string[] ViewLabels = ["Front", "Right", "Back", "Left"];
    private readonly MainViewModel _vm;
    private BatchWindow? _batch;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        RestoreLayout();
        Closing += (_, _) => SaveLayout();
        Loaded += async (_, _) => await _vm.ReloadAsync();
        // Ctrl+Enter saves and moves on, even from inside the description box (which takes plain Enter).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _vm.SaveAndNextCommand.CanExecute(null))
            {
                _vm.SaveAndNextCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void RestoreLayout()
    {
        var layout = _vm.Settings.Layout;
        // Size and maximised state only: the main window always opens centred.
        WindowBounds.Apply(this, layout.Main, position: false);
        if (layout.ListWidth >= ListColumn.MinWidth) ListColumn.Width = new GridLength(layout.ListWidth);
        if (layout.ListColumns.Count == ItemGrid.Columns.Count)
            for (var i = 0; i < ItemGrid.Columns.Count; i++)
                if (layout.ListColumns[i] > 10) ItemGrid.Columns[i].Width = layout.ListColumns[i];
        SkyrimNetExpander.IsExpanded = layout.SkyrimNetExpanded;
        RawReplyExpander.IsExpanded = layout.RawReplyExpanded;
    }

    private void SaveLayout()
    {
        var layout = _vm.Settings.Layout;
        layout.Main = WindowBounds.Capture(this);
        layout.ListWidth = ListColumn.ActualWidth;
        layout.ListColumns = ItemGrid.Columns.Select(c => c.ActualWidth).ToList();
        layout.SkyrimNetExpanded = SkyrimNetExpander.IsExpanded;
        layout.RawReplyExpanded = RawReplyExpander.IsExpanded;
        try { _vm.Settings.Save(); } catch (Exception e) { Services.Log.Write("Saving the layout failed: " + e.Message); }
    }

    private void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetMultiSelection(((ListView)sender).SelectedItems.Cast<ItemRowViewModel>());

    private void OnBatch(object sender, RoutedEventArgs e)
    {
        if (_vm.Workspace is null) return;
        if (_batch is null || !_batch.IsLoaded)
        {
            var batch = _batch = new BatchWindow(new BatchViewModel(_vm)) { Owner = this };
            WindowBounds.Apply(batch, _vm.Settings.Layout.Batch);
            batch.Closing += (_, a) => { if (!a.Cancel) _vm.Settings.Layout.Batch = WindowBounds.Capture(batch); };
            batch.Closed += (_, _) => { _batch = null; _vm.Settings.Save(); };
            batch.Show();
        }
        else _batch.Activate();
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        if (_batch?.IsRunning == true)
        {
            MessageBox.Show(this, "A batch is running. Let it finish (or stop it) before syncing.", "Sync");
            return;
        }
        await _vm.SyncAsync(this);
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_vm.Settings.Clone(), _vm.SkyrimNetConfigDir()) { Owner = this };
        WindowBounds.Apply(dialog, _vm.Settings.Layout.Settings);
        WindowBounds? bounds = null;
        dialog.Closing += (_, _) => bounds = WindowBounds.Capture(dialog);
        var ok = dialog.ShowDialog() == true;
        if (ok)
        {
            App.ApplyTheme(dialog.Result.Theme);
            await _vm.ApplySettingsAsync(dialog.Result);
        }
        // Whichever settings object is live now (the dialog's copy if it was accepted) keeps the size.
        _vm.Settings.Layout.Settings = bounds;
        _vm.Settings.Save();
    }

    /// <summary>Opens a clicked view (ours, or SkyrimNet's stored one) at full size.</summary>
    private void OnViewClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image { Source: not null, Tag: string tag }) return;
        var parts = tag.Split(':');
        var skyrimNet = parts[0] == "skyrimnet";
        var source = skyrimNet ? _vm.SkyrimNetViews : _vm.Views;
        var views = source.Select((img, i) => (ViewLabels[i], img)).ToList();
        var caption = (_vm.Selected?.Name ?? "Item") + (skyrimNet ? " (SkyrimNet's image)" : "");
        new ImageViewerWindow(views, int.Parse(parts[1]), caption) { Owner = this }.Show();
        e.Handled = true;
    }
}
