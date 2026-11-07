namespace AudioCtrl.Services;

/// <summary>
/// HID 协议 v2 编解码
/// 帧固定 8 字节:
///   上行(支架→电脑): [0]=类型 [1]=seq [2]=事件 [3..4]=支架ID(小端) [5..7]=预留
///   下行(电脑→支架): [0]=类型 [1]=ack_seq [2]=结果 [3..7]=预留
/// </summary>
public static class HidProtocol
{
    /// <summary>帧长度(字节)</summary>
    public const int FrameSize = 8;

    // ---- 上行消息类型 ----
    public const byte UpEvent = 0x01;   // 拿起/放回事件(需 ACK,带重传)
    public const byte UpInfo = 0x04;    // 应答 QUERY 的自报身份帧

    // ---- 下行消息类型 ----
    public const byte DnAck = 0x01;     // 事件确认
    public const byte DnQuery = 0x03;   // 身份/状态查询

    // ---- 事件值 ----
    public const byte EvLift = 0x01;    // 耳机拿起
    public const byte EvPlace = 0x02;   // 耳机放回

    // ---- ACK 结果码 ----
    public const byte AckOk = 0x00;             // 切换成功
    public const byte AckDeviceMissing = 0x01;  // 目标音频设备未连接
    public const byte AckNoMapping = 0x02;      // 该支架未配置设备映射
    public const byte AckError = 0x03;          // 执行异常

    /// <summary>构造 ACK 帧</summary>
    public static byte[] BuildAck(byte seq, byte result)
    {
        return new byte[FrameSize] { DnAck, seq, result, 0, 0, 0, 0, 0 };
    }

    /// <summary>构造 QUERY 帧(连接后询问支架身份)</summary>
    public static byte[] BuildQuery()
    {
        return new byte[FrameSize] { DnQuery, 0, 0, 0, 0, 0, 0, 0 };
    }

    /// <summary>
    /// 解析上行的 EVENT 帧
    /// </summary>
    /// <returns>是 EVENT 帧则返回 true,并输出序号/事件/支架ID</returns>
    public static bool TryParseEvent(byte[] frame, out byte seq, out byte evt, out ushort standId)
    {
        seq = 0; evt = 0; standId = 0;
        if (frame is null || frame.Length < FrameSize || frame[0] != UpEvent) return false;
        seq = frame[1];
        evt = frame[2];
        standId = (ushort)(frame[3] | (frame[4] << 8));
        return true;
    }

    /// <summary>
    /// 解析上行的 INFO 帧(支架对 QUERY 的应答)
    /// </summary>
    public static bool TryParseInfo(byte[] frame, out ushort standId, out byte fwVersion, out byte sensorLifted)
    {
        standId = 0; fwVersion = 0; sensorLifted = 0;
        if (frame is null || frame.Length < FrameSize || frame[0] != UpInfo) return false;
        fwVersion = frame[2];
        standId = (ushort)(frame[3] | (frame[4] << 8));
        sensorLifted = frame[5];
        return true;
    }
}
