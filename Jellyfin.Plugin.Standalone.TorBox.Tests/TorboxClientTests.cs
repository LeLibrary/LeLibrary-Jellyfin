using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.TorBox.Configuration;
using Jellyfin.Plugin.Standalone.TorBox.Providers;
using Xunit;

namespace Jellyfin.Plugin.Standalone.TorBox.Tests;

public class TorboxClientTests
{
    [Fact]
    public void OnlyTorBoxIsSupported()
    {
        using var factory = new DebridClientFactory();
        Assert.NotNull(factory.Create(new PluginConfiguration { Provider = "torbox", DebridApiKey = "k" }));
        Assert.Null(factory.Create(new PluginConfiguration { Provider = "realdebrid", DebridApiKey = "k" }));
    }

    [Fact]
    public void AnUnsupportedProviderSaysSo()
    {
        Assert.Equal(
            "Real-Debrid is not supported yet. Set the provider to TorBox.",
            DebridClientFactory.UnsupportedMessage("realdebrid"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();

        public List<string> Calls { get; } = new();

        public StubHandler Route(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _routes.Add((match, respond));
            return this;
        }

        public StubHandler Json(Func<HttpRequestMessage, bool> match, string body, HttpStatusCode status = HttpStatusCode.OK)
            => Route(match, _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!.PathAndQuery);
            foreach (var (match, respond) in _routes)
            {
                if (match(request)) return Task.FromResult(respond(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static TorboxClient Client(StubHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.torbox.app/") }, "TEST-KEY");

    private static string Page(int torrentId, params (int FileId, string Name, long Size)[] files)
    {
        var fileJson = string.Join(",", files.Select(f =>
            $$"""{"id":{{f.FileId}},"name":"{{f.Name}}","size":{{f.Size}}}"""));
        return $$"""{"success":true,"data":[{"id":{{torrentId}},"name":"T{{torrentId}}","files":[{{fileJson}}]}]}""";
    }

    [Fact]
    public async Task ListsEveryPageAndKeysFilesByProviderItemAndFile()
    {
        var handler = new StubHandler()
            .Json(r => r.RequestUri!.Query.Contains("offset=0"), Page(1, (2, "The.Matrix.2160p.mkv", 18_000_000_000L)))
            .Json(r => r.RequestUri!.Query.Contains("offset=1000"), Page(2, (7, "Show.S01E03.mkv", 3_000_000_000L)))
            .Json(r => r.RequestUri!.Query.Contains("offset=2000"), """{"success":true,"data":[]}""");

        var listing = await Client(handler).ListLibraryAsync(CancellationToken.None);

        Assert.Equal(ProviderHealth.Healthy, listing.Health);
        Assert.Equal(new[] { "torbox:1:2", "torbox:2:7" }, listing.Files.Select(f => f.Key).ToArray());
        Assert.Equal("Show.S01E03.mkv", listing.Files[1].Name);
        Assert.Equal(3_000_000_000L, listing.Files[1].Size);
    }

    [Fact]
    public async Task ABadKeyIsInvalidKeyAndNeverThrows()
    {
        var handler = new StubHandler().Json(_ => true, """{"success":false,"error":"BAD_TOKEN"}""", HttpStatusCode.Forbidden);
        var listing = await Client(handler).ListLibraryAsync(CancellationToken.None);
        Assert.Equal(ProviderHealth.InvalidKey, listing.Health);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task ARateLimitIsRateLimitedAndKeepsNoFiles()
    {
        var handler = new StubHandler().Json(_ => true, "{}", (HttpStatusCode)429);
        var listing = await Client(handler).ListLibraryAsync(CancellationToken.None);
        Assert.Equal(ProviderHealth.RateLimited, listing.Health);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task RepeatedTorrentAcrossPagesMakesListingUntrustworthy()
    {
        var handler = new StubHandler().Json(_ => true, Page(1, (2, "movie.mkv", 100)));
        var listing = await Client(handler).ListLibraryAsync(CancellationToken.None);
        Assert.False(listing.IsHealthy);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task AServerErrorIsUnavailable()
    {
        var handler = new StubHandler().Json(_ => true, "{}", HttpStatusCode.BadGateway);
        var listing = await Client(handler).ListLibraryAsync(CancellationToken.None);
        Assert.Equal(ProviderHealth.Unavailable, listing.Health);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":false,\"data\":[]}")]
    [InlineData("{\"success\":true,\"data\":null}")]
    [InlineData("{\"success\":true,\"data\":[{\"id\":1}]}")]
    [InlineData("{\"success\":true,\"data\":[{\"id\":1,\"files\":[{}]}]}")]
    [InlineData("not json")]
    public async Task AnInvalidPageCannotBeTreatedAsACompleteListing(string body)
    {
        var handler = new StubHandler()
            .Json(r => r.RequestUri!.Query.Contains("offset=0"), Page(1, (2, "movie.mkv", 100)))
            .Json(_ => true, body);

        var listing = await Client(handler).ListLibraryAsync(CancellationToken.None);

        Assert.False(listing.IsHealthy);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task MintReturnsTheLink()
    {
        var handler = new StubHandler().Json(r => r.RequestUri!.AbsolutePath.Contains("requestdl"),
            """{"success":true,"data":"https://cdn.torbox.app/abc"}""");

        var result = await Client(handler).MintAsync("torbox:1:2", CancellationToken.None);

        Assert.Equal(MintStatus.Ok, result.Status);
        Assert.Equal("https://cdn.torbox.app/abc", result.Url);
        Assert.Contains(handler.Calls, call => call.Contains("torrent_id=1") && call.Contains("file_id=2"));
    }

    [Fact]
    public async Task MintRetriesOnceThenGivesUp()
    {
        var attempts = 0;
        var handler = new StubHandler().Route(
            r => r.RequestUri!.AbsolutePath.Contains("requestdl"),
            _ => new HttpResponseMessage(++attempts == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"data":"https://cdn.torbox.app/ok"}""", Encoding.UTF8, "application/json"),
            });

        var result = await Client(handler).MintAsync("torbox:1:2", CancellationToken.None);

        Assert.Equal(MintStatus.Ok, result.Status);
        Assert.Equal("https://cdn.torbox.app/ok", result.Url);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task MintReturnsNullOnAProviderErrorBody()
    {
        var handler = new StubHandler().Json(r => r.RequestUri!.AbsolutePath.Contains("requestdl"),
            """{"success":false,"error":"DOWNLOAD_LIMIT"}""");
        var result = await Client(handler).MintAsync("torbox:1:2", CancellationToken.None);
        Assert.Equal(MintStatus.Error, result.Status);
        Assert.Null(result.Url);
    }

    [Theory]
    [InlineData("realdebrid:1:2")]
    [InlineData("torbox:")]
    [InlineData("")]
    public async Task MintRejectsKeysItCannotOwn(string key)
    {
        var handler = new StubHandler();
        var result = await Client(handler).MintAsync(key, CancellationToken.None);
        Assert.Equal(MintStatus.NotFound, result.Status);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task AFileGoneFromTheAccountIsNotFound()
    {
        var handler = new StubHandler().Json(_ => true, "{}", HttpStatusCode.NotFound);
        var result = await Client(handler).MintAsync("torbox:1:2", CancellationToken.None);
        Assert.Equal(MintStatus.NotFound, result.Status);
    }
}
