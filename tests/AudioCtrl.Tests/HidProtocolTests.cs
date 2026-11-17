using AudioCtrl.Services;

namespace AudioCtrl.Tests;

public class HidProtocolTests
{
    [Fact]
    public void BuildAck_ProducesExpectedFrame()
    {
        var ack = HidProtocol.BuildAck(0x25, HidProtocol.AckOk);

        Assert.Equal(HidProtocol.FrameSize, ack.Length);
        Assert.Equal(HidProtocol.DnAck, ack[0]);
        Assert.Equal(0x25, ack[1]);
        Assert.Equal(HidProtocol.AckOk, ack[2]);
    }

    [Fact]
    public void TryParseEvent_ReadsSeqEventAndLittleEndianStandId()
    {
        var frame = new byte[] { HidProtocol.UpEvent, 0x25, HidProtocol.EvLift, 0x05, 0xFF, 0, 0, 0 };

        Assert.True(HidProtocol.TryParseEvent(frame, out byte seq, out byte evt, out ushort standId));
        Assert.Equal(0x25, seq);
        Assert.Equal(HidProtocol.EvLift, evt);
        Assert.Equal(0xFF05, standId);
    }

    [Fact]
    public void TryParseInfo_ReadsVersionAndSensorState()
    {
        var frame = new byte[] { HidProtocol.UpInfo, 0, 0x23, 0x05, 0xFF, 1, 0, 0 };

        Assert.True(HidProtocol.TryParseInfo(frame, out ushort standId, out byte fw, out byte lifted));
        Assert.Equal(0xFF05, standId);
        Assert.Equal(0x23, fw);
        Assert.Equal(1, lifted);
    }

    [Fact]
    public void TryParseEvent_RejectsNonEventAndShortFrames()
    {
        var info = new byte[] { HidProtocol.UpInfo, 0, 0, 0, 0, 0, 0, 0 };
        var shortFrame = new byte[] { HidProtocol.UpEvent };

        Assert.False(HidProtocol.TryParseEvent(info, out _, out _, out _));
        Assert.False(HidProtocol.TryParseEvent(shortFrame, out _, out _, out _));
    }
}
