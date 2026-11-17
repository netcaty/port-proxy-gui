using System.Diagnostics;
using System.Text;

namespace PortProxyManager.Services;

public enum ProcTextEncoding
{
    /// <summary>系统 ANSI 代码页，适合 netsh / arp 等 Win32 工具。</summary>
    Ansi,

    /// <summary>UTF-8，适合已显式设置输出编码的 PowerShell。</summary>
    Utf8,

    /// <summary>UTF-16LE，适合 wsl.exe 自己的列表类输出（无 BOM）。</summary>
    Utf16,
}

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
/// 统一的子进程执行封装：默认按系统 ANSI 代码页解码，中文不乱码；
/// 个别工具（PowerShell / wsl.exe）输出 UTF-8 或 UTF-16，可显式指定编码。
/// 用 ArgumentList 让 .NET 负责命令行转义，避免手拼引号出错。
/// </summary>
public static class ProcRunner
{
    public static ProcResult Run(
        string fileName,
        IReadOnlyList<string> args,
        int timeoutMs = 30_000,
        ProcTextEncoding textEncoding = ProcTextEncoding.Ansi)
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

        var outText = stdout.IsCompletedSuccessfully ? Decode(stdout.Result, textEncoding) : string.Empty;
        var errText = stderr.IsCompletedSuccessfully ? Decode(stderr.Result, textEncoding) : string.Empty;

        return new ProcResult(process.ExitCode, outText, errText);
    }

    private static string Decode(byte[] bytes, ProcTextEncoding textEncoding)
    {
        if (bytes.Length == 0) return string.Empty;

        try
        {
            switch (textEncoding)
            {
                case ProcTextEncoding.Utf8:
                    return Encoding.UTF8.GetString(bytes);

                case ProcTextEncoding.Utf16:
                {
                    var start = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? 2 : 0;
                    var text = Encoding.Unicode.GetString(bytes, start, bytes.Length - start);
                    return text.Trim('\0', '\uFEFF');
                }

                default:
                    return AnsiText.Decode(bytes);
            }
        }
        catch
        {
            return AnsiText.Decode(bytes);
        }
    }
}
