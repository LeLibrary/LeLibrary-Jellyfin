using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.AllDebrid.Providers;
using Xunit;
namespace Jellyfin.Plugin.Standalone.AllDebrid.Tests;
public sealed class AllDebridClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => Task.FromResult(respond(r)); }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static AllDebridClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://example.test/") }, "key");
    [Fact]
    public async Task ReadsNestedFileAndUnlocksItsLink()
    {
        var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith("status")
            ? Json("""{"status":"success","data":{"magnets":[{"id":123,"statusCode":4,"filename":"A Show"}]}}""")
            : r.RequestUri.AbsolutePath.EndsWith("files")
                ? Json("""{"status":"success","data":{"magnets":[{"id":"123","files":[{"n":"Season 1","e":[{"n":"Show.S01E01.mkv","s":300,"l":"https://alldebrid.com/f/abc"}]}]}]}}""")
                : Json("""{"status":"success","data":{"link":"https://cdn.example/file"}}"""));
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.True(listing.IsHealthy);
        var file = Assert.Single(listing.Files);
        Assert.Equal("Show.S01E01.mkv", file.Name);
        Assert.Equal(MintStatus.Ok, (await client.MintAsync(file.Key, CancellationToken.None)).Status);
    }
    [Fact]
    public async Task MissingMagnetInBatchInvalidatesTheWholeListing()
    {
        var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith("status")
            ? Json("""{"status":"success","data":{"magnets":[{"id":123,"statusCode":4}]}}""")
            : Json("""{"status":"success","data":{"magnets":[]}}"""));
        var listing = await client.ListLibraryAsync(CancellationToken.None);
        Assert.False(listing.IsHealthy);
        Assert.Empty(listing.Files);
    }
}
