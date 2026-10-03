using Microsoft.Win32;

namespace AmbientLight.Host.Platform;

/// <summary>Values under <c>HKEY_CURRENT_USER</c>, abstracted so the registration rules are unit-testable.</summary>
public interface IRegistryValueStore
{
    /// <summary>Returns the value (<see cref="string"/> or <see cref="byte"/>[]), or <see langword="null"/> when the key or value is missing.</summary>
    object? GetValue(string subKey, string name);

    /// <summary>Writes a REG_SZ value, creating the key when needed.</summary>
    void SetString(string subKey, string name, string value);

    /// <summary>Writes a REG_BINARY value, creating the key when needed.</summary>
    void SetBinary(string subKey, string name, byte[] value);

    /// <summary>Deletes the value if it exists.</summary>
    void DeleteValue(string subKey, string name);
}

/// <summary>The real <c>HKEY_CURRENT_USER</c> hive. Every operation is per user and needs no elevation.</summary>
public sealed class CurrentUserRegistry : IRegistryValueStore
{
    /// <inheritdoc />
    public object? GetValue(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    /// <inheritdoc />
    public void SetString(string subKey, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void SetBinary(string subKey, string name, byte[] value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    /// <inheritdoc />
    public void DeleteValue(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
