using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Client;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class LeLibraryClientTests : IDisposable
{
    private const string PlanJson = """
    {"schemaVersion":1,"provider":"torbox","libraries":[],"collections":[],
     "items":[{"key":"torbox:1:2","library":"movies","kind":"movie","title":"Film","path":"Film/Film.strm"}]}
    """;

    private readonly string _dir;
    private readonly string _cache;

    public LeLibraryClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lelibrary-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cache = Path.Combine(_dir, "plan.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
        GC.SuppressFinalize(this);
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

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private LeLibraryClient Client(Stub stub) => new(new HttpClient(stub), _cache);

    [Fact]
    public async Task AFreshPlanIsUsedAndCached()
    {
        var stub = new Stub(_ => Json(PlanJson));
        var fetch = await Client(stub).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Network, fetch.Source);
        Assert.Single(fetch.Plan.Items);
        Assert.True(File.Exists(_cache));
        Assert.Equal("https://l.uk/abc.def/jellyfin/config.json", stub.Last!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("https://l.uk/abc.def")]
    [InlineData("https://l.uk/abc.def/")]
    [InlineData("https://l.uk/abc.def/manifest.json")]
    public async Task TheSetupUrlMayBePastedWithOrWithoutTheManifestSuffix(string setupUrl)
    {
        var stub = new Stub(_ => Json(PlanJson));
        await Client(stub).FetchAsync(setupUrl, CancellationToken.None);
        Assert.Equal("https://l.uk/abc.def/jellyfin/config.json", stub.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ANotModifiedAnswerUsesTheCache()
    {
        var stub = new Stub(_ => Json(PlanJson));
        await Client(stub).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        var cached = new Stub(request =>
        {
            // TryAddWithoutValidation keeps the header out of the typed
            // collection, so read it the way it went on the wire.
            var value = request.Headers.TryGetValues("If-None-Match", out var values)
                ? string.Join(",", values)
                : string.Empty;
            Assert.Equal("\"etag-1\"", value);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        // Seed the stored etag the way a real 200 would have.
        var first = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PlanJson, Encoding.UTF8, "application/json"),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-1\"") },
        });
        await Client(first).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        var fetch = await Client(cached).FetchAsync("https://l.uk/abc.def", CancellationToken.None);
        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Cache, fetch.Source);
        Assert.Single(fetch.Plan.Items);
    }

    [Fact]
    public async Task ANetworkFailureKeepsTheLastKnownGoodPlan()
    {
        await Client(new Stub(_ => Json(PlanJson))).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        var fetch = await Client(new Stub(_ => throw new HttpRequestException("down")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Cache, fetch.Source);
        Assert.NotNull(fetch.Error);
        Assert.Single(fetch.Plan.Items);
    }

    [Fact]
    public async Task ANetworkFailureWithoutACacheIsAFailure()
    {
        var fetch = await Client(new Stub(_ => throw new HttpRequestException("down")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.False(fetch.Ok);
        Assert.Empty(fetch.Plan.Items);
    }

    [Fact]
    public async Task AnErrorBodyKeepsTheCacheAndNeverBecomesAnEmptyPlan()
    {
        await Client(new Stub(_ => Json(PlanJson))).FetchAsync("https://l.uk/abc.def", CancellationToken.None);
        var before = File.ReadAllText(_cache);

        var fetch = await Client(new Stub(_ => Json("""{"success":false,"error":"library unavailable"}""")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Cache, fetch.Source);
        Assert.Single(fetch.Plan.Items);
        Assert.Contains("library unavailable", fetch.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_cache));
    }

    [Fact]
    public async Task AnErrorBodyWithoutACacheIsAFailure()
    {
        var fetch = await Client(new Stub(_ => Json("""{"success":false,"error":"library unavailable"}""")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.False(fetch.Ok);
        Assert.Contains("library unavailable", fetch.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AServerErrorIsReportedAndKeepsWorkingFromCache()
    {
        await Client(new Stub(_ => Json(PlanJson))).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        var fetch = await Client(new Stub(_ => Json("{}", HttpStatusCode.ServiceUnavailable)))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Cache, fetch.Source);
        Assert.Contains("503", fetch.Error);
    }
}
