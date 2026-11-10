using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Playback;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.LeLibrary.Virtual;

/// <summary>
/// Supplies the playback source for every virtual item. Jellyfin asks this when
/// something plays, so the link is minted fresh instead of being frozen in a
/// file, and nothing is probed or downloaded first.
/// </summary>
public sealed class LeLibraryMediaSourceProvider : IMediaSourceProvider
{
    private readonly LinkCache _links;
    private readonly Func<string> _playBase;

    /// <summary>Initializes a new instance of the <see cref="LeLibraryMediaSourceProvider"/> class.</summary>
    /// <param name="links">The link cache.</param>
    /// <param name="playBase">The server URL players reach us at.</param>
    public LeLibraryMediaSourceProvider(LinkCache links, Func<string> playBase)
    {
        _links = links;
        _playBase = playBase;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var key = VirtualIdentity.KeyFromPath(item?.Path);
        if (key is null) return Array.Empty<MediaSourceInfo>();

        // Mint once here so a missing file is a clean 410 at play time instead
        // of a source that fails mid-stream.
        var mint = await _links.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (mint.Status != Providers.MintStatus.Ok) return Array.Empty<MediaSourceInfo>();

        var baseUrl = _playBase().TrimEnd('/');
        return new[]
        {
            new MediaSourceInfo
            {
                Protocol = MediaProtocol.Http,
                Id = key,
                Path = $"{baseUrl}/LeLibrary/play/{key}",
                Name = item!.Name,
                IsRemote = true,
                SupportsProbing = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Type = MediaSourceType.Default,
                Container = "mkv",
                RunTimeTicks = item.RunTimeTicks,
            },
        };
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => throw new NotSupportedException("LeLibrary has no live streams");
}
