using System.Globalization;
using System.Windows.Media;

namespace Ts3Messenger.Services;

/// <summary>Stable per-user avatar colour and initial (Telegram-style letter avatars).</summary>
public static class AvatarPalette
{
    private static readonly Brush[] Brushes =
    [
        Gradient("#FF885E", "#FF516A"),
        Gradient("#FFCD6A", "#FFA85C"),
        Gradient("#82B1FF", "#665FFF"),
        Gradient("#A0DE7E", "#54CB68"),
        Gradient("#53EDD6", "#28C9B7"),
        Gradient("#72D5FD", "#2A9EF1"),
        Gradient("#E0A2F3", "#D669ED"),
    ];

    public static Brush For(string? key)
    {
        var hash = 0;
        foreach (var ch in key ?? "")
            hash = unchecked(hash * 31 + char.ToLowerInvariant(ch));
        return Brushes[(hash & int.MaxValue) % Brushes.Length];
    }

    public static string Initial(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0)
            return "?";
        var first = StringInfo.GetNextTextElement(text, 0);
        return first.ToUpper(CultureInfo.InvariantCulture);
    }

    private static Brush Gradient(string from, string to)
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(from),
            (Color)ColorConverter.ConvertFromString(to),
            90);
        brush.Freeze();
        return brush;
    }
}
