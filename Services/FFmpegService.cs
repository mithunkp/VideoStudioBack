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
    Task<string> DetectSmartCropPositionAsync(string inputVideoPath, double startTime, double endTime, CancellationToken cancellationToken = default);
    Task<string> DetectSmartCropPositionAsync(string inputVideoPath, double startTime, double endTime, string panMode, CancellationToken cancellationToken = default);
}

public class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;

    public FFmpegService(ILogger<FFmpegService> logger)
    {
        _logger = logger;
    }

    public async Task<double> GetVideoDurationAsync(string inputVideoPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting duration of video file {Input}", inputVideoPath);

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

        var outputTask      = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorOutputTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output      = await outputTask;
        var errorOutput = await errorOutputTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("FFprobe failed with exit code {Code}:\n{Error}", process.ExitCode, errorOutput);
            throw new Exception($"FFprobe failed to get duration. Error: {errorOutput}");
        }

        var cleanOutput = output.Trim();
        if (double.TryParse(cleanOutput, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var duration))
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
        var errorTask  = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error  = await errorTask;

        if (process.ExitCode != 0)
            throw new Exception($"FFprobe failed while checking audio streams: {error}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Any(line => line.Equals("audio", StringComparison.OrdinalIgnoreCase));
    }

    public async Task MuxVideoAndAudioAsync(string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Muxing {Video} + {Audio} → {Output}", videoPath, audioPath, outputPath);
        var arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac \"{outputPath}\"";
        await RunFFmpegProcessAsync(arguments, cancellationToken);

        if (!File.Exists(outputPath))
            throw new FileNotFoundException("FFmpeg failed to produce the muxed video file.", outputPath);

        _logger.LogInformation("Muxing completed successfully.");
    }

    public async Task ExtractFrameAsync(string inputVideoPath, string outputImagePath, double time, CancellationToken cancellationToken = default)
    {
        var arguments = $"-y -ss {time:0.00} -i \"{inputVideoPath}\" -frames:v 1 -vf \"scale=640:-2\" \"{outputImagePath}\"";
        await RunFFmpegProcessAsync(arguments, cancellationToken);

        if (!File.Exists(outputImagePath))
            throw new FileNotFoundException("FFmpeg failed to extract a crop-analysis frame.", outputImagePath);
    }

    public async Task<string> ExtractAudioAsync(string inputVideoPath, string outputAudioPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Extracting audio from {Input} to {Output}", inputVideoPath, outputAudioPath);
        var arguments = $"-y -i \"{inputVideoPath}\" -vn -acodec libmp3lame -ar 22050 -ac 1 -b:a 64k \"{outputAudioPath}\"";
        await RunFFmpegProcessAsync(arguments, cancellationToken);

        if (!File.Exists(outputAudioPath))
            throw new FileNotFoundException("FFmpeg failed to produce the audio file.", outputAudioPath);

        _logger.LogInformation("Audio extraction completed successfully.");
        return outputAudioPath;
    }

    public async Task<string> RenderShortAsync(
        string inputVideoPath,
        string outputVideoPath,
        double startTime,
        double endTime,
        VideoRenderOptions options,
        string cropPosition,
        string? subtitlesPath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Rendering short from {Input} ({Start}s → {End}s) to {Output}",
            inputVideoPath, startTime, endTime, outputVideoPath);

        // FIX: Bypass the AI natural cut if the user is doing a manual trim!
        var naturalEnd = options.ManualTrimEnabled 
            ? endTime 
            : await FindNaturalCutPointAsync(inputVideoPath, endTime, 3.0, cancellationToken);
            
        // Ensure duration never drops below a safe minimum
        var duration = Math.Max(0.5, naturalEnd - startTime);
        
        _logger.LogInformation("Cut: {Original:F2}s → {Adjusted:F2}s (Duration: {Duration:F2}s)", endTime, naturalEnd, duration);

        string cropX;
        if (cropPosition.StartsWith("min(") || cropPosition.StartsWith("(") ||
            cropPosition.All(c => char.IsDigit(c) || c == '.' || c == '-'))
        {
            cropX = cropPosition;
        }
        else
        {
            var durStr  = duration.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var cropMod = (options.CropMode ?? "static").Trim().ToLowerInvariant();
            var pos     = cropPosition.ToLowerInvariant();

            if (cropMod == "linear")
            {
                // Gentle linear drift: left/right drift 25 % of max pan; center oscillates
                cropX = pos switch
                {
                    "left"  => $"t/{durStr}*(iw-ow)/4",
                    "right" => $"iw-ow-t/{durStr}*(iw-ow)/4",
                    _       => $"(iw-ow)/2+sin(2*PI*t/{durStr})*(iw-ow)/10"
                };
            }
            else if (cropMod == "eased")
            {
                // Sine-eased drift — smooth start + settle
                cropX = pos switch
                {
                    "left"  => $"sin(PI/2*t/{durStr})*(iw-ow)/4",
                    "right" => $"iw-ow-sin(PI/2*t/{durStr})*(iw-ow)/4",
                    _       => $"(iw-ow)/2+sin(2*PI*t/{durStr})*(iw-ow)/10"
                };
            }
            else // static
            {
                cropX = pos switch
                {
                    "left"  => "0",
                    "right" => "iw-ow",
                    _       => "(iw-ow)/2"
                };
            }
        }

        var filters = $"crop=ih*9/16:ih:{cropX}:0,scale=720:1280";

        string? assPath = null;
        bool hasSubtitles = !string.IsNullOrWhiteSpace(subtitlesPath) && File.Exists(subtitlesPath);

        if (hasSubtitles)
        {
            assPath = Path.ChangeExtension(subtitlesPath!, ".ass");
            ConvertSrtToAss(subtitlesPath!, assPath, options);

            var escapedAss = assPath
                .Replace("\\", "/")
                .Replace(":", "\\:")
                .Replace("'", "\\'");

            filters += $",ass='{escapedAss}'";

            _logger.LogInformation(
                "ASS captions: font={Font}, color={Color}, accent={Accent}, pos={Pos}, anim={Anim}",
                options.CaptionFont, options.CaptionColor, options.AccentColor,
                options.CaptionPosition, options.TextAnimation);
        }

        var arguments =
            $"-y -ss {startTime:0.00} -t {duration:0.00} -i \"{inputVideoPath}\" " +
            $"-vf \"{filters}\" " +
            $"-c:v libx264 -preset superfast -crf 23 -c:a aac -b:a 128k \"{outputVideoPath}\"";

        await RunFFmpegProcessAsync(arguments, cancellationToken);

        if (!File.Exists(outputVideoPath))
            throw new FileNotFoundException("FFmpeg failed to produce the vertical short.", outputVideoPath);

        if (assPath != null)
            try { if (File.Exists(assPath)) File.Delete(assPath); } catch { /* ignore */ }

        _logger.LogInformation("Short rendered successfully: {Output}", outputVideoPath);
        return outputVideoPath;
    }

    private async Task<double> FindNaturalCutPointAsync(
        string inputVideoPath, double targetTime, double windowSeconds, CancellationToken ct)
    {
        try
        {
            var scanStart    = Math.Max(0, targetTime - windowSeconds);
            var scanDuration = windowSeconds * 2;

            var arguments =
                $"-v error -ss {scanStart:0.00} -t {scanDuration:0.00} -i \"{inputVideoPath}\" " +
                $"-af \"silencedetect=noise=-35dB:duration=0.15\" -f null -";

            var si = new ProcessStartInfo
            {
                FileName = "ffmpeg", Arguments = arguments,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };

            using var process = new Process { StartInfo = si };
            process.Start();

            var errTask = process.StandardError.ReadToEndAsync(ct);
            var stdTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var output = await errTask + await stdTask;

            var candidates = System.Text.RegularExpressions.Regex
                .Matches(output, @"silence_end: ([\d.]+)")
                .Select(m => scanStart + double.Parse(
                    m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Where(t => t >= targetTime - windowSeconds && t <= targetTime + windowSeconds)
                .ToList();

            if (candidates.Count == 0)
            {
                _logger.LogDebug("No silence near {Target:F2}s — keeping original.", targetTime);
                return targetTime;
            }

            var best = candidates.MinBy(t => Math.Abs(t - targetTime));
            _logger.LogInformation("Natural cut: {Original:F2}s → {Best:F2}s", targetTime, best);
            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Natural cut detection failed; keeping {Target:F2}s.", targetTime);
            return targetTime;
        }
    }

  // ═══════════════════════════════════════════════════════════════════════════════
    // ConvertSrtToAss
    // ═══════════════════════════════════════════════════════════════════════════════
    private static void ConvertSrtToAss(string srtPath, string assPath, VideoRenderOptions options)
    {
        var fontName = (options.CaptionFont ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "impact"       or "default"    => "Impact",
            "georgia"      or "editorial"  => "Georgia",
            "courier new"  or "mono"       => "Courier New",
            "outfit"       or "modern"     => "Outfit",
            "arial"        or "clean bold" => "Arial",
            "roboto"                       => "Roboto",
            "oswald"                       => "Oswald",
            var v when !string.IsNullOrWhiteSpace(v) => options.CaptionFont.Trim(),
            _                              => "Impact"
        };

        const int PlayResX = 720;
        const int PlayResY = 1280;
        const int FontSize = 95; 

        // ASS specifies -1 for true, 0 for false
        var bold = fontName is "Impact" or "Arial" or "Oswald" ? -1 : 0;

        // Color Mapping Fix:
        // Text gets the CaptionColor. The Shadow gets the AccentColor. 
        // We use a crisp, thin black outline to keep the text separated and legible.
        var primaryColour = HexToAssColour(options.CaptionColor);
        var shadowColour  = HexToAssColour(options.AccentColor);    
        const string outlineColour = "&H00000000"; // Solid Black
        
        var (alignment, marginV) = (options.CaptionPosition ?? "bottom").Trim().ToLowerInvariant() switch
        {
            "top"    => (8,  150),   
            "middle" => (5,   0),   
            _        => (2, 250),   
        };

        var animMode = (options.TextAnimation ?? "none").Trim().ToLowerInvariant();

       string BuildAnimTag(double evStartSec, double evEndSec)
        {
            var durationMs = (int)Math.Max(200, (evEndSec - evStartSec) * 1000);
            
            // FIX: Drastically speed up animations. 
            // 80ms makes the text pop instantly with the spoken audio.
            var fadeInMs   = Math.Min(80, durationMs / 4); 
            var fadeOutMs  = Math.Min(80, durationMs / 4);

            return animMode switch
            {
                "fade" => $"{{\\fad({fadeInMs},{fadeOutMs})}}",
                "pop" => $"{{\\fad({fadeInMs},0)\\fscx120\\fscy120\\t(0,{fadeInMs},\\fscx100\\fscy100)}}",
                // FIX: Convert margV (distance-from-edge) to absolute screen-Y for \move.
                // With \an2 (bottom), the Y coordinate in \move is measured from the TOP of
                // the screen, so we must use PlayResY - marginV, not marginV directly.
                "slide" => (options.CaptionPosition ?? "bottom").Trim().ToLowerInvariant() switch
                {
                    "top"    => $"{{\\an{alignment}\\fad({fadeInMs},0)\\move({PlayResX / 2},{marginV + 30},{PlayResX / 2},{marginV},0,{fadeInMs})}}",
                    "middle" => $"{{\\an{alignment}\\fad({fadeInMs},0)\\move({PlayResX / 2},{PlayResY / 2 + 30},{PlayResX / 2},{PlayResY / 2},0,{fadeInMs})}}",
                    _        => $"{{\\an{alignment}\\fad({fadeInMs},0)\\move({PlayResX / 2},{PlayResY - marginV + 30},{PlayResX / 2},{PlayResY - marginV},0,{fadeInMs})}}"
                },
                _ => string.Empty
            };
        }

        var srtLines = File.ReadAllLines(srtPath);
        var events   = ParseSrtEvents(srtLines);

        using var w = new StreamWriter(assPath, false, System.Text.Encoding.UTF8);

        w.WriteLine("[Script Info]");
        w.WriteLine("ScriptType: v4.00+");
        w.WriteLine($"PlayResX: {PlayResX}");
        w.WriteLine($"PlayResY: {PlayResY}");
        w.WriteLine("ScaledBorderAndShadow: yes");
        w.WriteLine("WrapStyle: 0");     
        w.WriteLine();

        w.WriteLine("[V4+ Styles]");
        w.WriteLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        w.WriteLine(
            $"Style: Caption," +
            $"{fontName},{FontSize}," +
            $"{primaryColour},&H00FFFFFF,{outlineColour},{shadowColour}," +
            $"{bold},0,-1,0," +    // FIXED: Changed Underline from 0 to -1 (True)
            $"100,100,1,0," +      
            $"1,2,6," +            // FIXED: Thin outline (2px), Thick Accent Shadow (6px offset)
            $"{alignment}," +      
            $"40,40,{marginV},1"); 
        w.WriteLine();

        w.WriteLine("[Events]");
        w.WriteLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var (evStart, evEnd, text) in events)
        {
            var assStart = FormatAssTime(evStart);
            var assEnd   = FormatAssTime(evEnd);
            var animTag  = BuildAnimTag(evStart, evEnd);
            w.WriteLine($"Dialogue: 0,{assStart},{assEnd},Caption,,0,0,0,,{animTag}{text}");
        }
    }    
    
    private static List<(double Start, double End, string Text)> ParseSrtEvents(string[] lines)
    {
        var events = new List<(double, double, string)>();
        var i      = 0;

        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) || int.TryParse(lines[i].Trim(), out _))
            {
                i++;
                continue;
            }

            if (lines[i].Contains("-->"))
            {
                var parts = lines[i].Split("-->", StringSplitOptions.TrimEntries);
                if (parts.Length == 2 &&
                    TryParseSrtTime(parts[0], out var start) &&
                    TryParseSrtTime(parts[1], out var end))
                {
                    i++;
                    var textLines = new List<string>();
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        textLines.Add(lines[i].Trim());
                        i++;
                    }

                    // Forced text casing to UPPERCASE for stylized impact
                    var raw   = string.Join(" ", textLines).ToUpperInvariant();
                    var clean = System.Text.RegularExpressions.Regex
                                    .Replace(raw, @"\{\\[^}]*\}", string.Empty)
                                    .Trim();

                    if (!string.IsNullOrWhiteSpace(clean))
                        events.Add((start, end, clean));
                }
                else { i++; }
            }
            else { i++; }
        }

        return events;
    }

    private static bool TryParseSrtTime(string s, out double seconds)
    {
        seconds = 0;
        s = s.Trim().Replace(',', '.');
        if (TimeSpan.TryParseExact(s, @"hh\:mm\:ss\.fff",
                System.Globalization.CultureInfo.InvariantCulture, out var ts))
        {
            seconds = ts.TotalSeconds;
            return true;
        }
        return false;
    }

    private static string FormatAssTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    private static string HexToAssColour(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "&H00FFFFFF";
        
        hex = hex.Trim();
        if (hex.StartsWith("#")) hex = hex[1..];

        // Handle #FFF and 8-character #RRGGBBAA formats gracefully
        if (hex.Length == 3)
            hex = new string(new char[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
        else if (hex.Length == 8)
            hex = hex[0..6];

        if (hex.Length != 6 || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^[0-9a-fA-F]{6}$"))
            return "&H00FFFFFF"; 

        var r = hex[0..2];
        var g = hex[2..4];
        var b = hex[4..6];
        return $"&H00{b}{g}{r}";
    }

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
        _logger.LogDebug("FFmpeg args: {Args}", arguments);
        process.Start();

        var errorTask  = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdTask    = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var errorOutput = await errorTask;
        await stdTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("FFmpeg exit {Code}:\n{Error}", process.ExitCode, errorOutput);
            throw new Exception(
                $"FFmpeg failed (exit {process.ExitCode}): {SummarizeProcessError(errorOutput)}");
        }
    }

    private static string SummarizeProcessError(string errorOutput)
    {
        if (errorOutput.Contains("Output file does not contain any stream",
                StringComparison.OrdinalIgnoreCase))
            return "The requested media stream was not found. This usually means the MP4 has no audio track.";

        return string.Join(Environment.NewLine,
            errorOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))
                .TakeLast(12));
    }

    public async Task<string> DetectSmartCropPositionAsync(
        string inputVideoPath, double startTime, double endTime,
        CancellationToken cancellationToken = default)
        => await DetectSmartCropPositionAsync(inputVideoPath, startTime, endTime, "static", cancellationToken);

    public async Task<string> DetectSmartCropPositionAsync(
        string inputVideoPath, double startTime, double endTime, string panMode,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Smart crop: {Input} {Start}–{End}s panMode={Mode}",
            inputVideoPath, startTime, endTime, panMode);

        var duration   = endTime - startTime;
        var framePaths = new List<string>();
        var votes      = new List<string>();
        var earlyVotes = new List<string>();
        var lateVotes  = new List<string>();

        try
        {
            for (int i = 0; i < 5; i++)
            {
                var timestamp = startTime + (i / 4.0) * duration;
                var framePath = Path.Combine(Path.GetTempPath(), $"crop_frame_{Guid.NewGuid()}.jpg");
                framePaths.Add(framePath);

                try
                {
                    await ExtractFrameAsync(inputVideoPath, framePath, timestamp, cancellationToken);
                    var leftYAVG  = await RunFFprobeStatsAsync(framePath, "crop=iw/2:ih:0:0",     cancellationToken);
                    var rightYAVG = await RunFFprobeStatsAsync(framePath, "crop=iw/2:ih:iw/2:0", cancellationToken);

                    var vote = leftYAVG > rightYAVG + 5 ? "left"
                             : rightYAVG > leftYAVG + 5 ? "right"
                             : "center";

                    votes.Add(vote);
                    if (i <= 1) earlyVotes.Add(vote);
                    else if (i >= 3) lateVotes.Add(vote);

                    _logger.LogDebug("Frame {F}: L={L:F0} R={R:F0} → {V}", i, leftYAVG, rightYAVG, vote);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Frame at {T}s failed: {E}", timestamp, ex.Message);
                }
            }

            if (votes.Count == 0)
            {
                _logger.LogWarning("No frames analyzed — defaulting to center.");
                return "center";
            }

            string Majority(List<string> v) =>
                v.Count == 0 ? "center" :
                v.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;

            var overall = Majority(votes);
            var early   = Majority(earlyVotes);
            var late    = Majority(lateVotes);

            _logger.LogInformation("Crop: overall={O} early={E} late={L}", overall, early, late);

            return panMode.Equals("linear", StringComparison.OrdinalIgnoreCase)
                ? BuildCropFilterExpression(early, late, duration, "linear")
                : panMode.Equals("eased", StringComparison.OrdinalIgnoreCase)
                ? BuildCropFilterExpression(early, late, duration, "eased")
                : overall;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smart crop detection error");
            return "center";
        }
        finally
        {
            foreach (var fp in framePaths)
                try { if (File.Exists(fp)) File.Delete(fp); } catch { }
        }
    }

    private async Task<double> RunFFprobeStatsAsync(string framePath, string cropFilter, CancellationToken ct)
    {
        var arguments = $"-v error -i \"{framePath}\" -vf \"{cropFilter},signalstats\" -f null -";
        try
        {
            var output = await RunFFprobeAsync(arguments, ct);
            var match  = System.Text.RegularExpressions.Regex.Match(output, @"YAVG:(\d+\.?\d*)");
            if (match.Success && double.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var yavg))
                return yavg;
        }
        catch (Exception ex) { _logger.LogWarning("YAVG stats failed: {E}", ex.Message); }
        return 0.0;
    }

    private async Task<string> RunFFprobeAsync(string arguments, CancellationToken ct)
    {
        var si = new ProcessStartInfo
        {
            FileName = "ffprobe", Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };

        using var process = new Process { StartInfo = si };
        _logger.LogDebug("FFprobe args: {Args}", arguments);
        process.Start();

        var errTask = process.StandardError.ReadToEndAsync(ct);
        var stdTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var err = await errTask;
        var std = await stdTask;

        if (process.ExitCode != 0)
            throw new Exception($"FFprobe exit {process.ExitCode}: {err}");

        return err + std;
    }

    private static string BuildCropFilterExpression(
        string startPos, string endPos, double duration, string panMode)
    {
        double Ratio(string p) => p.ToLowerInvariant() switch
        {
            "left"  => 0.0, "right" => 1.0, _ => 0.5
        };

        var s = Ratio(startPos);
        var e = Ratio(endPos);

        if (panMode.Equals("linear", StringComparison.OrdinalIgnoreCase))
            return $"min(max(0,{s:0.##}*(iw-ow)+({e:0.##}-{s:0.##})*(iw-ow)*t/{duration:0.##}),iw-ow)";

        if (panMode.Equals("eased", StringComparison.OrdinalIgnoreCase))
        {
            var h = duration / 2.0;
            return $"min(max(0,({s:0.##}+({e:0.##}-{s:0.##})*if(lt(t,{h:0.##}),4*pow(t/{duration:0.##},3),1-pow((-2*t+2*{duration:0.##})/{duration:0.##},3)/2))*(iw-ow)),iw-ow)";
        }

        return s switch { 0.0 => "0", 1.0 => "iw-ow", _ => "(iw-ow)/2" };
    }
}