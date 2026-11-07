using System.IO;
using System.Linq;

namespace AudioCtrl.Helpers;

/// <summary>
/// 日志工具类（v0.14.0 起双通道输出）
/// - 控制台：开发调试用（dotnet run / 重定向时可见；Release 双击运行无控制台，静默失败无害）
/// - 文件：exe 同级 Logs/audioctrl-日期.log，按天滚动，保留最近 7 天——
///   Release 版没有控制台，用户端排障全靠它
/// 线程安全：所有写入经过锁
/// </summary>
public static class Logger
{
    /// <summary>日志目录（跟随 AppPaths：exe 目录只读时回退 %LOCALAPPDATA%，与配置同根）</summary>
    private static string LogDir => AppPaths.LogsDir;

    /// <summary>保留最近几天的日志</summary>
    private const int RetainDays = 7;

    /// <summary>写文件互斥锁（多线程写 + 清理同用）</summary>
    private static readonly object FileLock = new();

    /// <summary>当前写入的日期文件名（跨天时切换文件并清理旧日志）</summary>
    private static string _currentFileDate = "";

    /// <summary>输出信息级别日志（正常业务流程：启动、功能触发等）</summary>
    public static void Info(string message)
    {
        Write("INFO", message, ConsoleColor.White);
    }

    /// <summary>输出警告级别日志（非致命问题：某设备未找到但可继续）</summary>
    public static void Warn(string message)
    {
        Write("WARN", message, ConsoleColor.Yellow);
    }

    /// <summary>输出错误级别日志（严重问题：API 调用失败、异常等）
    /// 带异常时附加完整类型 + 消息 + 堆栈（含所有内部异常）——
    /// Release 版日志是用户端唯一排障通道，只有 Message 排不了根因</summary>
    public static void Error(string message, Exception? ex = null)
    {
        var fullMessage = message;
        while (ex != null)
        {
            fullMessage += $"{Environment.NewLine}  └ 异常 {ex.GetType().Name}: {ex.Message}";
            if (!string.IsNullOrEmpty(ex.StackTrace))
                fullMessage += $"{Environment.NewLine}{Indent(ex.StackTrace)}";
            ex = ex.InnerException;
        }
        Write("ERROR", fullMessage, ConsoleColor.Red);
    }

    /// <summary>堆栈缩进两格，日志里易读</summary>
    private static string Indent(string stackTrace)
        => string.Join(Environment.NewLine, stackTrace.Split('\n').Select(l => "    " + l.TrimEnd('\r')));

    /// <summary>输出调试级别日志（技术细节：原始字节、API 返回值等）</summary>
    public static void Debug(string message)
    {
        Write("DEBUG", message, ConsoleColor.DarkGray);
    }

    /// <summary>
    /// 核心写入：格式化日志行，同时输出到控制台与滚动文件
    /// 格式: [yyyy-MM-dd HH:mm:ss.fff] [级别] 内容
    /// </summary>
    private static void Write(string level, string message, ConsoleColor color)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        // 1) 控制台（无控制台进程下 Console 调用会静默无害）
        try
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = originalColor;
        }
        catch { /* Release 双击运行无控制台，忽略 */ }

        // 2) 文件（任何失败都不影响主流程，日志系统绝不能抛）
        try
        {
            WriteToFile(timestamp, level, message);
        }
        catch { /* 磁盘满/权限等极端情况：丢日志不丢程序 */ }
    }

    /// <summary>写入按天滚动的日志文件，跨天时自动切换并清理过期日志</summary>
    private static void WriteToFile(string timestamp, string level, string message)
    {
        lock (FileLock)
        {
            string today = DateTime.Now.ToString("yyyyMMdd");
            if (today != _currentFileDate)
            {
                Directory.CreateDirectory(LogDir);
                _currentFileDate = today;
                CleanupOldLogs();
            }
            File.AppendAllText(Path.Combine(LogDir, $"audioctrl-{today}.log"),
                $"[{timestamp}] [{level}] {message}{Environment.NewLine}");
        }
    }

    /// <summary>删除超过保留天数的旧日志（只在跨天切换时执行一次）</summary>
    private static void CleanupOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-RetainDays);
        foreach (var file in Directory.EnumerateFiles(LogDir, "audioctrl-*.log"))
        {
            string datePart = Path.GetFileNameWithoutExtension(file)["audioctrl-".Length..];
            if (DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var fileDate)
                && fileDate < cutoff)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
}
