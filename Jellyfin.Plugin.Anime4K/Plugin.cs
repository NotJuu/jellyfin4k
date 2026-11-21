using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Anime4K.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Anime4K;

/// <summary>
/// Anime4K plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Anime4K";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("2c1076de-f14e-4f3b-9f09-55168a31d9fe");

    /// <inheritdoc />
    public override string Description =>
        "Echtzeit-Anime-Upscaling mit Anime4K – im Browser (WebGL) oder auf dem Server (FFmpeg/libplacebo) für Fernseher.";

    /// <summary>
    /// Gets a version string used for cache busting of the client script.
    /// </summary>
    public string ClientVersion => Version.ToString() + "-" + GetType().Assembly.ManifestModule.ModuleVersionId.ToString("N", CultureInfo.InvariantCulture)[..8];

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "Anime4K",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "auto_awesome",
                DisplayName = "Anime4K"
            }
        ];
    }
}
