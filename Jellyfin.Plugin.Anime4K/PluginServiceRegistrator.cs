using Jellyfin.Plugin.Anime4K.Middleware;
using Jellyfin.Plugin.Anime4K.Transcoding;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Anime4K;

/// <summary>
/// Registers the plugin services.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TranscodeManager>();
        serviceCollection.AddTransient<IStartupFilter, Anime4KStartupFilter>();
    }
}
