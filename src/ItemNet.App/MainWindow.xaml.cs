using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetMultiSelection(((ListView)sender).SelectedItems.Cast<ItemRowViewModel>());

    private void OnBatch(object sender, RoutedEventArgs e)
    {
        if (_vm.Workspace is null) return;
        if (_batch is null || !_batch.IsLoaded)
        {
            _batch = new BatchWindow(new BatchViewModel(_vm)) { Owner = this };
            _batch.Closed += (_, _) => _batch = null;
            _batch.Show();
        }
        else _batch.Activate();
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_vm.Settings.Clone(), _vm.SkyrimNetConfigDir()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        App.ApplyTheme(dialog.Result.Theme);
        await _vm.ApplySettingsAsync(dialog.Result);
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
