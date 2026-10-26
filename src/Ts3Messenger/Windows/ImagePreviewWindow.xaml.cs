using System.Windows;
using System.Windows.Input;
using Ts3Messenger.Services;

namespace Ts3Messenger.Windows;

public partial class ImagePreviewWindow : Window
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4;
    private double _zoom = 1;

    public ImagePreviewWindow(string imagePath, Window? owner)
    {
        InitializeComponent();
        Owner = owner;
        var src = ScreenCaptureService.TryLoadImage(imagePath);
        if (src != null)
            PreviewImage.Source = src;
        UpdateZoomChrome();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.25, ViewportCenter());

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25, ViewportCenter());

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        e.Handled = true;
        ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1, e.GetPosition(ImageScroll));
    }

    private Point ViewportCenter() => new(ImageScroll.ViewportWidth / 2, ImageScroll.ViewportHeight / 2);

    private void ZoomBy(double factor, Point originInScroll)
    {
        var next = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.001)
            return;

        var contentX = ImageScroll.HorizontalOffset + originInScroll.X;
        var contentY = ImageScroll.VerticalOffset + originInScroll.Y;
        var scale = next / _zoom;
        _zoom = next;
        ImageScale.ScaleX = ImageScale.ScaleY = _zoom;
        UpdateZoomChrome();
        ImageScroll.UpdateLayout();
        ImageScroll.ScrollToHorizontalOffset(contentX * scale - originInScroll.X);
        ImageScroll.ScrollToVerticalOffset(contentY * scale - originInScroll.Y);
    }

    private void UpdateZoomChrome()
    {
        ZoomLabel.Text = $"{_zoom * 100:0}%";
        ZoomOutButton.IsEnabled = _zoom > MinZoom + 0.001;
        ZoomInButton.IsEnabled = _zoom < MaxZoom - 0.001;
    }
}
