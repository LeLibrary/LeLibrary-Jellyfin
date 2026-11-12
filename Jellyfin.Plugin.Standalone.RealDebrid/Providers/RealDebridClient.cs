using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Standalone.RealDebrid.Providers;

/// <summary>Reads Real-Debrid torrents and resolves selected files for playback.</summary>
public sealed class RealDebridClient : IDebridClient
{
    private const int PageSize = 100;
    private const int MaxPages = 2000;
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static long _nextRequest;
    private static readonly ConcurrentDictionary<string, CachedFiles> FileCache = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly string _key;

    /// <summary>Initializes a new instance of the <see cref="RealDebridClient"/> class.</summary>
    /// <param name="http">An HTTP client with the Real-Debrid API base address.</param>
    /// <param name="key">The account API key.</param>
    public RealDebridClient(HttpClient http, string key) { _http = http; _key = key; }

    /// <inheritdoc />
    public string Provider => "realdebrid";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextRequest - Environment.TickCount64;
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
            _nextRequest = Environment.TickCount64 + 300;
        }
        finally
        {
            RequestGate.Release();
        }
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static ProviderHealth Health(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderHealth.InvalidKey,
        HttpStatusCode.TooManyRequests => ProviderHealth.RateLimited,
        _ => ProviderHealth.Unavailable,
    };

    /// <inheritdoc />
    public async Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "user", null, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? ProviderHealth.Healthy : Health(response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return ProviderHealth.Unavailable;
        }
    }

    /// <inheritdoc />
    public async Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken)
    {
        var files = new List<DebridFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long? expected = null;
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                using var response = await SendAsync(HttpMethod.Get, $"torrents?page={page}&limit={PageSize}", null, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return Failed(Health(response.StatusCode));
                if (response.Headers.TryGetValues("X-Total-Count", out var values))
                {
                    if (!long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var countHeader)
                        || countHeader < 0 || (expected.HasValue && expected != countHeader)) return Failed(ProviderHealth.Unavailable);
                    expected = countHeader;
                }
                if (response.StatusCode == HttpStatusCode.NoContent)
                    return !expected.HasValue || expected == seen.Count ? new ProviderListing(ProviderHealth.Healthy, files, null) : Failed(ProviderHealth.Unavailable);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (json.RootElement.ValueKind != JsonValueKind.Array) return Failed(ProviderHealth.Unavailable);
                var count = json.RootElement.GetArrayLength();
                foreach (var torrent in json.RootElement.EnumerateArray())
                {
                    if (torrent.ValueKind != JsonValueKind.Object || !String(torrent, "id", out var id)
                        || !String(torrent, "status", out var status) || !PlanKey.TryParse($"realdebrid:{id}:1", out _))
                        return Failed(ProviderHealth.Unavailable);
                    if (!seen.Add(id)) return Failed(ProviderHealth.Unavailable);
                    if (status != "downloaded") continue;
                    String(torrent, "filename", out var releaseName);
                    var cacheKey = CacheKey(torrent, id, releaseName);
                    if (cacheKey is not null && FileCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                    {
                        files.AddRange(cached.Files);
                        continue;
                    }
                    using var detailResponse = await SendAsync(HttpMethod.Get, "torrents/info/" + Uri.EscapeDataString(id), null, cancellationToken).ConfigureAwait(false);
                    if (!detailResponse.IsSuccessStatusCode) return Failed(Health(detailResponse.StatusCode));
                    using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    var detailFiles = new List<DebridFile>();
                    if (!ReadFiles(detail.RootElement, id, releaseName, detailFiles)) return Failed(ProviderHealth.Unavailable);
                    files.AddRange(detailFiles);
                    if (cacheKey is not null)
                    {
                        if (FileCache.Count >= 20000)
                            foreach (var entry in FileCache.Where(entry => entry.Value.Expires <= DateTimeOffset.UtcNow)) FileCache.TryRemove(entry.Key, out _);
                        if (FileCache.Count < 20000) FileCache[cacheKey] = new CachedFiles(DateTimeOffset.UtcNow.AddMinutes(15), detailFiles.ToArray());
                    }
                }
                if (count == 0)
                    return !expected.HasValue || expected == seen.Count ? new ProviderListing(ProviderHealth.Healthy, files, null) : Failed(ProviderHealth.Unavailable);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return Failed(ProviderHealth.Unavailable);
        }
        return Failed(ProviderHealth.Unavailable);
    }

    private static ProviderListing Failed(ProviderHealth health) => new(health, Array.Empty<DebridFile>(), "Real-Debrid did not return a complete listing");

    private string? CacheKey(JsonElement torrent, string id, string name)
    {
        if (!String(torrent, "hash", out var hash) || string.IsNullOrWhiteSpace(name)
            || !torrent.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out var size)) return null;
        var account = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_key)));
        return string.Join(":", account, id, hash, size.ToString(CultureInfo.InvariantCulture), name);
    }

    private sealed record CachedFiles(DateTimeOffset Expires, IReadOnlyList<DebridFile> Files);

    private static bool ReadFiles(JsonElement root, string id, string releaseName, List<DebridFile> output)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return false;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("id", out var fid) || !fid.TryGetInt64(out var fileId)
                || !file.TryGetProperty("selected", out var selected) || !selected.TryGetInt32(out var isSelected)) return false;
            if (isSelected == 0) continue;
            if (isSelected != 1 || !String(file, "path", out var path)) return false;
            var name = System.IO.Path.GetFileName(path.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) return false;
            var size = file.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var n) ? n : 0;
            output.Add(new DebridFile($"realdebrid:{id}:{fileId.ToString(CultureInfo.InvariantCulture)}", name, size, releaseName));
        }
        return true;
    }

    private static bool String(JsonElement item, string property, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <inheritdoc />
    public async Task<MintResult> MintAsync(string key, CancellationToken cancellationToken)
    {
        if (!PlanKey.TryParse(key, out var parsed) || parsed.Provider != Provider) return MintResult.Fail(MintStatus.NotFound);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "torrents/info/" + Uri.EscapeDataString(parsed.ItemId), null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return MintResult.Fail(MintStatus.NotFound);
            if (!response.IsSuccessStatusCode) return MintResult.Fail(MintStatus.Error);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array) return MintResult.Fail(MintStatus.Error);
            var index = 0;
            string? link = null;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("id", out var id) || !id.TryGetInt64(out var fileId)
                    || !file.TryGetProperty("selected", out var selected) || !selected.TryGetInt32(out var isSelected)) return MintResult.Fail(MintStatus.Error);
                if (isSelected == 0) continue;
                if (isSelected != 1) return MintResult.Fail(MintStatus.Error);
                if (fileId.ToString(CultureInfo.InvariantCulture) == parsed.FileId)
                {
                    if (index >= links.GetArrayLength() || links[index].ValueKind != JsonValueKind.String) return MintResult.Fail(MintStatus.Error);
                    link = links[index].GetString();
                    break;
                }
                index++;
            }
            if (string.IsNullOrEmpty(link)) return MintResult.Fail(MintStatus.NotFound);
            using var unrestrict = await SendAsync(HttpMethod.Post, "unrestrict/link", new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", link) }), cancellationToken).ConfigureAwait(false);
            if (!unrestrict.IsSuccessStatusCode) return MintResult.Fail(MintStatus.Error);
            using var result = JsonDocument.Parse(await unrestrict.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return result.RootElement.ValueKind == JsonValueKind.Object && String(result.RootElement, "download", out var url)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? new MintResult(MintStatus.Ok, url) : MintResult.Fail(MintStatus.Error);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return MintResult.Fail(MintStatus.Error);
        }
    }
}
