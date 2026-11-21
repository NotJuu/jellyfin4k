using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Anime4K.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anime4K.Transcoding;

/// <summary>
/// Manages Anime4K transcoding jobs (one ffmpeg process per playback session).
/// </summary>
public sealed class TranscodeManager : IDisposable
{
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(90);
    private readonly Dictionary<string, TranscodeJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<TranscodeManager> _logger;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ISessionManager _sessionManager;
    private readonly Timer _cleanupTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeManager"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="configurationManager">Configuration manager.</param>
    /// <param name="mediaEncoder">Media encoder.</param>
    /// <param name="sessionManager">Session manager.</param>
    public TranscodeManager(
        ILogger<TranscodeManager> logger,
        IServerConfigurationManager configurationManager,
        IMediaEncoder mediaEncoder,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _configurationManager = configurationManager;
        _mediaEncoder = mediaEncoder;
        _sessionManager = sessionManager;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Gets the root directory for all Anime4K transcodes.
    /// </summary>
    public string RootDirectory => Path.Combine(_configurationManager.GetTranscodePath(), "anime4k");

    /// <summary>
    /// Gets the directory that holds generated shader bundles.
    /// </summary>
    public string ShaderDirectory => Path.Combine(_configurationManager.ApplicationPaths.CachePath, "anime4k-shaders");

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Returns the ffmpeg binary to use.
    /// </summary>
    /// <returns>Path to ffmpeg.</returns>
    public string GetFfmpegPath()
    {
        var configured = Config.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return string.IsNullOrEmpty(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath;
    }

    /// <summary>
    /// Returns the encoder that will be used.
    /// </summary>
    /// <returns>Encoder choice.</returns>
    public EncoderChoice GetEncoder()
    {
        var options = _configurationManager.GetEncodingOptions();
        return FfmpegCommandBuilder.ResolveEncoder(Config.Encoder, options.HardwareAccelerationType.ToString(), options.EnableHardwareEncoding);
    }

    /// <summary>
    /// Builds a <see cref="SourceInfo"/> from a Jellyfin media source.
    /// </summary>
    /// <param name="mediaSource">Media source.</param>
    /// <param name="request">Request.</param>
    /// <returns>Source info.</returns>
    public static SourceInfo CreateSourceInfo(MediaSourceInfo mediaSource, TranscodeRequest request)
    {
        var streams = mediaSource.MediaStreams ?? new List<MediaStream>();
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal)
            ?? throw new InvalidOperationException("Media source has no video stream.");

        MediaStream? audio = null;
        if (request.AudioStreamIndex is int ai && ai >= 0)
        {
            audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == ai && !s.IsExternal);
        }

        audio ??= streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == mediaSource.DefaultAudioStreamIndex && !s.IsExternal)
            ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && !s.IsExternal);

        SubtitleInfo? subtitle = null;
        if (request.BurnSubtitle && request.SubtitleStreamIndex is int si && si >= 0)
        {
            var sub = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == si);
            if (sub is not null)
            {
                var external = sub.IsExternal && !string.IsNullOrEmpty(sub.Path) ? sub.Path : null;
                if (sub.IsExternal && external is null)
                {
                    sub = null;
                }
                else if (!sub.IsTextSubtitleStream && external is not null)
                {
                    // external image subtitles (e.g. .sup) are not supported for burn-in
                    sub = null;
                }

                if (sub is not null)
                {
                    subtitle = new SubtitleInfo
                    {
                        StreamIndex = sub.Index,
                        IsText = sub.IsTextSubtitleStream,
                        ExternalPath = external,
                        RelativeIndex = streams.Count(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && s.Index < sub.Index)
                    };
                }
            }
        }

        return new SourceInfo
        {
            Path = mediaSource.Path,
            VideoStreamIndex = video.Index,
            Width = video.Width ?? 0,
            Height = video.Height ?? 0,
            AudioStreamIndex = audio?.Index,
            DurationSeconds = (mediaSource.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond,
            Subtitle = subtitle
        };
    }

    /// <summary>
    /// Returns a completed segment, starting or restarting ffmpeg if required.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <param name="source">Source.</param>
    /// <param name="segment">Segment number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The segment path or null on failure.</returns>
    public async Task<string?> GetSegmentAsync(TranscodeRequest request, SourceInfo source, int segment, CancellationToken cancellationToken)
    {
        TranscodeJob job;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _jobs.TryGetValue(request.SessionKey, out var existing);
            if (existing is not null && existing.Request.OutputKey != request.OutputKey)
            {
                RemoveJobLocked(existing);
                existing = null;
            }

            var reuse = false;
            if (existing is not null && segment >= existing.StartSegment)
            {
                if (existing.IsSegmentComplete(segment))
                {
                    reuse = true;
                }
                else if (!existing.HasExited)
                {
                    // segment will be produced soon by the running encoder?
                    var highest = existing.GetHighestCompleteSegment();
                    reuse = segment <= highest + 3;
                    if (reuse)
                    {
                        existing.Resume();
                    }
                }
            }

            if (reuse)
            {
                job = existing!;
            }
            else
            {
                if (existing is not null)
                {
                    RemoveJobLocked(existing);
                }

                job = StartJobLocked(request, source, segment);
            }

            job.LastAccess = DateTime.UtcNow;
            job.LastRequestedSegment = segment;
        }
        finally
        {
            _lock.Release();
        }

        var ok = await job.WaitForSegmentAsync(segment, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            _logger.LogWarning(
                "Anime4K: segment {Segment} not available (ffmpeg exited: {Exited}, code {Code}). {Error}",
                segment,
                job.HasExited,
                job.ExitCode,
                job.ErrorOutput);
            return null;
        }

        job.DeleteOldSegments(segment - 6);
        return job.GetSegmentPath(segment);
    }

    /// <summary>
    /// Returns the init segment (fMP4 / HEVC only).
    /// </summary>
    /// <param name="request">Request.</param>
    /// <param name="source">Source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path of init.mp4 or null.</returns>
    public async Task<string?> GetInitSegmentAsync(TranscodeRequest request, SourceInfo source, CancellationToken cancellationToken)
    {
        TranscodeJob? job;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _jobs.TryGetValue(request.SessionKey, out job);
            if (job is null || job.Request.OutputKey != request.OutputKey)
            {
                if (job is not null)
                {
                    RemoveJobLocked(job);
                }

                job = StartJobLocked(request, source, 0);
            }

            job.LastAccess = DateTime.UtcNow;
        }
        finally
        {
            _lock.Release();
        }

        var path = Path.Combine(job.Directory, "init.mp4");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            // init.mp4 is complete once the first segment is listed
            if (File.Exists(path) && job.GetHighestCompleteSegment() >= job.StartSegment)
            {
                return path;
            }

            if (job.HasExited)
            {
                return File.Exists(path) ? path : null;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Stops all jobs of a play session.
    /// </summary>
    /// <param name="playSessionId">Play session id.</param>
    /// <param name="deviceId">Device id (used when no play session id is given).</param>
    public void StopJobs(string? playSessionId, string? deviceId)
    {
        _lock.Wait();
        try
        {
            foreach (var job in _jobs.Values.ToList())
            {
                if ((!string.IsNullOrEmpty(playSessionId) && string.Equals(job.Request.PlaySessionId, playSessionId, StringComparison.Ordinal))
                    || (string.IsNullOrEmpty(playSessionId) && !string.IsNullOrEmpty(deviceId) && string.Equals(job.Request.DeviceId, deviceId, StringComparison.Ordinal)))
                {
                    RemoveJobLocked(job);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns information about running jobs.
    /// </summary>
    /// <returns>Job list.</returns>
    public IReadOnlyList<JobStatus> GetJobs()
    {
        _lock.Wait();
        try
        {
            return _jobs.Values.Select(j => new JobStatus(
                j.Request.ItemId,
                j.Request.DeviceId,
                j.Request.PlaySessionId,
                j.Request.Mode,
                j.Request.Quality,
                j.Request.Height,
                j.StartSegment,
                j.GetHighestCompleteSegment(),
                j.LastRequestedSegment,
                j.HasExited,
                j.IsPaused,
                j.ExitCode,
                j.Started,
                j.CommandLine,
                j.ErrorOutput)).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _cleanupTimer.Dispose();
        _lock.Wait();
        try
        {
            foreach (var job in _jobs.Values)
            {
                job.Dispose();
            }

            _jobs.Clear();
        }
        finally
        {
            _lock.Release();
        }

        _lock.Dispose();
    }

    private TranscodeJob StartJobLocked(TranscodeRequest request, SourceInfo source, int segment)
    {
        var config = Config;

        // enforce the concurrency limit by stopping the least recently used job
        var max = Math.Max(1, config.MaxConcurrentJobs);
        while (_jobs.Count >= max)
        {
            var oldest = _jobs.Values.OrderBy(j => j.LastAccess).First();
            _logger.LogInformation("Anime4K: job limit reached, stopping job for device {Device}", oldest.Request.DeviceId);
            RemoveJobLocked(oldest);
        }

        var encoder = GetEncoder();
        var shader = ShaderLibrary.WriteBundle(ShaderDirectory, request.Mode, request.Quality);
        var dirName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.SessionKey + "|" + request.OutputKey)))[..16].ToLowerInvariant()
            + "_" + segment.ToString(CultureInfo.InvariantCulture);
        var dir = Path.Combine(RootDirectory, dirName);
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                dir += "_" + Guid.NewGuid().ToString("N")[..6];
            }
        }

        var vaapi = string.IsNullOrWhiteSpace(config.VaapiDevice) ? _configurationManager.GetEncodingOptions().VaapiDevice : config.VaapiDevice;
        var args = FfmpegCommandBuilder.Build(source, config, encoder, vaapi, shader, request.Height, segment, dir);
        var job = new TranscodeJob(request, dir, segment, encoder.IsHevc ? "mp4" : "ts", _logger);
        job.Start(GetFfmpegPath(), args);
        _jobs[request.SessionKey] = job;
        return job;
    }

    private void RemoveJobLocked(TranscodeJob job)
    {
        if (_jobs.TryGetValue(job.Request.SessionKey, out var current) && ReferenceEquals(current, job))
        {
            _jobs.Remove(job.Request.SessionKey);
        }

        job.Dispose();
    }

    private void Cleanup()
    {
        if (!_lock.Wait(0))
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var segLen = Math.Clamp(Config.SegmentLength, 1, 20);
            var pauseAhead = Math.Max(4, 90 / segLen); // ~90 s buffer
            var resumeAhead = Math.Max(2, 45 / segLen);
            foreach (var job in _jobs.Values.ToList())
            {
                if (now - job.LastAccess > _idleTimeout)
                {
                    _logger.LogInformation("Anime4K: stopping idle job for device {Device}", job.Request.DeviceId);
                    RemoveJobLocked(job);
                    continue;
                }

                // throttling: keep ffmpeg roughly 45-90 seconds ahead of the player
                var ahead = job.GetHighestCompleteSegment() - job.LastRequestedSegment;
                if (!job.IsPaused && ahead > pauseAhead)
                {
                    job.Pause();
                }
                else if (job.IsPaused && ahead < resumeAhead)
                {
                    job.Resume();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anime4K: cleanup failed");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void OnPlaybackStopped(object? sender, MediaBrowser.Controller.Library.PlaybackStopEventArgs e)
    {
        try
        {
            StopJobs(e.PlaySessionId, e.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anime4K: failed to stop jobs");
        }
    }

    /// <summary>
    /// Status of a job.
    /// </summary>
    /// <param name="ItemId">Item id.</param>
    /// <param name="DeviceId">Device id.</param>
    /// <param name="PlaySessionId">Play session id.</param>
    /// <param name="Mode">Mode.</param>
    /// <param name="Quality">Quality.</param>
    /// <param name="Height">Output height.</param>
    /// <param name="StartSegment">First segment.</param>
    /// <param name="HighestSegment">Highest complete segment.</param>
    /// <param name="RequestedSegment">Last requested segment.</param>
    /// <param name="Exited">Whether ffmpeg exited.</param>
    /// <param name="Paused">Whether ffmpeg is paused because it is far enough ahead.</param>
    /// <param name="ExitCode">Exit code.</param>
    /// <param name="Started">Start time.</param>
    /// <param name="CommandLine">Command line.</param>
    /// <param name="Log">ffmpeg log.</param>
    public sealed record JobStatus(
        Guid ItemId,
        string? DeviceId,
        string? PlaySessionId,
        string Mode,
        string Quality,
        int Height,
        int StartSegment,
        int HighestSegment,
        int RequestedSegment,
        bool Exited,
        bool Paused,
        int? ExitCode,
        DateTime Started,
        string CommandLine,
        string Log);
}
