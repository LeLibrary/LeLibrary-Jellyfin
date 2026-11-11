using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Client;
using Jellyfin.Plugin.LeLibrary.Model;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class MetadataClientTests : IDisposable
{
    private const string DocumentJson = """
    {
      "schemaVersion": 1,
      "generatedAt": "2026-11-11T12:00:00.000Z",
      "titles": {
        "movie:603": {
          "kind": "movie", "title": "The Matrix", "year": 1999,
          "overview": "A hacker learns.", "runtime": 136,
          "genres": ["Action"], "rating": 8.2,
          "poster": "https://image.tmdb.org/p.jpg"
        },
        "tv:63639": {
          "kind": "tv", "title": "The Expanse",
          "episodes": { "1:3": { "title": "Remember the Cant", "runtime": 45 } }
        }
      }
    }
    """;

    private readonly string _dir;
    private readonly string _cache;

    public MetadataClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lelibrary-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cache = Path.Combine(_dir, "metadata.json");
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

    private MetadataClient Client(Stub stub) => new(new HttpClient(stub), _cache);

    [Fact]
    public async Task AMetadataDocumentIsParsed()
    {
        var fetch = await Client(new Stub(_ => Json(DocumentJson))).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Network, fetch.Source);

        var movie = fetch.Document.Find("movie", 603, null, null);
        Assert.NotNull(movie);
        Assert.Equal("The Matrix", movie!.Title);
        Assert.Equal(136, movie.Runtime);
        Assert.Equal(8.2, movie.Rating);
        Assert.Equal(new[] { "Action" }, movie.Genres);

        var show = fetch.Document.Find("tv", 63639, null, null);
        Assert.NotNull(show);
        Assert.Equal("Remember the Cant", show!.Episodes["1:3"].Title);
    }

    [Fact]
    public async Task TheDocumentIsFoundBesideThePlan()
    {
        var stub = new Stub(_ => Json(DocumentJson));
        await Client(stub).FetchAsync("https://l.uk/abc.def/manifest.json", CancellationToken.None);
        Assert.Equal("https://l.uk/abc.def/jellyfin/metadata.json", stub.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task AMalformedDocumentKeepsTheCachedCopy()
    {
        await Client(new Stub(_ => Json(DocumentJson))).FetchAsync("https://l.uk/abc.def", CancellationToken.None);
        var before = File.ReadAllText(_cache);

        var fetch = await Client(new Stub(_ => Json("{ not json")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Cache, fetch.Source);
        Assert.NotNull(fetch.Document.Find("movie", 603, null, null));
        Assert.Equal(before, File.ReadAllText(_cache));
    }

    [Fact]
    public async Task ANetworkFailureKeepsTheLastKnownGoodDocument()
    {
        await Client(new Stub(_ => Json(DocumentJson))).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        var fetch = await Client(new Stub(_ => throw new HttpRequestException("down")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Cache, fetch.Source);
        Assert.NotNull(fetch.Document.Find("movie", 603, null, null));
    }

    [Fact]
    public async Task ANotModifiedAnswerUsesTheCache()
    {
        var first = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(DocumentJson, Encoding.UTF8, "application/json"),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-1\"") },
        });
        await Client(first).FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        var cached = new Stub(request =>
        {
            var value = request.Headers.TryGetValues("If-None-Match", out var values)
                ? string.Join(",", values)
                : string.Empty;
            Assert.Equal("\"etag-1\"", value);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        var fetch = await Client(cached).FetchAsync("https://l.uk/abc.def", CancellationToken.None);
        Assert.True(fetch.Ok);
        Assert.Equal(PlanSource.Cache, fetch.Source);
        Assert.NotNull(fetch.Document.Find("tv", 63639, null, null));
    }

    [Fact]
    public async Task ADocumentWithNoTitlesIsEmptyRatherThanBroken()
    {
        var fetch = await Client(new Stub(_ => Json("""{"schemaVersion":1}""")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.True(fetch.Ok);
        Assert.Empty(fetch.Document.Titles);
    }

    [Fact]
    public async Task ANetworkFailureWithoutACacheIsAFailure()
    {
        var fetch = await Client(new Stub(_ => throw new HttpRequestException("down")))
            .FetchAsync("https://l.uk/abc.def", CancellationToken.None);

        Assert.False(fetch.Ok);
        Assert.Empty(fetch.Document.Titles);
    }

    [Fact]
    public void AFindFallsBackToTheOtherIds()
    {
        var document = new MetadataDocument
        {
            Titles = new System.Collections.Generic.Dictionary<string, MetadataTitle>
            {
                ["movie:tt0133093"] = new() { Kind = "movie", Title = "The Matrix" },
            },
        };

        Assert.NotNull(document.Find("movie", 999, "tt0133093", null));
        Assert.Null(document.Find("tv", 999, "tt0133093", null));
        Assert.Null(document.Find("movie", null, null, null));
    }

    [Fact]
    public void AFindLooksForATvdbIdUnderItsOwnNamespace()
    {
        var document = new MetadataDocument
        {
            Titles = new System.Collections.Generic.Dictionary<string, MetadataTitle>
            {
                ["movie:tvdb:603"] = new() { Kind = "movie", Title = "A TVDB-only title" },
            },
        };

        // 603 is also a TMDB id in the wild; the TVDB fallback must not find
        // the other title.
        Assert.Null(document.Find("movie", 603, null, null));
        Assert.NotNull(document.Find("movie", null, null, 603));
    }
}
