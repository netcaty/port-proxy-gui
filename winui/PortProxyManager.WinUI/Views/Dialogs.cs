using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortProxyManager.Services;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// ContentDialog 的统一包装：旧版 WinForms 的 MessageBox 等价物。
/// WinUI 同一时刻只允许显示一个 ContentDialog，显示前先关掉上一个。
///
/// 注意：MenuFlyout（右键菜单）关闭动画期间弹 ContentDialog 会让 XAML
/// 进程直接崩溃（进程级 faulting，.NET 异常捕获不到）——因此所有显示
/// 都经 DispatcherQueue 延迟一拍执行，且本类方法永不抛出。
/// </summary>
public static class Dialogs
{
    private static ContentDialog? _current;

    public static Task InfoAsync(string title, string message)
    {
        var dialog = Create(title, message, closeText: "确定", primaryText: null, secondaryText: null);
        return ShowAsync(dialog);
    }

    public static Task<bool> ConfirmAsync(string title, string message, bool defaultIsYes = true)
    {
        var dialog = Create(title, message, closeText: "否", primaryText: "是", secondaryText: null);
        dialog.DefaultButton = defaultIsYes ? ContentDialogButton.Primary : ContentDialogButton.Close;
        return ConfirmCoreAsync(dialog);
    }

    private static ContentDialog Create(
        string title, string message, string closeText, string? primaryText, string? secondaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeText,
        };

        if (primaryText is not null) dialog.PrimaryButtonText = primaryText;
        if (secondaryText is not null) dialog.SecondaryButtonText = secondaryText;
        return dialog;
    }

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        // 延迟一拍：等当前 UI 事件（含右键菜单的 flyout 关闭动画）走完再显示
        var tcs = new TaskCompletionSource<ContentDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var queue = AppRuntime.UiQueue;
        if (queue is null) return ContentDialogResult.None;

        var scheduled = queue.TryEnqueue(() =>
        {
            _ = ShowOnUiThreadAsync(dialog, tcs);
        });

        if (!scheduled)
        {
            tcs.SetResult(ContentDialogResult.None);
        }

        return await tcs.Task;
    }

    private static async Task ShowOnUiThreadAsync(ContentDialog dialog, TaskCompletionSource<ContentDialogResult> tcs)
    {
        try
        {
            var window = AppRuntime.MainWindow;
            if (window?.Content?.XamlRoot is not { } root)
            {
                tcs.SetResult(ContentDialogResult.None);
                return;
            }

            dialog.XamlRoot = root;

            // 防重入：上一个对话框还没关闭时先关掉
            if (_current is { } previous)
            {
                try { previous.Hide(); } catch { /* 已关闭 */ }
            }

            _current = dialog;
            try
            {
                tcs.SetResult(await dialog.ShowAsync());
            }
            finally
            {
                _current = null;
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"显示对话框失败：{ex.Message}");
            if (!tcs.Task.IsCompleted) tcs.SetResult(ContentDialogResult.None);
        }
    }

    private static async Task<bool> ConfirmCoreAsync(ContentDialog dialog)
    {
        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary;
    }
}
