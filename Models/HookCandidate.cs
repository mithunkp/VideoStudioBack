using System.Text.Json.Serialization;

namespace backend.Models;

public class HookCandidate
{
    [JsonPropertyName("start_time")]
    public double StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double EndTime { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("video_url")]
    public string VideoUrl { get; set; } = string.Empty;

    [JsonPropertyName("crop_position")]
    public string CropPosition { get; set; } = "center";

    [JsonPropertyName("is_manual")]
    public bool IsManual { get; set; } = false;
}

public class HookAnalysisResult
{
    [JsonPropertyName("hooks")]
    public List<HookCandidate> Hooks { get; set; } = new();
}
