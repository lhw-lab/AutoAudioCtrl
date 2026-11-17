using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioCtrl.Helpers;

namespace AudioCtrl.Views;

/// <summary>
/// 设备切换通知弹窗
/// 从屏幕右下角滑入，显示 3 秒后渐隐消失
/// 内置队列管理，同一时间只显示一个 Toast
/// </summary>
public partial class SwitchToastWindow : Window
{
    // ============================================================
    // 静态队列管理
    // ============================================================

    private static readonly Queue<ToastMessage> _pendingQueue = new();
    private static SwitchToastWindow? _currentToast;
    private static string? _lastShownDeviceId;  // 防重复：记录最后一次显示的设备 ID
    private static DateTime _lastShowTime;        // 防重复：最后一次显示的时间

    /// <summary>通知总开关(托盘菜单"切换通知"控制,关闭后 Show() 全部静默)。
    /// 关闭瞬间连带清空排队与合并窗口——"全部静默"不留尾巴
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                _pendingQueue.Clear();
                _mergeTimer.Stop();
                var current = _currentToast;
                _currentToast = null;
                current?.Close();   // 关闭开关时，正在显示的这条也立即消失
            }
        }
    }
    private static bool _enabled = true;

    /// <summary>合并窗口:首条通知延迟显示,期间到达的另一类型(播放/录制)合并为一条</summary>
    private static readonly DispatcherTimer _mergeTimer = CreateMergeTimer();
    private static DispatcherTimer CreateMergeTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) => { t.Stop(); if (_currentToast == null) ShowNextToast(); };
        return t;
    }

    /// <summary>
    /// 显示设备切换通知（线程安全）
    /// 如果已有 Toast 正在显示，则排队等待
    /// 同一设备 1 秒内重复通知会被去重，防止 SetDefaultEndpoint 三次调用触发多次回调
    /// 同一次切换的播放+录制两类通知会合并为一条(250ms 合并窗口)
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <param name="deviceType">设备类型：播放/录音</param>
    /// <param name="deviceId">设备 ID（用于去重）</param>
    public static void Show(string deviceName, string deviceType, string? deviceId = null)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (!Enabled)
            {
                Logger.Debug($"[Toast] 通知已关闭,静默: {deviceType} -> {deviceName}");
                return;
            }

            // 去重：同一设备 1 秒内不重复显示
            var now = DateTime.Now;
            if (deviceId != null &&
                deviceId == _lastShownDeviceId &&
                (now - _lastShowTime).TotalMilliseconds < 1000)
            {
                Logger.Info($"[Toast] 跳过重复通知: {deviceName}");
                return;
            }

            // 合并:队列中恰好有一条未显示的另一类型通知 → 播放+录制并为一条
            if (_pendingQueue.Count == 1 &&
                _pendingQueue.Peek().DeviceType != deviceType &&
                _pendingQueue.Peek().DeviceType != "Both" && deviceType != "Both")
            {
                var first = _pendingQueue.Dequeue();
                var pb = first.DeviceType == "Playback" ? first : new ToastMessage(deviceName, deviceType, deviceId);
                var rec = first.DeviceType == "Playback" ? new ToastMessage(deviceName, deviceType, deviceId) : first;
                _pendingQueue.Enqueue(new ToastMessage(pb.DeviceName, "Both", pb.DeviceId, rec.DeviceName));
                Logger.Info($"[Toast] 合并通知: 播放 {pb.DeviceName} + 录制 {rec.DeviceName}");
            }
            else
            {
                Logger.Info($"[Toast] 请求通知: {deviceType} -> {deviceName}");
                _pendingQueue.Enqueue(new ToastMessage(deviceName, deviceType, deviceId));
            }

            // 防积压:队列超过 4 条时丢弃最旧的,避免事件风暴后回放几分钟
            while (_pendingQueue.Count > 4)
            {
                var dropped = _pendingQueue.Dequeue();
                Logger.Info($"[Toast] 队列积压,丢弃: {dropped.DeviceName}");
            }

            // 当前无显示中的 Toast:启动合并窗口(到期仍无伙伴就显示队首)
            if (_currentToast == null && !_mergeTimer.IsEnabled)
            {
                _mergeTimer.Start();
            }
        });
    }

    /// <summary>
    /// 从队列中取出下一个 Toast 并显示
    /// 弹出前再次检查去重，防止队列中堆积了重复通知
    /// </summary>
    private static void ShowNextToast()
    {
        if (!Enabled || _pendingQueue.Count == 0)
            return;

        var msg = _pendingQueue.Dequeue();

        // 弹出前再次去重：如果与上一次显示的设备相同，跳过
        var now = DateTime.Now;
        if (msg.DeviceId != null &&
            msg.DeviceId == _lastShownDeviceId &&
            (now - _lastShowTime).TotalMilliseconds < 5000)
        {
            Logger.Info($"[Toast] 弹出前去重跳过: {msg.DeviceType} -> {msg.DeviceName}");
            // 继续检查队列中是否有下一个
            ShowNextToast();
            return;
        }

        var toast = new SwitchToastWindow(msg.DeviceName, msg.DeviceType, msg.DeviceName2);
        _currentToast = toast;

        // 记录最后一次显示，用于去重判断
        _lastShownDeviceId = msg.DeviceId;
        _lastShowTime = now;

        toast.Show();
        Logger.Info($"[Toast] 显示通知: {msg.DeviceType} -> {msg.DeviceName}");
    }

    /// <summary>
    /// 当前 Toast 动画结束后调用，清理并显示下一个
    /// </summary>
    private void ReleaseAndQueueNext()
    {
        _currentToast = null;
        ShowNextToast();
    }

    // ============================================================
    // 实例成员
    // ============================================================

    private const int DisplayDurationMs = 3000;  // 显示时长
    private const int FadeInMs = 300;            // 入场动画时长
    private const int FadeOutMs = 400;           // 出场动画时长

    /// <summary>
    /// 私有构造函数，通过 Show() 静态方法创建
    /// </summary>
    private SwitchToastWindow(string deviceName, string deviceType, string? deviceName2 = null)
    {
        InitializeComponent();

        // 设置文字内容（deviceType 为内部类型 "Playback"/"Recording"/"Both"，文案在显示时按当前语言生成）
        if (deviceType == "Both" && deviceName2 != null)
        {
            // 合并通知:播放设备名 + 内联麦克风名,一条 Toast 表达双重切换
            DeviceNameText.Text = $"{deviceName}  ·  🎤 {deviceName2}";
            StatusText.Text = Loc.T("S.Toast.BothFmt");
            DeviceIcon.Text = "🔊";
        }
        else
        {
            DeviceNameText.Text = deviceName;
            StatusText.Text = Loc.F("S.Toast.DefaultFmt", Loc.T(deviceType == "Recording"
                ? "S.Dev.TypeRecording" : "S.Dev.TypePlayback"));
            DeviceIcon.Text = deviceType == "Recording" ? "🎤" : "🔊";
        }

        // 定位到屏幕右下角（考虑窗口大小）
        PositionAtBottomRight();

        // 初始状态：透明 + 向下偏移（入场动画起点）
        Opacity = 0;
    }

    /// <summary>
    /// 将窗口定位到屏幕右下角
    /// </summary>
    private void PositionAtBottomRight()
    {
        // 获取主工作区（排除任务栏）
        var workArea = SystemParameters.WorkArea;
        var taskbarHeight = SystemParameters.PrimaryScreenHeight - workArea.Height;
        var rightMargin = 20;
        var bottomMargin = taskbarHeight + 20;

        Left = workArea.Right - Width - rightMargin;
        Top = workArea.Bottom - Height - bottomMargin;
    }

    /// <summary>
    /// 窗口加载完成后启动动画序列
    /// 1. 入场：淡入 + 上滑
    /// 2. 停留 3 秒
    /// 3. 出场：淡出 + 下滑
    /// 4. 关闭并释放队列
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 入场动画：淡入 + 上滑
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);

        // 使用 TranslateTransform 实现上滑效果
        var slideIn = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(FadeInMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var transform = new TranslateTransform();
        RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, slideIn);

        // 停留后出场动画
        var showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DisplayDurationMs) };
        showTimer.Tick += (s, _) =>
        {
            showTimer.Stop();

            // 出场动画：淡出 + 下滑
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var slideOut = new DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(FadeOutMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s2, _) =>
            {
                Close();
                ReleaseAndQueueNext();
            };

            BeginAnimation(OpacityProperty, fadeOut);
            transform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        };
        showTimer.Start();
    }

    /// <summary>
    /// 待显示的 Toast 消息（DeviceName2 仅合并通知使用:第二设备名/录制设备名）
    /// </summary>
    private readonly struct ToastMessage(string deviceName, string deviceType, string? deviceId, string? deviceName2 = null)
    {
        public string DeviceName { get; } = deviceName;
        public string DeviceType { get; } = deviceType;
        public string? DeviceId { get; } = deviceId;
        public string? DeviceName2 { get; } = deviceName2;
    }
}
