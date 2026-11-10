using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Sync;

namespace Jellyfin.Plugin.LeLibrary.Providers;

/// <summary>
/// TorBox: torrents only. Usenet and web downloads number their items in the
/// same namespace as torrents, so they are a separate family and not part of a
/// torrent plan.
/// </summary>
public sealed class TorboxClient : IDebridClient
{
    private const int PageSize = 100;

    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <summary>Initializes a new instance of the <see cref="TorboxClient"/> class.</summary>
    /// <param name="http">The HTTP client (base address https://api.torbox.app/).</param>
    /// <param name="apiKey">The user's own TorBox key.</param>
    public TorboxClient(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    public string Provider => "torbox";

    /// <inheritdoc />
    public async Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken)
    {
        var files = new List<DebridFile>();
        for (var offset = 0; ; offset += PageSize)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"v1/api/torrents/mylist?bypass_cache=true&limit={PageSize}&offset={offset}");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
                var page = ReadTorrents(body);
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var (torrentId, torrentFiles) in page)
                {
                    foreach (var file in torrentFiles)
                    {
                        if (file.Id is null) continue;
                        files.Add(new DebridFile(
                            PathBuilder.Key(Provider, torrentId, file.Id.Value),
                            file.Name ?? string.Empty,
                            file.Size));
                    }
                }

                // A short page can still be followed by more (the API is not
                // strict about the limit), so only an empty page ends the walk.
                // The page cap is a backstop against a provider that always
                // answers: 2,000 pages is 200,000 downloads.
                if (offset >= PageSize * 2000)
                {
                    break;
                }
            }
        }

        return new ProviderListing(ProviderHealth.Healthy, files, null);
    }

    /// <inheritdoc />
    public async Task<MintResult> MintAsync(string key, CancellationToken cancellationToken)
    {
        if (!PathBuilder.TryParseKey(key, out var parsed) || parsed.Provider != Provider)
        {
            return MintResult.Fail(MintStatus.NotFound);
        }

        var url = $"v1/api/torrents/requestdl?token={Uri.EscapeDataString(_apiKey)}"
            + $"&torrent_id={Uri.EscapeDataString(parsed.ItemId)}"
            + $"&file_id={Uri.EscapeDataString(parsed.FileId)}"
            + "&zip_link=false";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static List<(long TorrentId, List<(long? Id, string? Name, long Size)> Files)> ReadTorrents(string body)
    {
        var result = new List<(long, List<(long?, string?, long)>)>();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            return result;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var torrent in data.EnumerateArray())
        {
            if (!torrent.TryGetProperty("id", out var idElement) || !TryLong(idElement, out var torrentId))
            {
                continue;
            }

            var files = new List<(long?, string?, long)>();
            if (torrent.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in fileArray.EnumerateArray())
                {
                    long? fileId = file.TryGetProperty("id", out var fileIdElement) && TryLong(fileIdElement, out var parsedId)
                        ? parsedId
                        : null;
                    var name = file.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : null;
                    long size = file.TryGetProperty("size", out var sizeElement) && TryLong(sizeElement, out var parsedSize)
                        ? parsedSize
                        : 0;
                    files.Add((fileId, name, size));
                }
            }

            result.Add((torrentId, files));
        }

        return result;
    }

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
