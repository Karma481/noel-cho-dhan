using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Threading;
using AmbientLight.App.Interop;
using AmbientLight.App.Settings;
using AmbientLight.App.Tray;
using AmbientLight.Core.Settings;
using AmbientLight.Host;
using AmbientLight.Host.Pipeline;
using AmbientLight.Host.Platform;
using AmbientLight.Host.Settings;
using AmbientLight.Serial;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace AmbientLight.App;

/// <summary>
/// Composition root: builds every service at startup, wires settings → orchestrator → tray, and tears it all
/// down in reverse order at exit. Lives on the UI thread.
/// </summary>
internal sealed partial class AppHost : IDisposable
{
    private const string PauseGesture = "Ctrl+Alt+L";

    /// <summary>Retry period for adding the tray icon while the taskbar does not exist yet.</summary>
    private static readonly TimeSpan TrayRetryInterval = TimeSpan.FromSeconds(5);

    private readonly Application _app;
    private readonly Dispatcher _dispatcher;
    private readonly SingleInstance _instance;
    private readonly SerilogLoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly AppPaths _paths;
    private readonly SettingsCoordinator _coordinator;
    private readonly SettingsHolder _settings;
    private readonly IRegistryValueStore _registry;
    private readonly string _executablePath;
    private readonly StartupRegistration _startup;
    private readonly PipelineServices _services;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly FullscreenMonitor _fullscreen;
    private readonly TrayIconController _tray;
    private readonly HotkeyManager _hotkeys;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _trayRetryTimer;
    private int _stateUpdateQueued;
    private bool _preferPowerSavingGpu;
    private bool _disposed;

    private AppHost(Application app, SingleInstance instance, string[] args)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
        _instance = instance;
        _paths = AppPaths.Default;
        Directory.CreateDirectory(_paths.DataDirectory);

        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                _paths.LogFilePattern,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _loggerFactory = new SerilogLoggerFactory(serilog, dispose: true);
        _logger = _loggerFactory.CreateLogger<AppHost>();
        var version = typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "?";
        AppLog.Starting(_logger, version, Environment.OSVersion.VersionString);

        HookUnhandledExceptions();

        // Settings first: everything else is configured from them.
        _coordinator = new SettingsCoordinator(_paths.SettingsFile, _loggerFactory.CreateLogger<SettingsCoordinator>());
        var load = _coordinator.Load();
        _settings = new SettingsHolder(load.Settings);
        _coordinator.Attach(_settings);

        // Registry integration, before anything creates a DXGI factory.
        _executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        _registry = new CurrentUserRegistry();
        _startup = new StartupRegistration(_registry, _executablePath);
        TryRegistry(() =>
        {
            if (_startup.RepairIfMoved())
            {
                AppLog.AutostartRepaired(_logger, _executablePath);
            }
        });
        _preferPowerSavingGpu = load.Settings.Performance.PreferPowerSavingGpu;
        EnsureGpuPreference(load.Settings);

        // Pipeline.
        _services = new PipelineServices(_settings, _loggerFactory);
        _orchestrator = new PipelineOrchestrator(_settings, _services.Stages, _loggerFactory.CreateLogger<PipelineOrchestrator>());
        _orchestrator.StateChanged += OnPipelineStateChanged;

        _fullscreen = new FullscreenMonitor(FullscreenDetector.IsExclusiveFullscreenActive);
        _fullscreen.Changed += OnFullscreenChanged;
        _settings.Published += OnSettingsPublished;

        // Shell integration.
        _tray = new TrayIconController(ShowSettings, TogglePause, Exit, PauseGesture);
        CreateTrayIcon();
        _hotkeys = new HotkeyManager();
        RegisterHotkey('L', "pause / resume", TogglePause);
        RegisterHotkey('O', "overlay on / off", ToggleOverlay);
        _instance.ActivationRequested += OnActivationRequested;
        _instance.ExitRequested += OnExitRequested;

        // Go.
        _fullscreen.SetEnabled(load.Settings.Performance.PauseOverlayInExclusiveFullscreen);
        _orchestrator.Start();
        _tray.Update(_orchestrator.State);

        ReportLoadOutcome(load);
        var autostarted = args.Contains(StartupRegistration.AutostartArgument, StringComparer.OrdinalIgnoreCase);
        if (!autostarted)
        {
            // Launched by hand: show that the app is alive. At sign-in it stays quietly in the tray.
            ShowSettings();
        }
    }

    /// <summary>Builds and starts the app. Throws when a required piece cannot be created.</summary>
    public static AppHost Start(Application app, SingleInstance instance, string[] args)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(args);
        return new AppHost(app, instance, args);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppLog.Stopping(_logger);

        _instance.ActivationRequested -= OnActivationRequested;
        _instance.ExitRequested -= OnExitRequested;
        _trayRetryTimer?.Stop();
        _hotkeys.Dispose();
        _settingsWindow?.Close();
        _settings.Published -= OnSettingsPublished;
        _fullscreen.Changed -= OnFullscreenChanged;
        _fullscreen.Dispose();

        // Producers first; the LED output sends its blackout frame as it stops.
        _orchestrator.StateChanged -= OnPipelineStateChanged;
        _orchestrator.Dispose();
        _services.Dispose();

        _coordinator.Dispose();
        _tray.Dispose();
        _instance.Dispose();
        _loggerFactory.Dispose();
    }

    private void ShowSettings()
    {
        if (_disposed)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(
            _settings,
            _orchestrator,
            _services,
            _coordinator,
            _startup,
            SerialOutputService.GetAvailablePorts,
            _hotkeys.Registrations,
            _loggerFactory.CreateLogger<SettingsViewModel>());
        var window = new SettingsWindow(viewModel);
        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _settingsWindow = null;
        };
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    private void CreateTrayIcon()
    {
        if (_tray.TryCreate(out var error))
        {
            _trayRetryTimer?.Stop();
            _trayRetryTimer = null;
            return;
        }

        if (_trayRetryTimer is null)
        {
            // Typically Explorer is not up yet at sign-in. Keep running (hotkeys work, relaunching the exe opens
            // Settings) and add the icon as soon as the taskbar exists.
            AppLog.TrayUnavailable(_logger, error?.Message ?? "unknown error");
            _trayRetryTimer = new DispatcherTimer(TrayRetryInterval, DispatcherPriority.Background, (_, _) => CreateTrayIcon(), _dispatcher);
        }
    }

    private void TogglePause()
    {
        var paused = _orchestrator.TogglePause();
        AppLog.PauseToggled(_logger, paused);
    }

    private void ToggleOverlay()
    {
        var current = _settings.Current.Settings;
        var next = current with { Overlay = current.Overlay with { Enabled = !current.Overlay.Enabled } };
        if (_settings.TryPublish(next, out var issues))
        {
            AppLog.OverlayToggled(_logger, next.Overlay.Enabled);
        }
        else
        {
            AppLog.OverlayToggleRejected(_logger, string.Join("; ", issues));
        }
    }

    private void Exit() => _app.Shutdown(0);

    private void RegisterHotkey(char key, string description, Action action)
    {
        var registration = _hotkeys.RegisterCtrlAlt(key, description, action);
        if (!registration.IsRegistered)
        {
            AppLog.HotkeyUnavailable(_logger, registration.Gesture);
        }
    }

    private void OnSettingsPublished(object? sender, SettingsSnapshot snapshot)
    {
        var performance = snapshot.Settings.Performance;
        _fullscreen.SetEnabled(performance.PauseOverlayInExclusiveFullscreen);

        // Only on the off → on transition: a slider drag publishes many snapshots.
        if (performance.PreferPowerSavingGpu && !_preferPowerSavingGpu)
        {
            EnsureGpuPreference(snapshot.Settings);
        }

        _preferPowerSavingGpu = performance.PreferPowerSavingGpu;
    }

    private void OnFullscreenChanged(object? sender, bool active)
    {
        AppLog.FullscreenChanged(_logger, active);
        _orchestrator.SetExclusiveFullscreen(active);
    }

    private void OnPipelineStateChanged(object? sender, PipelineState state)
    {
        // May arrive from the fullscreen monitor's timer thread; coalesce and read the latest state on the UI thread.
        if (Interlocked.Exchange(ref _stateUpdateQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                Volatile.Write(ref _stateUpdateQueued, 0);
                if (!_disposed)
                {
                    _tray.Update(_orchestrator.State);
                }
            });
        }
    }

    private void OnActivationRequested(object? sender, EventArgs e) => _dispatcher.BeginInvoke(ShowSettings);

    private void OnExitRequested(object? sender, EventArgs e)
    {
        AppLog.ExitRequested(_logger);
        _dispatcher.BeginInvoke(Exit);
    }

    private void EnsureGpuPreference(AppSettings settings)
    {
        if (!settings.Performance.PreferPowerSavingGpu)
        {
            return;
        }

        TryRegistry(() =>
        {
            var outcome = new GpuPreferenceRegistration(_registry, _executablePath).EnsurePowerSaving();
            if (outcome != GpuPreferenceOutcome.AlreadyPowerSaving)
            {
                AppLog.GpuPreference(_logger, outcome);
            }
        });
    }

    private void TryRegistry(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            AppLog.RegistryFailed(_logger, exception);
        }
    }

    private void ReportLoadOutcome(SettingsLoadResult load)
    {
        switch (load.Outcome)
        {
            case SettingsLoadOutcome.Repaired:
                _tray.Notify(
                    "Settings partly reset",
                    $"Some values in config.json were invalid and were reset to their defaults. The original was saved as {Path.GetFileName(load.BackupPath)}.",
                    warning: true);
                break;
            case SettingsLoadOutcome.ResetToDefaults:
                _tray.Notify(
                    "Settings reset",
                    $"config.json could not be read and was replaced with the defaults. The original was saved as {Path.GetFileName(load.BackupPath)}.",
                    warning: true);
                break;
            case SettingsLoadOutcome.Unreadable:
                _tray.Notify(
                    "Settings not loaded",
                    "config.json is locked or not accessible. Defaults are used and changes will not be saved this session.",
                    warning: true);
                break;
        }
    }

    private void HookUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                AppLog.Crash(_logger, exception);
            }

            _loggerFactory.Dispose();
        };

        // A bug in the Settings window must not take the lights down with it: log it and keep running.
        _app.DispatcherUnhandledException += (_, e) =>
        {
            AppLog.UiError(_logger, e.Exception);
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.UiError(_logger, e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Opens Explorer with <paramref name="path"/> selected.</summary>
    internal static void RevealInExplorer(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
