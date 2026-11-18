using System.Net;
using System.Net.Sockets;
using PortProxyManager.Models;
using PortProxyManager.Services;

namespace PortProxyManager.UI;

public sealed class EntryDialog : Form
{
    private readonly PortProxyEntry? _existing;
    private readonly EntryMeta? _existingMeta;
    private readonly IReadOnlyList<PortProxyEntry> _allEntries;
    private readonly IReadOnlyList<string> _knownGroups;
    private readonly bool _elevated;

    private readonly ComboBox _familyBox = new();
    private readonly TextBox _listenAddress = new();
    private readonly NumericUpDown _listenPort = new();
    private readonly ComboBox _connectAddress = new();
    private readonly ToolTip _tip = new();
    private List<AddressHint> _addressHints = new();
    private string? _pendingSelectionAddress;
    private string? _pendingSelectionDisplay;
    private int _populateVersion;
    private int _selectionVersion;
    private readonly System.Windows.Forms.Timer _guestRefreshTimer = new() { Interval = ProbeTimeouts.HintRefreshMs };
    private bool _populatingHints;
    private readonly NumericUpDown _connectPort = new();
    private readonly RadioButton _followNo = new();
    private readonly RadioButton _followYes = new();
    private List<FollowChoice> _followChoices = new();
    private string _fixedAddressText = "127.0.0.1";
    private bool _populatingFollow;
    private FollowTarget? _initialFollowTarget;
    private readonly TextBox _nameBox = new();
    private readonly ComboBox _groupBox = new();
    private readonly TextBox _noteBox = new();
    private readonly CheckBox _firewallBox = new();
    private readonly TextBox _preview = new();
    private readonly Label _errorLabel = new();
    private readonly Label _testResult = new();

    private sealed record FollowChoice(
        FollowTarget? Target, AddressHint? Hint, string Display, bool IsCurrentMatch = false)
    {
        public override string ToString() => Display;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PortProxyEntry? Result { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public EntryMeta? ResultMeta { get; private set; }

    public bool CreateFirewallRule => _firewallBox.Checked;

    public EntryDialog(
        PortProxyEntry? existing,
        EntryMeta? meta,
        IReadOnlyList<PortProxyEntry> allEntries,
        bool elevated,
        IReadOnlyList<string>? knownGroups = null)
    {
        _existing = existing;
        _existingMeta = meta;
        _allEntries = allEntries;
        _elevated = elevated;
        _knownGroups = knownGroups ?? Array.Empty<string>();

        BuildUi();
        LoadValues();

        // 编辑已有跟随条目时切到跟随模式；CheckedChanged 会按模式填充候选
        _followYes.Checked = _initialFollowTarget is not null;

        UpdatePreview();
        GuestHintCache.RefreshIfStale();

        _guestRefreshTimer.Tick += (_, _) =>
        {
            // 打开下拉期间不刷新；关闭后才在后台更新缓存
            if (!_connectAddress.DroppedDown) GuestHintCache.RefreshIfStale();
        };

        // 句柄创建后控件才有真实宽度，这时再按内容定高一次
        Shown += async (_, _) =>
        {
            ResizePreview();

            // 启动预热可能还没跑完；等它结束后补一次候选
            await RefreshCandidatesWhenReadyAsync();
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        DpiScale.Apply(this, 560, 596, 480, 540);
        ResizePreview();
        base.OnLoad(e);
    }

    private void BuildUi()
    {
        Text = _existing is null ? "新增端口映射" : "编辑端口映射";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9f);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(16, 12, 16, 4),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));

        // 类型（放在容器里用 Dock.Left 定宽，避免被表格重新测量）
        _familyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _familyBox.Dock = DockStyle.Left;
        _familyBox.Width = 240;
        _familyBox.Items.AddRange(new object[]
        {
            "自动（按地址推断）", "v4tov4", "v4tov6", "v6tov4", "v6tov6",
        });
        _familyBox.SelectedIndex = 0;
        _familyBox.SelectedIndexChanged += (_, _) => UpdatePreview();

        var familyPanel = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 30 };
        familyPanel.Controls.Add(_familyBox);
        AddRow(table, "类型", familyPanel, span: true);

        // 监听地址 / 端口
        _listenAddress.Text = "0.0.0.0";
        _listenAddress.TextChanged += (_, _) => UpdatePreview();
        _listenAddress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _listenPort.Minimum = 1;
        _listenPort.Maximum = 65535;
        _listenPort.Value = 8080;
        _listenPort.TextAlign = HorizontalAlignment.Right;
        _listenPort.Anchor = AnchorStyles.Right;
        _listenPort.Width = 96;
        _listenPort.ValueChanged += (_, _) => UpdatePreview();
        _listenPort.TextChanged += (_, _) => UpdatePreview();
        AddRow(table, "监听地址", _listenAddress, control2: _listenPort);

        // 目标（固定 IP / 跟随目标 单选 + 单一目标输入框）
        _followNo.Text = "固定 IP";
        _followYes.Text = "跟随目标";
        _followNo.AutoSize = true;
        _followYes.AutoSize = true;
        _followNo.Checked = true;
        _followNo.Margin = new Padding(0, 7, 2, 0);
        _followYes.Margin = new Padding(0, 7, 6, 0);
        _followNo.CheckedChanged += (_, _) => ApplyFollowMode();
        _followYes.CheckedChanged += (_, _) => ApplyFollowMode();

        _connectAddress.DropDownStyle = ComboBoxStyle.DropDown;
        _connectAddress.Text = _fixedAddressText;
        _connectAddress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _connectAddress.TextChanged += (_, _) => UpdatePreview();
        _connectAddress.DropDown += (_, _) => PopulateForCurrentMode();
        // 只有在用户真正从下拉里选中时才回填，程序填充 Items 不应覆盖手输地址
        _connectAddress.SelectionChangeCommitted += (_, _) =>
        {
            if (!IsFollowMode) ApplySelectedHint();
        };
        _connectAddress.SelectedIndexChanged += (_, _) =>
        {
            if (IsFollowMode) ApplyFollowSelection();
        };
        _connectAddress.DropDownClosed += (_, _) => OnAddressDropDownClosed();
        _tip.SetToolTip(_connectAddress,
            "固定 IP：可手输或选择自动探测到的地址；跟随目标：WSL / 虚拟机 IP 变化时自动更新。");

        _connectPort.Minimum = 1;
        _connectPort.Maximum = 65535;
        _connectPort.Value = 80;
        _connectPort.TextAlign = HorizontalAlignment.Right;
        _connectPort.Anchor = AnchorStyles.Right;
        _connectPort.Width = 96;
        _connectPort.ValueChanged += (_, _) => UpdatePreview();
        _connectPort.TextChanged += (_, _) => UpdatePreview();

        var modeFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Left,
            Margin = new Padding(0),
        };
        modeFlow.Controls.Add(_followNo);
        modeFlow.Controls.Add(_followYes);

        var targetLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0),
        };
        targetLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetLayout.Controls.Add(modeFlow, 0, 0);
        _connectAddress.Margin = new Padding(8, 3, 0, 3);
        targetLayout.Controls.Add(_connectAddress, 1, 0);

        var targetPanel = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 30 };
        targetPanel.Controls.Add(targetLayout);
        AddRow(table, "目标", targetPanel, control2: _connectPort);

        // 连通性测试
        var testButton = new Button
        {
            Text = "测试目标连通性",
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            Dock = DockStyle.Left,
        };
        testButton.Click += async (_, _) => await TestConnectAsync();

        _testResult.Dock = DockStyle.Fill;
        _testResult.TextAlign = ContentAlignment.MiddleLeft;
        _testResult.ForeColor = SystemColors.GrayText;

        var testPanel = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 30 };
        testPanel.Controls.Add(_testResult);
        testPanel.Controls.Add(testButton);
        AddRow(table, string.Empty, testPanel, span: true);

        // 名称 / 分组 / 备注
        _nameBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddRow(table, "名称", _nameBox, span: true);

        _groupBox.DropDownStyle = ComboBoxStyle.DropDown;
        _groupBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        foreach (var group in _knownGroups) _groupBox.Items.Add(group);
        AddRow(table, "分组", _groupBox, span: true);

        _noteBox.Multiline = true;
        _noteBox.Height = 56;
        _noteBox.ScrollBars = ScrollBars.Vertical;
        _noteBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        AddRow(table, "备注", _noteBox, span: true);

        // 防火墙
        _firewallBox.Text = "同时创建入站放行规则（TCP）";
        _firewallBox.AutoSize = true;
        _firewallBox.Anchor = AnchorStyles.Left;
        AddRow(table, string.Empty, _firewallBox, span: true);

        // 等价命令
        _preview.ReadOnly = true;
        _preview.Multiline = true;
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA);
        _preview.Font = new Font("Consolas", 8.5f);
        _preview.Height = 64;
        _preview.ScrollBars = ScrollBars.Vertical;
        _preview.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        AddRow(table, "等价命令", _preview, span: true);

        // 错误提示
        _errorLabel.AutoSize = false;
        _errorLabel.Height = 34;
        _errorLabel.ForeColor = Color.FromArgb(0xB7, 0x1C, 0x1C);
        _errorLabel.TextAlign = ContentAlignment.MiddleLeft;
        _errorLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddRow(table, string.Empty, _errorLabel, span: true);

        var hint = new Label
        {
            Text = "netsh portproxy 只支持 TCP；监听地址填 0.0.0.0 或 * 表示所有网卡。",
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            Height = 20,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        AddRow(table, string.Empty, hint, span: true);

        // 末尾放一个百分比行吸收剩余空间，避免多余空白被分摊到各控件之间
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Controls.Add(table);

        // 按钮区（先加表格再加按钮：停靠按 z-order 反向处理）
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(16, 9, 16, 9),
        };

        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(88, 28),
            FlatStyle = FlatStyle.System,
        };

        var ok = new Button
        {
            Text = _existing is null ? "创建" : "保存",
            AutoSize = true,
            MinimumSize = new Size(88, 28),
            FlatStyle = FlatStyle.System,
        };
        ok.Click += (_, _) => OnConfirm();

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control,
        Control? control2 = null, bool span = false)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (label.Length > 0)
        {
            var text = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 12, 6),
            };
            table.Controls.Add(text, 0, row);
        }

        control.Margin = new Padding(0, 3, 0, 3);
        table.Controls.Add(control, 1, row);

        if (control2 is not null)
        {
            control2.Margin = new Padding(8, 3, 0, 3);
            table.Controls.Add(control2, 2, row);
        }
        else if (span)
        {
            table.SetColumnSpan(control, 2);
        }
    }

    private void LoadValues()
    {
        if (_existing is null) return;

        _familyBox.SelectedItem = _existing.Family;
        _listenAddress.Text = _existing.NormalizedListen;
        _listenPort.Value = Math.Clamp(_existing.ListenPort, 1, 65535);
        _connectAddress.Text = _existing.NormalizedConnect;
        _fixedAddressText = _existing.NormalizedConnect;
        _connectPort.Value = Math.Clamp(_existing.ConnectPort, 1, 65535);

        if (_existingMeta is not null)
        {
            _nameBox.Text = _existingMeta.Name ?? string.Empty;
            _groupBox.Text = _existingMeta.Group ?? string.Empty;
            _noteBox.Text = _existingMeta.Note ?? string.Empty;
            _firewallBox.Checked = _existingMeta.HasFirewallRule;
            _initialFollowTarget = FollowTarget.FromMeta(_existingMeta);
        }
    }

    private bool IsFollowMode => _followYes.Checked;

    /// <summary>展开下拉时按当前模式填充候选；过期就顺手触发一次后台刷新。</summary>
    private void PopulateForCurrentMode()
    {
        if (IsFollowMode)
        {
            GuestHintCache.RefreshIfStale();
            PopulateFollowChoices();
        }
        else
        {
            PopulateAddressHints();
        }
    }

    /// <summary>按单选按钮切换固定 IP / 跟随目标模式，并重建目标输入框内容。</summary>
    private void ApplyFollowMode()
    {
        if (IsFollowMode)
        {
            // 记住固定 IP，切回来时恢复
            if (TryExtractAddress(_connectAddress.Text) is { } fixedIp)
                _fixedAddressText = fixedIp;

            _connectAddress.DropDownStyle = ComboBoxStyle.DropDownList;
            PopulateFollowChoices();
        }
        else
        {
            _connectAddress.DropDownStyle = ComboBoxStyle.DropDown;

            // 句柄未创建（构造期间）时不填充，等下拉打开或 Shown 后再填
            if (_connectAddress.IsHandleCreated) PopulateAddressHints();

            SetConnectAddress(_fixedAddressText);
        }

        UpdatePreview();
    }

    /// <summary>
    /// 重建跟随目标下拉。当前探测到的 WSL / 虚拟机会排在前面；
    /// 已保存但当前探测不到的目标也保留一项，避免编辑离线虚拟机时丢配置。
    /// </summary>
    private void PopulateFollowChoices()
    {
        if (_populatingFollow) return;

        _populatingFollow = true;
        try
        {
            var previous = (_connectAddress.SelectedItem as FollowChoice)?.Target
                ?? _initialFollowTarget;

            // 启动时已预热；这里只读缓存，避免展开下拉时被 arp.exe / wsl.exe 阻塞。
            var hints = GuestHintCache.Snapshot
                .Where(TargetFollowService.IsFollowable)
                // 同一台虚拟机可能有多块网卡/多个地址，优先按 MAC 合并成一条
                .GroupBy(h => h.Mac ?? h.Id ?? h.Address, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(FollowOrder)   // 稳定排序：保留探测结果内部的主地址顺序
                .ToList();

            var matched = false;
            var choices = new List<FollowChoice>();

            foreach (var hint in hints)
            {
                var target = FollowTarget.FromHint(hint);
                if (target is null) continue;

                var current = previous is not null && TargetFollowService.Matches(hint, previous);
                matched |= current;

                choices.Add(new FollowChoice(
                    target, hint, $"{target.Display} → {hint.Address}", current));
            }

            if (previous is not null && !matched)
            {
                choices.Add(new FollowChoice(
                    previous, null, $"{previous.Display}（当前未检测到，保留原地址）", true));
            }

            _followChoices = choices;

            _connectAddress.BeginUpdate();
            _connectAddress.Items.Clear();
            foreach (var choice in choices) _connectAddress.Items.Add(choice);
            _connectAddress.EndUpdate();

            _connectAddress.SelectedItem =
                choices.FirstOrDefault(c => c.IsCurrentMatch)
                ?? choices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"填充跟随目标失败：{ex.Message}");
        }
        finally
        {
            _populatingFollow = false;
        }

        // SelectedIndexChanged 被 _populatingFollow 守卫拦住了，这里补一次预览
        UpdatePreview();
        ResizeDropDownWidth(_followChoices.Select(c => c.Display).ToList());
    }

    private static int FollowOrder(AddressHint hint) => hint.Kind switch
    {
        "wsl" => 0,
        "hyperv" => 1,
        "wsb" => 2,
        _ => 3,
    };

    private FollowTarget? CurrentFollowTarget()
    {
        // 固定 IP 模式即"不跟随"；跟随模式下取当前选中的目标
        if (!IsFollowMode) return null;
        return (_connectAddress.SelectedItem as FollowChoice)?.Target ?? _initialFollowTarget;
    }

    private FollowChoice? CurrentFollowChoice()
    {
        if (!IsFollowMode) return null;
        return _connectAddress.SelectedItem as FollowChoice;
    }

    private void ApplyFollowSelection()
    {
        if (_populatingFollow) return;
        UpdatePreview();
    }

    /// <summary>
    /// 当前生效的目标地址：跟随模式取选中目标的探测地址（离线时保留原地址），
    /// 固定 IP 模式取输入框文本。
    /// </summary>
    private string GetCurrentConnectAddress()
    {
        if (!IsFollowMode) return _connectAddress.Text.Trim();

        var address = CurrentFollowChoice()?.Hint?.Address;
        if (!string.IsNullOrEmpty(address)) return address;

        // 离线跟随：保留条目原本的目标地址
        return _existing?.NormalizedConnect ?? string.Empty;
    }

    /// <summary>
    /// 窗口显示后等待一次候选刷新（如果启动预热还在跑就复用它），
    /// 完成后按当前模式补填下拉。用户正在展开下拉时不打断选择。
    /// </summary>
    private async Task RefreshCandidatesWhenReadyAsync()
    {
        try
        {
            await GuestHintCache.RefreshNowAsync(force: false);

            if (IsDisposed || _connectAddress.DroppedDown) return;

            if (IsFollowMode) PopulateFollowChoices();
            else PopulateAddressHints();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"补充目标候选失败：{ex.Message}");
        }
    }

    private string ResolveFamily()
    {
        if (_familyBox.SelectedItem is string selected && !selected.StartsWith("自动"))
            return selected;

        return Validator.InferFamily(_listenAddress.Text, GetCurrentConnectAddress());
    }

    private void UpdatePreview()
    {
        var family = ResolveFamily();
        var entry = BuildEntry(family, out _);

        _preview.Text = entry is null
            ? "（地址或端口不合法，无法生成命令）"
            : entry.NetshPreview;

        _preview.SelectionStart = 0;
        ResizePreview();
    }

    /// <summary>
    /// 填充目标地址候选。程序启动时已经在后台预热过完整候选（网关、ARP 邻居、
    /// WSL、虚拟机、沙盒、设备名），打开下拉只读缓存快照，不启动探测。
    /// 只有缓存仍为空时（首次启动尚未完成）才同步取一次网关/ARP 兜底。
    /// </summary>
    private void PopulateAddressHints()
    {
        if (_populatingHints) return;

        _populatingHints = true;
        try
        {
            var current = _connectAddress.Text;
            var selectionStart = _connectAddress.SelectionStart;
            var populateVersion = ++_populateVersion;
            var selectionVersion = _selectionVersion;

            // 先清掉上一次的选择，否则重建 Items 后原生控件可能按旧索引
            // 重新选中一项，把编辑框改成整条展示文本。
            _connectAddress.SelectedIndex = -1;

            var snapshot = GuestHintCache.Snapshot;
            var source = snapshot.Count > 0
                ? snapshot
                : NetworkHintService.FilterUnconfirmedGuests(NetworkHintService.GetHints());

            var sandboxMacs = new HashSet<string>(
                source
                    .Where(h => h.Category.Contains("沙盒", StringComparison.Ordinal) && h.Mac is not null)
                    .Select(h => h.Mac!),
                StringComparer.OrdinalIgnoreCase);

            var merged = new List<AddressHint>();
            var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSandboxMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hint in source)
            {
                // 沙盒同一 MAC 可能出现在多个虚拟交换机上；只保留优先级最高的一条
                if (hint.Mac is not null
                    && sandboxMacs.Contains(hint.Mac)
                    && !seenSandboxMacs.Add(hint.Mac))
                    continue;

                if (seenAddresses.Add(hint.Address)) merged.Add(hint);
            }

            _addressHints = merged;

            // 旧状态可能是整条"IP · 说明"，还原成纯 IP 再保留
            if (TryExtractAddress(current) is { } extracted) current = extracted;

            _connectAddress.BeginUpdate();
            _connectAddress.Items.Clear();
            foreach (var hint in merged) _connectAddress.Items.Add(hint.Display);
            _connectAddress.EndUpdate();

            // Items 重建后控件可能自动选中第一项；先清掉选择，再恢复用户手输的内容
            _connectAddress.SelectedIndex = -1;
            if (current.Length > 0)
            {
                _connectAddress.Text = current;
                _connectAddress.SelectionStart = Math.Min(selectionStart, current.Length);
            }

            // 原生 ComboBox 在下拉打开后，会把与某项展示文本前缀匹配的输入
            // 自动补全成整条展示文本（例如手输 IP 恰好是列表里某项的开头）。
            // 等它处理完这一轮消息后再恢复纯 IP；期间若用户已选中某项则不再覆盖。
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || !_connectAddress.DroppedDown) return;
                if (populateVersion != _populateVersion || selectionVersion != _selectionVersion) return;

                var text = _connectAddress.Text;
                var isNativeDisplay = _addressHints.Any(h =>
                    string.Equals(h.Display, text, StringComparison.Ordinal))
                    || TryExtractAddress(text) is not null;
                if (!isNativeDisplay) return;

                _connectAddress.SelectedIndex = -1;
                _connectAddress.Text = current;
                _connectAddress.SelectionStart = Math.Min(selectionStart, current.Length);
            }));

            ResizeDropDownWidth(merged.Select(h => h.Display).ToList());
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"填充地址候选失败：{ex.Message}");
        }
        finally
        {
            _populatingHints = false;
        }
    }

    private void OnAddressDropDownClosed()
    {
        NormalizeCommittedSelection();

        // 打开期间不刷新；关闭后在后台更新，下次打开生效
        if (!_guestRefreshTimer.Enabled) _guestRefreshTimer.Start();
        GuestHintCache.RefreshIfStale();
    }

    /// <summary>按最长一条的内容加宽下拉，否则设备名 / 虚拟机名会被截断。</summary>
    private void ResizeDropDownWidth(IReadOnlyList<string> items)
    {
        try
        {
            if (!_connectAddress.IsHandleCreated) return;

            var width = _connectAddress.Width;
            using var graphics = _connectAddress.CreateGraphics();

            foreach (var item in items)
            {
                var measured = TextRenderer.MeasureText(
                    graphics,
                    item,
                    _connectAddress.Font,
                    new Size(2000, 24),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                width = Math.Max(width, measured.Width + SystemInformation.VerticalScrollBarWidth + 16);
            }

            var maxWidth = (int)Math.Round(780 * DeviceDpi / 96.0);
            var workingWidth = Screen.FromControl(_connectAddress).WorkingArea.Width - 48;
            maxWidth = Math.Max(_connectAddress.Width, Math.Min(maxWidth, workingWidth));

            _connectAddress.DropDownWidth = Math.Clamp(width, _connectAddress.Width, maxWidth);
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"调整地址下拉宽度失败：{ex.Message}");
        }
    }

    /// <summary>选中下拉项时只把 IP 填进文本框，不带上后面的说明。</summary>
    private void ApplySelectedHint()
    {
        if (_populatingHints) return;

        var index = _connectAddress.SelectedIndex;
        if (index < 0 || index >= _addressHints.Count) return;

        var hint = _addressHints[index];
        _selectionVersion++;
        _pendingSelectionAddress = hint.Address;
        _pendingSelectionDisplay = hint.Display;

        SetConnectAddress(hint.Address);

        // 原生 ComboBox 在提交/关闭时会再把编辑框改成整条 Display 文本，
        // 这里先排一次恢复；DropDownClosed 里再兜底一次。
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            NormalizeCommittedSelectionCore(consume: false);
        }));
    }

    private void NormalizeCommittedSelection()
    {
        if (_pendingSelectionAddress is null) return;

        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            NormalizeCommittedSelectionCore(consume: true);
        }));
    }

    /// <summary>
    /// 把编辑框从整条展示文本恢复成纯 IP。支持新格式 "IP - type:id"
    /// 和旧格式 "IP · 说明（来源）"。用户自己改了别的内容时不覆盖；
    /// consume 表示这次是最终收尾。
    /// </summary>
    private void NormalizeCommittedSelectionCore(bool consume)
    {
        var address = _pendingSelectionAddress;
        if (address is null) return;

        var display = _pendingSelectionDisplay;
        var text = _connectAddress.Text;
        var looksNative = text.Length == 0
            || text == address
            || (display is not null && text == display);

        if (looksNative) SetConnectAddress(address);

        if (consume)
        {
            _pendingSelectionAddress = null;
            _pendingSelectionDisplay = null;
        }
    }

    private void SetConnectAddress(string address)
    {
        _connectAddress.Text = address;

        // ComboBox 从长下拉文本（"IP - type:id"）切回纯 IP 时可能保留横向滚动偏移，
        // 表现为只看到 IP 的后半段。先把编辑框拉回开头，再决定光标位置。
        _connectAddress.SelectionStart = 0;
        _connectAddress.SelectionLength = 0;

        if (_connectAddress.Enabled)
        {
            // 手输/可选状态下光标放末尾，方便继续修改；纯 IP 长度足够显示，不会再次滚动。
            _connectAddress.SelectionStart = address.Length;
            _connectAddress.SelectionLength = 0;
        }
    }

    /// <summary>
    /// 兼容旧状态：如果输入框里是整条展示文本（"IP - type:id" 或旧版
    /// "IP · 说明（来源）"），取回前面的纯 IP，避免下次打开下拉时把说明
    /// 也当成地址保留。
    /// </summary>
    private static string? TryExtractAddress(string text)
    {
        var candidate = text.Trim();

        var legacySeparator = candidate.IndexOf("  ·  ", StringComparison.Ordinal);
        if (legacySeparator > 0) candidate = candidate[..legacySeparator].Trim();

        var separator = candidate.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) candidate = candidate[..separator].Trim();

        return IPAddress.TryParse(candidate, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetwork
            ? candidate
            : null;
    }

    /// <summary>
    /// 等价命令按实际换行数撑高，避免用户还要滚动才能看全。
    /// 行数限制在 2–7 行，超长时仍可滚动。
    /// </summary>
    private void ResizePreview()
    {
        if (_preview.IsDisposed || !_preview.IsHandleCreated) return;

        try
        {
            var font = _preview.Font;
            var available = _preview.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8;
            if (available < 80) return;

            var text = _preview.Text.Length == 0 ? " " : _preview.Text;
            var measured = TextRenderer.MeasureText(
                text, font, new Size(available, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            var lineHeight = font.Height;
            var lines = (int)Math.Ceiling(measured.Height / (double)lineHeight);
            lines = Math.Clamp(lines, 2, 7);

            _preview.Height = lines * lineHeight + 10;
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"调整命令预览高度失败：{ex.Message}");
        }
    }

    private PortProxyEntry? BuildEntry(string family, out string error)
    {
        error = string.Empty;

        if (!Validator.TryNormalizeAddress(
                _listenAddress.Text, !family.StartsWith("v4"), out var listen, out var listenError))
        {
            error = listenError;
            return null;
        }

        var connectText = GetCurrentConnectAddress();

        if (!Validator.TryNormalizeAddress(
                connectText, family.EndsWith("v6"), out var connect, out var connectError))
        {
            error = connectError;
            return null;
        }

        return new PortProxyEntry
        {
            Family = family,
            Protocol = "tcp",
            ListenAddress = listen,
            ListenPort = (int)_listenPort.Value,
            ConnectAddress = connect,
            ConnectPort = (int)_connectPort.Value,
        };
    }

    private void OnConfirm()
    {
        _errorLabel.Text = string.Empty;

        if (IsFollowMode && GetCurrentConnectAddress().Length == 0)
        {
            _errorLabel.Text = "请选择跟随目标，或切回固定 IP 手动输入。";
            return;
        }

        var family = ResolveFamily();
        var entry = BuildEntry(family, out var buildError);

        if (entry is null)
        {
            _errorLabel.Text = buildError;
            return;
        }

        var validation = Validator.Validate(entry, _allEntries, _existing?.Key);

        if (!validation.Ok)
        {
            _errorLabel.Text = validation.Error;
            return;
        }

        if (!string.IsNullOrWhiteSpace(validation.Warning))
        {
            var answer = MessageBox.Show(this,
                validation.Warning + "\n\n仍要继续吗？",
                "端口提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;
        }

        if (_existing is not null && !string.Equals(_existing.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(this,
                "监听地址或端口发生了变化：\n\n" +
                $"原：{_existing.Family} {_existing.ListenText}\n" +
                $"新：{entry.Family} {entry.ListenText}\n\n" +
                "portproxy 不支持原地修改，会先删除旧映射再创建新的。继续吗？",
                "确认修改", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;
        }

        if (_firewallBox.Checked && !_elevated)
        {
            _errorLabel.Text = "创建防火墙规则需要管理员权限。";
            return;
        }

        var follow = IsFollowMode ? CurrentFollowTarget() : null;

        Result = entry;
        ResultMeta = new EntryMeta
        {
            Name = Trimmed(_nameBox.Text),
            Group = Trimmed(_groupBox.Text),
            Note = Trimmed(_noteBox.Text),
            HasFirewallRule = _firewallBox.Checked,
            CreatedAt = _existingMeta?.CreatedAt ?? DateTimeOffset.Now,
            FollowKind = follow?.Kind,
            FollowId = follow?.Id,
            FollowMac = follow?.Mac,
            FollowLabel = follow?.Label,
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task TestConnectAsync()
    {
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = "正在测试…";

        var (ok, detail) = await PortProbe.TestConnectAsync(
            GetCurrentConnectAddress(), (int)_connectPort.Value);

        _testResult.ForeColor = ok
            ? Color.FromArgb(0x1B, 0x5E, 0x20)
            : Color.FromArgb(0xB7, 0x1C, 0x1C);
        _testResult.Text = detail;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _guestRefreshTimer.Stop();
            _guestRefreshTimer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string? Trimmed(string value)
    {
        var text = value.Trim();
        return text.Length == 0 ? null : text;
    }
}
