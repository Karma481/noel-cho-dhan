using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Host.Pipeline;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using DrawingIcon = System.Drawing.Icon;

namespace AmbientLight.App.Tray;

/// <summary>
/// The notification-area icon: right-click menu (Settings, Pause/Resume, Exit), double-click opens Settings,
/// and the icon and tooltip follow the pipeline state.
/// </summary>
/// <remarks>
/// <para>
/// Creating the icon fails while the taskbar does not exist (Explorer not started yet or restarting, a shell
/// replacement). The app keeps running without it and the caller retries <see cref="TryCreate"/>; meanwhile
/// the hotkeys work and launching the executable again opens the Settings window. Once created, H.NotifyIcon
/// re-adds the icon by itself when Explorer restarts.
/// </para>
/// <para>
/// <see cref="TaskbarIcon"/> owns the <see cref="DrawingIcon"/> assigned to it: it disposes the previous icon on
/// every change and the current one when disposed. Each assignment therefore gets a freshly loaded instance.
/// </para>
/// <para>Create, update and dispose on the UI thread.</para>
/// </remarks>
internal sealed partial class TrayIconController : IDisposable
{
    private const int SmallIconMetric = 49; // SM_CXSMICON

    private readonly Action _openSettings;
    private readonly ContextMenu _menu;
    private readonly MenuItem _pauseItem;
    private readonly int _iconSize;
    private TaskbarIcon? _taskbarIcon;
    private PipelineState? _state;
    private bool _disposed;

    /// <param name="openSettings">Opens (or focuses) the Settings window.</param>
    /// <param name="togglePause">Pauses or resumes the effect.</param>
    /// <param name="exit">Exits the app.</param>
    /// <param name="pauseGesture">Hotkey shown next to the pause item.</param>
    public TrayIconController(Action openSettings, Action togglePause, Action exit, string pauseGesture)
    {
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        ArgumentNullException.ThrowIfNull(togglePause);
        ArgumentNullException.ThrowIfNull(exit);

        _iconSize = GetSmallIconSize();

        var settingsItem = new MenuItem { Header = "_Settings…", FontWeight = FontWeights.SemiBold };
        settingsItem.Click += (_, _) => openSettings();

        _pauseItem = new MenuItem { Header = "_Pause effect", InputGestureText = pauseGesture };
        _pauseItem.Click += (_, _) => togglePause();

        var exitItem = new MenuItem { Header = "E_xit" };
        exitItem.Click += (_, _) => exit();

        _menu = new ContextMenu();
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);
    }

    /// <summary>True once the icon is in the notification area.</summary>
    public bool IsCreated => _taskbarIcon is not null;

    /// <summary>Adds the icon to the notification area. Returns <see langword="false"/> (with the reason) when the shell refused.</summary>
    public bool TryCreate(out Exception? error)
    {
        error = null;
        if (_disposed || _taskbarIcon is not null)
        {
            return !_disposed;
        }

        var taskbarIcon = new TaskbarIcon
        {
            Icon = CreateIcon(IsDimmed(_state)),
            ToolTipText = ToolTipFor(_state),
            ContextMenu = _menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        taskbarIcon.TrayMouseDoubleClick += OnDoubleClick;

        try
        {
            // false: do not put the process into Windows 11 "efficiency mode" (EcoQoS). That mode is meant for
            // idle background apps and would throttle the capture and overlay threads.
            taskbarIcon.ForceCreate(false);
        }
        catch (InvalidOperationException exception)
        {
            taskbarIcon.TrayMouseDoubleClick -= OnDoubleClick;
            taskbarIcon.ContextMenu = null;
            taskbarIcon.Dispose();
            error = exception;
            return false;
        }

        _taskbarIcon = taskbarIcon;
        return true;
    }

    /// <summary>Reflects <paramref name="state"/> in the icon, tooltip and pause item.</summary>
    public void Update(PipelineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_disposed)
        {
            return;
        }

        var previous = _state;
        _state = state;
        _pauseItem.Header = state.IsPaused ? "_Resume effect" : "_Pause effect";
        if (_taskbarIcon is null)
        {
            return;
        }

        try
        {
            if (previous is null || IsDimmed(previous) != IsDimmed(state))
            {
                _taskbarIcon.Icon = CreateIcon(IsDimmed(state));
            }

            _taskbarIcon.ToolTipText = ToolTipFor(state);
        }
        catch (InvalidOperationException)
        {
            // The shell is restarting; H.NotifyIcon re-creates the icon from its current properties afterwards.
        }
    }

    /// <summary>Shows a balloon/toast from the tray icon, if it exists.</summary>
    public void Notify(string title, string message, bool warning)
    {
        if (_disposed || _taskbarIcon is null)
        {
            return;
        }

        try
        {
            _taskbarIcon.ShowNotification(title, message, warning ? NotificationIcon.Warning : NotificationIcon.Info);
        }
        catch (InvalidOperationException)
        {
            // Notifications are informational; the same facts are in the log and the Settings window.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_taskbarIcon is not null)
        {
            _taskbarIcon.TrayMouseDoubleClick -= OnDoubleClick;
            _taskbarIcon.Dispose();
            _taskbarIcon = null;
        }
    }

    private static bool IsDimmed(PipelineState? state) => state is not null && state.Plan.Mode != PipelineMode.Running;

    private static string ToolTipFor(PipelineState? state) =>
        state is null ? "Ambient Light" : "Ambient Light — " + StatusText.Describe(state);

    private void OnDoubleClick(object sender, RoutedEventArgs e) => _openSettings();

    private DrawingIcon CreateIcon(bool dimmed) =>
        LoadIcon(dimmed ? "Assets/AmbientLight-paused.ico" : "Assets/AmbientLight.ico", _iconSize);

    private static DrawingIcon LoadIcon(string resourcePath, int size)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/{resourcePath}", UriKind.Absolute))
            ?? throw new InvalidOperationException($"Missing icon resource {resourcePath}.");
        using var stream = resource.Stream;

        // Picks the image closest to the tray's icon size for the current DPI from the multi-size .ico.
        return new DrawingIcon(stream, size, size);
    }

    private static int GetSmallIconSize()
    {
        var size = GetSystemMetricsForDpi(SmallIconMetric, GetDpiForSystem());
        return size > 0 ? size : 16;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
