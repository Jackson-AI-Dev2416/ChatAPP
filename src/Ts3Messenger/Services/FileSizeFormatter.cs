using System.Globalization;
using System.Text.Json;

namespace Ts3Messenger.Services;

internal static class FileSizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        if (bytes < 1024)
            return $"{bytes} B";

        double size = bytes;
        var unit = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static long ReadBytesFromJson(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s) && long.TryParse(s, out var parsed))
                return parsed;
            return 0;
        }

        if (el.ValueKind != JsonValueKind.Number)
            return 0;

        if (el.TryGetInt64(out var i64))
            return i64;

        if (el.TryGetUInt64(out var u64))
            return u64 > long.MaxValue ? long.MaxValue : (long)u64;

        return 0;
    }

    public static long ResolveBytes(long jsonBytes, long existingBytes, long? localFileLength)
    {
        if (localFileLength is long local && local > 0)
        {
            if (jsonBytes <= 0)
                return local;
            if (IsPlausibleMatch(jsonBytes, local))
                return jsonBytes;
            return local;
        }

        if (jsonBytes > 0)
            return jsonBytes;
        return existingBytes > 0 ? existingBytes : 0;
    }

    private static bool IsPlausibleMatch(long jsonBytes, long localBytes)
    {
        if (jsonBytes == localBytes)
            return true;
        var max = Math.Max(jsonBytes, localBytes);
        var min = Math.Min(jsonBytes, localBytes);
        if (min == 0)
            return false;
        return (double)max / min <= 1.05;
    }
}
