using Microsoft.UI.Xaml;
using PortProxyManager.Models;
using PortProxyManager.WinUI.ViewModels;
using Windows.Graphics;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// 新增/编辑映射窗口。WinUI 没有模态 ShowDialog：用非模态窗口 + 结果回传，
/// 同一时刻只允许一个编辑窗口（旧版是模态对话框，行为差异见 README）。
/// </summary>
public sealed partial class EntryEditWindow : Window
{
    private static EntryEditWindow? _open;

    public EntryEditViewModel ViewModel { get; }

    private EntryEditWindow(
        PortProxyEntry? existing, EntryMeta? meta, IReadOnlyList<PortProxyEntry> allEntries)
    {
        InitializeComponent();

        ViewModel = new EntryEditViewModel(existing, meta, allEntries);
        Root.DataContext = ViewModel;

        Title = ViewModel.Title;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        ViewModel.CloseRequested += () => Close();

        // Window 没有 Loaded 事件，挂在根布局上；
        // Resize 用物理像素，逻辑尺寸按主窗口测得的渲染缩放换算
        Root.Loaded += (_, _) =>
        {
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(600 * AppRuntime.UiScale),
                (int)Math.Round(640 * AppRuntime.UiScale)));

            ViewModel.LoadValues();
            ViewModel.RefreshPreview();
        };

        Closed += (_, _) => _open = null;
    }

    /// <summary>
    /// 打开编辑窗口并等待结果。若已有编辑窗口在开着，把它带到前台并返回未确认，
    /// 避免同时编辑两条映射造成 meta 写入竞争。
    /// </summary>
    public static async Task<(bool Confirmed, PortProxyEntry? Entry, EntryMeta? Meta, bool CreateFirewall)> ShowAsync(
        PortProxyEntry? existing, EntryMeta? meta, IReadOnlyList<PortProxyEntry> allEntries)
    {
        if (_open is not null)
        {
            _open.Activate();
            return (false, null, null, false);
        }

        var window = new EntryEditWindow(existing, meta, allEntries);
        _open = window;

        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(true);
        window.Activate();

        await closed.Task;

        return (window.ViewModel.Confirmed, window.ViewModel.Result, window.ViewModel.ResultMeta, window.ViewModel.CreateFirewallRule);
    }
}
