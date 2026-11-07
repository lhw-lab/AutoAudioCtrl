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

    /// <summary>
    /// 显示设备切换通知（线程安全）
    /// 如果已有 Toast 正在显示，则排队等待
    /// 同一设备 1 秒内重复通知会被去重，防止 SetDefaultEndpoint 三次调用触发多次回调
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <param name="deviceType">设备类型：播放/录音</param>
    /// <param name="deviceId">设备 ID（用于去重）</param>
    public static void Show(string deviceName, string deviceType, string? deviceId = null)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // 去重：同一设备 1 秒内不重复显示
            var now = DateTime.Now;
            if (deviceId != null &&
                deviceId == _lastShownDeviceId &&
                (now - _lastShowTime).TotalMilliseconds < 1000)
            {
                Logger.Info($"[Toast] 跳过重复通知: {deviceName}");
                return;
            }

            Logger.Info($"[Toast] 请求通知: {deviceType} -> {deviceName}");
            _pendingQueue.Enqueue(new ToastMessage(deviceName, deviceType, deviceId));

            // 防积压:队列超过 4 条时丢弃最旧的,避免事件风暴后回放几分钟
            while (_pendingQueue.Count > 4)
            {
                var dropped = _pendingQueue.Dequeue();
                Logger.Info($"[Toast] 队列积压,丢弃: {dropped.DeviceName}");
            }

            // 如果当前没有正在显示的 Toast，立即显示队列中的第一个
            if (_currentToast == null)
            {
                ShowNextToast();
            }
        });
    }

    /// <summary>
    /// 从队列中取出下一个 Toast 并显示
    /// 弹出前再次检查去重，防止队列中堆积了重复通知
    /// </summary>
    private static void ShowNextToast()
    {
        if (_pendingQueue.Count == 0)
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

        var toast = new SwitchToastWindow(msg.DeviceName, msg.DeviceType);
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
    private SwitchToastWindow(string deviceName, string deviceType)
    {
        InitializeComponent();

        // 设置文字内容（deviceType 为内部类型 "Playback"/"Recording"，文案在显示时按当前语言生成）
        DeviceNameText.Text = deviceName;
        StatusText.Text = Loc.F("S.Toast.DefaultFmt", Loc.T(deviceType == "Recording"
            ? "S.Dev.TypeRecording" : "S.Dev.TypePlayback"));
        DeviceIcon.Text = deviceType == "Recording" ? "🎤" : "🔊";

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
    /// 待显示的 Toast 消息
    /// </summary>
    private readonly struct ToastMessage(string deviceName, string deviceType, string? deviceId)
    {
        public string DeviceName { get; } = deviceName;
        public string DeviceType { get; } = deviceType;
        public string? DeviceId { get; } = deviceId;
    }
}
