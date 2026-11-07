namespace AudioCtrl.Models;

/// <summary>
/// 应用程序设置（便携版存 exe 同级 Config/settings.json）
/// v0.16.0 起只保留仍在使用的字段——旧配置文件里的热键/循环组/HID 映射等
/// 已删字段会被反序列化自动忽略，老配置无需迁移
/// </summary>
public class AppSettings
{
    /// <summary>主题模式：0=系统默认，1=浅色，2=深色</summary>
    public int Theme { get; set; } = 0; // 默认跟随系统

    /// <summary>界面语言：0=跟随系统，1=简体中文，2=English</summary>
    public int Language { get; set; } = 0; // 默认跟随系统

    /// <summary>开机自启（默认开；实际自启项在 HKCU Run 键，此字段是用户偏好，
    /// 用于区分"从未设置"和"明确关闭"，并供托盘菜单开关回显）</summary>
    public bool AutoStart { get; set; } = true;
}
