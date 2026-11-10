using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Client;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Providers;
using Jellyfin.Plugin.LeLibrary.Sync;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class SyncRunnerTests : IDisposable
{
    private const string BaseUrl = "http://jellyfin:8096";

    private readonly string _root;

    public SyncRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lelibrary-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
        GC.SuppressFinalize(this);
    }

    private sealed class FakePlans : IPlanSource
    {
        public PlanFetch Result { get; set; } = new(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new List<PlanItem>(),
        }, PlanSource.Network, Array.Empty<string>());

        public Task<PlanFetch> FetchAsync(string? setupUrl, CancellationToken cancellationToken) => Task.FromResult(Result);
    }

    private sealed class FakeProvider : IDebridClient
    {
        public ProviderListing Listing { get; set; } = new(ProviderHealth.Healthy, new List<DebridFile>(), null);

        public string Provider => "torbox";

        public Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken) => Task.FromResult(Listing);

        public Task<string?> MintLinkAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Listing.Health);
    }

    private static PlanItem Movie(string key, string title, int year, string path) => new()
    {
        Key = key,
        Library = "movies",
        Kind = "movie",
        Title = title,
        Year = year,
        Path = path,
    };

    private static PlanItem Episode(string key, string series, int season, int episode, string path) => new()
    {
        Key = key,
        Library = "shows",
        Kind = "episode",
        SeriesTitle = series,
        SeriesYear = 2015,
        Season = season,
        Episode = episode,
        Path = path,
    };

    private SyncRunner Runner(FakePlans plans, FakeProvider provider)
        => new(plans, provider);

    private static DebridFile File(string key) => new(key, "file.mkv", 1_000);

    private string[] AllFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public async Task TheFirstSyncWritesTheStrmAndTheNfo()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[]
            {
                Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm"),
                Episode("torbox:9:8", "The Expanse", 1, 3, "The Expanse (2015)/Season 01/The Expanse S01E03.strm"),
            },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2"), File("torbox:9:8") }, null);

        var result = await Runner(plans, provider).RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Added);
        Assert.Contains("The Matrix (1999)/The Matrix (1999).strm", AllFiles());
        Assert.Contains("The Matrix (1999)/The Matrix (1999).nfo", AllFiles());
        Assert.Contains("The Expanse (2015)/Season 01/The Expanse S01E03.strm", AllFiles());
        Assert.Contains("The Expanse (2015)/tvshow.nfo", AllFiles());
        Assert.Equal(
            "http://jellyfin:8096/LeLibrary/play/torbox:1:2",
            System.IO.File.ReadAllText(Path.Combine(_root, "The Matrix (1999)/The Matrix (1999).strm")).Trim());
    }

    [Fact]
    public async Task ARunWithNoChangesTouchesNothing()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2") }, null);

        var runner = Runner(plans, provider);
        await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);
        var second = await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.True(second.Ok);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Removed);
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public async Task AFailedPlanKeepsEverything()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2") }, null);
        var runner = Runner(plans, provider);
        await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        plans.Result = PlanFetch.Failed("LeLibrary answered 503");
        var result = await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Removed);
        Assert.Contains("The Matrix (1999)/The Matrix (1999).strm", AllFiles());
    }

    [Theory]
    [InlineData(ProviderHealth.RateLimited)]
    [InlineData(ProviderHealth.InvalidKey)]
    [InlineData(ProviderHealth.Unavailable)]
    public async Task AnUnhealthyProviderKeepsEverything(ProviderHealth health)
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2") }, null);
        var runner = Runner(plans, provider);
        await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        provider.Listing = new ProviderListing(health, Array.Empty<DebridFile>(), "no");
        var result = await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Removed);
        Assert.Contains("The Matrix (1999)/The Matrix (1999).strm", AllFiles());
    }

    [Fact]
    public async Task AnItemGoneFromBothSidesIsRemoved()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2") }, null);
        var runner = Runner(plans, provider);
        await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        plans.Result = new PlanFetch(true, null, new JellyfinPlan { Provider = "torbox", Items = Array.Empty<PlanItem>() }, PlanSource.Network, Array.Empty<string>());
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, Array.Empty<DebridFile>(), null);
        var result = await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Removed);
        Assert.DoesNotContain(AllFiles(), f => f.EndsWith(".strm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnItemStillOnDebridButMissingFromThePlanIsRemoved()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2") }, null);
        var runner = Runner(plans, provider);
        await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        // The user excluded it in LeLibrary: the plan no longer lists it, and
        // that is the authority on selection.
        plans.Result = new PlanFetch(true, null, new JellyfinPlan { Provider = "torbox", Items = Array.Empty<PlanItem>() }, PlanSource.Network, Array.Empty<string>());
        var result = await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public async Task AnItemThePlanListsButDebridDoesNotIsSkippedNotWritten()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, Array.Empty<DebridFile>(), null);

        var result = await Runner(plans, provider).RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task ARenameMovesTheStrmAndItsNfo()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999).strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2") }, null);
        var runner = Runner(plans, provider);
        await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "The Matrix", 1999, "The Matrix (1999)/The Matrix (1999) - 2160p.strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var result = await runner.RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.Equal(1, result.Moved);
        Assert.Contains("The Matrix (1999)/The Matrix (1999) - 2160p.strm", AllFiles());
        Assert.DoesNotContain("The Matrix (1999)/The Matrix (1999).strm", AllFiles());
    }

    [Fact]
    public async Task FilesWeDidNotWriteAreNeverDeleted()
    {
        var foreign = Path.Combine(_root, "Other", "keep.strm");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        System.IO.File.WriteAllText(foreign, "http://elsewhere.example/stream");

        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan { Provider = "torbox", Items = Array.Empty<PlanItem>() }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, Array.Empty<DebridFile>(), null);

        var result = await Runner(plans, provider).RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.True(System.IO.File.Exists(foreign));
    }

    [Fact]
    public async Task AnUnsafePlanPathIsRebuiltInsideTheRoot()
    {
        var plans = new FakePlans();
        plans.Result = new PlanFetch(true, null, new JellyfinPlan
        {
            Provider = "torbox",
            Items = new[] { Movie("torbox:1:2", "Heat", 1995, "../../escape.strm") },
        }, PlanSource.Network, Array.Empty<string>());
        var provider = new FakeProvider();
        provider.Listing = new ProviderListing(ProviderHealth.Healthy, new[] { File("torbox:1:2") }, null);

        await Runner(plans, provider).RunAsync(new SyncRequest(_root, "https://l.uk/x", BaseUrl), CancellationToken.None);

        Assert.Contains("Heat (1995)/Heat (1995).strm", AllFiles());
        Assert.False(System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.strm")));
    }
}
