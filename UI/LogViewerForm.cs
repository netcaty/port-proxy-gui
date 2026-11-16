using System.Diagnostics;
using System.Text;

namespace PortProxyManager.UI;

public sealed class LogViewerForm : Form
{
    private readonly string _dir;
    private readonly ComboBox _files = new();
    private readonly TextBox _text = new();
    private readonly Label _status = new();

    public LogViewerForm(string logDir)
    {
        _dir = logDir;
        BuildUi();
        LoadFiles();
    }

    private void BuildUi()
    {
        Text = "操作日志";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9f);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(12, 8, 12, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _files.Width = 220;
        _files.DropDownStyle = ComboBoxStyle.DropDownList;
        _files.SelectedIndexChanged += (_, _) => LoadFile();

        var refresh = new Button { Text = "刷新", AutoSize = true, FlatStyle = FlatStyle.System };
        refresh.Click += (_, _) => { LoadFiles(); LoadFile(); };

        var openDir = new Button { Text = "打开日志目录", AutoSize = true, FlatStyle = FlatStyle.System };
        openDir.Click += (_, _) => OpenDirectory();

        toolbar.Controls.Add(new Label { Text = "日志文件", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
        toolbar.Controls.Add(_files);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(openDir);

        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.ScrollBars = ScrollBars.Vertical;
        _text.WordWrap = true;
        _text.Dock = DockStyle.Fill;
        _text.Font = new Font("Consolas", 9f);
        _text.BackColor = SystemColors.Window;
        _text.BorderStyle = BorderStyle.None;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 26;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(12, 0, 12, 0);
        _status.ForeColor = SystemColors.GrayText;

        var close = new Button
        {
            Text = "关闭",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            FlatStyle = FlatStyle.System,
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(12, 8, 12, 8),
        };
        buttons.Controls.Add(close);

        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(buttons);
        Controls.Add(toolbar);

        CancelButton = close;
    }

    protected override void OnLoad(EventArgs e)
    {
        DpiScale.Apply(this, 880, 560, 600, 400);
        base.OnLoad(e);
    }

    private void LoadFiles()
    {
        _files.Items.Clear();

        try
        {
            if (!Directory.Exists(_dir))
            {
                _status.Text = $"日志目录不存在：{_dir}";
                return;
            }

            var files = Directory.GetFiles(_dir, "app-*.log")
                .OrderByDescending(f => f)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();

            foreach (var file in files) _files.Items.Add(file);

            if (_files.Items.Count > 0) _files.SelectedIndex = 0;
            else _status.Text = "还没有日志文件。";
        }
        catch (Exception ex)
        {
            _status.Text = $"读取日志目录失败：{ex.Message}";
        }
    }

    private void LoadFile()
    {
        if (_files.SelectedItem is not string name) return;

        try
        {
            var path = Path.Combine(_dir, name);
            var content = File.ReadAllText(path, Encoding.UTF8);

            _text.Text = content;
            _text.SelectionStart = 0;
            _text.SelectionLength = 0;
            _status.Text = $"{path}　·　{content.Length} 字符";
        }
        catch (Exception ex)
        {
            _status.Text = $"读取失败：{ex.Message}";
        }
    }

    private void OpenDirectory()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            Process.Start(new ProcessStartInfo { FileName = _dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开目录失败：{ex.Message}", "日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
