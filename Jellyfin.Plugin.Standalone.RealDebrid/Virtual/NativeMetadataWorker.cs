using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Standalone.RealDebrid.Virtual;

/// <summary>Resumable, sequential native Jellyfin metadata and image work.</summary>
public sealed class NativeMetadataWorker : IDisposable
{
    private readonly Channel<Guid> _pending = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<Guid> _ids = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly IProviderManager _providers;
    private readonly ILibraryManager _library;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<NativeMetadataWorker> _log;
    private readonly string _path;
    private readonly Task _worker;

    /// <summary>Initializes a new instance of the <see cref="NativeMetadataWorker"/> class.</summary>
    public NativeMetadataWorker(IProviderManager providers, ILibraryManager library, IFileSystem fileSystem, IApplicationPaths paths, ILogger<NativeMetadataWorker> log)
    {
        _providers = providers; _library = library; _fileSystem = fileSystem; _log = log;
        _path = Path.Combine(paths.DataPath, "standalone-realdebrid", "metadata-queue.json");
        try
        {
            if (File.Exists(_path))
            {
                foreach (var id in JsonSerializer.Deserialize<Guid[]>(File.ReadAllText(_path)) ?? Array.Empty<Guid>())
                    if (id != Guid.Empty && _ids.Add(id)) _pending.Writer.TryWrite(id);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { _log.LogWarning(error, "Could not restore the metadata queue"); }
        _worker = Task.Run(ProcessAsync);
    }

    /// <summary>Persists and schedules items for native Jellyfin identification.</summary>
    public void ScheduleMany(IEnumerable<BaseItem> items)
    {
        lock (_gate)
        {
            var added = new List<Guid>();
            foreach (var item in items)
                if (item.Id != Guid.Empty && _ids.Add(item.Id)) added.Add(item.Id);
            if (added.Count == 0) return;
            Save();
            foreach (var id in added) _pending.Writer.TryWrite(id);
        }
    }

    // Called under the gate; an interrupted write leaves the old snapshot intact.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_ids));
            File.Move(temp, _path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _log.LogWarning(error, "Could not save the metadata queue"); }
    }

    private async Task ProcessAsync()
    {
        var sinceSave = 0;
        try
        {
            await foreach (var id in _pending.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var complete = false;
                try
                {
                    var item = _library.GetItemById(id);
                    if (item is not null)
                    {
                        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                            ReplaceAllImages = false,
                            ReplaceAllMetadata = false,
                        };
                        await _providers.RefreshSingleItem(item, options, _stop.Token).ConfigureAwait(false);
                    }
                    complete = true;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error) { _log.LogWarning(error, "Metadata refresh failed for {Id}; it will be retried after restart", id); }
                lock (_gate)
                {
                    if (complete) _ids.Remove(id);
                    if (++sinceSave >= 32 || !_pending.Reader.TryPeek(out _)) { Save(); sinceSave = 0; }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pending.Writer.TryComplete(); _stop.Cancel();
        lock (_gate) Save();
        _ = _worker;
    }
}
