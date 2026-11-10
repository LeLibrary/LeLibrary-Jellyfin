using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeLibrary.Virtual;

/// <summary>What a sync changed in Jellyfin's library.</summary>
/// <param name="Added">Items created.</param>
/// <param name="Updated">Items whose metadata changed.</param>
/// <param name="Removed">Items deleted because the plan no longer has them.</param>
/// <param name="Unchanged">Items already right.</param>
/// <param name="Skipped">Plan entries that could not be represented.</param>
public sealed record VirtualSyncResult(int Added, int Updated, int Removed, int Unchanged, int Skipped)
{
    /// <summary>Gets the total number of changes.</summary>
    public int Changes => Added + Updated + Removed;
}

/// <summary>
/// Writes the plan into Jellyfin as items: no files, no library scan. This is
/// the zurg model - the plugin owns the records, Jellyfin just shows them.
/// </summary>
public sealed class VirtualLibrary
{
    /// <summary>Library name for movies.</summary>
    public const string MoviesLibrary = "LeLibrary Movies";

    /// <summary>Library name for shows.</summary>
    public const string ShowsLibrary = "LeLibrary Shows";

    private readonly ILibraryManager _library;
    private readonly ILogger<VirtualLibrary> _log;

    /// <summary>Initializes a new instance of the <see cref="VirtualLibrary"/> class.</summary>
    /// <param name="library">The library manager.</param>
    /// <param name="log">The logger.</param>
    public VirtualLibrary(ILibraryManager library, ILogger<VirtualLibrary> log)
    {
        _library = library;
        _log = log;
    }

    /// <summary>
    /// Makes sure the two libraries exist and returns their folders.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folders.</returns>
    public async Task<(CollectionFolder Movies, CollectionFolder Shows)> EnsureLibrariesAsync(CancellationToken cancellationToken)
    {
        var existing = _library.GetVirtualFolders()
            .Select(folder => folder.ItemId)
            .Select(id => _library.GetItemById(id))
            .OfType<CollectionFolder>()
            .ToList();

        var movies = existing.FirstOrDefault(f => string.Equals(f.Name, MoviesLibrary, StringComparison.OrdinalIgnoreCase));
        if (movies is null)
        {
            await _library.AddVirtualFolder(MoviesLibrary, CollectionTypeOptions.movies, new MediaBrowser.Model.Configuration.LibraryOptions(), false)
                .ConfigureAwait(false);
        }

        var shows = existing.FirstOrDefault(f => string.Equals(f.Name, ShowsLibrary, StringComparison.OrdinalIgnoreCase));
        if (shows is null)
        {
            await _library.AddVirtualFolder(ShowsLibrary, CollectionTypeOptions.tvshows, new MediaBrowser.Model.Configuration.LibraryOptions(), false)
                .ConfigureAwait(false);
        }

        var refreshed = _library.GetVirtualFolders()
            .Select(folder => folder.ItemId)
            .Select(id => _library.GetItemById(id))
            .OfType<CollectionFolder>()
            .ToList();
        var moviesFolder = refreshed.First(f => string.Equals(f.Name, MoviesLibrary, StringComparison.OrdinalIgnoreCase));
        var showsFolder = refreshed.First(f => string.Equals(f.Name, ShowsLibrary, StringComparison.OrdinalIgnoreCase));
        _ = cancellationToken;
        return (moviesFolder, showsFolder);
    }

    /// <summary>
    /// Applies the desired library: creates, updates and removes items.
    /// </summary>
    /// <param name="desired">The library the plan wants.</param>
    /// <param name="movies">The movies library folder.</param>
    /// <param name="shows">The shows library folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What changed.</returns>
    public Task<VirtualSyncResult> ApplyAsync(
        VirtualLibraryPlan desired,
        CollectionFolder movies,
        CollectionFolder shows,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        var (moviesAdded, moviesUpdated, moviesUnchanged) = ApplyGroup(desired.Movies, movies);
        added += moviesAdded;
        updated += moviesUpdated;
        unchanged += moviesUnchanged;

        // Parents before children: Jellyfin resolves ParentId when a child is
        // created, so a season whose series does not exist yet is an orphan.
        var seriesParents = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var group in new[] { desired.Series, desired.Seasons, desired.Episodes })
        {
            foreach (var item in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = ResolveParent(item, seriesParents, shows, movies);
                if (parent is null)
                {
                    _log.LogWarning("LeLibrary: no parent for {Path}", item.Path);
                    continue;
                }

                var (created, changed) = Upsert(item, parent);
                if (created) added++;
                else if (changed) updated++;
                else unchanged++;

                if (item.Item is MediaBrowser.Controller.Entities.TV.Series)
                {
                    seriesParents[item.Path] = item.Item;
                }
            }
        }

        var removed = RemoveStale(desired);
        _log.LogInformation("LeLibrary: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Skipped} skipped", added, updated, removed, unchanged, desired.Skipped);
        return Task.FromResult(new VirtualSyncResult(added, updated, removed, unchanged, desired.Skipped));
    }

    private (int Added, int Updated, int Unchanged) ApplyGroup(IReadOnlyList<VirtualItem> items, BaseItem parent)
    {
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var item in items)
        {
            var (created, changed) = Upsert(item, parent);
            if (created) added++;
            else if (changed) updated++;
            else unchanged++;
        }

        return (added, updated, unchanged);
    }

    private BaseItem? ResolveParent(VirtualItem item, Dictionary<string, BaseItem> seriesParents, CollectionFolder shows, CollectionFolder movies)
    {
        if (item.ParentPath is null)
        {
            return item.Item is MediaBrowser.Controller.Entities.TV.Series ? shows : movies;
        }

        if (seriesParents.TryGetValue(item.ParentPath, out var known)) return known;
        var existing = _library.FindByPath(item.ParentPath, null);
        if (existing is not null && item.Item is MediaBrowser.Controller.Entities.TV.Series) seriesParents[item.ParentPath] = existing;
        return existing;
    }

    private (bool Created, bool Changed) Upsert(VirtualItem item, BaseItem parent)
    {
        var existing = _library.FindByPath(item.Path, null);
        if (existing is null)
        {
            _library.CreateItem(item.Item, parent);
            return (true, false);
        }

        var changed = false;
        if (!string.Equals(existing.Name, item.Item.Name, StringComparison.Ordinal))
        {
            existing.Name = item.Item.Name;
            changed = true;
        }

        if (existing.ProductionYear != item.Item.ProductionYear)
        {
            existing.ProductionYear = item.Item.ProductionYear;
            changed = true;
        }

        if (item.Item.ProviderIds.Count > 0 && !SameIds(existing.ProviderIds, item.Item.ProviderIds))
        {
            existing.ProviderIds = item.Item.ProviderIds;
            changed = true;
        }

        if (changed)
        {
            _library.UpdateItemAsync(existing, parent, ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
        }

        return (false, changed);
    }

    private static bool SameIds(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var pair in b)
        {
            if (!a.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private int RemoveStale(VirtualLibraryPlan desired)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in new[] { desired.Movies, desired.Series, desired.Seasons, desired.Episodes })
        {
            foreach (var item in group) wanted.Add(item.Path);
        }

        // Children before parents so a delete never orphans a subtree.
        var order = new[]
        {
            Jellyfin.Data.Enums.BaseItemKind.Episode,
            Jellyfin.Data.Enums.BaseItemKind.Season,
            Jellyfin.Data.Enums.BaseItemKind.Series,
            Jellyfin.Data.Enums.BaseItemKind.Movie,
        };
        var removed = 0;
        foreach (var kind in order)
        {
            var stale = _library.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { kind },
            }).Where(item => item.Path is not null
                && item.Path.StartsWith(VirtualIdentity.Scheme, StringComparison.Ordinal)
                && !wanted.Contains(item.Path))
                .ToList();

            foreach (var item in stale)
            {
                _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true);
                removed++;
            }
        }

        return removed;
    }
}
