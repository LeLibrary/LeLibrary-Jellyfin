using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Standalone.RealDebrid.Model;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Standalone.RealDebrid.Virtual;

/// <summary>Turns plan items into Jellyfin items. No database, no network.</summary>
public static class VirtualItemFactory
{
    /// <summary>Builds the whole desired library from a plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="realFileNames">The provider's real file name for each plan key, when it is known.</param>
    /// <returns>Items in dependency order, with relative paths.</returns>
    public static VirtualLibraryPlan Build(JellyfinPlan plan, IReadOnlyDictionary<string, string>? realFileNames = null)
    {
        var movies = new List<VirtualItem>();
        var episodes = new List<VirtualItem>();
        var series = new Dictionary<string, VirtualItem>(StringComparer.Ordinal);
        var seasons = new Dictionary<string, VirtualItem>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var item in plan.Items)
        {
            var realFileName = realFileNames is null ? null : realFileNames.GetValueOrDefault(item.Key);

            if (string.Equals(item.Kind, "movie", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(item.Title)) { skipped++; continue; }
                movies.Add(Movie(item, realFileName));
                continue;
            }

            if (string.Equals(item.Kind, "episode", StringComparison.OrdinalIgnoreCase))
            {
                if (item.Season is null || item.Episode is null || string.IsNullOrWhiteSpace(item.SeriesTitle))
                {
                    skipped++;
                    continue;
                }

                var seriesKey = ItemIdentity.SeriesKey(item);
                if (!series.TryGetValue(seriesKey, out var seriesItem))
                {
                    seriesItem = Series(item, seriesKey);
                    series[seriesKey] = seriesItem;
                }

                var seasonKey = ItemIdentity.SeasonKey(seriesKey, item.Season.Value);
                if (!seasons.TryGetValue(seasonKey, out var seasonItem))
                {
                    seasonItem = Season(item, seasonKey, seriesItem.RelativePath);
                    seasons[seasonKey] = seasonItem;
                }

                // Jellyfin represents a multi-episode file as one item with an
                // end number, so one playback represents the whole file.
                var fileName = VirtualIdentity.PlayFileName(item.Path, item.SeriesTitle, item.SeriesYear, realFileName);
                episodes.Add(Episode(item, item.Episode.Value, item.Key, fileName, seasonItem.RelativePath));

                continue;
            }

            skipped++;
        }

        return new VirtualLibraryPlan(movies, series.Values.ToList(), seasons.Values.ToList(), episodes, skipped);
    }

    /// <summary>The stable key of a series.</summary>
    /// <param name="item">Any episode of the series.</param>
    /// <returns>The key.</returns>
    public static string SeriesKey(PlanItem item) => ItemIdentity.SeriesKey(item);

    private static VirtualItem Movie(PlanItem item, string? realFileName)
    {
        var relative = VirtualIdentity.MediaRelativePath("movie", item.Title!, item.Year, null, null, item.Key);
        var fileName = VirtualIdentity.PlayFileName(item.Path, item.Title, item.Year, realFileName);
        var movie = new Movie
        {
            Name = item.Title!,
            ProductionYear = item.Year,
            IsVirtualItem = false,
            IsInMixedFolder = true,
            Container = VirtualIdentity.ContainerOf(fileName),
            ProviderIds = ProviderIds(item, VirtualIdentity.ProviderId, item.Key),
        };
        return new VirtualItem(relative, null, item.Key, fileName, movie, item.Key);
    }

    private static VirtualItem Series(PlanItem item, string key)
    {
        var relative = VirtualIdentity.SeriesRelativePath(item.SeriesTitle!, item.SeriesYear, key);
        var fileName = VirtualIdentity.PlayFileName(null, item.SeriesTitle, item.SeriesYear);
        var series = new Series
        {
            Name = item.SeriesTitle!,
            ProductionYear = item.SeriesYear,
            IsVirtualItem = false,
            ProviderIds = ProviderIds(item, VirtualIdentity.SeriesId, key, VirtualIdentity.ShowId),
        };
        return new VirtualItem(relative, null, key, fileName, series, key);
    }

    private static VirtualItem Season(PlanItem item, string key, string seriesRelative)
    {
        var relative = VirtualIdentity.SeasonRelativePath(seriesRelative, item.Season!.Value);
        var fileName = VirtualIdentity.PlayFileName(null, item.SeriesTitle, item.SeriesYear) + ".mkv";
        var season = new Season
        {
            Name = "Season " + item.Season.Value.ToString(CultureInfo.InvariantCulture),
            IndexNumber = item.Season,
            IsVirtualItem = false,
            ProviderIds = ProviderIds(item, VirtualIdentity.SeasonId, key, VirtualIdentity.ShowId),
        };
        return new VirtualItem(relative, seriesRelative, key, fileName, season, key);
    }

    private static VirtualItem Episode(PlanItem item, int number, string identity, string fileName, string seasonRelative)
    {
        var relative = VirtualIdentity.MediaRelativePath("episode", item.SeriesTitle!, item.SeriesYear, item.Season, number, item.Key);
        var episode = new Episode
        {
            // The plan's title belongs to the first episode of the file.
            Name = number == item.Episode && !string.IsNullOrWhiteSpace(item.EpisodeTitle)
                ? item.EpisodeTitle!
                : "Episode " + number.ToString(CultureInfo.InvariantCulture),
            IndexNumber = number,
            IndexNumberEnd = item.EpisodeEnd > number ? item.EpisodeEnd : null,
            ParentIndexNumber = item.Season,
            IsVirtualItem = false,
            Container = VirtualIdentity.ContainerOf(fileName),
            // The provider id holds the identity, so the row can be found
            // again by what it is.
            ProviderIds = ProviderIds(item, VirtualIdentity.ProviderId, identity, VirtualIdentity.ShowId),
        };
        return new VirtualItem(relative, seasonRelative, item.Key, fileName, episode, identity);
    }

    private static Dictionary<string, string> ProviderIds(PlanItem item, string name, string value, string? showIdName = null)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [name] = value,
        };
        if (showIdName is not null && item.TmdbId is > 0)
        {
            ids[showIdName] = item.TmdbId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (item.TmdbId is > 0) ids["Tmdb"] = item.TmdbId.Value.ToString(CultureInfo.InvariantCulture);
        if (item.TvdbId is > 0) ids["Tvdb"] = item.TvdbId.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(item.ImdbId)) ids["Imdb"] = item.ImdbId!;
        return ids;
    }
}
