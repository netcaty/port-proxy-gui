using PortProxyManager.Services;
using PortProxyManager.UI;

namespace PortProxyManager;

internal static class Program
{
    private const string MutexName = @"Local\PortProxyManager.SingleInstance";
    private const string ActivateEventName = @"Local\PortProxyManager.Activate";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUiException(e.Exception);
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

    /// <summary>界面线程的未处理异常：记录 + 提示，但不让进程消失。</summary>
    private static void OnUiException(Exception exception)
    {
        var detail = CrashLogger.Report(exception, "Application.ThreadException");

        try
        {
            var nl = Environment.NewLine;
            MessageBox.Show(
                "程序遇到了一个未处理的错误，已保住当前进程并记录了日志。" + nl + nl +
                "错误：" + exception.Message + nl + nl + detail,
                "PortProxy 管理器 — 内部错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 连对话框都弹不出来时只能依赖日志
        }
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
            acquired = mutex.WaitOne(0);
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
                // 激活失败也不影响提示
            }

            if (!options.Tray)
            {
                Cli.ErrorBox(
                    "PortProxy 管理器已经在运行，已切换到那个窗口。\n\n" +
                    "注意：本次启动参数（例如 --data-dir）不会生效，" +
                    "正在运行的实例使用它自己的数据目录。");
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
                $"启动 data-dir={paths.DataDir} ({paths.SourceLabel}) exe={exePath} " +
                $"built={built} elevated={elevated}");

            if (paths.FallbackReason is not null)
                LogService.Current?.Warn($"数据目录已从 exe 同目录降级：{paths.FallbackReason}");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { /* manifest 已声明 */ }

            var settings = new SettingsStore(paths.SettingsFile);
            var meta = new MetaStore(paths.MetaFile);

            using var activateEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, ActivateEventName);

            using var form = new MainForm(paths, meta, settings, elevated, options.Tray);

            var registration = ThreadPool.RegisterWaitForSingleObject(
                activateEvent,
                (_, _) =>
                {
                    try
                    {
                        if (form.IsDisposed) return;
                        form.BeginInvoke(form.ActivateFromTray);
                    }
                    catch
                    {
                        // 窗口正在关闭
                    }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);

            try
            {
                Application.Run(form);
            }
            finally
            {
                registration.Unregister(null);
            }

            return 0;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* 已释放 */ }
        }
    }
}
