using Jellyfin.Plugin.LeLibrary.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class LeLibraryLibraryOptionsTests
{
    private static readonly string[] Types = { "Movie", "Series", "Season", "Episode" };

    [Fact]
    public void OnlyLeLibraryIsAllowedToFetch()
    {
        var options = new LibraryOptions();

        Assert.True(LeLibraryLibraryOptions.Apply(options));

        foreach (var type in Types)
        {
            var entry = options.GetTypeOptions(type);
            Assert.NotNull(entry);
            Assert.Equal(new[] { "LeLibrary" }, entry!.MetadataFetchers);
            Assert.Empty(entry.ImageFetchers);
        }
    }

    [Fact]
    public void TheEntryIsWrittenOnceAndThenLeftAlone()
    {
        var options = new LibraryOptions();
        Assert.True(LeLibraryLibraryOptions.Apply(options));
        var after = options.GetTypeOptions("Movie");

        Assert.False(LeLibraryLibraryOptions.Apply(options));

        Assert.Same(after, options.GetTypeOptions("Movie"));
    }

    [Fact]
    public void TheOperatorsOwnSettingsSurvive()
    {
        var options = new LibraryOptions
        {
            TypeOptions = new[]
            {
                new TypeOptions
                {
                    Type = "Movie",
                    ImageOptions = new[] { new ImageOption { Type = ImageType.Primary, Limit = 1 } },
                    SimilarItemProviders = new[] { "IMDB" },
                },
            },
        };

        LeLibraryLibraryOptions.Apply(options);

        var entry = options.GetTypeOptions("Movie")!;
        Assert.Equal(new[] { "IMDB" }, entry.SimilarItemProviders);
        Assert.Single(entry.ImageOptions);
        // The types that had no entry of their own get one.
        Assert.NotNull(options.GetTypeOptions("Series"));
    }

    [Fact]
    public void AnEntryTheOperatorHasChangedIsNotTouchedAgain()
    {
        var options = new LibraryOptions
        {
            TypeOptions = new[]
            {
                new TypeOptions { Type = "Movie", MetadataFetchers = new[] { "TheMovieDb" } },
                new TypeOptions { Type = "Series", MetadataFetchers = new[] { "TheMovieDb" } },
                new TypeOptions { Type = "Season", MetadataFetchers = new[] { "TheMovieDb" } },
                new TypeOptions { Type = "Episode", MetadataFetchers = new[] { "TheMovieDb" } },
            },
        };

        Assert.False(LeLibraryLibraryOptions.Apply(options));

        Assert.Equal(new[] { "TheMovieDb" }, options.GetTypeOptions("Movie")!.MetadataFetchers);
    }

    [Fact]
    public void AMissingTypeOptionsArrayIsHandled()
    {
        var options = new LibraryOptions { TypeOptions = null! };

        Assert.True(LeLibraryLibraryOptions.Apply(options));

        Assert.NotNull(options.GetTypeOptions("Episode"));
    }
}
