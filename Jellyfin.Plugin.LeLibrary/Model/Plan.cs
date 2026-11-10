namespace Jellyfin.Plugin.LeLibrary.Model;

/// <summary>
/// One item of the LeLibrary plan. Field names mirror the JSON contract
/// (Docs/agents/jellyfin.md in the LeLibrary repo); unknown fields are ignored.
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

    /// <summary>Gets the episode title, when LeLibrary has one.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets the release quality (2160p, 1080p, ...).</summary>
    public string? Quality { get; init; }

    /// <summary>Gets the HDR marker (dv, hdr10+, hdr).</summary>
    public string? Hdr { get; init; }

    /// <summary>Gets the relative path LeLibrary chose for this item.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the poster URL.</summary>
    public string? Poster { get; init; }
}
