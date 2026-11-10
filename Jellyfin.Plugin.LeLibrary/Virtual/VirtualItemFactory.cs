using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LeLibrary.Model;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.LeLibrary.Virtual;

/// <summary>
/// Turns plan items into Jellyfin items. Pure: no database, no network, so the
/// identity rules are pinned by tests.
/// </summary>
public static class VirtualItemFactory
{
    /// <summary>Builds the whole desired library from a plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>Items in dependency order (series, seasons, episodes) plus movies.</param>
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

                var seriesPath = VirtualIdentity.SeriesPath(item.TmdbId, item.SeriesTitle);
                if (!series.TryGetValue(seriesPath, out var seriesItem))
                {
                    seriesItem = Series(item, seriesPath);
                    series[seriesPath] = seriesItem;
                }

                var seasonPath = VirtualIdentity.SeasonPath(seriesPath, item.Season.Value);
                if (!seasons.TryGetValue(seasonPath, out var seasonItem))
                {
                    seasonItem = Season(item, seasonPath, seriesPath);
                    seasons[seasonPath] = seasonItem;
                }

                episodes.Add(Episode(item, seriesPath, seasonPath));
                continue;
            }

            skipped++;
        }

        return new VirtualLibraryPlan(
            movies,
            series.Values.ToList(),
            seasons.Values.ToList(),
            episodes,
            skipped);
    }

    private static VirtualItem Movie(PlanItem item)
    {
        var path = VirtualIdentity.PlayPath(item.Key);
        var movie = new Movie
        {
            Id = VirtualIdentity.IdFor(path),
            Path = path,
            Name = item.Title!,
            OriginalTitle = item.Title,
            ProductionYear = item.Year,
            IsVirtualItem = true,
            ProviderIds = ProviderIds(item),
            Overview = null,
        };
        return new VirtualItem(path, null, movie);
    }

    private static VirtualItem Series(PlanItem item, string path)
    {
        var series = new Series
        {
            Id = VirtualIdentity.IdFor(path),
            Path = path,
            Name = item.SeriesTitle!,
            ProductionYear = item.SeriesYear,
            IsVirtualItem = true,
            ProviderIds = ProviderIds(item),
        };
        return new VirtualItem(path, null, series);
    }

    private static VirtualItem Season(PlanItem item, string path, string seriesPath)
    {
        var number = item.Season!.Value;
        var season = new Season
        {
            Id = VirtualIdentity.IdFor(path),
            Path = path,
            Name = "Season " + number.ToString(CultureInfo.InvariantCulture),
            IndexNumber = number,
            SeriesId = VirtualIdentity.IdFor(seriesPath),
            IsVirtualItem = true,
        };
        return new VirtualItem(path, seriesPath, season);
    }

    private static VirtualItem Episode(PlanItem item, string seriesPath, string seasonPath)
    {
        var path = VirtualIdentity.PlayPath(item.Key);
        var episode = new Episode
        {
            Id = VirtualIdentity.IdFor(path),
            Path = path,
            Name = string.IsNullOrWhiteSpace(item.EpisodeTitle)
                ? "Episode " + item.Episode!.Value.ToString(CultureInfo.InvariantCulture)
                : item.EpisodeTitle,
            IndexNumber = item.Episode,
            ParentIndexNumber = item.Season,
            SeriesId = VirtualIdentity.IdFor(seriesPath),
            SeasonId = VirtualIdentity.IdFor(seasonPath),
            IsVirtualItem = true,
            ProviderIds = ProviderIds(item),
        };
        return new VirtualItem(path, seasonPath, episode);
    }

    private static Dictionary<string, string> ProviderIds(PlanItem item)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TmdbId is > 0) ids["Tmdb"] = item.TmdbId.Value.ToString(CultureInfo.InvariantCulture);
        if (item.TvdbId is > 0) ids["Tvdb"] = item.TvdbId.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(item.ImdbId)) ids["Imdb"] = item.ImdbId!;
        return ids;
    }
}
