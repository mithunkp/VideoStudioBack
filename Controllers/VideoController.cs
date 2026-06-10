using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using backend.Services;
using backend.Models;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VideoController : ControllerBase
{
    private readonly IVideoProcessingOrchestrator _orchestrator;
    private readonly IGeminiHookService _geminiService;
    private readonly ILogger<VideoController> _logger;

    public VideoController(
        IVideoProcessingOrchestrator orchestrator, 
        IGeminiHookService geminiService,
        ILogger<VideoController> logger)
    {
        _orchestrator = orchestrator;
        _geminiService = geminiService;
        _logger = logger;
    }

    [HttpGet("limits")]
    public async Task<IActionResult> GetLimits(CancellationToken cancellationToken)
    {
        try
        {
            if (GeminiHookService.GeminiTier == "Unknown" || GeminiHookService.GroqRequestsRemaining == "Unknown")
            {
                await _geminiService.ProbeApiLimitsAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while attempting to probe API limits.");
        }

        var limits = _geminiService.GetApiLimits();
        return Ok(limits);
    }

    [HttpPost("process")]
    [DisableRequestSizeLimit] // Allows processing of large video files locally
    [RequestFormLimits(MultipartBodyLengthLimit = 4294967296)]
    public async Task<IActionResult> ProcessVideo(IFormFile file, [FromForm] VideoRenderOptions options, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded or file is empty." });
        }

        var extension = Path.GetExtension(file.FileName).ToLower();
        if (extension != ".mp4")
        {
            return BadRequest(new { error = "Only widescreen MP4 files (.mp4) are supported by this application." });
        }

        _logger.LogInformation("Received file processing request: {Name} (Size: {Size} bytes)", 
            file.FileName, file.Length);

        try
        {
            using var stream = file.OpenReadStream();
            var result = await _orchestrator.ProcessVideoAsync(stream, file.FileName, options, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Configuration or setup error processing video.");
            return StatusCode(400, new { error = ex.Message });
        }
        catch (PipelineException ex)
        {
            _logger.LogError(ex, "Pipeline run {RunId} failed at stage {Stage}.", ex.RunId, ex.Stage);
            var statusCode = ex.InnerException is InvalidOperationException ? 400 : 500;
            return StatusCode(statusCode, new
            {
                error = ex.Message,
                runId = ex.RunId,
                stage = ex.Stage,
                detail = ex.InnerException is InvalidOperationException
                    ? ex.InnerException.Message
                    : ex.InnerException?.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while processing video file.");
            var errorMsg = ex.InnerException != null ? $"{ex.Message} (Detail: {ex.InnerException.Message})" : ex.Message;
            return StatusCode(500, new { error = $"Pipeline error: {errorMsg}" });
        }
    }

    public class ProcessUrlRequest : VideoRenderOptions
    {
        public string VideoUrl { get; set; } = string.Empty;
    }

    [HttpPost("process-url")]
    public async Task<IActionResult> ProcessVideoUrl([FromBody] ProcessUrlRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.VideoUrl))
        {
            return BadRequest(new { error = "Video URL is required." });
        }

        _logger.LogInformation("Received URL processing request: {Url}", request.VideoUrl);

        try
        {
            var result = await _orchestrator.ProcessVideoUrlAsync(request.VideoUrl, request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Configuration or setup error processing video URL.");
            return StatusCode(400, new { error = ex.Message });
        }
        catch (PipelineException ex)
        {
            _logger.LogError(ex, "Pipeline run {RunId} failed at stage {Stage}.", ex.RunId, ex.Stage);
            var statusCode = ex.InnerException is InvalidOperationException ? 400 : 500;
            return StatusCode(statusCode, new
            {
                error = ex.Message,
                runId = ex.RunId,
                stage = ex.Stage,
                detail = ex.InnerException is InvalidOperationException
                    ? ex.InnerException.Message
                    : ex.InnerException?.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while processing video URL.");
            var errorMsg = ex.InnerException != null ? $"{ex.Message} (Detail: {ex.InnerException.Message})" : ex.Message;
            return StatusCode(500, new { error = $"Pipeline error: {errorMsg}" });
        }
    }
}
