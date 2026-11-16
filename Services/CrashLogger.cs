using System.Text;

namespace PortProxyManager.Services;

/// <summary>
/// 未处理异常的最后一道网。图形界面最怕的是"闪退"——进程消失、没有任何提示、
/// 用户和开发者都不知道发生了什么。这里把异常栈写到文件，并尽量给用户一个提示。
/// 写盘有多级回退，确保即使数据目录不可写也能留下记录。
/// </summary>
public static class CrashLogger
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static string? _primaryDir;

    /// <summary>数据目录确定后调用，崩溃日志优先落在这里。</summary>
    public static void Initialize(string logDir)
    {
        _primaryDir = logDir;
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch
        {
            // 只是首选项，写不了也没关系
        }
    }

    public static string Report(Exception exception, string source)
    {
        var text = Build(exception, source);
        var path = Write(text);
        LogService.Current?.Error($"{source} 未处理异常：{exception}");

        return path is null
            ? text
            : $"{text}{Environment.NewLine}{Environment.NewLine}（已记录到 {path}）";
    }

    private static string Build(Exception exception, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"来源：{source}");
        sb.AppendLine($"版本：{typeof(CrashLogger).Assembly.GetName().Version}");
        sb.AppendLine($"数据目录：{_primaryDir ?? "(未初始化)"}");
        sb.AppendLine($"exe：{Environment.ProcessPath}");
        sb.AppendLine($"权限：{(Elevation.IsElevated ? "管理员" : "普通用户")}");
        sb.AppendLine($"系统：{Environment.OSVersion} / .NET {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine(exception.ToString());
        return sb.ToString();
    }

    private static string? Write(string text)
    {
        foreach (var dir in Candidates())
        {
            try
            {
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                lock (Gate)
                {
                    File.AppendAllText(file, text, Utf8NoBom);
                }
                return file;
            }
            catch
            {
                // 换下一个位置
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        if (_primaryDir is not null) yield return _primaryDir;

        var exeDir = AppContext.BaseDirectory;
        yield return exeDir;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PortProxyManager");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortProxyManager");

        yield return Path.GetTempPath();
    }
}
