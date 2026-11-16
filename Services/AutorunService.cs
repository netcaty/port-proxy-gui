using System.Security;
using System.Text;

namespace PortProxyManager.Services;

/// <summary>
/// 登录自启。用计划任务（RunLevel=HighestAvailable）而不是 HKCU\...\Run：
/// 提权程序放在 Run 键会让每次开机弹一次 UAC，计划任务可以静默以管理员身份启动。
/// 通过 /xml 注册，避免 schtasks /tr 那套引号地狱。
/// </summary>
public static class AutorunService
{
    public const string TaskName = "PortProxyManager";

    private static string Schtasks => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public static string ExecutablePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "PortProxyManager.exe");

    public static string BuildArguments(string dataDir) =>
        $"--tray --data-dir=\"{dataDir}\"";

    public static bool IsEnabled()
    {
        try
        {
            var result = ProcRunner.Run(Schtasks, new[] { "/query", "/tn", TaskName });
            return result.Ok;
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Message) Enable(string dataDir)
    {
        if (!Elevation.IsElevated)
            return (false, "注册开机自启需要管理员权限。");

        var xml = BuildXml(ExecutablePath, dataDir);
        var temp = Path.Combine(Path.GetTempPath(), $"ppm-task-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks 要求 XML 是 Unicode（UTF-16，带 BOM）
            File.WriteAllText(temp, xml, Encoding.Unicode);

            var result = ProcRunner.Run(Schtasks, new[] { "/create", "/tn", TaskName, "/xml", temp, "/f" });
            if (!result.Ok)
                return (false, NetshRunner.Friendly(result));

            return IsEnabled()
                ? (true, $"已注册开机自启（计划任务「{TaskName}」，管理员权限，不弹 UAC）。")
                : (false, "计划任务创建命令返回成功，但查询不到该任务，请检查「任务计划程序」。");
        }
        catch (Exception ex)
        {
            return (false, $"注册开机自启失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 临时文件删不掉无所谓
            }
        }
    }

    public static (bool Ok, string Message) Disable()
    {
        if (!Elevation.IsElevated)
            return (false, "删除开机自启需要管理员权限。");

        var result = ProcRunner.Run(Schtasks, new[] { "/delete", "/tn", TaskName, "/f" });
        if (result.Ok || !IsEnabled()) return (true, "已取消开机自启。");
        return (false, NetshRunner.Friendly(result));
    }

    private static string BuildXml(string exePath, string dataDir)
    {
        var user = SecurityElement.Escape(Elevation.CurrentUser) ?? string.Empty;
        var exe = SecurityElement.Escape(exePath) ?? string.Empty;
        var args = SecurityElement.Escape(BuildArguments(dataDir)) ?? string.Empty;

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PortProxy 管理器：登录后以管理员权限在托盘常驻。</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>{args}</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }
}
