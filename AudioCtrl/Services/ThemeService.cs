using System.Windows;
using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// 主题切换服务
/// 负责在浅色/深色主题之间切换，通过替换 Application.Resources 中的主题资源字典实现
/// 主题选项：0=系统默认（跟随 Windows 浅色/深色模式），1=浅色，2=深色
/// </summary>
public static class ThemeService
{
    /// <summary>主题值常量</summary>
    public const int SystemDefault = 0;
    public const int Light = 1;
    public const int Dark = 2;

    /// <summary>
    /// 应用指定主题
    /// </summary>
    /// <param name="themeMode">0=系统默认，1=浅色，2=深色</param>
    public static void ApplyTheme(int themeMode)
    {
        // 确定最终主题：系统默认时检测 Windows 主题
        bool isDark = themeMode switch
        {
            SystemDefault => IsWindowsDarkMode(),
            Light => false,
            Dark => true,
            _ => false
        };

        // 选择主题资源字典路径
        string themePath = isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";

        // 在 Application.Resources 中查找并替换主题资源字典
        var app = System.Windows.Application.Current;
        if (app == null) return;

        var mergedDicts = app.Resources.MergedDictionaries;
        // 移除旧的主题字典
        var oldTheme = mergedDicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Theme.xaml") == true);
        if (oldTheme != null)
        {
            mergedDicts.Remove(oldTheme);
        }

        // 添加新主题字典
        var newTheme = new ResourceDictionary { Source = new Uri(themePath, UriKind.Relative) };
        mergedDicts.Add(newTheme);

        Logger.Info($"[ThemeService] 主题已切换为: {(isDark ? "深色" : "浅色")}");
    }

    /// <summary>
    /// 检测 Windows 系统是否处于深色模式
    /// 通过注册表 HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme
    /// </summary>
    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var value = key.GetValue("AppsUseLightTheme");
                if (value != null)
                {
                    return (int)value == 0;
                }
            }
        }
        catch
        {
            // 如果注册表读取失败，默认浅色
        }
        return false;
    }
}
