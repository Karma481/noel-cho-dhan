using AmbientLight.Core.Zones;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Processing;

/// <summary>Source-generated, allocation-free log messages for the processing stage.</summary>
internal static partial class ProcessingLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Color processing started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Color processing stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Information,
        Message = "Black bars changed; sampling content area {Bounds}")]
    public static partial void ContentBoundsChanged(ILogger logger, NormalizedRect bounds);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Error,
        Message = "Color processing failed; retrying with the next frame")]
    public static partial void Faulted(ILogger logger, Exception exception);
}
