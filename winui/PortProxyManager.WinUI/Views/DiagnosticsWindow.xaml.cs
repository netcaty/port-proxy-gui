using Microsoft.UI.Xaml;
using PortProxyManager.Models;
using PortProxyManager.WinUI.ViewModels;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// 诊断窗口。ShowAsync 返回用户选择的修复动作（None = 关闭/无动作）；
/// 主窗口执行动作后可再次打开本窗口复查（旧版 DiagnoseEntry 复查循环）。
/// </summary>
public sealed partial class DiagnosticsWindow : Window
{
    public DiagnosticsViewModel ViewModel { get; }

    public DiagnosticsWindow(PortProxyEntry entry, EntryMeta? meta, bool elevated)
    {
        InitializeComponent();

        ViewModel = new DiagnosticsViewModel(entry, meta, elevated);
        Root.DataContext = ViewModel;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        ViewModel.CloseRequested += () => Close();
        Closed += (_, _) => ViewModel.Cancel();

        // Window 没有 Loaded 事件，挂在根布局上
        Root.Loaded += async (_, _) =>
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(780 * AppRuntime.UiScale),
                (int)Math.Round(520 * AppRuntime.UiScale)));

            await ViewModel.RunDiagnosticsAsync();
        };
    }

    private void OnCloseClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.ReportNone();
        Close();
    }

    public static async Task<PortProxyManager.Services.DiagnosticAction> ShowAsync(
        PortProxyEntry entry, EntryMeta? meta, bool elevated)
    {
        var window = new DiagnosticsWindow(entry, meta, elevated);

        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(true);
        window.Activate();

        await closed.Task;

        return window.ViewModel.ReportedAction;
    }
}
