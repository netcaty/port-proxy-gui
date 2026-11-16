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
    private List<AddressHint> _wslHints = new();
    private bool _wslProbed;
    private bool _populatingHints;
    private readonly NumericUpDown _connectPort = new();
    private readonly TextBox _nameBox = new();
    private readonly ComboBox _groupBox = new();
    private readonly TextBox _noteBox = new();
    private readonly CheckBox _firewallBox = new();
    private readonly TextBox _preview = new();
    private readonly Label _errorLabel = new();
    private readonly Label _testResult = new();

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
        UpdatePreview();

        // 句柄创建后控件才有真实宽度，这时再按内容定高一次
        Shown += (_, _) => ResizePreview();
    }

    protected override void OnLoad(EventArgs e)
    {
        DpiScale.Apply(this, 560, 556, 480, 500);
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

        // 目标地址 / 端口（可编辑下拉：可手输，也可选自动探测到的地址）
        _connectAddress.DropDownStyle = ComboBoxStyle.DropDown;
        _connectAddress.Text = "127.0.0.1";
        _connectAddress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _connectAddress.TextChanged += (_, _) => UpdatePreview();
        _connectAddress.DropDown += (_, _) => PopulateAddressHints();
        _connectAddress.SelectedIndexChanged += (_, _) => ApplySelectedHint();
        _tip.SetToolTip(_connectAddress,
            "可直接输入 IP；下拉里是本机网卡、默认网关、虚拟网卡、WSL 客户机与 ARP 邻居缓存");
        _connectPort.Minimum = 1;
        _connectPort.Maximum = 65535;
        _connectPort.Value = 80;
        _connectPort.TextAlign = HorizontalAlignment.Right;
        _connectPort.Anchor = AnchorStyles.Right;
        _connectPort.Width = 96;
        _connectPort.ValueChanged += (_, _) => UpdatePreview();
        _connectPort.TextChanged += (_, _) => UpdatePreview();
        AddRow(table, "目标地址", _connectAddress, control2: _connectPort);

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
        _connectPort.Value = Math.Clamp(_existing.ConnectPort, 1, 65535);

        if (_existingMeta is not null)
        {
            _nameBox.Text = _existingMeta.Name ?? string.Empty;
            _groupBox.Text = _existingMeta.Group ?? string.Empty;
            _noteBox.Text = _existingMeta.Note ?? string.Empty;
            _firewallBox.Checked = _existingMeta.HasFirewallRule;
        }
    }

    private string ResolveFamily()
    {
        if (_familyBox.SelectedItem is string selected && !selected.StartsWith("自动"))
            return selected;

        return Validator.InferFamily(_listenAddress.Text, _connectAddress.Text);
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
    /// 填充目标地址候选。本机网卡/网关/虚拟网卡/邻居缓存是同步取的（很快），
    /// WSL 客户机地址需要启动 wsl.exe，单独异步探测，回来后补进列表。
    /// </summary>
    private void PopulateAddressHints()
    {
        if (_populatingHints) return;

        _populatingHints = true;
        try
        {
            var current = _connectAddress.Text;

            var merged = new List<AddressHint>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hint in _wslHints.Concat(NetworkHintService.GetHints()))
                if (seen.Add(hint.Address)) merged.Add(hint);

            _addressHints = merged;

            _connectAddress.BeginUpdate();
            _connectAddress.Items.Clear();
            foreach (var hint in merged) _connectAddress.Items.Add(hint.Display);
            _connectAddress.EndUpdate();

            if (current.Length > 0) _connectAddress.Text = current;
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"填充地址候选失败：{ex.Message}");
        }
        finally
        {
            _populatingHints = false;
        }

        if (!_wslProbed)
        {
            _wslProbed = true;
            _ = ProbeWslAsync();
        }
    }

    private async Task ProbeWslAsync()
    {
        try
        {
            var hints = await NetworkHintService.GetWslHintsAsync().ConfigureAwait(true);
            if (IsDisposed || hints.Count == 0) return;

            _wslHints = hints.ToList();

            // 下拉还开着就把新条目补进去
            if (_connectAddress.DroppedDown)
            {
                _connectAddress.DroppedDown = false;
                PopulateAddressHints();
                _connectAddress.DroppedDown = true;
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"WSL 地址探测失败：{ex.Message}");
        }
    }

    /// <summary>选中下拉项时只把 IP 填进文本框，不带上后面的说明。</summary>
    private void ApplySelectedHint()
    {
        if (_populatingHints) return;

        var index = _connectAddress.SelectedIndex;
        if (index < 0 || index >= _addressHints.Count) return;

        _connectAddress.Text = _addressHints[index].Address;
        _connectAddress.SelectionStart = _connectAddress.Text.Length;
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

        if (!Validator.TryNormalizeAddress(
                _connectAddress.Text, family.EndsWith("v6"), out var connect, out var connectError))
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

        Result = entry;
        ResultMeta = new EntryMeta
        {
            Name = Trimmed(_nameBox.Text),
            Group = Trimmed(_groupBox.Text),
            Note = Trimmed(_noteBox.Text),
            HasFirewallRule = _firewallBox.Checked,
            CreatedAt = _existingMeta?.CreatedAt ?? DateTimeOffset.Now,
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task TestConnectAsync()
    {
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = "正在测试…";

        var (ok, detail) = await PortProbe.TestConnectAsync(
            _connectAddress.Text.Trim(), (int)_connectPort.Value);

        _testResult.ForeColor = ok
            ? Color.FromArgb(0x1B, 0x5E, 0x20)
            : Color.FromArgb(0xB7, 0x1C, 0x1C);
        _testResult.Text = detail;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    private static string? Trimmed(string value)
    {
        var text = value.Trim();
        return text.Length == 0 ? null : text;
    }
}
