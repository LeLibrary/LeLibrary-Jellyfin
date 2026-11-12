using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.TorBox.Playback;
using Jellyfin.Plugin.Standalone.TorBox.Providers;
using Xunit;

namespace Jellyfin.Plugin.Standalone.TorBox.Tests;

public class PlaybackTests
{
    private sealed class BlockingProvider : IDebridClient
    {
        public int Mints;
        public TaskCompletionSource<MintResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Provider => "torbox";
        public Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MintResult> MintAsync(string key, CancellationToken cancellationToken) { Interlocked.Increment(ref Mints); return Completion.Task; }
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedLinkResolution()
    {
        var provider = new BlockingProvider();
        var cache = new LinkCache(() => provider);
        using var stop = new CancellationTokenSource();
        var first = cache.GetAsync("torbox:1:2", stop.Token);
        var second = cache.GetAsync("torbox:1:2", CancellationToken.None);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        provider.Completion.SetResult(new MintResult(MintStatus.Ok, "https://cdn.example/file"));
        Assert.Equal(MintStatus.Ok, (await second).Status);
        Assert.Equal(1, provider.Mints);
    }
    private sealed class FakeProvider : IDebridClient
    {
        public MintResult Next { get; set; } = new(MintStatus.Ok, "https://cdn.example/file.mkv");
        public int Mints { get; private set; }
        public string Provider => "torbox";

        public Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ProviderListing(ProviderHealth.Healthy, Array.Empty<DebridFile>(), null));

        public Task<MintResult> MintAsync(string key, CancellationToken cancellationToken)
        {
            Mints += 1;
            return Task.FromResult(Next);
        }

        public Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderHealth.Healthy);
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Partial(long from, long to, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent("0123456789"u8.ToArray()),
        };
        response.Headers.Add("Accept-Ranges", "bytes");
        response.Content.Headers.Add("Content-Range", $"bytes {from}-{to}/{total}");
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/x-matroska");
        return response;
    }

    [Fact]
    public async Task ALinkIsCachedForFifteenMinutes()
    {
        var provider = new FakeProvider();
        var cache = new LinkCache(() => provider);
        var first = await cache.GetAsync("torbox:1:2", CancellationToken.None);
        var second = await cache.GetAsync("torbox:1:2", CancellationToken.None);

        Assert.Equal(MintStatus.Ok, first.Status);
        Assert.Equal("https://cdn.example/file.mkv", second.Url);
        Assert.Equal(1, provider.Mints);
    }

    [Fact]
    public async Task ChangingCredentialsDoesNotReuseThePreviousAccountsLink()
    {
        var provider = new FakeProvider();
        var credential = "first-account";
        var cache = new LinkCache(() => provider, () => credential);
        await cache.GetAsync("torbox:1:2", CancellationToken.None);
        credential = "second-account";
        provider.Next = new MintResult(MintStatus.Ok, "https://cdn.example/second");
        var link = await cache.GetAsync("torbox:1:2", CancellationToken.None);
        Assert.Equal("https://cdn.example/second", link.Url);
        Assert.Equal(2, provider.Mints);
    }

    [Fact]
    public async Task AFailedMintIsNotCached()
    {
        var provider = new FakeProvider { Next = MintResult.Fail(MintStatus.Error) };
        var cache = new LinkCache(() => provider);
        await cache.GetAsync("torbox:1:2", CancellationToken.None);
        provider.Next = new MintResult(MintStatus.Ok, "https://cdn.example/ok.mkv");
        var second = await cache.GetAsync("torbox:1:2", CancellationToken.None);

        Assert.Equal(MintStatus.Ok, second.Status);
        Assert.Equal(2, provider.Mints);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("torbox:1")]
    [InlineData("torbox:../x:2")]
    [InlineData("unknown:1:2")]
    public async Task ABadKeyIsABadRequest(string key)
    {
        var proxy = new PlaybackProxy(() => new FakeProvider(), new LinkCache(() => new FakeProvider()), new HttpClient(new Stub(_ => Partial(0, 9, 10))));
        var result = await proxy.OpenAsync(key, null, null, CancellationToken.None);
        Assert.Equal(400, result.Status);
    }

    [Fact]
    public async Task TheRangeIsPassedThroughAndTheUpstreamStreamIsReturned()
    {
        var provider = new FakeProvider();
        var stub = new Stub(_ => Partial(0, 9, 100));
        var proxy = new PlaybackProxy(() => provider, new LinkCache(() => provider), new HttpClient(stub));

        var result = await proxy.OpenAsync("torbox:1:2", "bytes=0-9", null, CancellationToken.None);

        Assert.Equal(206, result.Status);
        Assert.Equal("video/x-matroska", result.ContentType);
        Assert.Equal("bytes 0-9/100", result.ContentRange);
        Assert.NotNull(stub.Last);
        Assert.Equal("bytes=0-9", stub.Last!.Headers.Range!.ToString());
        Assert.NotNull(result.Body);
        await using var body = result.Body!;
        using var reader = new StreamReader(body);
        Assert.Equal("0123456789", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task AFileGoneFromTheAccountIs410()
    {
        var provider = new FakeProvider { Next = MintResult.Fail(MintStatus.NotFound) };
        var proxy = new PlaybackProxy(() => provider, new LinkCache(() => provider), new HttpClient(new Stub(_ => Partial(0, 9, 10))));

        var result = await proxy.OpenAsync("torbox:1:2", null, null, CancellationToken.None);

        Assert.Equal(410, result.Status);
    }

    [Fact]
    public async Task AMintFailureIs502()
    {
        var provider = new FakeProvider { Next = MintResult.Fail(MintStatus.Error) };
        var proxy = new PlaybackProxy(() => provider, new LinkCache(() => provider), new HttpClient(new Stub(_ => Partial(0, 9, 10))));

        var result = await proxy.OpenAsync("torbox:1:2", null, null, CancellationToken.None);

        Assert.Equal(502, result.Status);
        Assert.Contains("TorBox", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUpstreamFailureIs502()
    {
        var provider = new FakeProvider();
        var proxy = new PlaybackProxy(() => provider, new LinkCache(() => provider), new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))));

        var result = await proxy.OpenAsync("torbox:1:2", null, null, CancellationToken.None);

        Assert.Equal(502, result.Status);
    }

    [Fact]
    public async Task ExpiredLinkIsRemintedAndRangeIsRetried()
    {
        var provider = new FakeProvider();
        var attempts = 0;
        var stub = new Stub(_ => ++attempts == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Partial(10, 19, 100));
        var proxy = new PlaybackProxy(() => provider, new LinkCache(() => provider), new HttpClient(stub));
        var result = await proxy.OpenAsync("torbox:1:2", "bytes=10-19", null, CancellationToken.None);
        Assert.Equal(206, result.Status);
        Assert.Equal(2, provider.Mints);
        Assert.Equal("bytes=10-19", stub.Last!.Headers.Range!.ToString());
        await result.Body!.DisposeAsync();
    }
}
