using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Model;

namespace Jellyfin.Plugin.LeLibrary.Client;

/// <summary>The outcome of one metadata fetch.</summary>
/// <param name="Ok">Whether there is a usable document.</param>
/// <param name="Error">A readable reason when something went wrong.</param>
/// <param name="Document">The document (empty when !Ok).</param>
/// <param name="Source">Network or cache.</param>
public sealed record MetadataFetch(bool Ok, string? Error, MetadataDocument Document, PlanSource Source)
{
    /// <summary>Gets an empty failed fetch.</summary>
    /// <param name="error">Why.</param>
    /// <returns>The fetch result.</returns>
    public static MetadataFetch Failed(string error) => new(false, error, MetadataDocument.Empty, PlanSource.Network);
}

/// <summary>
/// Reads the metadata document from the setup install URL and keeps the last
/// good copy on disk, beside the plan's cache.
///
/// It has its own ETag and its own cache file: a new overview must not
/// invalidate the 13 MB plan, and a new plan must not invalidate the metadata.
/// </summary>
public sealed class MetadataClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The document is camelCase and these types are PascalCase.
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _cachePath;

    /// <summary>Initializes a new instance of the <see cref="MetadataClient"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="cachePath">Where the last known good document is kept.</param>
    public MetadataClient(HttpClient http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
    }

    /// <summary>Fetches the metadata document.</summary>
    /// <param name="setupUrl">The setup install URL (with or without /manifest.json).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The document and where it came from.</returns>
    public async Task<MetadataFetch> FetchAsync(string? setupUrl, CancellationToken cancellationToken)
    {
        var url = MetadataUrl(setupUrl);
        if (url is null)
        {
            return MetadataFetch.Failed("the LeLibrary URL is not set");
        }

        var cached = ReadCache();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(cached?.Etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached!.Etag);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception err) when (err is HttpRequestException or TaskCanceledException)
        {
            return Fallback(cached, "LeLibrary could not be reached");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Fallback(cached, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fallback(cached, $"LeLibrary answered {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            MetadataDocument document;
            try
            {
                document = Parse(body);
            }
            catch (JsonException)
            {
                // A 200 with something unreadable is a failure: the cached copy
                // stays, and the library is not blanked.
                return Fallback(cached, "the metadata document could not be read");
            }

            var etag = response.Headers.ETag?.ToString();
            WriteCache(new CachedMetadata(etag, body));
            return new MetadataFetch(true, null, document, PlanSource.Network);
        }
    }

    /// <summary>
    /// The metadata URL: the plan's, with the last segment changed. Both are
    /// derived from the same setup URL, so the two can never drift.
    /// </summary>
    /// <param name="setupUrl">The pasted value.</param>
    /// <returns>The metadata URL, or null when the setup URL is not usable.</returns>
    public static string? MetadataUrl(string? setupUrl)
    {
        var plan = LeLibraryClient.PlanUrl(setupUrl);
        if (plan is null)
        {
            return null;
        }

        // ".../jellyfin/config.json" -> ".../jellyfin/metadata.json"
        return plan[..^"config.json".Length] + "metadata.json";
    }

    private static MetadataDocument Parse(string body)
        => JsonSerializer.Deserialize<MetadataDocument>(body, JsonOptions) ?? MetadataDocument.Empty;

    private static MetadataFetch Fallback(CachedMetadata? cached, string? error)
    {
        if (cached is null || string.IsNullOrWhiteSpace(cached.Body))
        {
            return MetadataFetch.Failed(error ?? "no cached metadata");
        }

        try
        {
            return new MetadataFetch(true, error, Parse(cached.Body), PlanSource.Cache);
        }
        catch (JsonException)
        {
            return MetadataFetch.Failed(error ?? "the cached metadata could not be read");
        }
    }

    private CachedMetadata? ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            return JsonSerializer.Deserialize<CachedMetadata>(File.ReadAllText(_cachePath));
        }
        catch (Exception err) when (err is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCache(CachedMetadata metadata)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(metadata));
            File.Move(temp, _cachePath, true);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException)
        {
            // A cache write failure must not fail the sync; the next fetch just
            // starts from scratch.
        }
    }

    private sealed record CachedMetadata(string? Etag, string? Body);
}
