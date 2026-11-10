using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Client;
using Jellyfin.Plugin.LeLibrary.Configuration;
using Jellyfin.Plugin.LeLibrary.Providers;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeLibrary;

/// <summary>
/// The scheduled sync: plan in, Jellyfin items out. No files and no library
/// scan, so a large library appears in one pass instead of hours of scanning.
/// </summary>
public sealed class SyncTask : IScheduledTask
{
    private readonly LeLibrarySync _sync;
    private readonly IServerApplicationHost _appHost;
    private readonly ILibraryManager _library;
    private readonly ILogger<SyncTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncTask"/> class.</summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="appHost">The server host.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public SyncTask(LeLibrarySync sync, IServerApplicationHost appHost, ILibraryManager library, ILogger<SyncTask> logger)
    {
        _sync = sync;
        _appHost = appHost;
        _library = library;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LeLibrary Sync";

    /// <inheritdoc />
    public string Key => "LeLibrarySync";

    /// <inheritdoc />
    public string Description => "Reads your LeLibrary plan and updates the library shown in Jellyfin.";

    /// <inheritdoc />
    public string Category => "LeLibrary";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var configuration = Plugin.Instance?.Configuration;
        var hours = Math.Clamp(configuration?.SyncHours ?? 6, 1, 24);
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
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

        var outcome = await _sync.RunAsync(configuration, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
        if (!outcome.Ok)
        {
            _logger.LogWarning("LeLibrary sync stopped without changes: {Error}", outcome.Error);
            return;
        }

        _logger.LogInformation(
            "LeLibrary sync: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Skipped} skipped",
            outcome.Result!.Added,
            outcome.Result.Updated,
            outcome.Result.Removed,
            outcome.Result.Unchanged,
            outcome.Result.Skipped);
        _ = _appHost;
        _ = _library;
    }
}
