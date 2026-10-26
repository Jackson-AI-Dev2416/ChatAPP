using System.Text.Json;

namespace Ts3Messenger.Services;

public static class ChatTimestamps
{
    private static TimeZoneInfo _companyZone = TimeZoneInfo.Utc;
    private static string _companyTzId = "UTC";

    public static string CompanyTimeZoneId => _companyTzId;

    public static void SetCompanyTimeZone(string? ianaOrWindowsId)
    {
        if (string.IsNullOrWhiteSpace(ianaOrWindowsId))
            return;
        _companyTzId = ianaOrWindowsId.Trim();
        if (TryResolveTimeZone(_companyTzId, out var tz))
            _companyZone = tz;
    }

    public static async Task TryLoadFromServerAsync(Ts3ApiClient api)
    {
        try
        {
            var tz = await api.GetCompanyTimeZoneAsync();
            SetCompanyTimeZone(tz);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Company timezone load failed: {ex.Message}");
        }
    }

    public static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    public static DateTime ParseJsonInstant(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrEmpty(s)
                && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return NormalizeUtc(parsed);
        }

        return NormalizeUtc(el.GetDateTime());
    }

    public static string Format(DateTime value)
    {
        var utc = NormalizeUtc(value);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, _companyZone);
        return local.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public static DateTime ToCompanyLocal(DateTime value) =>
        TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtc(value), _companyZone);

    public static string FormatShortTime(DateTime value) => ToCompanyLocal(value).ToString("HH:mm");

    /// <summary>Date separator text, e.g. "Today", "Yesterday", "Monday, March 16" or "March 16, 2025".</summary>
    public static string FormatDayHeader(DateTime value)
    {
        var day = ToCompanyLocal(value).Date;
        var today = ToCompanyLocal(DateTime.UtcNow).Date;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7)) return day.ToString("dddd, MMMM d", culture);
        return day.Year == today.Year
            ? day.ToString("MMMM d", culture)
            : day.ToString("MMMM d, yyyy", culture);
    }

    /// <summary>Contact list time: HH:mm today, "Yesterday", weekday within a week, else yyyy-MM-dd.</summary>
    public static string FormatListTime(DateTime value)
    {
        var local = ToCompanyLocal(value);
        var today = ToCompanyLocal(DateTime.UtcNow).Date;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd", culture);
        return local.ToString("yyyy-MM-dd");
    }

    private static bool TryResolveTimeZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch
        {
            // ignored
        }

        var windows = MapIanaToWindows(id);
        if (windows != null)
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(windows);
                return true;
            }
            catch
            {
                // ignored
            }
        }

        zone = TimeZoneInfo.Utc;
        return false;
    }

    private static string? MapIanaToWindows(string iana) =>
        iana switch
        {
            "Asia/Seoul" => "Korea Standard Time",
            "UTC" => "UTC",
            "America/New_York" => "Eastern Standard Time",
            "Europe/London" => "GMT Standard Time",
            _ => null,
        };
}
