using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Anime4K.Middleware;

/// <summary>
/// Adds the <see cref="Anime4KMiddleware"/> at the very beginning of the ASP.NET Core pipeline.
/// </summary>
public sealed class Anime4KStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<Anime4KMiddleware>();
            next(app);
        };
    }
}
