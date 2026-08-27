using System.Windows;

namespace AirCode;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Global unhandled exception guard
        DispatcherUnhandledException += (s, ex) =>
        {
            ex.Handled = true;
            System.Diagnostics.Debug.WriteLine($"Unhandled: {ex.Exception}");
        };
    }
}
