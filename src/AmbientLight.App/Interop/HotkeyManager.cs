using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AmbientLight.App.Interop;

/// <summary>A system-wide hotkey and whether Windows accepted it.</summary>
/// <param name="Gesture">Display text, for example <c>Ctrl+Alt+L</c>.</param>
/// <param name="Description">What it does.</param>
/// <param name="IsRegistered">False when another application already owns the combination.</param>
internal sealed record HotkeyRegistration(string Gesture, string Description, bool IsRegistered);

/// <summary>
/// System-wide hotkeys through <c>RegisterHotKey</c> on a message-only window, so they work while a game or
/// a video player has the focus and cost nothing until pressed.
/// </summary>
/// <remarks>Must be created and disposed on the UI thread, whose dispatcher pumps the window's messages.</remarks>
internal sealed partial class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<HotkeyRegistration> _registrations = [];
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("AmbientLight.Hotkeys")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    /// <summary>Every hotkey requested so far with its registration result.</summary>
    public IReadOnlyList<HotkeyRegistration> Registrations => _registrations;

    /// <summary>Registers Ctrl+Alt+<paramref name="key"/> (a letter) to run <paramref name="action"/> on the UI thread.</summary>
    public HotkeyRegistration RegisterCtrlAlt(char key, string description, Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(action);
        var upper = char.ToUpperInvariant(key);
        if (upper is < 'A' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "Only letters are supported.");
        }

        var id = _nextId++;
        // Virtual-key codes of letters equal their upper-case ASCII codes.
        var registered = RegisterHotKey(_window.Handle, id, ModControl | ModAlt | ModNoRepeat, upper);
        if (registered)
        {
            _actions[id] = action;
        }

        var registration = new HotkeyRegistration($"Ctrl+Alt+{upper}", description, registered);
        _registrations.Add(registration);
        return registration;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }

        _actions.Clear();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }

        return IntPtr.Zero;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
