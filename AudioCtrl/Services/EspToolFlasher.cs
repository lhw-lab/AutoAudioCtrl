using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AudioCtrl.Helpers;

namespace AudioCtrl.Services;

/// <summary>
/// 一键烧录服务(esptool 集成,面向 ESP32-C3 新板)
///
/// 职责:
///   1. 解析 esptool 与固件镜像的来源(发布包内置优先,开发环境回退)
///   2. 进程调用 esptool 写入三段镜像(C3 标准布局),实时解析输出/进度
///
/// 端口协调由调用方负责:烧录前必须先断开本程序对该 COM 口的占用
/// (StandSerialService.CloseConnection + FlashingInProgress 抑制重连),
/// esptool 需要独占端口来执行 DTR/RTS 复位时序。
///
/// esptool 许可为 GPLv2+:作为独立进程调用属 mere aggregation,
/// 不影响主程序 MIT 许可(发布包需在 THIRD-PARTY-NOTICES 登记并附带其源码获取方式)。
/// </summary>
public partial class EspToolFlasher
{
    /// <summary>烧录镜像布局(ESP32-C3 标准:bootloader@0x0,分区表@0x8000,应用@0x10000)</summary>
    private static readonly (int Offset, string File)[] ImageLayout =
    {
        (0x00000, "bootloader.bin"),
        (0x08000, "partitions.bin"),
        (0x10000, "firmware.bin"),
    };

    /// <summary>解析出的工具与固件路径(Command + ArgsPrefix 用于 python -m esptool 形态)</summary>
    public sealed record ToolPaths(string Command, string ArgsPrefix, string FirmwareDir, string Source);

    /// <summary>规范化输出行(进度条已被拆成多行,诊断/显示用)</summary>
    public event Action<string>? OutputLine;

    /// <summary>当前文件写入进度 0-100(esptool 的百分比按单文件计,每段镜像重置)</summary>
    public event Action<int>? Progress;

    [GeneratedRegex(@"\((\d+)\s*%\)")]
    private static partial Regex PercentRegex();

    /// <summary>
    /// 解析 esptool 与固件目录:
    ///   esptool: ① exe 目录 Tools\esptool.exe(发布包内置) ② PATH 上的 esptool.exe
    ///            ③ PlatformIO 自带:penv 的 python + tool-esptoolpy 包里的 esptool.py
    ///              (装了 VSCode PlatformIO 但没装系统 Python 的开发机——曾因缺这层
    ///               报"退出码 9009 找不到 python"烧录失败)
    ///            ④ python -m esptool(开发机 pip 安装)
    ///   固件:   ① 参数指定 ② exe 目录 Firmware\(发布包内置)
    ///            ③ 开发仓库:从 exe 目录向上找 firmware-c3\.pio\build\esp32c3
    /// 两者齐备才返回 ToolPaths,否则 null(调用方给出缺什么提示)。
    /// </summary>
    public static ToolPaths? ResolveTools(string? firmwareDirOverride = null)
    {
        var exeDir = AppContext.BaseDirectory;

        // ---- esptool ----
        string command, argsPrefix, esptoolSource;
        var bundled = Path.Combine(exeDir, "Tools", "esptool.exe");
        if (File.Exists(bundled))
        {
            command = bundled;
            argsPrefix = "";
            esptoolSource = bundled;
        }
        else if (TryResolvePlatformIoEsptool(out string pioPy, out string pioScript))
        {
            command = pioPy;
            argsPrefix = $"\"{pioScript}\"";
            esptoolSource = $"PlatformIO 自带 esptool ({pioScript})";
        }
        else if (TryResolvePython(out string python))
        {
            command = python;
            argsPrefix = "-m esptool";
            esptoolSource = "python -m esptool (pip 安装)";
        }
        else
        {
            // 最后一层也没有 Python:明确返回 null,让 UI 禁用“开始烧录”并提示缺工具,
            // 而不是先启用、点击后才报“无法启动进程”。
            return null;
        }

        // ---- 固件目录 ----
        string? fwDir = firmwareDirOverride
                        ?? FirstExisting(Path.Combine(exeDir, "Firmware"))
                        ?? FindDevFirmwareDir(exeDir);
        if (fwDir == null) return null;

        foreach (var (_, file) in ImageLayout)
        {
            if (!File.Exists(Path.Combine(fwDir, file))) return null;
        }
        return new ToolPaths(command, argsPrefix, fwDir, esptoolSource);
    }

    private static string? FirstExisting(string dir) => Directory.Exists(dir) ? dir : null;

    /// <summary>
    /// 在 PATH 中探测可用的 python.exe(pip 装 esptool 的兜底层)。
    /// 只做存在性检查;esptool 模块是否存在仍由实际烧录时报错兜底。
    /// </summary>
    private static bool TryResolvePython(out string pythonPath)
    {
        pythonPath = "";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim().Trim('"'), "python.exe");
            if (File.Exists(candidate))
            {
                pythonPath = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 探测 PlatformIO 自带的 esptool:%USERPROFILE%\.platformio 下 penv 的 python.exe
    /// 与 packages\tool-esptoolpy\esptool.py 直调(已实测可用,无需系统 Python)。
    /// penv python 可能不在 PATH,必须用完整路径调用
    /// </summary>
    private static bool TryResolvePlatformIoEsptool(out string pythonPath, out string esptoolScript)
    {
        pythonPath = "";
        esptoolScript = "";
        try
        {
            var pioRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformio");
            var py = Path.Combine(pioRoot, "penv", "Scripts", "python.exe");
            var script = Path.Combine(pioRoot, "packages", "tool-esptoolpy", "esptool.py");
            if (File.Exists(py) && File.Exists(script))
            {
                pythonPath = py;
                esptoolScript = script;
                return true;
            }
        }
        catch { /* 探测失败静默,走下一层兜底 */ }
        return false;
    }

    /// <summary>开发环境回退:从 exe 目录逐级向上找 firmware-c3\.pio\build\esp32c3</summary>
    private static string? FindDevFirmwareDir(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "firmware-c3", ".pio", "build", "esp32c3");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 烧录一个端口(阻塞至 esptool 退出)。返回 true = 退出码 0。
    /// tools 传入null时按默认规则解析;建议传入调用方已解析/已展示给用户的同一份,
    /// 避免解析两次且界面显示与实际使用一致。
    /// 输出经 OutputLine 逐行上报,百分比经 Progress 上报。
    /// 调用前必须已释放本程序对该端口的占用。
    /// </summary>
    public async Task<bool> FlashAsync(string portName, ToolPaths? tools = null,
                                       CancellationToken ct = default)
    {
        tools ??= ResolveTools()
                 ?? throw new InvalidOperationException("esptool 或固件镜像不可用(ResolveTools 为空)");

        // write_flash 参数:偏移+文件成对出现;--after hard_reset 烧完自动复位重启。
        // 镜像路径必须加引号:仓库路径常含空格(如 "audio ctrl"),不带引号会被
        // esptool 拆成多个参数导致烧录失败(路径含空格时)
        var sb = new StringBuilder();
        sb.Append(tools.ArgsPrefix);
        sb.Append(" --chip esp32c3 --port ").Append(portName);
        sb.Append(" --baud 921600 --after hard_reset write_flash");
        foreach (var (offset, file) in ImageLayout)
        {
            sb.Append(" 0x").Append(offset.ToString("X"))
              .Append(" \"").Append(Path.Combine(tools.FirmwareDir, file)).Append('"');
        }

        Logger.Info($"[Flash] 开始烧录 {portName}: {tools.Command} {sb}");
        int lastProgress = -1;

        var psi = new ProcessStartInfo
        {
            FileName = tools.Command,
            Arguments = sb.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            Logger.Error($"[Flash] 无法启动 esptool: {tools.Command}", null);
            return false;
        }

        // esptool 的进度条用 \r 不换行:按字符块读原始流,自己按 \r/\n 切行
        var pumpOut = PumpLines(proc.StandardOutput, ct);
        var pumpErr = PumpLines(proc.StandardError, ct);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }
        finally
        {
            await Task.WhenAll(pumpOut, pumpErr);
        }

        bool ok = proc.ExitCode == 0;
        Logger.Info($"[Flash] esptool 退出码 {proc.ExitCode} ({(ok ? "成功" : "失败")})");
        if (ok) Progress?.Invoke(100);
        return ok;

        // 本地函数:读流切行,匹配百分比的行同时上报 Progress
        async Task PumpLines(StreamReader reader, CancellationToken token)
        {
            var buf = new char[4096];
            var line = new StringBuilder();
            while (!token.IsCancellationRequested)
            {
                int n;
                try { n = await reader.ReadAsync(buf.AsMemory(), token); }
                catch (Exception) { break; }   // 流关闭/进程退出
                if (n == 0) break;

                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (c == '\r' || c == '\n')
                    {
                        var s = line.ToString();
                        line.Clear();
                        if (s.Length == 0) continue;
                        Emit(s);
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }
            if (line.Length > 0) Emit(line.ToString());
        }

        void Emit(string s)
        {
            OutputLine?.Invoke(s);
            var m = PercentRegex().Match(s);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int pct) && pct != lastProgress)
            {
                lastProgress = pct;
                Progress?.Invoke(pct);
            }
        }

        static void TryKill(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
