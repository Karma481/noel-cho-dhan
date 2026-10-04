using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows.Threading;
using AmbientLight.App.Interop;
using AmbientLight.Core.Settings;
using AmbientLight.Host;
using AmbientLight.Host.Pipeline;
using AmbientLight.Host.Platform;
using AmbientLight.Host.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AmbientLight.App.Settings;

/// <summary>
/// State of the Settings window. Every edit is applied at once: the form values are merged into the current
/// settings and published, the pipeline picks them up on its next frame, and <see cref="SettingsCoordinator"/>
/// writes config.json after a short quiet period. There is no Save or Apply button.
/// </summary>
/// <remarks>
/// A change the validator rejects (LED output enabled without a COM port) stays in the form, with the reason
/// shown in <see cref="IssuesText"/>, and is published as soon as it becomes valid. Owned by the UI thread.
/// </remarks>
internal sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    private readonly SettingsHolder _settings;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly PipelineServices _services;
    private readonly SettingsCoordinator _coordinator;
    private readonly StartupRegistration _startup;
    private readonly Func<IReadOnlyList<string>> _getPorts;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly RateMeter _captureRate = new();
    private readonly RateMeter _serialRate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private bool _loading;
    private bool _publishing;
    private bool _disposed;

    // Form fields (see SettingsFormValues).
    private bool _overlayEnabled;
    private double _innerIntensity;
    private double _innerGlow;
    private double _washIntensity;
    private double _spreadWidth;
    private double _blurRadius;
    private OverlayBlendMode _blendMode;
    private double _overlayBrightness;
    private double _opacity;
    private double _saturation;
    private double _contrast;
    private double _luminanceGain;
    private bool _keepPictureClear;
    private OverlayPreset _activePreset;
    private int _targetFps;
    private bool _pauseOverlayInExclusiveFullscreen;
    private bool _preferPowerSavingGpu;
    private bool _serialEnabled;
    private string _portName = string.Empty;
    private int _baudRate;
    private bool _startWithWindows;

    // Display state.
    private string _modeText = string.Empty;
    private string _pauseButtonText = "Pause";
    private string? _errorText;
    private string? _issuesText;
    private string? _startupHint;
    private string _captureStatusText = string.Empty;
    private string _overlayStatusText = string.Empty;
    private string _serialStatusText = string.Empty;
    private string? _saveStatusText;

    public SettingsViewModel(
        SettingsHolder settings,
        PipelineOrchestrator orchestrator,
        PipelineServices services,
        SettingsCoordinator coordinator,
        StartupRegistration startup,
        Func<IReadOnlyList<string>> getPorts,
        IReadOnlyList<HotkeyRegistration> hotkeys,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _getPorts = getPorts ?? throw new ArgumentNullException(nameof(getPorts));
        ArgumentNullException.ThrowIfNull(hotkeys);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = Dispatcher.CurrentDispatcher;

        HotkeysText = string.Join(
            "  ·  ",
            hotkeys.Select(static hotkey => hotkey.IsRegistered
                ? $"{hotkey.Gesture} {hotkey.Description}"
                : $"{hotkey.Gesture} taken by another app"));

        BaudRates = new ObservableCollection<int>(SerialSettings.StandardBaudRates);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        TogglePauseCommand = new RelayCommand(() => _orchestrator.TogglePause());
        OpenConfigCommand = new RelayCommand(OpenConfig);
        OpenLogsCommand = new RelayCommand(OpenLogs);

        LoadFrom(_settings.Current.Settings);
        RefreshPorts();
        LoadStartupState();
        RefreshState();

        _settings.Published += OnSettingsPublished;
        _orchestrator.StateChanged += OnPipelineStateChanged;

        // The status footer is only polled while the window is open; closing it stops the timer.
        _statusTimer = new DispatcherTimer(StatusInterval, DispatcherPriority.Background, (_, _) => RefreshStatus(), _dispatcher);
        RefreshStatus();
    }

    // ── Virtual Overlay ────────────────────────────────────────────────────────────────────────────

    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set => SetAndPublish(ref _overlayEnabled, value);
    }

    public bool IsSubtlePreset
    {
        get => _activePreset == OverlayPreset.Subtle;
        set => SelectPreset(value, OverlayPreset.Subtle);
    }

    public bool IsBalancedPreset
    {
        get => _activePreset == OverlayPreset.Balanced;
        set => SelectPreset(value, OverlayPreset.Balanced);
    }

    public bool IsCinematicPreset
    {
        get => _activePreset == OverlayPreset.Cinematic;
        set => SelectPreset(value, OverlayPreset.Cinematic);
    }

    /// <summary>What the active preset does, or that the look has been customized.</summary>
    public string PresetHint => _activePreset switch
    {
        OverlayPreset.Subtle => "A calm halo close to the bezel, gentle colors.",
        OverlayPreset.Balanced => "A bright edge glow with a soft ambient wash. The default.",
        OverlayPreset.Cinematic => "Additive light washing deep into the screen with boosted color, like a video player's ambient mode.",
        _ => "Custom look. Pick a preset to start over from it.",
    };

    /// <summary>Inner glow strength, 0..100 %.</summary>
    public double InnerIntensity
    {
        get => _innerIntensity;
        set => SetAndPublish(ref _innerIntensity, value);
    }

    /// <summary>Inner glow width, 0.2..10 % of the shorter screen side.</summary>
    public double InnerGlow
    {
        get => _innerGlow;
        set => SetAndPublish(ref _innerGlow, value);
    }

    /// <summary>Ambient wash strength, 0..100 %.</summary>
    public double WashIntensity
    {
        get => _washIntensity;
        set => SetAndPublish(ref _washIntensity, value);
    }

    /// <summary>Ambient wash spread, 0.5..50 % of the shorter screen side.</summary>
    public double SpreadWidth
    {
        get => _spreadWidth;
        set => SetAndPublish(ref _spreadWidth, value);
    }

    /// <summary>Ambient wash blur radius, 0..50 % of the shorter screen side.</summary>
    public double BlurRadius
    {
        get => _blurRadius;
        set => SetAndPublish(ref _blurRadius, value);
    }

    // ── Color & Blend ──────────────────────────────────────────────────────────────────────────────

    public bool IsNormalBlend
    {
        get => _blendMode == OverlayBlendMode.Normal;
        set => SelectBlendMode(value, OverlayBlendMode.Normal);
    }

    public bool IsScreenBlend
    {
        get => _blendMode == OverlayBlendMode.Screen;
        set => SelectBlendMode(value, OverlayBlendMode.Screen);
    }

    public bool IsAdditiveBlend
    {
        get => _blendMode == OverlayBlendMode.Additive;
        set => SelectBlendMode(value, OverlayBlendMode.Additive);
    }

    /// <summary>What the selected blend mode looks like.</summary>
    public string BlendHint => _blendMode switch
    {
        OverlayBlendMode.Normal => "Paints the glow over the picture with the set opacity; dark scenes darken the edges.",
        OverlayBlendMode.Screen => "Adds light like a projector: dark colors stay transparent and bright content stays bright.",
        _ => "Adds the full glow to the picture: the most intense look, may wash out bright content at the edges.",
    };

    /// <summary>0..100 %.</summary>
    public double OverlayBrightness
    {
        get => _overlayBrightness;
        set => SetAndPublish(ref _overlayBrightness, value);
    }

    /// <summary>0..100 %.</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetAndPublish(ref _opacity, value);
    }

    /// <summary>0..2×.</summary>
    public double Saturation
    {
        get => _saturation;
        set => SetAndPublish(ref _saturation, value);
    }

    /// <summary>0.5..2×.</summary>
    public double Contrast
    {
        get => _contrast;
        set => SetAndPublish(ref _contrast, value);
    }

    /// <summary>0.5..2×.</summary>
    public double LuminanceGain
    {
        get => _luminanceGain;
        set => SetAndPublish(ref _luminanceGain, value);
    }

    /// <summary>In letterboxed video, light only the black bars.</summary>
    public bool KeepPictureClear
    {
        get => _keepPictureClear;
        set => SetAndPublish(ref _keepPictureClear, value);
    }

    // ── Performance & Mode ─────────────────────────────────────────────────────────────────────────

    public bool IsPowerSavingFps
    {
        get => _targetFps == SettingsFormValues.PowerSavingFps;
        set
        {
            if (value)
            {
                SetTargetFps(SettingsFormValues.PowerSavingFps);
            }
        }
    }

    public bool IsSmoothFps
    {
        get => _targetFps == SettingsFormValues.SmoothFps;
        set
        {
            if (value)
            {
                SetTargetFps(SettingsFormValues.SmoothFps);
            }
        }
    }

    /// <summary>Shown when config.json holds a rate other than the two offered.</summary>
    public string? CustomFpsText => _targetFps is SettingsFormValues.PowerSavingFps or SettingsFormValues.SmoothFps
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"Custom rate from config.json: {_targetFps} FPS");

    public bool PauseOverlayInExclusiveFullscreen
    {
        get => _pauseOverlayInExclusiveFullscreen;
        set => SetAndPublish(ref _pauseOverlayInExclusiveFullscreen, value);
    }

    public bool PreferPowerSavingGpu
    {
        get => _preferPowerSavingGpu;
        set => SetAndPublish(ref _preferPowerSavingGpu, value);
    }

    /// <summary>Written to the registry immediately (it is Windows state, not part of config.json).</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value)
            {
                return;
            }

            try
            {
                _startup.SetEnabled(value);
                _startWithWindows = value;
                LoadStartupState();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
            {
                AppLog.AutostartFailed(_logger, exception);
                StartupHint = "Could not change the autostart entry: " + exception.Message;
            }

            // Re-read after the binding has finished writing, so a failed change visibly reverts the switch.
            _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(StartWithWindows)));
        }
    }

    public string? StartupHint
    {
        get => _startupHint;
        private set => SetProperty(ref _startupHint, value);
    }

    // ── Hardware LED ───────────────────────────────────────────────────────────────────────────────

    public bool SerialEnabled
    {
        get => _serialEnabled;
        set
        {
            if (value && _portName.Length == 0 && AvailablePorts.Count == 1)
            {
                // The common case: exactly one USB serial device plugged in.
                _portName = AvailablePorts[0];
                OnPropertyChanged(nameof(SelectedPort));
            }

            SetAndPublish(ref _serialEnabled, value);
        }
    }

    public ObservableCollection<string> AvailablePorts { get; } = [];

    public string? SelectedPort
    {
        get => _portName.Length == 0 ? null : _portName;
        set => SetAndPublish(ref _portName, value ?? string.Empty);
    }

    public ObservableCollection<int> BaudRates { get; }

    public int BaudRate
    {
        get => _baudRate;
        set => SetAndPublish(ref _baudRate, value);
    }

    public IRelayCommand RefreshPortsCommand { get; }

    // ── Header and footer ──────────────────────────────────────────────────────────────────────────

    public string ModeText
    {
        get => _modeText;
        private set => SetProperty(ref _modeText, value);
    }

    public string PauseButtonText
    {
        get => _pauseButtonText;
        private set => SetProperty(ref _pauseButtonText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>Validation errors (change not applied) or warnings (applied) of the latest edit.</summary>
    public string? IssuesText
    {
        get => _issuesText;
        private set => SetProperty(ref _issuesText, value);
    }

    public string CaptureStatusText
    {
        get => _captureStatusText;
        private set => SetProperty(ref _captureStatusText, value);
    }

    /// <summary>Includes whether the overlay is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE).</summary>
    public string OverlayStatusText
    {
        get => _overlayStatusText;
        private set => SetProperty(ref _overlayStatusText, value);
    }

    public string SerialStatusText
    {
        get => _serialStatusText;
        private set => SetProperty(ref _serialStatusText, value);
    }

    public string? SaveStatusText
    {
        get => _saveStatusText;
        private set => SetProperty(ref _saveStatusText, value);
    }

    public string HotkeysText { get; }

    public IRelayCommand TogglePauseCommand { get; }

    public IRelayCommand OpenConfigCommand { get; }

    public IRelayCommand OpenLogsCommand { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _settings.Published -= OnSettingsPublished;
        _orchestrator.StateChanged -= OnPipelineStateChanged;
    }

    private void SetTargetFps(int fps)
    {
        if (_targetFps == fps)
        {
            return;
        }

        _targetFps = fps;
        OnPropertyChanged(nameof(IsPowerSavingFps));
        OnPropertyChanged(nameof(IsSmoothFps));
        OnPropertyChanged(nameof(CustomFpsText));
        Publish();
    }

    private void SetAndPublish<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            Publish();
        }
    }

    private SettingsFormValues CurrentValues() => new()
    {
        OverlayEnabled = _overlayEnabled,
        InnerIntensityPercent = _innerIntensity,
        InnerGlowPercent = _innerGlow,
        WashIntensityPercent = _washIntensity,
        SpreadWidthPercent = _spreadWidth,
        BlurRadiusPercent = _blurRadius,
        BlendMode = _blendMode,
        OverlayBrightnessPercent = _overlayBrightness,
        OpacityPercent = _opacity,
        Saturation = _saturation,
        Contrast = _contrast,
        LuminanceGain = _luminanceGain,
        KeepPictureClear = _keepPictureClear,
        TargetFps = _targetFps,
        PauseOverlayInExclusiveFullscreen = _pauseOverlayInExclusiveFullscreen,
        PreferPowerSavingGpu = _preferPowerSavingGpu,
        SerialEnabled = _serialEnabled,
        PortName = _portName,
        BaudRate = _baudRate,
    };

    private void SelectPreset(bool selected, OverlayPreset preset)
    {
        if (!selected || _loading || _disposed)
        {
            return;
        }

        // The form, not the published settings, is the basis: an edit that is still invalid (LED output without
        // a port) is kept while the look changes.
        var basis = CurrentValues().ApplyTo(_settings.Current.Settings);
        var next = basis with { Overlay = OverlayPresets.Apply(basis.Overlay, preset) };
        LoadFrom(next);
        Publish();
    }

    private void SelectBlendMode(bool selected, OverlayBlendMode mode)
    {
        if (!selected || _blendMode == mode)
        {
            return;
        }

        _blendMode = mode;
        OnPropertyChanged(nameof(IsNormalBlend));
        OnPropertyChanged(nameof(IsScreenBlend));
        OnPropertyChanged(nameof(IsAdditiveBlend));
        OnPropertyChanged(nameof(BlendHint));
        Publish();
    }

    private void UpdateActivePreset()
    {
        var preset = OverlayPresets.Detect(CurrentValues().ApplyTo(_settings.Current.Settings).Overlay);
        if (preset == _activePreset)
        {
            return;
        }

        _activePreset = preset;
        OnPropertyChanged(nameof(IsSubtlePreset));
        OnPropertyChanged(nameof(IsBalancedPreset));
        OnPropertyChanged(nameof(IsCinematicPreset));
        OnPropertyChanged(nameof(PresetHint));
    }

    private void Publish()
    {
        if (_loading || _disposed)
        {
            return;
        }

        var next = CurrentValues().ApplyTo(_settings.Current.Settings);
        UpdateActivePreset();
        _publishing = true;
        try
        {
            if (_settings.TryPublish(next, out var issues))
            {
                IssuesText = FormatIssues(issues);
            }
            else
            {
                IssuesText = FormatIssues(issues.Where(static issue => issue.Severity == SettingsIssueSeverity.Error)) + " (not applied yet)";
                AppLog.SettingsRejected(_logger, string.Join("; ", issues));
            }
        }
        finally
        {
            _publishing = false;
        }
    }

    private void LoadFrom(AppSettings settings)
    {
        var values = SettingsFormValues.From(settings);
        _loading = true;
        try
        {
            OverlayEnabled = values.OverlayEnabled;
            InnerIntensity = values.InnerIntensityPercent;
            InnerGlow = values.InnerGlowPercent;
            WashIntensity = values.WashIntensityPercent;
            SpreadWidth = values.SpreadWidthPercent;
            BlurRadius = values.BlurRadiusPercent;
            SelectBlendMode(true, values.BlendMode);
            OverlayBrightness = values.OverlayBrightnessPercent;
            Opacity = values.OpacityPercent;
            Saturation = values.Saturation;
            Contrast = values.Contrast;
            LuminanceGain = values.LuminanceGain;
            KeepPictureClear = values.KeepPictureClear;
            SetTargetFps(values.TargetFps);
            PauseOverlayInExclusiveFullscreen = values.PauseOverlayInExclusiveFullscreen;
            PreferPowerSavingGpu = values.PreferPowerSavingGpu;
            SerialEnabled = values.SerialEnabled;
            SelectedPort = values.PortName;
            if (!BaudRates.Contains(values.BaudRate))
            {
                BaudRates.Add(values.BaudRate);
            }

            BaudRate = values.BaudRate;
            IssuesText = null;
        }
        finally
        {
            _loading = false;
        }

        UpdateActivePreset();
    }

    private void RefreshPorts()
    {
        var keep = _portName;
        IReadOnlyList<string> ports;
        try
        {
            ports = _getPorts();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            ports = [];
        }

        // Clearing the list makes the ComboBox write null into SelectedPort; _loading keeps that from being
        // published, and the configured port is restored afterwards (listed even while its device is unplugged).
        _loading = true;
        try
        {
            AvailablePorts.Clear();
            foreach (var port in ports.Order(StringComparer.OrdinalIgnoreCase))
            {
                AvailablePorts.Add(port);
            }

            if (keep.Length > 0 && !AvailablePorts.Contains(keep, StringComparer.OrdinalIgnoreCase))
            {
                AvailablePorts.Add(keep);
            }

            _portName = keep;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(SelectedPort));
    }

    private void LoadStartupState()
    {
        StartupState state;
        try
        {
            state = _startup.GetState();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            StartupHint = "Could not read the autostart entry: " + exception.Message;
            return;
        }

        _startWithWindows = state == StartupState.Enabled;
        OnPropertyChanged(nameof(StartWithWindows));
        StartupHint = state switch
        {
            StartupState.DisabledInTaskManager => "Disabled in Task Manager › Startup apps. Turning it on here enables it again.",
            StartupState.RegisteredElsewhere => "Another copy of Ambient Light is set to start with Windows. Turning it on here points it at this copy.",
            _ => "Starts in the tray at sign-in, without opening this window.",
        };
    }

    private void RefreshState()
    {
        var state = _orchestrator.State;
        ModeText = StatusText.Describe(state);
        PauseButtonText = state.IsPaused ? "Resume" : "Pause";
        ErrorText = state.LastError;
    }

    private void RefreshStatus()
    {
        if (_disposed)
        {
            return;
        }

        var status = _services.GetStatus();
        var now = _clock.Elapsed;
        var captureFps = _captureRate.Sample(status.Capture.FramesPublished, now);
        var serialFps = status.Serial is { } serial ? _serialRate.Sample(serial.FramesSent, now) : 0;

        CaptureStatusText = StatusText.DescribeCapture(status, captureFps);
        OverlayStatusText = StatusText.DescribeOverlay(status);
        SerialStatusText = StatusText.DescribeSerial(status, serialFps);

        SaveStatusText = !_coordinator.AutosaveEnabled
            ? "config.json could not be read at startup; changes are not saved this session."
            : _coordinator.LastSaveError is { } error ? "Saving config.json failed: " + error : null;
    }

    private void OnSettingsPublished(object? sender, SettingsSnapshot snapshot)
    {
        // Our own publishes already match the form; reloading would round a slider being dragged.
        if (_publishing)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsPublished(sender, _settings.Current));
            return;
        }

        if (!_disposed)
        {
            LoadFrom(snapshot.Settings);
        }
    }

    private void OnPipelineStateChanged(object? sender, PipelineState state) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                RefreshState();
                RefreshStatus();
            }
        });

    private void OpenConfig()
    {
        _coordinator.Flush();
        AppHost.RevealInExplorer(_coordinator.Path);
    }

    private void OpenLogs()
    {
        var folder = AppPaths.Default.LogDirectory;
        Directory.CreateDirectory(folder);
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private static string? FormatIssues(IEnumerable<SettingsIssue> issues)
    {
        var messages = issues.Select(static issue => issue.Message).ToArray();
        return messages.Length == 0 ? null : string.Join(" ", messages);
    }
}
