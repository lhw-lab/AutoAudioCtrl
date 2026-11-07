using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioCtrl.Helpers;

/// <summary>
/// 本地化取值辅助类
/// - Loc.T(key)：代码侧取当前语言的字符串（菜单项、Toast、状态文本等运行时构建的内容）
/// - 语言切换由 LanguageService 换字典实现，之后调用 T() 自动返回新语言；
///   XAML 侧用 {DynamicResource} 绑定则即时刷新
/// </summary>
public static class Loc
{
    /// <summary>按键名取当前语言字符串，找不到时原样返回键名（便于发现漏配）</summary>
    public static string T(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>带格式化参数的取值（资源里用 {0}{1} 占位）</summary>
    public static string F(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, T(key), args);
}

/// <summary>
/// XAML 绑定用的格式化转换器：ConverterParameter 传资源键名，
/// 绑定值填入资源串的 {0}。语言切换后需触发对应集合刷新才会重新求值
/// （如 UpdateStands / RefreshAudioDevices）
/// </summary>
public class LocFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Format(culture, Loc.T(parameter as string ?? ""), value ?? "");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
