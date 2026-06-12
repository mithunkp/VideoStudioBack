using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// Singleton that holds the active API keys in memory.
/// Source: keys.json only (user-saved via the UI settings page).
/// Keys can be updated at runtime via UpdateKeysAsync() without a restart.
/// </summary>
public interface IApiKeyStore
{
    string GroqApiKey { get; }
    string GeminiApiKey { get; }
    bool HasGroqKey { get; }
    bool HasGeminiKey { get; }
    KeyStatus Status { get; }
    Task UpdateKeysAsync(string? groqKey, string? geminiKey);
    Task<KeyValidationResult> ValidateGroqKeyAsync(string key, CancellationToken ct = default);
    Task<KeyValidationResult> ValidateGeminiKeyAsync(string key, CancellationToken ct = default);
}

public record KeyValidationResult(bool Valid, string Message);

public class KeyStatus
{
    public bool GroqConfigured { get; init; }
    public bool GeminiConfigured { get; init; }
    public string GroqSource { get; init; } = "none";
    public string GeminiSource { get; init; } = "none";
}

public class ApiKeyStoreService : IApiKeyStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const string PlaceholderGroq   = "YOUR_GROQ_API_KEY_HERE";
    private const string PlaceholderGemini = "YOUR_GEMINI_API_KEY_HERE";

    private readonly string _keysFilePath;
    private readonly ILogger<ApiKeyStoreService> _logger;
    private readonly HttpClient _http;

    private string _groqKey   = string.Empty;
    private string _geminiKey = string.Empty;

    public ApiKeyStoreService(
        IWebHostEnvironment env,
        ILogger<ApiKeyStoreService> logger,
        IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _http = httpFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(15);

        // keys.json lives next to the content root — the ONLY source of keys
        _keysFilePath = Path.Combine(env.ContentRootPath, "keys.json");

        LoadKeys();
    }

    // ── Public accessors ────────────────────────────────────────────────────
    public string GroqApiKey   => _groqKey;
    public string GeminiApiKey => _geminiKey;
    public bool HasGroqKey     => !string.IsNullOrWhiteSpace(_groqKey);
    public bool HasGeminiKey   => !string.IsNullOrWhiteSpace(_geminiKey);

    public KeyStatus Status => new()
    {
        GroqConfigured   = HasGroqKey,
        GeminiConfigured = HasGeminiKey,
        GroqSource       = HasGroqKey   ? "keys.json" : "none",
        GeminiSource     = HasGeminiKey ? "keys.json" : "none"
    };

    // ── Key loading (startup) ───────────────────────────────────────────────
    private void LoadKeys()
    {
        // Only source: keys.json written by the user via the UI settings page.
        // appsettings.json is intentionally NOT read for API keys.
        if (!File.Exists(_keysFilePath))
        {
            _logger.LogInformation("keys.json not found — no API keys configured. Open the Settings page to add them.");
            return;
        }

        try
        {
            var json   = File.ReadAllText(_keysFilePath);
            var stored = JsonSerializer.Deserialize<StoredKeys>(json);
            if (stored != null)
            {
                if (!string.IsNullOrWhiteSpace(stored.GroqApiKey) && stored.GroqApiKey != PlaceholderGroq)
                    _groqKey = stored.GroqApiKey;

                if (!string.IsNullOrWhiteSpace(stored.GeminiApiKey) && stored.GeminiApiKey != PlaceholderGemini)
                    _geminiKey = stored.GeminiApiKey;
            }

            _logger.LogInformation(
                "API keys loaded from keys.json — Groq: {G}, Gemini: {M}",
                HasGroqKey ? "SET" : "NOT SET",
                HasGeminiKey ? "SET" : "NOT SET");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read keys.json — no API keys available.");
        }
    }

    // ── Runtime key update (called from KeysController) ────────────────────
    public async Task UpdateKeysAsync(string? groqKey, string? geminiKey)
    {
        if (!string.IsNullOrWhiteSpace(groqKey))
            _groqKey = groqKey.Trim();

        if (!string.IsNullOrWhiteSpace(geminiKey))
            _geminiKey = geminiKey.Trim();

        // Persist to disk so keys survive a restart
        var stored = new StoredKeys
        {
            GroqApiKey   = _groqKey,
            GeminiApiKey = _geminiKey
        };

        await File.WriteAllTextAsync(_keysFilePath,
            JsonSerializer.Serialize(stored, JsonOpts));

        _logger.LogInformation("API keys updated and persisted to keys.json.");
    }

    // ── Validation helpers (hit the real APIs) ──────────────────────────────
    public async Task<KeyValidationResult> ValidateGroqKeyAsync(string key, CancellationToken ct = default)
    {
        key = key.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return new(false, "Key cannot be empty.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.groq.com/openai/v1/models");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

            var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
                return new(true, "✓ Groq key is valid and active.");

            var body = await res.Content.ReadAsStringAsync(ct);
            return res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? new(false, "Invalid key — Groq returned 401 Unauthorized. Double-check the key.")
                : new(false, $"Groq returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        }
        catch (TaskCanceledException)
        {
            return new(false, "Request timed out — check your internet connection.");
        }
        catch (Exception ex)
        {
            return new(false, $"Network error: {ex.Message}");
        }
    }

    public async Task<KeyValidationResult> ValidateGeminiKeyAsync(string key, CancellationToken ct = default)
    {
        key = key.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return new(false, "Key cannot be empty.");

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}";
            var res = await _http.GetAsync(url, ct);

            if (res.IsSuccessStatusCode)
                return new(true, "✓ Gemini key is valid and active.");

            var body = await res.Content.ReadAsStringAsync(ct);
            return res.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                   res.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? new(false, "Invalid key — Gemini rejected it. Double-check the key in AI Studio.")
                : new(false, $"Gemini returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        }
        catch (TaskCanceledException)
        {
            return new(false, "Request timed out — check your internet connection.");
        }
        catch (Exception ex)
        {
            return new(false, $"Network error: {ex.Message}");
        }
    }

    private class StoredKeys
    {
        public string GroqApiKey   { get; set; } = string.Empty;
        public string GeminiApiKey { get; set; } = string.Empty;
    }
}
