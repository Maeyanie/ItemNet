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
        Log.Start();
        Log.Write($"ItemNet {typeof(App).Assembly.GetName().Version} starting; .NET {Environment.Version}; under MO2: {AppSettings.RunningUnderMo2()}");
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("Unhandled: " + a.ExceptionObject);

        var settings = AppSettings.Load();
        Log.Write($"Settings: mode {settings.Mode}, game '{settings.GamePath}', MO2 '{settings.Mo2Instance}' profile '{settings.Mo2Profile}'");
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
        Log.Write("Unhandled UI exception: " + e.Exception);
        MessageBox.Show(e.Exception.ToString(), "ItemNet: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
