using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioCtrl.Helpers;

/// <summary>
/// 应用图标工具（纯 WPF + Win32，不依赖 System.Drawing / WinForms）
/// 图标源：exe 内嵌入的 AudioCtrl.ico（csproj 中以 Resource 方式打包）
/// - 窗口图标：IconBitmapDecoder 解码出 ImageSource
/// - 托盘图标：解析 ICO 目录后 CreateIconFromResourceEx 生成原生 HICON
/// </summary>
public static class AppIcon
{
    /// <summary>ICO 资源的 Pack URI（csproj 需含 Resource Include="AudioCtrl.ico"）</summary>
    private const string IcoPackUri = "pack://application:,,,/AudioCtrl.ico";

    /// <summary>读取嵌入 ICO 的原始字节（每次调用返回新副本）</summary>
    public static byte[] GetIcoBytes()
    {
        var sri = Application.GetResourceStream(new Uri(IcoPackUri));
        if (sri == null)
            throw new InvalidOperationException("未找到嵌入资源 AudioCtrl.ico");
        using var ms = new MemoryStream();
        sri.Stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 生成 WPF 窗口图标（标题栏/任务栏）
    /// 取 ICO 中 ≤64px 的最大帧，WPF 会自动缩放到各显示尺寸
    /// </summary>
    public static ImageSource CreateWindowIcon()
    {
        var bytes = GetIcoBytes();
        using var ms = new MemoryStream(bytes);
        var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        // 优先取 64 以下的最大尺寸帧（256 帧做窗口图标浪费且缩放慢）
        var frame = decoder.Frames
            .Where(f => f.Width <= 64)
            .OrderByDescending(f => f.Width)
            .FirstOrDefault() ?? decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// 生成托盘用原生 HICON（Shell_NotifyIcon 需要）
    /// 按系统小图标尺寸（SM_CXSMICON，DPI 感知）选最接近的 ICO 条目
    /// 用完必须 DestroyIcon 释放
    /// </summary>
    public static IntPtr CreateTrayHIcon()
    {
        var bytes = GetIcoBytes();
        int desired = GetSystemMetrics(SM_CXSMICON);
        if (desired <= 0) desired = 16;

        // 解析 ICO 目录：6 字节头 + N 个 16 字节目录项
        int count = BitConverter.ToUInt16(bytes, 4);
        int bestOffset = -1, bestSize = -1, bestLen = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i;
            int width = bytes[entry] == 0 ? 256 : bytes[entry]; // 0 表示 256
            int len = BitConverter.ToInt32(bytes, entry + 8);
            int offset = BitConverter.ToInt32(bytes, entry + 12);
            // 选不超过期望尺寸的最大帧；若全都超过则取最小帧
            bool better;
            if (bestOffset < 0) better = true;
            else if (width <= desired && bestSize > desired) better = true;                 // 首个不超尺寸的
            else if (width <= desired && bestSize <= desired) better = width > bestSize;    // 都不超取更大
            else better = bestSize > desired && width < bestSize;                            // 都超取更小
            if (better) { bestOffset = offset; bestSize = width; bestLen = len; }
        }
        if (bestOffset < 0)
            throw new InvalidOperationException("AudioCtrl.ico 中没有可用图标帧");

        // 把该帧数据复制出来（P/Invoke 的 byte[] 只能从首元素传起）
        var frameBytes = new byte[bestLen];
        Array.Copy(bytes, bestOffset, frameBytes, 0, bestLen);

        // cx/cy 传 0 表示按条目自身尺寸创建
        IntPtr hIcon = CreateIconFromResourceEx(frameBytes, (uint)bestLen, true, 0x00030000, 0, 0, LR_DEFAULTCOLOR);
        if (hIcon == IntPtr.Zero)
            throw new InvalidOperationException($"CreateIconFromResourceEx 失败: {Marshal.GetLastWin32Error()}");
        return hIcon;
    }

    /// <summary>销毁 HICON（CreateTrayHIcon 的配对释放）</summary>
    public static void DestroyHIcon(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }

    // ==================== Win32 ====================

    private const int SM_CXSMICON = 49;
    private const uint LR_DEFAULTCOLOR = 0;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize,
        bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint Flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
