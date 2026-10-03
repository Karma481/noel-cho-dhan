using System.Runtime.InteropServices;

namespace AmbientLight.Host.Platform;

/// <summary>
/// Keeps one instance per user session. A second launch signals the first one and exits: by default it asks it
/// to open its Settings window (double-clicking the executable while the app sits in the tray does the obvious
/// thing); with <c>exitRunningInstance</c> it asks it to shut down cleanly, for scripts that replace the
/// executable with a new version.
/// </summary>
/// <remarks>
/// The objects live in the session's <c>Local\</c> namespace. The mutex is only used for its existence, never
/// acquired, so there is no thread affinity and nothing to release on another thread at exit.
/// </remarks>
public sealed partial class SingleInstance : IDisposable
{
    private const int AllowAnyProcess = -1; // ASFW_ANY

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly EventWaitHandle _exit;
    private readonly RegisteredWaitHandle _activationRegistration;
    private readonly RegisteredWaitHandle _exitRegistration;

    private SingleInstance(Mutex mutex, EventWaitHandle activation, EventWaitHandle exit)
    {
        _mutex = mutex;
        _activation = activation;
        _exit = exit;
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activation,
            static (state, _) => ((SingleInstance)state!).ActivationRequested?.Invoke(state, EventArgs.Empty),
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            exit,
            static (state, _) => ((SingleInstance)state!).ExitRequested?.Invoke(state, EventArgs.Empty),
            this,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <summary>Raised on a thread-pool thread when another launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Raised on a thread-pool thread when another launch asked this instance to exit.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Returns the instance guard when this is the first instance; otherwise signals the running instance (to
    /// show itself, or to exit when <paramref name="exitRunningInstance"/> is set) and returns <see langword="null"/>.
    /// </summary>
    /// <param name="name">Name shared by all instances of the app.</param>
    /// <param name="exitRunningInstance">Ask a running instance to exit instead of showing itself.</param>
    public static SingleInstance? TryAcquire(string name, bool exitRunningInstance = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(initiallyOwned: false, $@"Local\{name}.Instance", out var createdNew);
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activate");
        var exit = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{name}.Exit");
        if (createdNew)
        {
            return new SingleInstance(mutex, activation, exit);
        }

        if (exitRunningInstance)
        {
            exit.Set();
        }
        else
        {
            // This process was just started by the user, so it may hand its foreground right to the running
            // instance; without it Windows would only flash the Settings window's taskbar button.
            AllowSetForegroundWindow(AllowAnyProcess);
            activation.Set();
        }

        exit.Dispose();
        activation.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _activationRegistration.Unregister(null);
        _exitRegistration.Unregister(null);
        _activation.Dispose();
        _exit.Dispose();
        _mutex.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
