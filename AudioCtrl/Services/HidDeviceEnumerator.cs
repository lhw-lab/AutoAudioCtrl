using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// HID 设备枚举器（v0.13.0 去 HidLibrary，自写 SetupAPI 实现）
/// 用 SetupDiGetClassDevs + HID 接口 GUID 枚举当前在线的 HID 设备接口，
/// 从设备路径解析 VID/PID 过滤。HidLibrary 依赖仅剩的枚举用途由此替代，
/// 后续读写仍走 StandHidService 里原有的 CreateFile/ReadFile/WriteFile。
/// </summary>
public static partial class HidDeviceEnumerator
{
    /// <summary>HID 设备接口 GUID（GUID_DEVINTERFACE_HID，系统固定值）</summary>
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>从设备路径提取 vid_XXXX&pid_XXXX（不区分大小写）</summary>
    [GeneratedRegex("vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    private const uint DIGCF_PRESENT = 0x02;          // 只列当前在线的设备
    private const uint DIGCF_DEVICEINTERFACE = 0x10;  // 按接口类枚举

    /// <summary>
    /// 枚举 HID 设备路径，按 VID/PID 过滤。
    /// vid/pid 传 0 表示该位不过滤（都传 0 = 列出全部 HID，调试用）。
    /// 返回的路径可直接交给 CreateFile 打开。
    /// </summary>
    public static List<string> EnumeratePaths(int vid, int pid)
    {
        var result = new List<string>();
        var guid = HidInterfaceGuid;   // readonly 字段不能直接 ref 传参，取局部副本
        IntPtr devInfo = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
        {
            Logger.Warn($"[HidEnum] SetupDiGetClassDevs 失败: {Marshal.GetLastWin32Error()}");
            return result;
        }

        try
        {
            var ifaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
            };
            int scanned = 0;
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero,
                         ref guid, i, ref ifaceData); i++)
            {
                scanned++;

                // 两段式取路径：先问需要多大缓冲
                int needed = 0;
                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                if (needed <= 0) continue;

                IntPtr buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    // 结构体头 cbSize：x86=6(DWORD+WCHAR)，x64=8(对齐填充)，路径从偏移 4 开始
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData,
                            buffer, needed, ref needed, IntPtr.Zero))
                    {
                        Logger.Debug($"[HidEnum] 取设备详情失败: {Marshal.GetLastWin32Error()}");
                        continue;
                    }
                    string path = Marshal.PtrToStringUni((IntPtr)((long)buffer + 4)) ?? "";
                    if (path.Length == 0) continue;

                    // 按 VID/PID 过滤（路径形如 \\?\hid#vid_0483&pid_572b#...）
                    var m = VidPidRegex().Match(path);
                    if (!m.Success) continue;
                    int devVid = Convert.ToInt32(m.Groups[1].Value, 16);
                    int devPid = Convert.ToInt32(m.Groups[2].Value, 16);
                    if (vid != 0 && devVid != vid) continue;
                    if (pid != 0 && devPid != pid) continue;
                    result.Add(path);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            Logger.Debug($"[HidEnum] 扫描到 {scanned} 个 HID 接口, VID={vid:X4}/PID={pid:X4} 匹配 {result.Count} 个");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
        return result;
    }

    // ==================== Win32 ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize,
        ref int RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
}
