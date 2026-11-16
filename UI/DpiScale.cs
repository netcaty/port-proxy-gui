namespace PortProxyManager.UI;

/// <summary>
/// 手工构造窗体的 DPI 适配。
///
/// 为什么不用 AutoScaleMode：非设计器创建的窗体在 AutoScaleMode.Dpi 下只把窗体本身
/// 放大，TableLayoutPanel 的绝对列宽、控件默认宽度等"布局输入"并不会被缩放，
/// 结果就是字体按 DPI 变大、控件还是 96 DPI 的尺寸，标签换行、输入框被压扁。
///
/// 所以这里关掉自动缩放：界面全部用 96 DPI 的设计像素搭建，然后在 OnLoad 里
/// 把整棵控件树乘以 dpi/96。字体是 pt 单位，本身随 DPI 变化，不需要处理。
/// </summary>
public static class DpiScale
{
    public static float Factor(Control control)
    {
        var dpi = control.DeviceDpi;
        if (dpi <= 0) dpi = 96;
        return dpi / 96f;
    }

    public static int Value(Control control, int designValue) =>
        (int)Math.Round(designValue * Factor(control));

    public static Size SizeOf(Control control, int designWidth, int designHeight) =>
        new(Value(control, designWidth), Value(control, designHeight));

    /// <summary>
    /// 按设计尺寸设定窗体的客户区与最小尺寸，并把子控件树整体缩放。
    /// 必须从 OnLoad 调用（此时 DeviceDpi 才是准确的）。
    /// </summary>
    public static void Apply(Form form, int designClientWidth, int designClientHeight,
        int? designMinWidth = null, int? designMinHeight = null)
    {
        var factor = Factor(form);

        form.SuspendLayout();
        if (Math.Abs(factor - 1f) > 0.001f) ScaleTree(form, factor);

        form.ClientSize = new Size(
            (int)Math.Round(designClientWidth * factor),
            (int)Math.Round(designClientHeight * factor));

        if (designMinWidth is not null && designMinHeight is not null)
        {
            form.MinimumSize = new Size(
                (int)Math.Round(designMinWidth.Value * factor),
                (int)Math.Round(designMinHeight.Value * factor));
        }

        form.ResumeLayout(true);
    }

    /// <summary>把窗体（不含窗体自身）的子控件树按比例缩放。</summary>
    public static void ScaleTree(Control root, float factor)
    {
        ScaleChildren(root, factor);
    }

    private static void ScaleChildren(Control parent, float factor)
    {
        foreach (Control child in parent.Controls)
        {
            child.SuspendLayout();

            ScaleOne(child, factor);
            ScaleChildren(child, factor);

            child.ResumeLayout(false);
        }

        parent.PerformLayout();
    }

    private static void ScaleOne(Control control, float factor)
    {
        // AutoSize 的控件尺寸由内容（字体）决定，而字体本身已随 DPI 变化，
        // 再乘一次就会翻倍（表现为按钮/复选框巨大）。这类控件只挪位置。
        if (control.AutoSize)
        {
            control.Location = new Point(
                (int)Math.Round(control.Left * factor),
                (int)Math.Round(control.Top * factor));
        }
        else
        {
            var bounds = control.Bounds;
            control.Bounds = new Rectangle(
                (int)Math.Round(bounds.X * factor),
                (int)Math.Round(bounds.Y * factor),
                (int)Math.Round(bounds.Width * factor),
                (int)Math.Round(bounds.Height * factor));
        }

        control.Margin = ScalePadding(control.Margin, factor);
        control.Padding = ScalePadding(control.Padding, factor);

        if (!control.MinimumSize.IsEmpty)
        {
            control.MinimumSize = new Size(
                (int)Math.Round(control.MinimumSize.Width * factor),
                (int)Math.Round(control.MinimumSize.Height * factor));
        }

        if (!control.MaximumSize.IsEmpty)
        {
            control.MaximumSize = new Size(
                (int)Math.Round(control.MaximumSize.Width * factor),
                (int)Math.Round(control.MaximumSize.Height * factor));
        }

        switch (control)
        {
            case TableLayoutPanel table:
                foreach (ColumnStyle style in table.ColumnStyles)
                {
                    if (style.SizeType == SizeType.Absolute)
                        style.Width = (float)Math.Round(style.Width * factor);
                }
                foreach (RowStyle style in table.RowStyles)
                {
                    if (style.SizeType == SizeType.Absolute)
                        style.Height = (float)Math.Round(style.Height * factor);
                }
                break;

            case DataGridView grid:
                grid.ColumnHeadersHeight = (int)Math.Round(grid.ColumnHeadersHeight * factor);
                grid.RowTemplate.Height = (int)Math.Round(grid.RowTemplate.Height * factor);
                grid.ColumnHeadersDefaultCellStyle.Padding =
                    ScalePadding(grid.ColumnHeadersDefaultCellStyle.Padding, factor);
                grid.DefaultCellStyle.Padding = ScalePadding(grid.DefaultCellStyle.Padding, factor);
                break;

            case ToolStrip strip:
                strip.ImageScalingSize = new Size(
                    (int)Math.Round(strip.ImageScalingSize.Width * factor),
                    (int)Math.Round(strip.ImageScalingSize.Height * factor));

                // ToolStripItem 不在 Controls 树里，必须单独处理
                foreach (ToolStripItem item in strip.Items)
                {
                    item.Margin = ScalePadding(item.Margin, factor);
                    item.Padding = ScalePadding(item.Padding, factor);

                    if (!item.AutoSize && item is ToolStripTextBox or ToolStripComboBox)
                        item.Width = (int)Math.Round(item.Width * factor);
                }
                break;
        }
    }

    private static Padding ScalePadding(Padding padding, float factor) => new(
        (int)Math.Round(padding.Left * factor),
        (int)Math.Round(padding.Top * factor),
        (int)Math.Round(padding.Right * factor),
        (int)Math.Round(padding.Bottom * factor));
}
