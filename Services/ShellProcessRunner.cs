using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PortProxyManager.Services;

/// <summary>
/// 用资源管理器（当前登录用户、非提权）的令牌启动子进程并收集输出。
///
/// Windows 沙盒 CLI 通过命名管道连接 WindowsSandboxServer，管道名由当前
/// 令牌的 User / Owner SID 计算。提权令牌的 Owner 是 Administrators，而
/// 正在运行的 WindowsSandboxServer 是非提权的（Owner 是当前用户），两边
/// 管道名对不上，wsb list / wsb ip 会一直等到 30 秒超时。这里借用
/// explorer.exe 的中等完整性令牌运行 wsb，让它和沙盒服务使用同一个管道名。
///
/// 输出通过 `cmd /c ... > 临时文件 2>&1` 收集，避免依赖句柄继承。
/// </summary>
internal static class ShellProcessRunner
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>
    /// 以桌面 shell 用户的身份运行命令。失败时返回 null，并通过 error 说明原因。
    /// 调用方应只在自身已提权时使用；非提权进程直接 ProcRunner 即可。
    /// </summary>
    public static ProcResult? TryRun(
        string fileName,
        IReadOnlyList<string> args,
        int timeoutMs,
        out string? error)
    {
        error = null;

        EnablePrivilege("SeImpersonatePrivilege");

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"portproxy-wsb-{Guid.NewGuid():N}.txt");

        IntPtr processToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            var shellPid = FindShellProcessId();
            if (shellPid == 0)
            {
                error = "找不到桌面 shell（explorer.exe）进程";
                return null;
            }

            var shellProcess = OpenProcess(ProcessQueryLimitedInformation, false, shellPid);
            if (shellProcess == IntPtr.Zero)
            {
                error = Win32Error("OpenProcess");
                return null;
            }

            try
            {
                if (!OpenProcessToken(shellProcess, TokenDuplicate | TokenQuery, out processToken))
                {
                    error = Win32Error("OpenProcessToken");
                    return null;
                }
            }
            finally
            {
                CloseHandle(shellProcess);
            }

            if (!DuplicateTokenEx(
                    processToken,
                    MaximumAllowed,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenPrimary,
                    out primaryToken))
            {
                error = Win32Error("DuplicateTokenEx");
                return null;
            }

            var cmdExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");

            var commandLine = BuildCommandLine(cmdExe, fileName, args, outputPath);
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                dwFlags = StartfUseShowWindow,
                wShowWindow = SwHide,
            };

            if (!CreateProcessWithTokenW(
                    primaryToken,
                    0,
                    cmdExe,
                    commandLine,
                    CreateNoWindow,
                    IntPtr.Zero,
                    Path.GetDirectoryName(fileName),
                    ref startup,
                    out var processInfo))
            {
                error = Win32Error("CreateProcessWithTokenW");
                return null;
            }

            processHandle = processInfo.hProcess;
            threadHandle = processInfo.hThread;

            var wait = WaitForSingleObject(processHandle, (uint)Math.Max(1, timeoutMs));
            if (wait == WaitTimeout)
            {
                KillTree(processInfo.dwProcessId);
                error = $"{Path.GetFileName(fileName)} 在 {timeoutMs} ms 内没有结束。";
                return null;
            }

            if (wait == WaitFailed)
            {
                error = Win32Error("WaitForSingleObject");
                return null;
            }

            GetExitCodeProcess(processHandle, out var exitCode);
            var text = ReadOutput(outputPath);
            return new ProcResult(unchecked((int)exitCode), text, string.Empty);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (processToken != IntPtr.Zero) CloseHandle(processToken);
            TryDelete(outputPath);
        }
    }

    private static uint FindShellProcessId()
    {
        var shellWindow = GetShellWindow();
        if (shellWindow != IntPtr.Zero)
        {
            GetWindowThreadProcessId(shellWindow, out var pid);
            if (pid != 0) return pid;
        }

        try
        {
            var currentSession = Process.GetCurrentProcess().SessionId;
            return (uint?)Process.GetProcessesByName("explorer")
                .FirstOrDefault(p => p.SessionId == currentSession)?.Id ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static StringBuilder BuildCommandLine(
        string cmdExe,
        string fileName,
        IReadOnlyList<string> args,
        string outputPath)
    {
        var inner = new StringBuilder();
        inner.Append(Quote(fileName));
        foreach (var arg in args)
        {
            inner.Append(' ');
            inner.Append(Quote(arg));
        }
        inner.Append(" > ");
        inner.Append(Quote(outputPath));
        inner.Append(" 2>&1");

        var commandLine = new StringBuilder();
        commandLine.Append(Quote(cmdExe));
        commandLine.Append(" /d /s /c \"");
        commandLine.Append(inner);
        commandLine.Append('"');
        return commandLine;
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string ReadOutput(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                var bytes = File.ReadAllBytes(path);
                return AnsiText.Decode(bytes);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }

        return string.Empty;
    }

    private static void KillTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch
        {
            // 进程已经退出
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件留待系统清理
        }
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out var token))
            return;

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string Win32Error(string api)
    {
        var code = Marshal.GetLastWin32Error();
        var message = code != 0 ? new Win32Exception(code).Message : "未知错误";
        return $"{api} 失败（{code}）：{message}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        uint logonFlags,
        string applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string privilegeName, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
