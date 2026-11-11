using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LeLibrary.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.LeLibrary.Providers;

/// <summary>
/// Finds the document's entry for a Jellyfin item, using the ids the plan put
/// on it. One place, so the metadata provider and the art agree about which
/// title an item is.
/// </summary>
public static class MetadataLookup
{
    /// <summary>The internal provider id for a TMDB id.</summary>
    public const string Tmdb = "Tmdb";

    /// <summary>The internal provider id for an IMDb id.</summary>
    public const string Imdb = "Imdb";

    /// <summary>The internal provider id for a TVDB id.</summary>
    public const string Tvdb = "Tvdb";

    /// <summary>The document's entry for an item, or null when it has none.</summary>
    /// <param name="item">The item.</param>
    /// <param name="document">The document.</param>
    /// <returns>The title, or null.</returns>
    public static MetadataTitle? For(BaseItem item, MetadataDocument? document)
    {
        if (document is null || item is null)
        {
            return null;
        }

        // A film is looked up as a film; a show, a season and an episode all
        // belong to a show.
        var kind = item is Movie ? "movie" : "tv";
        return document.Find(kind, Number(item, Tmdb), Text(item, Imdb), Text(item, Tvdb));
    }

    /// <summary>The episode's entry under its show, or null when it has none.</summary>
    /// <param name="episode">The episode.</param>
    /// <param name="title">Its show's entry.</param>
    /// <returns>The episode's metadata, or null.</returns>
    public static MetadataEpisode? EpisodeFor(Episode episode, MetadataTitle? title)
    {
        if (title is null || episode.ParentIndexNumber is not { } season || episode.IndexNumber is not { } number)
        {
            return null;
        }

        var key = season.ToString(CultureInfo.InvariantCulture) + ":" + number.ToString(CultureInfo.InvariantCulture);
        return title.Episodes.TryGetValue(key, out var metadata) ? metadata : null;
    }

    private static int? Number(BaseItem item, string provider)
        => item.ProviderIds is not null
            && item.ProviderIds.TryGetValue(provider, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;

    private static string? Text(BaseItem item, string provider)
        => item.ProviderIds is not null && item.ProviderIds.TryGetValue(provider, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
}
