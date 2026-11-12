using System;
using System.Net.Http;
using Jellyfin.Plugin.Standalone.AllDebrid.Playback;
using Jellyfin.Plugin.Standalone.AllDebrid.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Standalone.AllDebrid;

/// <summary>
/// Registers the plugin's services with Jellyfin's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;
        serviceCollection.AddSingleton(sp => new DebridClientFactory(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DebridClientFactory>>()));
        serviceCollection.AddSingleton<IDebridClient>(sp =>
        {
            var configuration = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("the Standalone plugin is not loaded yet");
            return sp.GetRequiredService<DebridClientFactory>().Create(configuration)
                ?? throw new InvalidOperationException(DebridClientFactory.UnsupportedMessage(configuration.Provider));
        });
        serviceCollection.AddSingleton(sp => new LinkCache(() =>
        {
            var configuration = Plugin.Instance?.Configuration;
            if (configuration is null)
            {
                throw new InvalidOperationException("the Standalone plugin is not loaded yet");
            }

            return sp.GetRequiredService<DebridClientFactory>().Create(configuration)
                ?? throw new InvalidOperationException(DebridClientFactory.UnsupportedMessage(configuration.Provider));
        }, () => Plugin.Instance?.Configuration.DebridApiKey ?? string.Empty));
        serviceCollection.AddSingleton(sp => new PlaybackProxy(
            () =>
            {
                var configuration = Plugin.Instance?.Configuration;
                if (configuration is null)
                {
                    throw new InvalidOperationException("the Standalone plugin is not loaded yet");
                }

                return sp.GetRequiredService<DebridClientFactory>().Create(configuration)
                    ?? throw new InvalidOperationException(DebridClientFactory.UnsupportedMessage(configuration.Provider));
            },
            sp.GetRequiredService<LinkCache>(),
            // Streaming client: no timeout, no buffering. The default client's
            // 100s limit would cut long downloads.
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            }));
        serviceCollection.AddSingleton<Jellyfin.Plugin.Standalone.AllDebrid.Virtual.ILibraryWriter, Jellyfin.Plugin.Standalone.AllDebrid.Virtual.JellyfinLibraryWriter>();
        serviceCollection.AddSingleton<Jellyfin.Plugin.Standalone.AllDebrid.Virtual.NativeMetadataWorker>();
        serviceCollection.AddSingleton<Jellyfin.Plugin.Standalone.AllDebrid.Virtual.VirtualLibrary>();
        // Jellyfin builds the metadata provider itself (providers are found by
        // type, not by registration) and resolves its constructor arguments
        // from this container, so the holder has to be here.
        serviceCollection.AddSingleton<StandaloneSync>();
        serviceCollection.AddSingleton(sp => new SyncTask(
            sp.GetRequiredService<StandaloneSync>(),
            sp.GetRequiredService<IServerApplicationHost>(),
            sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SyncTask>>()));
    }
}
