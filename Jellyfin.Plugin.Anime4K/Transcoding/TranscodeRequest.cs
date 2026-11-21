using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Anime4K.Transcoding;

/// <summary>
/// Parameters of an Anime4K server transcode.
/// </summary>
public sealed class TranscodeRequest
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the media source id.</summary>
    public string? MediaSourceId { get; set; }

    /// <summary>Gets or sets the audio stream index (absolute stream index, as reported by Jellyfin).</summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>Gets or sets the subtitle stream index to burn in.</summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>Gets or sets a value indicating whether the subtitle stream is burned into the video.</summary>
    public bool BurnSubtitle { get; set; }

    /// <summary>Gets or sets the Anime4K mode.</summary>
    public string Mode { get; set; } = "A";

    /// <summary>Gets or sets the quality preset.</summary>
    public string Quality { get; set; } = "HQ";

    /// <summary>Gets or sets the output height.</summary>
    public int Height { get; set; } = 2160;

    /// <summary>Gets or sets the play session id.</summary>
    public string? PlaySessionId { get; set; }

    /// <summary>Gets or sets the device id.</summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets the key identifying the encoder output (everything that changes the produced segments).
    /// </summary>
    public string OutputKey => string.Join(
        '|',
        ItemId.ToString("N", CultureInfo.InvariantCulture),
        MediaSourceId ?? string.Empty,
        AudioStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "-",
        BurnSubtitle ? SubtitleStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "-" : "-",
        Mode,
        Quality,
        Height.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Gets the key of the playback session (one running encoder per session).
    /// </summary>
    public string SessionKey => string.IsNullOrEmpty(PlaySessionId)
        ? (DeviceId ?? string.Empty) + "|" + ItemId.ToString("N", CultureInfo.InvariantCulture)
        : PlaySessionId!;

    /// <summary>
    /// Builds the query string (without leading '?') used for playlist and segment URLs.
    /// </summary>
    /// <param name="apiKey">Optional access token.</param>
    /// <returns>Query string.</returns>
    public string ToQueryString(string? apiKey)
    {
        var sb = new StringBuilder();
        void Add(string key, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        Add("MediaSourceId", MediaSourceId);
        Add("AudioStreamIndex", AudioStreamIndex?.ToString(CultureInfo.InvariantCulture));
        Add("SubtitleStreamIndex", SubtitleStreamIndex?.ToString(CultureInfo.InvariantCulture));
        Add("BurnSubtitle", BurnSubtitle ? "true" : null);
        Add("Mode", Mode);
        Add("Quality", Quality);
        Add("Height", Height.ToString(CultureInfo.InvariantCulture));
        Add("PlaySessionId", PlaySessionId);
        Add("DeviceId", DeviceId);
        Add("ApiKey", apiKey);
        return sb.ToString();
    }
}
