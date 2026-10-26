using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Ts3Messenger.Services;

internal static class ComposeInputHelper
{
    public static string GetMessageText(TextBox box)
    {
        var raw = box.Text ?? "";
        return NormalizeNewlines(raw).Trim();
    }

    public static string GetMessageText(RichTextBox box)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var block in box.Document.Blocks)
        {
            if (!first)
                sb.Append('\n');
            first = false;
            AppendBlock(sb, block);
        }

        return NormalizeNewlines(sb.ToString()).Trim();
    }

    /// <summary>True when nothing at all is typed (whitespace and line breaks count as content).</summary>
    public static bool IsEmpty(RichTextBox box)
    {
        if (box.Document.Blocks.Count > 1)
            return false;
        if (box.Document.Blocks.FirstBlock is not { } block)
            return true;
        var sb = new StringBuilder();
        AppendBlock(sb, block);
        return sb.Length == 0;
    }

    public static string NormalizeNewlines(string raw) =>
        raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static void Clear(RichTextBox box)
    {
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph());
    }

    public static void SetPlainText(RichTextBox box, string text)
    {
        Clear(box);
        if (string.IsNullOrEmpty(text))
            return;
        var p = new Paragraph();
        p.Inlines.Add(new Run(text));
        box.Document.Blocks.Add(p);
    }

    private static void AppendBlock(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case Paragraph p:
                AppendInlines(sb, p.Inlines);
                break;
            case List list:
                var firstItem = true;
                foreach (var item in list.ListItems)
                {
                    if (!firstItem)
                        sb.Append('\n');
                    firstItem = false;
                    var firstChild = true;
                    foreach (var child in item.Blocks)
                    {
                        if (!firstChild)
                            sb.Append('\n');
                        firstChild = false;
                        AppendBlock(sb, child);
                    }
                }
                break;
            case Section section:
                var firstSub = true;
                foreach (var child in section.Blocks)
                {
                    if (!firstSub)
                        sb.Append('\n');
                    firstSub = false;
                    AppendBlock(sb, child);
                }
                break;
            default:
                sb.Append(new TextRange(block.ContentStart, block.ContentEnd).Text);
                break;
        }
    }

    private static void AppendInlines(StringBuilder sb, InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    sb.Append(run.Text);
                    break;
                case LineBreak:
                    sb.Append('\n');
                    break;
                case Emoji.Wpf.EmojiInline emoji:
                    sb.Append(emoji.Text);
                    break;
                case Span span:
                    AppendInlines(sb, span.Inlines);
                    break;
            }
        }
    }
}
