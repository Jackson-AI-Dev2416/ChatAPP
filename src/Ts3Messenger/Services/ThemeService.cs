using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ts3Messenger.Services;

/// <summary>Light/dark palette switching and the bundled app font (Pretendard).</summary>
public static class ThemeService
{
    private const string LightSource = "Themes/Colors.Light.xaml";
    private const string DarkSource = "Themes/Colors.Dark.xaml";
    private const int DwmUseImmersiveDarkMode = 20;

    public static readonly FontFamily AppFont =
        new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Pretendard, Segoe UI");

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    /// <summary>Call once from the App constructor, before any window exists.</summary>
    public static void Initialize(Application app)
    {
        var metadata = () => new FrameworkPropertyMetadata(
            AppFont, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure);
        TextElement.FontFamilyProperty.OverrideMetadata(typeof(Window), metadata());
        TextElement.FontFamilyProperty.OverrideMetadata(typeof(ContextMenu), metadata());
        TextElement.FontFamilyProperty.OverrideMetadata(typeof(ToolTip), metadata());

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));

        var dark = string.Equals(AppSettingsStore.Load().Theme, "dark", StringComparison.OrdinalIgnoreCase);
        SwapPalette(app, dark);
    }

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool dark)
    {
        if (Application.Current is not { } app || dark == IsDark)
            return;
        SwapPalette(app, dark);
        foreach (Window w in app.Windows)
            ApplyTitleBar(w);

        var settings = AppSettingsStore.Load();
        settings.Theme = dark ? "dark" : "light";
        try
        {
            AppSettingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"테마 저장 실패: {ex.Message}");
        }

        Changed?.Invoke();
    }

    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private static void SwapPalette(Application app, bool dark)
    {
        var merged = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri(dark ? DarkSource : LightSource, UriKind.Relative) };
        var existing = merged.FirstOrDefault(d =>
            d.Source != null
            && (d.Source.OriginalString.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase)
                || d.Source.OriginalString.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase)));
        if (existing != null)
            merged[merged.IndexOf(existing)] = palette;
        else
            merged.Insert(0, palette);
        IsDark = dark;
    }

    private static void ApplyTitleBar(Window window)
    {
        if (window.WindowStyle == WindowStyle.None)
            return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var value = IsDark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // Older Windows builds: title bar keeps the system colour.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
