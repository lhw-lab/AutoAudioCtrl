using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// 支架传输层聚合器:把多个传输(HID 的 StandHidService + 串口的 StandSerialService)
/// 合并成一个面向 MainWindow 的统一表面——事件合并上报、按路径路由下行帧。
/// 两种传输可同时在线(新 C3 串口支架与旧 STM32 HID 支架混插混用)。
/// </summary>
public class StandTransportHub : IDisposable
{
    private readonly List<IStandTransport> _transports = new();

    /// <summary>devicePath → 所属传输(RefreshConnections 时重建,SendFrame 路由用)</summary>
    private readonly Dictionary<string, IStandTransport> _pathOwner = new();

    private readonly object _lock = new();

    /// <summary>收到上行帧(任一传输,devicePath + 8 字节载荷)</summary>
    public event Action<string, byte[]>? FrameReceived;

    /// <summary>某个支架断开(任一传输)</summary>
    public event Action<string>? StandRemoved;

    /// <summary>注册一个传输(只加不改;事件在此处级联订阅)</summary>
    public void Add(IStandTransport transport)
    {
        transport.FrameReceived += (path, frame) => FrameReceived?.Invoke(path, frame);
        transport.StandRemoved += path => StandRemoved?.Invoke(path);
        lock (_lock) _transports.Add(transport);
        Logger.Info($"[Hub] 已注册传输: {transport.Name}");
    }

    /// <summary>当前已连接的支架数(各传输之和)</summary>
    public int ConnectedCount
    {
        get { lock (_lock) return _transports.Sum(t => t.ConnectedCount); }
    }

    /// <summary>重扫所有传输并重建路径路由表,返回全部在线路径</summary>
    public List<string> RefreshConnections()
    {
        List<IStandTransport> snapshot;
        lock (_lock) snapshot = _transports.ToList();

        var allPaths = new List<string>();
        foreach (var t in snapshot)
        {
            try
            {
                allPaths.AddRange(t.RefreshConnections());
            }
            catch (Exception ex)
            {
                Logger.Error($"[Hub] 传输 {t.Name} 重扫失败", ex);
            }
        }

        lock (_lock)
        {
            _pathOwner.Clear();
            foreach (var t in snapshot)
                foreach (var p in t.ConnectedPaths)
                    _pathOwner[p] = t;
        }
        return allPaths;
    }

    /// <summary>按路径把下行帧路由到所属传输(HID/串口各自的封帧在传输内部完成)</summary>
    public bool SendFrame(string devicePath, byte[] frame)
    {
        IStandTransport? t;
        lock (_lock) _pathOwner.TryGetValue(devicePath, out t);
        if (t == null)
        {
            Logger.Debug($"[Hub] 下行帧无处路由: {devicePath}");
            return false;
        }
        return t.SendFrame(devicePath, frame);
    }

    /// <summary>向所有在线支架广播 QUERY</summary>
    public void QueryAll()
    {
        List<IStandTransport> snapshot;
        lock (_lock) snapshot = _transports.ToList();
        foreach (var t in snapshot) t.QueryAll();
    }

    /// <summary>断开全部传输</summary>
    public void Dispose()
    {
        List<IStandTransport> snapshot;
        lock (_lock)
        {
            snapshot = _transports.ToList();
            _transports.Clear();
            _pathOwner.Clear();
        }
        foreach (var t in snapshot) t.Dispose();
    }
}
