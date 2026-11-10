using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LeLibrary.Model;

namespace Jellyfin.Plugin.LeLibrary.Virtual;

/// <summary>
/// The stable identity of a virtual item. The path is what Jellyfin stores and
/// finds the item by, and the id is derived from it so a resync updates the
/// same record instead of adding a duplicate.
/// </summary>
public static class VirtualIdentity
{
    /// <summary>The scheme every virtual path starts with.</summary>
    public const string Scheme = "lelibrary://";

    /// <summary>Path of a movie or episode, carrying its plan key.</summary>
    /// <param name="key">The plan key.</param>
    /// <returns>The virtual path.</returns>
    public static string PlayPath(string key) => Scheme + "play/" + key;

    /// <summary>Path of a series.</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="title">The title, used when there is no id.</param>
    /// <returns>The virtual path.</returns>
    public static string SeriesPath(int? tmdbId, string? title)
        => Scheme + "series/" + (tmdbId is > 0 ? tmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : Slug(title));

    /// <summary>Path of a season inside a series.</summary>
    /// <param name="seriesPath">The series path.</param>
    /// <param name="season">The season number.</param>
    /// <returns>The virtual path.</returns>
    public static string SeasonPath(string seriesPath, int season)
        => seriesPath + "/season/" + season.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The plan key inside a play path, or null.</summary>
    /// <param name="path">An item path.</param>
    /// <returns>The key.</returns>
    public static string? KeyFromPath(string? path)
    {
        const string prefix = Scheme + "play/";
        if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var key = path[prefix.Length..];
        return key.Length == 0 ? null : key;
    }

    /// <summary>
    /// A deterministic id for a path. Jellyfin keys user data (watched state,
    /// resume points) on the item id, so it must survive every resync.
    /// </summary>
    /// <param name="path">The virtual path.</param>
    /// <returns>The id.</returns>
    public static Guid IdFor(string path)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(path));
        return new Guid(hash);
    }

    private static string Slug(string? title)
    {
        var text = (title ?? "unknown").ToLowerInvariant();
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        return builder.ToString().Trim('-');
    }
}

/// <summary>An item ready to be written to Jellyfin's database.</summary>
/// <param name="Path">The virtual path (identity).</param>
/// <param name="ParentPath">The path of the parent item, or null for a library root.</param>
/// <param name="Item">The populated item.</param>
public sealed record VirtualItem(string Path, string? ParentPath, MediaBrowser.Controller.Entities.BaseItem Item);

/// <summary>A whole desired library, in dependency order: parents first.</summary>
/// <param name="Movies">Movies, parented to the movies library.</param>
/// <param name="Series">Series, parented to the shows library.</param>
/// <param name="Seasons">Seasons, parented to their series.</param>
/// <param name="Episodes">Episodes, parented to their season.</param>
/// <param name="Skipped">Plan entries that could not be represented.</param>
public sealed record VirtualLibraryPlan(
    IReadOnlyList<VirtualItem> Movies,
    IReadOnlyList<VirtualItem> Series,
    IReadOnlyList<VirtualItem> Seasons,
    IReadOnlyList<VirtualItem> Episodes,
    int Skipped);
