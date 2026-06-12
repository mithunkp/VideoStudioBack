using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace backend.Services;

public interface IGroqTranscriptionService
{
    Task<WhisperResponse> TranscribeAudioAsync(string audioFilePath, CancellationToken cancellationToken = default);
}

public class WhisperSegment
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class WhisperResponse
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("segments")]
    public List<WhisperSegment> Segments { get; set; } = new();
}

public class GroqTranscriptionService : IGroqTranscriptionService
{
    private readonly HttpClient _httpClient;
    private readonly IApiKeyStore _keyStore;
    private readonly ILogger<GroqTranscriptionService> _logger;

    // Resolved at call time so newly-saved keys take effect without a restart
    private string ApiKey => _keyStore.GroqApiKey;

    public GroqTranscriptionService(HttpClient httpClient, IApiKeyStore keyStore, ILogger<GroqTranscriptionService> logger)
    {
        _httpClient = httpClient;
        _keyStore   = keyStore;
        _logger     = logger;
    }

    public async Task<WhisperResponse> TranscribeAudioAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey == "YOUR_GROQ_API_KEY_HERE")
        {
            _logger.LogWarning("Groq API key is not configured. Returning mock/empty transcription.");
            throw new InvalidOperationException("Groq API Key is not configured in application settings.");
        }

        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException("Audio file not found for transcription.", audioFilePath);
        }

        _logger.LogInformation("Sending audio transcription request to Groq for: {FilePath}", audioFilePath);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        var content = new MultipartFormDataContent();
        
        // Add file stream
        var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));

        // Add parameters
        content.Add(new StringContent("whisper-large-v3-turbo"), "model");
        content.Add(new StringContent("verbose_json"), "response_format");

        request.Content = content;

        var response = await _httpClient.SendAsync(request, cancellationToken);
        
        // Ensure stream is closed
        fileStream.Close();

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Groq transcription API failed with status {Status}. Details: {Details}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Groq Whisper API returned status {response.StatusCode}: {errorBody}");
        }

        var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Groq response received successfully.");

        var whisperResult = JsonSerializer.Deserialize<WhisperResponse>(jsonResponse);
        if (whisperResult == null)
        {
            throw new Exception("Failed to deserialize Whisper response from Groq.");
        }

        _logger.LogInformation("Groq audio transcription completed. Transcribed {Count} segments.", whisperResult.Segments?.Count ?? 0);
        return whisperResult;
    }
}
