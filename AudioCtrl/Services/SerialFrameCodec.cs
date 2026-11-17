namespace AudioCtrl.Services;

/// <summary>
/// 串口传输层帧编解码(ESP32-C3 支架,协议 v2 载荷的串口承载)
///
/// 帧格式(定长 11 字节): magic(0xA5 0x5A) + 8 字节载荷 + CRC8
///   - 载荷 = HID 协议 v2 的 8 字节帧原样(见 HidProtocol / PROTOCOL.md),
///     串口只是传输层,C3 固件与上位机调度器共用同一套载荷语义
///   - CRC8: 多项式 0x07,初值 0,MSB 先行(与固件 crc8() 和 tools 脚本一致)
/// 字节流按 magic 重同步:非帧字节(如设备日志)在解析中被丢弃。
/// </summary>
public static class SerialFrameCodec
{
    public const byte Magic0 = 0xA5;
    public const byte Magic1 = 0x5A;
    public const int PayloadSize = HidProtocol.FrameSize;   // 8
    public const int FrameSize = 2 + PayloadSize + 1;       // 11

    /// <summary>CRC-8(多项式 0x07,初值 0,MSB 先行)</summary>
    public static byte Crc8(byte[] data, int offset, int count)
    {
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c ^= data[offset + i];
            for (int b = 0; b < 8; b++)
                c = (byte)((c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1);
        }
        return c;
    }

    /// <summary>把 8 字节协议载荷封成 11 字节串口帧</summary>
    public static byte[] Wrap(byte[] payload)
    {
        var f = new byte[FrameSize];
        f[0] = Magic0;
        f[1] = Magic1;
        Array.Copy(payload, 0, f, 2, PayloadSize);
        f[FrameSize - 1] = Crc8(f, 2, PayloadSize);
        return f;
    }

    /// <summary>
    /// 字节流帧解析器:feed 任意到达的字节,吐出完整的 8 字节载荷。
    /// 按 magic 找帧头,CRC 失败丢弃该帧头继续找(容错串口噪音);
    /// 保留末尾 1 字节防止帧头跨包被吞。
    /// </summary>
    public class StreamParser
    {
        private readonly byte[] _buf = new byte[256];
        private int _len;

        /// <summary>喂入新到的字节,返回本次解析出的载荷列表(可能为空)</summary>
        public List<byte[]> Feed(byte[] data, int count)
        {
            var payloads = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                if (_len >= _buf.Length) _len = 0;   // 防御:超长垃圾直接清空
                _buf[_len++] = data[i];

                if (_len < FrameSize) continue;

                // 缓冲区尾部是否是一个完整帧
                int s = _len - FrameSize;
                if (_buf[s] == Magic0 && _buf[s + 1] == Magic1 &&
                    Crc8(_buf, s + 2, PayloadSize) == _buf[s + 2 + PayloadSize])
                {
                    var payload = new byte[PayloadSize];
                    Array.Copy(_buf, s + 2, payload, 0, PayloadSize);
                    payloads.Add(payload);
                    _len = 0;   // 帧对齐后从头开始(正常情况下帧本就连续到达)
                }
            }
            return payloads;
        }
    }
}
