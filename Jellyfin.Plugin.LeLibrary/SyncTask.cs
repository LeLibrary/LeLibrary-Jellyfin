using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Client;
using Jellyfin.Plugin.LeLibrary.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeLibrary;

/// <summary>
/// The scheduled sync: plan in, playable files out. Refuses to touch anything
/// when the plan or the account listing cannot be trusted.
/// </summary>
public sealed class SyncTask : IScheduledTask
{
    private readonly DebridClientFactory _clients;
    private readonly IApplicationPaths _paths;
    private readonly IServerApplicationHost _appHost;
    private readonly ILibraryManager _library;
    private readonly ILogger<SyncTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncTask"/> class.</summary>
    /// <param name="clients">The provider factory.</param>
    /// <param name="paths">Jellyfin's paths (for the cached plan).</param>
    /// <param name="appHost">The server host (for the play URL).</param>
    /// <param name="library">The library manager (for the scan).</param>
    /// <param name="logger">The logger.</param>
    public SyncTask(DebridClientFactory clients, IApplicationPaths paths, IServerApplicationHost appHost, ILibraryManager library, ILogger<SyncTask> logger)
    {
        _clients = clients;
        _paths = paths;
        _appHost = appHost;
        _library = library;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LeLibrary Sync";

    /// <inheritdoc />
    public string Key => "LeLibrarySync";

    /// <inheritdoc />
    public string Description => "Reads your LeLibrary plan and updates the playable files for your debrid account.";

    /// <inheritdoc />
    public string Category => "LeLibrary";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var configuration = Plugin.Instance?.Configuration;
        var hours = Math.Clamp(configuration?.SyncHours ?? 6, 1, 24);
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger,
        };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(hours).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null) return;

        var result = await RunAsync(configuration, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
        if (!result.Ok)
        {
            _logger.LogWarning("LeLibrary sync stopped without changes: {Error}", result.Error);
            return;
        }

        if (result.Added + result.Removed + result.Moved > 0)
        {
            // Only the touched folder needs a rescan, and Jellyfin debounces it.
            _library.QueueLibraryScan();
        }
    }

    /// <summary>Runs one sync with the given settings (also used by "Run sync now").</summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    public Task<SyncResult> RunAsync(Configuration.PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.PlanUrl) || string.IsNullOrWhiteSpace(configuration.DebridApiKey))
        {
            return Task.FromResult(new SyncResult(false, "set the LeLibrary URL and your debrid key first", 0, 0, 0, 0, 0));
        }

        var cachePath = Path.Combine(_paths.PluginConfigurationsPath, "lelibrary-plan.json");
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LeLibrary-Jellyfin/0.1");
        var plans = new LeLibraryClient(http, cachePath);
        var provider = _clients.Create(configuration);
        var runner = new SyncRunner(plans, provider, message => _logger.LogInformation("LeLibrary: {Message}", message));
        var playBase = _appHost.GetApiUrlForLocalAccess();
        return runner.RunAsync(new SyncRequest(configuration.LibraryPath, configuration.PlanUrl, playBase), cancellationToken);
    }
}
