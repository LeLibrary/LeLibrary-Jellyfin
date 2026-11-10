using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Sync;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class NfoBuilderTests
{
    [Fact]
    public void AMovieCarriesItsIdsAndPoster()
    {
        var xml = NfoBuilder.Movie(new PlanItem
        {
            Kind = "movie",
            Title = "The Matrix",
            Year = 1999,
            TmdbId = 603,
            ImdbId = "tt0133093",
            Poster = "https://l.uk/abc.def/erdb/poster/tmdb:movie:603",
        }, playUrl: "http://jf:8096/LeLibrary/play/torbox:1:2");

        Assert.StartsWith("<movie>", xml);
        Assert.Contains("<title>The Matrix</title>", xml);
        Assert.Contains("<year>1999</year>", xml);
        Assert.Contains("<tmdbid>603</tmdbid>", xml);
        Assert.Contains("<imdbid>tt0133093</imdbid>", xml);
        Assert.Contains("""<thumb aspect="poster">https://l.uk/abc.def/erdb/poster/tmdb:movie:603</thumb>""", xml);
    }

    [Fact]
    public void AnEpisodeCarriesItsNumbersAndSeries()
    {
        var xml = NfoBuilder.Episode(new PlanItem
        {
            Kind = "episode",
            SeriesTitle = "The Expanse",
            SeriesYear = 2015,
            Season = 1,
            Episode = 3,
            EpisodeTitle = "Remember the Cant",
            TmdbId = 63639,
            TvdbId = 280619,
            Poster = "https://l.uk/img/w500/x.jpg",
        });

        Assert.StartsWith("<episodedetails>", xml);
        Assert.Contains("<title>Remember the Cant</title>", xml);
        Assert.Contains("<showtitle>The Expanse</showtitle>", xml);
        Assert.Contains("<season>1</season>", xml);
        Assert.Contains("<episode>3</episode>", xml);
        Assert.Contains("<tvdbid>280619</tvdbid>", xml);
    }

    [Fact]
    public void ASeriesCarriesItsIdsAndPoster()
    {
        var xml = NfoBuilder.Series(new PlanItem
        {
            Kind = "episode",
            SeriesTitle = "The Expanse",
            SeriesYear = 2015,
            TmdbId = 63639,
            TvdbId = 280619,
            Poster = "https://l.uk/img/w500/x.jpg",
        });

        Assert.StartsWith("<tvshow>", xml);
        Assert.Contains("<title>The Expanse</title>", xml);
        Assert.Contains("<year>2015</year>", xml);
        Assert.Contains("<tmdbid>63639</tmdbid>", xml);
    }

    [Fact]
    public void TitlesAreXmlEscaped()
    {
        var xml = NfoBuilder.Movie(new PlanItem { Title = "Fast & Furious <4>", Year = 2009 }, playUrl: "u");
        Assert.Contains("Fast &amp; Furious &lt;4&gt;", xml);
        Assert.DoesNotContain("<4>", xml);
    }

    [Fact]
    public void AMovieWithoutAPosterHasNoThumb()
    {
        var xml = NfoBuilder.Movie(new PlanItem { Title = "Heat", Year = 1995 }, playUrl: "u");
        Assert.DoesNotContain("<thumb", xml);
        Assert.Contains("<title>Heat</title>", xml);
    }
}
