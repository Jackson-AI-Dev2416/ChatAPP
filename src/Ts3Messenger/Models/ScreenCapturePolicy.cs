using System.Text.Json.Serialization;

namespace Ts3Messenger.Models;

public sealed class ScreenCapturePolicy
{
    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; } = 20;

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;

    [JsonPropertyName("capture_enabled")]
    public bool CaptureEnabled { get; set; } = true;
}
