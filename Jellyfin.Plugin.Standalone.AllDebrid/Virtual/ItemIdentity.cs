using System;
using System.Globalization;
using Jellyfin.Plugin.Standalone.AllDebrid.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Standalone.AllDebrid.Virtual;

/// <summary>
/// What an item <em>is</em>, as opposed to where it is served from.
///
/// Every kind of item needs an identity of its own. A series used to be
/// identified by the plan key of whichever episode arrived first, so removing
/// that episode removed the series with it; and an item's database id was
/// derived from its play URL, so changing the server URL rewrote every id and
/// orphaned the watch history.
///
/// The identity is what the sync matches on and what the id is derived from.
/// It never contains the server URL.
/// </summary>
public static class ItemIdentity
{
    /// <summary>The path segment the play endpoint lives under.</summary>
    private const string PlaySegment = "/StandaloneAllDebrid/play/";

    /// <summary>
    /// The identity of a series: its TMDB id when it has one, otherwise a slug
    /// of its title.
    /// </summary>
    /// <param name="item">Any episode of the series.</param>
    /// <returns>The series key.</returns>
    public static string SeriesKey(PlanItem item)
        => item.TmdbId is > 0
            ? "series:" + item.TmdbId.Value.ToString(CultureInfo.InvariantCulture)
            : "series:" + Slug(item.SeriesTitle);

    /// <summary>
    /// The identity of a season: the series' identity plus the season number.
    /// </summary>
    /// <param name="seriesKey">The series' identity.</param>
    /// <param name="season">The season number.</param>
    /// <returns>The season key.</returns>
    public static string SeasonKey(string seriesKey, int season)
        => seriesKey + ":season:" + season.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The key segment of a play URL: the part between <c>/StandaloneAllDebrid/play/</c>
    /// and the file name.
    /// </summary>
    /// <param name="path">The item's path.</param>
    /// <returns>The key, or null when the path is not a play URL.</returns>
    public static string? PlayKeyFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var at = path!.LastIndexOf(PlaySegment, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + PlaySegment.Length;
        var end = path.IndexOf('/', start);
        var key = end < 0 ? path[start..] : path[start..end];
        return key.Length == 0 ? null : key;
    }

    /// <summary>
    /// The identity of an item that already exists in the database.
    /// </summary>
    /// <param name="item">The row.</param>
    /// <returns>Its identity, or null when it has none.</returns>
    public static string? Of(BaseItem item)
    {
        if (item is Series)
        {
            // A legacy series row carries the plan key of one of its episodes
            // under the provider id, so that id must not be used. Its own
            // series id is used when it has one, and the play URL otherwise.
            return ProviderId(item, VirtualIdentity.SeriesId) ?? PlayKeyFromPath(item.Path);
        }

        if (item is Season)
        {
            return ProviderId(item, VirtualIdentity.SeasonId) ?? PlayKeyFromPath(item.Path);
        }

        return ProviderId(item, VirtualIdentity.ProviderId);
    }

    private static string? ProviderId(BaseItem item, string name)
    {
        if (item.ProviderIds is null)
        {
            return null;
        }

        return item.ProviderIds.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string Slug(string? title)
    {
        var value = string.IsNullOrWhiteSpace(title) ? "unknown" : title!.Trim().ToLowerInvariant();
        return value.Replace(' ', '-');
    }
}
