using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeLibrary.Virtual;

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

    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<VirtualLibrary> _log;

    /// <summary>Initializes a new instance of the <see cref="VirtualLibrary"/> class.</summary>
    /// <param name="library">The library manager.</param>
    /// <param name="log">The logger.</param>
    public VirtualLibrary(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, ILogger<VirtualLibrary> log)
    {
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
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
            _log.LogError("LeLibrary: could not create the {Name} library", name);
            return null;
        }

        // Jellyfin only links a library folder to its parent after this runs;
        // without it the folder has no physical parent and items have nowhere
        // to hang.
        await _library.ValidateTopLibraryFolders(cancellationToken, false).ConfigureAwait(false);

        if (_library.GetItemById(Guid.Parse(existing.ItemId)) is not CollectionFolder collection)
        {
            _log.LogError("LeLibrary: {Name} is not a library folder", name);
            return null;
        }

        var physicalId = collection.PhysicalFolderIds.FirstOrDefault();
        if (physicalId == Guid.Empty)
        {
            _log.LogError("LeLibrary: {Name} has no physical folder; {Path} must contain the anchor file", name, path);
            return null;
        }

        return _library.GetItemById(physicalId) as Folder;
    }

    /// <summary>Applies the desired library: creates, updates and removes items.</summary>
    /// <param name="desired">What the plan wants.</param>
    /// <param name="moviesRoot">The movies physical folder.</param>
    /// <param name="showsRoot">The shows physical folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What changed.</returns>
    public async Task<VirtualSyncResult> ApplyAsync(VirtualLibraryPlan desired, Folder moviesRoot, Folder showsRoot, string playBaseUrl, CancellationToken cancellationToken)
    {
        var existing = LoadExisting();
        // Grouped by parent: CreateItems takes one parent for the whole batch,
        // and SQLite refuses a single call with tens of thousands of rows.
        var created = new List<(BaseItem Parent, BaseItem Item)>();
        var updated = 0;
        var unchanged = 0;

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
                    _log.LogWarning("LeLibrary: no parent for {Path}", item.RelativePath);
                    continue;
                }

                // The item's path is the play URL, not a file: Jellyfin streams
                // a remote path as-is and never looks on disk.
                var playUrl = $"{playBaseUrl.TrimEnd('/')}/LeLibrary/play/{item.Key}/{Uri.EscapeDataString(item.FileName)}";
                var id = _library.GetNewItemId(playUrl, item.Item.GetType());
                byPath[item.RelativePath] = item.Item;

                if (existing.TryGetValue(id, out var current))
                {
                    if (NeedsUpdate(current, item.Item))
                    {
                        current.Name = item.Item.Name;
                        current.ProductionYear = item.Item.ProductionYear;
                        current.IndexNumber = item.Item.IndexNumber;
                        current.ParentIndexNumber = item.Item.ParentIndexNumber;
                        current.ProviderIds = item.Item.ProviderIds;
                        await current.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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

                item.Item.Id = id;
                item.Item.Path = playUrl;
                item.Item.ParentId = parent.Id;
                item.Item.DateCreated = DateTime.UtcNow;
                item.Item.DateModified = DateTime.UtcNow;
                item.Item.PresentationUniqueKey = id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
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
                    _library.CreateItems(batch.ToList(), parent, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                _library.CreateItems(batch, parent, cancellationToken);
            }
        }

        // Items we invent carry no images or overview of their own: Jellyfin
        // fetches those from the providers (TMDB, TVDB) using the ids we set.
        // Queue that work instead of doing it inline, or the sync would block
        // on thousands of HTTP calls.
        QueueMetadataRefresh(created);

        var removed = RemoveStale(desired, existing);
        _log.LogInformation(
            "LeLibrary: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Skipped} skipped",
            created.Count, updated, removed, unchanged, desired.Skipped);
        return new VirtualSyncResult(created.Count, updated, removed, unchanged, desired.Skipped);
    }

    private void QueueMetadataRefresh(IEnumerable<(BaseItem Parent, BaseItem Item)> created)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,
        };

        // Movies and series only: a series refresh pulls its seasons and
        // episodes with it, so queueing those separately triples the work.
        foreach (var (_, item) in created)
        {
            if (item is not (Movie or Series)) continue;
            _providers.QueueRefresh(item.Id, options, RefreshPriority.Normal);
        }
    }

    private Dictionary<Guid, BaseItem> LoadExisting()
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Episode,
                Jellyfin.Data.Enums.BaseItemKind.Series,
                Jellyfin.Data.Enums.BaseItemKind.Season,
            },
        };

        return _library.GetItemList(query)
            .Where(item => item.ProviderIds.ContainsKey(VirtualIdentity.ProviderId))
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static bool NeedsUpdate(BaseItem current, BaseItem wanted)
        => !string.Equals(current.Name, wanted.Name, StringComparison.Ordinal)
            || current.ProductionYear != wanted.ProductionYear
            || current.IndexNumber != wanted.IndexNumber
            || current.ParentIndexNumber != wanted.ParentIndexNumber;

    private int RemoveStale(VirtualLibraryPlan desired, Dictionary<Guid, BaseItem> existing)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in new[] { desired.Movies, desired.Series, desired.Seasons, desired.Episodes })
        {
            foreach (var item in group) wanted.Add(item.Key);
        }

        static int Rank(BaseItem item) => item switch
        {
            Episode => 0,
            Season => 1,
            Series => 2,
            _ => 3,
        };

        var stale = existing.Values
            .Where(item => !wanted.Contains(item.ProviderIds[VirtualIdentity.ProviderId]))
            .OrderBy(Rank)
            .ToList();

        foreach (var item in stale)
        {
            _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true);
        }

        return stale.Count;
    }
}
