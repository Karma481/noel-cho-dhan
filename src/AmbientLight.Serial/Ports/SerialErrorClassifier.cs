namespace AmbientLight.Serial.Ports;

/// <summary>Maps Win32 error codes from the comm API to <see cref="SerialPortError"/>.</summary>
public static class SerialErrorClassifier
{
    // WinError.h
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotReady = 21;
    internal const int ErrorBadCommand = 22;
    internal const int ErrorGenFailure = 31;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorInvalidName = 123;
    internal const int ErrorOperationAborted = 995;
    internal const int ErrorDeviceNotConnected = 1167;
    internal const int ErrorNoSuchDevice = 433;

    /// <summary>Classifies a failure of <c>CreateFile</c> on a COM port.</summary>
    public static SerialPortError FromOpenError(int win32Error) => win32Error switch
    {
        ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName => SerialPortError.NotFound,
        ErrorNotReady or ErrorGenFailure or ErrorDeviceNotConnected or ErrorNoSuchDevice => SerialPortError.NotFound,

        // COM ports are exclusive: a second CreateFile gets ACCESS_DENIED while another app holds the port.
        ErrorAccessDenied or ErrorSharingViolation => SerialPortError.Busy,
        _ => SerialPortError.NotFound,
    };

    /// <summary>Classifies a failure of <c>GetCommState</c>/<c>SetCommState</c>/<c>SetCommTimeouts</c>.</summary>
    public static SerialPortError FromConfigureError(int win32Error) => win32Error switch
    {
        ErrorInvalidParameter => SerialPortError.InvalidConfiguration,
        _ => SerialPortError.Disconnected,
    };

    /// <summary>
    /// Classifies a failure of <c>WriteFile</c>. Every write failure means the device can no longer be
    /// trusted (unplugged: BAD_COMMAND, GEN_FAILURE, ACCESS_DENIED, DEVICE_NOT_CONNECTED, OPERATION_ABORTED
    /// depending on the USB-serial driver), so all of them lead to a reconnect.
    /// </summary>
    public static SerialPortError FromWriteError(int win32Error) => SerialPortError.Disconnected;
}
