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
    private readonly MetadataHolder _metadata;
    private readonly ILogger<LeLibrarySync> _log;

    /// <summary>Initializes a new instance of the <see cref="LeLibrarySync"/> class.</summary>
    /// <param name="clients">The provider factory.</param>
    /// <param name="library">The virtual library writer.</param>
    /// <param name="paths">Jellyfin's paths (for the cached plan).</param>
    /// <param name="appHost">Jellyfin's application host.</param>
    /// <param name="metadata">Where the metadata document is kept for the providers.</param>
    /// <param name="log">The logger.</param>
    public LeLibrarySync(DebridClientFactory clients, VirtualLibrary library, IApplicationPaths paths, IServerApplicationHost appHost, MetadataHolder metadata, ILogger<LeLibrarySync> log)
    {
        _clients = clients;
        _library = library;
        _paths = paths;
        _appHost = appHost;
        _metadata = metadata;
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
    /// Whether a fetched document has not been applied to this library yet. A
    /// new one means every title in it is refreshed once — that is how an
    /// upgrade picks up metadata for items that already existed, because a
    /// metadata provider only runs when an item is refreshed.
    /// </summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="fetch">The fetch result.</param>
    /// <returns>True when the library should be refreshed.</returns>
    public static bool ShouldRefreshMetadata(PluginConfiguration configuration, MetadataFetch fetch)
        => fetch.Ok
            && !string.IsNullOrEmpty(fetch.Etag)
            && !string.Equals(configuration.AppliedMetadataEtag, fetch.Etag, StringComparison.Ordinal);

    /// <summary>
    /// Puts a fetched metadata document where Jellyfin's providers read it, and
    /// returns the document this sync should attach art from.
    ///
    /// A failed fetch changes nothing: the providers keep the last document
    /// they had, and the library is written without metadata rather than not
    /// written at all.
    /// </summary>
    /// <param name="holder">The document holder.</param>
    /// <param name="fetch">The fetch result.</param>
    /// <returns>The document to apply, or null.</returns>
    public static MetadataDocument? AcceptMetadata(MetadataHolder holder, MetadataFetch fetch)
    {
        if (!fetch.Ok)
        {
            return null;
        }

        holder.Document = fetch.Document;
        return fetch.Document;
    }

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

        // The metadata is an enrichment, never a dependency: a library without
        // it is better than no library, so a failed fetch is logged and the
        // sync carries on.
        var metadataPath = Path.Combine(_paths.PluginConfigurationsPath, "lelibrary-metadata.json");
        var metadata = await new MetadataClient(http, metadataPath)
            .FetchAsync(configuration.PlanUrl, cancellationToken)
            .ConfigureAwait(false);
        var document = AcceptMetadata(_metadata, metadata);
        if (!metadata.Ok)
        {
            _log.LogWarning("LeLibrary: metadata unavailable, the items keep what they have: {Error}", metadata.Error);
        }

        // A document the library has not been refreshed against yet means one
        // pass over every title, so existing items get the metadata too.
        var refreshMetadata = ShouldRefreshMetadata(configuration, metadata);
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

        var result = await _library.ApplyAsync(desired, movies, shows, playBase, allowRemovals, cancellationToken, document, refreshMetadata).ConfigureAwait(false);

        // Recorded only after the library was written: a failed sync must try
        // the refresh again next time.
        if (refreshMetadata)
        {
            configuration.AppliedMetadataEtag = metadata.Etag!;
            Plugin.Instance?.UpdateConfiguration(configuration);
        }

        return new SyncOutcome(true, null, result);
    }
}
