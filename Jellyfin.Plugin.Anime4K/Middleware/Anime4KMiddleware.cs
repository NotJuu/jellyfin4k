using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Anime4K.Configuration;
using Jellyfin.Plugin.Anime4K.Transcoding;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anime4K.Middleware;

/// <summary>
/// Middleware that
/// <list type="bullet">
/// <item>injects the Anime4K client script into the Jellyfin web client (index.html),</item>
/// <item>redirects playback of devices with server upscaling enabled to the Anime4K HLS stream
/// by rewriting the PlaybackInfo response (works with every Jellyfin client, also native TV apps),</item>
/// <item>stops Anime4K transcodes when a client stops its active encodings.</item>
/// </list>
/// </summary>
public sealed partial class Anime4KMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<Anime4KMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Anime4KMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next delegate.</param>
    /// <param name="logger">Logger.</param>
    public Anime4KMiddleware(RequestDelegate next, ILogger<Anime4KMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Processes a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>Task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        try
        {
            if (HttpMethods.IsGet(method) && Config.InjectClientScript && IsWebIndex(path))
            {
                await InjectScriptAsync(context).ConfigureAwait(false);
                return;
            }

            if ((HttpMethods.IsPost(method) || HttpMethods.IsGet(method)) && Config.EnableServerUpscaling)
            {
                var match = PlaybackInfoRegex().Match(path);
                if (match.Success && Guid.TryParse(match.Groups[1].Value, out var itemId))
                {
                    await RewritePlaybackInfoAsync(context, itemId).ConfigureAwait(false);
                    return;
                }
            }

            if (HttpMethods.IsDelete(method) && path.EndsWith("/Videos/ActiveEncodings", StringComparison.OrdinalIgnoreCase))
            {
                var manager = context.RequestServices.GetService<TranscodeManager>();
                manager?.StopJobs(GetQuery(context, "playSessionId"), GetQuery(context, "deviceId"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Anime4K middleware error for {Path}", path);
            if (context.Response.HasStarted)
            {
                throw;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsWebIndex(string path)
    {
        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetQuery(HttpContext context, string key)
    {
        var value = context.Request.Query[key].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private async Task<(int Status, string? ContentType, byte[] Body)> CaptureAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        return (context.Response.StatusCode, context.Response.ContentType, buffer.ToArray());
    }

    private static async Task WriteAsync(HttpContext context, byte[] body)
    {
        context.Response.ContentLength = body.Length;
        if (body.Length > 0)
        {
            await context.Response.Body.WriteAsync(body).ConfigureAwait(false);
        }
    }

    private async Task InjectScriptAsync(HttpContext context)
    {
        // we need the plain, uncompressed and complete body
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Request.Headers.Remove("Range");

        var (status, contentType, body) = await CaptureAsync(context).ConfigureAwait(false);
        if (status == StatusCodes.Status200OK
            && contentType is not null
            && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var html = Encoding.UTF8.GetString(body);
            if (!html.Contains("Anime4K/ClientScript", StringComparison.Ordinal))
            {
                var version = Uri.EscapeDataString(Plugin.Instance?.ClientVersion ?? "1");
                var tag = $"<script defer src=\"../Anime4K/ClientScript?v={version}\"></script>";
                var idx = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                html = idx >= 0 ? html.Insert(idx, tag) : html + tag;
                body = Encoding.UTF8.GetBytes(html);
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl = "no-cache";
            }
        }

        await WriteAsync(context, body).ConfigureAwait(false);
    }

    private async Task RewritePlaybackInfoAsync(HttpContext context, Guid itemId)
    {
        var authContext = context.RequestServices.GetService<IAuthorizationContext>();
        if (authContext is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var auth = await authContext.GetAuthorizationInfo(context.Request).ConfigureAwait(false);
        var config = Config;
        var device = config.FindDevice(auth.DeviceId);
        if (!auth.IsAuthenticated || device is null || !device.ServerUpscaling)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (config.OnlyAnime && !LooksLikeAnime(context, itemId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // read the requested stream indexes (query and/or JSON body)
        int? audioIndex = ParseInt(GetQuery(context, "AudioStreamIndex"));
        int? subtitleIndex = ParseInt(GetQuery(context, "SubtitleStreamIndex"));
        string? requestedSource = GetQuery(context, "MediaSourceId");
        if (HttpMethods.IsPost(context.Request.Method))
        {
            context.Request.EnableBuffering();
            try
            {
                using var doc = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.NameEquals("AudioStreamIndex") || p.Name.Equals("AudioStreamIndex", StringComparison.OrdinalIgnoreCase))
                        {
                            audioIndex ??= GetInt(p.Value);
                        }
                        else if (p.Name.Equals("SubtitleStreamIndex", StringComparison.OrdinalIgnoreCase))
                        {
                            subtitleIndex ??= GetInt(p.Value);
                        }
                        else if (p.Name.Equals("MediaSourceId", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        {
                            requestedSource ??= p.Value.GetString();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // no / invalid body
            }
            finally
            {
                context.Request.Body.Position = 0;
            }
        }

        context.Request.Headers.Remove("Accept-Encoding");
        var (status, contentType, body) = await CaptureAsync(context).ConfigureAwait(false);
        if (status == StatusCodes.Status200OK && contentType is not null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var rewritten = RewritePlaybackInfoJson(body, itemId, device, auth.DeviceId, auth.Token, audioIndex, subtitleIndex, requestedSource);
                if (rewritten is not null)
                {
                    body = rewritten;
                    _logger.LogInformation("Anime4K: playback of {Item} on device {Device} is upscaled by the server", itemId, auth.DeviceId);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Anime4K: could not rewrite PlaybackInfo");
            }
        }

        await WriteAsync(context, body).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites a PlaybackInfo response so that video media sources are played through the Anime4K HLS endpoint.
    /// </summary>
    /// <param name="body">Original JSON.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="device">Device settings.</param>
    /// <param name="deviceId">Device id.</param>
    /// <param name="token">Access token.</param>
    /// <param name="audioIndex">Requested audio stream.</param>
    /// <param name="subtitleIndex">Requested subtitle stream.</param>
    /// <param name="requestedSource">Requested media source id.</param>
    /// <returns>New JSON or null when nothing was changed.</returns>
    public static byte[]? RewritePlaybackInfoJson(
        byte[] body,
        Guid itemId,
        DeviceSetting device,
        string? deviceId,
        string? token,
        int? audioIndex,
        int? subtitleIndex,
        string? requestedSource)
    {
        var root = JsonNode.Parse(body) as JsonObject;
        if (root?["MediaSources"] is not JsonArray sources)
        {
            return null;
        }

        var playSessionId = root["PlaySessionId"]?.GetValue<string>();
        var changed = false;
        foreach (var node in sources)
        {
            if (node is not JsonObject ms)
            {
                continue;
            }

            var protocol = ms["Protocol"]?.ToString();
            var infinite = ms["IsInfiniteStream"]?.GetValue<bool>() ?? false;
            if (!string.Equals(protocol, "File", StringComparison.OrdinalIgnoreCase) || infinite)
            {
                continue;
            }

            if (ms["MediaStreams"] is not JsonArray streams
                || !streams.OfType<JsonObject>().Any(s => string.Equals(s["Type"]?.ToString(), "Video", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var msId = ms["Id"]?.ToString();
            var isRequested = string.IsNullOrEmpty(requestedSource) || string.Equals(requestedSource, msId, StringComparison.OrdinalIgnoreCase);
            var audio = (isRequested ? audioIndex : null) ?? GetInt(ms["DefaultAudioStreamIndex"]);
            var sub = (isRequested ? subtitleIndex : null) ?? GetInt(ms["DefaultSubtitleStreamIndex"]);

            var burn = false;
            if (sub is >= 0)
            {
                var subStream = streams.OfType<JsonObject>().FirstOrDefault(s =>
                    string.Equals(s["Type"]?.ToString(), "Subtitle", StringComparison.OrdinalIgnoreCase)
                    && GetInt(s["Index"]) == sub);
                if (subStream is not null)
                {
                    var delivery = subStream["DeliveryMethod"]?.ToString();
                    var isText = subStream["IsTextSubtitleStream"]?.GetValue<bool>() ?? false;
                    var isExternal = subStream["IsExternal"]?.GetValue<bool>() ?? false;

                    // text subtitles that the client can render itself stay external (sharper, switchable);
                    // everything else is burned into the upscaled picture.
                    burn = !(string.Equals(delivery, "External", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(delivery, "Hls", StringComparison.OrdinalIgnoreCase))
                           && (isText || !isExternal);
                    if (burn)
                    {
                        subStream["DeliveryMethod"] = "Encode";
                        subStream.Remove("DeliveryUrl");
                    }
                }
            }

            var request = new TranscodeRequest
            {
                ItemId = itemId,
                MediaSourceId = msId,
                AudioStreamIndex = audio,
                SubtitleStreamIndex = sub is >= 0 ? sub : null,
                BurnSubtitle = burn,
                Mode = ShaderLibrary.NormalizeMode(device.Mode),
                Quality = ShaderLibrary.NormalizeQuality(device.Quality),
                Height = ShaderLibrary.NormalizeHeight(device.TargetHeight),
                PlaySessionId = playSessionId,
                DeviceId = deviceId
            };

            ms["SupportsDirectPlay"] = false;
            ms["SupportsDirectStream"] = false;
            ms["SupportsTranscoding"] = true;
            ms["TranscodingUrl"] = "/Anime4K/Hls/" + itemId.ToString("N", CultureInfo.InvariantCulture) + "/master.m3u8?" + request.ToQueryString(token);
            ms["TranscodingSubProtocol"] = "hls";
            ms["TranscodingContainer"] = "ts";
            ms.Remove("TranscodeReasons");
            ms["TranscodeReasons"] = new JsonArray();
            changed = true;
        }

        return changed ? Encoding.UTF8.GetBytes(root.ToJsonString()) : null;
    }

    private static bool LooksLikeAnime(HttpContext context, Guid itemId)
    {
        var library = context.RequestServices.GetService<ILibraryManager>();
        var item = library?.GetItemById(itemId);
        if (item is null)
        {
            return true;
        }

        static bool Check(BaseItem? i) =>
            i is not null
            && ((i.Genres?.Any(g => g.Contains("anime", StringComparison.OrdinalIgnoreCase) || g.Contains("animation", StringComparison.OrdinalIgnoreCase)) ?? false)
                || (i.Path?.Contains("anime", StringComparison.OrdinalIgnoreCase) ?? false));

        return Check(item) || (item is Episode ep && Check(ep.Series)) || Check(item.GetParent());
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? GetInt(JsonElement e)
        => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    private static int? GetInt(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i))
            {
                return i;
            }

            if (v.TryGetValue<long>(out var l))
            {
                return (int)l;
            }

            if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var j))
            {
                return j;
            }
        }

        return null;
    }

    [GeneratedRegex(@"/Items/([0-9a-fA-F\-]{32,36})/PlaybackInfo$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaybackInfoRegex();
}
