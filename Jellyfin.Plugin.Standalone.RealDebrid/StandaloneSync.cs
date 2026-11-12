using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.RealDebrid.Configuration;
using Jellyfin.Plugin.Standalone.RealDebrid.Virtual;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Standalone.RealDebrid;

/// <summary>Outcome of a Real-Debrid sync.</summary>
public sealed record SyncOutcome(bool Ok, string? Error, VirtualSyncResult? Result);

/// <summary>Builds a Jellyfin library directly from a Real-Debrid account.</summary>
public sealed class StandaloneSync
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly DebridClientFactory _clients;
    private readonly VirtualLibrary _library;
    private readonly IApplicationPaths _paths;
    private readonly IServerApplicationHost _host;
    private readonly ILibraryManager _manager;

    /// <summary>Initializes a new instance of the <see cref="StandaloneSync"/> class.</summary>
    public StandaloneSync(DebridClientFactory clients, VirtualLibrary library, IApplicationPaths paths, IServerApplicationHost host, ILibraryManager manager)
    { _clients = clients; _library = library; _paths = paths; _host = host; _manager = manager; }

    /// <summary>Syncs a validated listing without removing existing items.</summary>
    public async Task<SyncOutcome> RunAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RunOnceAsync(configuration, cancellationToken).ConfigureAwait(false); }
        finally { _syncGate.Release(); }
    }

    private async Task<SyncOutcome> RunOnceAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.DebridApiKey)) return new(false, "Set your Real-Debrid API key first.", null);
        var provider = _clients.Create(configuration);
        if (provider is null) return new(false, "Real-Debrid is not configured.", null);
        var listing = await provider.ListLibraryAsync(cancellationToken).ConfigureAwait(false);
        if (!listing.IsHealthy) return new(false, listing.Error ?? "Real-Debrid listing failed.", null);
        var (plan, _) = new LocalPlanBuilder(_manager.ParseName).Build(provider.Provider, listing.Files);
        var desired = VirtualItemFactory.Build(plan, StandaloneSync.FileNamesByKey(listing.Files));
        var (movies, shows) = await _library.EnsureLibrariesAsync(Path.Combine(_paths.DataPath, "standalone-realdebrid"), "Standalone Real-Debrid", cancellationToken).ConfigureAwait(false);
        if (movies is null || shows is null) return new(false, "The Jellyfin libraries could not be created.", null);
        var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl) ? _host.GetApiUrlForLocalAccess() : configuration.ServerUrl.Trim();
        var result = await _library.ApplyAsync(desired, movies, shows, baseUrl, false, cancellationToken).ConfigureAwait(false);
        return new(true, null, result);
    }

    /// <summary>Returns original file names keyed by provider identity.</summary>
    public static System.Collections.Generic.Dictionary<string, string> FileNamesByKey(System.Collections.Generic.IEnumerable<Providers.DebridFile> files)
    {
        var names = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files) names.TryAdd(file.Key, file.Name);
        return names;
    }
}
