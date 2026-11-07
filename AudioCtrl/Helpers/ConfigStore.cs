using System.IO;

namespace AudioCtrl.Helpers;

/// <summary>
/// 配置文件原子写入助手（v0.16.0 引入，v0.16.1 目录解析移交 AppPaths）
/// 原子写入：直接 WriteAllText 写到一半崩溃会留下损坏 JSON，下次 Load 按
/// 默认值重建导致用户配置全丢。改为：同目录临时文件 + File.Replace 原子替换。
/// File.Replace 在个别文件系统（部分网络盘/可移动盘）上可能失败，此时回退
/// File.Move(overwrite)——退化为非原子但仍能写入。
/// </summary>
public static class ConfigStore
{
    /// <summary>配置目录（便携优先，只读目录回退 %LOCALAPPDATA%，见 AppPaths）</summary>
    public static string GetConfigDir() => AppPaths.ConfigDir;

    /// <summary>配置文件的完整路径（fileName 形如 settings.json）</summary>
    public static string GetConfigPath(string fileName)
        => Path.Combine(GetConfigDir(), fileName);

    /// <summary>
    /// 原子写入文本文件：临时文件 + File.Replace（目标不存在用 Move）。
    /// 失败抛异常，由调用方记日志。
    /// </summary>
    public static void AtomicWriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);

        try
        {
            if (File.Exists(path))
                File.Replace(tempPath, path, destinationBackupFileName: null);
            else
                File.Move(tempPath, path);
        }
        catch (PlatformNotSupportedException)
        {
            // 个别文件系统不支持 ReplaceFile：退化为覆盖移动
            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException)
        {
            File.Move(tempPath, path, overwrite: true);
        }
    }

    /// <summary>
    /// 配置文件解析失败时的抢救备份：改名为 .corrupt 保留现场再走默认值，
    /// 用户手改坏/半写坏的文件不至于被默认值直接覆盖丢失
    /// </summary>
    public static void BackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".corrupt", overwrite: true);
        }
        catch { /* 备份失败不阻塞主流程 */ }
    }
}
