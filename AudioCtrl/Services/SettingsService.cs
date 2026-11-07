using System.IO;
using System.Text.Json;
using AudioCtrl.Helpers;
using AudioCtrl.Models;

namespace AudioCtrl.Services;

/// <summary>
/// 应用程序设置持久化服务
/// 负责将 AppSettings 序列化为 JSON 并保存到 Config/ 目录
/// 支持自动加载默认设置（文件不存在时）
/// </summary>
public class SettingsService
{
    /// <summary>配置文件所在目录</summary>
    private readonly string _configDir;

    /// <summary>配置文件完整路径</summary>
    private readonly string _configPath;

    /// <summary>JSON 序列化选项：缩进格式化，便于用户手动编辑</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 允许中文不转义
    };

    /// <summary>
    /// 构造函数：确定配置文件路径
    /// 便携优先（exe 同级 Config/），目录不可写时自动回退 %LOCALAPPDATA%（见 ConfigStore）
    /// </summary>
    public SettingsService()
    {
        _configDir = ConfigStore.GetConfigDir();
        _configPath = ConfigStore.GetConfigPath("settings.json");

        Logger.Info($"[SettingsService] 配置路径: {_configPath}");
    }

    /// <summary>
    /// 加载设置
    /// 如果配置文件存在则读取，否则创建默认设置并保存
    /// </summary>
    public AppSettings Load()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    Logger.Info("[SettingsService] 设置已加载");
                    return settings;
                }
                Logger.Warn("[SettingsService] 配置文件解析结果为空，使用默认设置");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SettingsService] 加载配置文件失败: {ex.Message}，使用默认设置");
            }
            // 解析失败:损坏文件先备份再走默认值,不被默认值直接覆盖
            ConfigStore.BackupCorrupt(_configPath);
        }
        else
        {
            Logger.Info("[SettingsService] 配置文件不存在，将使用默认设置");
        }

        // 返回默认设置并自动保存
        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    /// <summary>
    /// 保存设置到 JSON 文件
    /// 原子写入（临时文件+替换）：写到一半崩溃也不会留下损坏 JSON
    /// </summary>
    /// <param name="settings">要保存的设置对象</param>
    /// <returns>是否保存成功</returns>
    public bool Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            ConfigStore.AtomicWriteAllText(_configPath, json);
            Logger.Info("[SettingsService] 设置已保存");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("[SettingsService] 保存设置失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 获取配置文件路径（用于 UI 展示或调试）
    /// </summary>
    public string ConfigPath => _configPath;
}
