using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Sync;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class PathBuilderTests
{
    private static PlanItem Movie(
        string title,
        int? year = null,
        string? quality = null,
        string? hdr = null,
        string path = "")
    {
        return new PlanItem
        {
            Key = "torbox:1:2",
            Kind = "movie",
            Library = "movies",
            Title = title,
            Year = year,
            Quality = quality,
            Hdr = hdr,
            Path = path,
        };
    }

    private static PlanItem Episode(
        string series,
        int? seriesYear,
        int season,
        int episode,
        int? episodeEnd = null,
        string path = "")
    {
        return new PlanItem
        {
            Key = "torbox:1:2",
            Kind = "episode",
            Library = "shows",
            SeriesTitle = series,
            SeriesYear = seriesYear,
            Season = season,
            Episode = episode,
            EpisodeEnd = episodeEnd,
            Path = path,
        };
    }

    [Fact]
    public void MoviePathMatchesTheContractExample()
    {
        Assert.Equal(
            "The Matrix (1999)/The Matrix (1999) - 2160p DV.strm",
            PathBuilder.MovieRelative(Movie("The Matrix", 1999, "2160p", "dv")));
    }

    [Fact]
    public void MoviePathDropsUnknownQualityAndYear()
    {
        Assert.Equal("Heat/Heat.strm", PathBuilder.MovieRelative(Movie("Heat")));
    }

    [Fact]
    public void EpisodePathPadsSeasonAndEpisode()
    {
        Assert.Equal(
            "The Expanse (2015)/Season 01/The Expanse S01E03.strm",
            PathBuilder.EpisodeRelative(Episode("The Expanse", 2015, 1, 3)));
    }

    [Fact]
    public void EpisodePathKeepsAMultiEpisodeFileOnItsRange()
    {
        Assert.Equal(
            "Show (2020)/Season 02/Show S02E04-E05.strm",
            PathBuilder.EpisodeRelative(Episode("Show", 2020, 2, 4, 5)));
    }

    [Fact]
    public void SanitizeRemovesCharactersWindowsAndSmbRefuse()
    {
        Assert.Equal("Mission- Impossible - Fallout-", PathBuilder.Sanitize("Mission: Impossible / Fallout?"));
        Assert.Equal("Trailing dots", PathBuilder.Sanitize("  Trailing dots...  "));
        Assert.Equal("Dr. Strangelove", PathBuilder.Sanitize("Dr. Strangelove"));
    }

    [Fact]
    public void SanitizeClampsLongNames()
    {
        var longName = new string('a', 200);
        Assert.Equal(120, PathBuilder.Sanitize(longName).Length);
    }

    [Fact]
    public void ADownloadedPlanPathIsUsedWhenItIsSafe()
    {
        var item = Movie("The Matrix", 1999, path: "The Matrix (1999)/The Matrix (1999) - 2160p DV.strm");
        Assert.Equal("The Matrix (1999)/The Matrix (1999) - 2160p DV.strm", PathBuilder.MovieRelative(item));
    }

    [Theory]
    [InlineData("../escape.strm")]
    [InlineData("/etc/passwd.strm")]
    [InlineData("C:\\Windows\\file.strm")]
    [InlineData("a/../../b.strm")]
    [InlineData("folder/notavideo.mkv")]
    [InlineData("")]
    public void AnUnsafePlanPathIsRejectedAndRebuilt(string path)
    {
        var relative = PathBuilder.MovieRelative(Movie("Heat", 1995, "1080p", path: path));
        Assert.Equal("Heat (1995)/Heat (1995) - 1080p.strm", relative);
    }

    [Fact]
    public void AKeyIsParsedStrictly()
    {
        Assert.True(PathBuilder.TryParseKey("torbox:12:34", out var key));
        Assert.Equal("torbox", key.Provider);
        Assert.Equal("12", key.ItemId);
        Assert.Equal("34", key.FileId);

        Assert.False(PathBuilder.TryParseKey("torbox:12", out _));
        Assert.False(PathBuilder.TryParseKey("torbox:../../x:34", out _));
        Assert.False(PathBuilder.TryParseKey("unknown:1:2", out _));
    }
}
