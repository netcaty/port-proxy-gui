using System.ComponentModel;
using System.Runtime.InteropServices;
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
/// 数据与命令在 MainViewModel。托盘常驻、关闭进托盘（旧版 HideToTray/ActivateFromTray）。
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    public MainViewModel ViewModel { get; } = new();

    private bool _forceExit;
    private bool _trayTipShown;

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

        // 新增/编辑与日志都改为窗口内页面，由 ViewModel 发出显示/隐藏请求
        ViewModel.EditorRequested += ShowEditor;
        ViewModel.EditorDismissed += HideEditor;
        ViewModel.LogRequested += ShowLogViewer;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 关闭进托盘：真退出时放行（托盘"退出"/提权重启走 ForceExit）
        AppWindow.Closing += (s, e) =>
        {
            if (!_forceExit && AppRuntime.Settings.Value.CloseToTray)
            {
                e.Cancel = true;
                HideToTray(showTip: true);
            }
            else
            {
                TrayIcon.Dispose();
            }
        };

        // Window 没有 Loaded 事件，挂在根布局上。
        // 窗口位置/尺寸恢复必须在 XamlRoot 就绪后（要用 RasterizationScale 换算物理像素）。
        RootLayout.Loaded += (_, _) =>
        {
            RestoreWindowPlacement();
            ApplyMinimumSize();
            AppRuntime.UiScale = GetScaleFactor();
            ViewModel.Initialize();
            RestoreColumnWidths();
            _trayTipShown = AppRuntime.Settings.Value.TrayTipShown;

            // --tray：启动后直接进托盘（旧版用一次性定时器错开避免闪烁）
            if (AppRuntime.StartInTray)
                HideToTray(showTip: false);
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

    // ---------------------------------------------------------------- 窗口内页面

    /// <summary>显示新增/编辑页（不再单独开窗），主列表暂时隐藏。</summary>
    private void ShowEditor(EntryEditViewModel viewModel)
    {
        HideLogViewer();

        var view = new EntryEditView(viewModel);
        EditorHost.Children.Clear();
        EditorHost.Children.Add(view);

        ListPage.Visibility = Visibility.Collapsed;
        EditorHost.Visibility = Visibility.Visible;
    }

    private void HideEditor()
    {
        if (EditorHost.Visibility != Visibility.Visible) return;

        EditorHost.Visibility = Visibility.Collapsed;
        EditorHost.Children.Clear();
        ListPage.Visibility = Visibility.Visible;
    }

    /// <summary>显示日志页（不再单独开窗）。</summary>
    private void ShowLogViewer(string logDir)
    {
        if (LogHost.Visibility == Visibility.Visible) return;

        HideEditor();

        var view = new LogViewerView(logDir);
        view.CloseRequested += (_, _) => HideLogViewer();

        LogHost.Children.Clear();
        LogHost.Children.Add(view);

        ListPage.Visibility = Visibility.Collapsed;
        LogHost.Visibility = Visibility.Visible;
    }

    private void HideLogViewer()
    {
        if (LogHost.Visibility != Visibility.Visible) return;

        LogHost.Visibility = Visibility.Collapsed;
        LogHost.Children.Clear();
        ListPage.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- 单实例激活

    // ---------------------------------------------------------------- 托盘与窗口生命周期

    private void ApplyMinimumSize()
    {
        // 旧版最小尺寸 820x460（逻辑值）
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            try
            {
                var scale = GetScaleFactor();
                presenter.PreferredMinimumWidth = (int)Math.Round(820 * scale);
                presenter.PreferredMinimumHeight = (int)Math.Round(460 * scale);
            }
            catch
            {
                // 旧版 API 不可用时忽略（窗口尺寸由持久化值保证）
            }
        }
    }

    /// <summary>托盘双击走 DoubleClickCommand（VM.TrayDoubleClickCommand）。</summary>

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ActivateFromSecondInstance();

    private void OnTrayRefresh(object sender, RoutedEventArgs e) => ViewModel.RefreshData();

    private void OnTraySyncFollow(object sender, RoutedEventArgs e) =>
        _ = ViewModel.SyncFollowAllAsync();

    private void OnTrayOpenDataDir(object sender, RoutedEventArgs e) =>
        ViewModel.OpenDataDirCommand.Execute(null);

    private void OnTrayToggleAutostart(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleAutostartCommand.Execute(null);
        UpdateTrayAutostartLabel();
    }

    private void OnTrayRestart(object sender, RoutedEventArgs e) => ViewModel.RestartFromTray();

    private void OnTrayExit(object sender, RoutedEventArgs e) => ForceExit();

    /// <summary>托盘"退出"/提权重启：跳过"关闭进托盘"拦截，真正退出。</summary>
    public void ForceExit()
    {
        _forceExit = true;
        Close();
    }

    /// <summary>最小化到托盘。首次给一条气泡提示（旧版 TrayTipShown）。</summary>
    private void HideToTray(bool showTip)
    {
        AppWindow.Hide();

        if (!showTip || _trayTipShown) return;

        TrayIcon.ShowNotification("PortProxy 管理器", "已最小化到托盘，单击图标恢复窗口。");
        _trayTipShown = true;
        AppRuntime.Settings.Value.TrayTipShown = true;
        AppRuntime.Settings.Save();
    }

    /// <summary>另一个实例通过 Activate 事件请求恢复窗口：还原 + 抢前台焦点（旧版 af0c411）。</summary>
    public void ActivateFromSecondInstance()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            ShowWindow(handle, SwRestore);
        }

        AppWindow.Show();
        Activate();
        SetForegroundWindow(handle);
    }

    /// <summary>托盘菜单的自启文案跟随实际状态（RefreshData 后更新）。</summary>
    public void UpdateTrayAutostartLabel()
    {
        TrayAutostartItem.Text = ViewModel.AutostartChecked ? "开机自启 ✓" : "开机自启";
    }
}
