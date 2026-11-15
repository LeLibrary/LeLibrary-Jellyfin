using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Standalone.TorBox.Providers;

/// <summary>
/// Reads TorBox torrent, web and Usenet downloads as distinct source families.
/// </summary>
public sealed class TorboxClient : IDebridClient
{
    private const int PageSize = 1000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RequestPacer> Pacers = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly bool _includeWebDownloads;
    private readonly bool _includeUsenet;

    /// <summary>Initializes a new instance of the <see cref="TorboxClient"/> class.</summary>
    /// <param name="http">The HTTP client (base address https://api.torbox.app/).</param>
    /// <param name="apiKey">The user's own TorBox key.</param>
    public TorboxClient(HttpClient http, string apiKey, bool includeWebDownloads = false, bool includeUsenet = false)
    {
        _http = http;
        _apiKey = apiKey;
        _includeWebDownloads = includeWebDownloads;
        _includeUsenet = includeUsenet;
    }

    /// <inheritdoc />
    public string Provider => "torbox";

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri;
        var endpoint = uri is null ? string.Empty : uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
        var pacer = Pacers.GetOrAdd(endpoint, _ => new RequestPacer());
        await pacer.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var delay = pacer.Next - Environment.TickCount64;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
            pacer.Next = Environment.TickCount64 + 750;
        }
        finally { pacer.Gate.Release(); }
        return await _http.SendAsync(request, token).ConfigureAwait(false);
    }

    private sealed class RequestPacer
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Next { get; set; }
    }

    /// <inheritdoc />
    public async Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken)
    {
        var files = new List<DebridFile>();
        var families = new List<(string Path, string Prefix)> { ("torrents", "") };
        if (_includeWebDownloads) families.Add(("webdl", "W"));
        if (_includeUsenet) families.Add(("usenet", "U"));
        foreach (var family in families)
        {
        var seen = new HashSet<long>();
        for (var offset = 0; ; offset += PageSize)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"v1/api/{family.Path}/mylist?bypass_cache=true&limit={PageSize}&offset={offset}");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception err) when (err is HttpRequestException or TaskCanceledException)
            {
                return new ProviderListing(ProviderHealth.Unavailable, new List<DebridFile>(), "TorBox could not be reached");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new ProviderListing(ProviderHealth.InvalidKey, new List<DebridFile>(), "TorBox rejected the API key");
                }

                if ((int)response.StatusCode == 429)
                {
                    return new ProviderListing(ProviderHealth.RateLimited, new List<DebridFile>(), "TorBox is rate limiting this account");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new ProviderListing(ProviderHealth.Unavailable, new List<DebridFile>(), $"TorBox answered {(int)response.StatusCode}");
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                List<(long TorrentId, string? ReleaseName, List<(long? Id, string? Name, long Size)> Files)> page;
                try
                {
                    page = ReadTorrents(body, family.Path != "torrents");
                }
                catch (JsonException)
                {
                    return new ProviderListing(ProviderHealth.Unavailable, new List<DebridFile>(), "TorBox returned an incomplete or invalid listing");
                }
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var (torrentId, releaseName, torrentFiles) in page)
                {
                    if (!seen.Add(torrentId))
                        return new ProviderListing(ProviderHealth.Unavailable, new List<DebridFile>(), "TorBox pages overlapped; try syncing again");
                    foreach (var file in torrentFiles)
                    {
                        if (file.Id is null) continue;
                        files.Add(new DebridFile(
                            $"{Provider}:{family.Prefix}{torrentId}:{file.Id.Value}",
                            file.Name ?? string.Empty,
                            file.Size,
                            releaseName));
                    }
                }

                // A short page can still be followed by more (the API is not
                // strict about the limit), so only an empty page ends the walk.
                // The page cap is a backstop against a provider that always
                // answers: 2,000 pages is 200,000 downloads.
                if (offset >= PageSize * 2000)
                {
                    return new ProviderListing(ProviderHealth.Unavailable, new List<DebridFile>(), "TorBox listing exceeded the page limit");
                }
            }
        }

        }
        return new ProviderListing(ProviderHealth.Healthy, files, null);
    }

    /// <inheritdoc />
    public async Task<MintResult> MintAsync(string key, CancellationToken cancellationToken)
    {
        if (!PlanKey.TryParse(key, out var parsed) || parsed.Provider != Provider)
        {
            return MintResult.Fail(MintStatus.NotFound);
        }

        var family = parsed.ItemId.StartsWith("W", StringComparison.Ordinal) ? "webdl"
            : parsed.ItemId.StartsWith("U", StringComparison.Ordinal) ? "usenet" : "torrents";
        var itemId = family == "torrents" ? parsed.ItemId : parsed.ItemId[1..];
        if (!long.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return MintResult.Fail(MintStatus.NotFound);
        var idParameter = family == "webdl" ? "web_id" : family == "usenet" ? "usenet_id" : "torrent_id";
        var url = $"v1/api/{family}/requestdl?token={Uri.EscapeDataString(_apiKey)}"
            + $"&{idParameter}={Uri.EscapeDataString(itemId)}"
            + $"&file_id={Uri.EscapeDataString(parsed.FileId)}"
            + "&zip_link=false";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            try
            {
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500 && attempt == 0)
                {
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return MintResult.Fail(MintStatus.NotFound);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return MintResult.Fail(MintStatus.Error);
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                {
                    return MintResult.Fail(MintStatus.Error);
                }

                return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                    ? new MintResult(MintStatus.Ok, data.GetString())
                    : MintResult.Fail(MintStatus.Error);
            }
            catch (Exception err) when (err is HttpRequestException or JsonException or TaskCanceledException)
            {
                if (attempt == 0) continue;
                return MintResult.Fail(MintStatus.Error);
            }
        }

        return MintResult.Fail(MintStatus.Error);
    }

    /// <inheritdoc />
    public async Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/api/user/me");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        try
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ProviderHealth.InvalidKey;
            }

            if ((int)response.StatusCode == 429) return ProviderHealth.RateLimited;
            if (!response.IsSuccessStatusCode) return ProviderHealth.Unavailable;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                return ProviderHealth.InvalidKey;
            }

            return ProviderHealth.Healthy;
        }
        catch (Exception err) when (err is HttpRequestException or JsonException or TaskCanceledException)
        {
            return ProviderHealth.Unavailable;
        }
    }

    private static List<(long TorrentId, string? ReleaseName, List<(long? Id, string? Name, long Size)> Files)> ReadTorrents(string body, bool requirePresent)
    {
        var result = new List<(long, string?, List<(long?, string?, long)>)>();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            throw new JsonException("TorBox did not return a successful listing");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("TorBox did not return a listing array");
        }

        foreach (var torrent in data.EnumerateArray())
        {
            if (torrent.ValueKind != JsonValueKind.Object || !torrent.TryGetProperty("id", out var idElement) || !TryLong(idElement, out var torrentId))
            {
                throw new JsonException("TorBox returned a torrent without an id");
            }

            var files = new List<(long?, string?, long)>();
            if (requirePresent)
            {
                if (!torrent.TryGetProperty("download_present", out var available)
                    || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException("TorBox returned a download without its availability");
                if (available.ValueKind == JsonValueKind.False)
                {
                    result.Add((torrentId, null, files));
                    continue;
                }
            }
            // TorBox can return files: null while a download is still being
            // checked or finalized. Keep its id in the pagination snapshot,
            // but do not expose an unplayable item or fail the whole account.
            if (torrent.TryGetProperty("download_present", out var present)
                && present.ValueKind == JsonValueKind.False
                && torrent.TryGetProperty("download_state", out var state)
                && state.ValueKind == JsonValueKind.String
                && IsPendingState(state.GetString()))
            {
                result.Add((torrentId, null, files));
                continue;
            }
            if (!torrent.TryGetProperty("files", out var fileArray) || fileArray.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("TorBox returned a torrent without its files");
            }

            if (fileArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in fileArray.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("TorBox returned an invalid file");
                    }
                    long? fileId = file.TryGetProperty("id", out var fileIdElement) && TryLong(fileIdElement, out var parsedId)
                        ? parsedId
                        : null;
                    if (fileId is null)
                    {
                        throw new JsonException("TorBox returned a file without an id");
                    }
                    var name = file.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : null;
                    long size = file.TryGetProperty("size", out var sizeElement) && TryLong(sizeElement, out var parsedSize)
                        ? parsedSize
                        : 0;
                    files.Add((fileId, name, size));
                }
            }

            var releaseName = torrent.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null;
            result.Add((torrentId, releaseName, files));
        }

        return result;
    }

    private static bool IsPendingState(string? value)
        => value?.Trim().ToLowerInvariant() is "checking" or "checkingresumedata" or "downloading"
            or "metadl" or "processing" or "uploading" or "uploading (no peers)"
            or "stalled" or "stalled (no seeds)" or "stalleddl" or "paused";

    private static bool TryLong(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}
