using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.TorBox.Playback;
using Jellyfin.Plugin.Standalone.TorBox.Virtual;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Standalone.TorBox.Virtual;

/// <summary>
/// Tells Jellyfin what our items are: a remote stream of a known container.
///
/// Without this, Jellyfin has a URL with no probe result, so it cannot name an
/// output extension and refuses to stream. Everything here is derived from the
/// file name the item was created with.
/// </summary>
public sealed class StandaloneMediaSourceProvider : IMediaSourceProvider
{
    private readonly LinkCache _links;
    private readonly MediaBrowser.Controller.IServerApplicationHost _appHost;

    /// <summary>Initializes a new instance of the <see cref="StandaloneMediaSourceProvider"/> class.</summary>
    /// <param name="links">The link cache.</param>
    /// <param name="appHost">The server host, used when no server URL is set.</param>
    public StandaloneMediaSourceProvider(LinkCache links, MediaBrowser.Controller.IServerApplicationHost appHost)
    {
        _links = links;
        _appHost = appHost;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is null || !item.ProviderIds.TryGetValue(VirtualIdentity.ProviderId, out var key) || string.IsNullOrEmpty(key))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // Never mint here. If the provider is rate limited at this moment, the
        // item would fall back to a source with no container and refuse to
        // stream; the resolver mints when the bytes are actually requested.
        _ = _links;
        _ = cancellationToken;
        await Task.CompletedTask.ConfigureAwait(false);

        var container = ContainerOf(item.Path);
        return new[]
        {
            new MediaSourceInfo
            {
                Protocol = MediaProtocol.Http,
                Id = key,
                Path = item.Path,
                Name = item.Name,
                IsRemote = true,
                Container = container,
                SupportsProbing = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Type = MediaSourceType.Default,
                RunTimeTicks = item.RunTimeTicks,
                MediaStreams = Array.Empty<MediaStream>(),
            },
        };
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => throw new NotSupportedException("Standalone has no live streams");

    private static string ContainerOf(string? path)
    {
        var extension = System.IO.Path.GetExtension(path ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "mkv" => "mkv",
            "mp4" => "mp4",
            "m4v" => "mp4",
            "avi" => "avi",
            "ts" => "ts",
            "webm" => "webm",
            "mov" => "mov",
            _ => "mkv",
        };
    }
}
