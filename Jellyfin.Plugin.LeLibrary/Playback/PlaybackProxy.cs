using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Providers;
using Jellyfin.Plugin.LeLibrary.Sync;

namespace Jellyfin.Plugin.LeLibrary.Playback;

/// <summary>One answer from the playback proxy.</summary>
/// <param name="Status">The HTTP status to send.</param>
/// <param name="ContentType">The upstream content type.</param>
/// <param name="ContentLength">The upstream length, when known.</param>
/// <param name="ContentRange">The upstream content range, when partial.</param>
/// <param name="AcceptRanges">The upstream accept-ranges value.</param>
/// <param name="Body">The stream to copy to the client (null for errors).</param>
/// <param name="Message">A readable message for an error status.</param>
public sealed record ProxyResponse(
    int Status,
    string? ContentType,
    long? ContentLength,
    string? ContentRange,
    string? AcceptRanges,
    Stream? Body,
    string? Message);

/// <summary>
/// The play endpoint's work, without ASP.NET: mint a link, forward the Range
/// request, hand back the upstream stream. Nothing is buffered.
/// </summary>
public sealed class PlaybackProxy
{
    private readonly Func<IDebridClient> _provider;
    private readonly LinkCache _links;
    private readonly HttpClient _http;

    /// <summary>Initializes a new instance of the <see cref="PlaybackProxy"/> class.</summary>
    /// <param name="provider">Builds the debrid client for the current settings.</param>
    /// <param name="links">The link cache.</param>
    /// <param name="http">The streaming HTTP client (no timeout).</param>
    public PlaybackProxy(Func<IDebridClient> provider, LinkCache links, HttpClient http)
    {
        _provider = provider;
        _links = links;
        _http = http;
    }

    /// <summary>Opens a playback stream.</summary>
    /// <param name="key">The plan key from the .strm file.</param>
    /// <param name="range">The incoming Range header.</param>
    /// <param name="ifRange">The incoming If-Range header.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What to send back.</returns>
    public async Task<ProxyResponse> OpenAsync(string? key, string? range, string? ifRange, CancellationToken cancellationToken)
    {
        var provider = _provider();
        if (!PathBuilder.TryParseKey(key, out var parsed) || parsed.Provider != provider.Provider)
        {
            return Error(400, "not a playable LeLibrary key");
        }

        var mint = await _links.GetAsync(key!, cancellationToken).ConfigureAwait(false);
        if (mint.Status == MintStatus.NotFound)
        {
            _links.Forget(key!);
            return Error(410, "this file is no longer in the debrid account");
        }

        if (mint.Status != MintStatus.Ok || string.IsNullOrEmpty(mint.Url))
        {
            return Error(502, $"could not get a link from {DisplayName(provider.Provider)}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, mint.Url);
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (!string.IsNullOrWhiteSpace(ifRange))
        {
            request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception err) when (err is HttpRequestException or TaskCanceledException)
        {
            _links.Forget(key!);
            return Error(502, "the debrid download could not be reached");
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            // A signed link that expired looks like a client error; forget it so
            // the next attempt mints a fresh one.
            if ((int)response.StatusCode is 401 or 403 or 404)
            {
                _links.Forget(key!);
            }

            return Error(502, "the debrid download answered " + (int)response.StatusCode);
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new ProxyResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentRange?.ToString(),
            response.Headers.TryGetValues("Accept-Ranges", out var ranges) ? string.Join(",", ranges) : null,
            body,
            null);
    }

    private static ProxyResponse Error(int status, string message)
        => new(status, null, null, null, null, null, message);

    private static string DisplayName(string provider) => provider switch
    {
        "torbox" => "TorBox",
        "realdebrid" => "Real-Debrid",
        "alldebrid" => "AllDebrid",
        "premiumize" => "Premiumize",
        _ => provider,
    };
}
