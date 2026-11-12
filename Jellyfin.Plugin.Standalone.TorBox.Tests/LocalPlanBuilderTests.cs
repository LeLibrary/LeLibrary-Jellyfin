using System;
using Jellyfin.Plugin.Standalone.TorBox.Providers;
using Jellyfin.Plugin.Standalone.TorBox.Virtual;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.Standalone.TorBox.Tests;

public class LocalPlanBuilderTests
{
    private static ItemLookupInfo ParseName(string name) => new() { Name = name, Year = null };

    [Fact]
    public void UsesJellyfinNamingToIdentifyAnEpisode()
    {
        var builder = new LocalPlanBuilder(ParseName);
        var (plan, skipped) = builder.Build("torbox", new[] { new DebridFile("torbox:1:2", "Example.Show.S02E03.mkv", 123) });
        var episode = Assert.Single(plan.Items);
        Assert.Equal("episode", episode.Kind);
        Assert.Equal(2, episode.Season);
        Assert.Equal(3, episode.Episode);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void IgnoresNonVideoAndKeysFromAnotherProvider()
    {
        var builder = new LocalPlanBuilder(ParseName);
        var (plan, skipped) = builder.Build("torbox", new[]
        {
            new DebridFile("torbox:1:2", "movie.rar", 1),
            new DebridFile("realdebrid:A:2", "movie.mkv", 1),
        });
        Assert.Empty(plan.Items);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void NamesMoviesUsingJellyfinsParser()
    {
        var builder = new LocalPlanBuilder(_ => new ItemLookupInfo { Name = "The Matrix", Year = 1999 });
        var (plan, _) = builder.Build("torbox", new[] { new DebridFile("torbox:1:2", "The.Matrix.1999.mkv", 123) });
        var movie = Assert.Single(plan.Items);
        Assert.Equal("movie", movie.Kind);
        Assert.Equal("The Matrix", movie.Title);
        Assert.Equal(1999, movie.Year);
    }

    [Fact]
    public void MultiEpisodeFileRemainsOneJellyfinItemWithAnEndNumber()
    {
        var builder = new LocalPlanBuilder(ParseName);
        var (plan, _) = builder.Build("torbox", new[] { new DebridFile("torbox:1:2", "Example.Show.S02E03-E04.mkv", 123) });
        var library = VirtualItemFactory.Build(plan);
        var episode = Assert.IsType<MediaBrowser.Controller.Entities.TV.Episode>(Assert.Single(library.Episodes).Item);
        Assert.Equal(3, episode.IndexNumber);
        Assert.Equal(4, episode.IndexNumberEnd);
    }
}
