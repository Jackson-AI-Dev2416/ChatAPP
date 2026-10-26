using System.IO;
using System.Text.Json;

namespace Ts3Messenger.Services;

public sealed class AppSettings
{
    public string ServerBaseUrl { get; set; } = "http://192.168.0.10";
    public bool RememberUserInfo { get; set; }
    public string LoginId { get; set; } = "";
    /// <summary>DPAPI-protected password (optional when RememberUserInfo).</summary>
    public string? ProtectedPasswordB64 { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    /// <summary>"light" or "dark".</summary>
    public string Theme { get; set; } = "light";
}

public static class AppSettingsStore
{
    private static string SettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ts3Messenger",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return new AppSettings();
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsFilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            SettingsFilePath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
