using System.Collections.Generic;

namespace Jellyfin.Plugin.Standalone.Premiumize.Model;

/// <summary>One Jellyfin library the plan describes.</summary>
/// <param name="Key">The library key (movies, shows).</param>
/// <param name="Name">The name to show in Jellyfin.</param>
/// <param name="Kind">The library kind.</param>
public sealed record PlanLibrary(string Key, string Name, string Kind);

/// <summary>A whole plan document.</summary>
public sealed class JellyfinPlan
{
    /// <summary>The schema this plugin understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the schema version Standalone sent.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets the debrid provider id.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets the libraries.</summary>
    public IReadOnlyList<PlanLibrary> Libraries { get; init; } = new List<PlanLibrary>();

    /// <summary>Gets the items.</summary>
    public IReadOnlyList<PlanItem> Items { get; init; } = new List<PlanItem>();
}

/// <summary>
/// One item of the Standalone plan. Field names mirror the JSON contract
/// (Docs/agents/jellyfin.md in the Standalone repo); unknown fields are ignored.
/// </summary>
public sealed record PlanItem
{
    /// <summary>Gets the provider-scoped key: provider:itemId:fileId.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets the library key the item belongs to (movies, shows).</summary>
    public string Library { get; init; } = string.Empty;

    /// <summary>Gets the item kind (movie, episode).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the movie title, or the series title for an episode.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the release year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the series title (episodes).</summary>
    public string? SeriesTitle { get; init; }

    /// <summary>Gets the series year (episodes).</summary>
    public int? SeriesYear { get; init; }

    /// <summary>Gets the season number.</summary>
    public int? Season { get; init; }

    /// <summary>Gets the episode number.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the last episode of a multi-episode file.</summary>
    public int? EpisodeEnd { get; init; }

    /// <summary>Gets the episode title, when Standalone has one.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets the release quality (2160p, 1080p, ...).</summary>
    public string? Quality { get; init; }

    /// <summary>Gets the HDR marker (dv, hdr10+, hdr).</summary>
    public string? Hdr { get; init; }

    /// <summary>Gets the relative path Standalone chose for this item.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the TMDB id.</summary>
    public int? TmdbId { get; init; }

    /// <summary>Gets the TVDB id (episodes).</summary>
    public int? TvdbId { get; init; }

    /// <summary>Gets the IMDB id.</summary>
    public string? ImdbId { get; init; }

    /// <summary>Gets the poster URL.</summary>
    public string? Poster { get; init; }
}
