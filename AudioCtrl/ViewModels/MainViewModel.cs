using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AudioCtrl.Helpers;
using AudioCtrl.Models;
using AudioCtrl.Services;
using AudioCtrl.Views;

namespace AudioCtrl.ViewModels;

/// <summary>
/// 主窗口 ViewModel
/// 负责管理 HID 设备状态、音频设备列表、设备卡片交互等 UI 数据
/// 采用 MVVM 模式，通过数据绑定驱动 UI 更新
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly AudioDeviceService _audioDeviceService;

    // ==================== HID 相关属性 ====================

    /// <summary>
    /// HID 设备连接状态文本（如"已连接"、"未检测到"）
    /// </summary>
    private string _hidStatusText = Loc.T("S.Stand.Detecting");
    public string HidStatusText
    {
        get => _hidStatusText;
        set => SetProperty(ref _hidStatusText, value);
    }

    /// <summary>
    /// 最近接收到的 HID 命令历史记录（最多 3 条，最新的在前）
    /// </summary>
    private readonly ObservableCollection<string> _hidHistory = new();
    public ObservableCollection<string> HidHistory
    {
        get => _hidHistory;
    }

    /// <summary>
    /// 最后接收到的 HID 数据（单条文本，向后兼容）
    /// </summary>
    private string _lastHidData = Loc.T("S.Status.Waiting");
    public string LastHidData
    {
        get => _lastHidData;
        set => SetProperty(ref _lastHidData, value);
    }

    /// <summary>
    /// HID 历史记录横向合并文本（供 UI 绑定），断开时显示"等待支架事件..."
    /// </summary>
    private string _hidHistoryText = Loc.T("S.Status.Waiting");
    public string HidHistoryText
    {
        get => _hidHistoryText;
        set => SetProperty(ref _hidHistoryText, value);
    }

    /// <summary>
    /// 添加一条 HID 接收记录，最多保留最近 3 条
    /// </summary>
    public void AddHidHistory(string entry)
    {
        _hidHistory.Insert(0, entry);
        while (_hidHistory.Count > 3)
            _hidHistory.RemoveAt(3);

        LastHidData = _hidHistory[0];
        HidHistoryText = string.Join("    ", _hidHistory);
    }

    /// <summary>
    /// 空闲状态下的占位文本(语言切换后调用:无历史记录时重置为当前语言的
    /// "等待支架事件...";已有历史则保持不动——历史条目保留事件发生时的语言)
    /// </summary>
    public void ResetHidHistoryIfIdle()
    {
        if (_hidHistory.Count == 0)
        {
            LastHidData = Loc.T("S.Status.Waiting");
            HidHistoryText = Loc.T("S.Status.Waiting");
        }
    }

    /// <summary>
    /// 已发现的 HID 设备数量
    /// </summary>
    private string _hidDeviceCount = "";
    public string HidDeviceCount
    {
        get => _hidDeviceCount;
        set => SetProperty(ref _hidDeviceCount, value);
    }

    // ==================== 耳机支架相关属性(协议 v2) ====================

    /// <summary>
    /// 已发现的支架列表(含离线但见过历史的),绑定到 UI 支架状态区
    /// </summary>
    private ObservableCollection<StandRuntime> _stands = new();
    public ObservableCollection<StandRuntime> Stands
    {
        get => _stands;
    }

    /// <summary>
    /// 映射配置显示行(右键配置的结果),绑定到 UI 映射配置区
    /// </summary>
    public ObservableCollection<MappingRow> Mappings { get; } = new();

    /// <summary>
    /// 用调度器当前状态刷新支架列表(在 UI 线程调用)
    /// </summary>
    public void UpdateStands(List<StandRuntime> stands)
    {
        _stands.Clear();
        foreach (var s in stands) _stands.Add(s);
    }

    // ==================== 音频设备相关属性 ====================

    /// <summary>
    /// 已连接的播放设备列表（扬声器/耳机/显示器音响等）
    /// 绑定到 UI 播放设备卡片区域
    /// </summary>
    private ObservableCollection<AudioDeviceInfo> _connectedPlaybackDevices = new();
    public ObservableCollection<AudioDeviceInfo> ConnectedPlaybackDevices
    {
        get => _connectedPlaybackDevices;
        set => SetProperty(ref _connectedPlaybackDevices, value);
    }

    /// <summary>
    /// 已连接的录制设备列表（麦克风/线路输入等）
    /// 绑定到 UI 录制设备卡片区域
    /// </summary>
    private ObservableCollection<AudioDeviceInfo> _connectedRecordingDevices = new();
    public ObservableCollection<AudioDeviceInfo> ConnectedRecordingDevices
    {
        get => _connectedRecordingDevices;
        set => SetProperty(ref _connectedRecordingDevices, value);
    }

    /// <summary>
    /// 未连接/已拔出的音频设备（折叠区域，默认收起）
    /// 点击"查看未连接设备"按钮后展开显示
    /// </summary>
    private ObservableCollection<AudioDeviceInfo> _disconnectedDevices = new();
    public ObservableCollection<AudioDeviceInfo> DisconnectedDevices
    {
        get => _disconnectedDevices;
        set => SetProperty(ref _disconnectedDevices, value);
    }

    /// <summary>
    /// 是否展开显示未连接设备区域
    /// 默认收起，用户点击切换按钮时更新
    /// </summary>
    private bool _showDisconnected;
    public bool ShowDisconnected
    {
        get => _showDisconnected;
        set => SetProperty(ref _showDisconnected, value);
    }

    /// <summary>
    /// 播放设备区域状态文本
    /// </summary>
    private string _playbackStatusText = "点击刷新按钮枚举播放设备";
    public string PlaybackStatusText
    {
        get => _playbackStatusText;
        set => SetProperty(ref _playbackStatusText, value);
    }

    /// <summary>
    /// 录制设备区域状态文本
    /// </summary>
    private string _recordingStatusText = "点击刷新按钮枚举录制设备";
    public string RecordingStatusText
    {
        get => _recordingStatusText;
        set => SetProperty(ref _recordingStatusText, value);
    }

    /// <summary>
    /// 总体音频设备状态文本
    /// </summary>
    private string _audioStatusText = "";
    public string AudioStatusText
    {
        get => _audioStatusText;
        set => SetProperty(ref _audioStatusText, value);
    }

    // ==================== 命令 ====================

    /// <summary>
    /// 刷新音频设备列表命令
    /// 点击刷新按钮时调用
    /// </summary>
    public ICommand RefreshAudioDevicesCommand { get; }

    /// <summary>
    /// 切换未连接设备区域的展开/收起状态
    /// 点击"查看未连接设备"时调用
    /// </summary>
    public ICommand ToggleDisconnectedCommand { get; }

    /// <summary>
    /// 点击音频设备卡片命令
    /// 点击设备卡片时调用（Step 5 将实现切换默认设备）
    /// </summary>
    public ICommand SelectAudioDeviceCommand { get; }

    // ==================== 版本号 ====================

    /// <summary>
    /// 应用程序版本号（绑定到标题栏）
    /// </summary>
    public string AppVersion { get; } = $"v{VersionInfo.CurrentVersion}";

    /// <summary>
    /// 主窗口 ViewModel 构造函数
    /// 初始化服务实例和命令绑定
    /// </summary>
    public MainViewModel()
    {
        _audioDeviceService = new AudioDeviceService();

        // 初始化命令
        RefreshAudioDevicesCommand = new RelayCommand(_ => RefreshAudioDevices());
        ToggleDisconnectedCommand = new RelayCommand(_ => ShowDisconnected = !ShowDisconnected);
        SelectAudioDeviceCommand = new RelayCommand(SelectAudioDevice);

        // 订阅系统默认设备变更事件（Windows 设置中切换时同步 UI）
        _audioDeviceService.DefaultPlaybackDeviceChanged += OnDefaultPlaybackChanged;
        _audioDeviceService.DefaultRecordingDeviceChanged += OnDefaultRecordingChanged;

        // 订阅设备列表变化事件（设备插入/拔出/启用/禁用时自动刷新 UI）
        _audioDeviceService.DeviceListChanged += OnDeviceListChanged;

        // 全局设置加载(主题/语言/自启;v0.16.0 起设置页 ViewModel 已随死代码清理删除)
        SettingsService = new SettingsService();
        Settings = SettingsService.Load();

        Logger.Info($"[MainViewModel] AudioCtrl {AppVersion} 初始化");
    }

    /// <summary>
    /// 全局应用设置(主题/语言/开机自启),修改后经 SettingsService.Save 持久化
    /// </summary>
    public AppSettings Settings { get; }

    /// <summary>
    /// 设置持久化服务(Config/settings.json)
    /// </summary>
    public SettingsService SettingsService { get; }

    /// <summary>
    /// 获取音频设备服务实例（供热键服务等外部组件切换设备用）
    /// </summary>
    public AudioDeviceService AudioDeviceService => _audioDeviceService;

    // ==================== 音频设备操作 ====================

    /// <summary>
    /// 刷新音频设备列表
    /// 调用 AudioDeviceService 枚举系统设备，并按连接状态分流到三个集合
    /// </summary>
    public void RefreshAudioDevices()
    {
        Logger.Info("[MainViewModel] 开始刷新音频设备...");
        AudioStatusText = Loc.T("S.Status.Enumerating");

        try
        {
            var devices = _audioDeviceService.EnumerateDevices();

            // 清空所有集合
            ConnectedPlaybackDevices.Clear();
            ConnectedRecordingDevices.Clear();
            DisconnectedDevices.Clear();

            // 按类型和连接状态分流设备
            foreach (var dev in devices)
            {
                if (dev.IsActive)
                {
                    // 已连接设备：按类型分别放入对应集合
                    if (dev.Type == "Playback")
                        ConnectedPlaybackDevices.Add(dev);
                    else
                        ConnectedRecordingDevices.Add(dev);
                }
                else
                {
                    // 未连接/已拔出设备：放入折叠区域
                    DisconnectedDevices.Add(dev);
                }
            }

            // 统计各类型数量
            int playbackCount = ConnectedPlaybackDevices.Count;
            int recordingCount = ConnectedRecordingDevices.Count;
            int disconnectedCount = DisconnectedDevices.Count;
            int defaultPlaybackCount = ConnectedPlaybackDevices.Count(d => d.IsDefault);
            int defaultRecordingCount = ConnectedRecordingDevices.Count(d => d.IsDefault);

            // 更新各区域状态文本
            PlaybackStatusText = Loc.F("S.Status.PlaybackFmt", playbackCount) +
                                 (defaultPlaybackCount > 0 ? Loc.T("S.Status.DefaultMarked") : "");
            RecordingStatusText = Loc.F("S.Status.RecordingFmt", recordingCount) +
                                  (defaultRecordingCount > 0 ? Loc.T("S.Status.DefaultMarked") : "");
            AudioStatusText = disconnectedCount > 0
                ? Loc.F("S.Status.SummaryDiscFmt", playbackCount + recordingCount, disconnectedCount)
                : Loc.F("S.Status.SummaryFmt", playbackCount + recordingCount);

            Logger.Info($"[MainViewModel] 音频设备刷新完成: {playbackCount} 播放, {recordingCount} 录音, {disconnectedCount} 未连接");
        }
        catch (Exception ex)
        {
            AudioStatusText = Loc.F("S.Status.EnumFail", ex.Message);
            Logger.Error($"[MainViewModel] 音频设备枚举失败: {ex}");
        }
    }

    /// <summary>
    /// 选中音频设备（设备卡片点击事件）
    /// 调用 AudioDeviceService 将点击的设备设为系统默认播放/录音设备
    /// </summary>
    private void SelectAudioDevice(object? parameter)
    {
        if (parameter is AudioDeviceInfo device)
        {
            // 双保险：非活跃端点(未连接)不设默认——Windows 会静默拒绝
            if (!device.IsActive)
            {
                Logger.Warn($"[MainViewModel] 忽略对未连接设备的点击: {device.Name}");
                return;
            }

            Logger.Info($"[MainViewModel] 用户点击设备: {device.Name} ({device.TypeLabel})");

            // 调用服务设置默认设备
            bool success = _audioDeviceService.SetDefaultDevice(device.Id, device.Type);

            if (!success)
            {
                Logger.Warn($"[MainViewModel] 设置默认设备失败: {device.Name}");
            }
            // 成功时 UI 会通过 OnDefaultPlaybackChanged / OnDefaultRecordingChanged 自动更新
        }
    }

    // ==================== 系统默认设备变更事件处理 ====================

    /// <summary>
    /// 系统默认播放设备变更事件处理器
    /// 当用户在 Windows 设置中切换默认扬声器/耳机时触发
    /// 更新 UI 中所有播放设备的 IsDefault 标记
    /// </summary>
    private void OnDefaultPlaybackChanged(string? newDefaultDeviceId)
    {
        Logger.Info($"[MainViewModel] 系统默认播放设备变更: {newDefaultDeviceId}");

        // 清除所有播放设备的默认标记
        foreach (var dev in ConnectedPlaybackDevices)
            dev.IsDefault = false;

        // 设置新默认设备的标记（AudioDeviceInfo 内部会触发 PropertyChanged，UI 自动刷新）
        if (newDefaultDeviceId != null)
        {
            var device = ConnectedPlaybackDevices.FirstOrDefault(d => d.Id == newDefaultDeviceId);
            if (device != null)
            {
                device.IsDefault = true;
                Logger.Info($"[MainViewModel] 新默认播放设备: {device.Name}");
                SwitchToastWindow.Show(device.Name, "Playback", newDefaultDeviceId);
            }
        }

        UpdateStatusTexts();
    }

    private void OnDefaultRecordingChanged(string? newDefaultDeviceId)
    {
        Logger.Info($"[MainViewModel] 系统默认录音设备变更: {newDefaultDeviceId}");

        // 清除所有录音设备的默认标记
        foreach (var dev in ConnectedRecordingDevices)
            dev.IsDefault = false;

        // 设置新默认设备的标记
        if (newDefaultDeviceId != null)
        {
            var device = ConnectedRecordingDevices.FirstOrDefault(d => d.Id == newDefaultDeviceId);
            if (device != null)
            {
                device.IsDefault = true;
                Logger.Info($"[MainViewModel] 新默认录音设备: {device.Name}");
                SwitchToastWindow.Show(device.Name, "Recording", newDefaultDeviceId);
            }
        }

        UpdateStatusTexts();
    }

    /// <summary>
    /// 更新各区域状态文本（播放/录制/总体）
    /// 在默认设备变更时调用，确保统计信息与实际状态一致
    /// </summary>
    private void UpdateStatusTexts()
    {
        int playbackCount = ConnectedPlaybackDevices.Count;
        int recordingCount = ConnectedRecordingDevices.Count;
        int disconnectedCount = DisconnectedDevices.Count;
        int defaultPlaybackCount = ConnectedPlaybackDevices.Count(d => d.IsDefault);
        int defaultRecordingCount = ConnectedRecordingDevices.Count(d => d.IsDefault);

        PlaybackStatusText = Loc.F("S.Status.PlaybackFmt", playbackCount) +
                             (defaultPlaybackCount > 0 ? Loc.T("S.Status.DefaultMarked") : "");
        RecordingStatusText = Loc.F("S.Status.RecordingFmt", recordingCount) +
                              (defaultRecordingCount > 0 ? Loc.T("S.Status.DefaultMarked") : "");
        AudioStatusText = disconnectedCount > 0
            ? Loc.F("S.Status.SummaryDiscFmt", playbackCount + recordingCount, disconnectedCount)
            : Loc.F("S.Status.SummaryFmt", playbackCount + recordingCount);
    }

    /// <summary>
    /// 设备列表变更事件处理器
    /// 当设备拔出/插入/启用/禁用时，自动重新枚举并刷新 UI
    /// 注意：COM 通知回调在后台线程，必须通过 Dispatcher 调度回 UI 线程
    /// </summary>
    private void OnDeviceListChanged()
    {
        // 将刷新操作调度到 WPF UI 线程，避免 ObservableCollection 跨线程修改异常
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Logger.Info("[MainViewModel] 检测到设备列表变化，自动刷新...");
            RefreshAudioDevices();
        });
    }
}
