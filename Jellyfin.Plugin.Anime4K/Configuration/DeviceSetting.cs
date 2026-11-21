namespace Jellyfin.Plugin.Anime4K.Configuration;

/// <summary>
/// Anime4K settings of a single client device.
/// </summary>
public class DeviceSetting
{
    /// <summary>
    /// Gets or sets the Jellyfin device id.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a friendly device name (informational).
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client application name (informational).
    /// </summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether playback on this device is upscaled by the server.
    /// </summary>
    public bool ServerUpscaling { get; set; }

    /// <summary>
    /// Gets or sets the Anime4K mode.
    /// </summary>
    public string Mode { get; set; } = "A";

    /// <summary>
    /// Gets or sets the quality preset.
    /// </summary>
    public string Quality { get; set; } = "HQ";

    /// <summary>
    /// Gets or sets the output height (1080, 1440, 2160).
    /// </summary>
    public int TargetHeight { get; set; } = 2160;
}
