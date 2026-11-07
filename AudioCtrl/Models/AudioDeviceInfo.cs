using System.ComponentModel;
using AudioCtrl.Helpers;
using System.Runtime.CompilerServices;

namespace AudioCtrl.Models;

/// <summary>
/// 音频设备信息模型
/// 用于存储系统音频设备的基本信息，包括设备名称、路径、类型等
/// 实现 INotifyPropertyChanged 接口，支持 UI 自动刷新（如默认设备标记切换）
/// </summary>
public class AudioDeviceInfo : INotifyPropertyChanged
{
    /// <summary>
    /// 属性变化事件，WPF 绑定系统监听此事件来更新界面
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发单个属性变化通知
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // ==================== 不可变属性（初始化后不变化，无需通知） ====================

    /// <summary>
    /// 设备唯一标识符（Windows Core Audio API 生成的字符串 ID）
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 设备显示名称（用户友好的名称，如"扬声器 (Realtek Audio)"）
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 设备类型：Playback（播放/扬声器）或 Recording（录制/麦克风）
    /// </summary>
    public string Type { get; init; } = "Playback";

    /// <summary>
    /// 设备完整路径（用于 API 调用的设备标识符）
    /// </summary>
    public string DevicePath { get; init; } = string.Empty;

    /// <summary>
    /// 设备是否处于活动状态（未禁用）
    /// </summary>
    public bool IsActive { get; init; } = true;

    // ==================== 可变属性（运行时会被 ViewModel 更新，需要通知） ====================

    private bool _isDefault;

    /// <summary>
    /// 是否为系统当前默认设备
    /// 当用户在 Windows 设置中切换默认设备时，此属性会被 ViewModel 更新
    /// 设置后自动通知 WPF 刷新 UI 中的 DataTrigger（绿色边框 + ✓ 标记）
    /// </summary>
    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (_isDefault != value)
            {
                _isDefault = value;
                OnPropertyChanged();
            }
        }
    }

    // ==================== 只读计算属性 ====================

    /// <summary>
    /// 用于 UI 显示的设备图标符号
    /// </summary>
    public string IconSymbol => Type == "Playback" ? "\u25B6" : "\u25CF"; // ▶ 播放 / ● 录制

    /// <summary>
    /// 设备类型标签（按当前界面语言显示）
    /// </summary>
    public string TypeLabel => Loc.T(Type == "Playback" ? "S.Dev.TypePlayback" : "S.Dev.TypeRecording");

    /// <summary>
    /// 返回设备的显示字符串（用于调试和日志）
    /// </summary>
    public override string ToString() => $"[{TypeLabel}] {Name}";
}
