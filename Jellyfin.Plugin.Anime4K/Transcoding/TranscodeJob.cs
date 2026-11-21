using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anime4K.Transcoding;

/// <summary>
/// A running ffmpeg process producing Anime4K upscaled HLS segments.
/// </summary>
public sealed partial class TranscodeJob : IDisposable
{
    private readonly ILogger _logger;
    private readonly StringBuilder _stderr = new();
    private readonly object _lock = new();
    private Process? _process;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeJob"/> class.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="directory">Output directory.</param>
    /// <param name="startSegment">First segment.</param>
    /// <param name="segmentExtension">Segment file extension.</param>
    /// <param name="logger">Logger.</param>
    public TranscodeJob(TranscodeRequest request, string directory, int startSegment, string segmentExtension, ILogger logger)
    {
        Request = request;
        Directory = directory;
        StartSegment = startSegment;
        SegmentExtension = segmentExtension;
        _logger = logger;
        LastAccess = DateTime.UtcNow;
        LastRequestedSegment = startSegment;
    }

    /// <summary>Gets the request.</summary>
    public TranscodeRequest Request { get; }

    /// <summary>Gets the output directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the first produced segment.</summary>
    public int StartSegment { get; }

    /// <summary>Gets the segment extension (ts or mp4).</summary>
    public string SegmentExtension { get; }

    /// <summary>Gets or sets the last access time.</summary>
    public DateTime LastAccess { get; set; }

    /// <summary>Gets or sets the last segment requested by the player.</summary>
    public int LastRequestedSegment { get; set; }

    /// <summary>Gets the start time.</summary>
    public DateTime Started { get; private set; }

    /// <summary>Gets a value indicating whether ffmpeg has exited.</summary>
    public bool HasExited { get; private set; }

    /// <summary>Gets the ffmpeg exit code.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Gets a value indicating whether ffmpeg is paused (throttled).</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Gets the full command line (for diagnostics).</summary>
    public string CommandLine { get; private set; } = string.Empty;

    /// <summary>Gets the collected stderr output (last few KB).</summary>
    public string ErrorOutput
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Gets the path of the ffmpeg playlist.</summary>
    public string PlaylistPath => Path.Combine(Directory, "stream.m3u8");

    /// <summary>
    /// Gets the path of a segment file.
    /// </summary>
    /// <param name="segment">Segment number.</param>
    /// <returns>The path.</returns>
    public string GetSegmentPath(int segment) => Path.Combine(Directory, "seg" + segment.ToString(CultureInfo.InvariantCulture) + "." + SegmentExtension);

    /// <summary>
    /// Starts ffmpeg.
    /// </summary>
    /// <param name="ffmpegPath">ffmpeg executable.</param>
    /// <param name="args">Arguments.</param>
    public void Start(string ffmpegPath, IReadOnlyList<string> args)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        CommandLine = ffmpegPath + " " + string.Join(' ', QuoteAll(args));
        _logger.LogInformation("Anime4K: starting ffmpeg: {Command}", CommandLine);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 16000)
                {
                    _stderr.Remove(0, _stderr.Length - 12000);
                }
            }
        };
        process.Exited += (_, _) =>
        {
            try
            {
                ExitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // ignore
            }

            HasExited = true;
            if (ExitCode is not null and not 0 && !_disposed)
            {
                _logger.LogError("Anime4K: ffmpeg exited with code {Code}: {Error}", ExitCode, ErrorOutput);
            }
        };

        Started = DateTime.UtcNow;
        process.Start();
        process.BeginErrorReadLine();
        _process = process;
    }

    /// <summary>
    /// Returns whether ffmpeg has finished writing a segment.
    /// </summary>
    /// <param name="segment">Segment number.</param>
    /// <returns>True when complete.</returns>
    public bool IsSegmentComplete(int segment)
    {
        var file = "seg" + segment.ToString(CultureInfo.InvariantCulture) + "." + SegmentExtension;
        try
        {
            if (!File.Exists(PlaylistPath))
            {
                return false;
            }

            foreach (var line in File.ReadLines(PlaylistPath))
            {
                if (line.EndsWith(file, StringComparison.Ordinal) && (line.Length == file.Length || line[line.Length - file.Length - 1] is '/' or '\\'))
                {
                    return File.Exists(GetSegmentPath(segment));
                }
            }
        }
        catch (IOException)
        {
            // playlist is being replaced right now
        }

        return false;
    }

    /// <summary>
    /// Returns the highest segment that is complete (or StartSegment - 1).
    /// </summary>
    /// <returns>Segment number.</returns>
    public int GetHighestCompleteSegment()
    {
        var highest = StartSegment - 1;
        try
        {
            if (!File.Exists(PlaylistPath))
            {
                return highest;
            }

            foreach (var line in File.ReadLines(PlaylistPath))
            {
                if (line.StartsWith("seg", StringComparison.Ordinal) || line.Contains("/seg", StringComparison.Ordinal) || line.Contains("\\seg", StringComparison.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(line.Trim());
                    if (name.StartsWith("seg", StringComparison.Ordinal)
                        && int.TryParse(name.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        highest = Math.Max(highest, n);
                    }
                }
            }
        }
        catch (IOException)
        {
            // ignore
        }

        return highest;
    }

    /// <summary>
    /// Waits until a segment is complete.
    /// </summary>
    /// <param name="segment">Segment number.</param>
    /// <param name="timeout">Timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the segment is available.</returns>
    public async Task<bool> WaitForSegmentAsync(int segment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsSegmentComplete(segment))
            {
                return true;
            }

            if (HasExited)
            {
                // last chance: ffmpeg might have finished between the checks
                return IsSegmentComplete(segment);
            }

            Resume();

            LastAccess = DateTime.UtcNow;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Pauses ffmpeg (SIGSTOP) when it is far ahead of the player. Only supported on Linux and macOS.
    /// </summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused || HasExited || _disposed || _process is null)
            {
                return;
            }

            if (SendSignal(_process.Id, OperatingSystem.IsMacOS() ? 17 : 19))
            {
                IsPaused = true;
            }
        }
    }

    /// <summary>
    /// Resumes a paused ffmpeg process (SIGCONT).
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (!IsPaused || _process is null)
            {
                return;
            }

            SendSignal(_process.Id, OperatingSystem.IsMacOS() ? 19 : 18);
            IsPaused = false;
        }
    }

    /// <summary>
    /// Deletes segments that are well behind the playback position.
    /// </summary>
    /// <param name="keepFrom">Keep segments from this number on.</param>
    public void DeleteOldSegments(int keepFrom)
    {
        for (var i = keepFrom - 1; i >= StartSegment && i >= keepFrom - 30; i--)
        {
            var path = GetSegmentPath(i);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // still being served
            }
            catch (UnauthorizedAccessException)
            {
                // ignore
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            if (_process is not null && !_process.HasExited)
            {
                if (IsPaused)
                {
                    SendSignal(_process.Id, OperatingSystem.IsMacOS() ? 19 : 18);
                    IsPaused = false;
                }

                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "Anime4K: error killing ffmpeg");
        }

        _process?.Dispose();

        // give the OS a moment to release file handles, then remove the directory
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    if (System.IO.Directory.Exists(Directory))
                    {
                        System.IO.Directory.Delete(Directory, true);
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        });
    }

    private static bool SendSignal(int pid, int signal)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return false;
        }

        try
        {
            return NativeKill(pid, signal) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int NativeKill(int pid, int sig);

    private static IEnumerable<string> QuoteAll(IEnumerable<string> args)
    {
        foreach (var a in args)
        {
            yield return a.Length == 0 || a.IndexOfAny([' ', '"', '\'', ';', '[', '(']) >= 0 ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a;
        }
    }
}
