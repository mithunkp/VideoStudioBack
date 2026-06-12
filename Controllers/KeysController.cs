using Microsoft.AspNetCore.Mvc;
using backend.Services;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class KeysController : ControllerBase
{
    private readonly IApiKeyStore _keyStore;
    private readonly ILogger<KeysController> _logger;

    public KeysController(IApiKeyStore keyStore, ILogger<KeysController> logger)
    {
        _keyStore = keyStore;
        _logger   = logger;
    }

    /// <summary>GET /api/keys/status — returns whether each key is configured (never the key value).</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var s = _keyStore.Status;
        return Ok(new
        {
            groq_configured   = s.GroqConfigured,
            gemini_configured = s.GeminiConfigured,
            groq_source       = s.GroqSource,
            gemini_source     = s.GeminiSource
        });
    }

    /// <summary>POST /api/keys/validate — tests a key against the real API without saving.</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidateRequest req, CancellationToken ct)
    {
        if (req == null)
            return BadRequest(new { error = "Request body is required." });

        KeyValidationResult result;

        if (req.Provider?.ToLowerInvariant() == "groq")
            result = await _keyStore.ValidateGroqKeyAsync(req.Key ?? string.Empty, ct);
        else if (req.Provider?.ToLowerInvariant() == "gemini")
            result = await _keyStore.ValidateGeminiKeyAsync(req.Key ?? string.Empty, ct);
        else
            return BadRequest(new { error = "Provider must be 'groq' or 'gemini'." });

        return Ok(new { valid = result.Valid, message = result.Message });
    }

    /// <summary>POST /api/keys/save — validates then persists keys to keys.json.</summary>
    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] SaveKeysRequest req, CancellationToken ct)
    {
        if (req == null)
            return BadRequest(new { error = "Request body is required." });

        var errors   = new List<string>();
        var warnings = new List<string>();

        // Validate Groq key if provided
        if (!string.IsNullOrWhiteSpace(req.GroqApiKey))
        {
            var vr = await _keyStore.ValidateGroqKeyAsync(req.GroqApiKey, ct);
            if (!vr.Valid) errors.Add($"Groq: {vr.Message}");
        }

        // Validate Gemini key if provided
        if (!string.IsNullOrWhiteSpace(req.GeminiApiKey))
        {
            var vr = await _keyStore.ValidateGeminiKeyAsync(req.GeminiApiKey, ct);
            if (!vr.Valid) errors.Add($"Gemini: {vr.Message}");
        }

        if (errors.Count > 0)
            return BadRequest(new { error = string.Join(" | ", errors) });

        // All provided keys are valid — persist them
        await _keyStore.UpdateKeysAsync(req.GroqApiKey, req.GeminiApiKey);

        _logger.LogInformation("API keys saved via UI.");

        return Ok(new
        {
            success  = true,
            message  = "Keys saved successfully.",
            warnings
        });
    }

    // ── Request DTOs ────────────────────────────────────────────────────────
    public class ValidateRequest
    {
        public string? Provider { get; set; }
        public string? Key      { get; set; }
    }

    public class SaveKeysRequest
    {
        public string? GroqApiKey   { get; set; }
        public string? GeminiApiKey { get; set; }
    }
}
