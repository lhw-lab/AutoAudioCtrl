using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using AudioCtrl.Services;

namespace AudioCtrl;

/// <summary>
/// 应用程序入口类
/// 负责应用程序生命周期管理：单实例保护、开机自启应用、主题加载、全局资源初始化
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>单实例互斥锁（持有引用防止 GC 回收导致锁失效）</summary>
    private Mutex? _singleInstanceMutex;

    /// <summary>本实例是否持有互斥锁（第二实例不持有,退出时不得 Release——
    /// 曾因无条件 Release 抛"unsynchronized block"异常,被全局兜底捕获后修复）</summary>
    private bool _ownsInstanceMutex;

    /// <summary>
    /// 应用程序启动时调用
    /// 全局异常兜底 → 单实例检查 → 自启注册表刷新 → 控制台编码 → 主题 → 创建主窗口
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：托盘常驻程序最怕"某次 COM 回调抛异常直接静默退出"，
        // 未处理异常一律记日志（带堆栈）并保活，Release 版日志是唯一排障通道
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Helpers.Logger.Error("[未处理异常-非UI线程]", args.ExceptionObject as Exception);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Helpers.Logger.Error("[未观察Task异常]", args.Exception);
            args.SetObserved();
        };

        // 设置控制台输出编码为 UTF-8，解决 PowerShell/终端中文乱码问题
        try { System.Console.OutputEncoding = Encoding.UTF8; } catch { }

        // 先应用语言与主题（单实例提示弹窗也要跟随）；自启刷新放在单实例检查之后
        var startupSettings = ApplyStartupSettings();

        // 单实例保护：双开会竞争 HID 句柄、双托盘图标、双 Toast，直接拦截
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\AudioCtrl_SingleInstance",
            out bool createdNew);
        _ownsInstanceMutex = createdNew;
        if (!createdNew)
        {
            Helpers.Logger.Warn("[启动] 检测到已有实例在运行，本次启动退出");
            Views.AppMessageBox.Show(null, Helpers.Loc.T("S.App.Title"),
                Helpers.Loc.T("S.App.SingleInstance"),
                yesNo: false);
            Shutdown();
            return;
        }

        // 自启注册表刷新放在单实例检查之后：第二实例（如从 U 盘/临时副本启动）
        // 不得把 Run 键改写到自己的路径，否则原安装位置的自启会被顶掉
        AutoStartService.Apply(startupSettings.AutoStart);

        // 手动创建主窗口（替代 App.xaml 中的 StartupUri）。
        // --startup（开机自启带此参数）：静默启动——窗口不显示只驻托盘，避免每次开机弹窗打扰
        bool silentStart = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow();
        if (silentStart)
        {
            // 不调用 Show：HWND 经 EnsureHandle 提前创建（托盘钩子/热插拔钩子依赖窗口句柄）
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            Helpers.Logger.Info("[启动] 静默启动模式(--startup)，驻留托盘不显示窗口");
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// 启动时加载设置并应用语言与主题
    /// 从配置文件读取设置，如果文件不存在则使用默认值（跟随系统 / 自启开）
    /// </summary>
    private Models.AppSettings ApplyStartupSettings()
    {
        Models.AppSettings settings;
        try
        {
            settings = new SettingsService().Load();
        }
        catch
        {
            settings = new Models.AppSettings();
        }
        // 语言要在主题之前应用：后续弹窗（如单实例提示）立即使用正确语言
        LanguageService.ApplyLanguage(settings.Language);
        ThemeService.ApplyTheme(settings.Theme);
        return settings;
    }

    /// <summary>UI 线程异常兜底（带洪泛保护）：偶发异常记日志保活；
    /// 60 秒内超过 30 次说明程序已半坏（典型如绑定 bug 循环抛），
    /// 继续吞掉只会刷爆日志且行为不可预期——弹窗告知后放行崩溃</summary>
    private readonly Queue<DateTime> _uiExceptionTimes = new();

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        Helpers.Logger.Error("[未处理异常-UI线程]", args.Exception);

        var now = DateTime.Now;
        _uiExceptionTimes.Enqueue(now);
        while (_uiExceptionTimes.Count > 0 && (now - _uiExceptionTimes.Peek()).TotalSeconds > 60)
            _uiExceptionTimes.Dequeue();

        if (_uiExceptionTimes.Count > 30)
        {
            Helpers.Logger.Error($"[异常洪泛] 60秒内 UI 异常 {_uiExceptionTimes.Count} 次,停止兜底放行崩溃");
            Views.AppMessageBox.Show(null, Helpers.Loc.T("S.App.Title"),
                Helpers.Loc.T("S.App.CrashLoop"), yesNo: false);
            args.Handled = false;
            return;
        }
        args.Handled = true;   // 吞掉继续运行：托盘工具死掉比带着伤运行更糟
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 只有真正持有互斥锁的第一实例才能 Release（Release 要求调用线程是所有者）
        if (_ownsInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
