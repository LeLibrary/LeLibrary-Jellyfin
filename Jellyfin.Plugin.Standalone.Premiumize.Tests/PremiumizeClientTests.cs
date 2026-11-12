using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.Premiumize.Providers;
using Xunit;
namespace Jellyfin.Plugin.Standalone.Premiumize.Tests;
public sealed class PremiumizeClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request));
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static PremiumizeClient Client(Func<HttpRequestMessage, HttpResponseMessage> callback) => new(new HttpClient(new Handler(callback)) { BaseAddress = new Uri("https://example.test/") }, "test");

    [Fact]
    public async Task CloudFileIdsAreOpaqueAndResolveTheSameFile()
    {
        var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith("listall")
            ? Json("""{"status":"success","files":[{"id":"x-y/1","name":"Movie.mkv","path":"Films/Movie.mkv","size":42}]}""")
            : r.RequestUri.Query.Contains("x-y%2F1", StringComparison.OrdinalIgnoreCase)
                ? Json("""{"status":"success","link":"https://cdn.example/file"}""") : Json("{}", HttpStatusCode.NotFound));
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.True(listing.IsHealthy);
        var file = Assert.Single(listing.Files);
        Assert.Equal("Movie.mkv", file.Name);
        Assert.Equal(MintStatus.Ok, (await client.MintAsync(file.Key, CancellationToken.None)).Status);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"error\",\"files\":[]}")]
    [InlineData("{\"status\":\"success\",\"files\":[{}]}")]
    [InlineData("not json")]
    public async Task InvalidOrIncompleteListingNeverAuthorizesCleanup(string body)
    {
        var listing = await Client(_ => Json(body)).ListLibraryAsync(CancellationToken.None);
        Assert.False(listing.IsHealthy);
        Assert.Empty(listing.Files);
    }
}
