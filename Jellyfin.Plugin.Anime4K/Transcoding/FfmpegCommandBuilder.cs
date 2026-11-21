using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Anime4K.Configuration;

namespace Jellyfin.Plugin.Anime4K.Transcoding;

/// <summary>
/// Description of the input media needed to build the ffmpeg command line.
/// </summary>
public sealed class SourceInfo
{
    /// <summary>Gets or sets the media file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute index of the video stream.</summary>
    public int VideoStreamIndex { get; set; }

    /// <summary>Gets or sets the source width.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the source height.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the absolute audio stream index (null = no audio).</summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets the subtitle to burn in (null = none).</summary>
    public SubtitleInfo? Subtitle { get; set; }
}

/// <summary>
/// A subtitle stream that is burned into the output.
/// </summary>
public sealed class SubtitleInfo
{
    /// <summary>Gets or sets the absolute stream index (internal subtitles).</summary>
    public int StreamIndex { get; set; }

    /// <summary>Gets or sets the index among the subtitle streams of the file (for the subtitles filter).</summary>
    public int RelativeIndex { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a text subtitle (SRT/ASS/...).</summary>
    public bool IsText { get; set; }

    /// <summary>Gets or sets the path of an external subtitle file (null = embedded).</summary>
    public string? ExternalPath { get; set; }
}

/// <summary>
/// Resolved encoder settings.
/// </summary>
/// <param name="Name">ffmpeg encoder name.</param>
/// <param name="IsHevc">Whether the codec is HEVC.</param>
/// <param name="Family">Encoder family (x264, nvenc, qsv, vaapi, amf, videotoolbox).</param>
public sealed record EncoderChoice(string Name, bool IsHevc, string Family);

/// <summary>
/// Builds the ffmpeg command line for Anime4K upscaling via libplacebo.
/// </summary>
public static class FfmpegCommandBuilder
{
    /// <summary>
    /// Resolves the encoder from the plugin configuration and Jellyfin's hardware acceleration type.
    /// </summary>
    /// <param name="configured">Configured encoder ("auto" or ffmpeg encoder name).</param>
    /// <param name="jellyfinHwAccel">Jellyfin hardware acceleration type.</param>
    /// <param name="jellyfinHwEncoding">Whether Jellyfin has hardware encoding enabled.</param>
    /// <returns>Encoder choice.</returns>
    public static EncoderChoice ResolveEncoder(string? configured, string? jellyfinHwAccel, bool jellyfinHwEncoding)
    {
        var name = (configured ?? "auto").Trim();
        if (string.IsNullOrEmpty(name) || name.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            name = !jellyfinHwEncoding ? "libx264" : (jellyfinHwAccel ?? string.Empty).ToLowerInvariant() switch
            {
                "nvenc" => "h264_nvenc",
                "qsv" => "h264_qsv",
                "vaapi" => "h264_vaapi",
                "amf" => "h264_amf",
                "videotoolbox" => "h264_videotoolbox",
                "rkmpp" => "h264_rkmpp",
                _ => "libx264",
            };
        }

        name = name.ToLowerInvariant();
        var hevc = name.StartsWith("hevc", StringComparison.Ordinal) || name == "libx265";
        var family = name switch
        {
            "libx264" or "libx265" => "x26x",
            _ when name.EndsWith("_nvenc", StringComparison.Ordinal) => "nvenc",
            _ when name.EndsWith("_qsv", StringComparison.Ordinal) => "qsv",
            _ when name.EndsWith("_vaapi", StringComparison.Ordinal) => "vaapi",
            _ when name.EndsWith("_amf", StringComparison.Ordinal) => "amf",
            _ when name.EndsWith("_videotoolbox", StringComparison.Ordinal) => "videotoolbox",
            _ => "other",
        };
        return new EncoderChoice(name, hevc, family);
    }

    /// <summary>
    /// Computes the output size for a source and a target height (keeps aspect ratio, even numbers,
    /// width limited to 16:9 of the target height for ultra wide content).
    /// </summary>
    /// <param name="srcWidth">Source width.</param>
    /// <param name="srcHeight">Source height.</param>
    /// <param name="targetHeight">Target height.</param>
    /// <returns>Output width and height.</returns>
    public static (int Width, int Height) ComputeOutputSize(int srcWidth, int srcHeight, int targetHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            return (targetHeight * 16 / 9 / 2 * 2, targetHeight);
        }

        double h = targetHeight;
        double w = srcWidth * h / srcHeight;
        double maxW = targetHeight * 16.0 / 9.0;
        if (w > maxW + 1)
        {
            w = maxW;
            h = srcHeight * w / srcWidth;
        }

        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2.0) * 2);
        return (Even(w), Even(h));
    }

    /// <summary>
    /// Escapes a path for use as filter option value inside single quotes (same rules Jellyfin uses).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Escaped path.</returns>
    public static string EscapeFilterPath(string path)
    {
        // https://ffmpeg.org/ffmpeg-filters.html#Notes-on-filtergraph-escaping
        return path
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", @"'\\\''", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the ffmpeg arguments.
    /// </summary>
    /// <param name="source">Source description.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="encoder">Encoder.</param>
    /// <param name="vaapiDevice">VAAPI device (only used for VAAPI encoders).</param>
    /// <param name="shaderPath">Path of the Anime4K shader bundle.</param>
    /// <param name="targetHeight">Output height.</param>
    /// <param name="startSegment">First segment number.</param>
    /// <param name="outputDirectory">Directory for segments and playlist.</param>
    /// <returns>The argument list.</returns>
    public static List<string> Build(
        SourceInfo source,
        PluginConfiguration config,
        EncoderChoice encoder,
        string? vaapiDevice,
        string shaderPath,
        int targetHeight,
        int startSegment,
        string outputDirectory)
    {
        var inv = CultureInfo.InvariantCulture;
        var segLen = Math.Clamp(config.SegmentLength, 1, 20);
        var start = startSegment * segLen;
        var (outW, outH) = ComputeOutputSize(source.Width, source.Height, targetHeight);
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning", "-y" };

        if (encoder.Family == "vaapi")
        {
            args.Add("-vaapi_device");
            args.Add(string.IsNullOrWhiteSpace(vaapiDevice) ? "/dev/dri/renderD128" : vaapiDevice);
        }

        if (!string.IsNullOrWhiteSpace(config.VulkanDevice))
        {
            args.Add("-init_hw_device");
            args.Add("vulkan=a4kvk:" + config.VulkanDevice.Trim());
            args.Add("-filter_hw_device");
            args.Add("a4kvk");
        }

        if (start > 0)
        {
            args.Add("-ss");
            args.Add(start.ToString(inv));
        }

        args.Add("-i");
        args.Add(source.Path);

        // ---- video filter graph ----
        var swFormat = encoder.Family == "x26x" ? "yuv420p" : "nv12";
        var placebo = string.Format(
            inv,
            "libplacebo=w={0}:h={1}:custom_shader_path='{2}':format={3}:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:upscaler=ewa_lanczos:downscaler=mitchell",
            outW,
            outH,
            EscapeFilterPath(shaderPath),
            swFormat);

        string graph;
        var sub = source.Subtitle;
        if (sub is not null && !sub.IsText && sub.ExternalPath is null)
        {
            // image based subtitles (PGS, DVD): scale the subtitle canvas to the output and overlay it.
            graph = string.Format(
                inv,
                "[0:{0}]{1}[a4kbase];[0:{2}]scale={3}:{4}[a4ksub];[a4kbase][a4ksub]overlay=eof_action=pass:repeatlast=0,format={5}",
                source.VideoStreamIndex,
                placebo,
                sub.StreamIndex,
                outW,
                outH,
                swFormat);
        }
        else
        {
            graph = string.Format(inv, "[0:{0}]{1}", source.VideoStreamIndex, placebo);
            if (sub is not null && sub.IsText)
            {
                var subPath = sub.ExternalPath ?? source.Path;
                graph += ",setpts=PTS+" + start.ToString(inv) + "/TB";
                graph += sub.ExternalPath is null
                    ? string.Format(inv, ",subtitles=f='{0}':si={1}", EscapeFilterPath(subPath), sub.RelativeIndex)
                    : string.Format(inv, ",subtitles=f='{0}'", EscapeFilterPath(subPath));
                graph += ",setpts=PTS-" + start.ToString(inv) + "/TB";
            }
        }

        if (encoder.Family == "vaapi")
        {
            graph += ",format=nv12,hwupload";
        }

        graph += "[a4kout]";

        args.Add("-filter_complex");
        args.Add(graph);
        args.Add("-map");
        args.Add("[a4kout]");

        if (source.AudioStreamIndex is not null)
        {
            args.Add("-map");
            args.Add("0:" + source.AudioStreamIndex.Value.ToString(inv));
        }

        // ---- video encoder ----
        args.Add("-c:v");
        args.Add(encoder.Name);
        var q = Math.Clamp(config.VideoQuality, 0, 51).ToString(inv);
        var maxRate = config.MaxBitrateMbps > 0 ? config.MaxBitrateMbps.ToString(inv) + "M" : null;
        switch (encoder.Family)
        {
            case "x26x":
                args.AddRange(["-preset", string.IsNullOrWhiteSpace(config.EncoderPreset) ? "veryfast" : config.EncoderPreset, "-crf", q]);
                if (!encoder.IsHevc)
                {
                    args.AddRange(["-profile:v", "high"]);
                }

                break;
            case "nvenc":
                args.AddRange(["-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0"]);
                break;
            case "qsv":
                args.AddRange(["-preset", "veryfast", "-global_quality", q, "-look_ahead", "0"]);
                break;
            case "vaapi":
                args.AddRange(["-rc_mode", "CQP", "-qp", q]);
                break;
            case "amf":
                args.AddRange(["-quality", "speed", "-rc", "cqp", "-qp_i", q, "-qp_p", q]);
                break;
            case "videotoolbox":
                args.AddRange(["-b:v", maxRate ?? "25M"]);
                maxRate = null;
                break;
        }

        if (maxRate is not null)
        {
            args.AddRange(["-maxrate", maxRate, "-bufsize", (config.MaxBitrateMbps * 2).ToString(inv) + "M"]);
        }

        if (encoder.IsHevc)
        {
            args.AddRange(["-tag:v", "hvc1"]);
        }

        args.AddRange(["-force_key_frames:0", string.Format(inv, "expr:gte(t,n_forced*{0})", segLen)]);

        // ---- audio ----
        if (source.AudioStreamIndex is not null)
        {
            args.AddRange(
            [
                "-c:a", "aac",
                "-b:a", Math.Clamp(config.AudioBitrate, 64, 640).ToString(inv) + "k",
                "-ac", Math.Clamp(config.AudioChannels, 1, 8).ToString(inv)
            ]);
        }

        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-max_muxing_queue_size", "2048"]);

        // ---- HLS output ----
        var ext = encoder.IsHevc ? "mp4" : "ts";
        args.AddRange(
        [
            "-f", "hls",
            "-hls_time", segLen.ToString(inv),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-start_number", startSegment.ToString(inv),
            "-hls_flags", "temp_file+independent_segments",
            "-hls_segment_type", encoder.IsHevc ? "fmp4" : "mpegts",
        ]);

        if (encoder.IsHevc)
        {
            args.AddRange(["-hls_fmp4_init_filename", "init.mp4"]);
        }

        args.AddRange(
        [
            "-hls_segment_filename", Path.Combine(outputDirectory, "seg%d." + ext),
            "-output_ts_offset", start.ToString(inv),
            Path.Combine(outputDirectory, "stream.m3u8")
        ]);

        return args;
    }
}
