#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PickUp Audio C3 支架自动化台架测试(无需人手按板)

用法: python stand_test.py [COM口]      # 默认 COM8

测试项(全部自动执行,结束打印汇总,退出码 0=全过):
  T1 发现:打开端口后 3s 内应收到周期广播的 INFO banner(发现机制)
  T2 问答:QUERY → 应答 INFO(下行解析+上行应答,standId 一致)
  T3 重传:TEST 远程触发事件且【不 ACK】→ 应收到 EVENT×4(首发+3 重传,
          间隔约 300ms),超限后停止(视为主机离线)
  T4 确认:TEST 触发,【收到即 ACK + flush】→ 无重传;并测 ACK→回执 INFO 的 RTT
  T5 连发:连续触发+ACK,seq 单调 +1
  T6 往返:QUERY→INFO RTT 连测 3 次(量化主机→固件→主机延迟)

要点:所有下行帧写完立即 ser.flush()(FlushFileBuffers)——
usbser.sys 可能缓冲小写入不立即发到 USB 线上,ACK 不 flush 会输给 300ms 重传。
(此结论同样适用于未来上位机 StandSerialService 的写路径)
"""
import os
import sys
import time

import serial

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from stand_probe import FrameParser, build, describe  # noqa: E402

UP_EVENT, UP_INFO = 0x01, 0x04
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(f"  {'✔' if ok else '✘'} {name}" + (f" —— {detail}" if detail else ""))


def w(ser, payload):
    """写一帧并立即 flush:确保 usbser 驱动把数据推到 USB 线上"""
    ser.write(build(payload))
    ser.flush()


def read_for(ser, parser, seconds):
    """读取指定秒数,返回 [(到达时刻, payload)]"""
    out, t_end = [], time.time() + seconds
    while time.time() < t_end:
        for p in parser.feed(ser.read(max(ser.in_waiting, 1))):
            out.append((time.time(), p))
    return out


def read_until(ser, parser, want_type, timeout):
    """读到首个指定类型帧立即返回(收到即处理的真实上位机行为)"""
    t_end = time.time() + timeout
    while time.time() < t_end:
        for p in parser.feed(ser.read(max(ser.in_waiting, 1))):
            if p[0] == want_type:
                return time.time(), p
    return None, None


def main():
    port = sys.argv[1] if len(sys.argv) > 1 else "COM8"
    ser = serial.Serial(port, 115200, timeout=0.02)
    parser = FrameParser()
    print(f"[test] {port} 已打开,开始自动化测试\n")

    # ---- T1 发现:周期广播 banner ----
    print("[T1] 发现:等待周期广播 banner(≤3s)")
    frames = read_for(ser, parser, 3.0)
    banners = [p for _, p in frames if p[0] == UP_INFO]
    stand_id = (banners[0][3] | banners[0][4] << 8) if banners else None
    check("T1 打开端口后 3s 内收到 INFO banner", bool(banners),
          f"standId=0x{stand_id:04X},共 {len(banners)} 条" if banners else "未收到")

    # ---- T2 问答:QUERY → INFO ----
    print("[T2] 问答:发送 QUERY")
    w(ser, [0x03] + [0] * 7)
    frames = read_for(ser, parser, 0.5)
    infos = [p for _, p in frames if p[0] == UP_INFO]
    ok = bool(infos) and (infos[0][3] | infos[0][4] << 8) == stand_id
    check("T2 QUERY 应答 INFO 且 standId 一致", ok,
          f"{len(infos)} 条应答" if infos else "无应答")

    # ---- T3 重传:TEST 触发,故意不 ACK ----
    print("[T3] 重传:TEST 远程触发事件,故意不 ACK,观察重传后放弃")
    w(ser, [0x05, 0x01] + [0] * 6)
    frames = read_for(ser, parser, 2.5)
    evs = [(t, p) for t, p in frames if p[0] == UP_EVENT]
    seqs = sorted({p[1] for _, p in evs})
    gaps = [round(evs[i + 1][0] - evs[i][0], 3) for i in range(len(evs) - 1)]
    check("T3 收到 EVENT 且全部同 seq(重传而非新事件)", len(evs) > 0 and len(seqs) == 1,
          f"{len(evs)} 份,seq={seqs}" if evs else "未收到")
    check("T3 重传份数 = 4(首发+3 重传)", len(evs) == 4, f"实际 {len(evs)} 份")
    check("T3 重传间隔 ≈300ms", len(gaps) == 3 and all(0.2 < g < 0.45 for g in gaps),
          f"间隔 {gaps}s")
    extra = [p for _, p in read_for(ser, parser, 0.6) if p[0] == UP_EVENT]
    check("T3 超限后停止重传(放弃)", not extra, f"多余 {len(extra)} 份")

    # ---- T4 确认:TEST 触发,收到即 ACK + flush,并测 ACK→回执 RTT ----
    print("[T4] 确认:TEST 触发,收到即 ACK(+flush),观察重传与 ACK 回执 RTT")
    w(ser, [0x05, 0x01] + [0] * 6)
    t_ev, ev = read_until(ser, parser, UP_EVENT, 0.5)
    check("T4 触发后收到 EVENT", ev is not None, describe(ev) if ev else "未收到")
    if ev is not None:
        w(ser, [0x01, ev[1], 0x00] + [0] * 5)          # ACK(seq, OK),立即 flush
        t_echo, echo = read_until(ser, parser, UP_INFO, 1.0)   # 固件 ACK 回执
        rtt = (t_echo - t_ev) * 1000 if t_echo else None
        check("T4 收到 ACK 回执 INFO(下行已送达)", echo is not None,
              f"事件→回执 RTT {rtt:.0f}ms" if rtt else "无回执")
        retrans = [p for _, p in read_for(ser, parser, 0.8) if p[0] == UP_EVENT]
        check("T4 ACK 后无重传", not retrans, f"重传 {len(retrans)} 份")

    # ---- T5 连发:连续触发+ACK,seq 单调 +1 ----
    print("[T5] 连发:连续触发+ACK,验证 seq 递增")
    seqs_seen = []
    for _ in range(2):
        w(ser, [0x05, 0x01] + [0] * 6)
        t, ev = read_until(ser, parser, UP_EVENT, 0.5)
        if ev is not None:
            w(ser, [0x01, ev[1], 0x00] + [0] * 5)
            seqs_seen.append(ev[1])
            read_for(ser, parser, 0.05)   # 排掉可能夹带的回执/重传
    mono = all(b == a + 1 for a, b in zip(seqs_seen, seqs_seen[1:]))
    check("T5 两次触发 seq 单调 +1", len(seqs_seen) == 2 and mono, f"seq 序列 {seqs_seen}")

    # ---- T6 往返延迟:QUERY→INFO RTT ×3 ----
    print("[T6] 往返:QUERY→INFO RTT 连测 3 次")
    rtts = []
    for _ in range(3):
        t0 = time.time()
        w(ser, [0x03] + [0] * 7)
        t1, _ = read_until(ser, parser, UP_INFO, 0.5)
        if t1:
            rtts.append(round((t1 - t0) * 1000))
    check("T6 三次 QUERY 全部应答", len(rtts) == 3, f"RTT {rtts} ms")

    ser.close()
    passed = sum(results)
    print(f"\n[test] 汇总: {passed}/{len(results)} 项通过")
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
