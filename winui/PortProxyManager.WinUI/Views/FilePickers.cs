using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// 文件选择器。未打包应用必须用 InitializeWithWindow 绑定窗口句柄。
/// </summary>
public static class FilePickers
{
    public static async Task<string?> SaveJsonAsync(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        picker.FileTypeChoices.Add("All", new List<string> { "." });

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> OpenJsonAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
