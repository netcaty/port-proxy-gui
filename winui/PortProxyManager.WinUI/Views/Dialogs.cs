using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// ContentDialog 的统一包装：旧版 WinForms 的 MessageBox 等价物。
/// WinUI 同一时刻只允许显示一个 ContentDialog，显示前先关掉上一个。
/// </summary>
public static class Dialogs
{
    private static ContentDialog? _current;

    public static async Task InfoAsync(string title, string message)
    {
        var dialog = Create(title, message, closeText: "确定", primaryText: null, secondaryText: null);
        await ShowAsync(dialog);
    }

    public static async Task<bool> ConfirmAsync(string title, string message, bool defaultIsYes = true)
    {
        var dialog = Create(
            title, message,
            closeText: "否", primaryText: "是", secondaryText: null);
        dialog.DefaultButton = defaultIsYes ? ContentDialogButton.Primary : ContentDialogButton.Close;

        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary;
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
        var window = AppRuntime.MainWindow;
        if (window?.Content?.XamlRoot is not { } root)
            return ContentDialogResult.None;

        dialog.XamlRoot = root;

        // 防重入：上一个对话框还没关闭时先关掉
        if (_current is { } previous)
        {
            try { previous.Hide(); } catch { /* 已关闭 */ }
        }

        _current = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _current = null;
        }
    }
}
