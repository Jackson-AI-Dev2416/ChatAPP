using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Ts3Messenger.Services;

public static class ChatMessageTextHelper
{
    private static readonly Regex UrlPattern = new(
        @"\b(?:https?://|www\.)[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static FontFamily UiFont => ThemeService.AppFont;

    public static FrameworkElement CreateMessageContent(
        string? plainText, bool forChatHistory = true, bool ownMessage = false)
    {
        var text = ComposeInputHelper.NormalizeNewlines(plainText ?? "");
        var emojiOnly = forChatHistory && ChatEmojiHelper.IsEmojiOnlyMessage(text);
        var fontSize = emojiOnly ? AppTypography.BubbleEmojiOnly : AppTypography.BubbleText;
        var lineHeight = fontSize * 1.4;
        return new SelectableMessageText(text, fontSize, lineHeight, ownMessage);
    }

    public static string? FindSelectedText(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is SelectableMessageText box && !box.Selection.IsEmpty)
                return box.Selection.Text;
            var nested = FindSelectedText(child);
            if (!string.IsNullOrEmpty(nested))
                return nested;
        }

        return null;
    }

    private static void AppendLine(InlineCollection target, string line, double fontSize, bool ownMessage)
    {
        if (line.Length == 0)
        {
            target.Add(new Run(" "));
            return;
        }

        var probe = new Emoji.Wpf.TextBlock
        {
            Text = line,
            FontFamily = UiFont,
            FontSize = fontSize,
        };
        var original = probe.Inlines.ToList();
        probe.Inlines.Clear();
        if (original.Count == 0)
        {
                AppendLinkedRuns(target, line, ownMessage);
            return;
        }

        foreach (var inline in original)
        {
            if (inline is Run run)
                AppendLinkedRuns(target, run.Text, ownMessage);
            else
                target.Add(inline);
        }
    }

    private static void AppendLinkedRuns(InlineCollection target, string text, bool ownMessage)
    {
        if (text.Length == 0)
            return;
        var index = 0;
        foreach (Match match in UrlPattern.Matches(text))
        {
            if (match.Index > index)
                target.Add(new Run(text[index..match.Index]));

            var raw = match.Value;
            var trimmed = raw.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"');
            if (!TryCreateLink(trimmed, ownMessage, out var link))
            {
                target.Add(new Run(raw));
            }
            else
            {
                target.Add(link);
                if (trimmed.Length < raw.Length)
                    target.Add(new Run(raw[trimmed.Length..]));
            }

            index = match.Index + match.Length;
        }

        if (index < text.Length)
            target.Add(new Run(text[index..]));
    }

    private static bool TryCreateLink(string display, bool ownMessage, out Hyperlink link)
    {
        link = null!;
        if (display.Length == 0)
            return false;
        var url = display.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "https://" + display
            : display;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var opened = false;
        link = new Hyperlink(new Run(display))
        {
            NavigateUri = uri,
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
        };
        link.SetResourceReference(
            TextElement.ForegroundProperty, ownMessage ? "LinkOnMineBrush" : "LinkBrush");
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            OpenOnce();
        };
        link.Click += (_, e) =>
        {
            e.Handled = true;
            OpenOnce();
        };
        return true;

        void OpenOnce()
        {
            if (opened)
                return;
            opened = true;
            OpenHttp(uri);
        }
    }

    private static void OpenHttp(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"링크 열기 실패: {ex.Message}");
        }
    }

    private sealed class SelectableMessageText : RichTextBox
    {
        private readonly bool _ownMessage;
        private readonly bool _emojiOnly;
        private int _emojiPerLine = -1;
        private double _packedWidth;

        public SelectableMessageText(string text, double fontSize, double lineHeight, bool ownMessage)
        {
            _ownMessage = ownMessage;
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            Margin = new Thickness(0);
            IsDocumentEnabled = true;
            IsUndoEnabled = false;
            SpellCheck.IsEnabled = false;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalAlignment = HorizontalAlignment.Left;
            MinWidth = 0;
            MinHeight = 0;
            Focusable = true;
            IsTabStop = false;
            Cursor = Cursors.IBeam;
            ContextMenu = null;
            FontFamily = UiFont;
            FontSize = fontSize;
            Document = BuildDocument(text, fontSize, lineHeight, ownMessage);
            _emojiOnly = IsEmojiDocument();
            if (_emojiOnly && _ownMessage)
                HorizontalAlignment = HorizontalAlignment.Right;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            Loaded += (_, _) => ApplyInheritedForeground();
        }

        protected override void OnVisualParentChanged(DependencyObject oldParent)
        {
            base.OnVisualParentChanged(oldParent);
            ApplyInheritedForeground();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // The chat list scrolls; a message must not swallow the wheel.
        }

        private void ApplyInheritedForeground()
        {
            // RichTextBox document text keeps the default black foreground unless the
            // document itself is painted. Bubble colors live on an ancestor.
            var brush = ResolveForeground();
            if (brush == null)
                return;
            Foreground = brush;
            Document.Foreground = brush;
            foreach (var block in Document.Blocks)
            {
                block.ClearValue(TextElement.ForegroundProperty);
                if (block is Paragraph paragraph)
                    ClearInlineForeground(paragraph.Inlines);
            }
        }

        private Brush? ResolveForeground()
        {
            for (DependencyObject? node = this; node != null; node = ParentOf(node))
            {
                if (node.ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue)
                    continue;
                if (node.GetValue(TextElement.ForegroundProperty) is Brush brush)
                    return brush;
            }

            return null;
        }

        private static DependencyObject? ParentOf(DependencyObject node)
        {
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var visualParent = VisualTreeHelper.GetParent(node);
                if (visualParent != null)
                    return visualParent;
            }

            return LogicalTreeHelper.GetParent(node);
        }

        private static void ClearInlineForeground(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Hyperlink)
                    continue;
                // Emoji.Wpf paints color glyphs only when this stays black. A light
                // bubble color turns the same geometry into a white silhouette.
                if (inline is Emoji.Wpf.EmojiInline emoji)
                {
                    emoji.Foreground = Brushes.Black;
                    continue;
                }
                inline.ClearValue(TextElement.ForegroundProperty);
                if (inline is Span span)
                    ClearInlineForeground(span.Inlines);
            }
        }

        private static FlowDocument BuildDocument(
            string text, double fontSize, double lineHeight, bool ownMessage)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                LineHeight = lineHeight,
                FontFamily = UiFont,
                FontSize = fontSize,
            };
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                AppendLine(paragraph.Inlines, lines[i], fontSize, ownMessage);
                if (i < lines.Length - 1)
                    paragraph.Inlines.Add(new LineBreak());
            }

            if (paragraph.Inlines.Count == 0)
                paragraph.Inlines.Add(new Run(" "));

            return new FlowDocument(paragraph)
            {
                PagePadding = new Thickness(0),
                FontFamily = UiFont,
                FontSize = fontSize,
            };
        }

        private bool IsEmojiDocument()
        {
            if (Document.Blocks.FirstBlock is not Paragraph paragraph)
                return false;
            var any = false;
            foreach (var inline in paragraph.Inlines)
            {
                if (inline is LineBreak)
                    continue;
                if (inline is Run run && string.IsNullOrWhiteSpace(run.Text))
                    continue;
                if (inline is not Emoji.Wpf.EmojiInline)
                    return false;
                any = true;
            }

            return any;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            var maxWidth = double.IsInfinity(constraint.Width) ? 480 : Math.Max(1, constraint.Width);
            Document.PagePadding = new Thickness(0);
            if (_emojiOnly && PackEmojiRows(maxWidth))
            {
                SetPageWidth(_packedWidth);
                var packed = base.MeasureOverride(new Size(_packedWidth, constraint.Height));
                return new Size(_packedWidth, packed.Height);
            }

            SetPageWidth(maxWidth);
            var full = base.MeasureOverride(new Size(maxWidth, constraint.Height));
            var natural = Math.Min(maxWidth, Math.Max(1, ContentWidth() + 4));
            if (natural >= maxWidth - 1)
                return new Size(Math.Min(maxWidth, Math.Max(natural, full.Width)), full.Height);

            SetPageWidth(natural);
            var shrunk = base.MeasureOverride(new Size(natural, constraint.Height));
            if (shrunk.Height <= full.Height + 1)
                return new Size(natural, shrunk.Height);

            // The width guess was short, so the last emoji dropped a line. Widen until
            // the line count matches the full-width layout.
            var lo = natural;
            var hi = maxWidth;
            for (var i = 0; i < 8; i++)
            {
                var mid = (lo + hi) / 2;
                SetPageWidth(mid);
                var trial = base.MeasureOverride(new Size(mid, constraint.Height));
                if (trial.Height > full.Height + 1)
                    lo = mid;
                else
                    hi = mid;
            }

            SetPageWidth(hi);
            var fit = base.MeasureOverride(new Size(hi, constraint.Height));
            return new Size(hi, fit.Height);
        }

        /// <summary>
        /// Own emoji rows share one right edge: a short message and the first line of a long
        /// message both end at the same place. Later lines use that same edge.
        /// </summary>
        private bool PackEmojiRows(double limit)
        {
            if (Document.Blocks.FirstBlock is not Paragraph paragraph)
                return false;
            var emojis = new List<Emoji.Wpf.EmojiInline>();
            foreach (var inline in paragraph.Inlines)
            {
                if (inline is Emoji.Wpf.EmojiInline emoji)
                    emojis.Add(emoji);
            }

            if (emojis.Count == 0)
                return false;

            var cell = 1.0;
            foreach (var emoji in emojis)
                cell = Math.Max(cell, InlineWidth(emoji, paragraph.FontSize, PixelsPerDip));
            var per = Math.Max(1, (int)Math.Floor(limit / cell));
            var cols = Math.Min(emojis.Count, per);
            var packed = cols * cell;
            if (per == _emojiPerLine && Math.Abs(packed - _packedWidth) < 0.5)
                return true;

            _emojiPerLine = per;
            _packedWidth = packed;
            paragraph.Inlines.Clear();
            paragraph.TextAlignment = _ownMessage ? TextAlignment.Right : TextAlignment.Left;
            for (var i = 0; i < emojis.Count; i++)
            {
                if (i > 0 && i % per == 0)
                    paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.Add(emojis[i]);
            }

            return true;
        }

        private void SetPageWidth(double width)
        {
            Document.PageWidth = width;
            Document.ColumnWidth = width;
        }

        private double ContentWidth()
        {
            var dip = PixelsPerDip;
            double widest = 0;
            double line = 0;
            foreach (var block in Document.Blocks)
            {
                if (block is not Paragraph paragraph)
                    continue;
                line = 0;
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline is LineBreak)
                    {
                        widest = Math.Max(widest, line);
                        line = 0;
                        continue;
                    }

                    line += InlineWidth(inline, paragraph.FontSize, dip);
                }

                widest = Math.Max(widest, line);
            }

            return widest;
        }

        private static double InlineWidth(Inline inline, double fontSize, double dip)
        {
            if (inline is Run run)
                return TextWidth(run.Text, fontSize, dip);
            if (inline is Hyperlink link)
                return TextWidth(new TextRange(link.ContentStart, link.ContentEnd).Text, fontSize, dip);
            if (inline is Emoji.Wpf.EmojiInline emoji)
            {
                var size = emoji.FontSize > 0 ? emoji.FontSize : fontSize;
                if (emoji.Child is FrameworkElement child)
                {
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (child.DesiredSize.Width > 1)
                        return child.DesiredSize.Width + 1;
                }

                return size * 1.15;
            }
            return fontSize * 1.35;
        }

        private static double TextWidth(string text, double fontSize, double dip)
        {
            if (text.Length == 0)
                return 0;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                fontSize,
                Brushes.Black,
                dip);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        private double PixelsPerDip
        {
            get
            {
                var source = PresentationSource.FromVisual(this);
                return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
        }
    }
}
