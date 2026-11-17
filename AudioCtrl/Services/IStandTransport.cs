namespace AudioCtrl.Services;

/// <summary>
/// 支架传输层接口:与具体总线(HID / USB 串口)解耦。
/// 传输层只负责"发现设备 → 收发 8 字节协议帧",不解释帧内容——
/// 帧编解码(HidProtocol)与调度(StandScheduler)对两种传输完全共用。
///
/// devicePath 为传输内的唯一标识(HID 为设备接口路径,串口为 "COM8" 这样的端口名),
/// 上层(调度器/UI)一律当不透明字符串使用。
/// </summary>
public interface IStandTransport : IDisposable
{
    /// <summary>传输名称(日志/诊断用,如 "HID" / "Serial")</summary>
    string Name { get; }

    /// <summary>收到上行帧(读线程回调;devicePath + 8 字节协议载荷)</summary>
    event Action<string, byte[]>? FrameReceived;

    /// <summary>某个支架断开(devicePath)——拔线或读异常</summary>
    event Action<string>? StandRemoved;

    /// <summary>当前已连接的支架数</summary>
    int ConnectedCount { get; }

    /// <summary>当前连接的所有设备路径(快照)</summary>
    List<string> ConnectedPaths { get; }

    /// <summary>
    /// 枚举系统中的支架并做增量连接:新出现的设备建立连接,
    /// 已消失的断开清理。返回当前在线的设备路径列表。
    /// </summary>
    List<string> RefreshConnections();

    /// <summary>向指定支架发送 8 字节下行帧(ACK/QUERY/TEST)</summary>
    bool SendFrame(string devicePath, byte[] frame);

    /// <summary>向所有在线支架发送 QUERY(用于上线时收集身份)</summary>
    void QueryAll();
}
