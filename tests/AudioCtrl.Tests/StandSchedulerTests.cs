using System.IO;
using AudioCtrl.Models;
using AudioCtrl.Services;

namespace AudioCtrl.Tests;

public class StandSchedulerTests
{
    private sealed class FakeAudio : IAudioDeviceService
    {
        public List<AudioDeviceInfo> Devices { get; } = new();
        public List<(string Id, string Type)> Calls { get; } = new();
        public string? DefaultAllRolesId { get; set; }

        public List<AudioDeviceInfo> EnumerateDevices() => Devices;

        public bool SetDefaultDevice(string deviceId, string type)
        {
            Calls.Add((deviceId, type));
            return true;
        }

        public bool IsDefaultForAllRoles(string deviceId, string type) => deviceId == DefaultAllRolesId;
    }

    private static (StandScheduler Scheduler, FakeAudio Audio, StandSettingsService Settings) Create()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"aac-test-{Guid.NewGuid():N}.json");
        var settings = new StandSettingsService(configPath);
        var audio = new FakeAudio();
        return (new StandScheduler(audio, settings), audio, settings);
    }

    private static byte[] Event(byte seq, byte evt, ushort standId) => new byte[]
    {
        HidProtocol.UpEvent, seq, evt,
        (byte)(standId & 0xFF), (byte)(standId >> 8), 0, 0, 0
    };

    private static AudioDeviceInfo Device(string id, string type, bool isDefault = false) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        IsActive = true,
        IsDefault = isDefault,
    };

    [Fact]
    public void Lift_CaptureOnlyMapping_StillSwitchesRecordingDevice()
    {
        var (scheduler, audio, settings) = Create();
        const ushort standId = 0x1234;
        settings.GetOrCreateMapping(standId).LiftCaptureId = "mic1";
        audio.Devices.Add(Device("mic1", "Recording"));

        var ack = scheduler.HandleFrame("COM1", Event(1, HidProtocol.EvLift, standId));

        Assert.NotNull(ack);
        Assert.Equal(HidProtocol.AckOk, ack![2]);
        Assert.Contains(("mic1", "Recording"), audio.Calls);
    }

    [Fact]
    public void Place_FallbackCaptureOnly_StillSwitchesRecordingDevice()
    {
        var (scheduler, audio, settings) = Create();
        settings.Current.FallbackCaptureId = "mic2";
        audio.Devices.Add(Device("mic2", "Recording"));

        var ack = scheduler.HandleFrame("COM2", Event(2, HidProtocol.EvPlace, 0x2222));

        Assert.NotNull(ack);
        Assert.Equal(HidProtocol.AckOk, ack![2]);
        Assert.Contains(("mic2", "Recording"), audio.Calls);
    }

    [Fact]
    public void Place_NoPlaybackOrCaptureMapping_ReturnsNoMapping()
    {
        var (scheduler, audio, _) = Create();

        var ack = scheduler.HandleFrame("COM3", Event(3, HidProtocol.EvPlace, 0x3333));

        Assert.NotNull(ack);
        Assert.Equal(HidProtocol.AckNoMapping, ack![2]);
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void DeviceDefaultOnlyForMultimedia_IsStillSetForAllRoles()
    {
        var (scheduler, audio, settings) = Create();
        settings.Current.FallbackPlaybackId = "spk";
        audio.Devices.Add(Device("spk", "Playback", isDefault: true));
        audio.DefaultAllRolesId = null; // 只 Multimedia 默认,三角色不一致

        var ack = scheduler.HandleFrame("COM4", Event(4, HidProtocol.EvPlace, 0x4444));

        Assert.NotNull(ack);
        Assert.Equal(HidProtocol.AckOk, ack![2]);
        Assert.Contains(("spk", "Playback"), audio.Calls);
    }

    [Fact]
    public void DeviceDefaultForAllRoles_IsNotSetAgain()
    {
        var (scheduler, audio, settings) = Create();
        settings.Current.FallbackCaptureId = "mic5";
        audio.Devices.Add(Device("mic5", "Recording", isDefault: true));
        audio.DefaultAllRolesId = "mic5";

        var ack = scheduler.HandleFrame("COM5", Event(5, HidProtocol.EvPlace, 0x5555));

        Assert.NotNull(ack);
        Assert.Equal(HidProtocol.AckOk, ack![2]);
        Assert.Empty(audio.Calls);
    }
}
