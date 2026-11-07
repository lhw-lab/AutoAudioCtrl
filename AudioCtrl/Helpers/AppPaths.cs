using System.IO;

namespace AudioCtrl.Helpers;

/// <summary>
/// 可写根目录解析（v0.16.1）
/// 便携优先：exe 同级目录可写 → 配置与日志都留在 exe 旁边（删目录零残留）；
/// exe 目录只读（如解压进 C:\Program Files）→ 统一回退 %LOCALAPPDATA%\AudioCtrl\。
/// Config 与 Logs 共用同一探测结果（Lazy 缓存，进程内只探一次）。
/// 注意：本类不得调用 Logger（Logger 反过来依赖本类解析日志目录，会循环），
/// 探测失败静默走回退即可。
/// </summary>
public static class AppPaths
{
    /// <summary>可写根目录（Lazy + 线程安全，首次访问时探测）</summary>
    private static readonly Lazy<string> _writableRoot = new(ResolveWritableRoot);

    /// <summary>可写根目录：exe 目录可写即 exe 目录，否则 %LOCALAPPDATA%\AudioCtrl</summary>
    public static string WritableRoot => _writableRoot.Value;

    /// <summary>配置目录</summary>
    public static string ConfigDir => Path.Combine(WritableRoot, "Config");

    /// <summary>日志目录</summary>
    public static string LogsDir => Path.Combine(WritableRoot, "Logs");

    /// <summary>解析可写根目录：探测 exe 目录，失败回退本地应用数据</summary>
    private static string ResolveWritableRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        if (IsDirWritable(baseDir))
            return baseDir;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioCtrl");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>探测目录可写性：实际写入并删除一个探测文件（权限判断唯一可靠方式）</summary>
    private static bool IsDirWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
