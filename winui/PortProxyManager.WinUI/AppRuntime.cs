using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PortProxyManager.Services;
using PortProxyManager.WinUI.Views;

namespace PortProxyManager.WinUI;

/// <summary>
/// 运行时上下文：Main 里组装好的服务实例 + 启动参数，供 ViewModel / 窗口使用。
/// WinUI 的入口是 Application.Start，构造参数不方便层层下传，集中放这里。
/// </summary>
public static class AppRuntime
{
    public static PathContext Paths { get; private set; } = null!;
    public static MetaStore Meta { get; private set; } = null!;
    public static SettingsStore Settings { get; private set; } = null!;
    public static bool Elevated { get; private set; }
    public static bool StartInTray { get; private set; }

    /// <summary>UI 线程队列。OnLaunched 时赋值；跨线程回调都经它切换。</summary>
    public static DispatcherQueue? UiQueue { get; private set; }

    public static MainWindow? MainWindow { get; set; }

    public static void Initialize(
        PathContext paths, MetaStore meta, SettingsStore settings,
        bool elevated, bool startInTray)
    {
        Paths = paths;
        Meta = meta;
        Settings = settings;
        Elevated = elevated;
        StartInTray = startInTray;
    }

    public static void SetUiQueue(DispatcherQueue queue) => UiQueue = queue;

    /// <summary>AppWindow 的 Move/Resize 用物理像素；布局尺寸按逻辑值计算，
    /// 这里取窗口的真实渲染缩放（远程会话里 DisplayArea.Dpi 可能失真）。</summary>
    public static double GetUiScale(Window window)
    {
        try
        {
            var scale = window.Content?.XamlRoot?.RasterizationScale ?? 0;
            if (scale > 0.01) return scale;
        }
        catch
        {
            // XamlRoot 未就绪
        }

        return 1.0;
    }

    /// <summary>
    /// 主窗口测得的渲染缩放。主窗口 Loaded 时写入；后开的窗口（编辑窗口）
    /// 在自己的 XamlRoot 就绪前可以直接复用这个值换算物理像素。
    /// </summary>
    public static double UiScale { get; set; } = 1.0;

    /// <summary>单实例激活：已有实例收到 Activate 事件后切到 UI 线程恢复窗口。</summary>
    public static void RequestActivate()
    {
        var queue = UiQueue;
        if (queue is null) return;

        queue.TryEnqueue(() =>
        {
            try
            {
                MainWindow?.ActivateFromSecondInstance();
            }
            catch
            {
                // 窗口正在关闭
            }
        });
    }
}
