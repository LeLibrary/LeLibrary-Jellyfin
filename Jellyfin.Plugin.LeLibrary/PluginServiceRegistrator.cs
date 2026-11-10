using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LeLibrary;

/// <summary>
/// Registers the plugin's services with Jellyfin's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Services are added task by task: the plan client and provider client
        // first, then the sync task, then the resolver. applicationHost is the
        // source of the server URL the .strm files point at.
        _ = serviceCollection;
        _ = applicationHost;
    }
}
