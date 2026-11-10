using System;
using System.Linq;
using Jellyfin.Plugin.LeLibrary.Model;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class PlanParserTests
{
    private const string Example = """
    {
      "schemaVersion": 1,
      "generatedAt": "2026-11-10T12:00:00.000Z",
      "provider": "torbox",
      "libraries": [
        { "key": "movies", "name": "LeLibrary Movies", "kind": "movies" },
        { "key": "shows", "name": "LeLibrary Shows", "kind": "shows" }
      ],
      "items": [
        {
          "key": "torbox:11:2", "library": "movies", "kind": "movie",
          "title": "The Matrix", "year": 1999, "tmdbId": 603, "imdbId": "tt0133093",
          "quality": "2160p", "hdr": "dv",
          "path": "The Matrix (1999)/The Matrix (1999) - 2160p DV.strm",
          "poster": "https://lelibrary.uk/abc.def/erdb/poster/tmdb:movie:603"
        },
        {
          "key": "torbox:77:2", "library": "shows", "kind": "episode",
          "seriesTitle": "The Expanse", "seriesYear": 2015, "tmdbId": 63639, "tvdbId": 280619,
          "season": 1, "episode": 3, "episodeTitle": "Remember the Cant",
          "path": "The Expanse (2015)/Season 01/The Expanse S01E03.strm",
          "poster": "https://lelibrary.uk/img/tmdb/w500/x.jpg"
        }
      ],
      "collections": []
    }
    """;

    [Fact]
    public void ParsesTheContractExample()
    {
        var result = PlanParser.Parse(Example);

        Assert.Empty(result.Warnings);
        Assert.Equal(1, result.Plan.SchemaVersion);
        Assert.Equal("torbox", result.Plan.Provider);
        Assert.Equal(2, result.Plan.Libraries.Count);
        Assert.Equal(2, result.Plan.Items.Count);

        var movie = result.Plan.Items[0];
        Assert.Equal("torbox:11:2", movie.Key);
        Assert.Equal("The Matrix", movie.Title);
        Assert.Equal(1999, movie.Year);
        Assert.Equal("2160p", movie.Quality);
        Assert.Equal("dv", movie.Hdr);
        Assert.Equal("The Matrix (1999)/The Matrix (1999) - 2160p DV.strm", movie.Path);

        var episode = result.Plan.Items[1];
        Assert.Equal("The Expanse", episode.SeriesTitle);
        Assert.Equal(1, episode.Season);
        Assert.Equal(3, episode.Episode);
        Assert.Equal(280619, episode.TvdbId);
    }

    [Fact]
    public void ANewerSchemaKeepsWorkingWithAWarning()
    {
        var json = Example.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        var result = PlanParser.Parse(json);
        Assert.Equal(2, result.Plan.SchemaVersion);
        Assert.Contains(result.Warnings, warning => warning.Contains("schema", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownPropertiesAreIgnored()
    {
        var json = Example.Replace("\"provider\": \"torbox\",", "\"provider\": \"torbox\", \"somethingNew\": { \"a\": 1 },");
        var result = PlanParser.Parse(json);
        Assert.Equal("torbox", result.Plan.Provider);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ANewPerItemFieldIsIgnored()
    {
        var json = Example.Replace("\"library\": \"movies\",", "\"library\": \"movies\", \"subtitles\": [\"a.srt\"],");
        var result = PlanParser.Parse(json);
        Assert.Equal(2, result.Plan.Items.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AnItemWithoutAKeyOrPathIsDroppedWithAWarning()
    {
        var json = """
        {
          "schemaVersion": 1, "provider": "torbox", "libraries": [], "collections": [],
          "items": [
            { "key": "", "library": "movies", "kind": "movie", "title": "No key", "path": "A/A.strm" },
            { "key": "torbox:1:1", "library": "movies", "kind": "movie", "title": "No path" },
            { "key": "torbox:2:2", "library": "movies", "kind": "movie", "title": "Good", "path": "G/G.strm" }
          ]
        }
        """;
        var result = PlanParser.Parse(json);
        Assert.Single(result.Plan.Items);
        Assert.Equal("torbox:2:2", result.Plan.Items[0].Key);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    public void AMalformedBodyIsAFormatException(string body)
    {
        Assert.Throws<PlanFormatException>(() => PlanParser.Parse(body));
    }

    [Fact]
    public void AnEmptyPlanIsValid()
    {
        var result = PlanParser.Parse("""{ "schemaVersion": 1, "provider": "torbox", "items": [], "libraries": [] }""");
        Assert.Empty(result.Plan.Items);
        Assert.Empty(result.Warnings);
    }
}
