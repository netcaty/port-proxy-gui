using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PortProxyManager.Services;

namespace PortProxyManager.WinUI.ViewModels;

/// <summary>导入预览的行投影：diff 数据 + 勾选状态 + 类型着色。</summary>
public sealed partial class DiffItemRow : ObservableObject
{
    private readonly Action? _onChanged;

    public DiffItemRow(DiffItem item, Action? onChanged = null)
    {
        Item = item;
        _onChanged = onChanged;
        Brush = item.Kind switch
        {
            DiffKind.Delete => MainViewModel.HelperBadBrush,
            DiffKind.Update => MainViewModel.HelperWarnBrush,
            DiffKind.Add => MainViewModel.HelperOkBrush,
            _ => MainViewModel.HelperMutedBrush,
        };
    }

    public DiffItem Item { get; }

    public string KindLabel => Item.KindLabel;
    public string Family => Item.Entry.Family;
    public string ListenText => Item.Entry.ListenText;
    public string ConnectText => Item.Entry.ConnectText;

    /// <summary>当前（未修改时）的连接方式；无变化/新增行为空。</summary>
    public string CurrentText =>
        Item.Kind == DiffKind.Update
            ? $"{Item.Current!.Family} {Item.Current!.ListenText} → {Item.Current!.ConnectText}"
            : string.Empty;

    public Visibility CurrentVisibility =>
        string.IsNullOrEmpty(CurrentText) ? Visibility.Collapsed : Visibility.Visible;

    public string Name => Item.Meta?.Name ?? string.Empty;
    public string Note => Item.Meta?.Note ?? string.Empty;
    public Microsoft.UI.Xaml.Media.Brush Brush { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    partial void OnIsSelectedChanged(bool value) => _onChanged?.Invoke();
}

/// <summary>
/// 导入差异预览。用户逐条勾选要应用的条目，确认后交回选中列表（旧版 ImportPreviewDialog）。
/// </summary>
public partial class ImportPreviewViewModel : ObservableObject
{
    public event Action? CloseRequested;
    public bool Confirmed { get; private set; }

    public ObservableCollection<DiffItemRow> Rows { get; } = new();

    [ObservableProperty]
    public partial bool SelectAll { get; set; } = true;

    public string Title => "导入预览";
    public string ConfirmText => "应用所选";

    public ImportPreviewViewModel(IReadOnlyList<DiffItem> diff)
    {
        foreach (var item in diff) Rows.Add(new DiffItemRow(item, NotifyRowChanged));
        RecomputeSummary();
    }

    public string SummaryText
    {
        get
        {
            int add = 0, update = 0, del = 0, same = 0;
            foreach (var row in Rows.Where(r => r.IsSelected))
            {
                switch (row.Item.Kind)
                {
                    case DiffKind.Add: add++; break;
                    case DiffKind.Update: update++; break;
                    case DiffKind.Delete: del++; break;
                    default: same++; break;
                }
            }

            return $"将执行：新增 {add}，覆盖 {update}，删除 {del}，同步备注 {same}";
        }
    }

    public Microsoft.UI.Xaml.Media.Brush DeleteBrush => MainViewModel.HelperBadBrush;

    public Visibility DeleteWarningVisibility =>
        Rows.Any(r => r.IsSelected && r.Item.Kind == DiffKind.Delete)
            ? Visibility.Visible
            : Visibility.Collapsed;

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var row in Rows) row.IsSelected = value;
        RecomputeSummary();
    }

    /// <summary>行勾选变化时重算汇总（行对象回调这里）。</summary>
    public void NotifyRowChanged()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DeleteWarningVisibility));
    }

    private void RecomputeSummary()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DeleteWarningVisibility));
    }

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
