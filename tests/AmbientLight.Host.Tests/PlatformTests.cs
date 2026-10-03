using AmbientLight.Host.Platform;
using Microsoft.Extensions.Time.Testing;

namespace AmbientLight.Host.Tests;

/// <summary>An in-memory HKCU.</summary>
internal sealed class FakeRegistry : IRegistryValueStore
{
    private readonly Dictionary<(string Key, string Name), object> _values = [];

    public object? GetValue(string subKey, string name) =>
        _values.TryGetValue((subKey.ToUpperInvariant(), name.ToUpperInvariant()), out var value) ? value : null;

    public void SetString(string subKey, string name, string value) => _values[(subKey.ToUpperInvariant(), name.ToUpperInvariant())] = value;

    public void SetBinary(string subKey, string name, byte[] value) => _values[(subKey.ToUpperInvariant(), name.ToUpperInvariant())] = value;

    public void DeleteValue(string subKey, string name) => _values.Remove((subKey.ToUpperInvariant(), name.ToUpperInvariant()));
}

public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Tools\Ambient Light\AmbientLight.exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly FakeRegistry _registry = new();
    private readonly HashSet<string> _existingFiles = new(StringComparer.OrdinalIgnoreCase) { Exe };

    [Fact]
    public void Initially_NotRegistered()
    {
        Assert.Equal(StartupState.NotRegistered, Create().GetState());
    }

    [Fact]
    public void Enable_WritesAQuotedCommandWithTheAutostartSwitch()
    {
        var startup = Create();

        startup.SetEnabled(true);

        Assert.Equal($"\"{Exe}\" --autostart", _registry.GetValue(RunKey, "AmbientLight"));
        Assert.Equal(StartupState.Enabled, startup.GetState());
        Assert.True(startup.IsEnabled);
    }

    [Fact]
    public void DisabledInTaskManager_IsReported_AndReEnabledExplicitly()
    {
        var startup = Create();
        startup.SetEnabled(true);
        _registry.SetBinary(ApprovedKey, "AmbientLight", [0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01]);

        Assert.Equal(StartupState.DisabledInTaskManager, startup.GetState());

        startup.SetEnabled(true);

        Assert.Equal(StartupState.Enabled, startup.GetState());
        Assert.Equal(0x02, ((byte[])_registry.GetValue(ApprovedKey, "AmbientLight")!)[0]);
    }

    [Fact]
    public void Disable_RemovesTheEntryAndTheTaskManagerFlag()
    {
        var startup = Create();
        startup.SetEnabled(true);
        _registry.SetBinary(ApprovedKey, "AmbientLight", [0x03]);

        startup.SetEnabled(false);

        Assert.Null(_registry.GetValue(RunKey, "AmbientLight"));
        Assert.Null(_registry.GetValue(ApprovedKey, "AmbientLight"));
        Assert.Equal(StartupState.NotRegistered, startup.GetState());
    }

    [Fact]
    public void EntryForAnotherCopy_IsReportedAsRegisteredElsewhere()
    {
        _registry.SetString(RunKey, "AmbientLight", "\"D:\\Old\\AmbientLight.exe\" --autostart");

        Assert.Equal(StartupState.RegisteredElsewhere, Create().GetState());
    }

    [Fact]
    public void PathComparison_IgnoresCase()
    {
        _registry.SetString(RunKey, "AmbientLight", $"\"{Exe.ToUpperInvariant()}\" --autostart");

        Assert.Equal(StartupState.Enabled, Create().GetState());
    }

    [Fact]
    public void RepairIfMoved_RepointsAnEntryWhoseExecutableIsGone()
    {
        _registry.SetString(RunKey, "AmbientLight", "\"D:\\Downloads\\AmbientLight.exe\" --autostart");
        var startup = Create();

        Assert.True(startup.RepairIfMoved());
        Assert.Equal(StartupState.Enabled, startup.GetState());
    }

    [Fact]
    public void RepairIfMoved_LeavesAnotherExistingCopyAlone()
    {
        const string Other = "D:\\Portable\\AmbientLight.exe";
        _existingFiles.Add(Other);
        _registry.SetString(RunKey, "AmbientLight", $"\"{Other}\" --autostart");
        var startup = Create();

        Assert.False(startup.RepairIfMoved());
        Assert.Equal(StartupState.RegisteredElsewhere, startup.GetState());
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Ambient Light\\AmbientLight.exe\" --autostart", "C:\\Program Files\\Ambient Light\\AmbientLight.exe")]
    [InlineData("C:\\Program Files\\Ambient Light\\AmbientLight.exe --autostart", "C:\\Program Files\\Ambient Light\\AmbientLight.exe")]
    [InlineData("C:\\Tools\\AmbientLight.exe", "C:\\Tools\\AmbientLight.exe")]
    [InlineData("  \"C:\\Tools\\AmbientLight.exe\"", "C:\\Tools\\AmbientLight.exe")]
    [InlineData("\"C:\\Tools\\Unterminated.exe", "C:\\Tools\\Unterminated.exe")]
    [InlineData("C:\\Tools\\script.cmd /quiet", "C:\\Tools\\script.cmd")]
    public void ParseExecutable_HandlesQuotedAndUnquotedCommands(string command, string expected)
    {
        Assert.Equal(expected, StartupRegistration.ParseExecutable(command));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0 }, true)]
    [InlineData(new byte[0], false)]
    public void TaskManagerFlag_OddFirstByteMeansDisabled(byte[] value, bool disabled)
    {
        Assert.Equal(disabled, StartupRegistration.IsDisabledFlag(value));
    }

    [Fact]
    public void TaskManagerFlag_MissingOrWrongTypeMeansEnabled()
    {
        Assert.False(StartupRegistration.IsDisabledFlag(null));
        Assert.False(StartupRegistration.IsDisabledFlag("03"));
    }

    private StartupRegistration Create() => new(_registry, Exe, fileExists: _existingFiles.Contains);
}

public sealed class GpuPreferenceRegistrationTests
{
    private const string Exe = @"C:\Tools\AmbientLight.exe";
    private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private readonly FakeRegistry _registry = new();

    [Fact]
    public void NoPreference_RegistersPowerSaving()
    {
        var outcome = Create().EnsurePowerSaving();

        Assert.Equal(GpuPreferenceOutcome.Registered, outcome);
        Assert.Equal("GpuPreference=1;", _registry.GetValue(Key, Exe));
        Assert.Equal(1, Create().GetPreference());
    }

    [Fact]
    public void OtherEntries_ArePreserved()
    {
        _registry.SetString(Key, Exe, "SwapEffectUpgradeEnable=1;");

        Assert.Equal(GpuPreferenceOutcome.Registered, Create().EnsurePowerSaving());
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;", _registry.GetValue(Key, Exe));
    }

    [Theory]
    [InlineData("GpuPreference=2;")]
    [InlineData("GpuPreference=0;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [InlineData("GpuPreference=fast;")]
    public void ExistingChoice_IsNeverChanged(string existing)
    {
        _registry.SetString(Key, Exe, existing);

        Assert.Equal(GpuPreferenceOutcome.UserChoiceKept, Create().EnsurePowerSaving());
        Assert.Equal(existing, _registry.GetValue(Key, Exe));
    }

    [Fact]
    public void PowerSavingAlreadySet_IsReported()
    {
        _registry.SetString(Key, Exe, "GpuPreference=1;");

        Assert.Equal(GpuPreferenceOutcome.AlreadyPowerSaving, Create().EnsurePowerSaving());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData(" gpupreference = 1 ; ", 1)]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=0;", 0)]
    [InlineData("GpuPreference=;", null)]
    [InlineData("SwapEffectUpgradeEnable=1", null)]
    public void ParsePreference_ReadsTheGpuPreferenceEntry(string? data, int? expected)
    {
        Assert.Equal(expected, GpuPreferenceRegistration.ParsePreference(data));
    }

    [Theory]
    [InlineData(null, "GpuPreference=1;")]
    [InlineData("A=1", "A=1;GpuPreference=1;")]
    [InlineData("A=1; ", "A=1;GpuPreference=1;")]
    public void WithPreference_AppendsToTheList(string? existing, string expected)
    {
        Assert.Equal(expected, GpuPreferenceRegistration.WithPreference(existing, 1));
    }

    private GpuPreferenceRegistration Create() => new(_registry, Exe);
}

public sealed class FullscreenMonitorTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new();
    private readonly List<bool> _changes = [];
    private int _probes;

    [Theory]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(1, false)]
    public void OnlyRunningD3DFullScreen_CountsAsExclusive(int state, bool exclusive)
    {
        Assert.Equal(exclusive, FullscreenDetector.IsExclusiveFullscreen(state));
    }

    [Fact]
    public void Disabled_NeverPolls()
    {
        using var monitor = Create(() => true);

        _time.Advance(Interval * 10);

        Assert.Equal(0, _probes);
        Assert.Empty(_changes);
    }

    [Fact]
    public void Activation_IsReportedAfterTwoConsecutiveReadings()
    {
        using var monitor = Create(() => true);
        monitor.SetEnabled(true);

        while (_changes.Count == 0 && _probes < 10)
        {
            _time.Advance(Interval);
        }

        Assert.Equal([true], _changes);
        Assert.Equal(FullscreenMonitor.ConfirmationPolls, _probes);
        Assert.True(monitor.IsActive);
    }

    [Fact]
    public void SingleReadings_AreIgnored()
    {
        var reading = false;
        using var monitor = Create(() => reading = !reading);
        monitor.SetEnabled(true);

        _time.Advance(Interval * 10);

        Assert.True(_probes >= 10);
        Assert.Empty(_changes);
    }

    [Fact]
    public void Deactivation_IsReportedToo()
    {
        var active = true;
        using var monitor = Create(() => active);
        monitor.SetEnabled(true);
        _time.Advance(Interval * 3);
        Assert.Equal([true], _changes);

        active = false;
        _time.Advance(Interval * 3);

        Assert.Equal([true, false], _changes);
        Assert.False(monitor.IsActive);
    }

    [Fact]
    public void Disabling_WhileActive_ReportsInactive()
    {
        using var monitor = Create(() => true);
        monitor.SetEnabled(true);
        _time.Advance(Interval * 3);

        monitor.SetEnabled(false);
        var probes = _probes;
        _time.Advance(Interval * 5);

        Assert.Equal([true, false], _changes);
        Assert.Equal(probes, _probes);
    }

    private FullscreenMonitor Create(Func<bool> probe)
    {
        var monitor = new FullscreenMonitor(
            () =>
            {
                _probes++;
                return probe();
            },
            Interval,
            _time);
        monitor.Changed += (_, active) => _changes.Add(active);
        return monitor;
    }
}
