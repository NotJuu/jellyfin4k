using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Anime4K.Transcoding;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Anime4K.Api;

/// <summary>
/// HLS endpoints delivering the Anime4K upscaled stream (server side upscaling).
/// </summary>
[ApiController]
[Authorize]
[Route("Anime4K/Hls")]
public class Anime4KHlsController : ControllerBase
{
    private readonly TranscodeManager _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="Anime4KHlsController"/> class.
    /// </summary>
    /// <param name="manager">Transcode manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="authorizationContext">Authorization context.</param>
    public Anime4KHlsController(
        TranscodeManager manager,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IAuthorizationContext authorizationContext)
    {
        _manager = manager;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _authorizationContext = authorizationContext;
    }

    private static int SegmentLength => Math.Clamp(Plugin.Instance?.Configuration.SegmentLength ?? 4, 1, 20);

    /// <summary>
    /// Master playlist.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="query">Stream parameters.</param>
    /// <returns>Playlist.</returns>
    [HttpGet("{itemId}/master.m3u8")]
    [Produces("application/vnd.apple.mpegurl")]
    public async Task<ActionResult> GetMasterPlaylist([FromRoute, Required] Guid itemId, [FromQuery] HlsQuery query)
    {
        var (request, source, token, error) = await ResolveAsync(itemId, query).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var (w, h) = FfmpegCommandBuilder.ComputeOutputSize(source!.Width, source.Height, request!.Height);
        var encoder = _manager.GetEncoder();
        var videoCodec = encoder.IsHevc ? "hvc1.1.6.L153.B0" : "avc1.640033";
        var codecs = source.AudioStreamIndex is null ? videoCodec : videoCodec + ",mp4a.40.2";
        var bandwidth = Math.Max(4, Plugin.Instance?.Configuration.MaxBitrateMbps ?? 40) * 1_000_000;

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},AVERAGE-BANDWIDTH={bandwidth / 2},RESOLUTION={w}x{h},CODECS=\"{codecs}\"\n");
        sb.Append("main.m3u8?").Append(request.ToQueryString(token)).Append('\n');
        return Playlist(sb.ToString());
    }

    /// <summary>
    /// Media playlist (complete VOD playlist, segments are produced on demand).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="query">Stream parameters.</param>
    /// <returns>Playlist.</returns>
    [HttpGet("{itemId}/main.m3u8")]
    [Produces("application/vnd.apple.mpegurl")]
    public async Task<ActionResult> GetMediaPlaylist([FromRoute, Required] Guid itemId, [FromQuery] HlsQuery query)
    {
        var (request, source, token, error) = await ResolveAsync(itemId, query).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var hevc = _manager.GetEncoder().IsHevc;
        var segLen = SegmentLength;
        var duration = source!.DurationSeconds > 0 ? source.DurationSeconds : 3 * 3600;
        var count = Math.Max(1, (int)Math.Ceiling(duration / segLen - 0.001));
        var qs = request!.ToQueryString(token);
        var ext = hevc ? "mp4" : "ts";

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-VERSION:{(hevc ? 7 : 3)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{segLen + 1}\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-INDEPENDENT-SEGMENTS\n");
        if (hevc)
        {
            sb.Append("#EXT-X-MAP:URI=\"init.mp4?").Append(qs).Append("\"\n");
        }

        for (var i = 0; i < count; i++)
        {
            var len = Math.Min(segLen, duration - (i * (double)segLen));
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{len:0.000000},\n");
            sb.Append(CultureInfo.InvariantCulture, $"{i}.{ext}?").Append(qs).Append('\n');
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return Playlist(sb.ToString());
    }

    /// <summary>
    /// fMP4 init segment (HEVC output only).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="query">Stream parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Init segment.</returns>
    [HttpGet("{itemId}/init.mp4")]
    public async Task<ActionResult> GetInitSegment([FromRoute, Required] Guid itemId, [FromQuery] HlsQuery query, CancellationToken cancellationToken)
    {
        var (request, source, _, error) = await ResolveAsync(itemId, query).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var path = await _manager.GetInitSegmentAsync(request!, source!, cancellationToken).ConfigureAwait(false);
        return path is null ? Problem("Anime4K transcoding failed (see server log).") : PhysicalFile(path, "video/mp4");
    }

    /// <summary>
    /// A media segment.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="segment">Segment number.</param>
    /// <param name="extension">File extension.</param>
    /// <param name="query">Stream parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Segment.</returns>
    [HttpGet("{itemId}/{segment:int}.{extension}")]
    public async Task<ActionResult> GetSegment(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] int segment,
        [FromRoute, Required] string extension,
        [FromQuery] HlsQuery query,
        CancellationToken cancellationToken)
    {
        if (segment < 0)
        {
            return BadRequest();
        }

        var (request, source, _, error) = await ResolveAsync(itemId, query).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var path = await _manager.GetSegmentAsync(request!, source!, segment, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return Problem("Anime4K transcoding failed (see server log).");
        }

        var contentType = extension.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "video/mp2t";
        return PhysicalFile(path, contentType);
    }

    private ContentResult Playlist(string text)
    {
        Response.Headers.CacheControl = "no-cache";
        return Content(text, "application/vnd.apple.mpegurl", Encoding.UTF8);
    }

    private async Task<(TranscodeRequest? Request, SourceInfo? Source, string? Token, ActionResult? Error)> ResolveAsync(Guid itemId, HlsQuery query)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableServerUpscaling)
        {
            return (null, null, null, NotFound("Anime4K server upscaling is disabled."));
        }

        var auth = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.User;
        var item = user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return (null, null, null, NotFound());
        }

        var sources = _mediaSourceManager.GetStaticMediaSources(item, false, user);
        var mediaSource = sources.FirstOrDefault(s => string.Equals(s.Id, query.MediaSourceId, StringComparison.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault();
        if (mediaSource is null || string.IsNullOrEmpty(mediaSource.Path))
        {
            return (null, null, null, NotFound());
        }

        var request = new TranscodeRequest
        {
            ItemId = itemId,
            MediaSourceId = mediaSource.Id,
            AudioStreamIndex = query.AudioStreamIndex,
            SubtitleStreamIndex = query.SubtitleStreamIndex,
            BurnSubtitle = query.BurnSubtitle ?? false,
            Mode = ShaderLibrary.NormalizeMode(query.Mode),
            Quality = ShaderLibrary.NormalizeQuality(query.Quality),
            Height = ShaderLibrary.NormalizeHeight(query.Height),
            PlaySessionId = query.PlaySessionId,
            DeviceId = string.IsNullOrEmpty(query.DeviceId) ? auth.DeviceId : query.DeviceId
        };

        try
        {
            var source = TranscodeManager.CreateSourceInfo(mediaSource, request);
            return (request, source, auth.Token, null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, null, null, BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Query parameters of the HLS endpoints.
    /// </summary>
    public class HlsQuery
    {
        /// <summary>Gets or sets the media source id.</summary>
        public string? MediaSourceId { get; set; }

        /// <summary>Gets or sets the audio stream index.</summary>
        public int? AudioStreamIndex { get; set; }

        /// <summary>Gets or sets the subtitle stream index.</summary>
        public int? SubtitleStreamIndex { get; set; }

        /// <summary>Gets or sets whether subtitles are burned in.</summary>
        public bool? BurnSubtitle { get; set; }

        /// <summary>Gets or sets the mode.</summary>
        public string? Mode { get; set; }

        /// <summary>Gets or sets the quality.</summary>
        public string? Quality { get; set; }

        /// <summary>Gets or sets the output height.</summary>
        public int? Height { get; set; }

        /// <summary>Gets or sets the play session id.</summary>
        public string? PlaySessionId { get; set; }

        /// <summary>Gets or sets the device id.</summary>
        public string? DeviceId { get; set; }
    }
}
