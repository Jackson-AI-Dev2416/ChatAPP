using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace Ts3Messenger.Services;

public static class TaskbarBadgeHelper
{
    public static void SetUnreadCount(Window window, int count)
    {
        window.TaskbarItemInfo ??= new TaskbarItemInfo();
        if (count <= 0)
        {
            window.TaskbarItemInfo.Overlay = null;
            return;
        }

        var text = count > 99 ? "99+" : count.ToString();
        window.TaskbarItemInfo.Overlay = CreateBadge(text);
    }

    private static ImageSource CreateBadge(string text)
    {
        const int size = 16;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(Brushes.Crimson, null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                9,
                Brushes.White,
                VisualTreeHelper.GetDpi(dv).PixelsPerDip);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }
}
