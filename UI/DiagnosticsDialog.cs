using System.ComponentModel;
using PortProxyManager.Models;
using PortProxyManager.Services;

namespace PortProxyManager.UI;

/// <summary>
/// 单条映射的转发链路诊断结果。窗口自身负责异步执行诊断；
/// 修复动作通过 <see cref="Action"/> 交回主窗体执行，执行后会重新打开本窗口复查。
/// </summary>
public sealed class DiagnosticsDialog : Form
{
    private readonly PortProxyEntry _entry;
    private readonly EntryMeta? _meta;
    private readonly bool _elevated;

    private readonly Label _header = new();
    private readonly Label _summary = new();
    private readonly ListView _list = new();
    private readonly FlowLayoutPanel _actions = new();
    private readonly Button _rerunButton = new();
    private readonly Button _copyButton = new();

    private DiagnosticReport? _report;
    private CancellationTokenSource? _cts;
    private bool _running;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DiagnosticAction Action { get; private set; } = DiagnosticAction.None;

    public DiagnosticsDialog(PortProxyEntry entry, EntryMeta? meta, bool elevated)
    {
        _entry = entry;
        _meta = meta;
        _elevated = elevated;

        BuildUi();

        Shown += async (_, _) => await RunDiagnosticsAsync();
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private void BuildUi()
    {
        Text = "诊断转发链路";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9f);

        _header.Dock = DockStyle.Top;
        _header.Height = 54;
        _header.Padding = new Padding(14, 10, 14, 4);
        _header.TextAlign = ContentAlignment.MiddleLeft;
        _header.AutoEllipsis = true;
        _header.Text = BuildHeaderText();

        _summary.Dock = DockStyle.Top;
        _summary.Height = 28;
        _summary.Padding = new Padding(14, 0, 14, 4);
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Text = "准备诊断…";
        _summary.ForeColor = SystemColors.GrayText;

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.HideSelection = false;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.ShowItemToolTips = true;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("结果", 64);
        _list.Columns.Add("检查项", 128);
        _list.Columns.Add("说明", 500);
        _list.Resize += (_, _) => FitColumns();

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            ColumnCount = 2,
            Padding = new Padding(12, 8, 12, 8),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _actions.Dock = DockStyle.Fill;
        _actions.FlowDirection = FlowDirection.LeftToRight;
        _actions.WrapContents = false;
        _actions.AutoScroll = true;
        _actions.Margin = Padding.Empty;

        var right = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
        };

        var close = new Button
        {
            Text = "关闭",
            AutoSize = true,
            MinimumSize = new Size(88, 28),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        _copyButton.Text = "复制报告";
        _copyButton.AutoSize = true;
        _copyButton.MinimumSize = new Size(88, 28);
        _copyButton.FlatStyle = FlatStyle.System;
        _copyButton.Enabled = false;
        _copyButton.Click += (_, _) => CopyReport();

        _rerunButton.Text = "重新诊断";
        _rerunButton.AutoSize = true;
        _rerunButton.MinimumSize = new Size(88, 28);
        _rerunButton.FlatStyle = FlatStyle.System;
        _rerunButton.Click += async (_, _) => await RunDiagnosticsAsync();

        right.Controls.Add(close);
        right.Controls.Add(_copyButton);
        right.Controls.Add(_rerunButton);

        bottom.Controls.Add(_actions, 0, 0);
        bottom.Controls.Add(right, 1, 0);

        // Fill 先加，Top 后加才会落在最上面
        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(_summary);
        Controls.Add(_header);

        CancelButton = close;
    }

    protected override void OnLoad(EventArgs e)
    {
        DpiScale.Apply(this, 760, 500, 600, 400);
        FitColumns();
        base.OnLoad(e);
    }

    private string BuildHeaderText()
    {
        var name = string.IsNullOrWhiteSpace(_meta?.Name) ? "（未命名）" : _meta.Name;
        return $"{name}　{_entry.Family}/{_entry.Protocol}　{_entry.ListenText} → {_entry.ConnectText}";
    }

    private async Task RunDiagnosticsAsync()
    {
        if (_running) return;

        _running = true;
        _rerunButton.Enabled = false;
        _copyButton.Enabled = false;
        _actions.Controls.Clear();
        _list.Items.Clear();
        _summary.Text = "正在诊断…";
        _summary.ForeColor = SystemColors.GrayText;
        Cursor = Cursors.WaitCursor;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var report = await DiagnosticsService.RunAsync(_entry, _meta, _elevated, _cts.Token);

            if (IsDisposed) return;

            _report = report;
            RenderReport(report);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或重新诊断
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"诊断失败：{ex}");
            if (!IsDisposed)
            {
                _summary.Text = "诊断失败：" + ex.Message;
                _summary.ForeColor = Color.FromArgb(0xB7, 0x1C, 0x1C);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _running = false;
                _rerunButton.Enabled = true;
                _copyButton.Enabled = _report is not null;
                Cursor = Cursors.Default;
            }
        }
    }

    private void RenderReport(DiagnosticReport report)
    {
        _summary.Text =
            $"通过 {report.PassCount}，警告 {report.WarnCount}，失败 {report.FailCount}，跳过 {report.SkipCount}" +
            (_elevated ? string.Empty : "。部分修复需要管理员权限。");

        _summary.ForeColor = report.FailCount > 0
            ? Color.FromArgb(0xB7, 0x1C, 0x1C)
            : report.WarnCount > 0
                ? Color.FromArgb(0x8A, 0x5A, 0x00)
                : Color.FromArgb(0x1B, 0x5E, 0x20);

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            foreach (var check in report.Checks)
            {
                var item = new ListViewItem(check.StatusLabel)
                {
                    ForeColor = StatusColor(check.Status),
                    ToolTipText = check.Detail,
                };

                item.SubItems.Add(check.Name);
                item.SubItems.Add(check.Detail);
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _actions.Controls.Clear();

        foreach (var check in report.Checks
                     .Where(c => c.Action != DiagnosticAction.None)
                     .GroupBy(c => c.Action)
                     .Select(g => g.First()))
        {
            var action = check.Action;
            var button = new Button
            {
                Text = check.ActionLabel ?? ActionText(action),
                AutoSize = true,
                MinimumSize = new Size(96, 28),
                FlatStyle = FlatStyle.System,
            };

            button.Click += (_, _) =>
            {
                Action = action;
                DialogResult = DialogResult.OK;
                Close();
            };

            _actions.Controls.Add(button);
        }

        FitColumns();
    }

    private void CopyReport()
    {
        if (_report is null) return;

        try
        {
            Clipboard.SetText(_report.ToText());
            _summary.Text = "报告已复制到剪贴板。";
            _summary.ForeColor = SystemColors.GrayText;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败：{ex.Message}", "诊断", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void FitColumns()
    {
        if (_list.Columns.Count < 3 || _list.IsDisposed) return;

        var statusWidth = DpiScale.Value(this, 64);
        var nameWidth = DpiScale.Value(this, 128);

        _list.Columns[0].Width = statusWidth;
        _list.Columns[1].Width = nameWidth;
        _list.Columns[2].Width = Math.Max(
            DpiScale.Value(this, 200),
            _list.ClientSize.Width - statusWidth - nameWidth - DpiScale.Value(this, 8));
    }

    private static Color StatusColor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Pass => Color.FromArgb(0x1B, 0x5E, 0x20),
        DiagnosticStatus.Warn => Color.FromArgb(0x8A, 0x5A, 0x00),
        DiagnosticStatus.Fail => Color.FromArgb(0xB7, 0x1C, 0x1C),
        _ => SystemColors.GrayText,
    };

    private static string ActionText(DiagnosticAction action) => action switch
    {
        DiagnosticAction.StartIpHelper => "启动 IP Helper",
        DiagnosticAction.RestartIpHelper => "重启 IP Helper",
        DiagnosticAction.RebuildMapping => "重建映射",
        DiagnosticAction.AddFirewallRule => "补充防火墙规则",
        _ => "修复",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        base.Dispose(disposing);
    }
}
