using System;
using System.Net.Http;
using Jellyfin.Plugin.LeLibrary.Configuration;
using Jellyfin.Plugin.LeLibrary.Providers;

namespace Jellyfin.Plugin.LeLibrary;

/// <summary>
/// Builds the debrid client for the current plugin settings. One shared HTTP
/// client, so a sync and a play re-use connections instead of handshaking.
/// </summary>
public sealed class DebridClientFactory : IDisposable
{
    private readonly HttpClient _api;

    /// <summary>Initializes a new instance of the <see cref="DebridClientFactory"/> class.</summary>
    public DebridClientFactory()
    {
        _api = new HttpClient { BaseAddress = new Uri("https://api.torbox.app/"), Timeout = TimeSpan.FromSeconds(30) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("LeLibrary-Jellyfin/0.1");
    }

    /// <summary>Creates the client for a configuration.</summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <returns>The provider client.</returns>
    public IDebridClient Create(PluginConfiguration configuration)
    {
        // v0.1 supports TorBox. The settings page offers the others only when
        // their clients exist, so an unknown value must not silently become
        // TorBox: it falls back to TorBox only because that is the only client.
        return new TorboxClient(_api, configuration.DebridApiKey ?? string.Empty);
    }

    /// <inheritdoc />
    public void Dispose() => _api.Dispose();
}
