using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Ts3Messenger.Services;

namespace Ts3Messenger.Windows;

public partial class SnapshotAnnotateWindow : Window
{
    private enum DrawTool
    {
        Pencil,
        Line,
        Rectangle,
    }

    private readonly string _sourcePath;
    private readonly List<Button> _toolButtons = new();
    private readonly List<Button> _colorButtons = new();
    private BitmapSource? _bitmap;
    private DrawTool _tool = DrawTool.Pencil;
    private System.Windows.Media.Color _strokeColor = Colors.Red;
    private bool _drawing;
    private Point _startPoint;
    private Polyline? _activePencil;
    private Shape? _previewShape;

    public string? ResultPath { get; private set; }

    public SnapshotAnnotateWindow(string imagePath)
    {
        _sourcePath = imagePath;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _bitmap = ScreenCaptureService.TryLoadImage(_sourcePath);
        if (_bitmap == null)
        {
            AppDialog.Show("Could not load the snapshot.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        SourceImage.Source = _bitmap;
        var w = _bitmap.PixelWidth;
        var h = _bitmap.PixelHeight;
        DrawCanvas.Width = w;
        DrawCanvas.Height = h;
        ImageHost.Width = w;
        ImageHost.Height = h;

        BuildToolbar();
        UpdateToolSelection();
        UpdateColorSelection();
    }

    private void BuildToolbar()
    {
        ExternalToolsPanel.Children.Clear();
        _toolButtons.Clear();
        _colorButtons.Clear();

        _toolButtons.Add(MakeToolButton(DrawTool.Pencil, "Pencil", CreatePencilIconVisual()));
        _toolButtons.Add(MakeToolButton(DrawTool.Line, "Line", CreateLineIconVisual()));
        _toolButtons.Add(MakeToolButton(DrawTool.Rectangle, "Rectangle", CreateRectIconVisual()));
        foreach (var b in _toolButtons)
            ExternalToolsPanel.Children.Add(b);

        ExternalToolsPanel.Children.Add(new Border { Width = 12 });

        foreach (var color in new[]
                 {
                     Colors.Red, Colors.Blue, Colors.LimeGreen, Colors.Gold, Colors.White, Colors.Black,
                 })
        {
            var swatch = MakeColorSwatch(color);
            _colorButtons.Add(swatch);
            ExternalToolsPanel.Children.Add(swatch);
        }

        ExternalToolsPanel.Children.Add(new Border { Width = 16 });

        var cancel = new Button
        {
            Content = "Cancel",
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(16, 6, 16, 6),
            Style = (Style)FindResource("SecondaryButtonStyle"),
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        ExternalToolsPanel.Children.Add(cancel);

        var send = new Button
        {
            Content = "Send",
            Padding = new Thickness(20, 6, 20, 6),
            Style = (Style)FindResource("PrimaryButtonStyle"),
        };
        send.Click += (_, _) => OnSendClick();
        ExternalToolsPanel.Children.Add(send);
    }

    private Button MakeToolButton(DrawTool tool, string toolTip, UIElement icon)
    {
        var btn = new Button
        {
            Content = icon,
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8),
            Tag = tool,
            ToolTip = toolTip,
            Style = (Style)FindResource("SecondaryButtonStyle"),
        };
        btn.Click += (_, _) =>
        {
            _tool = tool;
            UpdateToolSelection();
        };
        return btn;
    }

    private void UpdateToolSelection()
    {
        foreach (var btn in _toolButtons)
        {
            if (btn.Tag is DrawTool t)
                btn.Style = (Style)FindResource(t == _tool ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        }
    }

    private static UIElement CreatePencilIconVisual()
    {
        var viewbox = new Viewbox { Width = 22, Height = 22, Stretch = Stretch.Uniform };
        var canvas = new Canvas { Width = 24, Height = 24 };
        var body = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,19 L7,21 L20,8 L16,4 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(250, 204, 21)),
            Stroke = new SolidColorBrush(Color.FromRgb(180, 130, 20)),
            StrokeThickness = 0.6,
        };
        var tip = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M16,4 L20,0 L24,4 L20,8 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            Stroke = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            StrokeThickness = 0.5,
        };
        var wood = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,19 L1,23 L5,21 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            Stroke = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
            StrokeThickness = 0.4,
        };
        canvas.Children.Add(body);
        canvas.Children.Add(tip);
        canvas.Children.Add(wood);
        viewbox.Child = canvas;
        return viewbox;
    }

    private static UIElement CreateLineIconVisual()
    {
        var ink = new SolidColorBrush(Color.FromRgb(15, 23, 42));
        var viewbox = new Viewbox { Width = 22, Height = 22, Stretch = Stretch.Uniform };
        var grid = new Grid { Width = 24, Height = 24 };
        var line = new Line
        {
            X1 = 4,
            Y1 = 18,
            X2 = 20,
            Y2 = 6,
            Stroke = ink,
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        var a = new Ellipse { Width = 4, Height = 4, Fill = ink, Margin = new Thickness(2, 16, 0, 0) };
        var b = new Ellipse { Width = 4, Height = 4, Fill = ink, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 2, 0) };
        grid.Children.Add(line);
        grid.Children.Add(a);
        grid.Children.Add(b);
        viewbox.Child = grid;
        return viewbox;
    }

    private static UIElement CreateRectIconVisual()
    {
        var ink = new SolidColorBrush(Color.FromRgb(15, 23, 42));
        var viewbox = new Viewbox { Width = 22, Height = 22, Stretch = Stretch.Uniform };
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = 16,
            Height = 13,
            RadiusX = 2,
            RadiusY = 2,
            Stroke = ink,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 37, 99, 235)),
            Margin = new Thickness(4, 5, 0, 0),
        };
        viewbox.Child = rect;
        return viewbox;
    }

    private Button MakeColorSwatch(System.Windows.Media.Color color)
    {
        var btn = new Button
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(0),
            Tag = color,
            Background = new SolidColorBrush(color),
        };
        btn.Click += (_, _) =>
        {
            if (btn.Tag is System.Windows.Media.Color c)
            {
                _strokeColor = c;
                UpdateColorSelection();
            }
        };
        return btn;
    }

    private void UpdateColorSelection()
    {
        var accent = (Brush)FindResource("AccentBrush");
        foreach (var btn in _colorButtons)
        {
            if (btn.Tag is not System.Windows.Media.Color c)
                continue;
            var selected = c == _strokeColor;
            btn.BorderBrush = selected ? accent : new SolidColorBrush(Color.FromRgb(148, 163, 184));
            btn.BorderThickness = selected ? new Thickness(3) : new Thickness(1);
            btn.Width = selected ? 32 : 28;
            btn.Height = selected ? 32 : 28;
        }
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap == null) return;
        _drawing = true;
        _startPoint = e.GetPosition(DrawCanvas);
        DrawCanvas.CaptureMouse();

        if (_tool == DrawTool.Pencil)
        {
            _activePencil = new Polyline
            {
                Stroke = new SolidColorBrush(_strokeColor),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            _activePencil.Points.Add(_startPoint);
            DrawCanvas.Children.Add(_activePencil);
        }
        else if (_tool == DrawTool.Line)
        {
            _previewShape = new Line
            {
                X1 = _startPoint.X,
                Y1 = _startPoint.Y,
                X2 = _startPoint.X,
                Y2 = _startPoint.Y,
                Stroke = new SolidColorBrush(_strokeColor),
                StrokeThickness = 3,
            };
            DrawCanvas.Children.Add(_previewShape);
        }
        else
        {
            _previewShape = new Rectangle
            {
                Width = 0,
                Height = 0,
                Stroke = new SolidColorBrush(_strokeColor),
                StrokeThickness = 3,
                Fill = Brushes.Transparent,
            };
            Canvas.SetLeft(_previewShape, _startPoint.X);
            Canvas.SetTop(_previewShape, _startPoint.Y);
            DrawCanvas.Children.Add(_previewShape);
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _bitmap == null) return;
        var p = e.GetPosition(DrawCanvas);
        if (_tool == DrawTool.Pencil && _activePencil != null)
            _activePencil.Points.Add(p);
        else if (_previewShape is Line line)
        {
            line.X2 = p.X;
            line.Y2 = p.Y;
        }
        else if (_previewShape is Rectangle rect)
        {
            var x = Math.Min(_startPoint.X, p.X);
            var y = Math.Min(_startPoint.Y, p.Y);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = Math.Abs(p.X - _startPoint.X);
            rect.Height = Math.Abs(p.Y - _startPoint.Y);
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();
        _activePencil = null;
        _previewShape = null;
    }

    private void OnSendClick()
    {
        if (_bitmap == null) return;
        try
        {
            var w = _bitmap.PixelWidth;
            var h = _bitmap.PixelHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
                var brush = new VisualBrush(DrawCanvas)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, w, h),
                };
                dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var outPath = SnapshotPaths.CreateTempCapturePath();
            SavePng(rtb, outPath);
            TryDelete(_sourcePath);
            ResultPath = outPath;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Show($"Could not save snapshot: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            TryDelete(_sourcePath);
        base.OnClosed(e);
    }
}
