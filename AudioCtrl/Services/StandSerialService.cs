using System.Runtime.InteropServices;
using System.Threading;
using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// 多支架串口连接管理器(ESP32-C3 支架,协议 v2 载荷 + 串口传输层)
///
/// 职责与 StandHidService 对等(实现同一 IStandTransport):
///   - 枚举:ComPortEnumerator 按 VID 0x303A 预过滤(避开蓝牙口/调制解调器,
///     也避开开端口可能产生的副作用),再对候选口做协议握手探测
///   - 探测:打开端口(置 DTR)→ 发 QUERY → 1.5s 内收到任一合法帧即确认为支架;
///     无应答(桌面上别人的 ESP32 开发板/端口被占)则静默关闭
///   - 读:单句柄 + SetCommTimeouts 的阻塞读线程(串口可用通信超时实现
///     非轮询式读取,无需 HID 那套 overlapped 结构;字节流经 SerialFrameCodec
///     按 magic 重同步,CRC 过滤串口噪音)
///   - 写:WriteFile 后必须 FlushFileBuffers——usbser.sys 会缓冲小写入不立即
///     推到 USB 线上,ACK 不强刷会输给固件的 300ms 重传(实测教训)
///
/// devicePath 使用端口名本身("COM8")。
/// </summary>
public class StandSerialService : IStandTransport
{
    /// <summary>Espressif USB VID(C3 的 USB Serial/JTAG 固定 PID 0x1001,不可改)</summary>
    public const ushort EspressifVid = 0x303A;

    /// <summary>传输名(日志/诊断)</summary>
    public string Name => "Serial";

    /// <summary>握手探测超时:QUERY 应答 RTT 实测 ~1ms,留足余量</summary>
    private const int ProbeTimeoutMs = 1500;

    /// <summary>portName → 连接对象</summary>
    private readonly Dictionary<string, SerialConnection> _connections = new();

    private readonly object _lock = new();

    /// <summary>烧录进行中:抑制新端口的打开与探测(esptool 需要独占端口)</summary>
    private volatile bool _flashing;

    /// <summary>服务正在 Dispose:关闭时不向上层发 StandRemoved(窗口/调度器也在关闭)</summary>
    private volatile bool _disposing;

    /// <summary>最近一次"未知设备"上报时刻(端口级节流,避免每次重扫都重复上报)</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _unknownSeenAt = new();

    /// <summary>收到上行帧(portName, 8 字节载荷)</summary>
    public event Action<string, byte[]>? FrameReceived;

    /// <summary>某个已确认的支架断开(portName)——拔线或读异常</summary>
    public event Action<string>? StandRemoved;

    /// <summary>
    /// 观察到 VID 匹配但协议无应答的端口(很可能是未烧固件的新 C3 板;
    /// 也可能是桌面上别人的 ESP32 开发板)。同一端口 30s 内只上报一次。
    /// 烧录对话框据此预选端口。
    /// </summary>
    public event Action<string>? UnknownDeviceSeen;

    /// <summary>进入/退出烧录模式:期间不再打开新端口(已连接的照常收发)</summary>
    public void SetFlashing(bool flashing) { _flashing = flashing; }

    /// <summary>
    /// 主动断开指定端口(烧录前释放占用)。
    /// 已确认身份的支架会触发 StandRemoved(调度器按离线处理,烧完重扫即恢复)。
    /// </summary>
    public void CloseConnection(string portName)
    {
        SerialConnection? conn;
        lock (_lock)
        {
            if (_connections.TryGetValue(portName, out conn))
                _connections.Remove(portName);
        }
        if (conn == null) return;
        if (conn.Confirmed)
            Logger.Info($"串口连接已释放(烧录准备): {portName}");
        // Close() 内会通过 Closed → OnConnectionClosed 触发一次 StandRemoved。
        conn.Close();
    }

    public int ConnectedCount { get { lock (_lock) return _connections.Count; } }

    public List<string> ConnectedPaths { get { lock (_lock) return _connections.Keys.ToList(); } }

    /// <summary>
    /// 枚举 VID 匹配的在线串口并做增量连接:
    /// 新端口打开并进入握手探测(探测结果异步出来,无应答的自动关闭),
    /// 已消失的端口断开清理。
    /// </summary>
    public List<string> RefreshConnections()
    {
        List<ComPortEnumerator.ComPortInfo> ports;
        try
        {
            ports = ComPortEnumerator.Enumerate(EspressifVid);
        }
        catch (Exception ex)
        {
            Logger.Error("枚举串口设备时出错", ex);
            ports = new();
        }

        var toClose = new List<(string Port, SerialConnection Conn)>();
        List<string> result;

        lock (_lock)
        {
            // 1. 新出现的端口:打开 + 探测(烧录进行中则跳过,把端口让给 esptool)
            foreach (var p in ports)
            {
                if (_connections.ContainsKey(p.PortName)) continue;
                if (_flashing)
                {
                    Logger.Debug($"[Serial] 烧录进行中,跳过端口 {p.PortName}");
                    continue;
                }
                Logger.Debug($"[Serial] 候选端口 {p.PortName} ({p.InstanceId}),开始探测");
                var conn = new SerialConnection(p.PortName);
                conn.FrameReceived += f => FrameReceived?.Invoke(p.PortName, f);
                conn.Closed += () => OnConnectionClosed(p.PortName, conn);
                if (conn.Open())
                {
                    _connections[p.PortName] = conn;
                    Logger.Info($"串口端口已打开(探测中): {p.PortName}");
                }
            }

            // 2. 已消失的端口:先摘出表,关闭动作放到锁外(见下)
            var present = ports.Select(p => p.PortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var port in _connections.Keys.ToList())
            {
                if (present.Contains(port)) continue;
                var conn = _connections[port];
                _connections.Remove(port);
                toClose.Add((port, conn));
            }

            result = _connections.Keys.ToList();
        }

        // Close() 会 Join 读线程(最长 1s),绝不能在 _lock 内做;也不能阻塞 UI 线程。
        // 关完后由 Closed → OnConnectionClosed 统一触发 StandRemoved(只发一次)。
        foreach (var (port, conn) in toClose)
        {
            var c = conn;
            _ = Task.Run(() =>
            {
                try { c.Close(); }
                catch (Exception ex) { Logger.Error($"[Serial] 关闭串口连接异常: {port}", ex); }
            });
        }

        return result;
    }

    /// <summary>连接结束回调(读线程发现断开/探测超时/端口异常)</summary>
    private void OnConnectionClosed(string port, SerialConnection conn)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(port, out var cur) && ReferenceEquals(cur, conn))
                _connections.Remove(port);
        }
        if (_disposing) return;
        if (conn.Confirmed)
        {
            Logger.Info($"串口支架连接失效: {port}");
            StandRemoved?.Invoke(port);
        }
        else
        {
            // 协议无应答:大概率是未烧固件的新 C3 板(上报一次供烧录对话框预选;
            // 30s 端口级节流,避免每次热插拔重扫都刷屏)
            Logger.Debug($"[Serial] {port} 探测无应答,已关闭(非支架固件或端口被占用)");
            long now = Environment.TickCount64;
            if (!_unknownSeenAt.TryGetValue(port, out long last) || now - last > 30_000)
            {
                _unknownSeenAt[port] = now;
                Logger.Info($"[Serial] 发现未识别的 Espressif 设备: {port}(未烧固件的新板?)");
                UnknownDeviceSeen?.Invoke(port);
            }
        }
    }

    /// <summary>向指定支架发送 8 字节下行帧(内部封 magic+CRC)</summary>
    public bool SendFrame(string devicePath, byte[] frame)
    {
        SerialConnection? conn;
        lock (_lock) _connections.TryGetValue(devicePath, out conn);
        return conn?.WriteFrame(frame) ?? false;
    }

    /// <summary>向所有在线端口发送 QUERY(既是身份查询,也是未确认端口的探测帧)</summary>
    public void QueryAll()
    {
        var q = HidProtocol.BuildQuery();
        foreach (var port in ConnectedPaths)
        {
            SendFrame(port, q);
        }
    }

    public void Dispose()
    {
        _disposing = true;
        List<SerialConnection> conns;
        lock (_lock)
        {
            conns = _connections.Values.ToList();
            _connections.Clear();
        }
        foreach (var c in conns) c.Close();
    }

    // =====================================================================
    // 单个串口连接:阻塞读线程 + 同步写
    // =====================================================================
    private class SerialConnection
    {
        private readonly string _port;
        private IntPtr _handle = Win32.INVALID_HANDLE_VALUE;
        private Thread? _readThread;
        private CancellationTokenSource? _cts;
        private readonly object _writeLock = new();
        private readonly object _stateLock = new();
        private long _openedAt;
        private int _closedNotified;

        /// <summary>收到完整上行帧(8 字节载荷,已过 CRC;读线程回调)</summary>
        public event Action<byte[]>? FrameReceived;

        /// <summary>连接结束(无论是否确认过身份;读线程或 Close 触发)</summary>
        public event Action? Closed;

        /// <summary>是否已通过协议握手确认是支架(收到过任一合法帧)</summary>
        public bool Confirmed { get; private set; }

        public SerialConnection(string port) { _port = port; }

        /// <summary>
        /// 打开端口并启动读线程,随后立即发 QUERY 探测。
        /// CDC 的波特率/校验位无意义(USB 直通),只需通信超时和 DTR。
        /// </summary>
        public bool Open()
        {
            // COM10 及以上必须 \\.\ 前缀长命名
            _handle = Win32.CreateFile(
                $"\\\\.\\{_port}",
                Win32.GENERIC_READ | Win32.GENERIC_WRITE,
                0,                                    // 串口独占(被占用则打开失败,正好跳过)
                IntPtr.Zero,
                Win32.OPEN_EXISTING,
                0,                                    // 同步句柄(靠通信超时做非阻塞读)
                IntPtr.Zero);

            if (_handle == Win32.INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Debug($"打开串口失败: {_port} (错误码 {err})");
                return false;
            }

            // 读:字节间隔 20ms 或总 50ms 即返回(拿到部分数据也返回);
            // 写:2s 总超时,防拔线时 WriteFile 长时间挂住 ACK 线程
            var timeouts = new Win32.COMMTIMEOUTS
            {
                ReadIntervalTimeout = 20,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = 50,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 2000,
            };
            if (!Win32.SetCommTimeouts(_handle, ref timeouts) ||
                !Win32.EscapeCommFunction(_handle, Win32.SETDTR))   // 置 DTR = 主机已连接(固件据此发 banner)
            {
                Logger.Error($"配置串口失败: {_port}", null);
                CleanupHandles();
                return false;
            }
            Win32.PurgeComm(_handle, Win32.PURGE_ALL);   // 清掉打开前驱动缓冲的杂散字节

            _openedAt = Environment.TickCount64;
            _cts = new CancellationTokenSource();
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"StandSerial-{_port}" };
            _readThread.Start();

            WriteFrame(HidProtocol.BuildQuery());        // 探测帧:是支架则毫秒级应答 INFO
            return true;
        }

        /// <summary>写一帧(magic+载荷+CRC),写完强刷把数据推到 USB 线上</summary>
        public bool WriteFrame(byte[] payload)
        {
            IntPtr h;
            lock (_stateLock) h = _handle;
            if (h == Win32.INVALID_HANDLE_VALUE || payload.Length != HidProtocol.FrameSize)
                return false;

            var frame = SerialFrameCodec.Wrap(payload);
            lock (_writeLock)
            {
                uint written = 0;
                if (!Win32.WriteFile(h, frame, (uint)frame.Length, ref written, IntPtr.Zero))
                {
                    Logger.Debug($"写串口帧失败: {_port} (错误码 {Marshal.GetLastWin32Error()})");
                    return false;
                }
                if (written != (uint)frame.Length)
                {
                    Logger.Debug($"写串口帧只写入 {written}/{frame.Length} 字节: {_port};固件会按 300ms 重传兜底");
                    return false;
                }
                // 关键:usbser.sys 会缓冲小写入,不 flush 的话 ACK 会输给固件 300ms 重传
                if (!Win32.FlushFileBuffers(h))
                {
                    Logger.Debug($"串口 flush 失败: {_port} (错误码 {Marshal.GetLastWin32Error()})");
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 读循环:阻塞 ReadFile(受通信超时约束)→ 字节流喂给帧解析器 →
        /// 完整帧回调。读失败(拔线)或探测超时则结束连接。
        /// </summary>
        private void ReadLoop()
        {
            var ct = _cts!.Token;
            var parser = new SerialFrameCodec.StreamParser();
            var buf = new byte[64];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    uint got = 0;
                    if (!Win32.ReadFile(_handle, buf, (uint)buf.Length, ref got, IntPtr.Zero))
                    {
                        End();   // 读失败:设备拔出/端口失效
                        return;
                    }

                    if (got > 0)
                    {
                        foreach (var payload in parser.Feed(buf, (int)got))
                        {
                            lock (_stateLock)
                            {
                                if (!Confirmed)
                                {
                                    Confirmed = true;
                                    Logger.Info($"串口支架已确认(协议握手通过): {_port}");
                                }
                            }
                            FrameReceived?.Invoke(payload);
                        }
                    }

                    if (!Confirmed && Environment.TickCount64 - _openedAt > ProbeTimeoutMs)
                    {
                        End();   // 探测超时:不是自家固件,静默关闭
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"串口读取循环异常: {_port}", ex);
                End();
            }
        }

        /// <summary>读线程内部结束:清理句柄并通知服务层(不能 Join 自己)</summary>
        private void End()
        {
            CleanupHandles();
            NotifyClosedOnce();
        }

        /// <summary>外部主动关闭(拔出/程序退出):停读线程再关句柄</summary>
        public void Close()
        {
            _cts?.Cancel();
            if (_handle != Win32.INVALID_HANDLE_VALUE)
                Win32.CancelIoEx(_handle, IntPtr.Zero);
            try { _readThread?.Join(1000); } catch { }
            CleanupHandles();
            NotifyClosedOnce();
        }

        /// <summary>
        /// Closed 事件只发一次:读线程 End() 与外部 Close() 都可能走到这里,
        /// 重复通知会让 StandRemoved/离线日志发两遍。
        /// </summary>
        private void NotifyClosedOnce()
        {
            if (Interlocked.Exchange(ref _closedNotified, 1) == 0)
                Closed?.Invoke();
        }

        private void CleanupHandles()
        {
            lock (_stateLock)
            {
                if (_handle != Win32.INVALID_HANDLE_VALUE)
                {
                    try { Win32.CloseHandle(_handle); } catch { }
                    _handle = Win32.INVALID_HANDLE_VALUE;
                }
            }
        }
    }

    /// <summary>Win32 API P/Invoke(串口所需的最小集合)</summary>
    private static class Win32
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint OPEN_EXISTING = 3;
        public const int SETDTR = 5;                       // EscapeCommFunction:置 DTR
        public const uint PURGE_ALL = 0x0000000F;          // 收发双向中止+清缓冲
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            ref uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            ref uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushFileBuffers(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetCommTimeouts(IntPtr hFile, ref COMMTIMEOUTS lpCommTimeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool EscapeCommFunction(IntPtr hFile, int dwFunc);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool PurgeComm(IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);
    }
}
