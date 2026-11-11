using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LeLibrary.Model;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.LeLibrary.Virtual;

/// <summary>Turns plan items into Jellyfin items. No database, no network.</summary>
public static class VirtualItemFactory
{
    /// <summary>Builds the whole desired library from a plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>Items in dependency order, with relative paths.</returns>
    public static VirtualLibraryPlan Build(JellyfinPlan plan)
    {
        var movies = new List<VirtualItem>();
        var episodes = new List<VirtualItem>();
        var series = new Dictionary<string, VirtualItem>(StringComparer.Ordinal);
        var seasons = new Dictionary<string, VirtualItem>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var item in plan.Items)
        {
            if (string.Equals(item.Kind, "movie", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(item.Title)) { skipped++; continue; }
                movies.Add(Movie(item));
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

                episodes.Add(Episode(item, seasonItem.RelativePath));
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

    private static VirtualItem Movie(PlanItem item)
    {
        var relative = VirtualIdentity.MediaRelativePath("movie", item.Title!, item.Year, null, null, item.Key);
        var movie = new Movie
        {
            Name = item.Title!,
            ProductionYear = item.Year,
            IsVirtualItem = false,
            IsInMixedFolder = true,
            ProviderIds = ProviderIds(item, VirtualIdentity.ProviderId, item.Key),
        };
        AttachArt(movie, item);
        return new VirtualItem(relative, null, item.Key, VirtualIdentity.PlayFileName(item.Path, item.Title, item.Year), movie, item.Key);
    }

    private static VirtualItem Series(PlanItem item, string key)
    {
        var relative = VirtualIdentity.SeriesRelativePath(item.SeriesTitle!, item.SeriesYear, key);
        var series = new Series
        {
            Name = item.SeriesTitle!,
            ProductionYear = item.SeriesYear,
            IsVirtualItem = false,
            ProviderIds = ProviderIds(item, VirtualIdentity.SeriesId, key),
        };
        AttachArt(series, item);
        return new VirtualItem(relative, null, key, VirtualIdentity.PlayFileName(null, item.SeriesTitle, item.SeriesYear), series, key);
    }

    private static VirtualItem Season(PlanItem item, string key, string seriesRelative)
    {
        var relative = VirtualIdentity.SeasonRelativePath(seriesRelative, item.Season!.Value);
        var season = new Season
        {
            Name = "Season " + item.Season.Value.ToString(CultureInfo.InvariantCulture),
            IndexNumber = item.Season,
            IsVirtualItem = false,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [VirtualIdentity.SeasonId] = key,
            },
        };
        return new VirtualItem(relative, seriesRelative, key, VirtualIdentity.PlayFileName(null, item.SeriesTitle, item.SeriesYear) + ".mkv", season, key);
    }

    private static VirtualItem Episode(PlanItem item, string seasonRelative)
    {
        var relative = VirtualIdentity.MediaRelativePath("episode", item.SeriesTitle!, item.SeriesYear, item.Season, item.Episode, item.Key);
        var episode = new Episode
        {
            Name = string.IsNullOrWhiteSpace(item.EpisodeTitle)
                ? "Episode " + item.Episode!.Value.ToString(CultureInfo.InvariantCulture)
                : item.EpisodeTitle,
            IndexNumber = item.Episode,
            ParentIndexNumber = item.Season,
            IsVirtualItem = false,
            ProviderIds = ProviderIds(item, VirtualIdentity.ProviderId, item.Key),
        };
        return new VirtualItem(relative, seasonRelative, item.Key, VirtualIdentity.PlayFileName(item.Path, item.SeriesTitle, item.SeriesYear), episode, item.Key);
    }

    /// <summary>Attaches the poster and backdrop LeLibrary chose to an item.</summary>
    /// <param name="item">The Jellyfin item.</param>
    /// <param name="plan">The plan item it came from.</param>
    public static void AttachArt(MediaBrowser.Controller.Entities.BaseItem item, PlanItem plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.Poster))
        {
            item.AddImage(new MediaBrowser.Controller.Entities.ItemImageInfo
            {
                Path = plan.Poster!,
                Type = MediaBrowser.Model.Entities.ImageType.Primary,
            });
        }
    }

    private static Dictionary<string, string> ProviderIds(PlanItem item, string name, string value)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [name] = value,
        };
        if (item.TmdbId is > 0) ids["Tmdb"] = item.TmdbId.Value.ToString(CultureInfo.InvariantCulture);
        if (item.TvdbId is > 0) ids["Tvdb"] = item.TvdbId.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(item.ImdbId)) ids["Imdb"] = item.ImdbId!;
        return ids;
    }
}
