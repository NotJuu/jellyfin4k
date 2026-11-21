using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Anime4K.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the client script is injected into the Jellyfin web client.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether server side upscaling (for TVs and weak devices) is enabled.
    /// </summary>
    public bool EnableServerUpscaling { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether non-admin users may switch server upscaling on for their own devices
    /// from the player UI.
    /// </summary>
    public bool AllowUsersServerUpscaling { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional ffmpeg path. Empty = use the ffmpeg configured in Jellyfin.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video encoder. "auto" derives it from Jellyfin's hardware acceleration settings.
    /// </summary>
    public string Encoder { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the quality value (CRF / CQ / QP depending on encoder). Lower = better.
    /// </summary>
    public int VideoQuality { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum video bitrate in Mbit/s (0 = unlimited, quality based only).
    /// </summary>
    public int MaxBitrateMbps { get; set; } = 40;

    /// <summary>
    /// Gets or sets the encoder speed preset for x264/x265 (ultrafast ... medium).
    /// </summary>
    public string EncoderPreset { get; set; } = "veryfast";

    /// <summary>
    /// Gets or sets the HLS segment length in seconds.
    /// </summary>
    public int SegmentLength { get; set; } = 4;

    /// <summary>
    /// Gets or sets the AAC audio bitrate in kbit/s.
    /// </summary>
    public int AudioBitrate { get; set; } = 192;

    /// <summary>
    /// Gets or sets the number of audio channels (2 = stereo, 6 = 5.1).
    /// </summary>
    public int AudioChannels { get; set; } = 2;

    /// <summary>
    /// Gets or sets the Vulkan device used by libplacebo (index or name). Empty = automatic.
    /// </summary>
    public string VulkanDevice { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the VAAPI render node used for VAAPI encoding. Empty = Jellyfin setting or /dev/dri/renderD128.
    /// </summary>
    public string VaapiDevice { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of simultaneous Anime4K transcodes.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>
    /// Gets or sets the default Anime4K mode (A, B, C, AA, BB, CA).
    /// </summary>
    public string DefaultMode { get; set; } = "A";

    /// <summary>
    /// Gets or sets the default quality preset (Fast, HQ, Ultra).
    /// </summary>
    public string DefaultQuality { get; set; } = "HQ";

    /// <summary>
    /// Gets or sets the default output height for server upscaling.
    /// </summary>
    public int DefaultTargetHeight { get; set; } = 2160;

    /// <summary>
    /// Gets or sets a value indicating whether server upscaling should only be applied to items whose
    /// path or genres look like anime.
    /// </summary>
    public bool OnlyAnime { get; set; }

    /// <summary>
    /// Gets or sets per-device settings.
    /// </summary>
    public List<DeviceSetting> Devices { get; set; } = new();

    /// <summary>
    /// Finds the device setting for the given device id.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>The setting or null.</returns>
    public DeviceSetting? FindDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        return Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
    }
}
