using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Providers;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class RealDebridClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static RealDebridClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new HttpClient(new Handler(handler)) { BaseAddress = new Uri("https://example.test/") }, "secret");

    [Fact]
    public async Task ListsOnlySelectedFilesFromCompletedTorrents()
    {
        var client = Client(r => r.RequestUri!.Query.Contains("page=2") ? Json("[]") : r.RequestUri.AbsolutePath.EndsWith("/torrents")
            ? Json("""[{"id":"ABC12","status":"downloaded"},{"id":"PENDING","status":"downloading"}]""")
            : Json("""{"files":[{"id":3,"path":"/Movie/movie.mkv","selected":1,"bytes":123},{"id":4,"path":"/ignored.mkv","selected":0}]}"""));
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.True(listing.IsHealthy);
        var file = Assert.Single(listing.Files);
        Assert.Equal("realdebrid:ABC12:3", file.Key);
        Assert.Equal("movie.mkv", file.Name);
        Assert.Equal(123, file.Size);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"A\"}]")]
    [InlineData("not json")]
    public async Task InvalidListingNeverClaimsCompleteness(string body)
    {
        var listing = await Client(_ => Json(body)).ListLibraryAsync(CancellationToken.None);
        Assert.False(listing.IsHealthy);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task FailedDetailsInvalidateWholeListing()
    {
        var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith("/torrents")
            ? Json("""[{"id":"ABC","status":"downloaded"}]""") : Json("{}", HttpStatusCode.TooManyRequests));
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.Equal(ProviderHealth.RateLimited, listing.Health);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task OverlappingPagesCannotAuthorizeCleanup()
    {
        var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith("/torrents")
            ? Json("""[{"id":"ABC","status":"downloading"}]""") : Json("{}"));
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.False(listing.IsHealthy);
    }

    [Fact]
    public async Task MissingItemsFromDeclaredTotalCannotAuthorizeCleanup()
    {
        var client = Client(r =>
        {
            var response = Json(r.RequestUri!.Query.Contains("page=2") ? "[]" : """[{"id":"ABC","status":"downloading"}]""");
            response.Headers.Add("X-Total-Count", "2");
            return response;
        });
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.False(listing.IsHealthy);
    }

    [Fact]
    public async Task PlaybackSelectsTheCorrectSelectedFileBeforeUnrestricting()
    {
        string? submitted = null;
        var client = Client(r =>
        {
            if (r.Method == HttpMethod.Get) return Json("""{"files":[{"id":1,"selected":0},{"id":2,"selected":1},{"id":3,"selected":1}],"links":["https://restricted/2","https://restricted/3"]}""");
            submitted = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"download":"https://cdn.example/video"}""");
        });
        var result = await client.MintAsync("realdebrid:ABC:3", CancellationToken.None);
        Assert.Equal(MintStatus.Ok, result.Status);
        Assert.Equal("https://cdn.example/video", result.Url);
        Assert.Contains("restricted%2F3", submitted);
    }

    [Fact]
    public async Task UnknownFileNeverFallsBackToAnotherLink()
    {
        var client = Client(_ => Json("""{"files":[{"id":1,"selected":1}],"links":["https://restricted/1"]}"""));
        var result = await client.MintAsync("realdebrid:ABC:99", CancellationToken.None);
        Assert.Equal(MintStatus.NotFound, result.Status);
    }
}
