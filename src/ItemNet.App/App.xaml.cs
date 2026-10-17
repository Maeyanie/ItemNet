using System.Windows;
using System.Windows.Threading;
using ItemNet.App.Services;
using ItemNet.App.ViewModels;

namespace ItemNet.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var settings = AppSettings.Load();
        var firstRun = string.IsNullOrEmpty(settings.GamePath) && string.IsNullOrEmpty(settings.Mo2Instance);
        if (firstRun)
        {
            settings.ApplyFirstRunDefaults();
            settings.Save();
        }

        var window = new MainWindow(new MainViewModel(settings));
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "ItemNet: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
