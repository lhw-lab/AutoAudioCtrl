using System.Globalization;
using System.Windows;
using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// 界面语言服务（结构与 ThemeService 一致）
/// - 0 = 跟随系统（系统 UI 语言以 zh 开头 → 中文，否则英文）
/// - 1 = 简体中文   2 = English
/// 实现方式：替换 Application.Resources.MergedDictionaries 里的语言字典，
/// XAML 侧 {DynamicResource 键名} 的文本即时刷新；代码侧下次 Loc.T() 取到新值
/// </summary>
public static class LanguageService
{
    /// <summary>中文语言包的文件名片段（用于在合并字典里定位语言字典）</summary>
    private const string LangDictMark = "Lang.";

    /// <summary>应用语言设置（在主题加载之后、创建任何窗口之前调用）</summary>
    public static void ApplyLanguage(int lang)
    {
        string dict = Resolve(lang);
        SwapDictionary(dict);
        Logger.Info($"[语言] 已应用: {DisplayName(lang)}");
    }

    /// <summary>把 0/1/2 解析成实际语言字典文件名</summary>
    private static string Resolve(int lang) => lang switch
    {
        1 => "zh-CN",
        2 => "en-US",
        _ => IsSystemChinese() ? "zh-CN" : "en-US",
    };

    /// <summary>系统 UI 语言是否中文（取当前线程 UI 文化，兼容控制面板覆盖）</summary>
    private static bool IsSystemChinese()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

    /// <summary>语言选项显示名（语言菜单用，选项本身不翻译语义）</summary>
    public static string DisplayName(int lang) => lang switch
    {
        0 => Loc.T("S.Lang.System"),
        1 => Loc.T("S.Lang.Zh"),
        2 => Loc.T("S.Lang.En"),
        _ => lang.ToString(),
    };

    /// <summary>替换应用资源里的语言字典（不存在则插入到最前）</summary>
    private static void SwapDictionary(string culture)
    {
        var uri = new Uri($"pack://application:,,,/Themes/Lang.{culture}.xaml", UriKind.Absolute);
        var dicts = Application.Current.Resources.MergedDictionaries;

        // 先移除旧语言字典（按文件名特征定位）
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            if (dicts[i].Source != null && dicts[i].Source!.OriginalString.Contains(LangDictMark))
            {
                dicts.RemoveAt(i);
            }
        }
        dicts.Insert(0, new ResourceDictionary { Source = uri });
    }
}
