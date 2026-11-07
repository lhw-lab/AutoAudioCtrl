using AudioCtrl.Helpers;
using AudioCtrl.Models;

namespace AudioCtrl.Services;

/// <summary>
/// 支架调度器(产品的"大脑")
///
/// 职责:
///   1. 解析支架上行帧(EVENT/INFO),按 seq 去重(重复帧只补 ACK 不重复执行)
///   2. 维护每个支架的运行时状态(在线/拿起/最近事件)
///   3. 调度规则:后拿起者优先;放回时若还有其他拿起的耳机则切回最近拿起的那副;
///      全部放回则切到"外放"(用户配置的 fallback 设备)
///   4. 执行设备切换(按设备 ID),并给出本次事件的 ACK 结果码
///
/// 不持有 HID 句柄:收到帧/发送 ACK 由 UI 层(MainWindow)中转,便于测试
/// </summary>
public class StandScheduler
{
    private readonly AudioDeviceService _audio;
    private readonly StandSettingsService _settings;

    /// <summary>standId → 运行时状态(含已离线但见过历史的支架)</summary>
    private readonly Dictionary<ushort, StandRuntime> _stands = new();

    private readonly object _lock = new();

    /// <summary>状态变化(UI 需要刷新支架列表时触发,可能在读线程回调)</summary>
    public event Action? StateChanged;

    public StandScheduler(AudioDeviceService audio, StandSettingsService settings)
    {
        _audio = audio;
        _settings = settings;
    }

    /// <summary>当前所有已知支架(快照,UI 绑定用)</summary>
    public List<StandRuntime> Stands
    {
        get { lock (_lock) return _stands.Values.ToList(); }
    }

    /// <summary>
    /// 处理一帧上行数据(读线程回调调用)
    /// </summary>
    /// <returns>需要回发给该支架的下行帧(ACK);INFO 帧返回 null</returns>
    public byte[]? HandleFrame(string devicePath, byte[] frame)
    {
        // ---- EVENT 帧:拿起/放回事件 ----
        if (HidProtocol.TryParseEvent(frame, out byte seq, out byte evt, out ushort standId))
        {
            byte result;
            lock (_lock)
            {
                var rt = GetOrCreate(standId);
                rt.DevicePath = devicePath;   // 标记在线

                // 去重:同一 seq 的重发(ACK 丢了),只补 ACK 不重复执行
                if (seq == rt.LastSeq && rt.LastEventTime != default)
                {
                    Logger.Info($"[Scheduler] 支架 {standId:X4} 重复帧 seq={seq},补发 ACK(结果 {rt.LastResult})");
                    return HidProtocol.BuildAck(seq, rt.LastResult);
                }

                rt.LastSeq = seq;
                rt.LastEventTime = DateTime.Now;
                if (evt == HidProtocol.EvLift)
                {
                    rt.Lifted = true;
                    rt.LastLiftTime = DateTime.Now;
                }
                else
                {
                    rt.Lifted = false;
                }
            }

            Logger.Info($"[Scheduler] 支架 {standId:X4} 事件: {(evt == HidProtocol.EvLift ? "拿起" : "放回")} (seq={seq})");

            // 调度并切换(不在锁内做,避免枚举设备耗时阻塞)
            result = RecomputeAndApply();

            lock (_lock)
            {
                var rt = GetOrCreate(standId);
                rt.LastResult = result;
            }
            StateChanged?.Invoke();
            return HidProtocol.BuildAck(seq, result);
        }

        // ---- INFO 帧:连接时的自报身份(含当前传感器状态) ----
        if (HidProtocol.TryParseInfo(frame, out ushort infoId, out byte fw, out byte sensorLifted))
        {
            bool stateChanged;
            lock (_lock)
            {
                var rt = GetOrCreate(infoId);
                bool isNewConnection = rt.DevicePath != devicePath;   // 首见或换了路径(重连)
                bool wasLifted = rt.Lifted;
                rt.DevicePath = devicePath;
                rt.Lifted = sensorLifted == 1;
                // false→true 的 INFO 对齐要刷新 LastLiftTime:
                // "后拿起者优先"按此排序,若沿用离线前的旧时间,另一支架会被误判为更晚拿起。
                // 重复 INFO(true→true)不动,避免每次 QUERY 都刷新排序
                if (rt.Lifted && !wasLifted) rt.LastLiftTime = DateTime.Now;

                // 仅在(重)连接时清去重状态：固件复位后 tx_seq 从头计数，旧 LastSeq
                // 可能吞掉复位后的首个事件。已连接期间的重复 QUERY/INFO 不清——
                // 避免恰好与重传 EVENT 竞争时把重传当新事件重复执行
                if (isNewConnection)
                {
                    rt.LastSeq = 0;
                    rt.LastEventTime = default;
                    rt.LastResult = HidProtocol.AckOk;
                }

                Logger.Info($"[Scheduler] 支架 {infoId:X4} 上线: 固件 v{(fw >> 4)}.{fw & 0xF}, 耳机{(rt.Lifted ? "已被拿起" : "在架上")}");

                // 状态对齐的判定：传感器状态与主机认知不同才动。
                //   启动时在架上(认知false,实际false) → 不动,不抢用户当前输出
                //   启动/重连时已被拿起(false→true)  → 切到该支架绑定的设备
                //   主机以为拿起,实际已放回(true→false,如睡眠期间放回) → 切回外放
                stateChanged = rt.Lifted != wasLifted;
            }
            StateChanged?.Invoke();

            if (stateChanged)
            {
                Logger.Info($"[Scheduler] 支架 {infoId:X4} 上线状态与主机不一致,对齐音频状态");
                RecomputeAndApply();
                StateChanged?.Invoke();
            }
            return null;
        }

        return null;   // 未知帧类型,忽略
    }

    /// <summary>
    /// 支架拔出(USB 断开):标记离线;若该支架耳机处于拿起状态,视为放回并重新调度
    /// </summary>
    public void HandleStandRemoved(string devicePath)
    {
        bool needRecompute = false;
        lock (_lock)
        {
            var rt = _stands.Values.FirstOrDefault(s => s.DevicePath == devicePath);
            if (rt != null)
            {
                Logger.Info($"[Scheduler] 支架 {rt.StandIdHex} 离线(耳机{(rt.Lifted ? "曾被拿起,按放回处理" : "在架上")})");
                if (rt.Lifted)
                {
                    rt.Lifted = false;
                    needRecompute = true;
                }
                rt.DevicePath = null;
            }
        }
        if (needRecompute)
        {
            RecomputeAndApply();
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 核心调度:重新计算当前应使用的设备并执行切换
    /// 规则:在线且被拿起的支架中,最近拿起者优先;没有拿起的 → fallback(外放)
    /// </summary>
    /// <returns>本次触发事件的 ACK 结果码</returns>
    public byte RecomputeAndApply()
    {
        string? targetPlayback;
        string? targetCapture;
        byte result = HidProtocol.AckOk;

        // ---- 1. 决策:找出"当前应该用哪个设备" ----
        StandRuntime? active;
        lock (_lock)
        {
            active = _stands.Values
                .Where(s => s.Online && s.Lifted)
                .OrderByDescending(s => s.LastLiftTime)
                .FirstOrDefault();
        }

        if (active != null)
        {
            // 有耳机被拿起 → 用该支架映射的设备
            var m = _settings.GetMapping(active.StandId);
            if (m?.LiftPlaybackId == null)
            {
                Logger.Warn($"[Scheduler] 支架 {active.StandIdHex} 未配置映射,不切换");
                return HidProtocol.AckNoMapping;
            }
            targetPlayback = m.LiftPlaybackId;
            targetCapture = m.LiftCaptureId;
        }
        else
        {
            // 全部放回 → 用 fallback(外放);未配置则不切
            targetPlayback = _settings.Current.FallbackPlaybackId;
            targetCapture = _settings.Current.FallbackCaptureId;
            if (targetPlayback == null)
            {
                Logger.Info("[Scheduler] 全部放回,但未配置外放设备,不切换");
                return HidProtocol.AckNoMapping;
            }
        }

        // ---- 2. 执行切换(按设备 ID,先枚举确认设备在线) ----
        var all = _audio.EnumerateDevices();

        var pb = all.FirstOrDefault(d => d.Id == targetPlayback && d.IsActive);
        if (pb == null)
        {
            Logger.Warn($"[Scheduler] 目标播放设备不在线(ID 末段 ...{LastSegment(targetPlayback)}),放弃播放切换");
            result = HidProtocol.AckDeviceMissing;
        }
        else
        {
            _audio.SetDefaultDevice(pb.Id, "Playback");
            Logger.Info($"[Scheduler] 已切换播放设备 → {pb.Name}");
        }

        // 麦克风尽力而为:失败不影响播放结果码(事件主体是播放设备)
        if (targetCapture != null)
        {
            var cap = all.FirstOrDefault(d => d.Id == targetCapture && d.IsActive);
            if (cap != null)
            {
                _audio.SetDefaultDevice(cap.Id, "Recording");
                Logger.Info($"[Scheduler] 已切换录制设备 → {cap.Name}");
            }
            else
            {
                Logger.Warn("[Scheduler] 目标录制设备不在线,跳过麦克风切换");
            }
        }

        return result;
    }

    /// <summary>配置被用户修改后调用:立即按当前状态重新调度一次</summary>
    public void OnMappingChanged()
    {
        RecomputeAndApply();
        StateChanged?.Invoke();
    }

    private StandRuntime GetOrCreate(ushort standId)
    {
        if (!_stands.TryGetValue(standId, out var rt))
        {
            rt = new StandRuntime { StandId = standId };
            _stands[standId] = rt;
        }
        return rt;
    }

    /// <summary>设备 ID 很长,日志里只显示末段</summary>
    private static string LastSegment(string? id)
        => string.IsNullOrEmpty(id) ? "?" : id[^Math.Min(12, id.Length)..];
}
