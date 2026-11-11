using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.LeLibrary.Providers;

/// <summary>
/// Tells Jellyfin which providers may fetch metadata and images for the
/// LeLibrary libraries.
///
/// Jellyfin looks the options up by the item's CLR type name — "Movie",
/// "Series", "Season", "Episode" — and when an entry exists, its
/// <c>MetadataFetchers</c> and <c>ImageFetchers</c> are <em>allow-lists of
/// provider names</em>. Jellyfin's own TMDb and TVDB providers are called
/// <c>TheMovieDb</c> and <c>TheTVDB</c>, so an allow-list of <c>LeLibrary</c>
/// stands them down. A custom metadata provider is never gated by the list, so
/// ours still runs.
///
/// The entry is written only when there is none: an operator who re-enables a
/// provider in Jellyfin's admin UI keeps that choice, and this does not argue
/// with them on every sync.
/// </summary>
public static class LeLibraryLibraryOptions
{
    /// <summary>The types the plugin owns.</summary>
    private static readonly string[] Types = { "Movie", "Series", "Season", "Episode" };

    /// <summary>Writes the allow-list where Jellyfin has none yet.</summary>
    /// <param name="options">The library's options.</param>
    /// <returns>True when an entry was added.</returns>
    public static bool Apply(LibraryOptions options)
    {
        var entries = options.TypeOptions?.ToList() ?? new List<TypeOptions>();
        var changed = false;

        foreach (var type in Types)
        {
            // Checked against the list itself: LibraryOptions.GetTypeOptions
            // dereferences the array, which is null on a hand-built options
            // object.
            if (entries.Any(entry => string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase)))
            {
                // The operator has an entry for this type. Leave all of it as
                // it is, including the fetchers.
                continue;
            }

            entries.Add(new TypeOptions
            {
                Type = type,
                // The allow-list. Jellyfin's own providers are not in it.
                MetadataFetchers = new[] { "LeLibrary" },
                // An empty list stops every remote image provider: the art
                // comes from the document, and Jellyfin's own providers would
                // only add a second poster.
                ImageFetchers = Array.Empty<string>(),
            });
            changed = true;
        }

        if (changed)
        {
            options.TypeOptions = entries.ToArray();
        }

        return changed;
    }
}
