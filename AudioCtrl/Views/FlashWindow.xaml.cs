using System.Windows;
using System.Windows.Input;
using AudioCtrl.Helpers;
using AudioCtrl.Services;

namespace AudioCtrl.Views;

/// <summary>
/// 一键烧录对话框的交互逻辑。
/// 烧录流程编排在此:释放端口 → 抑制串口重连(esptool 需独占)→ 进程调用 esptool
/// → 完成后恢复重连并通知上层重扫(板子已被 esptool hard_reset,经热插拔路径重新识别)。
/// 端口释放与 Flashing 抑制都通过 StandSerialService 完成,上位机其余部分无感知。
/// </summary>
public partial class FlashWindow : Window
{
    private readonly StandSerialService _serial;
    private readonly Action? _onFinished;
    private readonly EspToolFlasher _flasher = new();
    private readonly EspToolFlasher.ToolPaths? _tools;
    private readonly Queue<string> _logTail = new();
    private CancellationTokenSource? _cts;

    public FlashWindow(StandSerialService serial, IReadOnlyList<string> candidatePorts, Action? onFinished)
    {
        InitializeComponent();
        _serial = serial;
        _onFinished = onFinished;

        // 工具与固件来源(发布包内置优先,开发环境回退到 PlatformIO 构建目录)
        _tools = EspToolFlasher.ResolveTools();
        ToolStatus.Text = _tools != null
            ? Loc.F("S.Flash.ToolSrc", _tools.Source, _tools.FirmwareDir)
            : Loc.T("S.Flash.NoTool");

        foreach (var p in candidatePorts)
        {
            PortList.Items.Add(p);
        }
        if (PortList.Items.Count > 0) PortList.SelectedIndex = 0;
        PortHint.Visibility = PortList.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        StartButton.IsEnabled = _tools != null && PortList.Items.Count > 0;

        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>无边框窗口拖动</summary>
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (PortList.SelectedItem is not string port) return;
        StartButton.IsEnabled = false;
        PortList.IsEnabled = false;
        StatusText.Text = Loc.T("S.Flash.Running");
        StatusText.Foreground = TryFindResource("PrimaryText") as System.Windows.Media.Brush
                                ?? System.Windows.Media.Brushes.Black;
        FlashProgress.Value = 0;
        _cts = new CancellationTokenSource();

        _flasher.OutputLine += OnFlashOutput;
        _flasher.Progress += OnFlashProgress;
        bool ok = false;
        try
        {
            // 1. 让出端口:断开连接(已确认的支架按离线处理)+ 抑制热插拔重扫期间的自动重连
            _serial.SetFlashing(true);
            _serial.CloseConnection(port);
            await Task.Delay(300);   // 给驱动一点时间完成句柄释放

            // 2. esptool 写入(免按键自动进烧录模式;完成即 hard_reset 重启)
            ok = await _flasher.FlashAsync(port, _tools, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("--- cancelled ---");
        }
        catch (Exception ex)
        {
            Logger.Error("[Flash] 烧录过程异常", ex);
            AppendLog(ex.Message);
        }
        finally
        {
            // 3. 恢复串口服务并触发重扫(新固件上电 → banner/QUERY → 识别为支架)
            _serial.SetFlashing(false);
            _flasher.OutputLine -= OnFlashOutput;
            _flasher.Progress -= OnFlashProgress;
        }

        StatusText.Text = ok ? Loc.T("S.Flash.Done") : Loc.T("S.Flash.Fail");
        StatusText.Foreground = ok
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x73, 0x73));
        StartButton.IsEnabled = true;
        PortList.IsEnabled = true;

        if (ok) _onFinished?.Invoke();   // 重扫支架(经 MainWindow.RefreshStands)
    }

    /// <summary>esptool 输出行(线程池线程)→ 调度到 UI 线程显示尾部 10 行</summary>
    private void OnFlashOutput(string line)
    {
        Dispatcher.Invoke(() => AppendLog(line));
    }

    /// <summary>写入进度百分比(线程池线程)</summary>
    private void OnFlashProgress(int pct)
    {
        Dispatcher.Invoke(() => FlashProgress.Value = pct);
    }

    private void AppendLog(string line)
    {
        _logTail.Enqueue(line);
        while (_logTail.Count > 10) _logTail.Dequeue();
        LogText.Text = string.Join(Environment.NewLine, _logTail);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();   // Closed 回调里取消进行中的烧录
    }
}
