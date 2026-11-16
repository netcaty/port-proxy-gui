using System.Diagnostics;

namespace PortProxyManager.Services;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string Combined
    {
        get
        {
            var parts = new[] { StdOut, StdErr }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim());
            return string.Join(Environment.NewLine, parts);
        }
    }
}

/// <summary>
/// 统一的子进程执行封装：按系统 ANSI 代码页解码输出，避免中文乱码。
/// 用 ArgumentList 让 .NET 负责命令行转义，避免手拼引号出错。
/// </summary>
public static class ProcRunner
{
    public static ProcResult Run(string fileName, IReadOnlyList<string> args, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = Task.Run(() => AnsiText.ReadAllBytes(process.StandardOutput.BaseStream));
        var stderr = Task.Run(() => AnsiText.ReadAllBytes(process.StandardError.BaseStream));

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已被回收 */ }
            throw new TimeoutException($"{Path.GetFileName(fileName)} 在 {timeoutMs} ms 内没有结束。");
        }

        try
        {
            Task.WaitAll(new Task[] { stdout, stderr }, 5_000);
        }
        catch
        {
            // 读取任务异常时用已有内容兜底
        }

        var outText = stdout.IsCompletedSuccessfully ? AnsiText.Decode(stdout.Result) : string.Empty;
        var errText = stderr.IsCompletedSuccessfully ? AnsiText.Decode(stderr.Result) : string.Empty;

        return new ProcResult(process.ExitCode, outText, errText);
    }
}
