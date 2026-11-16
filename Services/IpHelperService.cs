using System.Runtime.InteropServices;

namespace PortProxyManager.Services;

public enum IpHlpState
{
    Running,
    Stopped,
    NotFound,
    Unknown,
}

/// <summary>
/// IP Helper（iphlpsvc）状态检测。portproxy 的转发是由这个服务实现的，
/// 它没运行的话映射配了也不会通。
/// 用 SC 管理器 API 而不是 sc.exe，避免解析本地化文本。
/// </summary>
public static class IpHelperService
{
    private const string ServiceName = "iphlpsvc";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceRunning = 4;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDisabled = 1058;
    private const int ErrorServiceNotActive = 1062;
    private const int ServiceControlStop = 1;
    private const int ServiceStopped = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
        public int dwProcessId;
        public int dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service, int infoLevel, ref ServiceStatusProcess buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartService(IntPtr service, int argCount, IntPtr argPointers);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, int control, ref ServiceStatusProcess status);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static IpHlpState GetState()
    {
        var scm = IntPtr.Zero;
        var svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) return IpHlpState.Unknown;

            svc = OpenService(scm, ServiceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == 1060 ? IpHlpState.NotFound : IpHlpState.Unknown;

            var status = new ServiceStatusProcess();
            var size = Marshal.SizeOf<ServiceStatusProcess>();
            if (!QueryServiceStatusEx(svc, ScStatusProcessInfo, ref status, size, out _))
                return IpHlpState.Unknown;

            return status.dwCurrentState == ServiceRunning ? IpHlpState.Running : IpHlpState.Stopped;
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"查询 IP Helper 状态失败：{ex.Message}");
            return IpHlpState.Unknown;
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// 重启 IP Helper。用于"配置存在但端口没进入监听"的情况——
    /// 服务只在启动时读一次 portproxy 配置，绕过 netsh 写注册表后必须重启才生效。
    /// </summary>
    public static (bool Ok, string Message) TryRestart()
    {
        if (!Elevation.IsElevated)
            return (false, "重启 IP Helper 需要管理员权限。");

        var scm = IntPtr.Zero;
        var svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
                return (false, $"无法连接服务管理器（错误码 {Marshal.GetLastWin32Error()}）。");

            svc = OpenService(scm, ServiceName, ServiceQueryStatus | ServiceStart | ServiceStop);
            if (svc == IntPtr.Zero)
                return (false, $"无法打开 IP Helper 服务（错误码 {Marshal.GetLastWin32Error()}）。");

            var status = new ServiceStatusProcess();

            if (!ControlService(svc, ServiceControlStop, ref status))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ErrorServiceNotActive)   // 本来就没在跑，继续启动
                    return (false, $"停止 IP Helper 失败（错误码 {err}）。");
            }
            else
            {
                // 等服务真正停下来，最多 6 秒
                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                    var probe = new ServiceStatusProcess();
                    var size = Marshal.SizeOf<ServiceStatusProcess>();
                    if (QueryServiceStatusEx(svc, ScStatusProcessInfo, ref probe, size, out _)
                        && probe.dwCurrentState == ServiceStopped)
                        break;
                }
            }

            if (!StartService(svc, 0, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                return err == ErrorServiceAlreadyRunning
                    ? (true, "IP Helper 已在运行。")
                    : (false, $"启动 IP Helper 失败（错误码 {err}）。");
            }

            return (true, "IP Helper 已重启，映射应当已重新加载。");
        }
        catch (Exception ex)
        {
            return (false, $"重启 IP Helper 时出错：{ex.Message}");
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    public static (bool Ok, string Message) TryStart()
    {
        if (!Elevation.IsElevated)
            return (false, "启动服务需要管理员权限，请以管理员身份重新启动本程序。");

        var scm = IntPtr.Zero;
        var svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
                return (false, $"无法连接服务管理器（错误码 {Marshal.GetLastWin32Error()}）。");

            svc = OpenService(scm, ServiceName, ServiceStart | ServiceQueryStatus);
            if (svc == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                return (false, code == 1060
                    ? "系统里找不到 IP Helper 服务（iphlpsvc），端口转发功能不可用。"
                    : $"无法打开 IP Helper 服务（错误码 {code}）。");
            }

            if (StartService(svc, 0, IntPtr.Zero)) return (true, "IP Helper 已启动。");

            var err = Marshal.GetLastWin32Error();
            if (err == ErrorServiceAlreadyRunning) return (true, "IP Helper 本来就在运行。");
            if (err == ErrorServiceDisabled)
                return (false, "IP Helper 服务已被禁用，请先在「服务」里把启动类型改为「手动」或「自动」。");

            return (false, $"启动 IP Helper 失败（错误码 {err}）。");
        }
        catch (Exception ex)
        {
            return (false, $"启动 IP Helper 时出错：{ex.Message}");
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }
}
