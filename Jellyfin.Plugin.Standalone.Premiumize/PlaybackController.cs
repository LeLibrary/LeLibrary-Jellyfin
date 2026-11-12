using System.Threading;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.Premiumize.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Standalone.Premiumize;

/// <summary>
/// The play endpoint the .strm files point at. Anonymous on purpose: the plan
/// key in the URL is the capability, exactly like a Standalone install link, and
/// players never send a Jellyfin token for a remote stream.
/// </summary>
[ApiController]
[Route("StandalonePremiumize")]
public sealed class PlaybackController : ControllerBase
{
    private readonly PlaybackProxy _proxy;

    /// <summary>Initializes a new instance of the <see cref="PlaybackController"/> class.</summary>
    /// <param name="proxy">The playback proxy.</param>
    public PlaybackController(PlaybackProxy proxy)
    {
        _proxy = proxy;
    }

    /// <summary>Streams one file from the user's debrid account.</summary>
    /// <param name="key">The plan key: provider:itemId:fileId.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream, or an error status with a readable message.</returns>
    [HttpGet("play/{secret}/{key}/{fileName}")]
    [HttpHead("play/{secret}/{key}/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> Play(string secret, string key, string? fileName, CancellationToken cancellationToken)
    {
        var expected = Plugin.Instance?.Configuration.StreamSecret;
        if (string.IsNullOrEmpty(expected) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(expected))) return NotFound();
        var range = Request.Headers.TryGetValue("Range", out var rangeValue) ? rangeValue.ToString() : null;
        var ifRange = Request.Headers.TryGetValue("If-Range", out var ifRangeValue) ? ifRangeValue.ToString() : null;
        var result = await _proxy.OpenAsync(key, range, ifRange, cancellationToken).ConfigureAwait(false);

        if (result.Body is null)
        {
            return StatusCode(result.Status, result.Message);
        }

        Response.StatusCode = result.Status;
        // Jellyfin builds the item's container from this URL's extension, and a
        // player uses the response type; upstream often says octet-stream.
        var contentType = result.ContentType;
        if (string.IsNullOrEmpty(contentType) || contentType == "application/octet-stream")
        {
            contentType = ContentTypeFor(fileName, result.ContentType);
        }

        Response.ContentType = contentType ?? "application/octet-stream";
        if (result.ContentLength is not null) Response.ContentLength = result.ContentLength;
        if (result.ContentRange is not null) Response.Headers["Content-Range"] = result.ContentRange;
        Response.Headers["Accept-Ranges"] = result.AcceptRanges ?? "bytes";

        // A probe is a HEAD: headers only, and the upstream body must be closed
        // or the connection leaks on every item Jellyfin inspects.
        if (HttpMethods.IsHead(Request.Method))
        {
            await result.Body.DisposeAsync().ConfigureAwait(false);
            return new EmptyResult();
        }

        await using (result.Body)
        {
            await result.Body.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    private static string? ContentTypeFor(string? fileName, string? fallback)
    {
        var extension = System.IO.Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".mkv" => "video/x-matroska",
            ".mp4" or ".m4v" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".ts" => "video/mp2t",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => fallback,
        };
    }
}
