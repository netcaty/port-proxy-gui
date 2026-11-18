using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortProxyManager.Models;
using PortProxyManager.Services;

namespace PortProxyManager.WinUI.ViewModels;

/// <summary>诊断列表行投影（状态着色）。</summary>
public sealed class DiagnosticCheckRow
{
    public DiagnosticCheckRow(DiagnosticCheck check)
    {
        Check = check;
        Brush = check.Status switch
        {
            DiagnosticStatus.Pass => MainViewModel.HelperOkBrush,
            DiagnosticStatus.Warn => MainViewModel.HelperWarnBrush,
            DiagnosticStatus.Fail => MainViewModel.HelperBadBrush,
            _ => MainViewModel.HelperMutedBrush,
        };
    }

    public DiagnosticCheck Check { get; }
    public string StatusLabel => Check.StatusLabel;
    public string Name => Check.Name;
    public string Detail => Check.Detail;
    public Microsoft.UI.Xaml.Media.Brush Brush { get; }
}

/// <summary>修复按钮行（命令随行携带，供 DataTemplate 内 x:Bind）。</summary>
public sealed class FixActionRow
{
    public FixActionRow(DiagnosticAction action, string label, CommunityToolkit.Mvvm.Input.IRelayCommand runFixCommand)
    {
        Action = action;
        Label = label;
        RunFixCommand = runFixCommand;
    }

    public DiagnosticAction Action { get; }
    public string Label { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand RunFixCommand { get; }
}

/// <summary>
/// 诊断窗口逻辑。旧版 DiagnosticsDialog：异步执行 10 项检查，
/// 失败项生成修复按钮，动作交回主窗口执行后复查。
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly PortProxyEntry _entry;
    private readonly EntryMeta? _meta;
    private readonly bool _elevated;
    private CancellationTokenSource? _cts;
    private bool _running;
    private DiagnosticReport? _report;

    /// <summary>用户点了修复按钮：动作交回主窗口执行（诊断页随后关闭）。</summary>
    public DiagnosticAction ReportedAction { get; private set; } = DiagnosticAction.None;

    public string Title => "诊断转发链路";
    public string HeaderText { get; }

    public ObservableCollection<DiagnosticCheckRow> Checks { get; } = new();

    /// <summary>失败项对应的修复动作（去重后）。</summary>
    public ObservableCollection<DiagnosticAction> FixActions { get; } = new();

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "准备诊断…";

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Brush SummaryBrush { get; set; } = MainViewModel.HelperMutedBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    public partial bool IsRunning { get; set; }

    public bool NotRunning => !IsRunning;

    public bool HasReport => _report is not null;

    public event Action? CloseRequested;

    public DiagnosticsViewModel(PortProxyEntry entry, EntryMeta? meta, bool elevated)
    {
        _entry = entry;
        _meta = meta;
        _elevated = elevated;

        var name = string.IsNullOrWhiteSpace(meta?.Name) ? "（未命名）" : meta!.Name;
        HeaderText = $"{name}　{entry.Family}/{entry.Protocol}　{entry.ListenText} → {entry.ConnectText}";
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>诊断服务的深链 await 之后续体可能不在 UI 线程；
    /// x:Bind 的 PropertyChanged 不做封送，所有 UI 更新必须显式切回。</summary>
    private void Marshal(Action action)
    {
        var queue = AppRuntime.UiQueue;
        if (queue is null)
        {
            action();
            return;
        }

        queue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogService.Current?.Warn($"诊断窗口 UI 更新失败：{ex.Message}");
            }
        });
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        if (_running) return;

        _running = true;
        IsRunning = true;
        FixActionButtons.Clear();
        Checks.Clear();
        SummaryText = "正在诊断…";
        SummaryBrush = MainViewModel.HelperMutedBrush;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var report = await DiagnosticsService.RunAsync(_entry, _meta, _elevated, _cts.Token);
            _report = report;
            Marshal(() => RenderReport(report));
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或重新诊断
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"诊断失败：{ex}");
            Marshal(() =>
            {
                SummaryText = "诊断失败：" + ex.Message;
                SummaryBrush = MainViewModel.HelperBadBrush;
            });
        }
        finally
        {
            _running = false;
            Marshal(() => IsRunning = false);
        }
    }

    private void RenderReport(DiagnosticReport report)
    {
        OnPropertyChanged(nameof(HasReport));

        SummaryText =
            $"通过 {report.PassCount}，警告 {report.WarnCount}，失败 {report.FailCount}，跳过 {report.SkipCount}" +
            (_elevated ? string.Empty : "。部分修复需要管理员权限。");
        SummaryBrush = report.FailCount > 0
            ? MainViewModel.HelperBadBrush
            : report.WarnCount > 0
                ? MainViewModel.HelperWarnBrush
                : MainViewModel.HelperOkBrush;

        Checks.Clear();
        foreach (var check in report.Checks)
            Checks.Add(new DiagnosticCheckRow(check));

        FixActionButtons.Clear();
        foreach (var check in report.Checks
                     .Where(c => c.Action != DiagnosticAction.None)
                     .GroupBy(c => c.Action)
                     .Select(g => g.First()))
            FixActionButtons.Add(new FixActionRow(
                check.Action, check.ActionLabel ?? ActionText(check.Action), RunFixCommand));
    }

    /// <summary>修复按钮的展示文本（按动作去重）。</summary>
    public ObservableCollection<FixActionRow> FixActionButtons { get; } = new();

    private static string ActionText(DiagnosticAction action) => action switch
    {
        DiagnosticAction.StartIpHelper => "启动 IP Helper",
        DiagnosticAction.RestartIpHelper => "重启 IP Helper",
        DiagnosticAction.RebuildMapping => "重建映射",
        DiagnosticAction.AddFirewallRule => "补充防火墙规则",
        _ => "修复",
    };

    [RelayCommand]
    private void RunFix(DiagnosticAction action)
    {
        if (action == DiagnosticAction.None) return;
        ReportedAction = action;
        CloseRequested?.Invoke();
    }

    /// <summary>用户直接关闭诊断页：无修复动作，返回列表。</summary>
    [RelayCommand]
    private void Close()
    {
        ReportedAction = DiagnosticAction.None;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (_report is null) return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(_report.ToText());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            SummaryText = "报告已复制到剪贴板。";
            SummaryBrush = MainViewModel.HelperMutedBrush;
        }
        catch (Exception ex)
        {
            SummaryText = "复制失败：" + ex.Message;
            SummaryBrush = MainViewModel.HelperBadBrush;
        }
    }
}
