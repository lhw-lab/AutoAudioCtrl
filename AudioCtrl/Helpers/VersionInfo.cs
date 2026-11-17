namespace AudioCtrl.Helpers;

/// <summary>
/// 版本信息工具类
/// 集中管理应用程序版本号，确保控制台输出和 UI 显示使用同一来源
/// </summary>
public static class VersionInfo
{
    /// <summary>
    /// 当前应用版本号——直接取程序集版本(由根目录 Directory.Build.props 的
    /// &lt;Version&gt; 唯一维护),避免 csproj/代码两处手改后漂移。
    /// </summary>
    public static string CurrentVersion { get; } =
        typeof(VersionInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
