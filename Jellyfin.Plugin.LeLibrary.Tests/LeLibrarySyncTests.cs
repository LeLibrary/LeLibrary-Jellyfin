using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LeLibrary;
using Jellyfin.Plugin.LeLibrary.Client;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Providers;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

/// <summary>
/// The sync's own decisions: what it may delete, and how it reads the
/// provider's listing.
/// </summary>
public class LeLibrarySyncTests
{
    private static MetadataDocument Document() => new()
    {
        Titles = new Dictionary<string, MetadataTitle>
        {
            ["movie:603"] = new() { Kind = "movie", Title = "The Matrix" },
        },
    };

    [Fact]
    public void OnlyAFreshPlanMayRemoveAnything()
    {
        Assert.True(LeLibrarySync.AllowRemovals(PlanSource.Network));
        Assert.False(LeLibrarySync.AllowRemovals(PlanSource.Cache));
    }

    [Fact]
    public void AFetchedDocumentGoesWhereTheProvidersReadIt()
    {
        var holder = new MetadataHolder();
        var document = Document();

        var applied = LeLibrarySync.AcceptMetadata(
            holder,
            new MetadataFetch(true, null, document, PlanSource.Network));

        Assert.Same(document, applied);
        Assert.Same(document, holder.Document);
    }

    [Fact]
    public void AFailedFetchKeepsTheDocumentTheProvidersAlreadyHave()
    {
        var holder = new MetadataHolder { Document = Document() };
        var before = holder.Document;

        var applied = LeLibrarySync.AcceptMetadata(holder, MetadataFetch.Failed("down"));

        Assert.Null(applied);
        Assert.Same(before, holder.Document);
    }

    [Fact]
    public void AListingWithTwoFilesUnderOneKeyIsRead()
    {
        var files = new[]
        {
            new DebridFile("torbox:1:2", "The.Matrix.2160p.mkv", 18_000_000_000L),
            new DebridFile("torbox:1:2", "The.Matrix.2160p.mkv", 18_000_000_000L),
        };

        var names = LeLibrarySync.FileNamesByKey(files);

        Assert.Equal("The.Matrix.2160p.mkv", names["torbox:1:2"]);
        Assert.Single(names);
    }
}
