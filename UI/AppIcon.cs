using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AIMeter.UI;

/// <summary>Draws the gauge icon at runtime so the project ships without binary assets.</summary>
internal static class AppIcon
{
    public static Icon Create(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pad = size * 0.06f, d = size - pad * 2;
            using var bg = new SolidBrush(Color.FromArgb(255, 30, 30, 36));
            g.FillEllipse(bg, pad, pad, d, d);

            float w = size * 0.14f, inset = pad + w;
            var arc = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
            using var track = new Pen(Color.FromArgb(90, 255, 255, 255), w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var claude = new Pen(Color.FromArgb(255, 217, 119, 87), w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var codex = new Pen(Color.FromArgb(255, 63, 185, 138), w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(track, arc, 135, 270);
            g.DrawArc(claude, arc, 135, 150);
            g.DrawArc(codex, arc, 300, 90);
        }
        // Created once per run; the HICON lives for the process lifetime.
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static BitmapSource ToImageSource(Icon icon)
        => Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
}
