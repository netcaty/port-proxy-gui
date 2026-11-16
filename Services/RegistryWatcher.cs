using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PortProxyManager.Services;

/// <summary>
/// 用 RegNotifyChangeKeyValue 监听 PortProxy 键，别人在 cmd 里改映射时界面自动刷新，
/// 不需要定时轮询。键暂时不存在时退化为 2 秒一次的打开重试。
/// </summary>
public sealed class RegistryWatcher : IDisposable
{
    private const int RegNotifyChangeName = 0x1;
    private const int RegNotifyChangeLastSet = 0x4;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        bool watchSubtree,
        int notifyFilter,
        IntPtr hEvent,
        bool asynchronous);

    private readonly AutoResetEvent _changed = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;

    /// <summary>注册表变更后触发（已在后台线程，UI 需自行切回主线程）。</summary>
    public event Action? Changed;

    public RegistryWatcher()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "PortProxyRegistryWatcher",
        };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            RegistryKey? key = null;
            try
            {
                key = Registry.LocalMachine.OpenSubKey(
                    RegistryReader.RootPath,
                    RegistryKeyPermissionCheck.ReadSubTree,
                    RegistryRights.ReadKey | RegistryRights.Notify);

                if (key is null)
                {
                    // 键还不存在（从未配置过）。睡一会儿再试，顺便相当于低频轮询。
                    if (_stop.WaitOne(2000)) return;
                    continue;
                }

                _changed.Reset();

                var rc = RegNotifyChangeKeyValue(
                    key.Handle,
                    watchSubtree: true,
                    notifyFilter: RegNotifyChangeName | RegNotifyChangeLastSet,
                    hEvent: _changed.SafeWaitHandle.DangerousGetHandle(),
                    asynchronous: true);

                if (rc != 0)
                {
                    if (_stop.WaitOne(1000)) return;
                    continue;
                }

                var index = WaitHandle.WaitAny(new WaitHandle[] { _changed, _stop }, 5000);
                if (index == WaitHandle.WaitTimeout) continue;   // 超时只是重新订阅
                if (index == 1) return;                          // 收到停止信号

                // 一次 netsh 写操作可能触发多次通知，合并一下
                Thread.Sleep(250);
                try
                {
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    LogService.Current?.Error($"注册表变更回调异常：{ex}");
                }
            }
            catch (Exception ex)
            {
                LogService.Current?.Warn($"注册表监听中断，稍后重试：{ex.Message}");
                if (_stop.WaitOne(2000)) return;
            }
            finally
            {
                key?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Set();
        _changed.Dispose();
    }
}
