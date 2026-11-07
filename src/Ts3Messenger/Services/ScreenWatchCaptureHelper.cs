using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ts3Messenger.Services;

public static class ScreenWatchCaptureHelper
{
    private const int MaxLongEdge = 1600;
    private const long JpegQuality = 60L;

    public sealed record MonitorFrame(int Index, int Count, byte[] Jpeg);

    public static IReadOnlyList<MonitorFrame> CaptureAllMonitorsJpeg()
    {
        var screens = Screen.AllScreens;
        var count = screens.Length;
        if (count == 0)
            return Array.Empty<MonitorFrame>();

        var frames = new List<MonitorFrame>(count);
        for (var i = 0; i < count; i++)
        {
            var screen = screens[i];
            var b = screen.Bounds;
            frames.Add(new MonitorFrame(i + 1, count, CaptureBoundsJpeg(b.X, b.Y, b.Width, b.Height)));
        }
        return frames;
    }

    private static byte[] CaptureBoundsJpeg(int x, int y, int w, int h)
    {
        if (w < 2 || h < 2)
            return Array.Empty<byte>();

        using var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        using var scaled = ScaleDown(bitmap, MaxLongEdge);
        return EncodeJpeg(scaled, JpegQuality);
    }

    private static Bitmap ScaleDown(Bitmap source, int maxLongEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxLongEdge)
            return (Bitmap)source.Clone();

        var scale = maxLongEdge / (double)longEdge;
        var nw = Math.Max(1, (int)Math.Round(source.Width * scale));
        var nh = Math.Max(1, (int)Math.Round(source.Height * scale));
        var dest = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dest))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, nw, nh);
        }
        return dest;
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bitmap.Save(ms, codec, ep);
        return ms.ToArray();
    }
}
