using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingIcon = System.Drawing.Icon;
using DrawingSmoothing = System.Drawing.Drawing2D.SmoothingMode;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ts3Messenger.Services;

public static class AppIcon
{
    private static ImageSource? _icon;

    public static ImageSource Icon => _icon ??= CreateChatIcon();

    public static void ApplyToWindow(Window window) => window.Icon = Icon;

    public static void NotifyShell()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            SHChangeNotify(0x00002000, 0x0005 | 0x1000, path, null);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"아이콘 갱신 알림 실패: {ex.Message}");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string? item1, string? item2);

    public static DrawingIcon CreateNotifyIcon(bool unread = false)
    {
        using var bmp = RenderToBitmap(32);
        if (unread)
            DrawUnreadDot(bmp);
        var handle = bmp.GetHicon();
        try
        {
            using var temp = DrawingIcon.FromHandle(handle);
            return (DrawingIcon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static void SaveAppIco(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = RenderToBitmap(32);
        var handle = bmp.GetHicon();
        try
        {
            using var temp = DrawingIcon.FromHandle(handle);
            using var fs = File.Create(path);
            temp.Save(fs);
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static ImageSource CreateChatIcon()
    {
        var dv = CreateDrawingVisual(64);
        var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawUnreadDot(DrawingBitmap bmp)
    {
        using var g = DrawingGraphics.FromImage(bmp);
        g.SmoothingMode = DrawingSmoothing.AntiAlias;
        var dot = bmp.Width * 0.34f;
        var x = bmp.Width - dot - 1f;
        var y = 1f;
        using var ring = new System.Drawing.SolidBrush(DrawingColor.White);
        g.FillEllipse(ring, x - 1.6f, y - 1.6f, dot + 3.2f, dot + 3.2f);
        using var red = new System.Drawing.SolidBrush(DrawingColor.FromArgb(239, 68, 68));
        g.FillEllipse(red, x, y, dot, dot);
    }

    private static DrawingBitmap RenderToBitmap(int size)
    {
        var dv = CreateDrawingVisual(size);
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return new DrawingBitmap(ms);
    }

    private static DrawingVisual CreateDrawingVisual(int size)
    {
        var dv = new DrawingVisual();
        using var dc = dv.RenderOpen();
        var u = size / 64.0;
        var radius = 18 * u;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, size, size), radius, radius));

        var tile = new LinearGradientBrush(
            Color.FromRgb(56, 189, 248),
            Color.FromRgb(79, 70, 229),
            new Point(0, 0),
            new Point(size, size));
        dc.DrawRectangle(tile, null, new Rect(0, 0, size, size));

        var sheen = new RadialGradientBrush(
            Color.FromArgb(90, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255))
        {
            Center = new Point(0.32, 0.18),
            GradientOrigin = new Point(0.32, 0.18),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };
        dc.DrawRectangle(sheen, null, new Rect(0, 0, size, size));

        var body = new Rect(13 * u, 13 * u, 38 * u, 28 * u);
        var corner = 13 * u;
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(36, 15, 23, 42)),
            null,
            new Rect(body.X, body.Y + 1.5 * u, body.Width, body.Height),
            corner,
            corner);
        dc.DrawRoundedRectangle(Brushes.White, null, body, corner, corner);
        dc.DrawEllipse(Brushes.White, null, new Point(24 * u, 42 * u), 5.5 * u, 5.5 * u);
        dc.DrawEllipse(Brushes.White, null, new Point(17 * u, 48 * u), 3.4 * u, 3.4 * u);

        var ink = new SolidColorBrush(Color.FromRgb(67, 56, 202));
        var dot = 3.15 * u;
        foreach (var x in new[] { 24.0, 32.0, 40.0 })
            dc.DrawEllipse(ink, null, new Point(x * u, 27 * u), dot, dot);

        dc.Pop();
        return dv;
    }
}
