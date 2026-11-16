using System.Text;

namespace PortProxyManager.Services;

/// <summary>
/// 极简日志：每天一个文件，保留最近 7 天。
/// 任何 IO 异常都被吞掉——日志失败绝不能影响主流程。
/// </summary>
public sealed class LogService
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static LogService? Current { get; private set; }

    private readonly string _dir;

    public LogService(string logDir)
    {
        _dir = logDir;
        Directory.CreateDirectory(_dir);
        Current = this;
        PurgeOld();
    }

    public string CurrentFile => Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
    public string Dir => _dir;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                File.AppendAllText(CurrentFile, line, Utf8NoBom);
            }
        }
        catch
        {
            // 日志不可写时静默放弃
        }
    }

    public void PurgeOld(int keepDays = 7)
    {
        try
        {
            var limit = DateTime.Now.Date.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < limit) File.Delete(file);
                }
                catch
                {
                    // 单个文件删不掉不影响其他
                }
            }
        }
        catch
        {
            // 目录不可枚举时忽略
        }
    }
}
