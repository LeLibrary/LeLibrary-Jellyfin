using System;
using System.Net.Http;
using Jellyfin.Plugin.Standalone.Premiumize.Configuration;
using Jellyfin.Plugin.Standalone.Premiumize.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Standalone.Premiumize;

/// <summary>
/// Builds the debrid client for the current plugin settings. One shared HTTP
/// client, so a sync and a play re-use connections instead of handshaking.
/// </summary>
public sealed class DebridClientFactory : IDisposable
{
    private readonly HttpClient _api;
    private readonly ILogger<DebridClientFactory>? _log;

    /// <summary>Initializes a new instance of the <see cref="DebridClientFactory"/> class.</summary>
    /// <param name="log">The logger, when there is one.</param>
    public DebridClientFactory(ILogger<DebridClientFactory>? log = null)
    {
        _log = log;
        var baseUri = new Uri("https://www.premiumize.me/api/");
#if DEBUG
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("STANDALONE_PM_TEST_API"), UriKind.Absolute, out var testUri)) baseUri = testUri;
#endif
        _api = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("Standalone-Jellyfin/0.1");
    }

    /// <summary>Creates the client for a configuration.</summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <returns>The provider client, or null when the provider is not implemented.</returns>
    public IDebridClient? Create(PluginConfiguration configuration)
    {
        // v0.1 supports TorBox. The settings page offers the others only when
        // their clients exist, so an unknown value must not silently become
        // TorBox: the user would be talking to the wrong account.
        if (string.Equals(configuration.Provider, "premiumize", StringComparison.OrdinalIgnoreCase))
        {
            return new PremiumizeClient(_api, configuration.DebridApiKey ?? string.Empty);
        }

        _log?.LogWarning("Standalone: {Provider} is not supported yet", ProviderName(configuration.Provider));
        return null;
    }

    /// <summary>The name of a provider, as it is shown to the user.</summary>
    /// <param name="provider">The provider id.</param>
    /// <returns>The name.</returns>
    public static string ProviderName(string? provider) => provider switch
    {
        "torbox" => "TorBox",
        "realdebrid" => "Real-Debrid",
        "alldebrid" => "AllDebrid",
        "premiumize" => "Premiumize",
        _ => provider ?? string.Empty,
    };

    /// <summary>What to tell the user when they picked a provider that does not work yet.</summary>
    /// <param name="provider">The provider id.</param>
    /// <returns>The message.</returns>
    public static string UnsupportedMessage(string? provider)
        => $"{ProviderName(provider)} is not supported yet. Set the provider to Premiumize.";

    /// <inheritdoc />
    public void Dispose() => _api.Dispose();
}
