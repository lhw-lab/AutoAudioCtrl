using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AudioCtrl.Helpers;
using AudioCtrl.Models;

namespace AudioCtrl.Services;

/// <summary>
/// 音频设备枚举服务
/// 使用 Windows Core Audio API（COM 互操作）枚举系统中的播放和录音设备
/// 无需第三方 NuGet 包，直接调用系统底层 API
/// </summary>
public class AudioDeviceService
{
    /// <summary>
    /// IMMDeviceEnumerator 的 COM 类 ID
    /// 用于创建 Windows 音频设备枚举器的 COM 实例
    /// </summary>
    private static readonly Guid MMDeviceEnumeratorClsid =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>
    /// 设备 friendly name 属性键
    /// 用于从 IPropertyStore 中读取设备的用户友好名称
    /// </summary>
    private static readonly PropertyKey PKEY_Device_FriendlyName = new(
        "{A45C254E-DF1C-4EFD-8020-67D146A850E0}", 14);

    /// <summary>
    /// 清除 COM 属性变体，释放其内部资源（如 LPWSTR 字符串），防泄漏
    /// </summary>
    [System.Runtime.InteropServices.DllImport("Ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>
    /// 默认录音设备变更事件（当系统在 Windows 设置中切换默认麦克风时触发）
    /// </summary>
    public event Action<string?>? DefaultRecordingDeviceChanged;

    /// <summary>
    /// 默认播放设备变更事件（当系统在 Windows 设置中切换默认扬声器时触发）
    /// </summary>
    public event Action<string?>? DefaultPlaybackDeviceChanged;

    /// <summary>
    /// 音频设备状态变化事件（设备插入/拔出/启用/禁用）
    /// 事件参数为变化的设备 ID
    /// </summary>
    public event Action? DeviceListChanged;

    /// <summary>
    /// 系统默认设备变更通知器
    /// 用于监听 Windows 系统中默认音频设备的切换
    /// </summary>
    private IMMNotificationClient? _notificationClient;

    /// <summary>
    /// 枚举系统中所有音频设备（播放 + 录音）
    /// 返回包含设备名称、类型、默认设备标记的列表
    /// </summary>
    /// <returns>音频设备信息列表</returns>
    public List<AudioDeviceInfo> EnumerateDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        // 创建 COM 音频设备枚举器实例（COM 实例不会为 null）
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid);
        var enumerator = (IMMDeviceEnumerator)(Activator.CreateInstance(type!) ??
            throw new InvalidOperationException("无法创建音频设备枚举器 COM 实例"));

        try
        {
            // 枚举所有播放设备（扬声器/耳机/显示器等）
            EnumerateByType(enumerator!, EDataFlow.eRender, "Playback", devices);

            // 枚举所有录音设备（麦克风/线路输入等）
            EnumerateByType(enumerator!, EDataFlow.eCapture, "Recording", devices);

            // 获取默认播放设备 ID
            var defaultPlaybackId = GetDefaultDeviceId(enumerator, EDataFlow.eRender);
            // 获取默认录音设备 ID
            var defaultRecordingId = GetDefaultDeviceId(enumerator, EDataFlow.eCapture);

            // 分别标记默认播放设备和默认录音设备
            foreach (var dev in devices)
            {
                dev.IsDefault = dev.Id == defaultPlaybackId || dev.Id == defaultRecordingId;
            }

            // 注册系统默认设备变更通知（仅在首次枚举时注册一次）
            if (_notificationClient == null)
            {
                RegisterDeviceNotification(enumerator);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator!);
        }

        Logger.Info($"[AudioDeviceService] 枚举到 {devices.Count} 个音频设备");
        return devices;
    }

    /// <summary>
    /// 按设备类型（播放/录音）枚举设备
    /// 遍历指定类型的所有设备并提取名称和 ID 信息
    /// </summary>
    private void EnumerateByType(
        IMMDeviceEnumerator enumerator, EDataFlow flow, string typeLabel,
        List<AudioDeviceInfo> devices)
    {
        // 获取指定类型的设备集合（只枚举活动状态的设备）
        var collection = enumerator.EnumAudioDevices(flow,
            DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged);

        try
        {
            int count = collection.GetCount();
            Logger.Info($"[AudioDeviceService] 检测到 {count} 个{typeLabel}设备");

            for (int i = 0; i < count; i++)
            {
                var device = collection.Item(i);
                try
                {
                    // 获取设备 ID
                    device.GetId(out string deviceId);

                    // 获取设备友好名称（用户可读的设备名）
                    string name = GetFriendlyName(device) ?? "未知设备";

                    // 检查设备是否处于活动状态
                    device.GetState(out int state);
                    bool isActive = state == 1; // DEVICE_STATE_ACTIVE = 1

                    devices.Add(new AudioDeviceInfo
                    {
                        Id = deviceId,
                        Name = name,
                        Type = typeLabel,
                        DevicePath = deviceId,
                        IsActive = isActive
                    });
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }
    }

    /// <summary>
    /// 获取系统默认音频设备的 ID
    /// 根据设备类型（播放/录音）分别获取
    /// </summary>
    /// <param name="enumerator">设备枚举器 COM 实例</param>
    /// <param name="flow">设备类型：eRender 播放 / eCapture 录音</param>
    /// <returns>默认设备的唯一标识符，无则返回 null</returns>
    private string? GetDefaultDeviceId(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        try
        {
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia);
            defaultDevice.GetId(out string id);
            Marshal.ReleaseComObject(defaultDevice);
            return id;
        }
        catch
        {
            // 如果系统中没有默认设备，返回 null
            return null;
        }
    }

    /// <summary>
    /// 注册系统默认音频设备变更通知
    /// 当用户在 Windows 设置中切换默认设备时，COM 会回调此接口
    /// </summary>
    private void RegisterDeviceNotification(IMMDeviceEnumerator enumerator)
    {
        try
        {
            _notificationClient = new NotificationClient(
                playbackId => DefaultPlaybackDeviceChanged?.Invoke(playbackId),
                recordingId => DefaultRecordingDeviceChanged?.Invoke(recordingId),
                () => DeviceListChanged?.Invoke());
            enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            Logger.Info("[AudioDeviceService] 已注册默认设备变更通知");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[AudioDeviceService] 注册默认设备通知失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 通过 IPropertyStore 读取设备的友好名称
    /// 使用 COM 属性存储接口获取设备的用户可读名称
    /// </summary>
    private static string? GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? propStore = null;
        try
        {
            // 激活设备的属性存储（只读模式）
            device.OpenPropertyStore(0, out propStore);
            if (propStore == null)
                return null;

            // 查询设备友好名称属性（需要本地副本用于 ref 传递）
            var key = PKEY_Device_FriendlyName;
            propStore.GetValue(ref key, out PropVariant variant);
            try
            {
                // 只接受字符串类型(VT_LPWSTR=31)，其余类型无意义直接放弃
                if (variant.vt != 31 || variant.pointerVal == IntPtr.Zero)
                    return null;
                return Marshal.PtrToStringUni(variant.pointerVal);
            }
            finally
            {
                // ★ 必须清除：COM 分配的变体(如 LPWSTR)不清除就泄漏字符串，
                // 每次枚举×每个设备都会执行本方法，长稳运行的真隐患
                PropVariantClear(ref variant);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (propStore != null)
                Marshal.ReleaseComObject(propStore);
        }
    }

    // ============================================================
    // 设置默认设备相关 COM 接口
    // IPolicyConfig 是非公开接口，用于切换 Windows 默认音频设备。
    // 方法顺序与签名由 COM 二进制接口(ABI)决定：IPolicyConfig 系列接口的
    // 12 方法 vtable 布局属于公开的 ABI 常识(EreTIk 等社区资料)，
    // 本文件按该布局自行声明接口，未复制任何第三方实现代码；
    // 思路上致谢 SoundSwitch 与 EreTIk(见 README 致谢段)
    // ============================================================

    /// <summary>
    /// IPolicyConfigX COM 接口（Windows 8+）
    /// IID: {8F9FB2AA-1C0B-4D54-B6BB-B2F2A10CE03C}
    /// vtable 包含 12 个方法，SetDefaultEndpoint 位于 slot 10
    /// </summary>
    [ComImport]
    [Guid("8F9FB2AA-1C0B-4D54-B6BB-B2F2A10CE03C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigX
    {
        [PreserveSig]
        int GetMixFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bDefault,
            [In] IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pEndpointFormat,
            [In] IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bDefault,
            [In] IntPtr pmftDefaultPeriod,
            [In] IntPtr pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pMode);

        [PreserveSig]
        int SetShareMode([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            [In] IntPtr key,
            [In] IntPtr pv);

        [PreserveSig]
        int SetPropertyValue([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            [In] IntPtr key,
            [In] IntPtr pv);

        /// <summary>设置指定设备为默认设备</summary>
        [PreserveSig]
        int SetDefaultEndpoint([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.U4)] ERole role);

        [PreserveSig]
        int SetEndpointVisibility([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }

    /// <summary>
    /// IPolicyConfig COM 接口（Windows 7）
    /// IID: {F8679F50-850A-41CF-9C72-430F290290C8}
    /// vtable 包含 12 个方法，与 IPolicyConfigX 相同
    /// </summary>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bDefault,
            [In] IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pEndpointFormat,
            [In] IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bDefault,
            [In] IntPtr pmftDefaultPeriod,
            [In] IntPtr pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pMode);

        [PreserveSig]
        int SetShareMode([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            [In] IntPtr key,
            [In] IntPtr pv);

        [PreserveSig]
        int SetPropertyValue([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            [In] IntPtr key,
            [In] IntPtr pv);

        /// <summary>设置指定设备为默认设备</summary>
        [PreserveSig]
        int SetDefaultEndpoint([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.U4)] ERole role);

        [PreserveSig]
        int SetEndpointVisibility([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }

    /// <summary>
    /// IPolicyConfigVista COM 接口（Windows Vista）
    /// IID: {568B9108-44BF-40B4-9006-86AFE5B5A620}
    /// vtable 包含 10 个方法，SetDefaultEndpoint 位于 slot 8
    /// </summary>
    [ComImport]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig]
        int GetMixFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bDefault,
            [In] IntPtr ppFormat);

        [PreserveSig]
        int SetDeviceFormat([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pEndpointFormat,
            [In] IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bDefault,
            [In] IntPtr pmftDefaultPeriod,
            [In] IntPtr pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr pMode);

        [PreserveSig]
        int SetShareMode([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In] IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            [In] IntPtr key,
            [In] IntPtr pv);

        [PreserveSig]
        int SetPropertyValue([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            [In] IntPtr key,
            [In] IntPtr pv);

        /// <summary>设置指定设备为默认设备</summary>
        [PreserveSig]
        int SetDefaultEndpoint([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.U4)] ERole role);

        [PreserveSig]
        int SetEndpointVisibility([In][MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [In][MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }

    /// <summary>
    /// PolicyConfig COM CoClass CLSID
    /// Windows 7/8/10/11 共用同一个 CoClass CLSID
    /// {870AF99C-171D-4F9E-AF0D-E63DF40C2BC9}
    /// 创建实例后通过 QueryInterface 转换为不同版本的接口
    /// </summary>
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigCoClass
    {
    }

    /// <summary>
    /// 设置指定音频设备为系统默认设备
    /// 使用 IPolicyConfig COM 接口，同时设置 Console、Multimedia、Communications 三个角色
    /// 创建一次 COM 实例后尝试转换为 Win8+/Win7/Vista 三个接口版本（接口布局见上方 ABI 说明）
    /// </summary>
    /// <param name="deviceId">要设为默认的设备 ID</param>
    /// <param name="type">设备类型：Playback=播放设备, Recording=录音设备</param>
    /// <returns>是否成功</returns>
    public bool SetDefaultDevice(string deviceId, string type)
    {
        Logger.Info($"[AudioDeviceService] 尝试设置默认设备: {deviceId} ({type})");

        try
        {
            // 创建 PolicyConfig COM 实例（使用单一 CoClass CLSID）
            var policyConfig = new PolicyConfigCoClass();

            // 尝试转换为不同版本的接口（Windows 从新到旧：Win8+ → Win7 → Vista）
            IPolicyConfigX? configX = policyConfig as IPolicyConfigX;
            IPolicyConfig? configVII = policyConfig as IPolicyConfig;
            IPolicyConfigVista? configVista = policyConfig as IPolicyConfigVista;

            try
            {
                int hr, hr2, hr3;
                bool success;

                // 优先使用 Win8+ 接口
                if (configX != null)
                {
                    Logger.Debug("[AudioDeviceService] 使用 IPolicyConfigX (Win8+)");
                    hr = configX.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    hr2 = configX.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    hr3 = configX.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    success = (hr == 0 && hr2 == 0 && hr3 == 0);
                    Logger.Info($"[AudioDeviceService] SetDefaultEndpoint 结果: Console={hr:X8}, Multimedia={hr2:X8}, Communications={hr3:X8}");
                }
                // 其次使用 Win7 接口
                else if (configVII != null)
                {
                    Logger.Debug("[AudioDeviceService] 使用 IPolicyConfig (Win7)");
                    hr = configVII.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    hr2 = configVII.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    hr3 = configVII.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    success = (hr == 0 && hr2 == 0 && hr3 == 0);
                    Logger.Info($"[AudioDeviceService] SetDefaultEndpoint 结果: Console={hr:X8}, Multimedia={hr2:X8}, Communications={hr3:X8}");
                }
                // 最后使用 Vista 接口
                else if (configVista != null)
                {
                    Logger.Debug("[AudioDeviceService] 使用 IPolicyConfigVista (Vista)");
                    hr = configVista.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    hr2 = configVista.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    hr3 = configVista.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    success = (hr == 0 && hr2 == 0 && hr3 == 0);
                    Logger.Info($"[AudioDeviceService] SetDefaultEndpoint 结果: Console={hr:X8}, Multimedia={hr2:X8}, Communications={hr3:X8}");
                }
                else
                {
                    Logger.Error("[AudioDeviceService] 无法将 PolicyConfig 转换为任何已知接口版本");
                    return false;
                }

                if (success)
                    Logger.Info($"[AudioDeviceService] 成功设置默认{type}设备");
                else
                    Logger.Warn($"[AudioDeviceService] SetDefaultEndpoint 返回非零 HRESULT");

                return success;
            }
            finally
            {
                Marshal.ReleaseComObject(policyConfig);
            }
        }
        catch (COMException ex)
        {
            Logger.Error($"[AudioDeviceService] PolicyConfig COM 异常: {ex.Message} (HRESULT=0x{ex.HResult:X8})");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AudioDeviceService] 设置默认设备异常: {ex.Message}");
            return false;
        }
    }

    // ============================================================
    // Windows Core Audio API COM 接口定义
    // 以下接口直接映射 Windows mmdevapi.dll 中的 COM 对象
    // ============================================================

    /// <summary>
    /// 设备数据流向枚举：eRender = 播放（输出），eCapture = 录音（输入）
    /// </summary>
    private enum EDataFlow
    {
        eRender = 0,    // 播放设备（扬声器、耳机等）
        eCapture = 1,   // 录音设备（麦克风、线路输入等）
    }

    /// <summary>
    /// 设备角色枚举：eMultimedia = 多媒体角色（系统默认设备）
    /// </summary>
    private enum ERole
    {
        eConsole = 0,       // 控制台角色
        eMultimedia = 1,    // 多媒体角色（我们使用这个）
        eCommunications = 2 // 通信角色（语音通话等）
    }

    /// <summary>
    /// 设备状态标志：用于枚举时过滤不同状态的设备
    /// </summary>
    [Flags]
    private enum DeviceState
    {
        Active = 0x00000001,    // 活动中的设备
        Disabled = 0x00000002,  // 已禁用的设备
        Unplugged = 0x00000004, // 已拔出的设备
        All = 0x00000007        // 所有状态的设备
    }

    /// <summary>
    /// 设备属性键结构
    /// 用于标识 IPropertyStore 中的特定属性（如设备名称、图标等）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;

        public PropertyKey(string guid, int propertyId)
        {
            fmtid = new Guid(guid);
            pid = propertyId;
        }
    }

    /// <summary>
    /// COM 属性值变体结构
    /// 用于从 IPropertyStore 读取属性值（此处仅用于读取字符串类型 VT_LPWSTR）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        private readonly ushort wReserved1;
        private readonly ushort wReserved2;
        private readonly ushort wReserved3;
        public IntPtr pointerVal;
    }

    /// <summary>
    /// IMMDeviceEnumerator COM 接口
    /// Windows 音频设备枚举器的核心接口，用于获取设备列表和默认设备
    /// GUID: {A95664D2-9614-4F35-A746-DE8DB63617E6}
    /// </summary>
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        /// <summary>
        /// 枚举指定类型的所有音频设备
        /// </summary>
        IMMDeviceCollection EnumAudioDevices(EDataFlow dataFlow, DeviceState dwStateMask);

        /// <summary>
        /// 获取指定角色的默认音频设备
        /// </summary>
        IMMDevice GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role);

        /// <summary>
        /// 通过设备 ID 获取设备实例
        /// </summary>
        IMMDevice GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId);

        /// <summary>
        /// 注册音频设备变更通知（我们调用此方法来监听默认设备切换）
        /// </summary>
        void RegisterEndpointNotificationCallback(IMMNotificationClient pClient);

        /// <summary>
        /// 注销音频设备变更通知
        /// </summary>
        void UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    /// <summary>
    /// IMMDeviceCollection COM 接口
    /// 音频设备集合，用于遍历枚举到的设备
    /// GUID: {0BD7A1BE-7A1A-44DB-8397-CC5392387B5E}
    /// </summary>
    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount();
        IMMDevice Item(int nDevice);
    }

    /// <summary>
    /// IMMDevice COM 接口
    /// 单个音频设备的接口，可获取设备 ID、状态、属性等
    /// GUID: {D666063F-1587-4E43-81F1-B948E807363F}
    /// </summary>
    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        /// <summary>
        /// 激活设备的音频端点接口（本功能暂不使用）
        /// </summary>
        void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        /// <summary>
        /// 打开设备的属性存储，用于读取设备名称等属性
        /// </summary>
        void OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

        /// <summary>
        /// 获取设备的唯一标识符字符串
        /// </summary>
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        /// <summary>
        /// 获取设备当前状态（活动/禁用/拔出）
        /// </summary>
        void GetState(out int pdwState);
    }

    /// <summary>
    /// IPropertyStore COM 接口
    /// 用于读取和写入 Windows 设备的属性信息（如设备友好名称）
    /// GUID: {886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}
    /// </summary>
    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount();
        void GetAt(int iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant propvar);
        void Commit();
    }

    /// <summary>
    /// IMMNotificationClient COM 接口
    /// 用于接收系统音频设备变更通知（设备添加/移除、默认设备切换等）
    /// GUID: {799AEEC9-7E89-4D85-8390-6C703CEC60C0}
    /// </summary>
    [ComImport]
    [Guid("799AEEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        /// <summary>
        /// 设备状态变化时触发（设备插入/拔出）
        /// </summary>
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

        /// <summary>
        /// 设备新增时触发
        /// </summary>
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        /// <summary>
        /// 设备移除时触发
        /// </summary>
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        /// <summary>
        /// 默认设备变更时触发（我们关注这个）
        /// </summary>
        /// <param name="dataFlow">设备类型：eRender=播放 / eCapture=录音</param>
        /// <param name="defaultDeviceId">新的默认设备 ID</param>
        void OnDefaultDeviceChanged(EDataFlow dataFlow, ERole role,
            [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

        /// <summary>
        /// 设备属性变化时触发（本功能未使用）
        /// </summary>
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            PropertyKey key);
    }

    /// <summary>
    /// IMMNotificationClient 的实现类
    /// 当系统默认设备变更或设备列表变化时，触发对应事件通知 ViewModel 更新 UI
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private class NotificationClient : IMMNotificationClient
    {
        private readonly Action<string?> _onPlaybackChanged;
        private readonly Action<string?> _onRecordingChanged;
        private readonly Action _onDeviceListChanged;

        public NotificationClient(Action<string?> onPlaybackChanged, Action<string?> onRecordingChanged, Action onDeviceListChanged)
        {
            _onPlaybackChanged = onPlaybackChanged;
            _onRecordingChanged = onRecordingChanged;
            _onDeviceListChanged = onDeviceListChanged;
        }

        /// <summary>
        /// 设备状态变化时触发（设备拔出/插入/启用/禁用）
        /// 状态码：1=ACTIVE, 2=DISABLED, 4=UNPLUGGED
        /// </summary>
        public void OnDeviceStateChanged(string deviceId, int newState)
        {
            var stateName = newState switch
            {
                1 => "已连接",
                2 => "已禁用",
                4 => "已拔出",
                _ => $"状态码:{newState}"
            };
            Logger.Info($"[AudioDeviceService] 设备状态变化: {deviceId} -> {stateName}");
            _onDeviceListChanged();
        }

        /// <summary>
        /// 新增音频设备时触发
        /// </summary>
        public void OnDeviceAdded(string deviceId)
        {
            Logger.Info($"[AudioDeviceService] 新增音频设备: {deviceId}");
            _onDeviceListChanged();
        }

        /// <summary>
        /// 移除音频设备时触发
        /// </summary>
        public void OnDeviceRemoved(string deviceId)
        {
            Logger.Info($"[AudioDeviceService] 移除音频设备: {deviceId}");
            _onDeviceListChanged();
        }

        public void OnDefaultDeviceChanged(EDataFlow dataFlow, ERole role, string defaultDeviceId)
        {
            if (role != ERole.eMultimedia)
                return;

            Logger.Info($"[AudioDeviceService] 系统默认设备变更: {(dataFlow == EDataFlow.eRender ? "播放" : "录音")} -> {defaultDeviceId}");

            if (dataFlow == EDataFlow.eRender)
                _onPlaybackChanged(defaultDeviceId);
            else
                _onRecordingChanged(defaultDeviceId);
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
