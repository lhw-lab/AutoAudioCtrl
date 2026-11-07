using Microsoft.Win32;
using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// 开机自启服务（HKCU 注册表 Run 键，免安装绿色软件完全可用）
/// - Enable(): 写入/刷新 Run 键为当前 exe 完整路径——每次启动都调用一次，
///   用户挪动文件夹后自启路径自动纠正
/// - Disable(): 删除 Run 键
/// - 用户偏好持久化在 Config/settings.json 的 AutoStart 字段（默认开），
///   区分"从未设置(默认开)"和"用户明确关闭"
/// </summary>
public static class AutoStartService
{
    /// <summary>HKCU Run 键路径</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名（即自启项显示名）</summary>
    private const string ValueName = "AudioCtrl";

    /// <summary>写入/刷新自启项（带引号防路径空格问题）</summary>
    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            string exePath = Environment.ProcessPath ?? "";
            if (exePath.Length > 0)
            {
                key?.SetValue(ValueName, $"\"{exePath}\" --startup");   // --startup: 开机静默驻托盘
                Logger.Info($"[自启] Run 键已刷新: {exePath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[自启] 写入 Run 键失败: {ex.Message}");
        }
    }

    /// <summary>移除自启项</summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName);
                Logger.Info("[自启] Run 键已移除");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[自启] 移除 Run 键失败: {ex.Message}");
        }
    }

    /// <summary>当前自启项是否存在于注册表（不判断路径新旧）</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按用户设置应用自启状态（应用启动时调用：开=刷新路径，关=移除）</summary>
    public static void Apply(bool userEnabled)
    {
        if (userEnabled) Enable();
        else Disable();
    }
}
