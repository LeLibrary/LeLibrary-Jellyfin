using System;
using System.Linq;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Virtual;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class VirtualLibraryTests
{
    private static PlanItem Movie(string key, string title, int year) => new()
    {
        Key = key,
        Kind = "movie",
        Library = "movies",
        Title = title,
        Year = year,
        TmdbId = 603,
    };

    private static PlanItem Episode(string key, string series, int? seriesYear, int season, int episode, int? tmdbId = 63639) => new()
    {
        Key = key,
        Kind = "episode",
        Library = "shows",
        SeriesTitle = series,
        SeriesYear = seriesYear,
        Season = season,
        Episode = episode,
        EpisodeTitle = "Remember the Cant",
        TmdbId = tmdbId,
        TvdbId = 280619,
    };

    [Fact]
    public void AMoviePathStaysInsideTheLibraryFolder()
    {
        var path = VirtualIdentity.MediaRelativePath("movie", "The Matrix", 1999, null, null, "torbox:1:2");
        Assert.StartsWith("The Matrix (1999)/", path, StringComparison.Ordinal);
        Assert.EndsWith(".mkv", path, StringComparison.Ordinal);
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        Assert.DoesNotContain("://", path, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEpisodePathCarriesItsSeasonAndEpisode()
    {
        var path = VirtualIdentity.MediaRelativePath("episode", "The Expanse", 2015, 1, 3, "torbox:7:2");
        Assert.Contains("Season 01", path, StringComparison.Ordinal);
        Assert.Contains("S01E03", path, StringComparison.Ordinal);
    }

    [Fact]
    public void AMovieBecomesOneItemWithItsIds()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan { Items = new[] { Movie("torbox:1:2", "The Matrix", 1999) } });

        var movie = Assert.Single(plan.Movies);
        Assert.Equal("The Matrix", movie.Item.Name);
        Assert.Equal(1999, movie.Item.ProductionYear);
        // Jellyfin treats a virtual item as not-part-of-a-library; ours are ordinary rows.
        Assert.False(movie.Item.IsVirtualItem);
        Assert.Equal("torbox:1:2", movie.Item.ProviderIds[VirtualIdentity.ProviderId]);
        Assert.Equal("603", movie.Item.ProviderIds["Tmdb"]);
        Assert.StartsWith("The Matrix (1999)", movie.RelativePath, StringComparison.Ordinal);
        Assert.Empty(plan.Series);
        Assert.Empty(plan.Episodes);
    }

    [Fact]
    public void EpisodesGetOneSeriesOneSeasonAndTheirOwnItems()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[]
            {
                Episode("torbox:7:1", "The Expanse", 2015, 1, 1),
                Episode("torbox:7:2", "The Expanse", 2015, 1, 3),
                Episode("torbox:7:3", "The Expanse", 2015, 2, 1),
            },
        });

        Assert.Single(plan.Series);
        Assert.Equal(2, plan.Seasons.Count);
        Assert.Equal(3, plan.Episodes.Count);

        var series = plan.Series[0];
        Assert.Contains("The Expanse (2015)", series.RelativePath, StringComparison.Ordinal);
        Assert.Equal("The Expanse", series.Item.Name);

        var seasonOne = plan.Seasons.Single(s => s.RelativePath.EndsWith("Season 01", StringComparison.Ordinal));
        Assert.Equal(series.RelativePath, seasonOne.ParentRelativePath);

        var episode = plan.Episodes[1];
        Assert.Contains("S01E03", episode.RelativePath, StringComparison.Ordinal);
        Assert.Equal(seasonOne.RelativePath, episode.ParentRelativePath);
        Assert.Equal(3, episode.Item.IndexNumber);
        Assert.Equal(1, episode.Item.ParentIndexNumber);
        Assert.Equal("Remember the Cant", episode.Item.Name);
    }

    [Fact]
    public void TwoShowsWithTheSameTitleDoNotShareASeries()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[]
            {
                Episode("torbox:1:1", "Battlestar Galactica", 1978, 1, 1, tmdbId: 131),
                Episode("torbox:2:1", "Battlestar Galactica", 2004, 1, 1, tmdbId: 1972),
            },
        });

        Assert.Equal(2, plan.Series.Count);
        Assert.Equal(2, plan.Seasons.Count);
    }

    [Fact]
    public void AnEpisodeWithoutASeasonOrSeriesIsSkippedNotGuessed()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[]
            {
                new PlanItem { Key = "torbox:9:9", Kind = "episode", Season = 1, SeriesTitle = "Show", TmdbId = 1 },
                new PlanItem { Key = "torbox:9:8", Kind = "episode", Season = null, Episode = 2, SeriesTitle = "Show", TmdbId = 1 },
                new PlanItem { Key = "torbox:9:7", Kind = "movie", Title = "", Year = 2000 },
                new PlanItem { Key = "torbox:9:6", Kind = "weird" },
            },
        });

        Assert.Empty(plan.Movies);
        Assert.Empty(plan.Episodes);
        Assert.Equal(4, plan.Skipped);
    }

    [Fact]
    public void TheSeriesPathFallsBackToTheTitleWithoutATmdbId()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[] { Episode("torbox:1:1", "Some Show", 2020, 1, 1, tmdbId: null) },
        });

        Assert.Contains("Some Show (2020)", plan.Series[0].RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ASeriesAndItsSeasonsCarryTheirOwnIds()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[] { Episode("torbox:7:2", "The Expanse", 2015, 1, 3) },
        });

        var series = Assert.Single(plan.Series);
        Assert.Equal("series:63639", series.Identity);
        Assert.Equal("series:63639", series.Item.ProviderIds[VirtualIdentity.SeriesId]);
        // The series keeps the external ids: Jellyfin's own providers look the
        // title up by them.
        Assert.Equal("63639", series.Item.ProviderIds["Tmdb"]);
        // It must not carry the plan key of one of its episodes.
        Assert.False(series.Item.ProviderIds.ContainsKey(VirtualIdentity.ProviderId));

        var season = Assert.Single(plan.Seasons);
        Assert.Equal("series:63639:season:1", season.Identity);
        Assert.Equal("series:63639:season:1", season.Item.ProviderIds[VirtualIdentity.SeasonId]);
    }

    [Fact]
    public void AMovieAndAnEpisodeKeepTheirPlanKeyAsTheirIdentity()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[]
            {
                Movie("torbox:1:2", "The Matrix", 1999),
                Episode("torbox:7:2", "The Expanse", 2015, 1, 3),
            },
        });

        var movie = Assert.Single(plan.Movies);
        Assert.Equal("torbox:1:2", movie.Identity);
        Assert.Equal("torbox:1:2", movie.Item.ProviderIds[VirtualIdentity.ProviderId]);

        var episode = Assert.Single(plan.Episodes);
        Assert.Equal("torbox:7:2", episode.Identity);
        Assert.Equal("torbox:7:2", episode.Item.ProviderIds[VirtualIdentity.ProviderId]);
    }

    [Fact]
    public void AMultiEpisodeFileBecomesOneItemPerEpisode()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[]
            {
                new PlanItem
                {
                    Key = "torbox:96523770:13", Kind = "episode", Library = "shows",
                    SeriesTitle = "2 Broke Girls", SeriesYear = 2011, TmdbId = 39340,
                    Season = 6, Episode = 1, EpisodeEnd = 2,
                },
            },
        });

        Assert.Equal(2, plan.Episodes.Count);
        Assert.Equal(new[] { 1, 2 }, plan.Episodes.Select(e => e.Item.IndexNumber!.Value).OrderBy(n => n).ToArray());
        Assert.Equal("torbox:96523770:13", plan.Episodes[0].Identity);
        Assert.Equal("torbox:96523770:13~E2", plan.Episodes[1].Identity);
        Assert.NotEqual(plan.Episodes[0].FileName, plan.Episodes[1].FileName);
    }

    [Fact]
    public void ASingleEpisodeFileIsOneItem()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[]
            {
                new PlanItem { Key = "torbox:1:2", Kind = "episode", Library = "shows", SeriesTitle = "Show", Season = 1, Episode = 3 },
            },
        });

        Assert.Single(plan.Episodes);
        Assert.Equal("torbox:1:2", plan.Episodes[0].Identity);
    }
}
