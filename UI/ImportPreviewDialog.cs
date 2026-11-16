using System.ComponentModel;
using PortProxyManager.Services;

namespace PortProxyManager.UI;

public sealed class ImportPreviewDialog : Form
{
    private sealed class Row
    {
        public bool Selected { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Listen { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public DiffItem Item { get; set; } = null!;
    }

    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly BindingList<Row> _rows;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<DiffItem> SelectedItems { get; private set; } = Array.Empty<DiffItem>();

    public ImportPreviewDialog(IReadOnlyList<DiffItem> diff, string fileName, bool elevated)
    {
        _rows = new BindingList<Row>(diff.Select(item => new Row
        {
            Selected = item.Kind is DiffKind.Add or DiffKind.Update,
            Kind = item.KindLabel,
            Type = item.Entry.Family,
            Listen = item.Entry.ListenText,
            Target = item.Entry.ConnectText,
            Name = item.Meta?.Name ?? string.Empty,
            Item = item,
        }).ToList());

        BuildUi(diff, fileName, elevated);
        UpdateSummary();
    }

    private void BuildUi(IReadOnlyList<DiffItem> diff, string fileName, bool elevated)
    {
        Text = "导入预览";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9f);

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(14, 10, 14, 6),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"来自：{fileName}\n勾选要应用的条目；「删除」默认不勾选，避免误删现有映射。",
            AutoEllipsis = true,
        };

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.None;
        _grid.ColumnHeadersHeight = 30;
        _grid.RowTemplate.Height = 26;
        _grid.DataSource = _rows;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(Row.Selected),
            HeaderText = "应用",
            Width = 52,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.Kind),
            HeaderText = "操作",
            Width = 70,
            ReadOnly = true,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.Type),
            HeaderText = "类型",
            Width = 80,
            ReadOnly = true,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.Listen),
            HeaderText = "监听",
            Width = 150,
            ReadOnly = true,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.Target),
            HeaderText = "目标",
            Width = 150,
            ReadOnly = true,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.Name),
            HeaderText = "名称",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true,
        });

        _grid.CellFormatting += (_, e) =>
        {
            if (_grid.Columns[e.ColumnIndex].DataPropertyName != nameof(Row.Kind)) return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is not Row row) return;

            e.CellStyle!.ForeColor = row.Item.Kind switch
            {
                DiffKind.Add => Color.FromArgb(0x1B, 0x5E, 0x20),
                DiffKind.Update => Color.FromArgb(0x8A, 0x5A, 0x00),
                DiffKind.Delete => Color.FromArgb(0xB7, 0x1C, 0x1C),
                _ => SystemColors.GrayText,
            };
        };

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            UpdateSummary();
        };

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = 30;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Padding = new Padding(14, 0, 14, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(14, 8, 14, 8),
        };

        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(84, 26),
            FlatStyle = FlatStyle.System,
        };

        var ok = new Button
        {
            Text = "应用选中项",
            AutoSize = true,
            MinimumSize = new Size(104, 26),
            FlatStyle = FlatStyle.System,
            Enabled = elevated,
        };
        ok.Click += (_, _) => Confirm();

        var toggle = new Button
        {
            Text = "全选 / 全不选",
            AutoSize = true,
            MinimumSize = new Size(104, 26),
            FlatStyle = FlatStyle.System,
        };
        toggle.Click += (_, _) =>
        {
            var allChecked = _rows.All(r => r.Selected);
            foreach (var row in _rows) row.Selected = !allChecked;
            _rows.ResetBindings();
            _grid.Refresh();
            UpdateSummary();
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(toggle);

        if (!elevated)
        {
            _summary.Text = "当前不是管理员身份，无法应用导入，请以管理员身份重新启动。";
            _summary.ForeColor = Color.FromArgb(0xB7, 0x1C, 0x1C);
        }

        Controls.Add(_grid);
        Controls.Add(_summary);
        Controls.Add(buttons);
        Controls.Add(header);

        AcceptButton = ok;
        CancelButton = cancel;

        // 集合顺序决定停靠结果：Fill 必须先加入
        Controls.SetChildIndex(_grid, 0);
    }

    protected override void OnLoad(EventArgs e)
    {
        DpiScale.Apply(this, 760, 480, 620, 380);
        base.OnLoad(e);
    }

    private void UpdateSummary()
    {
        if (_summary.ForeColor == Color.FromArgb(0xB7, 0x1C, 0x1C)) return;

        var checkedRows = _rows.Where(r => r.Selected).ToList();
        var add = checkedRows.Count(r => r.Item.Kind == DiffKind.Add);
        var update = checkedRows.Count(r => r.Item.Kind == DiffKind.Update);
        var delete = checkedRows.Count(r => r.Item.Kind == DiffKind.Delete);
        var same = checkedRows.Count(r => r.Item.Kind == DiffKind.Same);

        _summary.ForeColor = SystemColors.ControlText;
        _summary.Text = $"将应用 {checkedRows.Count} 项：新增 {add}，覆盖 {update}，删除 {delete}，无变化 {same}" +
                        $"　（文件共 {_rows.Count} 条）";
    }

    private void Confirm()
    {
        _grid.EndEdit();

        var selected = _rows.Where(r => r.Selected).Select(r => r.Item).ToList();

        if (selected.Any(i => i.Kind == DiffKind.Delete))
        {
            var count = selected.Count(i => i.Kind == DiffKind.Delete);
            var answer = MessageBox.Show(this,
                $"有 {count} 条现有映射会被删除，确定继续吗？\n\n执行前会自动备份当前配置。",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;
        }

        SelectedItems = selected;
        DialogResult = DialogResult.OK;
        Close();
    }
}
