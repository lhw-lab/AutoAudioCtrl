using Microsoft.Win32;

namespace AudioCtrl.Services;

/// <summary>
/// 串口枚举(仅在线设备,按 USB VID 过滤)
///
/// 实现:两个注册表视图求交集,全部只读、无需管理员权限、无互操作结构体:
///   1. HKLM\HARDWARE\DEVICEMAP\SERIALCOMM —— 系统硬件映射,只含【当前在线】的串口
///      (\Device\USBSER000 → COM8)
///   2. HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&PID_yyyy\<实例>\
///      Device Parameters\PortName —— USB 串口设备的端口名,键名里带 VID/PID
/// 交集 = "在线 且 指定 VID" 的串口。
///
/// ESP32-C3 的 USB Serial/JTAG 是固定功能外设,VID/PID 恒为 Espressif 0x303A:0x1001
/// 且不可改——因此必须靠 VID 预过滤(避开蓝牙虚拟口/调制解调器),再用协议握手
/// 区分"自家固件"与"桌面上其他 ESP32 开发板"(见 StandSerialService 的探测)。
/// </summary>
public static class ComPortEnumerator
{
    /// <summary>一条候选串口的信息</summary>
    /// <param name="PortName">端口名,如 "COM8"</param>
    /// <param name="InstanceId">USB 实例路径(日志用),含 VID/PID/序列号</param>
    public sealed record ComPortInfo(string PortName, string InstanceId);

    /// <summary>枚举当前在线、指定 USB VID 的串口</summary>
    public static List<ComPortInfo> Enumerate(ushort vid)
    {
        var result = new List<ComPortInfo>();

        // 1. 在线串口名集合(SERIALCOMM 只列当前存在的口)
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var serialComm = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
        {
            if (serialComm != null)
            {
                foreach (var name in serialComm.GetValueNames())
                {
                    if (serialComm.GetValue(name) is string port && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        present.Add(port);
                }
            }
        }

        if (present.Count == 0) return result;

        // 2. 枚举 USB\VID_xxxx* 设备,读其 PortName,与在线集合求交
        string vidPrefix = $"VID_{vid:X4}";
        using (var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
        {
            if (usb == null) return result;
            foreach (var devKey in usb.GetSubKeyNames())          // 形如 VID_303A&PID_1001
            {
                if (!devKey.StartsWith(vidPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                using var dev = usb.OpenSubKey(devKey);
                if (dev == null) continue;
                foreach (var instance in dev.GetSubKeyNames())    // 每个物理实例(串号/MAC)
                {
                    using var inst = dev.OpenSubKey(instance);
                    using var params_ = inst?.OpenSubKey("Device Parameters");
                    if (params_?.GetValue("PortName") is not string port) continue;
                    if (!present.Contains(port)) continue;         // 已拔出的残留键,跳过
                    result.Add(new ComPortInfo(port, $"{devKey}\\{instance}"));
                }
            }
        }
        return result;
    }
}
