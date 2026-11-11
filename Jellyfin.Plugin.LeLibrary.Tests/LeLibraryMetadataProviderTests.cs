using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Providers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class LeLibraryMetadataProviderTests
{
    private static MetadataDocument Document() => new()
    {
        Titles = new Dictionary<string, MetadataTitle>(StringComparer.Ordinal)
        {
            ["movie:603"] = new()
            {
                Kind = "movie",
                Title = "The Matrix",
                Year = 1999,
                Overview = "A hacker learns.",
                Runtime = 136,
                Genres = new[] { "Action", "Science Fiction" },
                Rating = 8.2,
                Certification = "R",
                ReleaseDate = "1999-03-30",
            },
            ["tv:63639"] = new()
            {
                Kind = "tv",
                Title = "The Expanse",
                Year = 2015,
                Overview = "A missing girl.",
                Genres = new[] { "Drama" },
                Episodes = new Dictionary<string, MetadataEpisode>(StringComparer.Ordinal)
                {
                    ["1:3"] = new()
                    {
                        Title = "Remember the Cant",
                        Overview = "The Rocinante answers.",
                        Runtime = 45,
                        AirDate = "2015-11-20",
                    },
                },
            },
        },
    };

    private static MetadataRefreshOptions Options() => new(new DirectoryService(null!));

    private static LeLibraryMetadataProvider Provider() => new(new MetadataHolder { Document = Document() });

    [Fact]
    public async Task AMovieGetsItsOverviewRuntimeAndGenres()
    {
        var movie = new Movie
        {
            Name = "The Matrix",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "603" },
        };

        var update = await Provider().FetchAsync(movie, Options(), default);

        Assert.Equal(ItemUpdateType.MetadataEdit, update);
        Assert.Equal("A hacker learns.", movie.Overview);
        Assert.Equal(TimeSpan.FromMinutes(136).Ticks, movie.RunTimeTicks);
        Assert.Equal(new[] { "Action", "Science Fiction" }, movie.Genres);
        Assert.Equal(8.2f, movie.CommunityRating!.Value, 2);
        Assert.Equal("R", movie.OfficialRating);
        Assert.Equal(new DateTime(1999, 3, 30), movie.PremiereDate);
        Assert.Equal(1999, movie.ProductionYear);
    }

    [Fact]
    public async Task ASeriesGetsItsOverviewAndGenres()
    {
        var series = new Series
        {
            Name = "The Expanse",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "63639" },
        };

        var update = await Provider().FetchAsync(series, Options(), default);

        Assert.Equal(ItemUpdateType.MetadataEdit, update);
        Assert.Equal("A missing girl.", series.Overview);
        Assert.Equal(new[] { "Drama" }, series.Genres);
        Assert.Null(series.RunTimeTicks);
    }

    [Fact]
    public async Task AnEpisodeGetsItsTitle()
    {
        var episode = new Episode
        {
            Name = "Episode 3",
            IndexNumber = 3,
            ParentIndexNumber = 1,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "63639" },
        };

        var update = await Provider().FetchAsync(episode, Options(), default);

        Assert.Equal(ItemUpdateType.MetadataEdit, update);
        Assert.Equal("Remember the Cant", episode.Name);
        Assert.Equal("The Rocinante answers.", episode.Overview);
        Assert.Equal(TimeSpan.FromMinutes(45).Ticks, episode.RunTimeTicks);
        Assert.Equal(new DateTime(2015, 11, 20), episode.PremiereDate);
    }

    [Fact]
    public async Task ATitleWithNoMetadataIsLeftAlone()
    {
        var movie = new Movie
        {
            Name = "Unknown",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "999" },
        };

        var update = await Provider().FetchAsync(movie, Options(), default);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("Unknown", movie.Name);
        Assert.Null(movie.Overview);
    }

    [Fact]
    public async Task AnEpisodeWithNoEntryIsLeftAlone()
    {
        var episode = new Episode
        {
            Name = "Episode 9",
            IndexNumber = 9,
            ParentIndexNumber = 1,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "63639" },
        };

        var update = await Provider().FetchAsync(episode, Options(), default);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("Episode 9", episode.Name);
    }

    [Fact]
    public async Task AnItemWithNoProviderIdsIsLeftAlone()
    {
        var movie = new Movie { Name = "No ids" };

        var update = await Provider().FetchAsync(movie, Options(), default);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Null(movie.Overview);
    }

    [Fact]
    public async Task TheDocumentIsReadAtFetchTimeNotAtConstruction()
    {
        var holder = new MetadataHolder { Document = Document() };
        var provider = new LeLibraryMetadataProvider(holder);
        var movie = new Movie
        {
            Name = "The Matrix",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "603" },
        };

        // A later sync replaces the document; the cached provider instance must
        // use the new one, not the one it was built with.
        holder.Document = MetadataDocument.Empty;

        var update = await provider.FetchAsync(movie, Options(), default);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Null(movie.Overview);
    }
}
