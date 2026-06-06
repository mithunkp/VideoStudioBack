using System.Diagnostics;
using Microsoft.Extensions.Logging;
using backend.Models;

namespace backend.Services;

public interface IFFmpegService
{
    Task<bool> HasAudioStreamAsync(string inputVideoPath, CancellationToken cancellationToken = default);
    Task<string> ExtractAudioAsync(string inputVideoPath, string outputAudioPath, CancellationToken cancellationToken = default);
    Task<string> RenderShortAsync(string inputVideoPath, string outputVideoPath, double startTime, double endTime, VideoRenderOptions options, string cropPosition, string? subtitlesPath, CancellationToken cancellationToken = default);
    Task<double> GetVideoDurationAsync(string inputVideoPath, CancellationToken cancellationToken = default);
    Task ExtractFrameAsync(string inputVideoPath, string outputImagePath, double time, CancellationToken cancellationToken = default);
    Task MuxVideoAndAudioAsync(string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken = default);
}

public class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;

    public FFmpegService(ILogger<FFmpegService> _logger)
    {
        this._logger = _logger;
    }

    public async Task<double> GetVideoDurationAsync(string inputVideoPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting duration of video file {Input}", inputVideoPath);

        // ffprobe arguments to get duration only
        var arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputVideoPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        
        _logger.LogDebug("Starting FFprobe with args: {Args}", arguments);
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorOutputTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var errorOutput = await errorOutputTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("FFprobe process failed with exit code {Code}. Error details:\n{Error}", process.ExitCode, errorOutput);
            throw new Exception($"FFprobe failed to get duration of video file. Error: {errorOutput}");
        }

        var cleanOutput = output.Trim();
        if (double.TryParse(cleanOutput, out var duration))
        {
            _logger.LogInformation("Extracted video duration: {Duration}s", duration);
            return duration;
        }

        throw new Exception($"Failed to parse duration from FFprobe output: {cleanOutput}");
    }

    public async Task<bool> HasAudioStreamAsync(string inputVideoPath, CancellationToken cancellationToken = default)
    {
        var arguments = $"-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 \"{inputVideoPath}\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new Exception($"FFprobe failed while checking audio streams: {error}");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.Equals("audio", StringComparison.OrdinalIgnoreCase));
    }

    public async Task MuxVideoAndAudioAsync(string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Muxing separate video {Video} and audio {Audio} into {Output}", videoPath, audioPath, outputPath);

        // Muxing parameters: copy video codec, encode audio to aac
        var arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac \"{outputPath}\"";

        await RunFFmpegProcessAsync(arguments, cancellationToken);

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException("FFmpeg failed to produce the muxed video file.", outputPath);
        }

        _logger.LogInformation("Muxing completed successfully.");
    }

    public async Task ExtractFrameAsync(string inputVideoPath, string outputImagePath, double time, CancellationToken cancellationToken = default)
    {
        var arguments = $"-y -ss {time:0.00} -i \"{inputVideoPath}\" -frames:v 1 -vf \"scale=640:-2\" \"{outputImagePath}\"";
        await RunFFmpegProcessAsync(arguments, cancellationToken);
        if (!File.Exists(outputImagePath))
        {
            throw new FileNotFoundException("FFmpeg failed to extract a crop-analysis frame.", outputImagePath);
        }
    }

    public async Task<string> ExtractAudioAsync(string inputVideoPath, string outputAudioPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Extracting audio from {Input} to {Output}", inputVideoPath, outputAudioPath);

        // Extract audio as standard MP3 for Groq Whisper
        // -vn: exclude video
        // -acodec libmp3lame -q:a 4: quality profile 4 MP3 (~128kbps)
        // -y: overwrite output
        var arguments = $"-y -i \"{inputVideoPath}\" -vn -acodec libmp3lame -ar 22050 -ac 1 -b:a 64k \"{outputAudioPath}\"";

        await RunFFmpegProcessAsync(arguments, cancellationToken);

        if (!File.Exists(outputAudioPath))
        {
            throw new FileNotFoundException("FFmpeg failed to produce the audio file.", outputAudioPath);
        }

        _logger.LogInformation("Audio extraction completed successfully.");
        return outputAudioPath;
    }

    public async Task<string> RenderShortAsync(string inputVideoPath, string outputVideoPath, double startTime, double endTime, VideoRenderOptions options, string cropPosition, string? subtitlesPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Rendering cropped vertical short from {Input} (from {Start} to {End}) to {Output}",
            inputVideoPath, startTime, endTime, outputVideoPath);

        // -ss before -i for fast seeking
        // -to for duration cut
        // -vf crop for 9:16 vertical crop
        // -c:v libx264 -preset superfast -crf 23 -c:a aac -b:a 128k
        var duration = endTime - startTime;
        var cropX = cropPosition.ToLowerInvariant() switch
        {
            "left" => "0",
            "right" => "iw-ow",
            _ => "(iw-ow)/2"
        };
        var filters = $"crop=ih*9/16:ih:{cropX}:0,scale=720:1280";

        var caption = SanitizeCaption(options.CaptionText);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            var font = options.CaptionFont.ToLowerInvariant() switch
            {
                "editorial" => "serif",
                "mono" => "monospace",
                _ => "sans-serif"
            };
            var baseY = options.CaptionPosition.ToLowerInvariant() switch
            {
                "top" => "h*0.12",
                "middle" => "(h-text_h)/2",
                _ => "h*0.78"
            };
            var alpha = options.TextAnimation.ToLowerInvariant() != "none"
                ? ":alpha='if(lt(t\\,0.5)\\,t/0.5\\,1)'"
                : string.Empty;
            filters += $",drawtext=font='{font}':text='{caption}':fontcolor={NormalizeColor(options.CaptionColor)}:fontsize=h/16:borderw=4:bordercolor=black:shadowcolor={NormalizeColor(options.AccentColor)}:shadowx=0:shadowy=8:x=(w-text_w)/2:y='{baseY}'{alpha}";
        }
        if (!string.IsNullOrWhiteSpace(subtitlesPath) && File.Exists(subtitlesPath))
        {
            var escapedPath = subtitlesPath.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");
            filters += $",subtitles='{escapedPath}':force_style='FontName=Arial,FontSize=18,Bold=1,PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=3,Shadow=1,Alignment=2,MarginV=90'";
        }

        var arguments = $"-y -ss {startTime:0.00} -t {duration:0.00} -i \"{inputVideoPath}\" -vf \"{filters}\" -c:v libx264 -preset superfast -crf 23 -c:a aac -b:a 128k \"{outputVideoPath}\"";

        await RunFFmpegProcessAsync(arguments, cancellationToken);

        if (!File.Exists(outputVideoPath))
        {
            throw new FileNotFoundException("FFmpeg failed to produce the vertical video short file.", outputVideoPath);
        }

        _logger.LogInformation("Vertical short rendering completed successfully.");
        return outputVideoPath;
    }

    private static string SanitizeCaption(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || ".,!?-".Contains(c)).Take(60).ToArray());

    private static string NormalizeColor(string? value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value ?? string.Empty, "^#[0-9a-fA-F]{6}$")
            ? $"0x{value![1..]}"
            : "white";

    private async Task RunFFmpegProcessAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        _logger.LogDebug("Starting FFmpeg with args: {Args}", arguments);

        process.Start();

        // FFmpeg writes standard logging to StandardError
        var errorOutputTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var errorOutput = await errorOutputTask;
        var standardOutput = await standardOutputTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("FFmpeg process failed with exit code {Code}. Error details:\n{Error}", process.ExitCode, errorOutput);
            throw new Exception($"FFmpeg process failed with exit code {process.ExitCode}. Error: {SummarizeProcessError(errorOutput)}");
        }
    }

    private static string SummarizeProcessError(string errorOutput)
    {
        if (errorOutput.Contains("Output file does not contain any stream", StringComparison.OrdinalIgnoreCase))
        {
            return "The requested media stream was not found. This usually means the MP4 has no audio track.";
        }

        var lines = errorOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(12);

        return string.Join(Environment.NewLine, lines);
    }
}
