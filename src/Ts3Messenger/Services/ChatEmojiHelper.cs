using System.Globalization;

namespace Ts3Messenger.Services;

internal static class ChatEmojiHelper
{
    public static bool IsEmojiOnlyMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasContent = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
                continue;
            hasContent = true;
            if (IsAllowedInEmojiOnly(ch))
                continue;
            return false;
        }

        return hasContent;
    }

    private static bool IsAllowedInEmojiOnly(char ch)
    {
        if (char.IsSurrogate(ch))
            return true;
        var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
        return cat is UnicodeCategory.OtherSymbol
            or UnicodeCategory.Surrogate
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.Format
            || ch is '\u200d' or '\ufe0f' or '\u20e3';
    }
}
