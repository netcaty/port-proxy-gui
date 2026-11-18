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
    private List<CandidateItem> _candidateItems = new();
    private int _populateVersion;
    private readonly System.Windows.Forms.Timer _guestRefreshTimer = new() { Interval = ProbeTimeouts.HintRefreshMs };
    private bool _populatingHints;
    private readonly NumericUpDown _connectPort = new();
    private readonly CheckBox _followToggle = new();
    private FollowTarget? _initialFollowTarget;
    private bool _followStoppedByUser;
    private readonly TextBox _nameBox = new();
    private readonly ComboBox _groupBox = new();
    private readonly TextBox _noteBox = new();
    private readonly CheckBox _firewallBox = new();
    private readonly TextBox _preview = new();
    private readonly Label _errorLabel = new();
    private readonly Label _testResult = new();

    /// <summary>
    /// 目标候选下拉里的一项：探测到的地址（可跟随与否由 Hint 决定）、
    /// 编辑离线跟随条目时的兜底项，或"手动填写 IP"入口。
    /// </summary>
    private sealed record CandidateItem(
        AddressHint? Hint, FollowTarget? FallbackTarget, string Display)
    {
        public bool IsManualEntry => Hint is null && FallbackTarget is null;

        public bool IsFollowable =>
            (Hint is not null && TargetFollowService.IsFollowable(Hint))
            || FallbackTarget is not null;

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
        PopulateCandidates();
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

        // 目标候选（单一输入框：手输 IP 或选择探测到的地址/设备）
        _connectAddress.DropDownStyle = ComboBoxStyle.DropDown;
        _connectAddress.Text = "127.0.0.1";
        _connectAddress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _connectAddress.TextChanged += (_, _) =>
        {
            UpdateFollowToggle();
            UpdatePreview();
        };
        _connectAddress.DropDown += (_, _) => PopulateCandidates();
        _connectAddress.SelectionChangeCommitted += (_, _) => OnCandidateCommitted();
        _connectAddress.SelectedIndexChanged += (_, _) => OnSelectedCandidateChanged();
        _connectAddress.DropDownClosed += (_, _) => OnAddressDropDownClosed();
        _tip.SetToolTip(_connectAddress,
            "可手输 IP，或选择网关 / 局域网设备 / WSL / 虚拟机 / 沙盒；可跟随的设备可打开跟随开关。");

        _connectPort.Minimum = 1;
        _connectPort.Maximum = 65535;
        _connectPort.Value = 80;
        _connectPort.TextAlign = HorizontalAlignment.Right;
        _connectPort.Anchor = AnchorStyles.Right;
        _connectPort.Width = 96;
        _connectPort.ValueChanged += (_, _) => UpdatePreview();
        _connectPort.TextChanged += (_, _) => UpdatePreview();
        AddRow(table, "目标候选", _connectAddress, control2: _connectPort);

        // 跟随开关与连通性测试并排；悬停给出说明
        _followToggle.Text = "跟随";
        _followToggle.AutoSize = true;
        _followToggle.Enabled = false;
        _followToggle.Margin = new Padding(0, 6, 8, 3);
        _followToggle.CheckedChanged += (_, _) => UpdatePreview();
        _tip.SetToolTip(_followToggle,
            "对 WSL / 虚拟机 / Windows 沙盒开启 IP 自动跟随；网关 / 局域网设备不支持。");

        var testButton = new Button
        {
            Text = "测试目标连通性",
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 3, 8, 3),
        };
        testButton.Click += async (_, _) => await TestConnectAsync();
        _tip.SetToolTip(testButton, "按当前目标地址与端口测试 TCP 连通性。");

        _testResult.Dock = DockStyle.Fill;
        _testResult.TextAlign = ContentAlignment.MiddleLeft;
        _testResult.ForeColor = SystemColors.GrayText;

        // 用 FlowLayoutPanel 明确排列开关与按钮，避免 Dock 叠加顺序导致控件重叠
        var testActions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Left,
            Margin = new Padding(0),
        };
        testActions.Controls.Add(_followToggle);
        testActions.Controls.Add(testButton);

        var testPanel = new Panel
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = 36,
            Padding = new Padding(0, 0, 8, 0),
        };
        testPanel.Controls.Add(testActions);
        testPanel.Controls.Add(_testResult);
        testActions.MouseHover += (_, _) => ShowFollowTipHint();
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

    /// <summary>
    /// 重建目标候选下拉：第一项为"手动填写 IP"入口，其后是网关 / 局域网设备 /
    /// 已确认运行中的 WSL / 虚拟机 / 沙盒。编辑离线跟随条目时补一个兜底项。
    /// </summary>
    private void PopulateCandidates()
    {
        if (_populatingHints) return;

        _populatingHints = true;
        try
        {
            _populateVersion++;
            _connectAddress.SelectedIndex = -1;

            // 启动时已预热；这里只读缓存，避免展开下拉时被 arp.exe / wsl.exe 阻塞。
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

            var items = new List<CandidateItem>
            {
                new(null, null, "— 手动填写 IP —"),
            };

            foreach (var hint in merged)
            {
                var followable = TargetFollowService.IsFollowable(hint);
                items.Add(new CandidateItem(
                    hint,
                    followable ? FollowTarget.FromHint(hint) : null,
                    hint.Display));
            }

            // 正在跟随的条目：优先选中对应候选；目标未检测到时补一个兜底项
            var previousTarget = _followStoppedByUser ? null : _initialFollowTarget;
            if (previousTarget is not null)
            {
                var matched = items.FirstOrDefault(i =>
                    i.Hint is not null && TargetFollowService.Matches(i.Hint, previousTarget));

                if (matched is null)
                {
                    var fallback = new CandidateItem(
                        null, previousTarget,
                        $"{previousTarget.Display}（当前未检测到，保留原地址）");
                    items.Add(fallback);
                }
            }

            _candidateItems = items;

            _connectAddress.BeginUpdate();
            _connectAddress.Items.Clear();
            foreach (var item in items) _connectAddress.Items.Add(item);
            _connectAddress.EndUpdate();

            if (previousTarget is not null)
            {
                _connectAddress.SelectedItem =
                    items.FirstOrDefault(i =>
                        i.Hint is not null && TargetFollowService.Matches(i.Hint, previousTarget))
                    ?? items.FirstOrDefault(i => i.FallbackTarget == previousTarget);
                _followToggle.Checked = true;
            }
            else
            {
                _connectAddress.SelectedIndex = -1;
                _followToggle.Checked = false;

                var current = _connectAddress.Text;
                if (TryExtractAddress(current) is { } extracted) current = extracted;

                if (current.Length > 0)
                    _connectAddress.Text = current;
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"填充目标候选失败：{ex.Message}");
        }
        finally
        {
            _populatingHints = false;
        }

        UpdateFollowToggle();
        UpdatePreview();
        ResizeDropDownWidth(_candidateItems.Select(i => i.Display).ToList());
    }

    /// <summary>跟随开关只对可跟随的设备（WSL / 虚拟机 / 沙盒）可点击。</summary>
    private void UpdateFollowToggle()
    {
        var item = _connectAddress.SelectedItem as CandidateItem;
        var followable = item?.IsFollowable == true;

        _followToggle.Enabled = followable;
        if (!followable) _followToggle.Checked = false;
    }

    /// <summary>
    /// 跟随开关被禁用时（网关 / 局域网设备 / 手输 IP）不响应 ToolTip 组件，
    /// 这里借面板的 MouseHover 在开关位置补一条悬停说明。
    /// </summary>
    private void ShowFollowTipHint()
    {
        if (_followToggle.Enabled || IsDisposed || !_followToggle.IsHandleCreated) return;

        var parent = _followToggle.Parent;
        if (parent is null) return;

        if (!parent.RectangleToScreen(_followToggle.Bounds).Contains(Cursor.Position)) return;

        _tip.Show(
            "仅 WSL / 虚拟机 / Windows 沙盒可开启跟随；当前选中项不支持。",
            _followToggle, 0, _followToggle.Height + 4, 2500);
    }

    /// <summary>用户在下拉里提交了某个候选：手动填写项清空文本，其余保留展示文本。</summary>
    private void OnCandidateCommitted()
    {
        if (_populatingHints) return;

        if (_connectAddress.SelectedItem is CandidateItem item)
        {
            if (item.IsManualEntry) _connectAddress.Text = string.Empty;
            _followStoppedByUser = false;
        }
        else
        {
            // 手输状态
            _followStoppedByUser = true;
        }

        UpdateFollowToggle();
        UpdatePreview();
    }

    private void OnSelectedCandidateChanged()
    {
        if (_populatingHints) return;

        UpdateFollowToggle();
        UpdatePreview();
    }

    /// <summary>
    /// 当前生效的目标地址：选中候选取其探测地址（离线跟随保留原地址），
    /// 手输状态取输入框文本。
    /// </summary>
    private string GetCurrentConnectAddress()
    {
        if (_connectAddress.SelectedItem is CandidateItem item)
        {
            if (item.Hint?.Address is { Length: > 0 } hintAddress) return hintAddress;
            return _existing?.NormalizedConnect ?? string.Empty;
        }

        return TryExtractAddress(_connectAddress.Text) ?? _connectAddress.Text.Trim();
    }

    /// <summary>
    /// 窗口显示后等待一次候选刷新（如果启动预热还在跑就复用它），
    /// 完成后补填下拉。用户正在展开下拉时不打断选择。
    /// </summary>
    private async Task RefreshCandidatesWhenReadyAsync()
    {
        try
        {
            await GuestHintCache.RefreshNowAsync(force: false);

            if (IsDisposed || _connectAddress.DroppedDown) return;

            PopulateCandidates();
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

    private void OnAddressDropDownClosed()
    {
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

        if (_followToggle.Checked && GetCurrentConnectAddress().Length == 0)
        {
            _errorLabel.Text = "跟随目标当前未检测到，且没有已保存的目标地址。";
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

        var selectedItem = _connectAddress.SelectedItem as CandidateItem;
        var follow = _followToggle.Checked && selectedItem is not null
            ? selectedItem.Hint is not null
                ? FollowTarget.FromHint(selectedItem.Hint)
                : selectedItem.FallbackTarget
            : null;

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
