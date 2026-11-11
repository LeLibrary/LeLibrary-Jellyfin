using System.Collections.Generic;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class ItemIdentityTests
{
    [Fact]
    public void ASeriesKeyIsItsOwnKey()
    {
        var key = ItemIdentity.SeriesKey(new PlanItem { TmdbId = 98410, SeriesTitle = "1000-lb Sisters" });
        Assert.Equal("series:98410", key);
    }

    [Fact]
    public void ASeriesKeyFallsBackToTheTitle()
    {
        var key = ItemIdentity.SeriesKey(new PlanItem { TmdbId = null, SeriesTitle = "Some Show" });
        Assert.Equal("series:some-show", key);
    }

    [Fact]
    public void ASeasonKeyCarriesTheSeriesAndTheSeason()
    {
        Assert.Equal("series:98410:season:1", ItemIdentity.SeasonKey("series:98410", 1));
    }

    [Fact]
    public void ThePlayKeyIsReadFromAPlayUrl()
    {
        Assert.Equal("torbox:94418283:0", ItemIdentity.PlayKeyFromPath("http://host:8096/LeLibrary/play/torbox:94418283:0/Show%20S01E01.mkv"));
        Assert.Null(ItemIdentity.PlayKeyFromPath("/config/data/lelibrary/movies"));
        Assert.Null(ItemIdentity.PlayKeyFromPath(null));
    }

    [Fact]
    public void AMovieTakesItsIdentityFromTheProviderId()
    {
        var movie = new Movie { ProviderIds = new Dictionary<string, string> { [VirtualIdentity.ProviderId] = "torbox:1:2" } };
        Assert.Equal("torbox:1:2", ItemIdentity.Of(movie));
    }

    [Fact]
    public void ASeriesTakesItsIdentityFromThePlayUrl()
    {
        // A legacy series row: the provider id belongs to one of its episodes.
        var series = new Series
        {
            ProviderIds = new Dictionary<string, string> { [VirtualIdentity.ProviderId] = "torbox:94418283:0" },
            Path = "http://host:8096/LeLibrary/play/series:98410/Show%20(2020).mkv",
        };
        Assert.Equal("series:98410", ItemIdentity.Of(series));
    }
}
