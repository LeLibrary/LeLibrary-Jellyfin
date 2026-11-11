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
    /// <summary>
    /// The provider id holding the signature of the art the plugin wrote.
    ///
    /// Jellyfin downloads a non-local image on the first request and rewrites
    /// the item's path to the file it saved, so the item's own paths can never
    /// be compared with the document again. This records what was written, so
    /// a sync can tell "the art changed" from "Jellyfin saved it".
    /// </summary>
    public const string SignatureId = "LeLibraryArt";

    /// <summary>
    /// The signature of an item's art: its type and URL pairs, sorted. Computed
    /// from the item the plan built, never from a row Jellyfin has touched.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The signature, or an empty string when the item has no art.</returns>
    public static string Signature(BaseItem item)
        => string.Join(
            ";",
            (item.ImageInfos ?? Array.Empty<ItemImageInfo>())
                .OrderBy(image => image.Type)
                .ThenBy(image => image.Path, StringComparer.Ordinal)
                .Select(image => ((int)image.Type).ToString(System.Globalization.CultureInfo.InvariantCulture) + "=" + image.Path));

    /// <summary>
    /// Copies the wanted item's images onto a row, one type at a time.
    ///
    /// A type the wanted item has nothing for is left alone, so a metadata
    /// outage — which leaves the wanted item with the plan's poster and
    /// nothing else — cannot strip the artwork a row already has.
    /// </summary>
    /// <param name="current">The row.</param>
    /// <param name="wanted">The item the plan built.</param>
    /// <returns>True when an image was written.</returns>
    public static bool ApplyPerType(BaseItem current, BaseItem wanted)
    {
        var changed = false;
        foreach (var image in wanted.ImageInfos ?? Array.Empty<ItemImageInfo>())
        {
            var images = current.ImageInfos ?? Array.Empty<ItemImageInfo>();
            var existing = images.Where(row => row.Type == image.Type).ToList();
            if (existing.Count == 1 && string.Equals(existing[0].Path, image.Path, StringComparison.Ordinal))
            {
                continue;
            }

            var kept = images.Where(row => row.Type != image.Type).ToList();
            kept.Add(new ItemImageInfo { Path = image.Path, Type = image.Type });
            current.ImageInfos = kept.ToArray();
            changed = true;
        }

        return changed;
    }

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
