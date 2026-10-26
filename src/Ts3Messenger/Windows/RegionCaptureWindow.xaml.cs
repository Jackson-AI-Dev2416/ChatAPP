using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace Ts3Messenger.Windows;

public partial class RegionCaptureWindow : Window
{
    private Point _start;
    private bool _dragging;
    public Rect? SelectedRegion { get; private set; }

    /// <summary>Physical screen pixels, computed before the window closes.</summary>
    public (int X, int Y, int Width, int Height)? SelectedRegionScreenPixels { get; private set; }

    public RegionCaptureWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        WindowState = WindowState.Normal;
        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectedRegion = null;
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Enter && SelectedRegion is { Width: >= 4, Height: >= 4 })
        {
            CommitSelection();
            DialogResult = true;
            Close();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(RootCanvas);
        CaptureMouse();
        UpdateSelection(_start, _start);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        UpdateSelection(_start, e.GetPosition(RootCanvas));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var end = e.GetPosition(RootCanvas);
        UpdateSelection(_start, end);
        if (SelectedRegion is { Width: >= 4, Height: >= 4 })
        {
            CommitSelection();
            DialogResult = true;
            Close();
        }
    }

    private void CommitSelection()
    {
        if (SelectedRegion is not Rect region || region.Width < 4 || region.Height < 4)
            return;
        var topLeft = PointToScreen(region.TopLeft);
        var bottomRight = PointToScreen(region.BottomRight);
        var x = (int)Math.Floor(Math.Min(topLeft.X, bottomRight.X));
        var y = (int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y));
        var w = (int)Math.Ceiling(Math.Abs(bottomRight.X - topLeft.X));
        var h = (int)Math.Ceiling(Math.Abs(bottomRight.Y - topLeft.Y));
        if (w >= 2 && h >= 2)
            SelectedRegionScreenPixels = (x, y, w, h);
    }

    private void UpdateSelection(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Abs(a.X - b.X);
        var h = Math.Abs(a.Y - b.Y);
        SelectedRegion = new Rect(x, y, w, h);
        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
        SelectionBorder.Width = w;
        SelectionBorder.Height = h;
        SelectionBorder.Visibility = Visibility.Visible;
    }
}
