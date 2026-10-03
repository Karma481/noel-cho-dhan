namespace AmbientLight.Serial.Protocol;

/// <summary>
/// Encodes LED colors as Adalight frames.
/// </summary>
/// <remarks>
/// <code>
/// offset  0    1    2    3        4        5                      6 ...
///         'A'  'd'  'a'  count-hi count-lo hi ^ lo ^ 0x55         R G B  R G B  ...
/// </code>
/// <para>
/// The count field holds <b>LED count − 1</b> (so 1..65536 LEDs fit in 16 bits) and the checksum
/// byte lets the receiver reject a header that was assembled from corrupted or misaligned bytes. The
/// payload itself carries no checksum in this protocol; the receiver resynchronizes on the next
/// "Ada" header after any loss.
/// </para>
/// <para>Golden vector, shared with the firmware's native tests: three LEDs red, green, blue encode as
/// <c>41 64 61 00 02 57 FF 00 00 00 FF 00 00 00 FF</c>.</para>
/// </remarks>
public static class AdalightEncoder
{
    /// <summary>Header length in bytes.</summary>
    public const int HeaderLength = 6;

    /// <summary>Largest LED count the 16-bit count field can express.</summary>
    public const int MaxLedCount = 65536;

    /// <summary>XOR constant of the header checksum.</summary>
    public const byte ChecksumSeed = 0x55;

    /// <summary>Frame length for <paramref name="ledCount"/> LEDs.</summary>
    public static int FrameLength(int ledCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ledCount, MaxLedCount);
        return HeaderLength + (ledCount * 3);
    }

    /// <summary>Writes the 6-byte header for <paramref name="ledCount"/> LEDs.</summary>
    public static void WriteHeader(int ledCount, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ledCount, MaxLedCount);
        if (destination.Length < HeaderLength)
        {
            throw new ArgumentException($"The destination must hold at least {HeaderLength} bytes.", nameof(destination));
        }

        var count = ledCount - 1;
        var high = (byte)(count >> 8);
        var low = (byte)count;
        destination[0] = (byte)'A';
        destination[1] = (byte)'d';
        destination[2] = (byte)'a';
        destination[3] = high;
        destination[4] = low;
        destination[5] = (byte)(high ^ low ^ ChecksumSeed);
    }

    /// <summary>
    /// Encodes <paramref name="rgb"/> (R,G,B per LED, wire order) as a complete frame into
    /// <paramref name="destination"/> and returns the frame length. Allocation-free.
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> rgb, Span<byte> destination)
    {
        if (rgb.IsEmpty || rgb.Length % 3 != 0)
        {
            throw new ArgumentException("The payload must hold three bytes per LED and at least one LED.", nameof(rgb));
        }

        var ledCount = rgb.Length / 3;
        var length = FrameLength(ledCount);
        if (destination.Length < length)
        {
            throw new ArgumentException($"The destination holds {destination.Length} bytes; {length} are needed.", nameof(destination));
        }

        WriteHeader(ledCount, destination);
        rgb.CopyTo(destination[HeaderLength..]);
        return length;
    }

    /// <summary>Encodes an all-black frame for <paramref name="ledCount"/> LEDs and returns its length.</summary>
    public static int EncodeBlack(int ledCount, Span<byte> destination)
    {
        var length = FrameLength(ledCount);
        if (destination.Length < length)
        {
            throw new ArgumentException($"The destination holds {destination.Length} bytes; {length} are needed.", nameof(destination));
        }

        WriteHeader(ledCount, destination);
        destination[HeaderLength..length].Clear();
        return length;
    }
}
