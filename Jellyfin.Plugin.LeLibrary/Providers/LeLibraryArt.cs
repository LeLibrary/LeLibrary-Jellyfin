using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LeLibrary.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LeLibrary.Providers;

/// <summary>
/// Puts the document's artwork on an item, as URLs.
///
/// The plugin never downloads an image: Jellyfin fetches a non-local image on
/// the first request that wants it and caches it on disk. That is the path the
/// plan's poster has always taken, and it keeps the plugin out of Jellyfin's
/// image pipeline, which is where the <c>SemaphoreFullException</c> came from.
/// </summary>
public static class LeLibraryArt
{
    /// <summary>Attaches the document's art to an item, looking it up by the item's own ids.</summary>
    /// <param name="item">The item.</param>
    /// <param name="document">The document.</param>
    public static void Attach(BaseItem item, MetadataDocument? document)
    {
        var title = MetadataLookup.For(item, document);
        var episode = item is MediaBrowser.Controller.Entities.TV.Episode value
            ? MetadataLookup.EpisodeFor(value, title)
            : null;
        Attach(item, title, episode);
    }

    /// <summary>
    /// Attaches one title's art, and one episode's still when the item is an
    /// episode. A type the document has nothing for is left as it is: the art
    /// is never removed here.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="title">The title's metadata, or null.</param>
    /// <param name="episode">The episode's metadata when the item is an episode, or null.</param>
    public static void Attach(BaseItem item, MetadataTitle? title, MetadataEpisode? episode)
    {
        if (episode is not null)
        {
            // An episode's own image is its still. Without one it keeps what it
            // has and Jellyfin shows its series' art.
            if (!string.IsNullOrWhiteSpace(episode.Still))
            {
                Set(item, ImageType.Primary, episode.Still!);
            }

            return;
        }

        if (title is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(title.Poster))
        {
            Set(item, ImageType.Primary, title.Poster!);
        }

        if (!string.IsNullOrWhiteSpace(title.Backdrop))
        {
            Set(item, ImageType.Backdrop, title.Backdrop!);
        }
    }

    private static void Set(BaseItem item, ImageType type, string url)
    {
        var images = item.ImageInfos ?? Array.Empty<ItemImageInfo>();
        var existing = images.Where(image => image.Type == type).ToList();
        if (existing.Count == 1 && string.Equals(existing[0].Path, url, StringComparison.Ordinal))
        {
            // Already right: leaving the array alone keeps an unchanged item
            // unchanged, so a sync does not rewrite it.
            return;
        }

        var kept = images.Where(image => image.Type != type).ToList();
        kept.Add(new ItemImageInfo { Path = url, Type = type });
        item.ImageInfos = kept.ToArray();
    }
}
