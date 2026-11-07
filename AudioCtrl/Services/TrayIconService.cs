using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AudioCtrl.Helpers;
using AudioCtrl.ViewModels;

namespace AudioCtrl.Services;

/// <summary>
/// 系统托盘图标服务（纯 Win32 Shell_NotifyIcon + WPF ContextMenu，v0.13.0 去 WinForms）
/// - 图标：嵌入 AudioCtrl.ico 生成的 HICON（见 AppIcon）
/// - 消息：主窗口 HWND 钩子接收托盘回调（WM_APP 系自定义消息）
/// - 菜单：复用应用的深色菜单样式（DarkContextMenu/DarkMenuItem），每次右键现构建
/// 行为与旧 WinForms NotifyIcon 版一致：双击恢复窗口、关闭/最小化隐藏到托盘、右键快捷切换设备
/// </summary>
public class TrayIconService : IDisposable
{
    // ==================== Win32 常量与结构 ====================

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01;   // 使用 uCallbackMessage 回调
    private const uint NIF_ICON = 0x02;      // 使用 hIcon
    private const uint NIF_TIP = 0x04;       // 使用 szTip 提示文本

    private const uint WM_APP_TRAY = 0x8101;         // 自定义托盘回调消息（WM_APP 区间）
    private const int WM_LBUTTONDBLCLK = 0x0203;     // 左键双击
    private const int WM_RBUTTONUP = 0x0205;         // 右键抬起

    /// <summary>Shell_NotifyIcon 的数据结构（Unicode 版，截到 hBalloonIcon 即可）</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ==================== 状态 ====================

    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private HwndSource? _hwndSource;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _added;
    private bool _disposed;

    // ==================== 生命周期 ====================

    /// <summary>构造时只记录引用；等窗口 HWND 就绪（SourceInitialized）后再注册托盘图标</summary>
    public TrayIconService(MainWindow window, MainViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;

        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            InitializeIcon();
        }
        else
        {
            // MainWindow 构造函数里创建本服务时 HWND 还没生成，延后注册
            _window.SourceInitialized += (_, _) => InitializeIcon();
        }
    }

    /// <summary>注册托盘图标 + 挂载消息钩子（需窗口 HWND 已创建）</summary>
    private void InitializeIcon()
    {
        if (_added) return;
        try
        {
            _hIcon = AppIcon.CreateTrayHIcon();

            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
            _hwndSource?.AddHook(WndProc);

            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = new WindowInteropHelper(_window).Handle,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_APP_TRAY,
                hIcon = _hIcon,
                szTip = Loc.T("S.Tray.Tip"),
            };
            _added = Shell_NotifyIconW(NIM_ADD, ref data);
            Logger.Info(_added
                ? "[系统托盘] 托盘图标初始化完成（Shell_NotifyIcon 原生实现）"
                : $"[系统托盘] 托盘图标注册失败: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[系统托盘] 初始化异常: {ex.Message}");
        }
    }

    /// <summary>更新托盘悬停提示(语言切换后调用)</summary>
    public void UpdateTooltip()
    {
        if (!_added || _hwndSource == null) return;
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwndSource.Handle,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = Loc.T("S.Tray.Tip"),
        };
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>释放：移除托盘图标、销毁 HICON、摘除消息钩子</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added && _hwndSource != null)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwndSource.Handle,
                uID = 1,
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        AppIcon.DestroyHIcon(_hIcon);
        _hIcon = IntPtr.Zero;
    }

    // ==================== 托盘消息处理 ====================

    /// <summary>托盘回调：左键双击恢复窗口，右键弹出深色菜单（在 UI 线程执行）。
    /// 原生消息钩子里抛出的异常无人捕获会直接带崩进程，必须整体兜底</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_APP_TRAY) return IntPtr.Zero;
        try
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMsg)
            {
                case WM_LBUTTONDBLCLK:
                    ShowWindow();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    ShowContextMenu();
                    handled = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[系统托盘] 托盘消息处理异常: {ex}");
        }
        return IntPtr.Zero;
    }

    /// <summary>显示主窗口（取消最小化、显示、激活置前）</summary>
    private void ShowWindow()
    {
        Logger.Info("[托盘] 恢复主窗口");
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    /// <summary>隐藏主窗口到托盘</summary>
    public void HideWindowToTray()
    {
        Logger.Info("[托盘] 隐藏窗口到托盘");
        _window.Hide();
    }

    /// <summary>退出应用：移除托盘图标 → 关闭程序（支架连接由 MainWindow.OnClosed 统一清理）</summary>
    private void ExitApplication()
    {
        Logger.Info("[托盘] 用户选择退出程序");
        Dispose();
        Application.Current.Shutdown();
    }

    // ==================== 右键菜单（复用应用深色样式，每次现构建保证设备列表最新） ====================

    /// <summary>构建并弹出托盘右键菜单</summary>
    private void ShowContextMenu()
    {
        var menu = BuildMenu();
        // 弹菜单前把前台交给主窗口，保证点击菜单外任意处能正常关闭（托盘菜单惯例）
        SetForegroundWindow(new WindowInteropHelper(_window).Handle);
        menu.PlacementTarget = _window;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>构建菜单：打开主窗口 / 快速切换设备(子菜单) / 开机自启 / 退出</summary>
    private ContextMenu BuildMenu()
    {
        var ctxStyle = (Style)_window.FindResource("DarkContextMenu");
        var itemStyle = (Style)_window.FindResource("DarkMenuItem");
        var sepStyle = (Style)_window.FindResource("DarkMenuSeparator");
        Separator Sep() => new() { Style = sepStyle };

        var menu = new ContextMenu { Style = ctxStyle };

        // 打开主窗口
        var openItem = new MenuItem
        {
            Header = Loc.T("S.Tray.Open"),
            FontWeight = FontWeights.Bold,
            Style = itemStyle,
        };
        openItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(openItem);

        menu.Items.Add(Sep());

        // 快速切换设备（按播放/录音分组，默认设备加标记，点击即切换）
        var switchMenu = new MenuItem { Header = Loc.T("S.Tray.Quick"), Style = itemStyle };
        BuildQuickSwitchMenu(switchMenu, itemStyle, sepStyle);
        menu.Items.Add(switchMenu);

        menu.Items.Add(Sep());

        // 开机自启开关（深色菜单模板不渲染系统勾选框，用 ✓ 前缀显示当前状态）
        bool autoStartState = _viewModel.Settings.AutoStart;
        var autoStartItem = new MenuItem
        {
            Header = (autoStartState ? "✓ " : "　") + Loc.T("S.Tray.AutoStart"),
            IsCheckable = true,
            IsChecked = autoStartState,
            Style = itemStyle,
        };
        // 注意：WPF 的 MenuItem 点击时会先自动翻转 IsChecked 再触发 Click 事件，
        // 不能在处理器里再取反（曾因此导致"永远只能开启"的 bug）。
        // 这里用闭包记录点击前状态，翻转它得到新状态，并显式回写 IsChecked——
        // 逻辑不依赖 WPF 翻转时序，怎么触发都正确。
        autoStartItem.Click += (_, _) =>
        {
            bool newState = !autoStartState;
            autoStartState = newState;
            autoStartItem.IsChecked = newState;
            autoStartItem.Header = (newState ? "✓ " : "　") + Loc.T("S.Tray.AutoStart");
            _viewModel.Settings.AutoStart = newState;
            // 直接序列化保存（不走 SaveSettingsCommand——那里带旧热键校验逻辑）
            new SettingsService().Save(_viewModel.Settings);
            AutoStartService.Apply(newState);
            Logger.Info($"[托盘菜单] 开机自启: {(newState ? "开" : "关")}");
        };
        menu.Items.Add(autoStartItem);

        menu.Items.Add(Sep());

        // 退出
        var exitItem = new MenuItem { Header = Loc.T("S.Tray.Exit"), Style = itemStyle };
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        return menu;
    }

    /// <summary>填充"快速切换设备"子菜单（数据来自主 ViewModel 的实时设备集合）</summary>
    private void BuildQuickSwitchMenu(MenuItem switchMenu, Style itemStyle, Style sepStyle)
    {
        bool hasDevices = false;

        // 播放设备
        hasDevices |= AddDeviceGroup(switchMenu, itemStyle, _viewModel.ConnectedPlaybackDevices,
            Loc.T("S.Tray.PbGroup"));
        if (_viewModel.ConnectedRecordingDevices.Count > 0 && hasDevices)
            switchMenu.Items.Add(new Separator { Style = sepStyle });
        // 录音设备
        hasDevices |= AddDeviceGroup(switchMenu, itemStyle, _viewModel.ConnectedRecordingDevices,
            Loc.T("S.Tray.RecGroup"));

        if (!hasDevices)
            switchMenu.Items.Add(new MenuItem { Header = Loc.T("S.Tray.NoDevices"), IsEnabled = false, Style = itemStyle });
    }

    /// <summary>添加一组设备的菜单条目，返回该组是否有设备</summary>
    private bool AddDeviceGroup(MenuItem parent, Style itemStyle,
        System.Collections.ObjectModel.ObservableCollection<Models.AudioDeviceInfo> devices, string groupLabel)
    {
        if (devices.Count == 0) return false;

        var header = new MenuItem
        {
            Header = groupLabel,
            IsEnabled = false,
            FontWeight = FontWeights.Bold,
            Style = itemStyle,
        };
        parent.Items.Add(header);

        foreach (var device in devices)
        {
            var item = new MenuItem
            {
                Header = device.IsDefault ? Loc.F("S.Tray.DefaultFmt", device.Name) : device.Name,
                FontWeight = device.IsDefault ? FontWeights.Bold : FontWeights.Normal,
                Style = itemStyle,
            };
            item.Click += (_, _) =>
            {
                Logger.Info($"[托盘菜单] 快速切换设备: {device.Name}");
                if (_viewModel.SelectAudioDeviceCommand.CanExecute(device))
                    _viewModel.SelectAudioDeviceCommand.Execute(device);
            };
            parent.Items.Add(item);
        }
        return true;
    }
}
