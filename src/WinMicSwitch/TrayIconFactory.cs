using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinMicSwitch;

internal static class TrayIconFactory
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);

    internal static Color Foreground()
    {
        if (SystemInformation.HighContrast) return SystemColors.WindowText;
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0);
        return light is 1 ? Color.Black : Color.White;
    }

    internal static Icon Create(string status, string number, int dpi, Color ink)
    {
        int size = Math.Clamp(32 * dpi / 96, 32, 128);
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.ScaleTransform(size / 32f, size / 32f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ink);
        using var pen = new Pen(ink, 1.85f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var original = g.Save();
        bool stacked = number.Length >= 3;
        if (stacked) { g.TranslateTransform(10, 0); g.ScaleTransform(.65f, .65f); }
        else if (number.Length == 2) g.ScaleTransform(.8f, 1);
        if (status == Ui.Unknown) Glyph(g, "?", new RectangleF(1, 1, 14, 30), brush);
        else if (status == Ui.NoMic)
        {
            g.DrawEllipse(pen, 2, 6, 13, 20);
            g.DrawLine(pen, 3, 23, 14, 9);
        }
        else
        {
            g.DrawRoundedRectangle(pen, new RectangleF(4.75f, 1.5f, 7.5f, 19), new SizeF(3.75f, 3.75f));
            g.DrawLine(pen, 1.5f, 15, 1.5f, 18);
            g.DrawLine(pen, 15.5f, 15, 15.5f, 18);
            g.DrawArc(pen, 1.5f, 11, 14, 14, 0, 180);
            g.DrawLine(pen, 8.5f, 25, 8.5f, 30);
            g.DrawLine(pen, 3, 30, 14, 30);
            if (status == Ui.Muted)
            {
                // Cut a transparent gap around the slash at tray resolution.
                g.CompositingMode = CompositingMode.SourceCopy;
                using var gap = new Pen(Color.Transparent, 4.5f);
                g.DrawLine(gap, 1, 25, 16, 4);
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawLine(pen, 1, 25, 16, 4);
            }
        }
        g.Restore(original);
        var area = stacked ? new RectangleF(1, 21, 30, 10) :
            number.Length == 2 ? new RectangleF(15, 2, 16, 28) : new RectangleF(19, 2, 12, 28);
        Glyph(g, number, area, brush);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void Glyph(Graphics g, string text, RectangleF area, Brush brush)
    {
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        path.AddString(text, family, (int)FontStyle.Regular, 32, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        float sy = Math.Min(area.Height / bounds.Height, area.Width / bounds.Width * 1.6f);
        float sx = Math.Min(sy, area.Width / bounds.Width);
        using var transform = new Matrix(sx, 0, 0, sy,
            area.X + (area.Width - bounds.Width * sx) / 2 - bounds.X * sx,
            area.Y + (area.Height - bounds.Height * sy) / 2 - bounds.Y * sy);
        path.Transform(transform);
        g.FillPath(brush, path);
    }
}
