using Microsoft.UI.Xaml;
using PortProxyManager.Services;
using PortProxyManager.WinUI.ViewModels;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// 导入差异预览窗口。ShowAsync 返回用户勾选的条目；取消时返回 null。
/// </summary>
public sealed partial class ImportPreviewWindow : Window
{
    public ImportPreviewViewModel ViewModel { get; }

    private ImportPreviewWindow(IReadOnlyList<DiffItem> diff)
    {
        InitializeComponent();

        ViewModel = new ImportPreviewViewModel(diff);
        Root.DataContext = ViewModel;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        ViewModel.CloseRequested += () => Close();

        Root.Loaded += (_, _) => AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(760 * AppRuntime.UiScale),
            (int)Math.Round(560 * AppRuntime.UiScale)));
    }

    public static async Task<IReadOnlyList<DiffItem>?> ShowAsync(IReadOnlyList<DiffItem> diff)
    {
        var window = new ImportPreviewWindow(diff);

        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(true);
        window.Activate();

        await closed.Task;

        return window.ViewModel.Confirmed
            ? window.ViewModel.Rows.Where(r => r.IsSelected).Select(r => r.Item).ToList()
            : null;
    }
}
