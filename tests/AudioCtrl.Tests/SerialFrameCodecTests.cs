using AudioCtrl.Services;

namespace AudioCtrl.Tests;

public class SerialFrameCodecTests
{
    private static byte[] Payload(byte seed = 0)
        => Enumerable.Range(1, HidProtocol.FrameSize).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void Wrap_ProducesMagicPayloadAndCrc()
    {
        var payload = Payload();
        var frame = SerialFrameCodec.Wrap(payload);

        Assert.Equal(SerialFrameCodec.FrameSize, frame.Length);
        Assert.Equal(SerialFrameCodec.Magic0, frame[0]);
        Assert.Equal(SerialFrameCodec.Magic1, frame[1]);
        Assert.Equal(payload, frame[2..^1]);
        Assert.Equal(SerialFrameCodec.Crc8(frame, 2, HidProtocol.FrameSize), frame[^1]);
    }

    [Fact]
    public void StreamParser_HandlesSplitFrame()
    {
        var payload = Payload();
        var frame = SerialFrameCodec.Wrap(payload);
        var parser = new SerialFrameCodec.StreamParser();

        Assert.Empty(parser.Feed(frame[..5], 5));
        var parsed = parser.Feed(frame[5..], frame.Length - 5);

        var only = Assert.Single(parsed);
        Assert.Equal(payload, only);
    }

    [Fact]
    public void StreamParser_HandlesTwoBackToBackFrames()
    {
        var p1 = Payload(0);
        var p2 = Payload(42);
        var data = SerialFrameCodec.Wrap(p1).Concat(SerialFrameCodec.Wrap(p2)).ToArray();
        var parser = new SerialFrameCodec.StreamParser();

        var parsed = parser.Feed(data, data.Length);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(p1, parsed[0]);
        Assert.Equal(p2, parsed[1]);
    }

    [Fact]
    public void StreamParser_ResyncsAfterNoiseAndBadCrc()
    {
        var payload = Payload(7);
        var frame = SerialFrameCodec.Wrap(payload);
        var bad = (byte[])frame.Clone();
        bad[^1] ^= 0xFF;
        var data = new byte[] { 0x00, 0xFF, 0xA5 }
            .Concat(bad)
            .Concat(frame)
            .ToArray();
        var parser = new SerialFrameCodec.StreamParser();

        var parsed = parser.Feed(data, data.Length);

        var only = Assert.Single(parsed);
        Assert.Equal(payload, only);
    }
}
