using System.Windows;
using AmbientLight.Host.Platform;

namespace AmbientLight.App;

/// <summary>
/// WPF entry point. The app lives in the tray (<see cref="ShutdownMode.OnExplicitShutdown"/>): closing the
/// Settings window never exits; the tray menu's Exit, <c>AmbientLight.exe --exit</c>, a Windows sign-out or a fatal
/// startup error do.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "AmbientLight";

    /// <summary>Command-line switch that makes a running instance exit.</summary>
    public const string ExitArgument = "--exit";

    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "AmbientLight.exe --exit" asks a running instance to shut down cleanly (for scripts replacing the exe).
        var exitRequested = e.Args.Contains(ExitArgument, StringComparer.OrdinalIgnoreCase);
        var instance = SingleInstance.TryAcquire(InstanceName, exitRequested);
        if (instance is null || exitRequested)
        {
            // Another instance is running and has been signalled (open Settings, or exit); or there is nothing to exit.
            instance?.Dispose();
            Shutdown(0);
            return;
        }

        try
        {
            _host = AppHost.Start(this, instance, e.Args);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            instance.Dispose();
            MessageBox.Show(
                $"Ambient Light could not start:\n\n{exception.Message}",
                "Ambient Light",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stops the pipeline (the LED strip receives its blackout frame), saves pending settings, removes the tray icon.
        _host?.Dispose();
        _host = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows may terminate the process right after sign-out/shutdown notifications, so clean up now.
        _host?.Dispose();
        _host = null;
        base.OnSessionEnding(e);
        Shutdown(0);
    }
}
