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
    public void ItemIdsSurviveEveryResync()
    {
        // Jellyfin keys watched state on the id: a new id every sync would lose
        // the user's progress.
        Assert.Equal(VirtualIdentity.IdFor("lelibrary://play/torbox:1:2"), VirtualIdentity.IdFor("lelibrary://play/torbox:1:2"));
        Assert.NotEqual(VirtualIdentity.IdFor("lelibrary://play/torbox:1:2"), VirtualIdentity.IdFor("lelibrary://play/torbox:1:3"));
        Assert.NotEqual(Guid.Empty, VirtualIdentity.IdFor("lelibrary://play/torbox:1:2"));
    }

    [Theory]
    [InlineData("lelibrary://play/torbox:12:34", "torbox:12:34")]
    [InlineData("lelibrary://series/603", null)]
    [InlineData("lelibrary://play/", null)]
    [InlineData("", null)]
    public void APlayPathCarriesItsKey(string path, string? expected)
    {
        Assert.Equal(expected, VirtualIdentity.KeyFromPath(path));
    }

    [Fact]
    public void AMovieBecomesOneItemWithItsIds()
    {
        var plan = VirtualItemFactory.Build(new JellyfinPlan { Items = new[] { Movie("torbox:1:2", "The Matrix", 1999) } });

        var movie = Assert.Single(plan.Movies);
        Assert.Equal("The Matrix", movie.Item.Name);
        Assert.Equal(1999, movie.Item.ProductionYear);
        Assert.True(movie.Item.IsVirtualItem);
        Assert.Equal("603", movie.Item.ProviderIds["Tmdb"]);
        Assert.Equal("lelibrary://play/torbox:1:2", movie.Path);
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
        Assert.Equal("lelibrary://series/63639", series.Path);
        Assert.Equal("The Expanse", series.Item.Name);

        var seasonOne = plan.Seasons.Single(s => s.Path.EndsWith("/season/1", StringComparison.Ordinal));
        Assert.Equal(series.Path, seasonOne.ParentPath);

        var episode = plan.Episodes[1];
        Assert.Equal("lelibrary://play/torbox:7:2", episode.Path);
        Assert.Equal(seasonOne.Path, episode.ParentPath);
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

        Assert.Equal("lelibrary://series/some-show", plan.Series[0].Path);
    }
}
