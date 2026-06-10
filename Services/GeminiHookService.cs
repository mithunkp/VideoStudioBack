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
    ApiLimitsInfo GetApiLimits();
    Task ProbeApiLimitsAsync(CancellationToken cancellationToken = default);
}

public class GeminiHookService : IGeminiHookService
{
    public static string GroqRequestsRemaining { get; private set; } = "Unknown";
    public static string GroqRequestsLimit { get; private set; } = "Unknown";
    public static string GroqTokensRemaining { get; private set; } = "Unknown";
    public static string GroqTokensLimit { get; private set; } = "Unknown";
    public static string GroqResetRequests { get; private set; } = "Unknown";
    public static string GroqResetTokens { get; private set; } = "Unknown";
    public static string GeminiTier { get; private set; } = "Unknown";

    private readonly HttpClient _httpClient;
    private readonly string _geminiApiKey;
    private readonly string _groqApiKey;
    private readonly ILogger<GeminiHookService> _logger;

    public GeminiHookService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiHookService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _geminiApiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
        _groqApiKey = configuration["Groq:ApiKey"] ?? string.Empty;
    }

    public ApiLimitsInfo GetApiLimits()
    {
        return new ApiLimitsInfo
        {
            GroqRequestsRemaining = GroqRequestsRemaining,
            GroqRequestsLimit = GroqRequestsLimit,
            GroqTokensRemaining = GroqTokensRemaining,
            GroqTokensLimit = GroqTokensLimit,
            GroqResetRequests = GroqResetRequests,
            GroqResetTokens = GroqResetTokens,
            GeminiTier = GeminiTier
        };
    }

    public async Task ProbeApiLimitsAsync(CancellationToken cancellationToken = default)
    {
        if (GroqRequestsRemaining == "Unknown" && !string.IsNullOrWhiteSpace(_groqApiKey) && _groqApiKey != "YOUR_GROQ_API_KEY_HERE")
        {
            try
            {
                var payload = new
                {
                    model = "llama-3.3-70b-versatile",
                    messages = new[] { new { role = "user", content = "ping" } },
                    max_tokens = 1
                };
                var json = JsonSerializer.Serialize(payload);
                await PostGroqWithRetryAsync("https://api.groq.com/openai/v1/chat/completions", json, cancellationToken, maxAttempts: 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to probe Groq API limits.");
            }
        }

        if (GeminiTier == "Unknown" && !string.IsNullOrWhiteSpace(_geminiApiKey) && _geminiApiKey != "YOUR_GEMINI_API_KEY_HERE")
        {
            try
            {
                var payload = new
                {
                    contents = new[] { new { parts = new[] { new { text = "ping" } } } }
                };
                var json = JsonSerializer.Serialize(payload);
                await PostWithRetryAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_geminiApiKey}", json, cancellationToken, maxAttempts: 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to probe Gemini API limits.");
            }
        }
    }

    public async Task<List<HookCandidate>> AnalyzeHooksAsync(WhisperResponse transcription, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_groqApiKey) || _groqApiKey == "YOUR_GROQ_API_KEY_HERE")
        {
            _logger.LogWarning("Groq API key is not configured.");
            throw new InvalidOperationException("Groq API Key is not configured in application settings.");
        }

        if (transcription.Segments == null || transcription.Segments.Count == 0)
        {
            _logger.LogWarning("Transcription has no segments to analyze.");
            return new List<HookCandidate>();
        }

        _logger.LogInformation("Preparing transcription data for Groq analysis...");

        // Format transcription into readable timestamp segments
        var transcriptBuilder = new StringBuilder();
        foreach (var segment in transcription.Segments)
        {
            transcriptBuilder.AppendLine($"[{segment.Start:0.00}s - {segment.End:0.00}s]: {segment.Text.Trim()}");
        }
        var formattedTranscript = transcriptBuilder.ToString();

        _logger.LogDebug("Formatted Transcript:\n{Transcript}", formattedTranscript);

        var systemPrompt = "You are a professional video editor and viral marketing expert. Analyze the provided video transcript with timestamps. Identify 1 to 3 segments that would serve as the most engaging viral shorts. Each segment should typically be between 10 to 60 seconds long and contain a complete, engaging thought, hook, or value point. Make sure the start_time and end_time match EXACTLY with the timestamp marks in the transcription. Choose crop_position as left, center, or right. Use center unless the words strongly imply a speaker or demonstration is positioned elsewhere. Return ONLY a valid JSON object matching the requested schema:\n{\n  \"hooks\": [\n    {\n      \"start_time\": 0.0,\n      \"end_time\": 0.0,\n      \"title\": \"string\",\n      \"reason\": \"string\",\n      \"crop_position\": \"string\"\n    }\n  ]\n}";

        var promptText = $"Here is the video transcript with timestamps:\n\n{formattedTranscript}\n\nPlease analyze it and extract the best viral segments.";

        // Construct request payload matching the Groq Chat Completion API spec
        var requestPayload = new
        {
            model = "llama-3.3-70b-versatile",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = promptText }
            },
            response_format = new { type = "json_object" },
            temperature = 0.2
        };

        var url = "https://api.groq.com/openai/v1/chat/completions";
        var jsonContent = JsonSerializer.Serialize(requestPayload);

        _logger.LogInformation("Sending hook analysis request to Groq API...");
        var response = await PostGroqWithRetryAsync(url, jsonContent, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Groq API failed with status {Status}. Details: {Details}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Groq API returned status {response.StatusCode}: {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Groq response received: {Body}", responseBody);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        
        // Extract content from response (choices[0].message.content)
        if (root.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentToken))
        {
            var rawJsonText = contentToken.GetString() ?? string.Empty;
            _logger.LogInformation("Parsed JSON string from Groq response. Extracting hooks...");

            var analysisResult = JsonSerializer.Deserialize<HookAnalysisResult>(rawJsonText);
            if (analysisResult != null && analysisResult.Hooks != null)
            {
                _logger.LogInformation("Successfully extracted {Count} hooks from Groq analysis.", analysisResult.Hooks.Count);
                return analysisResult.Hooks;
            }
        }

        throw new Exception("Failed to extract hooks list from Groq chat completions response structure.");
    }

    public async Task ApplyVisualCropAnalysisAsync(List<HookCandidate> hooks, IReadOnlyList<string> framePaths, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_geminiApiKey) || _geminiApiKey == "YOUR_GEMINI_API_KEY_HERE")
        {
            _logger.LogWarning("Gemini API key is not configured.");
            return;
        }

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

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_geminiApiKey}";
        var cropJson = JsonSerializer.Serialize(requestPayload);
        _logger.LogInformation("Sending one visual crop analysis request for {Count} hooks to Gemini API...", hooks.Count);
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
    // Retry helpers – retries on 429 / 503 / 504 with exponential back-off.
    // ---------------------------------------------------------------------------
    private static readonly System.Net.HttpStatusCode[] RetriableStatuses =
    [
        System.Net.HttpStatusCode.TooManyRequests,       // 429
        System.Net.HttpStatusCode.ServiceUnavailable,    // 503
        System.Net.HttpStatusCode.GatewayTimeout         // 504
    ];


    private async Task<HttpResponseMessage> PostGroqWithRetryAsync(
        string url, string jsonBody, CancellationToken cancellationToken,
        int maxAttempts = 4)
    {
        var delaySeconds = 2;
        HttpResponseMessage? response = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _groqApiKey);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                if (response.Headers.TryGetValues("x-ratelimit-remaining-requests", out var remainingReqs))
                {
                    GroqRequestsRemaining = remainingReqs.FirstOrDefault() ?? "Unknown";
                    response.Headers.TryGetValues("x-ratelimit-limit-requests", out var limitReqs);
                    GroqRequestsLimit = limitReqs?.FirstOrDefault() ?? "Unknown";
                    response.Headers.TryGetValues("x-ratelimit-remaining-tokens", out var remainingTokens);
                    GroqTokensRemaining = remainingTokens?.FirstOrDefault() ?? "Unknown";
                    response.Headers.TryGetValues("x-ratelimit-limit-tokens", out var limitTokens);
                    GroqTokensLimit = limitTokens?.FirstOrDefault() ?? "Unknown";
                    response.Headers.TryGetValues("x-ratelimit-reset-requests", out var resetReqs);
                    GroqResetRequests = resetReqs?.FirstOrDefault() ?? "Unknown";
                    response.Headers.TryGetValues("x-ratelimit-reset-tokens", out var resetTokens);
                    GroqResetTokens = resetTokens?.FirstOrDefault() ?? "Unknown";

                    _logger.LogInformation(
                        "--- Groq API Rate Limits ---" +
                        "\n  Requests remaining: {RemainingReqs} / {LimitReqs} (Resets in {ResetReqs})" +
                        "\n  Tokens remaining: {RemainingTokens} / {LimitTokens} (Resets in {ResetTokens})",
                        GroqRequestsRemaining, GroqRequestsLimit, GroqResetRequests,
                        GroqTokensRemaining, GroqTokensLimit, GroqResetTokens);
                }
                return response;
            }

            if (Array.IndexOf(RetriableStatuses, response.StatusCode) < 0)
                return response;   // non-retriable error – bubble up immediately

            if (attempt == maxAttempts)
            {
                _logger.LogError(
                    "Groq API still returning {Status} after {Attempts} attempts. Giving up.",
                    response.StatusCode, maxAttempts);
                return response;
            }

            _logger.LogWarning(
                "Groq API returned {Status} (attempt {Attempt}/{Max}). Retrying in {Delay}s…",
                response.StatusCode, attempt, maxAttempts, delaySeconds);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            delaySeconds = Math.Min(delaySeconds * 2, 30);   // cap at 30 s
        }

        return response!;
    }

    private async Task<HttpResponseMessage> PostWithRetryAsync(
        string url, string jsonBody, CancellationToken cancellationToken,
        int maxAttempts = 4)
    {
        var delaySeconds = 2;
        HttpResponseMessage? response = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                if (response.Headers.TryGetValues("x-gemini-service-tier", out var serviceTier))
                {
                    GeminiTier = serviceTier.FirstOrDefault() ?? "Unknown";
                    _logger.LogInformation(
                        "--- Gemini API Service Info ---" +
                        "\n  Service Tier: {Tier}",
                        GeminiTier);
                }
                return response;
            }

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

