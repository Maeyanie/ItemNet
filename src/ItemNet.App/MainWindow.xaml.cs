using System.Windows;
using ItemNet.App.ViewModels;

namespace ItemNet.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => await _vm.ReloadAsync();
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_vm.Settings.Clone(), _vm.SkyrimNetConfigDir()) { Owner = this };
        if (dialog.ShowDialog() == true) await _vm.ApplySettingsAsync(dialog.Result);
    }
}
