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
    private readonly IAudioDeviceService _audio;
    private readonly StandSettingsService _settings;

    /// <summary>standId → 运行时状态(含已离线但见过历史的支架)</summary>
    private readonly Dictionary<ushort, StandRuntime> _stands = new();

    private readonly object _lock = new();

    /// <summary>状态变化(UI 需要刷新支架列表时触发,可能在读线程回调)</summary>
    public event Action? StateChanged;

    public StandScheduler(IAudioDeviceService audio, StandSettingsService settings)
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
                rt.TransportKind = devicePath.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? "serial" : "hid";

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
                rt.TransportKind = devicePath.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? "serial" : "hid";
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

                // 日志分级:C3 固件的 ECHO_ON_ACK 设计会在每个事件 ACK 后回一帧 INFO,
                // 已连接期间的这类回执不该刷"上线"——只有真(重)连接才值得记 Info
                if (isNewConnection)
                {
                    Logger.Info($"[Scheduler] 支架 {infoId:X4} 上线: 固件 v{(fw >> 4)}.{fw & 0xF}, 耳机{(rt.Lifted ? "已被拿起" : "在架上")}");
                }
                else
                {
                    Logger.Debug($"[Scheduler] 支架 {infoId:X4} INFO 回执: 固件 v{(fw >> 4)}.{fw & 0xF}, 耳机{(rt.Lifted ? "已被拿起" : "在架上")}");
                }

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
        // 在线且拿起的支架中,按最近拿起优先;**未配置映射的支架跳过而非阻塞**——
        // 否则一个删了映射/忘了配置的支架耳机悬空时,会把其他支架的放回回退
        // 和外放 fallback 全部堵死(表现为"放下耳机不切外放")
        StandRuntime? active = null;
        StandMapping? activeMapping = null;
        lock (_lock)
        {
            var candidates = _stands.Values
                .Where(s => s.Online && s.Lifted)
                .OrderByDescending(s => s.LastLiftTime);
            foreach (var c in candidates)
            {
                var m = _settings.GetMapping(c.StandId);
                if (m is null || (m.LiftPlaybackId is null && m.LiftCaptureId is null))
                {
                    Logger.Warn($"[Scheduler] 支架 {c.StandIdHex} 已拿起但未配置播放/录制映射,调度时忽略");
                    continue;
                }
                active = c;
                activeMapping = m;
                break;
            }
        }

        if (active != null)
        {
            // 有(已配置映射的)耳机被拿起 → 用该支架映射的设备
            targetPlayback = activeMapping!.LiftPlaybackId;
            targetCapture = activeMapping.LiftCaptureId;
        }
        else
        {
            // 全部放回 → 用 fallback(外放);未配置则不切
            targetPlayback = _settings.Current.FallbackPlaybackId;
            targetCapture = _settings.Current.FallbackCaptureId;
            if (targetPlayback == null && targetCapture == null)
            {
                Logger.Info("[Scheduler] 全部放回,但未配置外放播放/录制设备,不切换");
                return HidProtocol.AckNoMapping;
            }
        }

        // ---- 2. 执行切换(按设备 ID,先枚举确认设备在线) ----
        var all = _audio.EnumerateDevices();

        var pb = targetPlayback != null
            ? all.FirstOrDefault(d => d.Id == targetPlayback && d.IsActive)
            : null;
        if (targetPlayback != null)
        {
            if (pb == null)
            {
                Logger.Warn($"[Scheduler] 目标播放设备不在线(ID 末段 ...{LastSegment(targetPlayback)}),放弃播放切换");
                result = HidProtocol.AckDeviceMissing;
            }
            else if (pb.IsDefault && _audio.IsDefaultForAllRoles(pb.Id, "Playback"))
            {
                /* 只有三个角色都已是默认才跳过:对同一端点重复 SetDefaultEndpoint 也会
                   触发 Windows 默认设备变更回调(按 3 个角色连发),导致假切换 Toast
                   弹个不停;但只查 Multimedia 会漏掉用户按角色分离默认设备的场景。 */
                Logger.Debug($"[Scheduler] 播放设备已是三角色默认,跳过设置: {pb.Name}");
            }
            else
            {
                _audio.SetDefaultDevice(pb.Id, "Playback");
                Logger.Info($"[Scheduler] 已切换播放设备 → {pb.Name}");
            }
        }

        // 麦克风尽力而为:失败不影响播放结果码(事件主体是播放设备)。
        // 只绑录制设备(没有播放映射)也必须执行,不能因为 targetPlayback 为空就跳过。
        if (targetCapture != null)
        {
            var cap = all.FirstOrDefault(d => d.Id == targetCapture && d.IsActive);
            if (cap != null)
            {
                if (cap.IsDefault && _audio.IsDefaultForAllRoles(cap.Id, "Recording"))
                {
                    Logger.Debug($"[Scheduler] 录制设备已是三角色默认,跳过设置: {cap.Name}");
                }
                else
                {
                    _audio.SetDefaultDevice(cap.Id, "Recording");
                    Logger.Info($"[Scheduler] 已切换录制设备 → {cap.Name}");
                }
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

    /// <summary>
    /// 删除一个支架(用户显式操作):从运行时列表移除。
    /// 若该支架当时在线且耳机处于拿起状态,按"拔线"语义重新调度(等同 HandleStandRemoved);
    /// 映射清理由调用方(StandSettingsService)负责,调度器不碰配置。
    /// 注意:在线支架被删后若再上报事件会重新出现(再次 GetOrCreate)——
    /// UI 层负责在删除前向用户说明这一点
    /// </summary>
    public void RemoveStand(ushort standId)
    {
        bool needRecompute = false;
        lock (_lock)
        {
            if (_stands.TryGetValue(standId, out var rt))
            {
                Logger.Info($"[Scheduler] 支架 {standId:X4} 已被用户删除(曾{(rt.Online ? "在线" : "离线")}" +
                            $"{(rt.Lifted ? ",耳机拿起,按放回重新调度" : "")})");
                if (rt.Online && rt.Lifted) needRecompute = true;
                _stands.Remove(standId);
            }
        }
        if (needRecompute) RecomputeAndApply();
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
