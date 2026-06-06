namespace backend.Models;

public class VideoRenderOptions
{
    public bool UseAi { get; set; }
    public double StartTime { get; set; }
    public double EndTime { get; set; } = 30;
    public string CropPosition { get; set; } = "center";
    public string CaptionText { get; set; } = string.Empty;
    public string CaptionFont { get; set; } = "Impact";
    public string CaptionColor { get; set; } = "#ffffff";
    public string AccentColor { get; set; } = "#a855f7";
    public string CaptionPosition { get; set; } = "bottom";
    public string TextAnimation { get; set; } = "none";
}
