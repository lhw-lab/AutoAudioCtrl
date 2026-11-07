using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioCtrl.Models;

/// <summary>
/// 单个耳机支架的映射配置(持久化到 Config/stand-settings.json)
/// 所有设备引用一律存音频端点 ID(而不是名字),同型号设备也能精确区分
/// </summary>
public class StandMapping
{
    /// <summary>支架唯一 ID(固件 UID 哈希,如 0xA3F2)。界面显示名由显示层
    /// 按 StandId 现场生成(随语言变化),不再持久化(旧配置中的 DisplayName
    /// 字段反序列化时自动忽略,无需迁移)</summary>
    public ushort StandId { get; set; }

    /// <summary>耳机拿起后切换到的播放设备 ID(null=未配置)</summary>
    public string? LiftPlaybackId { get; set; }

    /// <summary>耳机拿起后切换到的录制设备 ID(null=不切换麦克风)</summary>
    public string? LiftCaptureId { get; set; }
}

/// <summary>
/// 支架产品全局配置
/// </summary>
public class StandAppSettings
{
    /// <summary>各支架的映射表</summary>
    public List<StandMapping> Stands { get; set; } = new();

    /// <summary>所有支架都放回后切换到的播放设备(外放,null=未配置则不切)</summary>
    public string? FallbackPlaybackId { get; set; }

    /// <summary>所有支架都放回后切换到的录制设备(null=不切换)</summary>
    public string? FallbackCaptureId { get; set; }

    /// <summary>"拿起耳机时自动切配套麦克风"的首次提示是否已经回答过</summary>
    public bool AutoMicPromptDone { get; set; }

    /// <summary>拿起耳机时是否默认联动切换麦克风(首次提示的选择)</summary>
    public bool AutoMicDefault { get; set; } = true;
}

/// <summary>
/// 映射配置区的显示行(UI 绑定用)
/// </summary>
public class MappingRow
{
    /// <summary>行标题(如"支架 A3F2"、"耳机全部放下时")</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>明细(如"拿起时播放 → G633 · 麦克风 → G633")</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>行图标字符</summary>
    public string Icon { get; set; } = "\u25B6";

    /// <summary>删除标识:"fallback" 或 "stand:支架ID十进制"</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// 支架运行时状态(UI 绑定用,含在线/拿起状态)
/// </summary>
public class StandRuntime : INotifyPropertyChanged
{
    private bool _lifted;
    private string? _devicePath;
    private DateTime _lastEventTime;

    /// <summary>支架唯一 ID</summary>
    public ushort StandId { get; init; }

    /// <summary>ID 的十六进制显示(如 "A3F2")</summary>
    public string StandIdHex => StandId.ToString("X4");

    /// <summary>当前 USB 设备路径(null=离线)</summary>
    public string? DevicePath
    {
        get => _devicePath;
        set { _devicePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(Online)); }
    }

    /// <summary>是否在线(USB 已连接)</summary>
    public bool Online => DevicePath != null;

    /// <summary>耳机是否被拿起</summary>
    public bool Lifted
    {
        get => _lifted;
        set { _lifted = value; OnPropertyChanged(); }
    }

    /// <summary>最近一次拿起的时间(多支架同时拿起时,后拿起的优先)</summary>
    public DateTime LastLiftTime { get; set; }

    /// <summary>最近一次事件时间</summary>
    public DateTime LastEventTime
    {
        get => _lastEventTime;
        set { _lastEventTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastEventText)); }
    }

    /// <summary>最近事件的可读时间</summary>
    public string LastEventText => LastEventTime == default ? "-" : LastEventTime.ToString("HH:mm:ss");

    /// <summary>最近一次事件的 ACK 结果(0=成功)</summary>
    public byte LastResult { get; set; }

    /// <summary>最近处理过的事件序号(去重用)</summary>
    public byte LastSeq { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
