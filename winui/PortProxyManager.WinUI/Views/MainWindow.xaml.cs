using System.ComponentModel;
using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PortProxyManager.WinUI.ViewModels;
using Windows.Graphics;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// 主窗口。窗口位置/尺寸/最大化的持久化在这里（旧版 PersistWindowState），
/// 数据与命令在 MainViewModel。
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        Title = "PortProxy 管理器";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        Grid.DataContext = ViewModel;

        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.RightTapped += OnGridRightTapped;
        Grid.KeyDown += OnGridKeyDown;

        // Window 没有 Loaded 事件，挂在根布局上。
        // 窗口位置/尺寸恢复必须在 XamlRoot 就绪后（要用 RasterizationScale 换算物理像素）。
        RootLayout.Loaded += (_, _) =>
        {
            RestoreWindowPlacement();
            AppRuntime.UiScale = GetScaleFactor();
            ViewModel.Initialize();
            RestoreColumnWidths();
        };

        Closed += (_, _) =>
        {
            PersistWindowState();
            PersistColumnWidths();
            ViewModel.Dispose();
        };
    }

    // ---------------------------------------------------------------- 窗口位置与尺寸

    /// <summary>AppWindow 的 Move/Resize 用物理像素；设置里存的是逻辑值，按实际缩放换算。
    /// 用 XamlRoot.RasterizationScale（WinUI 真实渲染缩放）。</summary>
    private double GetScaleFactor() => AppRuntime.GetUiScale(this);

    private void RestoreWindowPlacement()
    {
        var s = AppRuntime.Settings.Value;
        var scale = GetScaleFactor();

        var width = s.WindowWidth;
        var height = s.WindowHeight;
        if (width < 400 || height < 300)
        {
            width = 1120;
            height = 660;
        }

        AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));

        if (s.WindowX != int.MinValue && s.WindowY != int.MinValue &&
            IsOnAnyScreen(s.WindowX, s.WindowY, width, height))
        {
            AppWindow.Move(new PointInt32(
                (int)Math.Round(s.WindowX * scale), (int)Math.Round(s.WindowY * scale)));
        }

        if (s.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private static bool IsOnAnyScreen(int x, int y, int width, int height)
    {
        // 粗略检查：与任一工作区的可见范围相交，避免恢复到已拔掉的显示器坐标
        try
        {
            var target = new RectInt32(x, y, width, height);
            foreach (var area in Microsoft.UI.Windowing.DisplayArea.FindAll())
            {
                var w = area.WorkArea;
                var intersects =
                    target.X < w.X + w.Width && w.X < target.X + target.Width &&
                    target.Y < w.Y + w.Height && w.Y < target.Y + target.Height;
                if (intersects) return true;
            }

            return false;
        }
        catch
        {
            // 枚举显示器偶发失败（WinRT factory 尚未就绪）：保守起见照常恢复
            return true;
        }
    }

    private void PersistWindowState()
    {
        var s = AppRuntime.Settings.Value;

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        var maximized = presenter?.State == OverlappedPresenterState.Maximized;

        if (!maximized)
        {
            // 物理像素按屏 DPI 归一化成逻辑值保存（与旧版 DeviceDpi 处理一致）
            var scale = GetScaleFactor();
            s.WindowWidth = Math.Max(1, (int)Math.Round(AppWindow.Size.Width / scale));
            s.WindowHeight = Math.Max(1, (int)Math.Round(AppWindow.Size.Height / scale));
            s.WindowX = (int)Math.Round(AppWindow.Position.X / scale);
            s.WindowY = (int)Math.Round(AppWindow.Position.Y / scale);
        }

        s.Maximized = maximized;
        AppRuntime.Settings.Save();
    }

    // ---------------------------------------------------------------- 列宽

    private void RestoreColumnWidths()
    {
        var widths = AppRuntime.Settings.Value.ColumnWidths;
        if (widths.Count == 0) return;

        foreach (var column in Grid.Columns)
        {
            if (column.Tag is not string tag) continue;
            if (!widths.TryGetValue(tag, out var width) || width < 24) continue;
            column.Width = new CommunityToolkit.WinUI.UI.Controls.DataGridLength(width);
        }
    }

    private void PersistColumnWidths()
    {
        var widths = AppRuntime.Settings.Value.ColumnWidths;
        widths.Clear();

        foreach (var column in Grid.Columns)
        {
            if (column.Tag is not string tag) continue;
            widths[tag] = (int)Math.Round(column.ActualWidth);
        }

        AppRuntime.Settings.Save();
    }

    // ---------------------------------------------------------------- 表格交互

    /// <summary>表头排序（XAML Sorting 事件）。</summary>
    private void OnGridSorting(object? sender, CommunityToolkit.WinUI.UI.Controls.DataGridColumnEventArgs e)
    {
        if (e.Column.Tag is not string property || property.Length == 0) return;

        ViewModel.SortBy(property);

        foreach (var column in Grid.Columns)
        {
            column.SortDirection = ViewModel.SortProperty == (column.Tag as string)
                ? (ViewModel.IsSortAscending(property)
                    ? DataGridSortDirection.Ascending
                    : DataGridSortDirection.Descending)
                : null;
        }
    }

    /// <summary>双击行 = 编辑；双击空白 = 新增（与旧版一致）。</summary>
    private void OnGridDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = TryGetRow(e.OriginalSource as DependencyObject);
        if (row is not null)
        {
            _ = ViewModel.EditEntryAsync(row);
        }
        else
        {
            _ = ViewModel.AddEntryCommand.ExecuteAsync(null);
        }
    }

    /// <summary>右键：选中命中的行并弹出上下文菜单。</summary>
    private void OnGridRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var row = TryGetRow(e.OriginalSource as DependencyObject);
        if (row is null) return;

        Grid.SelectedItem = row;
        RowMenu.ShowAt(Grid, e.GetPosition(Grid));
    }

    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Delete:
                _ = ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.F5:
                ViewModel.RefreshData();
                e.Handled = true;
                break;
        }
    }

    /// <summary>从事件源向上找到 DataGridRow，取它的 DataContext。</summary>
    private static EntryRowViewModel? TryGetRow(DependencyObject? start)
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is DataGridRow row)
                return row.DataContext as EntryRowViewModel;
        }

        return null;
    }

    // ---------------------------------------------------------------- 单实例激活

    /// <summary>另一个实例通过 Activate 事件请求恢复窗口。</summary>
    public void ActivateFromSecondInstance()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
    }
}
