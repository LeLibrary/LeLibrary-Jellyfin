using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.AllDebrid.Model;
using Jellyfin.Plugin.Standalone.AllDebrid.Providers;
using Jellyfin.Plugin.Standalone.AllDebrid.Virtual;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Standalone.AllDebrid.Virtual;

/// <summary>What a sync changed in Jellyfin's library.</summary>
/// <param name="Added">Items created.</param>
/// <param name="Updated">Items whose metadata changed.</param>
/// <param name="Removed">Items deleted.</param>
/// <param name="Unchanged">Items already right.</param>
/// <param name="Skipped">Plan entries that could not be represented.</param>
public sealed record VirtualSyncResult(int Added, int Updated, int Removed, int Unchanged, int Skipped);

/// <summary>
/// The library lives in Jellyfin's database, not on disk.
///
/// Each library points at a real, empty folder (a single anchor file keeps it
/// from being skipped), and the items hang off that folder's physical parent
/// with a path inside it. That is what makes Jellyfin treat them as ordinary
/// movies and episodes: no scan, no probing, nothing to read.
/// </summary>
public sealed class VirtualLibrary
{
    /// <summary>Suffix for the movies library, after the provider name.</summary>
    public const string MoviesSuffix = " Movies";

    /// <summary>Suffix for the series library, after the provider name.</summary>
    public const string SeriesSuffix = " Series";

    private const string AnchorFile = ".lelibrary";

    private readonly NativeMetadataWorker _metadataWorker;
    private readonly ILibraryManager _library;
    private readonly ILibraryWriter _writer;
    private readonly ILogger<VirtualLibrary> _log;

    /// <summary>Initializes a new instance of the <see cref="VirtualLibrary"/> class.</summary>
    /// <param name="library">The library manager, used to set the libraries up.</param>
    /// <param name="writer">Where the items are written.</param>
    /// <param name="log">The logger.</param>
    public VirtualLibrary(ILibraryManager library, ILibraryWriter writer, ILogger<VirtualLibrary> log, NativeMetadataWorker metadataWorker)
    {
        _metadataWorker = metadataWorker;
        _library = library;
        _writer = writer;
        _log = log;
    }

    /// <summary>Makes sure both libraries exist and returns their physical folders.</summary>
    /// <param name="dataPath">The plugin's data folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movies and shows folders, or nulls when one could not be made.</returns>
    public async Task<(Folder? Movies, Folder? Shows)> EnsureLibrariesAsync(string dataPath, string providerName, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(providerName) ? "Debrid" : providerName.Trim();
        var moviesPath = Path.Combine(dataPath, "movies");
        var showsPath = Path.Combine(dataPath, "shows");
        var movies = await EnsureLibraryAsync(name + MoviesSuffix, moviesPath, CollectionTypeOptions.movies, cancellationToken).ConfigureAwait(false);
        var shows = await EnsureLibraryAsync(name + SeriesSuffix, showsPath, CollectionTypeOptions.tvshows, cancellationToken).ConfigureAwait(false);
        return (movies, shows);
    }

    private async Task<Folder?> EnsureLibraryAsync(string name, string path, CollectionTypeOptions collectionType, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path);
        var anchor = Path.Combine(path, AnchorFile);
        if (!File.Exists(anchor))
        {
            File.WriteAllText(anchor, "This folder stays empty. The items are stored in Jellyfin's database.\n");
        }

        var existing = _library.GetVirtualFolders().Find(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var options = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(path) },
                EnableRealtimeMonitor = false,
                EnableChapterImageExtraction = false,
                EnableTrickplayImageExtraction = false,
            };
            await _library.AddVirtualFolder(name, collectionType, options, false).ConfigureAwait(false);
            existing = _library.GetVirtualFolders().Find(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (existing?.ItemId is null)
        {
            _log.LogError("Standalone: could not create the {Name} library", name);
            return null;
        }

        // Jellyfin only links a library folder to its parent after this runs;
        // without it the folder has no physical parent and items have nowhere
        // to hang.
        await _library.ValidateTopLibraryFolders(cancellationToken, false).ConfigureAwait(false);

        if (_library.GetItemById(Guid.Parse(existing.ItemId)) is not CollectionFolder collection)
        {
            _log.LogError("Standalone: {Name} is not a library folder", name);
            return null;
        }

        var physicalId = collection.PhysicalFolderIds.FirstOrDefault();
        if (physicalId == Guid.Empty)
        {
            _log.LogError("Standalone: {Name} has no physical folder; {Path} must contain the anchor file", name, path);
            return null;
        }

        return _library.GetItemById(physicalId) as Folder;
    }

    /// <summary>Applies the desired library: creates, updates and removes items.</summary>
    /// <param name="desired">What the plan wants.</param>
    /// <param name="moviesRoot">The movies physical folder.</param>
    /// <param name="showsRoot">The shows physical folder.</param>
    /// <param name="playBaseUrl">The base of the play URLs.</param>
    /// <param name="allowRemovals">Whether items that are no longer wanted may be deleted. A plan that came from a cache may not remove anything.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What changed.</returns>
    public Task<VirtualSyncResult> ApplyAsync(VirtualLibraryPlan desired, Folder moviesRoot, Folder showsRoot, string playBaseUrl, bool allowRemovals, CancellationToken cancellationToken)
    {
        var existing = LoadExisting();
        // Grouped by parent: CreateItems takes one parent for the whole batch,
        // and SQLite refuses a single call with tens of thousands of rows.
        var created = new List<(BaseItem Parent, BaseItem Item)>();
        var updated = 0;
        var unchanged = 0;
        // The rows that duplicate another row for the same item.
        var duplicates = new List<BaseItem>();

        // Series, then seasons, then episodes: a child needs its parent to exist.
        var byPath = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var sequences = new[]
        {
            (desired.Series, showsRoot),
            (desired.Seasons, showsRoot),
            (desired.Episodes, showsRoot),
            (desired.Movies, moviesRoot),
        };

        foreach (var (items, root) in sequences)
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = item.ParentRelativePath is null
                    ? root
                    : byPath.TryGetValue(item.ParentRelativePath, out var known) ? known : null;
                if (parent is null)
                {
                    _log.LogWarning("Standalone: no parent for {Path}", item.RelativePath);
                    continue;
                }

                // The path is the stream URL: the play endpoint serves the
                // bytes from the debrid account. It is not a file, and it is
                // not a .strm: Jellyfin reads the item's container from the
                // item itself, which is why the container has to be set.
                var identity = item.Identity;
                if (identity is null)
                {
                    // An item with no identity cannot be matched and must not
                    // be guessed at: a wrong guess duplicates it.
                    _log.LogWarning("Standalone: no identity for {Path}", item.RelativePath);
                    continue;
                }

                var secret = Plugin.Instance?.Configuration.StreamSecret ?? throw new InvalidOperationException("The stream secret is unavailable.");
                var playUrl = $"{playBaseUrl.TrimEnd('/')}/StandaloneAllDebrid/play/{secret}/{item.Key}/{Uri.EscapeDataString(item.FileName)}";
                byPath[item.RelativePath] = item.Item;

                if (existing.TryGetValue(identity, out var candidates))
                {
                    // A library that doubled before the fix has more than one
                    // row for the same item. The one that matches the current
                    // URL survives; the others are duplicates and go.
                    var current = ChooseSurvivor(candidates, playUrl);
                    foreach (var duplicate in candidates.Where(row => row.Id != current.Id))
                    {
                        duplicates.Add(duplicate);
                    }

                    var pathChanged = !string.Equals(current.Path, playUrl, StringComparison.Ordinal);

                    var changed = pathChanged || NeedsUpdate(current, item.Item);
                    if (pathChanged)
                    {
                        current.Path = playUrl;
                    }

                    current.IndexNumber = item.Item.IndexNumber;
                    current.ParentIndexNumber = item.Item.ParentIndexNumber;
                    if (current is Movie or Episode) current.Container = item.Item.Container;
                    MergeProviderIds(current, item.Item);
                    if (changed)
                    {
                        _writer.UpdateItem(current);
                        updated++;
                    }
                    else
                    {
                        unchanged++;
                    }

                    item.Item.Id = current.Id;
                    byPath[item.RelativePath] = current;
                    continue;
                }

                // The id comes from the identity, never from the URL: a change
                // of server URL must not rewrite every id and orphan the
                // watch history.
                item.Item.Id = _writer.NewItemId(identity, item.Item.GetType());
                item.Item.Path = playUrl;
                // The container Jellyfin derives when it builds the media
                // source. Without it a request that does not name a container
                // cannot be answered at all.
                if (item.Item is Movie or Episode) item.Item.Container = VirtualIdentity.ContainerOf(item.FileName);
                item.Item.ParentId = parent.Id;
                item.Item.DateCreated = DateTime.UtcNow;
                item.Item.DateModified = DateTime.UtcNow;
                item.Item.PresentationUniqueKey = item.Item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
                created.Add((parent, item.Item));
                byPath[item.RelativePath] = item.Item;
            }
        }

        const int BatchSize = 100;
        foreach (var group in created.GroupBy(entry => entry.Parent.Id))
        {
            var parent = group.First().Parent;
            var batch = new List<BaseItem>(BatchSize);
            foreach (var entry in group)
            {
                batch.Add(entry.Item);
                if (batch.Count == BatchSize)
                {
                    _writer.CreateItems(batch.ToList(), parent);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                _writer.CreateItems(batch, parent);
            }
        }

        // Do not queue recursive series refreshes: process each created item
        // individually, with one native provider/image refresh in flight.
        _metadataWorker.ScheduleMany(created.Select(entry => entry.Item));

        var removed = RemoveStale(desired, existing, duplicates, allowRemovals);
        _log.LogInformation(
            "Standalone: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Skipped} skipped",
            created.Count, updated, removed, unchanged, desired.Skipped);
        return Task.FromResult(new VirtualSyncResult(created.Count, updated, removed, unchanged, desired.Skipped));
    }

    private Dictionary<string, List<BaseItem>> LoadExisting()
    {
        // Keyed by what the item is, not by where it is served from: a row is
        // found again even when the server URL has changed. A library that
        // doubled before the fix has more than one row for an identity, so
        // every row is kept until the diff decides which one survives.
        var index = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
        foreach (var item in _writer.LoadItems())
        {
            var identity = ItemIdentity.Of(item);
            if (identity is null)
            {
                continue;
            }

            if (!index.TryGetValue(identity, out var rows))
            {
                rows = new List<BaseItem>();
                index[identity] = rows;
            }

            rows.Add(item);
        }

        return index;
    }

    /// <summary>
    /// The row kept for an identity when a library holds it more than once.
    /// The one whose path matches the current URL wins, because that is the row
    /// this sync is writing; otherwise the oldest, which is the one that has
    /// been watched.
    /// </summary>
    /// <param name="candidates">The rows with that identity.</param>
    /// <param name="playUrl">The URL this sync writes.</param>
    /// <returns>The row to keep.</returns>
    private static BaseItem ChooseSurvivor(IReadOnlyList<BaseItem> candidates, string playUrl)
    {
        foreach (var row in candidates)
        {
            if (string.Equals(row.Path, playUrl, StringComparison.Ordinal))
            {
                return row;
            }
        }

        var oldest = candidates[0];
        foreach (var row in candidates)
        {
            if (row.DateCreated < oldest.DateCreated)
            {
                oldest = row;
            }
        }

        return oldest;
    }

    private static bool NeedsUpdate(BaseItem current, BaseItem wanted)
        => (current is Movie or Episode) && !string.Equals(current.Container, wanted.Container, StringComparison.Ordinal)
            || current.IndexNumber != wanted.IndexNumber
            || current.ParentIndexNumber != wanted.ParentIndexNumber
            || ProviderIdsDiffer(current, wanted);

    /// <summary>
    /// Whether the row's ids are not the ones the plan wants.
    /// </summary>
    /// <param name="current">The row.</param>
    /// <param name="wanted">The item the plan built.</param>
    /// <returns>True when the row has to be rewritten.</returns>
    private static bool ProviderIdsDiffer(BaseItem current, BaseItem wanted)
    {
        // Only our own ids are compared. Jellyfin's own metadata refresh adds
        // ids of its own (an IMDb id, a TVDB id), and those must not make
        // every row look stale on every sync.
        foreach (var pair in wanted.ProviderIds)
        {
            if (!current.ProviderIds.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // One of our own ids that the plan no longer wants: a series that used
        // to carry the plan key of one of its episodes.
        foreach (var key in current.ProviderIds.Keys)
        {
            if (IsOurId(key) && !wanted.ProviderIds.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the plan's ids onto the row, keeping the ids Jellyfin's own
    /// providers found.
    /// </summary>
    /// <param name="current">The row.</param>
    /// <param name="wanted">The item the plan built.</param>
    private static void MergeProviderIds(BaseItem current, BaseItem wanted)
    {
        var merged = new Dictionary<string, string>(current.ProviderIds, StringComparer.OrdinalIgnoreCase);
        foreach (var key in merged.Keys.Where(IsOurId).ToList())
        {
            merged.Remove(key);
        }

        foreach (var pair in wanted.ProviderIds)
        {
            merged[pair.Key] = pair.Value;
        }

        current.ProviderIds = merged;
    }

    private static bool IsOurId(string key)
        => key == VirtualIdentity.ProviderId || key == VirtualIdentity.SeriesId || key == VirtualIdentity.SeasonId;

    private int RemoveStale(
        VirtualLibraryPlan desired,
        Dictionary<string, List<BaseItem>> existing,
        List<BaseItem> duplicates,
        bool allowRemovals)
    {
        // A plan that came from a cache is not proof that an item is gone. It
        // removes nothing: only a fresh, complete plan may delete.
        if (!allowRemovals)
        {
            return 0;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in new[] { desired.Movies, desired.Series, desired.Seasons, desired.Episodes })
        {
            foreach (var item in group)
            {
                if (item.Identity is not null) wanted.Add(item.Identity);
            }
        }

        static int Rank(BaseItem item) => item switch
        {
            Episode => 0,
            Season => 1,
            Series => 2,
            _ => 3,
        };

        // Compared by identity, not by provider id: a series used to carry the
        // plan key of one of its episodes, so removing that episode removed
        // the series with it.
        var stale = existing
            .Where(pair => !wanted.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToList();

        // A library that doubled before the fix holds the same item twice. The
        // row the diff kept is the one that stays; the other is a duplicate.
        foreach (var duplicate in duplicates)
        {
            if (!stale.Any(row => row.Id == duplicate.Id))
            {
                stale.Add(duplicate);
            }
        }

        stale = stale.OrderBy(Rank).ToList();
        foreach (var item in stale)
        {
            if (!string.IsNullOrEmpty(item.Path) && item.Path!.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(item.Path); } catch (IOException) { /* next sync retries */ }
            }

            _writer.DeleteItem(item);
        }

        return stale.Count;
    }
}
