using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LeLibrary;

/// <summary>
/// The play endpoint the .strm files point at. Anonymous on purpose: the plan
/// key in the URL is the capability, exactly like a LeLibrary install link, and
/// players never send a Jellyfin token for a remote stream.
/// </summary>
[ApiController]
[Route("LeLibrary")]
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
    [HttpGet("play/{key}")]
    [AllowAnonymous]
    public async Task<IActionResult> Play(string key, CancellationToken cancellationToken)
    {
        var range = Request.Headers.TryGetValue("Range", out var rangeValue) ? rangeValue.ToString() : null;
        var ifRange = Request.Headers.TryGetValue("If-Range", out var ifRangeValue) ? ifRangeValue.ToString() : null;
        var result = await _proxy.OpenAsync(key, range, ifRange, cancellationToken).ConfigureAwait(false);

        if (result.Body is null)
        {
            return StatusCode(result.Status, result.Message);
        }

        Response.StatusCode = result.Status;
        Response.ContentType = result.ContentType ?? "application/octet-stream";
        if (result.ContentLength is not null) Response.ContentLength = result.ContentLength;
        if (result.ContentRange is not null) Response.Headers["Content-Range"] = result.ContentRange;
        Response.Headers["Accept-Ranges"] = result.AcceptRanges ?? "bytes";
        await using (result.Body)
        {
            await result.Body.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
    }
}
