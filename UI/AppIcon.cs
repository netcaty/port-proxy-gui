using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PortProxyManager.UI;

/// <summary>程序化生成图标，避免带二进制资源文件。</summary>
public static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var scale = size / 32f;
            var rect = new RectangleF(1.5f * scale, 1.5f * scale, size - 3 * scale, size - 3 * scale);

            using (var background = new SolidBrush(Color.FromArgb(0x1F, 0x4B, 0x78)))
            using (var rounded = RoundedRect(rect, 7f * scale))
            {
                g.FillPath(background, rounded);
            }

            using var pen = new Pen(Color.White, Math.Max(1.6f, 2.1f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            var topY = 12f * scale;
            var bottomY = 20f * scale;
            g.DrawLine(pen, 7 * scale, topY, 20 * scale, topY);
            g.DrawLine(pen, 12 * scale, bottomY, 25 * scale, bottomY);

            using (var brush = new SolidBrush(Color.White))
            {
                g.FillPolygon(brush, new[]
                {
                    new PointF(20 * scale, topY - 4.2f * scale),
                    new PointF(20 * scale, topY + 4.2f * scale),
                    new PointF(26 * scale, topY),
                });

                g.FillPolygon(brush, new[]
                {
                    new PointF(12 * scale, bottomY - 4.2f * scale),
                    new PointF(12 * scale, bottomY + 4.2f * scale),
                    new PointF(6 * scale, bottomY),
                });
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
