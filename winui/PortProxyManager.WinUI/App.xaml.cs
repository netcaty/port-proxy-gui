using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortProxyManager.Services;
using PortProxyManager.WinUI.Views;

namespace PortProxyManager.WinUI;

/// <summary>
/// 应用入口。真正的启动管道（CLI 分发/自提权/单实例）在 Program.Main 里，
/// 走到这里时服务上下文（AppRuntime）已就绪。
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnXamlUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppRuntime.SetUiQueue(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        var window = new MainWindow();
        AppRuntime.MainWindow = window;
        window.Activate();
    }

    /// <summary>XAML 层的未处理异常：记日志并提示，不让进程消失。</summary>
    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var detail = CrashLogger.Report(e.Exception, "Application.UnhandledException");

        try
        {
            var window = AppRuntime.MainWindow;
            if (window is null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = window.Content.XamlRoot,
                Title = "内部错误",
                Content = "程序遇到了一个未处理的错误，已记录日志。\n\n" +
                          "错误：" + e.Message + "\n\n" + detail,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            _ = dialog.ShowAsync();
        }
        catch
        {
            // 连对话框都弹不出来时只能依赖日志
        }
    }
}
