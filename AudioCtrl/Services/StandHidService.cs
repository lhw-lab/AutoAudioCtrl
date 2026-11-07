using System.Runtime.InteropServices;
using System.Threading;
using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// 多支架 HID 连接管理器(协议 v2)
/// 管理所有已连接的耳机支架(VID:0x0483 PID:0x572B):
///   - 每个支架一条独立连接(读线程 + 写句柄)
///   - 枚举/热插拔:RefreshConnections() 做增量 diff,新增的连接、消失的断开
///   - 收到 8 字节上行帧时触发 FrameReceived(devicePath, frame)
/// 读路径沿用原 HidService 的 overlapped ReadFile 模式(.NET 8 验证可用);
/// 写路径用独立同步句柄 WriteFile(下行帧少且小,无需 overlapped)。
/// </summary>
public class StandHidService : IDisposable
{
    /// <summary>支架固件的 VID(STMicroelectronics)</summary>
    public const int StandVid = 0x0483;

    /// <summary>支架固件的 PID(自定义 HID)</summary>
    public const int StandPid = 0x572B;

    /// <summary>devicePath → 连接对象</summary>
    private readonly Dictionary<string, StandConnection> _connections = new();

    /// <summary>保护连接表的锁(读线程回调/热插拔/UI 都会碰)</summary>
    private readonly object _lock = new();

    /// <summary>收到上行帧(devicePath, 8字节载荷)</summary>
    public event Action<string, byte[]>? FrameReceived;

    /// <summary>某个支架断开(devicePath)——拔线或读异常</summary>
    public event Action<string>? StandRemoved;

    /// <summary>当前连接的支架数</summary>
    public int ConnectedCount { get { lock (_lock) return _connections.Count; } }

    /// <summary>当前连接的所有设备路径(快照)</summary>
    public List<string> ConnectedPaths { get { lock (_lock) return _connections.Keys.ToList(); } }

    /// <summary>
    /// 枚举系统中的支架并做增量连接:
    /// 新出现的路径建立连接;已消失的路径断开清理。
    /// 返回当前在线的设备路径列表。
    /// </summary>
    public List<string> RefreshConnections()
    {
        var foundPaths = new List<string>();
        try
        {
            // v0.13.0: 自写 SetupAPI 枚举（DIGCF_PRESENT 保证返回的都已在线，无需再查 IsConnected）
            foundPaths = HidDeviceEnumerator.EnumeratePaths(StandVid, StandPid);
        }
        catch (Exception ex)
        {
            Logger.Error("枚举支架 HID 设备时出错", ex);
        }

        lock (_lock)
        {
            // 1. 连接新出现的设备
            foreach (var path in foundPaths)
            {
                if (_connections.ContainsKey(path)) continue;
                var conn = new StandConnection(path);
                conn.FrameReceived += f => FrameReceived?.Invoke(path, f);
                conn.Disconnected += () => OnConnectionDead(path);
                if (conn.Open())
                {
                    _connections[path] = conn;
                    Logger.Info($"支架已连接: {path}");
                }
            }

            // 2. 断开已消失的设备
            foreach (var path in _connections.Keys.ToList())
            {
                if (!foundPaths.Contains(path))
                {
                    var conn = _connections[path];
                    _connections.Remove(path);
                    conn.Close();
                    Logger.Info($"支架已拔出,断开: {path}");
                }
            }

            return _connections.Keys.ToList();
        }
    }

    /// <summary>连接死亡回调(读线程发现设备断开)</summary>
    private void OnConnectionDead(string path)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(path, out var conn))
            {
                _connections.Remove(path);
                conn.Close();
            }
        }
        Logger.Info($"支架连接失效: {path}");
        StandRemoved?.Invoke(path);
    }

    /// <summary>
    /// 向指定支架发送 8 字节下行帧(ACK/QUERY)
    /// WriteFile 需要 1 字节 report-id 前缀(0x00),实际写 9 字节
    /// </summary>
    public bool SendFrame(string devicePath, byte[] frame)
    {
        StandConnection? conn;
        lock (_lock) _connections.TryGetValue(devicePath, out conn);
        return conn?.WriteFrame(frame) ?? false;
    }

    /// <summary>向所有在线支架发送 QUERY(用于上线时收集身份)</summary>
    public void QueryAll()
    {
        var q = HidProtocol.BuildQuery();
        foreach (var path in ConnectedPaths)
        {
            SendFrame(path, q);
        }
    }

    /// <summary>断开全部连接</summary>
    public void Dispose()
    {
        List<StandConnection> conns;
        lock (_lock)
        {
            conns = _connections.Values.ToList();
            _connections.Clear();
        }
        foreach (var c in conns) c.Close();
    }

    // =====================================================================
    // 单个支架连接:读线程(overlapped)+ 写句柄(同步)
    // =====================================================================
    private class StandConnection
    {
        private readonly string _path;
        private IntPtr _readHandle = Win32.INVALID_HANDLE_VALUE;
        private IntPtr _writeHandle = Win32.INVALID_HANDLE_VALUE;
        private Thread? _readThread;
        private CancellationTokenSource? _cts;
        private readonly object _writeLock = new();

        /// <summary>收到上行帧(已剥离 report-id,固定 8 字节)</summary>
        public event Action<byte[]>? FrameReceived;

        /// <summary>设备断开(读线程发现)</summary>
        public event Action? Disconnected;

        public StandConnection(string path) { _path = path; }

        /// <summary>打开读写句柄并启动读线程</summary>
        public bool Open()
        {
            // 读句柄:overlapped,异步读
            _readHandle = Win32.CreateFile(
                _path,
                Win32.GENERIC_READ | Win32.GENERIC_WRITE,
                Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Win32.OPEN_EXISTING,
                Win32.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            // 写句柄:同步(同一个设备路径允许再次打开,share 已放开)
            _writeHandle = Win32.CreateFile(
                _path,
                Win32.GENERIC_READ | Win32.GENERIC_WRITE,
                Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Win32.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (_readHandle == Win32.INVALID_HANDLE_VALUE || _writeHandle == Win32.INVALID_HANDLE_VALUE)
            {
                Logger.Error($"打开支架设备失败: {_path} (读句柄 {_readHandle}, 写句柄 {_writeHandle})");
                CloseHandles();
                return false;
            }

            _cts = new CancellationTokenSource();
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"StandRead-{_path.GetHashCode()}" };
            _readThread.Start();
            return true;
        }

        /// <summary>写一帧(9 字节 = 0x00 前缀 + 8 字节帧)</summary>
        public bool WriteFrame(byte[] frame)
        {
            if (_writeHandle == Win32.INVALID_HANDLE_VALUE || frame.Length != HidProtocol.FrameSize)
                return false;

            var buf = new byte[HidProtocol.FrameSize + 1];
            buf[0] = 0x00;                     // report-id 前缀(描述符未声明 report id)
            Array.Copy(frame, 0, buf, 1, HidProtocol.FrameSize);

            lock (_writeLock)
            {
                uint written = 0;
                bool ok = Win32.WriteFile(_writeHandle, buf, (uint)buf.Length, ref written, IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Debug($"写下行帧失败,错误码 {err} (0x{err:X})");
                }
                return ok;
            }
        }

        /// <summary>读循环:overlapped ReadFile,收到 9 字节(0x00+帧)后剥离前缀回调。
        /// 关键:事件为手动复位,每次异步读取完成后必须 ResetEvent,
        /// 否则下一次等待会立即返回上一次的旧结果,导致同一帧被反复派发(复读风暴)</summary>
        private void ReadLoop()
        {
            var ct = _cts!.Token;
            var buffer = new byte[64];
            var overlapped = new NativeOverlapped();
            var readEvent = Win32.CreateEvent(IntPtr.Zero, true, false, null);
            if (readEvent == IntPtr.Zero) { Logger.Error("创建 overlapped 事件失败"); return; }
            overlapped.EventHandle = readEvent;

            try
            {
                while (!ct.IsCancellationRequested && _readHandle != Win32.INVALID_HANDLE_VALUE)
                {
                    uint bytesRead = 0;
                    bool result = Win32.ReadFile(_readHandle, buffer, (uint)buffer.Length, ref bytesRead, ref overlapped);

                    if (result)
                    {
                        // 同步完成(数据已在缓冲区),直接派发
                        if (bytesRead > 1) Dispatch(buffer, bytesRead);
                        continue;
                    }

                    int error = Marshal.GetLastWin32Error();
                    if (error != Win32.ERROR_IO_PENDING)
                    {
                        if (error == Win32.ERROR_DEVICE_NOT_CONNECTED)
                        {
                            Logger.Info("支架设备已断开,停止监听");
                            Disconnected?.Invoke();
                            break;
                        }
                        Logger.Debug($"ReadFile 失败,错误码: {error} (0x{error:X})");
                        Thread.Sleep(100);
                        continue;
                    }

                    // ===== 异步读已挂起:等待【这一条】读完成 =====
                    // ★ 关键修正:等待超时后绝不能重新发起 ReadFile——
                    //   上一次读仍占着同一个 OVERLAPPED 结构,复用未决 I/O 的 OVERLAPPED
                    //   属未定义行为(实测表现为:旧帧重复派发 + 读路径卡死后新帧全部丢失,
                    //   即两次"复读风暴"的真正根因,Step 13 的 ResetEvent 只治了表象)。
                    //   协议无心跳,空闲是常态,超时后应继续等待同一条未决读。
                    bool pending = true;
                    while (pending && !ct.IsCancellationRequested)
                    {
                        uint waitResult = Win32.WaitForSingleObject(readEvent, 2000);
                        if (waitResult == Win32.WAIT_OBJECT_0)
                        {
                            // 本次 I/O 完成:取结果,复位事件,回到外层循环发下一条读
                            bool getResult = Win32.GetOverlappedResult(_readHandle, ref overlapped, ref bytesRead, false);
                            Win32.ResetEvent(readEvent);
                            if (!getResult)
                            {
                                int err2 = Marshal.GetLastWin32Error();
                                if (err2 == Win32.ERROR_DEVICE_NOT_CONNECTED)
                                {
                                    Logger.Info("支架设备已断开,停止监听");
                                    Disconnected?.Invoke();
                                    return;
                                }
                                Logger.Debug($"GetOverlappedResult 失败,错误码: {err2}");
                            }
                            else if (bytesRead > 1)
                            {
                                Dispatch(buffer, bytesRead);
                            }
                            pending = false;
                        }
                        // 等待超时:检查句柄是否还有效,然后继续等(不发新 ReadFile)
                        else if (_readHandle == Win32.INVALID_HANDLE_VALUE)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("支架读取循环异常", ex);
            }
            finally
            {
                if (readEvent != IntPtr.Zero) Win32.CloseHandle(readEvent);
            }
        }

        /// <summary>剥离 report-id 前缀,规范成 8 字节帧后回调</summary>
        private void Dispatch(byte[] buffer, uint bytesRead)
        {
            var data = new byte[bytesRead - 1];
            Array.Copy(buffer, 1, data, 0, bytesRead - 1);
            FrameReceived?.Invoke(data);
        }

        /// <summary>停止读线程并关闭句柄</summary>
        public void Close()
        {
            _cts?.Cancel();
            // 取消挂起的异步读,让读线程从等待中及时退出
            if (_readHandle != Win32.INVALID_HANDLE_VALUE)
                Win32.CancelIoEx(_readHandle, IntPtr.Zero);
            try { _readThread?.Join(2000); } catch { }
            CloseHandles();
        }

        private void CloseHandles()
        {
            if (_readHandle != Win32.INVALID_HANDLE_VALUE)
            {
                try { Win32.CloseHandle(_readHandle); } catch { }
                _readHandle = Win32.INVALID_HANDLE_VALUE;
            }
            if (_writeHandle != Win32.INVALID_HANDLE_VALUE)
            {
                try { Win32.CloseHandle(_writeHandle); } catch { }
                _writeHandle = Win32.INVALID_HANDLE_VALUE;
            }
        }
    }

    /// <summary>Win32 API P/Invoke 声明(与原 HidService 相同的一套)</summary>
    private static class Win32
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
        public const uint WAIT_OBJECT_0 = 0;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            ref uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            ref uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(
            IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(
            IntPtr hFile, ref NativeOverlapped lpOverlapped,
            ref uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll")]
        public static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);
    }
}
