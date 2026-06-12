using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using backend.Models;
using System.Text.RegularExpressions;
using System.Net;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace backend.Services;

public interface IVideoProcessingOrchestrator
{
    Task<VideoProcessingResult> ProcessVideoAsync(Stream videoStream, string originalFileName, VideoRenderOptions options, CancellationToken cancellationToken = default);
    Task<VideoProcessingResult> ProcessVideoUrlAsync(string videoUrl, VideoRenderOptions options, CancellationToken cancellationToken = default);
}

public class VideoProcessingResult
{
    public List<HookCandidate> Hooks { get; set; } = new();
    public HookCandidate? SelectedHook { get; set; }
    public string VideoUrl { get; set; } = string.Empty;
}

public class VideoProcessingOrchestrator : IVideoProcessingOrchestrator
{
    private readonly IFFmpegService _ffmpegService;
    private readonly IGroqTranscriptionService _groqService;
    private readonly IGeminiHookService _geminiService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VideoProcessingOrchestrator> _logger;
    
    private readonly string _tempDir;
    private readonly string _outputDir;

    public VideoProcessingOrchestrator(
        IFFmpegService ffmpegService,
        IGroqTranscriptionService groqService,
        IGeminiHookService geminiService,
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<VideoProcessingOrchestrator> logger)
    {
        _ffmpegService = ffmpegService;
        _groqService = groqService;
        _geminiService = geminiService;
        _configuration = configuration;
        _logger = logger;

        var tempConfig = configuration["VideoProcessing:TempDirectory"] ?? "Temp";
        _tempDir = Path.IsPathRooted(tempConfig) ? tempConfig : Path.Combine(env.ContentRootPath, tempConfig);

        var outputConfig = configuration["VideoProcessing:OutputDirectory"] ?? "wwwroot/shorts";
        _outputDir = Path.IsPathRooted(outputConfig) ? outputConfig : Path.Combine(env.ContentRootPath, outputConfig);

        // Ensure directories exist
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_outputDir);
    }

    public async Task<VideoProcessingResult> ProcessVideoAsync(Stream videoStream, string originalFileName, VideoRenderOptions options, CancellationToken cancellationToken = default)
    {
        // Fire-and-forget background cleanup of files older than 1 hour
        _ = Task.Run(() => DeleteOldFiles(), CancellationToken.None);

        var runId = Guid.NewGuid().ToString("N");
        var inputVideoPath = Path.Combine(_tempDir, $"input_{runId}.mp4");

        _logger.LogInformation("Starting video processing pipeline for run {RunId}...", runId);

        try
        {
            // 1. Stream input file to disk (keeps memory footprint very low)
            _logger.LogInformation("Streaming uploaded video file to disk at {Path}...", inputVideoPath);
            using (var fileStream = new FileStream(inputVideoPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await videoStream.CopyToAsync(fileStream, cancellationToken);
            }
            _logger.LogInformation("Saved uploaded video to disk.");

            var fileInfo = new FileInfo(inputVideoPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                throw new InvalidOperationException("The uploaded video file is empty or corrupted.");
            }

            return await RunProcessingPipelineAsync(inputVideoPath, runId, options, cancellationToken);
        }
        finally
        {
            // Cleanup input video path (audio is handled in the pipeline cleanup)
            TryDeleteFile(inputVideoPath);
        }
    }

    public async Task<VideoProcessingResult> ProcessVideoUrlAsync(string videoUrl, VideoRenderOptions options, CancellationToken cancellationToken = default)
    {
        // Fire-and-forget background cleanup of files older than 1 hour
        _ = Task.Run(() => DeleteOldFiles(), CancellationToken.None);

        var runId = Guid.NewGuid().ToString("N");
        var inputVideoPath = Path.Combine(_tempDir, $"input_{runId}.mp4");

        try
        {
            // 1. Check if it's a YouTube link (matching www.youtube.com, m.youtube.com, youtu.be, etc.)
            var youtubeUrlPattern = @"^(https?://)?([a-zA-Z0-9\-]+\.)?(youtube\.com|youtu\.be)/";
            if (Regex.IsMatch(videoUrl, youtubeUrlPattern, RegexOptions.IgnoreCase))
            {
                _logger.LogInformation("Detected YouTube URL. Downloading using YoutubeExplode...");
                using (var customHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    var youtube = new YoutubeClient(customHttpClient);
                    var video = await youtube.Videos.GetAsync(videoUrl, cancellationToken);
                    var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id, cancellationToken);
                    
                    var streamInfo = streamManifest.GetMuxedStreams().GetWithHighestVideoQuality();
                    if (streamInfo != null)
                    {
                        _logger.LogInformation("Downloading YouTube muxed stream: {Format} ({Quality})", streamInfo.Container, streamInfo.VideoQuality);
                        await youtube.Videos.Streams.DownloadAsync(streamInfo, inputVideoPath, cancellationToken: cancellationToken);
                    }
                    else
                    {
                        _logger.LogWarning("No muxed video stream found for this YouTube video. Downloading separate video and audio streams and merging them...");
                        
                        var videoStreamInfo = streamManifest.GetVideoOnlyStreams().GetWithHighestVideoQuality();
                        var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();

                        if (videoStreamInfo == null || audioStreamInfo == null)
                        {
                            throw new Exception("Could not find suitable separate video and audio streams for this YouTube video.");
                        }

                        var tempVideoPath = Path.Combine(_tempDir, $"temp_ytdl_v_{runId}.mp4");
                        var tempAudioPath = Path.Combine(_tempDir, $"temp_ytdl_a_{runId}.mp3");

                        try
                        {
                            _logger.LogInformation("Downloading separate YouTube video stream: {Format} ({Quality})", videoStreamInfo.Container, videoStreamInfo.VideoQuality);
                            await youtube.Videos.Streams.DownloadAsync(videoStreamInfo, tempVideoPath, cancellationToken: cancellationToken);

                            _logger.LogInformation("Downloading separate YouTube audio stream: {Format} ({Bitrate})", audioStreamInfo.Container, audioStreamInfo.Bitrate);
                            await youtube.Videos.Streams.DownloadAsync(audioStreamInfo, tempAudioPath, cancellationToken: cancellationToken);

                            _logger.LogInformation("Muxing video and audio streams using local FFmpeg wrapper...");
                            await _ffmpegService.MuxVideoAndAudioAsync(tempVideoPath, tempAudioPath, inputVideoPath, cancellationToken);
                        }
                        finally
                        {
                            TryDeleteFile(tempVideoPath);
                            TryDeleteFile(tempAudioPath);
                        }
                    }
                    _logger.LogInformation("YouTube video downloaded successfully.");
                }
            }
            // 2. Check if it's an Instagram link
            else if (videoUrl.Contains("instagram.com/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Detected Instagram URL. Attempting to parse direct video stream...");
                string directVideoUrl = await GetInstagramDirectVideoUrlAsync(videoUrl, cancellationToken);
                await DownloadDirectFileAsync(directVideoUrl, inputVideoPath, cancellationToken);
            }
            // 3. Fallback to direct HTTP download
            else
            {
                _logger.LogInformation("Downloading direct video file from URL: {Url}", videoUrl);
                await DownloadDirectFileAsync(videoUrl, inputVideoPath, cancellationToken);
            }

            var fileInfo = new FileInfo(inputVideoPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                throw new InvalidOperationException("The downloaded video file is empty or corrupted.");
            }

            return await RunProcessingPipelineAsync(inputVideoPath, runId, options, cancellationToken);
        }
        finally
        {
            TryDeleteFile(inputVideoPath);
        }
    }

    private async Task DownloadDirectFileAsync(string videoUrl, string outputPath, CancellationToken cancellationToken)
    {
        using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            using (var response = await httpClient.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType != null && 
                    !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && 
                    !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The URL does not point to a direct video file (received Content-Type: '{contentType}'). Make sure you provide a link directly pointing to a .mp4 video file.");
                }

                using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await stream.CopyToAsync(fileStream, cancellationToken);
                }
            }
        }
    }

    private async Task<string> GetInstagramDirectVideoUrlAsync(string url, CancellationToken cancellationToken)
    {
        using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            // Instagram requires standard browser headers to return page content instead of blocking
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
            
            var html = await httpClient.GetStringAsync(url, cancellationToken);
            
            // 1. Try matching meta og:video
            var match = Regex.Match(html, @"<meta[^>]*property=""og:video""[^>]*content=""([^""]+)""");
            if (match.Success)
            {
                return WebUtility.HtmlDecode(match.Groups[1].Value);
            }
            
            // 2. Try matching video_url in javascript configuration objects
            match = Regex.Match(html, @"""video_url"":""([^""]+)""");
            if (match.Success)
            {
                return Regex.Unescape(match.Groups[1].Value);
            }

            throw new InvalidOperationException("Could not extract direct video URL from the Instagram post. Instagram posts are protected or require user login session.");
        }
    }

    private async Task<VideoProcessingResult> RunProcessingPipelineAsync(string inputVideoPath, string runId, VideoRenderOptions options, CancellationToken cancellationToken)
    {
        var tempAudioPath = Path.Combine(_tempDir, $"audio_{runId}.mp3");
        var subtitlePaths = new List<string>();
        var cropFramePaths = new List<string>();
        var stage = "initializing";

        try
        {
            List<HookCandidate> hooks = new();
            WhisperResponse? transcription = null;

            if (!options.UseAi || options.ManualTrimEnabled)
            {
                stage = "reading video duration";
                var duration = await _ffmpegService.GetVideoDurationAsync(inputVideoPath, cancellationToken);
                var start = Math.Clamp(options.StartTime, 0, Math.Max(0, duration - 0.1));
                var requestedEnd = options.EndTime > start ? options.EndTime : start + 30;
                var end = Math.Min(duration, Math.Max(start + 0.1, requestedEnd));
                hooks.Add(new HookCandidate
                {
                    StartTime = Math.Round(start, 2),
                    EndTime = Math.Round(end, 2),
                    Title = string.IsNullOrWhiteSpace(options.CaptionText) ? "Manual Custom Trim" : options.CaptionText,
                    Reason = "Manually selected override clip rendered locally.",
                    CropPosition = options.CropPosition,
                    IsManual = true
                });
            }

            if (options.UseAi)
            {
                stage = "checking audio track";
                if (!await _ffmpegService.HasAudioStreamAsync(inputVideoPath, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "This MP4 has no audio track, so vocal transcription and spoken captions cannot run. " +
                        "The file appears to be video-only, often caused by downloading a DASH video stream. " +
                        "Upload a combined video+audio MP4, or paste the original YouTube/video URL in the Video URL tab so the backend can download and mux audio when available.");
                }

                // Extract audio only when AI transcription was requested.
                stage = "extracting vocal audio";
                _logger.LogInformation("Extracting audio from video...");
                await _ffmpegService.ExtractAudioAsync(inputVideoPath, tempAudioPath, cancellationToken);

                // Detect if API Keys are configured, otherwise fallback to mock mode
                var groqKey = _configuration["Groq:ApiKey"];
                var geminiKey = _configuration["Gemini:ApiKey"];
                bool isMockMode = string.IsNullOrWhiteSpace(groqKey) || groqKey == "YOUR_GROQ_API_KEY_HERE" ||
                                  string.IsNullOrWhiteSpace(geminiKey) || geminiKey == "YOUR_GEMINI_API_KEY_HERE";

                if (isMockMode)
                {
                    _logger.LogWarning("API keys are not configured. Running pipeline in high-fidelity mock mode.");

                    // Get actual video duration via FFprobe wrapper
                    stage = "reading video duration";
                    var duration = await _ffmpegService.GetVideoDurationAsync(inputVideoPath, cancellationToken);

                    if (duration <= 10)
                    {
                        hooks.Add(new HookCandidate
                        {
                            StartTime = 0,
                            EndTime = duration,
                            Title = "Quick Clip Highlight",
                            Reason = "Full duration clip chosen due to short source video length."
                        });
                    }
                    else
                    {
                        hooks.Add(new HookCandidate
                        {
                            StartTime = 1.0,
                            EndTime = Math.Min(duration, 12.0),
                            Title = "Viral Intro Hook",
                            Reason = "Captures the early high-engagement intro hook segment."
                        });

                        if (duration >= 20)
                        {
                            hooks.Add(new HookCandidate
                            {
                                StartTime = Math.Round(duration * 0.35, 2),
                                EndTime = Math.Min(duration, Math.Round(duration * 0.35 + 15, 2)),
                                Title = "Core Concept Detail",
                                Reason = "Highlights the core demonstration segment in detail."
                            });
                        }

                        if (duration >= 30)
                        {
                            hooks.Add(new HookCandidate
                            {
                                StartTime = Math.Round(duration * 0.7, 2),
                                EndTime = Math.Min(duration, Math.Round(duration * 0.7 + 10, 2)),
                                Title = "Action Summary Clip",
                                Reason = "Captures the conclusion recap and call to action."
                            });
                        }
                    }
                }
                else
                {
                    // 3. Transcribe audio using Groq
                    stage = "transcribing vocals with Groq Whisper";
                    _logger.LogInformation("Transcribing audio using Groq Whisper...");
                    transcription = await _groqService.TranscribeAudioAsync(tempAudioPath, cancellationToken);

                    if (transcription.Segments == null || transcription.Segments.Count == 0)
                    {
                        throw new Exception("Transcription failed or returned no text segments.");
                    }

                    // 4. Identify hooks using Google Gemini
                    stage = "finding viral sections with Gemini";
                    _logger.LogInformation("Analyzing transcript hooks using Google Gemini...");
                    var aiHooks = await _geminiService.AnalyzeHooksAsync(transcription, cancellationToken);

                    if (aiHooks == null || aiHooks.Count == 0)
                    {
                        throw new Exception("Gemini analysis completed but returned zero hook candidates.");
                    }

                    hooks.AddRange(aiHooks);
                }
            }

            // ── CROP POSITION ANALYSIS ──────────────────────────────────────────────
            // Always attempt visual crop analysis when AI mode is on, regardless of
            // whether transcription produced segments. Hooks may come from mock mode
            // or from Gemini without transcription segments — we still want smart crop.
            if (options.UseAi)
            {
                stage = "analyzing visual crop focus with Gemini";
                try
                {
                    for (var i = 0; i < hooks.Count; i++)
                    {
                        var hook = hooks[i];
                        var clipDuration = hook.EndTime - hook.StartTime;

                        // Sample at 30% into the clip (avoids opening cuts/transitions)
                        // and clamp to a valid position within the clip.
                        var sampleOffset = clipDuration * 0.30;
                        var sampleTime   = hook.StartTime + Math.Max(0.5, sampleOffset);
                        sampleTime       = Math.Min(sampleTime, hook.EndTime - 0.1);

                        var framePath = Path.Combine(_tempDir, $"crop_{runId}_hook{i}.jpg");
                        await _ffmpegService.ExtractFrameAsync(inputVideoPath, framePath, sampleTime, cancellationToken);
                        cropFramePaths.Add(framePath);

                        _logger.LogDebug(
                            "Extracted crop analysis frame for hook {Index} at {SampleTime:F2}s (clip {Start:F2}s–{End:F2}s).",
                            i, sampleTime, hook.StartTime, hook.EndTime);
                    }

                    await _geminiService.ApplyVisualCropAnalysisAsync(hooks, cropFramePaths, cancellationToken);

                    // Restore user's manual crop focus choice for manual overrides
                    foreach (var hook in hooks)
                    {
                        if (hook.IsManual)
                        {
                            hook.CropPosition = options.CropPosition;
                        }
                    }

                    // Log what Gemini returned so unrecognized values are visible in logs.
                    for (var i = 0; i < hooks.Count; i++)
                    {
                        _logger.LogInformation(
                            "Gemini crop result for hook {Index} ('{Title}'): raw='{Raw}'",
                            i, hooks[i].Title, hooks[i].CropPosition);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Visual crop analysis failed for run {RunId}; using fallback crop positions.", runId);
                }
            }
            // ── END CROP POSITION ANALYSIS ──────────────────────────────────────────

            _logger.LogInformation("Processing {Count} hook candidates. Rendering vertical cropped short videos...", hooks.Count);

            // 5. Render cropped vertical short for EACH hook candidate
            for (int i = 0; i < hooks.Count; i++)
            {
                var hook = hooks[i];
                var outputShortFileName = $"short_{runId}_hook{i}.mp4";
                var outputVideoPath = Path.Combine(_outputDir, outputShortFileName);
                string? subtitlesPath = null;

                // FIX: Prioritize AI transcription over static CaptionText if AI is enabled and successful
                if (options.UseAi && transcription?.Segments?.Count > 0)
                {
                    subtitlesPath = Path.Combine(_tempDir, $"captions_{runId}_hook{i}.srt");
                    WriteHookSubtitles(subtitlesPath, transcription.Segments, hook.StartTime, hook.EndTime);
                    subtitlePaths.Add(subtitlesPath);
                }
                else if (hook.IsManual && !string.IsNullOrWhiteSpace(options.CaptionText))
                {
                    subtitlesPath = Path.Combine(_tempDir, $"captions_{runId}_hook{i}.srt");
                    var duration = hook.EndTime - hook.StartTime;
                    using (var writer = new StreamWriter(subtitlesPath, false, System.Text.Encoding.UTF8))
                    {
                        writer.WriteLine(1);
                        writer.WriteLine($"00:00:00,000 --> {FormatSrtTime(duration)}");
                        writer.WriteLine(options.CaptionText);
                        writer.WriteLine();
                    }
                    subtitlePaths.Add(subtitlesPath);
                }

                _logger.LogInformation("Rendering hook candidate #{Index}: '{Title}' ({Start}s - {End}s)", 
                    i + 1, hook.Title, hook.StartTime, hook.EndTime);

                stage = $"rendering hook {i + 1} of {hooks.Count}";

                // normalize crop position with alias handling and detailed logging.
                var cropPosition = NormalizeCropPosition(hook.CropPosition, options.CropPosition, _logger);
                hook.CropPosition = cropPosition;

                _logger.LogInformation(
                    "Hook {Index} final crop position: '{CropPosition}' (AI raw: '{AiRaw}', fallback: '{Fallback}').",
                    i + 1, cropPosition, hook.CropPosition, options.CropPosition);

                await _ffmpegService.RenderShortAsync(inputVideoPath, outputVideoPath, hook.StartTime, hook.EndTime, options, cropPosition, subtitlesPath, cancellationToken);

                // Serve path is relative to the static files mapping (from wwwroot root)
                hook.VideoUrl = $"/shorts/{outputShortFileName}";
            }

            var selectedHook = hooks.FirstOrDefault();
            var relativeVideoUrl = selectedHook?.VideoUrl ?? string.Empty;

            _logger.LogInformation("Pipeline processing complete. Main video available at {Url}", relativeVideoUrl);

            return new VideoProcessingResult
            {
                Hooks = hooks,
                SelectedHook = selectedHook,
                VideoUrl = relativeVideoUrl
            };
        }
        catch (PipelineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline run {RunId} failed during stage {Stage}.", runId, stage);
            throw new PipelineException(runId, stage, ex);
        }
        finally
        {
            // Clean up temporary audio files
            TryDeleteFile(tempAudioPath);
            foreach (var subtitlePath in subtitlePaths)
            {
                TryDeleteFile(subtitlePath);
            }
            foreach (var cropFramePath in cropFramePaths)
            {
                TryDeleteFile(cropFramePath);
            }
        }
    }

    /// <summary>
    /// Resolves a crop position string from AI output into one of the three valid
    /// values: "left", "center", or "right".
    ///
    /// Changes from the original:
    ///   1. Normalizes common aliases Gemini may return (e.g. "centre", "middle",
    ///      "face", "subject", "auto") before the validity check.
    ///   2. Logs a warning with the raw AI value whenever the fallback is used,
    ///      making silent failures visible in the log stream.
    ///   3. Accepts an optional ILogger so the warning is attributed correctly.
    /// </summary>
    private static string NormalizeCropPosition(string? aiCropPosition, string fallback, ILogger? logger = null)
    {
        // Trim whitespace and normalize to lowercase for comparison.
        var value = aiCropPosition?.Trim().ToLowerInvariant();

        // Map common aliases that Gemini (or other AI services) may return
        // to one of the three accepted values.
        value = value switch
        {
            // "center" aliases
            "centre" or "middle" or "auto" or "subject" or "face" or "person" => "center",

            // "left" aliases
            "left-center" or "left center" or "far left" => "left",

            // "right" aliases
            "right-center" or "right center" or "far right" => "right",

            // Pass through unchanged; validity check follows.
            _ => value
        };

        if (value is "left" or "center" or "right")
            return value;

        // The AI returned something we don't recognise after alias resolution.
        // Log the raw value so it's easy to add new aliases later.
        logger?.LogWarning(
            "Unrecognized crop position '{RawValue}' received from AI (after alias resolution: '{Resolved}'). " +
            "Falling back to '{Fallback}'. Consider adding this alias to NormalizeCropPosition.",
            aiCropPosition, value, fallback);

        // Ensure the fallback is itself a valid value; default to "center" if not.
        return fallback is "left" or "center" or "right" ? fallback : "center";
    }

   private static void WriteHookSubtitles(string path, IEnumerable<WhisperSegment> segments, double hookStart, double hookEnd)
    {
        var relevantSegments = segments
            .Where(s => s.End > hookStart && s.Start < hookEnd && !string.IsNullOrWhiteSpace(s.Text))
            .ToList();

        var cards = new List<(double Start, double End, string Text)>();
        
        // Reduced to 3 words max for punchier, faster reading sync
        const int MaxWordsPerCard = 3; 

        foreach (var segment in relevantSegments)
        {
            var segStart = Math.Max(0, segment.Start - hookStart);
            var segEnd   = Math.Min(hookEnd - hookStart, segment.End - hookStart);
            var segDuration = segEnd - segStart;

            var words = segment.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            var chunks = words
                .Select((word, idx) => (word, idx))
                .GroupBy(x => x.idx / MaxWordsPerCard)
                .Select(g => string.Join(" ", g.Select(x => x.word)))
                .ToList();

            // FIX: Proportional time splitting based on text length.
            // This massively improves sync compared to dividing time equally.
            var totalChars = chunks.Sum(c => c.Length);
            var currentTime = segStart;

            foreach (var chunk in chunks)
            {
                var chunkDuration = totalChars > 0 
                    ? segDuration * ((double)chunk.Length / totalChars)
                    : segDuration / chunks.Count;

                var cardEnd = currentTime + chunkDuration;
                cards.Add((currentTime, Math.Max(currentTime + 0.1, cardEnd), chunk));
                currentTime = cardEnd;
            }
        }

        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        for (int i = 0; i < cards.Count; i++)
        {
            var (start, end, text) = cards[i];
            writer.WriteLine(i + 1);
            writer.WriteLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");
            writer.WriteLine(text);
            writer.WriteLine();
        }
    }
    
    private static string FormatSrtTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    private void DeleteOldFiles()
    {
        try
        {
            var maxAge = TimeSpan.FromHours(1); // Keep files for 1 hour to allow user download
            var now = DateTime.UtcNow;

            // Clean output shorts directory
            if (Directory.Exists(_outputDir))
            {
                var directoryInfo = new DirectoryInfo(_outputDir);
                foreach (var file in directoryInfo.GetFiles())
                {
                    // Only clean up generated videos
                    if (file.Name.StartsWith("short_") && (now - file.CreationTimeUtc) > maxAge)
                    {
                        try
                        {
                            file.Delete();
                            _logger.LogInformation("Deleted old rendered short file: {Name}", file.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete old rendered short: {Name}", file.Name);
                        }
                    }
                }
            }

            // Clean temp directory (for any leftovers from crashes)
            if (Directory.Exists(_tempDir))
            {
                var directoryInfo = new DirectoryInfo(_tempDir);
                foreach (var file in directoryInfo.GetFiles())
                {
                    if ((now - file.CreationTimeUtc) > maxAge)
                    {
                        try
                        {
                            file.Delete();
                            _logger.LogInformation("Deleted leftover temp file: {Name}", file.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete leftover temp file: {Name}", file.Name);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during old files cleanup.");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Deleted temporary file: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary file: {Path}", path);
        }
    }
}