using AmbientLight.Serial.Protocol;

namespace AmbientLight.Serial.Tests;

public sealed class AdalightEncoderTests
{
    [Fact]
    public void GoldenVector_MatchesTheFirmwareTests()
    {
        // Same bytes as test_golden_frame_decodes in firmware/esp32-adalight/test/test_native.
        byte[] rgb = [0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF];
        var frame = new byte[AdalightEncoder.FrameLength(3)];

        var length = AdalightEncoder.Encode(rgb, frame);

        Assert.Equal(15, length);
        Assert.Equal(
            new byte[] { 0x41, 0x64, 0x61, 0x00, 0x02, 0x57, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF },
            frame);
    }

    [Theory]
    [InlineData(1, 0x00, 0x00, 0x55)]
    [InlineData(100, 0x00, 0x63, 0x36)]
    [InlineData(256, 0x00, 0xFF, 0xAA)]
    [InlineData(257, 0x01, 0x00, 0x54)]
    [InlineData(300, 0x01, 0x2B, 0x7F)]
    [InlineData(1024, 0x03, 0xFF, 0xA9)]
    [InlineData(65536, 0xFF, 0xFF, 0x55)]
    public void Header_EncodesCountMinusOne_WithXorChecksum(int leds, byte high, byte low, byte checksum)
    {
        var header = new byte[6];

        AdalightEncoder.WriteHeader(leds, header);

        Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', high, low, checksum }, header);
    }

    [Fact]
    public void EveryLedCount_DecodesBackToItself()
    {
        var header = new byte[6];
        for (var leds = 1; leds <= AdalightEncoder.MaxLedCount; leds++)
        {
            AdalightEncoder.WriteHeader(leds, header);
            Assert.Equal((byte)(header[3] ^ header[4] ^ 0x55), header[5]);
            Assert.Equal(leds, ((header[3] << 8) | header[4]) + 1);
        }
    }

    [Fact]
    public void Black_IsAHeaderFollowedByZeros()
    {
        var frame = Enumerable.Repeat((byte)0xEE, AdalightEncoder.FrameLength(4) + 2).ToArray();

        var length = AdalightEncoder.EncodeBlack(4, frame);

        Assert.Equal(18, length);
        Assert.Equal((byte)'A', frame[0]);
        Assert.All(frame[6..18], b => Assert.Equal(0, b));
        Assert.Equal(0xEE, frame[18]); // nothing written past the frame
    }

    [Fact]
    public void Encode_RejectsInvalidInput()
    {
        var destination = new byte[64];

        Assert.Throws<ArgumentException>(() => AdalightEncoder.Encode([], destination));
        Assert.Throws<ArgumentException>(() => AdalightEncoder.Encode([1, 2], destination));
        Assert.Throws<ArgumentException>(() => AdalightEncoder.Encode(new byte[30], new byte[35]));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdalightEncoder.FrameLength(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdalightEncoder.FrameLength(65537));
    }

    [Fact]
    public void Encode_AllocatesNothing()
    {
        var rgb = new byte[300 * 3];
        var frame = new byte[AdalightEncoder.FrameLength(300)];
        AdalightEncoder.Encode(rgb, frame);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            AdalightEncoder.Encode(rgb, frame);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void StreamDecoder_FindsFramesInAMessyStream()
    {
        var a = new byte[AdalightEncoder.FrameLength(2)];
        var b = new byte[AdalightEncoder.FrameLength(1)];
        AdalightEncoder.Encode([1, 2, 3, 4, 5, 6], a);
        AdalightEncoder.Encode([7, 8, 9], b);
        byte[] stream = [0x00, (byte)'A', (byte)'d', .. a, (byte)'A', .. b];

        var payloads = AdalightStreamDecoder.DecodePayloads(stream);

        Assert.Equal(2, payloads.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, payloads[0]);
        Assert.Equal(new byte[] { 7, 8, 9 }, payloads[1]);
    }
}
