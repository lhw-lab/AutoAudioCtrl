using System.IO;
using System.Text.Json;
using AudioCtrl.Helpers;
using AudioCtrl.Models;

namespace AudioCtrl.Services;

/// <summary>
/// 支架配置持久化服务(Config/stand-settings.json)
/// 与旧 settings.json 分开存放,互不影响
/// </summary>
public class StandSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _configPath;

    /// <summary>当前生效的配置(内存对象,修改后调用 Save 持久化)</summary>
    public StandAppSettings Current { get; private set; } = new();

    public StandSettingsService()
    {
        _configPath = ConfigStore.GetConfigPath("stand-settings.json");
        Load();
    }

    /// <summary>加载配置(文件不存在或损坏时使用默认值并重建文件;
    /// 损坏文件先备份为 .corrupt 保留现场,不会被默认值直接覆盖丢失)</summary>
    private void Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<StandAppSettings>(json);
                if (loaded != null)
                {
                    Current = loaded;
                    Logger.Info($"[StandSettings] 支架配置已加载: {Current.Stands.Count} 个支架映射");
                    return;
                }
                Logger.Warn("[StandSettings] 配置解析结果为空,备份后使用默认配置");
                ConfigStore.BackupCorrupt(_configPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[StandSettings] 读取支架配置失败,使用默认配置", ex);
            ConfigStore.BackupCorrupt(_configPath);
        }
        Current = new StandAppSettings();
        Save();
    }

    /// <summary>保存配置到文件（原子写入：临时文件+替换，防半写损坏）</summary>
    public void Save()
    {
        try
        {
            ConfigStore.AtomicWriteAllText(_configPath,
                JsonSerializer.Serialize(Current, JsonOptions));
            Logger.Info("[StandSettings] 支架配置已保存");
        }
        catch (Exception ex)
        {
            Logger.Error("[StandSettings] 保存支架配置失败", ex);
        }
    }

    /// <summary>取指定支架的映射(不存在返回 null)</summary>
    public StandMapping? GetMapping(ushort standId)
        => Current.Stands.FirstOrDefault(s => s.StandId == standId);

    /// <summary>取指定支架的映射,不存在则创建空映射(显示名由 UI 层按语言生成)</summary>
    public StandMapping GetOrCreateMapping(ushort standId)
    {
        var m = GetMapping(standId);
        if (m == null)
        {
            m = new StandMapping { StandId = standId };
            Current.Stands.Add(m);
        }
        return m;
    }
}
