#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PickUp Audio C3 支架台架验证脚本

用法: python stand_probe.py [COM口] [监听秒数]
默认 COM8 / 30 秒。

验证内容(对应 v2 方案的主机侧行为):
  1. 打开端口(置 DTR) → 设备应立即回 INFO banner(含 standId/固件版本/传感器状态)
  2. 收到 EVENT 自动回 ACK(结果码 0x00) —— 走完"事件→确认"闭环
  3. 台架测试: 按板上 BOOT 键可触发拿起/放回事件(无需接微动开关)

依赖: pyserial(随 esptool 一并安装)
"""
import sys
import time

import serial  # noqa: E402

MAGIC = b"\xA5\x5A"
PAYLOAD_LEN = 8
FRAME_LEN = 2 + PAYLOAD_LEN + 1


def crc8(data: bytes) -> int:
    """CRC-8,多项式 0x07,初值 0,MSB 先行 —— 与固件 crc8() 一致"""
    c = 0
    for b in data:
        c ^= b
        for _ in range(8):
            c = ((c << 1) ^ 0x07) & 0xFF if c & 0x80 else (c << 1) & 0xFF
    return c


def build(payload: bytes) -> bytes:
    return MAGIC + bytes(payload) + bytes([crc8(payload)])


class FrameParser:
    """字节流按 magic 重同步的帧解析(与上位机未来 StandSerialService 同思路)"""

    def __init__(self):
        self.buf = b""

    def feed(self, data: bytes):
        self.buf += data
        out = []
        while True:
            i = self.buf.find(MAGIC)
            if i < 0:
                self.buf = self.buf[-1:]  # 保留末字节,防帧头跨包
                break
            if len(self.buf) - i < FRAME_LEN:
                self.buf = self.buf[i:]
                break
            frame = self.buf[i : i + FRAME_LEN]
            payload, crc = frame[2 : 2 + PAYLOAD_LEN], frame[-1]
            if crc8(payload) == crc:
                out.append(payload)
                self.buf = self.buf[i + FRAME_LEN :]
            else:
                # CRC 失败:丢弃这个帧头,从下一字节继续找(容错串口噪音)
                self.buf = self.buf[i + 2 :]
        return out


def describe(p: bytes) -> str:
    t = p[0]
    if t == 0x01:  # EVENT
        evt = "LIFT(拿起)" if p[2] == 1 else "PLACE(放回)"
        return f"EVENT seq={p[1]} {evt} standId=0x{p[3] | p[4] << 8:04X}"
    if t == 0x04:  # INFO
        sid, fw = p[3] | p[4] << 8, p[2]
        st = "lifted(拿起)" if p[5] == 1 else "placed(在架上)"
        return f"INFO standId=0x{sid:04X} fw=v{fw >> 4}.{fw & 0xF} sensor={st}"
    return "UNKNOWN " + " ".join(f"{b:02X}" for b in p)


def main():
    port = sys.argv[1] if len(sys.argv) > 1 else "COM8"
    dur = float(sys.argv[2]) if len(sys.argv) > 2 else 30.0

    parser = FrameParser()
    t0 = time.time()
    last_query = 0.0   # 周期性 QUERY:双向链路心跳,应答为 INFO(含当前传感器状态)
    infos = events = acks = reconnects = 0
    ser = None
    print(f"[probe] {port},监听 {dur:.0f}s —— 按板上 BOOT 键触发事件,RST/断线自动重连")
    try:
        while time.time() - t0 < dur:
            ts = time.time() - t0
            # ---- 端口未开/断开:自动重连(模拟上位机热插拔恢复) ----
            if ser is None:
                try:
                    ser = serial.Serial(port, 115200, timeout=0.1)
                    last_query = ts
                    tag = f"(第 {reconnects} 次恢复)" if reconnects else ""
                    print(f"[{ts:6.2f}s] ⚡ 端口已连接 {tag}")
                except serial.SerialException:
                    time.sleep(1.0)
                continue
            try:
                if ts - last_query >= 5.0:
                    last_query = ts
                    ser.write(build([0x03] + [0] * 7))   # QUERY 帧
                    print(f"[{ts:6.2f}s] ▶ QUERY")
                n = max(ser.in_waiting, 1)
                for p in parser.feed(ser.read(n)):
                    ts = time.time() - t0
                    print(f"[{ts:6.2f}s] ◀ {describe(p)}   raw={p.hex(' ')}")
                    if p[0] == 0x04:
                        infos += 1
                    if p[0] == 0x01:  # EVENT → 回 ACK(seq, OK=0x00),闭环
                        ser.write(build([0x01, p[1], 0x00] + [0] * 5))
                        acks += 1
                        events += 1
                        print(f"[{ts:6.2f}s] ▶ ACK seq={p[1]} result=OK")
            except serial.SerialException as e:
                print(f"[{ts:6.2f}s] ⚠ 端口断开({e.__class__.__name__}),1s 后重连…")
                try:
                    ser.close()
                except Exception:
                    pass
                ser = None
                reconnects += 1
    except KeyboardInterrupt:
        print("\n[probe] 手动停止")
    finally:
        if ser is not None:
            try:
                ser.close()
            except Exception:
                pass

    print(f"[probe] 结束: INFO {infos} / EVENT {events} / ACK {acks} / 断连恢复 {reconnects} 次")
    if infos and events and acks:
        print("[probe] ✔ 三项全部通过:握手 banner、事件上报、ACK 闭环")
    else:
        print("[probe] ✘ 未全部通过,检查上面的输出")


if __name__ == "__main__":
    main()
