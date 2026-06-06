using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using backend.Models;

namespace backend.Services;

public interface IGeminiHookService
{
    Task<List<HookCandidate>> AnalyzeHooksAsync(WhisperResponse transcription, CancellationToken cancellationToken = default);
    Task ApplyVisualCropAnalysisAsync(List<HookCandidate> hooks, IReadOnlyList<string> framePaths, CancellationToken cancellationToken = default);
}

public class GeminiHookService : IGeminiHookService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<GeminiHookService> _logger;

    public GeminiHookService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiHookService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
    }

    public async Task<List<HookCandidate>> AnalyzeHooksAsync(WhisperResponse transcription, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "YOUR_GEMINI_API_KEY_HERE")
        {
            _logger.LogWarning("Gemini API key is not configured.");
            throw new InvalidOperationException("Gemini API Key is not configured in application settings.");
        }

        if (transcription.Segments == null || transcription.Segments.Count == 0)
        {
            _logger.LogWarning("Transcription has no segments to analyze.");
            return new List<HookCandidate>();
        }

        _logger.LogInformation("Preparing transcription data for Gemini analysis...");

        // Format transcription into readable timestamp segments
        var transcriptBuilder = new StringBuilder();
        foreach (var segment in transcription.Segments)
        {
            transcriptBuilder.AppendLine($"[{segment.Start:0.00}s - {segment.End:0.00}s]: {segment.Text.Trim()}");
        }
        var formattedTranscript = transcriptBuilder.ToString();

        _logger.LogDebug("Formatted Transcript:\n{Transcript}", formattedTranscript);

        var systemPrompt = "You are a professional video editor and viral marketing expert. Analyze the provided video transcript with timestamps. Identify 1 to 3 segments that would serve as the most engaging viral shorts. Each segment should typically be between 10 to 60 seconds long and contain a complete, engaging thought, hook, or value point. Make sure the start_time and end_time match EXACTLY with the timestamp marks in the transcription. Choose crop_position as left, center, or right. Use center unless the words strongly imply a speaker or demonstration is positioned elsewhere. Return ONLY a valid JSON object matching the requested schema.";

        var promptText = $"Here is the video transcript with timestamps:\n\n{formattedTranscript}\n\nPlease analyze it and extract the best viral segments.";

        // Construct request payload matching the Gemini API spec
        var requestPayload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new { text = promptText }
                    }
                }
            },
            systemInstruction = new
            {
                parts = new[]
                {
                    new { text = systemPrompt }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        hooks = new
                        {
                            type = "ARRAY",
                            description = "List of top hook candidates for viral shorts",
                            items = new
                            {
                                type = "OBJECT",
                                properties = new
                                {
                                    start_time = new { type = "NUMBER", description = "The exact start time of the segment in seconds" },
                                    end_time = new { type = "NUMBER", description = "The exact end time of the segment in seconds" },
                                    title = new { type = "STRING", description = "A catchy, click-worthy title for the short" },
                                    reason = new { type = "STRING", description = "Short explanation of why this segment makes a great hook" },
                                    crop_position = new { type = "STRING", description = "Best likely horizontal crop focus: left, center, or right" }
                                },
                                required = new[] { "start_time", "end_time", "title", "reason", "crop_position" }
                            }
                        }
                    },
                    required = new[] { "hooks" }
                }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_apiKey}";
        var jsonContent = JsonSerializer.Serialize(requestPayload);

        _logger.LogInformation("Sending hook analysis request to Gemini API...");
        var response = await PostWithRetryAsync(url, jsonContent, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Gemini API failed with status {Status}. Details: {Details}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Gemini API returned status {response.StatusCode}: {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Gemini response received: {Body}", responseBody);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        
        // Extract content from response (candidates[0].content.parts[0].text)
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var candContent) &&
            candContent.TryGetProperty("parts", out var parts) &&
            parts.GetArrayLength() > 0 &&
            parts[0].TryGetProperty("text", out var textToken))
        {
            var rawJsonText = textToken.GetString() ?? string.Empty;
            _logger.LogInformation("Parsed JSON string from Gemini response. Extracting hooks...");

            var analysisResult = JsonSerializer.Deserialize<HookAnalysisResult>(rawJsonText);
            if (analysisResult != null && analysisResult.Hooks != null)
            {
                _logger.LogInformation("Successfully extracted {Count} hooks from Gemini analysis.", analysisResult.Hooks.Count);
                return analysisResult.Hooks;
            }
        }

        throw new Exception("Failed to extract hooks list from Gemini generateContent response structure.");
    }

    public async Task ApplyVisualCropAnalysisAsync(List<HookCandidate> hooks, IReadOnlyList<string> framePaths, CancellationToken cancellationToken = default)
    {
        if (hooks.Count == 0 || framePaths.Count != hooks.Count) return;

        var parts = new List<object>
        {
            new
            {
                text = "Choose the best 9:16 horizontal crop for each numbered frame. Keep the main face, speaker, or demonstrated object visible. Return JSON with a crops array containing index and crop_position (left, center, or right)."
            }
        };
        for (var i = 0; i < framePaths.Count; i++)
        {
            parts.Add(new { text = $"Frame index {i}" });
            parts.Add(new
            {
                inline_data = new
                {
                    mime_type = "image/jpeg",
                    data = Convert.ToBase64String(await File.ReadAllBytesAsync(framePaths[i], cancellationToken))
                }
            });
        }

        var requestPayload = new
        {
            contents = new[] { new { role = "user", parts } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        crops = new
                        {
                            type = "ARRAY",
                            items = new
                            {
                                type = "OBJECT",
                                properties = new
                                {
                                    index = new { type = "INTEGER" },
                                    crop_position = new { type = "STRING", description = "left, center, or right" }
                                },
                                required = new[] { "index", "crop_position" }
                            }
                        }
                    },
                    required = new[] { "crops" }
                }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_apiKey}";
        var cropJson = JsonSerializer.Serialize(requestPayload);
        _logger.LogInformation("Sending one visual crop analysis request for {Count} hooks...", hooks.Count);
        var response = await PostWithRetryAsync(url, cropJson, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Visual crop analysis failed with status {Status}; keeping fallback crop positions.", response.StatusCode);
            return;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) return;
        using var cropDoc = JsonDocument.Parse(text);
        foreach (var crop in cropDoc.RootElement.GetProperty("crops").EnumerateArray())
        {
            var index = crop.GetProperty("index").GetInt32();
            var position = crop.GetProperty("crop_position").GetString();
            if (index >= 0 && index < hooks.Count && position is "left" or "center" or "right")
            {
                hooks[index].CropPosition = position;
            }
        }
    }
    // ---------------------------------------------------------------------------
    // Retry helper – retries on 429 / 503 / 504 with exponential back-off.
    // ---------------------------------------------------------------------------
    private static readonly System.Net.HttpStatusCode[] RetriableStatuses =
    [
        System.Net.HttpStatusCode.TooManyRequests,       // 429
        System.Net.HttpStatusCode.ServiceUnavailable,    // 503
        System.Net.HttpStatusCode.GatewayTimeout         // 504
    ];

    private async Task<HttpResponseMessage> PostWithRetryAsync(
        string url, string jsonBody, CancellationToken cancellationToken,
        int maxAttempts = 4)
    {
        var delaySeconds = 2;
        HttpResponseMessage? response = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // StringContent is not reusable after a send, so create a fresh one each attempt.
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
                return response;

            if (Array.IndexOf(RetriableStatuses, response.StatusCode) < 0)
                return response;   // non-retriable error – bubble up immediately

            if (attempt == maxAttempts)
            {
                _logger.LogError(
                    "Gemini API still returning {Status} after {Attempts} attempts. Giving up.",
                    response.StatusCode, maxAttempts);
                return response;
            }

            _logger.LogWarning(
                "Gemini API returned {Status} (attempt {Attempt}/{Max}). Retrying in {Delay}s…",
                response.StatusCode, attempt, maxAttempts, delaySeconds);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            delaySeconds = Math.Min(delaySeconds * 2, 30);   // cap at 30 s
        }

        return response!;
    }
}
