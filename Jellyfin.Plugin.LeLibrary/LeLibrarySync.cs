using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Client;
using Jellyfin.Plugin.LeLibrary.Configuration;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Providers;
using Jellyfin.Plugin.LeLibrary.Virtual;
using Jellyfin.Plugin.LeLibrary.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeLibrary;

/// <summary>The outcome of one sync.</summary>
/// <param name="Ok">False when nothing could be trusted and nothing changed.</param>
/// <param name="Error">Why, when !Ok.</param>
/// <param name="Result">What changed, when Ok.</param>
public sealed record SyncOutcome(bool Ok, string? Error, VirtualSyncResult? Result);

/// <summary>
/// One sync: read the plan, check the account, write the items into Jellyfin.
///
/// Nothing is removed unless the plan was read successfully *and* the account
/// answered with a complete listing. Every other failure leaves the library
/// exactly as it is.
/// </summary>
public sealed class LeLibrarySync
{
    private readonly DebridClientFactory _clients;
    private readonly VirtualLibrary _library;
    private readonly IApplicationPaths _paths;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<LeLibrarySync> _log;

    /// <summary>Initializes a new instance of the <see cref="LeLibrarySync"/> class.</summary>
    /// <param name="clients">The provider factory.</param>
    /// <param name="library">The virtual library writer.</param>
    /// <param name="paths">Jellyfin's paths (for the cached plan).</param>
    /// <param name="log">The logger.</param>
    public LeLibrarySync(DebridClientFactory clients, VirtualLibrary library, IApplicationPaths paths, IServerApplicationHost appHost, ILogger<LeLibrarySync> log)
    {
        _clients = clients;
        _library = library;
        _paths = paths;
        _appHost = appHost;
        _log = log;
    }

    private static string ProviderName(string provider) => provider switch
    {
        "torbox" => "TorBox",
        "realdebrid" => "Real-Debrid",
        "alldebrid" => "AllDebrid",
        "premiumize" => "Premiumize",
        _ => provider,
    };

    /// <summary>Runs one sync.</summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    public async Task<SyncOutcome> RunAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.PlanUrl) || string.IsNullOrWhiteSpace(configuration.DebridApiKey))
        {
            return new SyncOutcome(false, "set the LeLibrary URL and your debrid key first", null);
        }

        var cachePath = Path.Combine(_paths.PluginConfigurationsPath, "lelibrary-plan.json");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LeLibrary-Jellyfin/0.1");
        var plans = new LeLibraryClient(http, cachePath);
        var fetch = await plans.FetchAsync(configuration.PlanUrl, cancellationToken).ConfigureAwait(false);
        if (!fetch.Ok)
        {
            return new SyncOutcome(false, fetch.Error ?? "the plan could not be read", null);
        }

        var provider = _clients.Create(configuration);
        if (!string.IsNullOrEmpty(fetch.Plan.Provider) && !string.Equals(fetch.Plan.Provider, provider.Provider, StringComparison.Ordinal))
        {
            return new SyncOutcome(false, $"the plan is for {fetch.Plan.Provider}, but the plugin is set to {provider.Provider}", null);
        }

        var listing = await provider.ListLibraryAsync(cancellationToken).ConfigureAwait(false);
        if (!listing.IsHealthy)
        {
            return new SyncOutcome(false, listing.Error ?? "the debrid account could not be listed", null);
        }

        // A key the account no longer has would be an item that cannot play.
        var available = listing.Files.Select(file => file.Key).ToHashSet(StringComparer.Ordinal);
        var playable = new JellyfinPlan
        {
            SchemaVersion = fetch.Plan.SchemaVersion,
            Provider = fetch.Plan.Provider,
            Libraries = fetch.Plan.Libraries,
            Items = fetch.Plan.Items.Where(item => available.Contains(item.Key)).ToList(),
        };

        var desired = VirtualItemFactory.Build(playable);
        var dataPath = Path.Combine(_paths.DataPath, "lelibrary");
        var (movies, shows) = await _library.EnsureLibrariesAsync(dataPath, ProviderName(provider.Provider), cancellationToken).ConfigureAwait(false);
        if (movies is null || shows is null)
        {
            return new SyncOutcome(false, "the libraries could not be created", null);
        }

        var playBase = string.IsNullOrWhiteSpace(configuration.ServerUrl)
            ? _appHost.GetApiUrlForLocalAccess()
            : configuration.ServerUrl.Trim();
        var result = await _library.ApplyAsync(desired, movies, shows, playBase, cancellationToken).ConfigureAwait(false);
        _ = _log;
        return new SyncOutcome(true, null, result);
    }
}
