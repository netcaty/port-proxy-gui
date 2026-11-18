using System.ComponentModel;
using System.Net.NetworkInformation;
using PortProxyManager.Models;
using PortProxyManager.Services;

namespace PortProxyManager.UI;

public sealed class MainForm : Form
{
    private readonly PathContext _paths;
    private readonly MetaStore _meta;
    private readonly SettingsStore _settings;
    private readonly PortProxyService _service = new();
    private readonly bool _elevated;
    private readonly Icon _icon;

    private readonly DataGridView _grid = new();
    private readonly ToolStrip _toolbar = new();
    private readonly Panel _banner = new();
    private readonly Label _bannerLabel = new();
    private readonly Button _bannerButton = new();
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly ToolStripStatusLabel _helperLabel = new();
    private readonly ToolStripStatusLabel _dirLabel = new();
    private readonly ToolStripTextBox _filterBox = new();
    private readonly ToolStripComboBox _groupBox = new();
    private readonly ToolStripButton _editButton = new("编辑");
    private readonly ToolStripButton _deleteButton = new("删除");
    private readonly ToolStripButton _diagnoseButton = new("诊断");
    private readonly ToolStripButton _autostartButton = new("开机自启");
    private readonly ToolTip _tooltip = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ContextMenuStrip _rowMenu = new();
    private ToolStripMenuItem _rowMenuEdit = null!;
    private ToolStripMenuItem _rowMenuDelete = null!;
    private ToolStripMenuItem _rowMenuCopy = null!;
    private ToolStripMenuItem _rowMenuProbe = null!;
    private ToolStripMenuItem _rowMenuFollow = null!;
    private ToolStripMenuItem _rowMenuDiagnose = null!;
    private ToolStripMenuItem _trayAutostart = null!;
    private EntryRow? _contextRow;
    private DateTime _lastLeftClickUtc = DateTime.MinValue;
    private Point _lastLeftClickPoint;

    private RegistryWatcher? _watcher;
    private readonly System.Windows.Forms.Timer _followTimer = new() { Interval = ProbeTimeouts.FollowSyncMs };
    private readonly System.Windows.Forms.Timer _networkDebounceTimer = new() { Interval = ProbeTimeouts.NetworkDebounceMs };
    private int _followSyncRunning;
    private int _followPendingCount;
    private List<EntryRow> _rows = new();
    private List<EntryRow> _view = new();
    private string? _sortProperty;
    private bool _sortAscending = true;
    private bool _refreshing;
    private bool _reallyExit;
    private bool _trayTipShown;
    private Action? _bannerAction;
    private readonly Panel _bannerButtonHost = new();
    private bool _layoutingBanner;
    private IpHlpState _ipState = IpHlpState.Unknown;
    private IReadOnlyList<PortProxyEntry> _deadMappings = Array.Empty<PortProxyEntry>();
    private bool? _autostartCache;
    private DateTime _autostartCheckedAtUtc = DateTime.MinValue;

    public MainForm(PathContext paths, MetaStore meta, SettingsStore settings, bool elevated, bool startInTray)
    {
        _paths = paths;
        _meta = meta;
        _settings = settings;
        _elevated = elevated;
        _icon = AppIcon.Create(32);

        BuildWindow();
        // 停靠布局按 z-order 反向处理：Fill 的控件必须先加入，
        // 顶部的 banner 最后加入才会落在最上面
        BuildGrid();
        BuildRowMenu();
        BuildStatusBar();
        BuildToolbar();
        BuildBanner();
        BuildTray();

        RestoreWindowState();
        _trayTipShown = _settings.Value.TrayTipShown;
        _filterBox.Text = _settings.Value.LastFilter;

        RefreshData();
        StartWatcher();
        InitializeFollowSync();

        // 启动时在后台预热全部目标地址候选，打开编辑窗口即可直接选
        _ = WarmCandidateHintsAsync();

        // 启动时按保留策略清一次旧快照，避免历史备份无限增长
        BackupService.Prune(_paths, _settings.Value);

        var uiTimer = new System.Windows.Forms.Timer { Interval = ProbeTimeouts.StatusTimerMs };
        uiTimer.Tick += (_, _) =>
        {
            uiTimer.Stop();
            uiTimer.Dispose();
            if (startInTray && !IsDisposed) HideToTray(false);
        };
        // 首次显示后如需进托盘，用一次性定时器错开，避免 Shown 事件里 Hide 闪烁
        if (startInTray) uiTimer.Start();
    }

    // ---------------------------------------------------------------- 界面构建

    private void BuildWindow()
    {
        SuspendLayout();

        // 关闭 WinForms 自动缩放，改由 DpiScale 在 OnLoad 里整体缩放控件树
        AutoScaleMode = AutoScaleMode.None;

        Text = "PortProxy 管理器";
        Icon = _icon;
        StartPosition = FormStartPosition.CenterScreen;
        Font = ResolveFont(9f);
        KeyPreview = true;
        // 尺寸与最小尺寸在 OnLoad 里按实际 DPI 计算（见 DpiScale）

        ResumeLayout();
    }

    private static Font ResolveFont(float size)
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
        {
            try
            {
                var font = new Font(name, size);
                if (font.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return font;
                font.Dispose();
            }
            catch
            {
                // 换下一个候选字体
            }
        }

        return SystemFonts.MessageBoxFont ?? Control.DefaultFont;
    }

    private void BuildBanner()
    {
        _banner.Dock = DockStyle.Top;
        _banner.Height = 0;
        _banner.Visible = false;
        _banner.BackColor = Color.FromArgb(0xFF, 0xF4, 0xCE);
        _banner.Padding = new Padding(12, 6, 12, 6);

        // 按钮不能直接 Dock=Right：那样会被拉成整条横幅那么高。
        // 放进一个右侧宿主面板，按钮自身固定高度、垂直居中。
        _bannerButtonHost.Dock = DockStyle.Right;
        _bannerButtonHost.Width = 0;
        _bannerButtonHost.Margin = Padding.Empty;

        _bannerButton.Dock = DockStyle.None;
        _bannerButton.FlatStyle = FlatStyle.System;
        _bannerButton.Click += (_, _) => _bannerAction?.Invoke();
        _bannerButtonHost.Controls.Add(_bannerButton);

        _banner.Resize += (_, _) => LayoutBannerButton();

        _bannerLabel.Dock = DockStyle.Fill;
        _bannerLabel.TextAlign = ContentAlignment.MiddleLeft;
        _bannerLabel.AutoEllipsis = true;

        // 停靠按 z-order 反向处理：后加入的按钮先占右侧，标签再填满剩余
        _banner.Controls.Add(_bannerLabel);
        _banner.Controls.Add(_bannerButtonHost);

        Controls.Add(_banner);
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.RenderMode = ToolStripRenderMode.System;
        _toolbar.Padding = new Padding(10, 7, 10, 7);
        _toolbar.ImageScalingSize = new Size(16, 16);

        var addButton = new ToolStripButton("新增") { ToolTipText = "新增映射 (Ctrl+N)" };
        addButton.Click += (_, _) => AddEntry();

        _editButton.ToolTipText = "编辑选中映射 (Ctrl+E)";
        _editButton.Click += (_, _) => EditEntry();
        _editButton.Enabled = false;

        _deleteButton.ToolTipText = "删除选中映射 (Del)";
        _deleteButton.Click += (_, _) => DeleteEntry();
        _deleteButton.Enabled = false;

        var refreshButton = new ToolStripButton("刷新") { ToolTipText = "重新读取 (F5)" };
        refreshButton.Click += (_, _) => RefreshData();

        _diagnoseButton.ToolTipText = "诊断选中映射的转发链路";
        _diagnoseButton.Click += (_, _) => DiagnoseSelected();
        _diagnoseButton.Enabled = false;

        var importButton = new ToolStripButton("导入") { ToolTipText = "从 JSON 导入（先预览差异）" };
        importButton.Click += (_, _) => ImportFile();

        var exportButton = new ToolStripButton("导出") { ToolTipText = "导出全部映射与备注" };
        exportButton.Click += (_, _) => ExportFile();

        var logButton = new ToolStripButton("日志") { ToolTipText = "查看操作日志" };
        logButton.Click += (_, _) => ShowLog();

        _autostartButton.ToolTipText = "登录时自动启动（计划任务，不弹 UAC）";
        _autostartButton.Text = "自启";
        _autostartButton.Click += (_, _) => ToggleAutostart();

        _filterBox.AutoSize = false;
        _filterBox.Margin = new Padding(2, 0, 12, 0);
        _filterBox.ToolTipText = "按名称、分组、地址、端口过滤";
        _filterBox.TextBox.PlaceholderText = "过滤…";
        _filterBox.TextChanged += (_, _) =>
        {
            if (_refreshing) return;
            ApplyFilter();
            _settings.Value.LastFilter = _filterBox.Text;
        };

        _groupBox.AutoSize = false;
        _groupBox.Margin = new Padding(0, 0, 12, 0);
        _groupBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _groupBox.ToolTipText = "按分组过滤";
        _groupBox.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshing) return;
            ApplyFilter();
            _settings.Value.LastGroup = CurrentGroupFilter();
        };

        _toolbar.Items.Add(addButton);
        _toolbar.Items.Add(_editButton);
        _toolbar.Items.Add(_deleteButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(refreshButton);
        _toolbar.Items.Add(_diagnoseButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_filterBox);
        _toolbar.Items.Add(_groupBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(importButton);
        _toolbar.Items.Add(exportButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_autostartButton);
        _toolbar.Items.Add(logButton);

        // ToolStripItem 不是 Control，宽度必须在加入工具栏之后设置，否则会被重置为默认值
        _filterBox.Width = 240;
        _groupBox.Width = 170;

        Controls.Add(_toolbar);
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersHeight = 38;
        _grid.RowTemplate.Height = 36;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0xF3, 0xF3, 0xF3);
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        _grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xCC, 0xE4, 0xF7);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        // 隔行浅底色，条目多时更好横向对读
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0xF7, 0xF9, 0xFB);

        // 列宽用权重按比例分配：DPI 和窗口宽度变化时都不会挤断表头
        AddColumn("Name", "名称", 130);
        AddColumn("Group", "分组", 80);
        AddColumn("Type", "类型", 75);
        AddColumn("ListenAddress", "监听地址", 108);
        AddColumn("ListenPort", "监听端口", 76);
        AddColumn("ConnectAddress", "目标地址", 108);
        AddColumn("ConnectPort", "目标端口", 76);
        AddColumn("Follow", "跟随", 96);
        AddColumn("Firewall", "防火墙", 68);
        AddColumn("Note", "备注", 150);

        _grid.SelectionChanged += (_, _) => UpdateSelectionState();

        _grid.KeyDown += GridKeyDown;
        _grid.ColumnHeaderMouseClick += GridHeaderClick;
        _grid.MouseDown += GridMouseDown;

        Controls.Add(_grid);
    }

    private void AddColumn(string property, string header, int fillWeight)
    {
        var column = new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = header,
            Name = property,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            Resizable = DataGridViewTriState.True,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            MinimumWidth = 48,
        };

        _grid.Columns.Add(column);
    }

    private void BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.SizingGrip = false;
        _statusBar.ShowItemToolTips = true;

        _countLabel.Text = "共 0 条";
        _countLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _countLabel.Padding = new Padding(4, 0, 8, 0);

        _helperLabel.Text = "IP Helper：检测中";
        _helperLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _helperLabel.Padding = new Padding(4, 0, 8, 0);

        _dirLabel.Spring = true;
        _dirLabel.TextAlign = ContentAlignment.MiddleLeft;
        _dirLabel.Text = $"数据目录：{_paths.DataDir}";
        _dirLabel.ToolTipText = "点击打开数据目录";
        _dirLabel.IsLink = true;
        _dirLabel.LinkBehavior = LinkBehavior.HoverUnderline;
        _dirLabel.Click += (_, _) => OpenDataDir();

        _statusBar.Items.Add(_countLabel);
        _statusBar.Items.Add(_helperLabel);
        _statusBar.Items.Add(_dirLabel);

        Controls.Add(_statusBar);
    }

    private void BuildTray()
    {
        _tray.Icon = _icon;
        _tray.Text = "PortProxy 管理器";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ActivateFromTray();

        var open = new ToolStripMenuItem("打开主窗口") { Font = new Font(Font, FontStyle.Bold) };
        open.Click += (_, _) => ActivateFromTray();

        var refresh = new ToolStripMenuItem("刷新");
        refresh.Click += (_, _) => RefreshData();

        var followSync = new ToolStripMenuItem("同步跟随目标");
        followSync.Click += (_, _) => _ = SyncFollowTargetsAsync(manual: true, forceRefresh: true);

        var dataDir = new ToolStripMenuItem("打开数据目录");
        dataDir.Click += (_, _) => OpenDataDir();

        _trayAutostart = new ToolStripMenuItem("开机自启") { CheckOnClick = false };
        _trayAutostart.Click += (_, _) => ToggleAutostart();

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitApplication();

        _trayMenu.Items.Add(open);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(refresh);
        _trayMenu.Items.Add(followSync);
        _trayMenu.Items.Add(dataDir);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_trayAutostart);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exit);
        _trayMenu.Opening += (_, _) =>
        {
            _trayAutostart.Checked = AutorunService.IsEnabled();
            _trayAutostart.Enabled = _elevated;
        };

        _tray.ContextMenuStrip = _trayMenu;
    }

    // ---------------------------------------------------------------- 数据刷新

    private void StartWatcher()
    {
        try
        {
            _watcher = new RegistryWatcher();
            _watcher.Changed += () =>
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(() =>
                    {
                        if (!IsDisposed) RefreshData();
                    });
                }
                catch
                {
                    // 窗口正在销毁
                }
            };
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"启动注册表监听失败，改为手动刷新：{ex.Message}");
        }
    }

    private void RefreshData(bool keepSelection = true)
    {
        if (_refreshing) return;
        _refreshing = true;

        try
        {
            var selectedKey = SelectedRow()?.Key;
            var entries = _service.Snapshot(out var readError);

            _rows = entries.Select(e => new EntryRow(e, _meta.Get(e.Key))).ToList();

            // 没有跟随项时清掉历史待同步提示
            if (_rows.All(r => r.Meta?.IsFollowing != true)) _followPendingCount = 0;

            RebuildGroupFilter();
            ApplyFilter(selectedKey);

            _countLabel.Text = $"共 {_rows.Count} 条";

            if (readError is not null)
            {
                _countLabel.Text += "（读取注册表失败）";
                _countLabel.ToolTipText = readError;
                LogService.Current?.Error($"刷新时读取注册表失败：{readError}");
            }

            _ipState = IpHelperService.GetState();
            _deadMappings = FindMappingsNotListening(entries);
            UpdateHelperStatus();
            UpdateAutostartButton();
            UpdateBanner();
            _tray.Text = $"PortProxy 管理器 · {_rows.Count} 条映射";

            // 顺手提示孤儿备注
            var orphans = _meta.FindOrphans(entries);
            if (orphans.Count > 0)
                _dirLabel.ToolTipText = $"有 {orphans.Count} 条备注没有对应映射（可在分组菜单里清理）";
        }
        finally
        {
            _refreshing = false;
        }

        UpdateSelectionState();
    }

    private void RebuildGroupFilter()
    {
        var current = CurrentGroupFilter();
        var groups = _rows.Select(r => r.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _groupBox.Items.Clear();
        _groupBox.Items.Add(AllGroupsLabel);
        foreach (var group in groups) _groupBox.Items.Add(group);

        var target = !string.IsNullOrWhiteSpace(current) && groups.Contains(current, StringComparer.CurrentCultureIgnoreCase)
            ? current
            : AllGroupsLabel;

        _groupBox.SelectedItem = target;
    }

    private const string AllGroupsLabel = "全部分组";

    private IReadOnlyList<string> KnownGroups() =>
        _rows.Select(r => r.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private string CurrentGroupFilter() =>
        _groupBox.SelectedItem as string ?? AllGroupsLabel;

    private void ApplyFilter(string? restoreSelectionKey = null)
    {
        restoreSelectionKey ??= SelectedRow()?.Key;

        var filter = _filterBox.Text.Trim();
        var group = CurrentGroupFilter();

        IEnumerable<EntryRow> query = _rows;

        if (group.Length > 0 && group != AllGroupsLabel)
            query = query.Where(r => string.Equals(r.Group, group, StringComparison.CurrentCultureIgnoreCase));

        if (filter.Length > 0)
        {
            query = query.Where(r =>
                r.FullText.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = query.ToList();

        if (_sortProperty is not null)
        {
            list.Sort((a, b) => CompareRows(a, b, _sortProperty, _sortAscending));
        }

        _view = list;
        _grid.DataSource = null;
        _grid.DataSource = new BindingList<EntryRow>(_view);

        ApplyRowHeight();

        UpdateSortGlyphs();
        RestoreSelection(restoreSelectionKey);
        UpdateSelectionState();
    }

    private void RestoreSelection(string? key)
    {
        if (key is null) return;

        for (var i = 0; i < _grid.Rows.Count; i++)
        {
            if (_grid.Rows[i].DataBoundItem is not EntryRow row) continue;
            if (!string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase)) continue;

            _grid.Rows[i].Selected = true;
            if (i >= 0 && i < _grid.Rows.Count) _grid.CurrentCell = _grid.Rows[i].Cells[0];
            return;
        }
    }

    private EntryRow? SelectedRow()
    {
        if (_grid.SelectedRows.Count == 0) return null;
        return _grid.SelectedRows[0].DataBoundItem as EntryRow;
    }

    private void UpdateSelectionState()
    {
        var row = SelectedRow();
        var canEdit = row is not null && _elevated;
        _editButton.Enabled = canEdit;
        _deleteButton.Enabled = canEdit;
        _diagnoseButton.Enabled = row is not null;
    }

    private void UpdateHelperStatus()
    {
        var state = _ipState;
        _helperLabel.Text = state switch
        {
            IpHlpState.Running => "IP Helper：运行中",
            IpHlpState.Stopped => "IP Helper：已停止",
            IpHlpState.NotFound => "IP Helper：未安装",
            _ => "IP Helper：状态未知",
        };

        _helperLabel.ForeColor = state == IpHlpState.Running ? Color.FromArgb(0x1B, 0x5E, 0x20) : Color.FromArgb(0xA6, 0x1B, 0x1B);
        _helperLabel.ToolTipText = state == IpHlpState.Running
            ? "端口转发依赖 IP Helper 服务，当前正常。"
            : "端口转发依赖 IP Helper（iphlpsvc）服务，它没运行的话映射配了也不会通。";
    }

    private void UpdateBanner()
    {
        var state = _ipState;
        var lines = new List<string>();
        Action? action = null;
        string actionText = string.Empty;

        if (state == IpHlpState.Stopped)
        {
            lines.Add("IP Helper（iphlpsvc）没有运行 —— 新增的映射不会生效。");
            if (_elevated)
            {
                action = StartHelperService;
                actionText = "启动服务";
            }
        }
        else if (state == IpHlpState.NotFound)
        {
            lines.Add("系统里找不到 IP Helper（iphlpsvc）服务，端口转发功能不可用。");
        }
        else if (_deadMappings.Count > 0)
        {
            var ports = string.Join("、", _deadMappings.Take(4).Select(e => e.ListenPort));
            var legacy = _deadMappings.FirstOrDefault(IsLegacyWildcard);

            var reserved = _deadMappings
                .Select(e => (Entry: e, Range: PortReservationService.FindContaining(e.ListenPort)))
                .FirstOrDefault(t => t.Range is not null);

            if (reserved.Entry is not null && reserved.Range is not null)
            {
                var range = reserved.Range.Value;
                lines.Add($"端口 {reserved.Entry.ListenPort} 落在系统保留区间 {range.Start}–{range.End}" +
                          "（由 Hyper-V / WSL / WinNAT 占用），任何程序都无法监听它 —— " +
                          "请改用其他端口；这些区间每次开机可能变化，所以以前能用的端口也会突然失效。");
            }
            else if (legacy is not null)
            {
                lines.Add($"端口 {legacy.ListenPort} 没有在监听：这条映射的监听地址在注册表里存成了 *，" +
                          "不是 netsh 的标准写法（0.0.0.0），IP Helper 通常无法加载它。");
                if (_elevated)
                {
                    action = () => RebuildMapping(legacy);
                    actionText = "重建为 0.0.0.0";
                }
            }
            else
            {
                lines.Add($"有 {_deadMappings.Count} 条映射没有生效：端口 {ports} 没有在监听 —— " +
                          "配置存在但 IP Helper 没有加载它。");
                if (_elevated)
                {
                    action = RestartHelperService;
                    actionText = "重启 IP Helper";
                }
            }
        }

        if (_paths.FallbackReason is not null)
        {
            lines.Add($"数据目录已降级到 {_paths.DataDir}（{_paths.FallbackReason}）。");
            if (action is null)
            {
                action = OpenDataDir;
                actionText = "打开目录";
            }
        }

        if (_followPendingCount > 0)
        {
            lines.Add($"有 {_followPendingCount} 条跟随目标已变化，等待写入映射。");
            if (action is null)
            {
                action = _elevated
                    ? () => { _ = SyncFollowTargetsAsync(manual: true, forceRefresh: true); }
                    : () => { RequireElevation(); };
                actionText = _elevated ? "立即同步" : "以管理员同步";
            }
        }

        _bannerAction = action;

        if (lines.Count == 0)
        {
            _banner.Visible = false;
            _banner.Height = 0;
            return;
        }

        _bannerLabel.Text = string.Join("　", lines);
        _tooltip.SetToolTip(_bannerLabel, string.Join(Environment.NewLine, lines));

        _bannerButton.Text = actionText;
        _bannerButton.Visible = actionText.Length > 0;
        LayoutBannerButton();

        // 高度必须按 DPI 换算：构造时 DpiScale 已经把 40 放大过，
        // 这里若再赋 40 就会把横幅压扁成一条（文案被裁掉）。
        _banner.Height = DpiScale.Value(this, 40);
        _banner.Visible = true;
    }

    private void UpdateAutostartButton()
    {
        var enabled = GetAutostartCached();
        _autostartButton.Checked = enabled;
        _autostartButton.Text = enabled ? "自启 ✓" : "自启";
        _autostartButton.Enabled = _elevated;
        _trayAutostart.Checked = enabled;
    }

    /// <summary>查询计划任务要起一个 schtasks 进程，缓存 30 秒避免刷新时反复调用。</summary>
    private bool GetAutostartCached(bool force = false)
    {
        if (!force
            && _autostartCache.HasValue
            && (DateTime.UtcNow - _autostartCheckedAtUtc).TotalSeconds < 30)
            return _autostartCache.Value;

        var enabled = AutorunService.IsEnabled();
        _autostartCache = enabled;
        _autostartCheckedAtUtc = DateTime.UtcNow;
        return enabled;
    }

    /// <summary>
    /// 找出"配置存在但端口没有被监听"的映射。
    /// portproxy 的监听由 IP Helper 负责，服务没加载配置时端口就是空的，
    /// 此时连本机自己的映射地址都会被拒绝。
    /// </summary>
    private IReadOnlyList<PortProxyEntry> FindMappingsNotListening(IReadOnlyList<PortProxyEntry> entries)
    {
        if (entries.Count == 0) return Array.Empty<PortProxyEntry>();

        try
        {
            var listeningPorts = new HashSet<int>(
                RegistryReader.ActiveTcpListeners().Select(l => l.Port));

            return entries.Where(e => !listeningPorts.Contains(e.ListenPort)).ToList();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"检查映射监听状态失败：{ex.Message}");
            return Array.Empty<PortProxyEntry>();
        }
    }

    /// <summary>注册表里把通配监听地址存成了字面量 *（非 netsh 标准形式）。</summary>
    private static bool IsLegacyWildcard(PortProxyEntry entry) =>
        entry.ListenAddress.Trim() == "*";

    /// <summary>
    /// 用标准写法重建一条映射：删除旧的 `*` 条目，再以 0.0.0.0（或 ::）添加。
    /// 删除时优先使用原始地址，因为 netsh 需要精确匹配注册表里的键名。
    /// </summary>
    private void RebuildMapping(PortProxyEntry entry)
    {
        if (!RequireElevation()) return;

        var nl = Environment.NewLine;
        var answer = MessageBox.Show(this,
            "将把这条映射重建为标准写法：" + nl + nl +
            $"原：{entry.Family} {entry.ListenText} → {entry.ConnectText}（监听地址存为 *）" + nl +
            $"新：{entry.Family} {entry.NetshListenAddress}:{entry.ListenPort} → {entry.ConnectText}" + nl + nl +
            "期间该端口会短暂中断，当前配置会先自动备份。继续吗？",
            "重建映射", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes) return;

        BackupService.WriteSnapshot(_paths, _service.Snapshot(), _meta, "pre-rebuild", _settings.Value);

        RunGuarded("重建映射", () =>
        {
            _service.Delete(entry, useRawListenAddress: true);

            var rebuilt = new PortProxyEntry
            {
                Family = entry.Family,
                Protocol = entry.Protocol,
                ListenAddress = entry.NetshListenAddress,
                ListenPort = entry.ListenPort,
                ConnectAddress = entry.NormalizedConnect,
                ConnectPort = entry.ConnectPort,
            };

            _service.Add(rebuilt);
        });

        ScheduleHealthRecheck();
    }

    /// <summary>写操作后监听器需要一点时间才出现，稍后再复查一次避免误报。</summary>
    private void ScheduleHealthRecheck()
    {
        var timer = new System.Windows.Forms.Timer { Interval = ProbeTimeouts.StatusTimerMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!IsDisposed) RefreshData();
        };
        timer.Start();
    }

    // ---------------------------------------------------------------- 目标跟随

    private void InitializeFollowSync()
    {
        _followTimer.Tick += (_, _) => _ = SyncFollowTargetsAsync(manual: false, forceRefresh: false);
        _followTimer.Start();

        _networkDebounceTimer.Tick += (_, _) =>
        {
            _networkDebounceTimer.Stop();
            _ = SyncFollowTargetsAsync(manual: false, forceRefresh: true);
        };

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        // 启动后稍等再同步一次；候选地址已在启动时开始预热，这里复用结果，
        // forceRefresh:false 可避免同一次启动里重复探测 WSL / 虚拟机。
        var startup = new System.Windows.Forms.Timer { Interval = ProbeTimeouts.StartupSyncDelayMs };
        startup.Tick += (_, _) =>
        {
            startup.Stop();
            startup.Dispose();
            if (!IsDisposed) _ = SyncFollowTargetsAsync(manual: false, forceRefresh: false);
        };
        startup.Start();
    }

    /// <summary>
    /// 启动时在后台探测全部目标地址候选，让「目标地址」和「目标跟随」下拉
    /// 打开即有内容。探测包含 WSL、Hyper-V、VMware、VirtualBox、Windows 沙盒、
    /// 默认网关和 ARP 邻居；全程不阻塞界面。
    /// </summary>
    private async Task WarmCandidateHintsAsync()
    {
        try
        {
            var hints = await GuestHintCache.RefreshNowAsync(force: true);
            if (IsDisposed) return;

            LogService.Current?.Info($"启动探测目标地址候选完成：{hints.Count} 条。");
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"启动探测目标地址候选失败：{ex.Message}");
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _networkDebounceTimer.Stop();
                _networkDebounceTimer.Start();
            });
        }
        catch
        {
            // 窗口正在销毁
        }
    }

    private void SyncFollowFromMenu()
    {
        if (_contextRow?.Meta?.IsFollowing != true) return;
        _ = SyncFollowTargetsAsync(manual: true, forceRefresh: true, onlyKey: _contextRow.Key);
    }

    /// <summary>
    /// 探测所有启用跟随的映射，地址变化时重建映射。
    /// 自动触发只写日志/托盘气泡；手动触发才弹结果汇总。
    /// </summary>
    private async Task SyncFollowTargetsAsync(bool manual, bool forceRefresh, string? onlyKey = null)
    {
        if (Interlocked.Exchange(ref _followSyncRunning, 1) == 1)
        {
            if (manual)
            {
                MessageBox.Show(this, "正在同步跟随目标，请稍候。", "目标跟随",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        try
        {
            var entries = _rows
                .Where(r => r.Meta?.IsFollowing == true)
                .Where(r => onlyKey is null
                    || string.Equals(r.Key, onlyKey, StringComparison.OrdinalIgnoreCase))
                .Select(r => (r.Entry, r.Meta!))
                .ToList();

            if (entries.Count == 0)
            {
                if (manual)
                {
                    MessageBox.Show(this, "当前没有启用目标跟随的映射。", "目标跟随",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            if (manual) Cursor = Cursors.WaitCursor;

            IReadOnlySet<string>? keys = onlyKey is null
                ? null
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { onlyKey };

            var report = await TargetFollowService.SyncAsync(
                entries, _service, _elevated, forceRefresh, keys);

            if (IsDisposed || Disposing) return;

            _followPendingCount = report.NeedsElevationCount;

            if (report.UpdatedCount > 0)
            {
                RefreshData();

                if (!manual)
                {
                    _tray.ShowBalloonTip(2500, "PortProxy 管理器",
                        $"已按跟随规则更新 {report.UpdatedCount} 条映射的目标地址。",
                        ToolTipIcon.Info);
                }
            }

            if (report.FailedCount > 0 && !manual)
            {
                _tray.ShowBalloonTip(3000, "PortProxy 管理器",
                    $"有 {report.FailedCount} 条跟随映射更新失败，详情见日志。",
                    ToolTipIcon.Warning);
            }

            if (report.UnresolvedCount > 0 && !manual)
            {
                LogService.Current?.Info(
                    $"目标跟随：{report.UnresolvedCount} 条目标当前未检测到，已保留原地址。");
            }

            UpdateBanner();

            if (manual)
            {
                var lines = report.Items
                    .Where(i => i.Status != FollowSyncStatus.UpToDate)
                    .Select(i => $"{i.Entry.ListenText}：{i.StatusLabel} — {i.Message}")
                    .Take(12)
                    .ToList();

                if (lines.Count == 0) lines.Add("所有跟随目标均无变化。");

                MessageBox.Show(this, string.Join(Environment.NewLine, lines),
                    "同步跟随目标", MessageBoxButtons.OK,
                    report.FailedCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"同步跟随目标失败：{ex}");
            if (manual)
            {
                MessageBox.Show(this, $"同步跟随目标失败：\n\n{ex.Message}", "目标跟随",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (manual && !IsDisposed) Cursor = Cursors.Default;
            Interlocked.Exchange(ref _followSyncRunning, 0);
        }
    }

    private void RestartHelperService()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            var (ok, message) = IpHelperService.TryRestart();
            RefreshData();
            MessageBox.Show(this, message, "IP Helper",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>按钮尺寸随文案变化，并在横幅内垂直居中。</summary>
    private void LayoutBannerButton()
    {
        if (_layoutingBanner) return;

        _layoutingBanner = true;
        try
        {
            if (!_bannerButton.Visible || _bannerButton.Text.Length == 0)
            {
                _bannerButtonHost.Width = 0;
                return;
            }

            var height = DpiScale.Value(this, 26);
            var textWidth = TextRenderer.MeasureText(_bannerButton.Text, _bannerButton.Font).Width;
            var width = textWidth + DpiScale.Value(this, 24);

            _bannerButton.Size = new Size(width, height);

            var hostWidth = width + DpiScale.Value(this, 10);
            if (_bannerButtonHost.Width != hostWidth) _bannerButtonHost.Width = hostWidth;

            _bannerButton.Left = Math.Max(0, _bannerButtonHost.ClientSize.Width - width);
            _bannerButton.Top = Math.Max(0, (_bannerButtonHost.ClientSize.Height - height) / 2);
        }
        finally
        {
            _layoutingBanner = false;
        }
    }

    private void StartHelperService()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            var (ok, message) = IpHelperService.TryStart();
            if (ok)
            {
                RefreshData();
                MessageBox.Show(this, message, "IP Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, message, "IP Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    // ---------------------------------------------------------------- 增删改

    private void AddEntry()
    {
        if (!RequireElevation()) return;

        using var dialog = new EntryDialog(null, null, _rows.Select(r => r.Entry).ToList(), _elevated, KnownGroups());
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        var entry = dialog.Result;
        var meta = dialog.ResultMeta;

        RunGuarded("新增映射", () =>
        {
            var added = _service.Add(entry);
            if (meta is not null && !meta.IsEmpty)
            {
                meta.CreatedAt ??= DateTimeOffset.Now;
                _meta.Set(added.Key, meta);
                _meta.Save();
            }

            if (dialog.CreateFirewallRule)
            {
                var (ok, message) = FirewallService.Add(added);
                if (!ok) throw new PortProxyException($"映射已创建，但防火墙规则添加失败：{message}");

                var stored = _meta.GetOrCreate(added.Key);
                stored.HasFirewallRule = true;
                _meta.Save();
            }
        });
    }

    private void EditEntry()
    {
        if (!RequireElevation()) return;

        var row = SelectedRow();
        if (row is null) return;

        using var dialog = new EntryDialog(row.Entry, row.Meta, _rows.Select(r => r.Entry).ToList(), _elevated, KnownGroups());
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        var updated = dialog.Result;
        if (row.Entry.SameTarget(updated))
        {
            // 只改了备注
            SaveMetaOnly(row.Entry.Key, dialog.ResultMeta);
            RefreshData();
            return;
        }

        BackupService.WriteSnapshot(_paths, _service.Snapshot(), _meta, "pre-update", _settings.Value);

        RunGuarded("修改映射", () =>
        {
            _service.Update(row.Entry, updated);

            if (!string.Equals(row.Entry.Key, updated.Key, StringComparison.OrdinalIgnoreCase))
                _meta.Remove(row.Entry.Key);

            if (dialog.ResultMeta is not null && !dialog.ResultMeta.IsEmpty)
                _meta.Set(updated.Key, dialog.ResultMeta);

            _meta.Save();

            if (row.Meta?.HasFirewallRule == true)
            {
                var (ok, message) = FirewallService.Delete(row.Entry);
                LogService.Current?.Info($"编辑后清理旧防火墙规则：{message}（{(ok ? "成功" : "失败")}）");
            }
        });
    }

    private void SaveMetaOnly(string key, EntryMeta? meta)
    {
        if (meta is null || meta.IsEmpty) _meta.Remove(key);
        else _meta.Set(key, meta);
        _meta.Save();
    }

    private void DeleteEntry()
    {
        if (!RequireElevation()) return;

        var row = SelectedRow();
        if (row is null) return;

        var name = string.IsNullOrWhiteSpace(row.Name) ? "（无名称）" : row.Name;
        var text =
            $"确定要删除这条映射吗？\n\n" +
            $"名称：{name}\n" +
            $"类型：{row.Entry.Family}\n" +
            $"监听：{row.Entry.ListenText}\n" +
            $"目标：{row.Entry.ConnectText}\n\n" +
            "删除前会把当前全部配置自动备份到 backups 目录。";

        if (MessageBox.Show(this, text, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        BackupService.WriteSnapshot(_paths, _service.Snapshot(), _meta, "pre-delete", _settings.Value);

        var hadFirewall = row.Meta?.HasFirewallRule == true;
        var deleted = RunGuarded("删除映射", () =>
        {
            _service.Delete(row.Entry);
            _meta.Remove(row.Entry.Key);
            _meta.Save();
        });

        if (!deleted || !hadFirewall) return;

        if (MessageBox.Show(this, $"是否同时删除这条映射的防火墙放行规则？（TCP {row.Entry.ListenPort}）",
                "清理防火墙", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            var (ok, message) = FirewallService.Delete(row.Entry);
            if (!ok) MessageBox.Show(this, message, "防火墙", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else RefreshData();
        }
    }

    private bool RequireElevation()
    {
        if (_elevated) return true;

        var result = MessageBox.Show(this,
            "修改端口映射需要管理员权限。\n\n是否以管理员身份重新启动本程序？",
            "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return false;

        var code = Cli.RelaunchElevated(Environment.GetCommandLineArgs().Skip(1)
            .Append("--data-dir=" + _paths.DataDir).ToArray(), wait: false);

        if (code != 0) MessageBox.Show(this, "提权启动失败，或用户取消了 UAC。", "提示");
        else Close();

        return false;
    }

    /// <summary>统一的执行包装：等待光标、异常翻译、完成后刷新。</summary>
    private bool RunGuarded(string title, Action action)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            action();
            return true;
        }
        catch (PortProxyException ex)
        {
            LogService.Current?.Error($"{title}失败：{ex.Message}");
            MessageBox.Show(this, $"{title}失败：\n\n{ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"{title}异常：{ex}");
            MessageBox.Show(this, $"{title}时发生意外错误：\n\n{ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshData();
        }
    }

    // ---------------------------------------------------------------- 导入导出

    private void ExportFile()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出映射配置",
            Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"portproxy-{DateTime.Now:yyyyMMdd-HHmm}.json",
            InitialDirectory = _paths.DataDir,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        try
        {
            BackupService.Export(dialog.FileName, _service.Snapshot(), _meta);
            MessageBox.Show(this, $"已导出到：\n{dialog.FileName}", "导出完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：\n\n{ex.Message}", "导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void ImportFile()
    {
        if (!RequireElevation()) return;

        using var dialog = new OpenFileDialog
        {
            Title = "导入映射配置",
            Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = _paths.DataDir,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ExportDocument document;
        try
        {
            document = BackupService.LoadDocument(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法读取配置文件：\n\n{ex.Message}", "导入", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var diff = BackupService.Diff(_service.Snapshot(), document);
        if (diff.All(d => d.Kind == DiffKind.Same) && diff.Count > 0)
        {
            if (MessageBox.Show(this, "文件内容与当前配置完全一致，仍然继续（会同步备注）？", "导入",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }

        using var preview = new ImportPreviewDialog(diff, dialog.FileName, _elevated);
        if (preview.ShowDialog(this) != DialogResult.OK) return;

        var selected = preview.SelectedItems;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "没有勾选任何条目。", "导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        BackupService.WriteSnapshot(_paths, _service.Snapshot(), _meta, "pre-import", _settings.Value);

        Cursor = Cursors.WaitCursor;
        try
        {
            var (ok, failed, errors) = BackupService.Apply(selected, _service, _meta);
            RefreshData();

            if (failed == 0)
            {
                MessageBox.Show(this, $"导入完成，成功 {ok} 条。", "导入",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this,
                    $"导入完成：成功 {ok} 条，失败 {failed} 条。\n\n" +
                    string.Join(Environment.NewLine, errors.Take(10)) +
                    (errors.Count > 10 ? $"\n…另有 {errors.Count - 10} 条" : string.Empty),
                    "导入（部分失败）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    // ---------------------------------------------------------------- 自启与日志

    private void ToggleAutostart()
    {
        if (!_elevated)
        {
            MessageBox.Show(this, "注册开机自启需要管理员权限。", "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var enabled = GetAutostartCached(force: true);
        var (ok, message) = enabled ? AutorunService.Disable() : AutorunService.Enable(_paths.DataDir);

        MessageBox.Show(this, message + (enabled ? string.Empty : $"\n\n启动参数：{AutorunService.BuildArguments(_paths.DataDir)}"),
            "开机自启", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        _autostartCache = null;   // 强制下次重新查询
        UpdateAutostartButton();
    }

    private void ShowLog()
    {
        var dir = LogService.Current?.Dir ?? _paths.LogDir;
        using var viewer = new LogViewerForm(dir);
        viewer.ShowDialog(this);
    }

    private void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开目录失败：{ex.Message}", "数据目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- 网格交互

    /// <summary>快捷键（与工具栏提示一致）。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N:
                AddEntry();
                return true;
            case Keys.Control | Keys.E:
                EditEntry();
                return true;
            case Keys.Control | Keys.O:
                ImportFile();
                return true;
            case Keys.Control | Keys.S:
                ExportFile();
                return true;
            case Keys.Control | Keys.L:
                ShowLog();
                return true;
            case Keys.F5:
                RefreshData();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void GridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            RefreshData();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            DeleteEntry();
            e.Handled = true;
        }
    }

    private void GridHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)    {
        if (e.ColumnIndex < 0) return;

        var property = _grid.Columns[e.ColumnIndex].DataPropertyName;
        if (string.IsNullOrEmpty(property)) return;

        if (string.Equals(_sortProperty, property, StringComparison.Ordinal))
            _sortAscending = !_sortAscending;
        else
        {
            _sortProperty = property;
            _sortAscending = true;
        }

        ApplyFilter();
    }

    /// <summary>
    /// 数据绑定时新建的行不会继承 RowTemplate.Height，必须逐行设置；
    /// DPI 缩放改变 RowTemplate.Height 之后也要重新应用一次。
    /// </summary>
    private void ApplyRowHeight()
    {
        var height = _grid.RowTemplate.Height;
        if (height <= 0) return;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Height != height) row.Height = height;
        }
    }

    private void UpdateSortGlyphs()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection =
                _sortProperty is not null && column.DataPropertyName == _sortProperty
                    ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                    : SortOrder.None;
        }
    }

    private static int CompareRows(EntryRow a, EntryRow b, string property, bool ascending)
    {
        var result = property switch
        {
            nameof(EntryRow.Name) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRow.Group) => string.Compare(a.Group, b.Group, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRow.Type) => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
            nameof(EntryRow.ListenAddress) => CompareAddress(a.ListenAddress, b.ListenAddress),
            nameof(EntryRow.ListenPort) => a.ListenPort.CompareTo(b.ListenPort),
            nameof(EntryRow.ConnectAddress) => CompareAddress(a.ConnectAddress, b.ConnectAddress),
            nameof(EntryRow.ConnectPort) => a.ConnectPort.CompareTo(b.ConnectPort),
            nameof(EntryRow.Follow) => string.Compare(a.Follow, b.Follow, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRow.Firewall) => string.Compare(a.Firewall, b.Firewall, StringComparison.Ordinal),
            nameof(EntryRow.Note) => string.Compare(a.Note, b.Note, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };

        return ascending ? result : -result;
    }

    private static int CompareAddress(string a, string b)
    {
        // *（所有地址）排最前，其余按字符串比较
        if (a == "*" && b != "*") return -1;
        if (b == "*" && a != "*") return 1;
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 行右键菜单：整个生命周期只创建一次。
    ///
    /// 曾经的做法是每次右键新建一个 ContextMenuStrip，并在 Closed 里 Dispose。
    /// 这会导致 ObjectDisposedException（Control.CreateHandle 访问已释放的菜单），
    /// 表现为右键或点空白处程序直接闪退，因此改为复用单一实例、随窗体释放。
    /// </summary>
    private void BuildRowMenu()
    {
        _rowMenuEdit = new ToolStripMenuItem("编辑…", null, (_, _) => EditEntry());
        _rowMenuDelete = new ToolStripMenuItem("删除…", null, (_, _) => DeleteEntry());
        _rowMenuCopy = new ToolStripMenuItem("复制 netsh 命令", null, (_, _) => CopyNetshCommand());
        _rowMenuProbe = new ToolStripMenuItem("测试目标连通性", null, (_, _) => ProbeTarget());
        _rowMenuFollow = new ToolStripMenuItem("立即同步跟随目标", null, (_, _) => SyncFollowFromMenu());
        _rowMenuDiagnose = new ToolStripMenuItem("诊断转发链路…", null, (_, _) => DiagnoseContextRow());

        _rowMenu.Items.Add(_rowMenuEdit);
        _rowMenu.Items.Add(_rowMenuDelete);
        _rowMenu.Items.Add(new ToolStripSeparator());
        _rowMenu.Items.Add(_rowMenuCopy);
        _rowMenu.Items.Add(_rowMenuProbe);
        _rowMenu.Items.Add(new ToolStripSeparator());
        _rowMenu.Items.Add(_rowMenuFollow);
        _rowMenu.Items.Add(_rowMenuDiagnose);
    }

    private void GridMouseDown(object? sender, MouseEventArgs e)
    {
        var hit = _grid.HitTest(e.X, e.Y);

        // DataGridView 会吞掉空白区域的 WM_LBUTTONDBLCLK，DoubleClick 事件不触发，
        // 所以按系统双击时间和距离自己判定：双击空白处 = 新增。
        if (e.Button == MouseButtons.Left)
        {
            var now = DateTime.UtcNow;
            var isDoubleClick =
                (now - _lastLeftClickUtc).TotalMilliseconds <= SystemInformation.DoubleClickTime
                && Math.Abs(e.X - _lastLeftClickPoint.X) <= SystemInformation.DoubleClickSize.Width / 2
                && Math.Abs(e.Y - _lastLeftClickPoint.Y) <= SystemInformation.DoubleClickSize.Height / 2;

            _lastLeftClickUtc = now;
            _lastLeftClickPoint = e.Location;

            if (isDoubleClick
                && (hit.RowIndex >= 0 || hit.Type == DataGridViewHitTestType.None))
            {
                _lastLeftClickUtc = DateTime.MinValue;   // 防止三击重复触发

                var onRow = hit.RowIndex >= 0;

                // 推迟到鼠标序列（含 MouseUp）走完再开模态窗口，
                // 否则这次抬起的按键可能落到刚弹出的对话框上。
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    if (onRow) EditEntry();
                    else AddEntry();
                });
            }

            return;
        }

        if (e.Button != MouseButtons.Right) return;

        if (hit.RowIndex < 0) return;

        _grid.ClearSelection();
        _grid.Rows[hit.RowIndex].Selected = true;

        _contextRow = SelectedRow();
        if (_contextRow is null) return;

        _rowMenuEdit.Enabled = _elevated;
        _rowMenuDelete.Enabled = _elevated;
        _rowMenuCopy.Enabled = true;
        _rowMenuProbe.Enabled = true;
        _rowMenuFollow.Enabled = _contextRow.Meta?.IsFollowing == true;
        _rowMenuDiagnose.Enabled = true;

        _rowMenu.Show(_grid, e.Location);
    }

    private void CopyNetshCommand()
    {
        if (_contextRow is null) return;

        try
        {
            Clipboard.SetText(_contextRow.Entry.NetshPreview);
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"复制到剪贴板失败：{ex.Message}");
        }
    }

    /// <summary>异步处理器不能把异常抛回消息循环，这里全部兜住。</summary>
    private async void ProbeTarget()
    {
        var row = _contextRow;
        if (row is null) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var (ok, detail) = await PortProbe.TestConnectAsync(row.Entry.NormalizedConnect, row.Entry.ConnectPort);
            Cursor = Cursors.Default;

            if (IsDisposed) return;

            MessageBox.Show(this, detail, ok ? "连接测试：成功" : "连接测试：失败",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            Cursor = Cursors.Default;
            LogService.Current?.Error($"连通性测试异常：{ex}");
            if (!IsDisposed)
                MessageBox.Show(this, $"测试失败：{ex.Message}", "连接测试", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------------------------------------------------------- 诊断

    private void DiagnoseSelected()
    {
        var row = SelectedRow();
        if (row is null) return;
        DiagnoseEntry(row);
    }

    private void DiagnoseContextRow()
    {
        if (_contextRow is null) return;
        DiagnoseEntry(_contextRow);
    }

    /// <summary>
    /// 打开诊断窗口；如果用户在窗口里点了修复动作，执行后再次打开复查。
    /// </summary>
    private async void DiagnoseEntry(EntryRow row)
    {
        var entry = row.Entry;
        var meta = _meta.Get(entry.Key) ?? row.Meta;

        try
        {
            while (!IsDisposed)
            {
                using var dialog = new DiagnosticsDialog(entry, meta, _elevated);

                if (dialog.ShowDialog(this) != DialogResult.OK
                    || dialog.Action == DiagnosticAction.None)
                    return;

                var ok = PerformDiagnosticAction(dialog.Action, entry);

                // 服务重启 / 端口重建需要一点时间才能看到监听器
                if (ok) await Task.Delay(ProbeTimeouts.StatusRefreshDelayMs);

                if (IsDisposed || Disposing) return;

                RefreshData();
                meta = _meta.Get(entry.Key) ?? meta;

                if (!ok) return;
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"诊断流程失败：{ex}");
            if (!IsDisposed)
            {
                MessageBox.Show(this, $"诊断流程失败：\n\n{ex.Message}", "诊断",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private bool PerformDiagnosticAction(DiagnosticAction action, PortProxyEntry entry)
    {
        switch (action)
        {
            case DiagnosticAction.StartIpHelper:
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    var (ok, message) = IpHelperService.TryStart();
                    MessageBox.Show(this, message, "IP Helper", MessageBoxButtons.OK,
                        ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    return ok;
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }

            case DiagnosticAction.RestartIpHelper:
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    var (ok, message) = IpHelperService.TryRestart();
                    MessageBox.Show(this, message, "IP Helper", MessageBoxButtons.OK,
                        ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    return ok;
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }

            case DiagnosticAction.RebuildMapping:
                RebuildMapping(entry);
                return true;

            case DiagnosticAction.AddFirewallRule:
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    var (ok, message) = FirewallService.Add(entry);
                    if (ok)
                    {
                        var stored = _meta.GetOrCreate(entry.Key);
                        stored.HasFirewallRule = true;
                        _meta.Save();
                    }

                    MessageBox.Show(this, message, "防火墙", MessageBoxButtons.OK,
                        ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    return ok;
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }

            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- 窗口生命周期

    private void RestoreWindowState()
    {
        var settings = _settings.Value;

        if (settings.WindowX != int.MinValue && settings.WindowY != int.MinValue)
        {
            var bounds = new Rectangle(settings.WindowX, settings.WindowY, 400, 300);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = bounds.Location;
            }
        }

        if (settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>设计尺寸（96 DPI）整体换算成当前 DPI 的实际像素。</summary>
    private void ApplyWindowMetrics()
    {
        var factor = DpiScale.Factor(this);
        if (Math.Abs(factor - 1f) > 0.001f) DpiScale.ScaleTree(this, factor);

        MinimumSize = DpiScale.SizeOf(this, 820, 460);

        var width = _settings.Value.WindowWidth;
        var height = _settings.Value.WindowHeight;

        if (width < 400 || height < 300)
        {
            width = 1120;
            height = 660;
        }

        Size = DpiScale.SizeOf(this, width, height);

        // 缩放后行高基准变了，重设已有行
        ApplyRowHeight();
    }

    private void PersistWindowState()
    {
        var settings = _settings.Value;

        if (WindowState == FormWindowState.Normal)
        {
            // 尺寸按 96 DPI 归一化保存，否则下次启动会被 AutoScaleMode 再放大一遍
            var scale = DeviceDpi > 0 ? DeviceDpi / 96.0 : 1.0;
            settings.WindowWidth = (int)(Width / scale);
            settings.WindowHeight = (int)(Height / scale);
            settings.WindowX = Location.X;
            settings.WindowY = Location.Y;
        }

        settings.Maximized = WindowState == FormWindowState.Maximized;
        settings.TrayTipShown = _trayTipShown;

        _settings.Save();
    }

    public void ActivateFromTray()
    {
        if (IsDisposed) return;

        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HideToTray(bool showTip)
    {
        Hide();
        ShowInTaskbar = false;

        if (!showTip || _trayTipShown) return;

        _tray.ShowBalloonTip(2500, "PortProxy 管理器", "已最小化到托盘，双击图标恢复窗口。", ToolTipIcon.Info);
        _trayTipShown = true;
        _settings.Value.TrayTipShown = true;
        _settings.Save();
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    protected override void OnLoad(EventArgs e)
    {
        ApplyWindowMetrics();
        base.OnLoad(e);

        LogService.Current?.Info(
            $"窗口就绪 dpi={DeviceDpi} autoscale={AutoScaleMode} size={Width}x{Height} " +
            $"minSize={MinimumSize.Width}x{MinimumSize.Height} screen={Screen.FromControl(this).Bounds}");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing && _settings.Value.CloseToTray)
        {
            e.Cancel = true;
            HideToTray(true);
            return;
        }

        _watcher?.Dispose();
        _watcher = null;

        _tray.Visible = false;

        PersistWindowState();

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

            _followTimer.Stop();
            _followTimer.Dispose();
            _networkDebounceTimer.Stop();
            _networkDebounceTimer.Dispose();

            _watcher?.Dispose();
            _tray.Dispose();
            _trayMenu.Dispose();
            _rowMenu.Dispose();
            _tooltip.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
