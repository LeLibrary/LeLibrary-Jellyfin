using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.LeLibrary.Model;

/// <summary>
/// One episode of the metadata document.
/// </summary>
public sealed record MetadataEpisode
{
    /// <summary>Gets the episode's title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the episode's synopsis.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the runtime in minutes.</summary>
    public int? Runtime { get; init; }

    /// <summary>Gets the air date.</summary>
    public string? AirDate { get; init; }

    /// <summary>Gets the still image URL.</summary>
    public string? Still { get; init; }
}

/// <summary>
/// One title of the metadata document. Every field may be absent: the server
/// leaves out what the library does not have.
/// </summary>
public sealed record MetadataTitle
{
    /// <summary>Gets "movie" or "tv".</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the synopsis.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the runtime in minutes.</summary>
    public int? Runtime { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    /// <summary>Gets the rating (0-10).</summary>
    public double? Rating { get; init; }

    /// <summary>Gets how many votes the rating has.</summary>
    public int? VoteCount { get; init; }

    /// <summary>Gets the certification, e.g. "R" or "TV-MA".</summary>
    public string? Certification { get; init; }

    /// <summary>Gets the release (or first air) date.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Gets the poster URL.</summary>
    public string? Poster { get; init; }

    /// <summary>Gets the backdrop URL.</summary>
    public string? Backdrop { get; init; }

    /// <summary>Gets the logo URL.</summary>
    public string? Logo { get; init; }

    /// <summary>Gets the episodes, keyed "season:episode".</summary>
    public IReadOnlyDictionary<string, MetadataEpisode> Episodes { get; init; } = new Dictionary<string, MetadataEpisode>(StringComparer.Ordinal);
}

/// <summary>
/// The metadata document the LeLibrary server serves, keyed by
/// <c>kind:id</c> so a film and a show that share a TMDB id cannot collide.
/// </summary>
public sealed class MetadataDocument
{
    /// <summary>Gets a document with nothing in it.</summary>
    public static MetadataDocument Empty { get; } = new();

    /// <summary>Gets the titles by their key.</summary>
    public IReadOnlyDictionary<string, MetadataTitle> Titles { get; init; } = new Dictionary<string, MetadataTitle>(StringComparer.Ordinal);

    /// <summary>
    /// Finds a title by the ids the plan carries, in the same order the server
    /// keys by: the TMDB id, then the IMDb id, then the TVDB id.
    /// </summary>
    /// <param name="kind">"movie" or "tv".</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="imdbId">The IMDb id.</param>
    /// <param name="tvdbId">The TVDB id.</param>
    /// <returns>The title, or null when the document has none.</returns>
    public MetadataTitle? Find(string kind, int? tmdbId, string? imdbId, string? tvdbId)
    {
        foreach (var id in new[]
        {
            tmdbId?.ToString(CultureInfo.InvariantCulture),
            imdbId,
            tvdbId,
        })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (Titles.TryGetValue(kind + ":" + id, out var title))
            {
                return title;
            }
        }

        return null;
    }
}
