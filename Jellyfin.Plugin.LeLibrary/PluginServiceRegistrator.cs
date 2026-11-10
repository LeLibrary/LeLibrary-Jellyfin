using System;
using System.Net.Http;
using Jellyfin.Plugin.LeLibrary.Playback;
using Jellyfin.Plugin.LeLibrary.Providers;
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
        _ = applicationHost;
        serviceCollection.AddSingleton<DebridClientFactory>();
        serviceCollection.AddSingleton<IDebridClient>(sp =>
        {
            var configuration = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("the LeLibrary plugin is not loaded yet");
            return sp.GetRequiredService<DebridClientFactory>().Create(configuration);
        });
        serviceCollection.AddSingleton(sp => new LinkCache(() =>
        {
            var configuration = Plugin.Instance?.Configuration;
            return configuration is null
                ? throw new InvalidOperationException("the LeLibrary plugin is not loaded yet")
                : sp.GetRequiredService<DebridClientFactory>().Create(configuration);
        }));
        serviceCollection.AddSingleton(sp => new PlaybackProxy(
            () =>
            {
                var configuration = Plugin.Instance?.Configuration;
                return configuration is null
                    ? throw new InvalidOperationException("the LeLibrary plugin is not loaded yet")
                    : sp.GetRequiredService<DebridClientFactory>().Create(configuration);
            },
            sp.GetRequiredService<LinkCache>(),
            // Streaming client: no timeout, no buffering. The default client's
            // 100s limit would cut long downloads.
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            }));
        serviceCollection.AddSingleton<Jellyfin.Plugin.LeLibrary.Virtual.VirtualLibrary>();
        serviceCollection.AddSingleton<LeLibrarySync>();
        serviceCollection.AddSingleton(sp => new SyncTask(
            sp.GetRequiredService<LeLibrarySync>(),
            sp.GetRequiredService<IServerApplicationHost>(),
            sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SyncTask>>()));
    }
}
