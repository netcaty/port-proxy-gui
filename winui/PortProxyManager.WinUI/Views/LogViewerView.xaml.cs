using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PortProxyManager.WinUI.Views;

/// <summary>日志查看页的轻量状态（文件少、交互简单，不单独建 RelayCommand 集）。</summary>
public sealed partial class LogViewerViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string ContentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;
}

/// <summary>
/// 操作日志页（旧版 LogViewerForm）：按天的日志文件下拉 + 内容展示。
/// 内嵌在主窗口里显示，通过 <see cref="CloseRequested"/> 返回列表。
/// </summary>
public sealed partial class LogViewerView : UserControl
{
    public LogViewerViewModel ViewModel { get; } = new();

    /// <summary>用户点了返回/关闭。</summary>
    public event EventHandler? CloseRequested;

    private readonly string _dir;

    public LogViewerView(string logDir)
    {
        _dir = logDir;

        InitializeComponent();

        Root.DataContext = ViewModel;

        // 每次打开都重建视图，Loaded 时重新扫描日志文件
        Loaded += (_, _) => LoadFiles();
    }

    private void LoadFiles()
    {
        FilesBox.Items.Clear();

        try
        {
            if (!Directory.Exists(_dir))
            {
                ViewModel.StatusText = $"日志目录不存在：{_dir}";
                return;
            }

            var files = Directory.GetFiles(_dir, "app-*.log")
                .OrderByDescending(f => f)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();

            foreach (var file in files) FilesBox.Items.Add(file);

            if (FilesBox.Items.Count > 0)
                FilesBox.SelectedIndex = 0;
            else
                ViewModel.StatusText = "还没有日志文件。";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"读取日志目录失败：{ex.Message}";
        }
    }

    private void LoadFile()
    {
        if (FilesBox.SelectedItem is not string name) return;

        try
        {
            var path = Path.Combine(_dir, name);
            var content = File.ReadAllText(path, System.Text.Encoding.UTF8);

            ViewModel.ContentText = content;
            ViewModel.StatusText = $"{path}　·　{content.Length} 字符";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"读取失败：{ex.Message}";
        }
    }

    private void OnFileSelected(object sender, SelectionChangedEventArgs e) => LoadFile();

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        LoadFiles();
        LoadFile();
    }

    private void OnOpenDirClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
