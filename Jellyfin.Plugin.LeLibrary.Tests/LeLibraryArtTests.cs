using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Providers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class LeLibraryArtTests
{
    private static MetadataTitle Title() => new()
    {
        Kind = "movie",
        Title = "The Matrix",
        Poster = "https://image.tmdb.org/t/p/w500/p.jpg",
        Backdrop = "https://image.tmdb.org/t/p/w1280/b.jpg",
    };

    private static MetadataEpisode Episode() => new()
    {
        Title = "Remember the Cant",
        Still = "https://artworks.thetvdb.com/e.jpg",
    };

    [Fact]
    public void AMovieGetsItsPosterAndBackdrop()
    {
        var movie = new Movie();

        LeLibraryArt.Attach(movie, Title(), null);

        Assert.Equal("https://image.tmdb.org/t/p/w500/p.jpg", movie.GetImages(ImageType.Primary).Single().Path);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/b.jpg", movie.GetImages(ImageType.Backdrop).Single().Path);
    }

    [Fact]
    public void AnEpisodeGetsItsStill()
    {
        var episode = new Episode();

        LeLibraryArt.Attach(episode, Title(), Episode());

        Assert.Equal("https://artworks.thetvdb.com/e.jpg", episode.GetImages(ImageType.Primary).Single().Path);
        Assert.Empty(episode.GetImages(ImageType.Backdrop));
    }

    [Fact]
    public void AnItemWithNoArtInTheDocumentKeepsNoArt()
    {
        var movie = new Movie();

        LeLibraryArt.Attach(movie, null, null);

        Assert.Empty(movie.ImageInfos);
    }

    [Fact]
    public void AnEpisodeWithoutAStillKeepsWhatItHas()
    {
        var episode = new Episode();
        var before = episode.ImageInfos;

        LeLibraryArt.Attach(episode, Title(), new MetadataEpisode { Title = "No still" });

        Assert.Same(before, episode.ImageInfos);
    }

    [Fact]
    public void TheDocumentReplacesThePlansPosterRatherThanAddingASecond()
    {
        var movie = new Movie();
        LeLibraryArt.Attach(movie, new MetadataTitle { Poster = "https://plan/p.jpg" }, null);

        LeLibraryArt.Attach(movie, Title(), null);

        var primary = movie.GetImages(ImageType.Primary).ToList();
        Assert.Single(primary);
        Assert.Equal("https://image.tmdb.org/t/p/w500/p.jpg", primary[0].Path);
    }

    [Fact]
    public void AnUnchangedAttachChangesNothing()
    {
        var movie = new Movie();
        LeLibraryArt.Attach(movie, Title(), null);
        var before = movie.ImageInfos;

        LeLibraryArt.Attach(movie, Title(), null);

        Assert.Same(before, movie.ImageInfos);
    }

    [Fact]
    public void TheDocumentIsFoundByTheItemsOwnIds()
    {
        var document = new MetadataDocument
        {
            Titles = new Dictionary<string, MetadataTitle>(StringComparer.Ordinal)
            {
                ["movie:603"] = Title(),
                ["tv:63639"] = new()
                {
                    Kind = "tv",
                    Poster = "https://artworks.thetvdb.com/series.jpg",
                    Episodes = new Dictionary<string, MetadataEpisode>(StringComparer.Ordinal)
                    {
                        ["1:3"] = Episode(),
                    },
                },
            },
        };

        var movie = new Movie { ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "603" } };
        var episode = new Episode
        {
            IndexNumber = 3,
            ParentIndexNumber = 1,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "63639" },
        };

        LeLibraryArt.Attach(movie, document);
        LeLibraryArt.Attach(episode, document);

        Assert.Equal("https://image.tmdb.org/t/p/w500/p.jpg", movie.GetImages(ImageType.Primary).Single().Path);
        Assert.Equal("https://artworks.thetvdb.com/e.jpg", episode.GetImages(ImageType.Primary).Single().Path);
    }

    [Fact]
    public void ASignatureIsStableAndChangesWithTheArt()
    {
        var movie = new Movie();
        LeLibraryArt.Attach(movie, Title(), null);
        var signature = LeLibraryArt.Signature(movie);

        Assert.Equal(signature, LeLibraryArt.Signature(movie));

        LeLibraryArt.Attach(movie, new MetadataTitle { Poster = "https://other/p.jpg", Backdrop = "https://other/b.jpg" }, null);

        Assert.NotEqual(signature, LeLibraryArt.Signature(movie));
    }

    [Fact]
    public void TheSignatureIsOfWhatThePluginWritesNotOfWhatJellyfinSaved()
    {
        var wanted = new Movie();
        LeLibraryArt.Attach(wanted, Title(), null);
        var wantedSignature = LeLibraryArt.Signature(wanted);

        var current = new Movie();
        LeLibraryArt.ApplyPerType(current, wanted);
        // Jellyfin downloads the image on first request and rewrites the path
        // to the file it saved. That is why the sync compares the desired
        // item's signature with the one it stored, and never recomputes it
        // from the row.
        current.ImageInfos[0].Path = "/config/metadata/library/aa/aa-poster.jpg";

        Assert.Equal(wantedSignature, LeLibraryArt.Signature(wanted));
        Assert.NotEqual(wantedSignature, LeLibraryArt.Signature(current));
    }

    [Fact]
    public void ApplyingOneTypeLeavesTheOthersAlone()
    {
        var episode = new Episode();
        LeLibraryArt.Attach(episode, Title(), Episode());
        var still = episode.GetImages(ImageType.Primary).Single().Path;

        // The document has nothing for it this time, so the wanted item has no
        // images at all.
        var wanted = new Episode();

        LeLibraryArt.ApplyPerType(episode, wanted);

        Assert.Equal(still, episode.GetImages(ImageType.Primary).Single().Path);
    }
}
