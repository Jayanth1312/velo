using System;
using Microsoft.UI.Xaml;

namespace Velo.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        Log.Write($"==== App start. Log at {Log.Path} ====");
        UnhandledException += (_, e) =>
        {
            Log.Ex("App.UnhandledException", e.Exception);
            e.Handled = true; // keep the window up so we can read the log
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"AppDomain.UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Ex("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Write("OnLaunched: creating MainWindow");
        try
        {
            _window = new MainWindow();
            _window.Activate();
            Log.Write("OnLaunched: window activated");
        }
        catch (Exception ex)
        {
            Log.Ex("OnLaunched", ex);
            throw;
        }
    }
}
