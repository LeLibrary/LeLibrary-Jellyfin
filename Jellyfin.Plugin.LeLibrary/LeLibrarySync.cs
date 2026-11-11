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

    private static string ProviderName(string provider) => DebridClientFactory.ProviderName(provider);

    /// <summary>
    /// Whether a plan may delete anything. Only a plan that was read fresh
    /// proves that an item is gone: a cached one may be out of date, and a
    /// failed fetch proves nothing at all.
    /// </summary>
    /// <param name="source">Where the plan came from.</param>
    /// <returns>True when removals are allowed.</returns>
    public static bool AllowRemovals(PlanSource source) => source == PlanSource.Network;

    /// <summary>
    /// The provider's file names, keyed the way the plan keys its items. Two
    /// files under one key are one file seen twice.
    /// </summary>
    /// <param name="files">The provider's listing.</param>
    /// <returns>The file name for each key.</returns>
    public static Dictionary<string, string> FileNamesByKey(IEnumerable<DebridFile> files)
        => files
            .GroupBy(file => file.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

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

        // Before anything else: a provider the plugin cannot talk to is the
        // user's mistake, and saying so is more useful than whatever the plan
        // fetch would complain about.
        var provider = _clients.Create(configuration);
        if (provider is null)
        {
            return new SyncOutcome(false, DebridClientFactory.UnsupportedMessage(configuration.Provider), null);
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
        // The file's real name is kept: the container and the play URL have to
        // carry the file's real extension, not a guess.
        var available = FileNamesByKey(listing.Files);
        var playable = new JellyfinPlan
        {
            SchemaVersion = fetch.Plan.SchemaVersion,
            Provider = fetch.Plan.Provider,
            Libraries = fetch.Plan.Libraries,
            Items = fetch.Plan.Items.Where(item => available.ContainsKey(item.Key)).ToList(),
        };

        var desired = VirtualItemFactory.Build(playable, available);
        var dataPath = Path.Combine(_paths.DataPath, "lelibrary");
        var (movies, shows) = await _library.EnsureLibrariesAsync(dataPath, ProviderName(provider.Provider), cancellationToken).ConfigureAwait(false);
        if (movies is null || shows is null)
        {
            return new SyncOutcome(false, "the libraries could not be created", null);
        }

        var playBase = string.IsNullOrWhiteSpace(configuration.ServerUrl)
            ? _appHost.GetApiUrlForLocalAccess()
            : configuration.ServerUrl.Trim();

        // A plan that came from the cache is not proof that anything is gone:
        // it may not remove a single item.
        var allowRemovals = AllowRemovals(fetch.Source);
        if (!allowRemovals)
        {
            _log.LogInformation(
                "LeLibrary: the plan came from the cache, so nothing will be removed. {Error}",
                fetch.Error ?? "the plan was not modified");
        }

        var result = await _library.ApplyAsync(desired, movies, shows, playBase, allowRemovals, cancellationToken).ConfigureAwait(false);
        return new SyncOutcome(true, null, result);
    }
}
