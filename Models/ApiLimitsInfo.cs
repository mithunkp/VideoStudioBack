using System.Text.Json.Serialization;

namespace backend.Models;

public class ApiLimitsInfo
{
    [JsonPropertyName("groq_requests_remaining")]
    public string GroqRequestsRemaining { get; set; } = "Unknown";

    [JsonPropertyName("groq_requests_limit")]
    public string GroqRequestsLimit { get; set; } = "Unknown";

    [JsonPropertyName("groq_tokens_remaining")]
    public string GroqTokensRemaining { get; set; } = "Unknown";

    [JsonPropertyName("groq_tokens_limit")]
    public string GroqTokensLimit { get; set; } = "Unknown";

    [JsonPropertyName("groq_reset_requests")]
    public string GroqResetRequests { get; set; } = "Unknown";

    [JsonPropertyName("groq_reset_tokens")]
    public string GroqResetTokens { get; set; } = "Unknown";

    [JsonPropertyName("gemini_tier")]
    public string GeminiTier { get; set; } = "Unknown";
}
