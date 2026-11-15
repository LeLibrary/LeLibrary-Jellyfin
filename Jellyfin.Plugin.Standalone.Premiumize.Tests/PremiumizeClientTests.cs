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

    [Theory]
    [InlineData("authentication_failed", ProviderHealth.InvalidKey)]
    [InlineData("permission_denied", ProviderHealth.InvalidKey)]
    [InlineData("rate_limit_reached", ProviderHealth.RateLimited)]
    [InlineData("transient_error", ProviderHealth.Unavailable)]
    public async Task HttpOkErrorEnvelopesAreNotSuccessfulListings(string code, ProviderHealth expected)
    {
        var client = Client(_ => Json($$"""{"status":"error","code":"{{code}}","files":[]}"""));
        Assert.Equal(expected, await client.ProbeAsync(CancellationToken.None));
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.Equal(expected, listing.Health);
        Assert.False(listing.IsHealthy);
    }

    [Fact]
    public async Task ItemNotFoundEnvelopeReturnsNotFound()
    {
        var client = Client(_ => Json("""{"status":"error","code":"not_found"}"""));
        Assert.Equal(MintStatus.NotFound, (await client.MintAsync("premiumize:616263:0", CancellationToken.None)).Status);
    }
}
