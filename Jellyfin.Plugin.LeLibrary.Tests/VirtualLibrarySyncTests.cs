using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

/// <summary>
/// The sync diff: what a sync creates, updates, keeps and removes. The
/// database is faked, so these tests need no Jellyfin server.
/// </summary>
public class VirtualLibrarySyncTests
{
    private static VirtualLibrary NewLibrary(ILibraryWriter writer)
        => new(
            null!,
            ProviderManager(),
            writer,
            // The diff never touches the file system; Jellyfin's directory
            // service only carries it.
            null!,
            NullLogger<VirtualLibrary>.Instance);

    private static Folder MoviesFolder() => new() { Id = Guid.NewGuid(), Name = "Movies" };

    private static Folder ShowsFolder() => new() { Id = Guid.NewGuid(), Name = "Shows" };

    private static VirtualLibraryPlan PlanWithOneMovie()
    {
        var movie = new Movie
        {
            Name = "The Matrix",
            ProductionYear = 1999,
            Container = "mkv",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [VirtualIdentity.ProviderId] = "torbox:1:2",
            },
        };

        return new VirtualLibraryPlan(
            new[] { new VirtualItem("The Matrix (1999)", null, "torbox:1:2", "The Matrix.mkv", movie, "torbox:1:2") },
            Array.Empty<VirtualItem>(),
            Array.Empty<VirtualItem>(),
            Array.Empty<VirtualItem>(),
            0);
    }

    /// <summary>A show with one season and three episodes.</summary>
    /// <param name="withFirstEpisode">Whether the first episode is still there.</param>
    /// <returns>The desired library.</returns>
    private static VirtualLibraryPlan PlanWithASeriesOfThreeEpisodes(bool withFirstEpisode = true)
    {
        var series = new Series
        {
            Name = "The Expanse",
            ProductionYear = 2015,
            Container = "mkv",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [VirtualIdentity.SeriesId] = "series:63639",
            },
        };

        const string SeriesPath = "Shows/The Expanse (2015)";
        const string SeasonPath = SeriesPath + "/Season 01";

        var season = new Season
        {
            Name = "Season 1",
            IndexNumber = 1,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [VirtualIdentity.SeasonId] = "series:63639:season:1",
            },
        };

        var episodes = new List<VirtualItem>();
        foreach (var number in Enumerable.Range(1, 3))
        {
            if (number == 1 && !withFirstEpisode)
            {
                continue;
            }

            var episode = new Episode
            {
                Name = "Episode " + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IndexNumber = number,
                ParentIndexNumber = 1,
                Container = "mkv",
                ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [VirtualIdentity.ProviderId] = "torbox:7:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            };

            episodes.Add(new VirtualItem(
                SeasonPath + "/The Expanse S01E0" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SeasonPath,
                "torbox:7:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "The Expanse S01E0" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mkv",
                episode,
                "torbox:7:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new VirtualLibraryPlan(
            Array.Empty<VirtualItem>(),
            new[] { new VirtualItem(SeriesPath, null, "series:63639", "The Expanse.mkv", series, "series:63639") },
            new[] { new VirtualItem(SeasonPath, SeriesPath, "series:63639:season:1", "The Expanse.mkv", season, "series:63639:season:1") },
            episodes,
            0);
    }

    [Fact]
    public async Task AFreshLibraryCreatesEveryItem()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
        var result = await library.ApplyAsync(PlanWithOneMovie(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public async Task AResyncChangesNothing()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
        var plan = PlanWithOneMovie();
        await library.ApplyAsync(plan, MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        var second = await library.ApplyAsync(plan, MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Removed);
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public async Task TheServerUrlDoesNotChangeTheItemId()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
        await library.ApplyAsync(PlanWithOneMovie(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        var before = Assert.Single(writer.Items);
        await library.ApplyAsync(PlanWithOneMovie(), MoviesFolder(), ShowsFolder(), "http://elsewhere:8096", default);
        var after = Assert.Single(writer.Items, i => i.GetType() == typeof(Movie));
        Assert.Equal(before.Id, after.Id);
    }

    [Fact]
    public async Task TheServerUrlDoesNotDuplicateTheLibrary()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
        await library.ApplyAsync(PlanWithOneMovie(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        await library.ApplyAsync(PlanWithOneMovie(), MoviesFolder(), ShowsFolder(), "http://elsewhere:8096", default);
        Assert.Single(writer.Items, i => i.GetType() == typeof(Movie));
    }

    [Fact]
    public async Task RemovingOneEpisodeDoesNotRemoveTheSeries()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
        await library.ApplyAsync(PlanWithASeriesOfThreeEpisodes(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);

        var result = await library.ApplyAsync(
            PlanWithASeriesOfThreeEpisodes(withFirstEpisode: false),
            MoviesFolder(),
            ShowsFolder(),
            "http://host:8096",
            default);

        Assert.Single(writer.Items, i => i.GetType() == typeof(Series));
        Assert.Equal(2, writer.Items.Count(i => i.GetType() == typeof(Episode)));
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public async Task AResyncKeepsTheContainer()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);

        // A fresh plan each time, the way a sync builds one: the first apply
        // fills the container in on the item it created, and the second sync
        // must not wipe it.
        VirtualLibraryPlan FreshPlan() => VirtualItemFactory.Build(new JellyfinPlan
        {
            Items = new[]
            {
                new PlanItem
                {
                    Key = "torbox:1:2", Kind = "movie", Library = "movies",
                    Title = "The Matrix", Year = 1999, Path = "The Matrix (1999)/The Matrix (1999).strm",
                },
            },
        });

        await library.ApplyAsync(FreshPlan(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        await library.ApplyAsync(FreshPlan(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);

        var item = Assert.Single(writer.Items, i => i.GetType() == typeof(Movie));
        Assert.Equal("mkv", item.Container);
    }

    [Fact]
    public async Task ARealMp4IsDeclaredAsMp4()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
        var plan = VirtualItemFactory.Build(
            new JellyfinPlan
            {
                Items = new[]
                {
                    new PlanItem
                    {
                        Key = "torbox:1:2", Kind = "movie", Library = "movies",
                        Title = "Movie", Year = 2019, Path = "Movie/Movie.strm",
                    },
                },
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["torbox:1:2"] = "Movie.2019.1080p.mp4" });

        await library.ApplyAsync(plan, MoviesFolder(), ShowsFolder(), "http://host:8096", default);

        var item = Assert.Single(writer.Items, i => i.GetType() == typeof(Movie));
        Assert.Equal("mp4", item.Container);
        Assert.EndsWith(".mp4", item.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothEpisodesOfOneFileAreCreated()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
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

        await library.ApplyAsync(plan, MoviesFolder(), ShowsFolder(), "http://host:8096", default);

        Assert.Equal(2, writer.Items.Count(i => i.GetType() == typeof(Episode)));
        Assert.Single(writer.Items, i => i.GetType() == typeof(Series));
    }

    [Fact]
    public async Task BothEpisodesOfOneFileAreMatchedAgainOnTheNextSync()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);

        VirtualLibraryPlan FreshPlan() => VirtualItemFactory.Build(new JellyfinPlan
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

        await library.ApplyAsync(FreshPlan(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        var second = await library.ApplyAsync(FreshPlan(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);

        Assert.Equal(2, writer.Items.Count(i => i.GetType() == typeof(Episode)));
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Removed);
    }

    /// <summary>
    /// A provider manager that only answers the one call the sync makes.
    /// </summary>
    private static IProviderManager ProviderManager()
        => DispatchProxy.Create<IProviderManager, ProviderManagerProxy>();
}

/// <summary>
/// Answers <c>QueueRefresh</c> and nothing else: the sync must not depend on
/// Jellyfin's provider machinery to be tested.
/// </summary>
public class ProviderManagerProxy : DispatchProxy
{
    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "QueueRefresh")
        {
            return null;
        }

        throw new NotSupportedException(targetMethod?.Name ?? "unknown");
    }
}
