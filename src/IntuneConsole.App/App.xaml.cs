using System.Windows;
using System.Windows.Threading;
using IntuneConsole.App.Services;

namespace IntuneConsole.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var window = new MainWindow();
        MainWindow = window;
        Session.Current.Configure(Session.Current.Settings);
        window.Show();
        window.Start();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Shell.Status($"Something went wrong: {e.Exception.Message}", isError: true);
        e.Handled = true;
    }
}
