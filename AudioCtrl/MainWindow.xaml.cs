using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioCtrl.Helpers;
using AudioCtrl.Models;
using AudioCtrl.Services;
using AudioCtrl.ViewModels;

namespace AudioCtrl;

/// <summary>
/// 主窗口代码(协议 v2 产品版)
/// - 单页模式:设备卡片 + 右键映射菜单 + 支架状态区(设置/帮助页暂时移除)
/// - HID 改为多支架管理(StandHidService)+ 调度器(StandScheduler)
/// - 全局快捷键功能未实现(HotkeyService 已随开源清理删除,见 CHANGELOG v0.15.1)
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>主窗口 ViewModel</summary>
    private readonly MainViewModel _viewModel;

    /// <summary>支架配置服务(Config/stand-settings.json)</summary>
    private readonly StandSettingsService _standSettings;

    /// <summary>多支架 HID 连接管理器</summary>
    private readonly StandHidService _standHid;

    /// <summary>支架调度器(拿起/放回 → 切换设备)</summary>
    private readonly StandScheduler _scheduler;

    /// <summary>系统托盘图标服务</summary>
    private TrayIconService? _trayIconService;

    /// <summary>底部事件历史:每个支架最后记录过的序号(重传的重复帧不重复记)</summary>
    private readonly Dictionary<ushort, byte> _lastLoggedSeq = new();

    public MainWindow()
    {
        Logger.Info("AudioCtrl 启动中...");

        InitializeComponent();

        // 设置窗口图标（任务栏/标题栏显示，v0.13.0 起从嵌入 ico 读取，不再依赖 System.Drawing）
        Icon = AppIcon.CreateWindowIcon();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Title = $"{Loc.T("S.App.Title")} {_viewModel.AppVersion}";

        // 支架产品服务链:配置 → 调度器 → HID 连接管理
        _standSettings = new StandSettingsService();
        _scheduler = new StandScheduler(_viewModel.AudioDeviceService, _standSettings);
        _standHid = new StandHidService();

        _standHid.FrameReceived += OnStandFrame;
        _standHid.StandRemoved += OnStandRemoved;
        _scheduler.StateChanged += OnSchedulerStateChanged;

        // 连接当前已插入的支架并向它们发 QUERY 收集身份
        RefreshStands();
        RefreshMappings();

        // USB 热插拔:支架插入/拔出时自动重连(WM_DEVICECHANGE 消息钩子在 OnSourceInitialized 挂载)

        // 枚举音频设备
        _viewModel.RefreshAudioDevices();

        // 系统托盘
        InitializeSystemTray();

        // 产品化:全局快捷键功能不提供(相关休眠代码已在开源清理中删除)
        Logger.Info("主窗口初始化完成(单页模式)");
    }

    // ==================== 支架协议 v2 ====================

    /// <summary>
    /// 重新枚举并连接所有支架,然后发 QUERY 收集身份
    /// </summary>
    private void RefreshStands()
    {
        var paths = _standHid.RefreshConnections();
        if (paths.Count > 0) _standHid.QueryAll();
        RefreshStandList();
    }

    /// <summary>
    /// HID 上行帧(读线程回调)→ 交给调度器处理 → 回发 ACK
    /// 调度在 UI 线程执行(COM 切换沿用本应用"COM 在 UI 线程"的既有模式)
    /// </summary>
    private void OnStandFrame(string devicePath, byte[] frame)
    {
        var hex = BitConverter.ToString(frame).Replace("-", " ");
        Logger.Debug($"[支架帧] {hex}");

        byte[]? ack = Dispatcher.Invoke(() => _scheduler.HandleFrame(devicePath, frame));
        if (ack != null)
        {
            // 发送移到线程池:避免同步 WriteFile 一旦阻塞卡死读线程(读线程必须永远活着收数据)
            System.Threading.Tasks.Task.Run(() => _standHid.SendFrame(devicePath, ack));
        }

        // 底部历史只记"拿起/放下"事件(可读文案),重传的重复帧不重复记录。
        // 去重字典的读写整体放在 UI 线程里:本方法跑在各支架的读线程上,
        // 多支架同时事件时 Dictionary 跨线程访问会损坏(多支架是产品卖点)
        if (HidProtocol.TryParseEvent(frame, out byte seq, out byte evt, out ushort standId))
        {
            Dispatcher.Invoke(() =>
            {
                if (_lastLoggedSeq.TryGetValue(standId, out byte lastSeq) && seq == lastSeq) return;
                _lastLoggedSeq[standId] = seq;
                string action = evt == HidProtocol.EvLift ? Loc.T("S.Hist.Lift") : Loc.T("S.Hist.Place");
                _viewModel.AddHidHistory(
                    Loc.F("S.Hist.Fmt", $"{DateTime.Now:HH:mm:ss}", standId.ToString("X4"), action));
            });
        }
    }

    /// <summary>支架 USB 断开(读线程回调)</summary>
    private void OnStandRemoved(string devicePath)
    {
        Dispatcher.Invoke(() => _scheduler.HandleStandRemoved(devicePath));
    }

    /// <summary>调度器状态变化(可能在读线程触发)→ 刷新支架状态区</summary>
    private void OnSchedulerStateChanged()
    {
        Dispatcher.Invoke(RefreshStandList);
    }

    /// <summary>刷新支架列表 UI 与底部状态文本(UI 线程)</summary>
    private void RefreshStandList()
    {
        var stands = _scheduler.Stands;
        _viewModel.UpdateStands(stands);
        int online = stands.Count(s => s.Online);
        _viewModel.HidStatusText = online > 0
            ? Loc.F("S.Stand.OnlineFmt", online)
            : Loc.T("S.Stand.Offline");
    }

    /// <summary>
    /// 刷新"映射配置"显示区:把当前所有配置(支架映射 + 全部放下)转成显示行
    /// 设备 ID → 名称一次性解析,避免多行重复枚举
    /// </summary>
    private void RefreshMappings()
    {
        _viewModel.Mappings.Clear();
        var devices = _viewModel.AudioDeviceService.EnumerateDevices();
        string NameOf(string? id) =>
            string.IsNullOrEmpty(id) ? "-"
                : (devices.FirstOrDefault(d => d.Id == id)?.Name ?? Loc.T("S.Map.DeviceMissing"));

        // 全部放下的外放配置(播放或录制任一存在即显示)
        var cfg = _standSettings.Current;
        if (cfg.FallbackPlaybackId != null || cfg.FallbackCaptureId != null)
        {
            var parts = new List<string>();
            if (cfg.FallbackPlaybackId != null) parts.Add(Loc.T("S.Map.PbPrefix") + NameOf(cfg.FallbackPlaybackId));
            if (cfg.FallbackCaptureId != null) parts.Add(Loc.T("S.Map.MicPrefix") + NameOf(cfg.FallbackCaptureId));
            _viewModel.Mappings.Add(new MappingRow
            {
                Icon = "\u23EA",   // ⏪
                Title = Loc.T("S.Map.FallbackTitle"),
                Detail = string.Join(" · ", parts),
                Tag = "fallback"
            });
        }

        // 各支架的拿起映射
        foreach (var m in cfg.Stands)
        {
            if (m.LiftPlaybackId == null && m.LiftCaptureId == null) continue;
            var parts = new List<string>();
            if (m.LiftPlaybackId != null) parts.Add(Loc.T("S.Map.LiftPbPrefix") + NameOf(m.LiftPlaybackId));
            if (m.LiftCaptureId != null) parts.Add(Loc.T("S.Map.MicPrefix") + NameOf(m.LiftCaptureId));
            _viewModel.Mappings.Add(new MappingRow
            {
                Icon = "\u25B6",   // ▶
                // 用 StandId 现场生成标题(不信任旧配置里可能残留的中文 DisplayName)
                Title = Loc.F("S.Stand.IdFmt", m.StandId.ToString("X4")),
                Detail = string.Join(" · ", parts),
                Tag = $"stand:{m.StandId}"
            });
        }
    }

    /// <summary>映射行删除按钮:按 Tag 清除对应配置</summary>
    private void OnDeleteMappingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag) return;
        bool changed = false;

        if (tag == "fallback")
        {
            if (_standSettings.Current.FallbackPlaybackId != null ||
                _standSettings.Current.FallbackCaptureId != null)
            {
                _standSettings.Current.FallbackPlaybackId = null;
                _standSettings.Current.FallbackCaptureId = null;
                changed = true;
                Logger.Info("[映射] 已清除「全部放下」配置");
            }
        }
        else if (tag.StartsWith("stand:"))
        {
            if (ushort.TryParse(tag["stand:".Length..], out ushort standId))
            {
                var m = _standSettings.GetMapping(standId);
                if (m != null && (m.LiftPlaybackId != null || m.LiftCaptureId != null))
                {
                    m.LiftPlaybackId = null;
                    m.LiftCaptureId = null;
                    changed = true;
                    Logger.Info($"[映射] 已清除支架 {standId:X4} 的映射");
                }
            }
        }

        if (changed)
        {
            _standSettings.Save();
            _scheduler.OnMappingChanged();
            RefreshMappings();
        }
    }

    /// <summary>
    /// USB 设备热插拔检测（v0.13.0 去 System.Management，改 WM_DEVICECHANGE 窗口消息）：
    /// DBT_DEVNODES_CHANGED 由系统广播给所有顶层窗口,无需 RegisterDeviceNotification;
    /// 一次插拔会连发多条通知,用 500ms 防抖定时器合并后再重扫支架
    /// </summary>
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 7;
    private const int WM_POWERBROADCAST = 0x0218;      // 电源事件广播(睡眠/唤醒)
    private const long PBT_APMRESUMEAUTOMATIC = 0x12;  // 自动唤醒(开盖/电源恢复)
    private const long PBT_APMRESUMESUSPEND = 0x07;    // 用户操作触发唤醒
    private HwndSource? _mainHwndSource;
    private DispatcherTimer? _hotplugTimer;
    private DispatcherTimer? _resumeTimer;

    /// <summary>窗口句柄就绪后挂消息钩子（热插拔消息从这进来）</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _mainHwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _mainHwndSource?.AddHook(MainWndProc);
        Logger.Info("USB 热插拔检测已启动(WM_DEVICECHANGE)");
    }

    /// <summary>主窗口消息钩子:处理设备变化广播与电源恢复事件</summary>
    private IntPtr MainWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && wParam.ToInt64() == DBT_DEVNODES_CHANGED)
        {
            // 防抖:每次通知重置定时器,静默 500ms 后才重扫(等系统完成设备枚举)
            _hotplugTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _hotplugTimer.Stop();
            _hotplugTimer.Tick -= OnHotplugTimerTick;   // 防重复订阅
            _hotplugTimer.Tick += OnHotplugTimerTick;
            _hotplugTimer.Start();
        }
        else if (msg == WM_POWERBROADCAST &&
                 (wParam.ToInt64() == PBT_APMRESUMEAUTOMATIC || wParam.ToInt64() == PBT_APMRESUMESUSPEND))
        {
            // 睡眠唤醒:USB 栈重新枚举需要数秒,延迟 3s 再重扫并向支架发 QUERY 对齐状态
            Logger.Info("[电源] 检测到系统唤醒,3 秒后重扫支架...");
            _resumeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _resumeTimer.Stop();
            _resumeTimer.Tick -= OnResumeTimerTick;
            _resumeTimer.Tick += OnResumeTimerTick;
            _resumeTimer.Start();
        }
        return IntPtr.Zero;
    }

    /// <summary>唤醒防抖到期:重扫支架(重连 + QueryAll 对齐状态)</summary>
    private void OnResumeTimerTick(object? sender, EventArgs e)
    {
        _resumeTimer?.Stop();
        RefreshStands();
    }

    /// <summary>防抖到期:重扫支架(增量连接,新增的连上、消失的断开)</summary>
    private void OnHotplugTimerTick(object? sender, EventArgs e)
    {
        _hotplugTimer?.Stop();
        Logger.Info("检测到 USB 设备变化,重扫支架...");
        RefreshStands();
    }

    // ==================== 设备卡片右键菜单(产品核心交互) ====================

    /// <summary>
    /// 设备卡片右键菜单(深色主题样式,固定两项):
    ///   播放卡片: 1.耳机放下时使用此设备播放  2.耳机拿起时使用此设备(子菜单选支架)
    ///   录制卡片: 1.耳机放下时使用此设备录制  2.耳机拿起时使用此设备(子菜单选支架)
    /// 删除映射操作在页面"映射配置"区完成,菜单里不提供
    /// </summary>
    private void OnDeviceCardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border border || border.Tag is not AudioDeviceInfo device) return;

        bool isPlayback = device.Type == "Playback";
        // 菜单壳在 XAML 里已挂好(保证第一次右键即弹出),这里动态填充菜单项
        var menu = border.ContextMenu ?? new ContextMenu { Style = (Style)border.FindResource("DarkContextMenu") };
        menu.Items.Clear();

        // 选项 1:耳机放下(全部放回)时使用此设备
        var fallbackItem = new MenuItem
        {
            Style = (Style)border.FindResource("DarkMenuItem"),
            Header = Loc.T(isPlayback ? "S.Ctx.PlacePb" : "S.Ctx.PlaceRec")
        };
        fallbackItem.Click += (_, _) =>
        {
            if (isPlayback) _standSettings.Current.FallbackPlaybackId = device.Id;
            else _standSettings.Current.FallbackCaptureId = device.Id;
            _standSettings.Save();
            _scheduler.OnMappingChanged();
            RefreshMappings();
            Logger.Info($"[映射] 全部放下{(isPlayback ? "播放" : "录制")} → {device.Name}");
        };
        menu.Items.Add(fallbackItem);

        // 选项 2:耳机拿起时使用此设备(子菜单:每个在线支架一项)
        var liftMenu = new MenuItem
        {
            Style = (Style)border.FindResource("DarkMenuItem"),
            Header = Loc.T("S.Ctx.Lift")
        };
        AddStandMenuItems(liftMenu, device, isCapture: !isPlayback, resourceSource: border);
        menu.Items.Add(liftMenu);

        // 菜单壳在 XAML 已挂好,填充完成后 WPF 自动弹出,无需再赋值
    }

    /// <summary>给"耳机拿起时使用此设备"子菜单添加每个在线支架的条目(同深色样式)</summary>
    private void AddStandMenuItems(MenuItem parent, AudioDeviceInfo device, bool isCapture,
                                   FrameworkElement resourceSource)
    {
        var onlineStands = _scheduler.Stands.Where(s => s.Online).ToList();
        if (onlineStands.Count == 0)
        {
            parent.IsEnabled = false;
            parent.Header += Loc.T("S.Ctx.NoStands");
            return;
        }

        foreach (var stand in onlineStands)
        {
            var standId = stand.StandId;
            var item = new MenuItem
            {
                Style = (Style)resourceSource.FindResource("DarkMenuItem"),
                Header = stand.StandIdHex + (stand.Lifted ? Loc.T("S.Ctx.LiftedMark") : "")
            };
            item.Click += (_, _) => AssignLiftDevice(standId, device, isCapture);
            parent.Items.Add(item);
        }
    }

    /// <summary>
    /// 把设备指定为某支架"拿起后"的目标设备。
    /// 指定播放设备时,若检测到同名配套麦克风:
    ///   - 首次使用弹一次提示,记住选择(默认联动切换麦克风)
    ///   - 之后按记住的选择自动配置,无需再问
    /// 用户也可以之后在录制卡片上右键单独指定其他麦克风覆盖
    /// </summary>
    private void AssignLiftDevice(ushort standId, AudioDeviceInfo device, bool isCapture)
    {
        var mapping = _standSettings.GetOrCreateMapping(standId);

        if (isCapture)
        {
            mapping.LiftCaptureId = device.Id;
            _standSettings.Save();
            _scheduler.OnMappingChanged();
            RefreshMappings();
            Logger.Info($"[映射] 支架 {standId:X4} 拿起后麦克风 → {device.Name}");
            return;
        }

        // 播放设备:重新指定时清掉旧麦克风,按联动规则重新决定
        mapping.LiftPlaybackId = device.Id;
        mapping.LiftCaptureId = null;

        var paired = _viewModel.AudioDeviceService.EnumerateDevices()
            .FirstOrDefault(d => d.Type == "Recording" && d.IsActive &&
                                 string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase));

        if (paired != null)
        {
            if (!_standSettings.Current.AutoMicPromptDone)
            {
                bool answer = Views.AppMessageBox.Show(this, Loc.T("S.Mic.Title"),
                    Loc.F("S.Mic.Body", device.Name),
                    yesNo: true);
                _standSettings.Current.AutoMicPromptDone = true;
                _standSettings.Current.AutoMicDefault = answer;
            }

            if (_standSettings.Current.AutoMicDefault)
            {
                mapping.LiftCaptureId = paired.Id;
                Logger.Info($"[映射] 支架 {standId:X4} 拿起后配套麦克风 → {paired.Name}");
            }
        }

        _standSettings.Save();
        _scheduler.OnMappingChanged();
        RefreshMappings();
        Logger.Info($"[映射] 支架 {standId:X4} 拿起后播放 → {device.Name}");
    }

    // ==================== 主题 / 语言切换 ====================

    /// <summary>主题选项资源键(0/1/2)——菜单文案走语言包</summary>
    private static readonly string[] ThemeKeys = { "S.Theme.System", "S.Theme.Light", "S.Theme.Dark" };

    /// <summary>语言选项资源键(0/1/2)</summary>
    private static readonly string[] LangKeys = { "S.Lang.System", "S.Lang.Zh", "S.Lang.En" };

    /// <summary>弹出三选一深色菜单的通用实现(主题/语言共用),当前项前打 ✓</summary>
    private void ShowChoiceMenu(System.Windows.Controls.Button anchor, string[] keys, int current,
                                Action<int> onPick)
    {
        var menu = new ContextMenu { Style = (Style)anchor.FindResource("DarkContextMenu") };
        for (int i = 0; i < keys.Length; i++)
        {
            int choice = i;
            var item = new MenuItem
            {
                Style = (Style)anchor.FindResource("DarkMenuItem"),
                Header = (i == current ? "\u2713  " : "      ") + Loc.T(keys[i])
            };
            item.Click += (_, _) => onPick(choice);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>主题按钮点击:三选一(0=跟随系统 1=浅色 2=深色)</summary>
    private void OnThemeButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        int current = Math.Clamp(_viewModel.Settings.Theme, 0, 2);
        ShowChoiceMenu(btn, ThemeKeys, current, ApplyTheme);
    }

    /// <summary>应用并持久化主题设置</summary>
    private void ApplyTheme(int theme)
    {
        ThemeService.ApplyTheme(theme);
        _viewModel.Settings.Theme = theme;
        _viewModel.SettingsService.Save(_viewModel.Settings);
        Logger.Info($"[主题] 已切换: {Loc.T(ThemeKeys[theme])}");
    }

    /// <summary>语言按钮点击:三选一(0=跟随系统 1=中文 2=English)</summary>
    private void OnLanguageButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        int current = Math.Clamp(_viewModel.Settings.Language, 0, 2);
        ShowChoiceMenu(btn, LangKeys, current, ApplyLanguage);
    }

    /// <summary>
    /// 应用并持久化语言设置,然后刷新所有"代码构建"的动态文本
    /// (XAML 侧 DynamicResource 已自动刷新;托盘/映射行/状态文本/窗口标题需手动重建)
    /// </summary>
    private void ApplyLanguage(int lang)
    {
        LanguageService.ApplyLanguage(lang);
        _viewModel.Settings.Language = lang;
        _viewModel.SettingsService.Save(_viewModel.Settings);

        Title = $"{Loc.T("S.App.Title")} {_viewModel.AppVersion}";
        _viewModel.RefreshAudioDevices();   // 状态文本 + 设备类型标签(重新枚举即重建)
        RefreshStandList();                 // 支架数状态文本 + 支架行(触发转换器重算)
        RefreshMappings();                  // 映射行文案
        _viewModel.ResetHidHistoryIfIdle(); // 底部"等待支架事件..."占位文本
        _trayIconService?.UpdateTooltip();  // 托盘悬停提示
        Logger.Info($"[语言] 已切换: {Loc.T(LangKeys[lang])}");
    }

    // ==================== 系统托盘 ====================

    /// <summary>初始化系统托盘功能</summary>
    private void InitializeSystemTray()
    {
        _trayIconService = new TrayIconService(this, _viewModel);
        StateChanged += OnWindowStateChanged;
        Logger.Info("[系统托盘] 托盘图标已创建");
    }

    /// <summary>窗口最小化时隐藏到系统托盘</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Logger.Info("[系统托盘] 窗口最小化，隐藏到托盘");
            _trayIconService?.HideWindowToTray();
        }
    }

    // ==================== UI 事件处理 ====================

    /// <summary>最小化按钮:隐藏到系统托盘</summary>
    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        _trayIconService?.HideWindowToTray();
    }

    /// <summary>音频设备卡片点击:设为默认设备</summary>
    private void OnAudioDeviceCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is AudioDeviceInfo device)
        {
            if (_viewModel.SelectAudioDeviceCommand.CanExecute(device))
            {
                _viewModel.SelectAudioDeviceCommand.Execute(device);
            }
        }
    }

    /// <summary>无边框窗口拖动</summary>
    private void OnWindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    /// <summary>关闭按钮:隐藏到系统托盘(从托盘菜单才能真正退出)</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Logger.Info("用户点击关闭，隐藏到系统托盘");
        _trayIconService?.HideWindowToTray();
    }

    /// <summary>窗口关闭时释放资源</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Logger.Info("窗口关闭，释放资源");
        _mainHwndSource?.RemoveHook(MainWndProc);
        _standHid.Dispose();
        _trayIconService?.Dispose();
    }
}
