using System.Threading;
using PortProxyManager.Services;
using PortProxyManager.WinUI;

namespace PortProxyManager.WinUI;

/// <summary>
/// 手写入口（DISABLE_XAML_GENERATED_MAIN）：CLI 分发、UAC 自提权、单实例
/// 都要在 XAML 应用启动之前完成，行为与旧版 Program.cs 对齐。
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\PortProxyManager.SingleInstance";
    private const string ActivateEventName = @"Local\PortProxyManager.Activate";
    internal const string RestartEnvName = "PORTPROXYMANAGER_RESTART";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                CrashLogger.Report(ex, "AppDomain.UnhandledException");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.Report(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        var options = CliOptions.Parse(args);

        if (options.Help)
        {
            Cli.WriteHelp();
            return 0;
        }

        // 只读模式：不需要提权，也不碰界面
        if (options.Dump) return Cli.Dump(options);
        if (options.Hints) return await Cli.Hints(options);

        var elevated = Elevation.IsElevated;

        if (!elevated && !options.NoElevate)
        {
            var code = Cli.RelaunchElevated(args, wait: options.SelfTest);

            if (code == 1223)
            {
                // 用户取消了 UAC
                if (options.SelfTest)
                {
                    Cli.EnsureConsole();
                    Cli.SafeWriteLine("已取消提权，只运行不需要管理员的部分。");
                    return SelfTest.Run(options, writePhase: false);
                }

                Cli.ErrorBox("管理端口映射需要管理员权限，已取消启动。");
                return 1223;
            }

            return code;
        }

        if (options.SelfTest) return SelfTest.Run(options, writePhase: elevated);

        return RunGui(options, elevated);
    }

    private static int RunGui(CliOptions options, bool elevated)
    {
        var paths = PathContext.Resolve(options.DataDir, out var error);
        if (paths is null)
        {
            Cli.ErrorBox("无法确定数据目录：\n\n" + error);
            return 2;
        }

        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            // 托盘"重启"场景：新实例稍等旧实例退出后再接管，
            // 避免激活了正在退出的旧窗口导致程序悄悄消失。
            var restartWait = Environment.GetEnvironmentVariable(RestartEnvName) == "1";
            Environment.SetEnvironmentVariable(RestartEnvName, null);

            acquired = mutex.WaitOne(restartWait ? 5000 : 0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;   // 上一个实例异常退出，接管
        }

        if (!acquired)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
                {
                    using (activate) activate.Set();
                }
            }
            catch
            {
                // 激活失败就静默退出（与 v2.0.0 行为一致）
            }

            return 0;
        }

        try
        {
            try
            {
                paths.EnsureLayout();
            }
            catch (Exception ex)
            {
                Cli.ErrorBox($"数据目录不可用：{paths.DataDir}\n\n{ex.Message}");
                return 2;
            }

            CrashLogger.Initialize(paths.LogDir);

            var exePath = AutorunService.ExecutablePath;
            string built;
            try
            {
                built = File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                built = "?";
            }

            new LogService(paths.LogDir).Info(
                $"启动(WinUI) data-dir={paths.DataDir} ({paths.SourceLabel}) exe={exePath} " +
                $"built={built} elevated={elevated}");

            if (paths.FallbackReason is not null)
                LogService.Current?.Warn($"数据目录已从 exe 同目录降级：{paths.FallbackReason}");

            var settings = new SettingsStore(paths.SettingsFile);
            var meta = new MetaStore(paths.MetaFile);

            using var activateEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, ActivateEventName);

            // 已运行实例的窗口激活：后台线程收到事件后切到 UI 线程
            var registration = ThreadPool.RegisterWaitForSingleObject(
                activateEvent,
                (_, _) => AppRuntime.RequestActivate(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);

            try
            {
                AppRuntime.Initialize(paths, meta, settings, elevated, options.Tray);

                global::WinRT.ComWrappersSupport.InitializeComWrappers();
                Microsoft.UI.Xaml.Application.Start(_unused =>
                {
                    _ = new App();
                });

                return 0;
            }
            finally
            {
                registration.Unregister(null);
            }
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* 已释放 */ }
        }
    }
}
