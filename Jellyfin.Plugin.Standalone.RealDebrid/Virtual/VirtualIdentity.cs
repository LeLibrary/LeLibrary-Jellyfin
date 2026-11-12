using System;
using System.Linq;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Standalone.RealDebrid.Virtual;

/// <summary>
/// Identity of a virtual item. Jellyfin derives the item id from the key, and
/// the key is also stored as a provider id so a resync can find its own items
/// without scanning the whole database.
/// </summary>
public static class VirtualIdentity
{
    /// <summary>Provider id that marks an item as ours and holds its plan key.</summary>
    public const string ProviderId = "StandaloneRealDebridKey";

    /// <summary>Provider id holding the series key for episodes.</summary>
    public const string SeriesId = "StandaloneRealDebridSeries";

    /// <summary>Provider id holding the season key for episodes.</summary>
    public const string SeasonId = "StandaloneRealDebridSeason";

    /// <summary>
    /// Provider id holding the show's TMDB id on a series, season and episode.
    /// Jellyfin's own TMDb provider overwrites an episode's <c>Tmdb</c> id with
    /// the episode's own, so the show's id needs a home of its own.
    /// </summary>
    public const string ShowId = "StandaloneRealDebridTmdb";

    /// <summary>
    /// Relative path for a movie or episode. The extension only exists so
    /// Jellyfin treats it as a video file; nothing is written to disk.
    /// </summary>
    /// <param name="kind">movie or episode.</param>
    /// <param name="title">The title or series name.</param>
    /// <param name="year">The year, when known.</param>
    /// <param name="season">The season, for episodes.</param>
    /// <param name="episode">The episode, for episodes.</param>
    /// <param name="key">The plan key (keeps paths unique).</param>
    /// <returns>A relative path.</returns>
    public static string MediaRelativePath(string kind, string title, int? year, int? season, int? episode, string key)
    {
        var folder = Folder(title, year);
        var safeKey = key.Replace(':', '-');
        if (string.Equals(kind, "episode", StringComparison.OrdinalIgnoreCase) && season is not null && episode is not null)
        {
            var s = season.Value.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var e = episode.Value.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            return $"{folder}/Season {s}/{folder} S{s}E{e} [{safeKey}].mkv";
        }

        return $"{folder}/{folder} [{safeKey}].mkv";
    }

    /// <summary>Relative path of a series folder.</summary>
    /// <param name="title">The series name.</param>
    /// <param name="year">The year, when known.</param>
    /// <param name="key">The series key.</param>
    /// <returns>A relative path.</returns>
    public static string SeriesRelativePath(string title, int? year, string key)
        => $"Shows/{Folder(title, year)} [{key.Replace(':', '-')}]";

    /// <summary>Relative path of a season folder.</summary>
    /// <param name="seriesPath">The series path.</param>
    /// <param name="season">The season number.</param>
    /// <returns>A relative path.</returns>
    public static string SeasonRelativePath(string seriesPath, int season)
        => $"{seriesPath}/Season {season.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// File name used in the play URL. Jellyfin reads the container from the
    /// extension, so it must look like the real file.
    /// </summary>
    /// <param name="planPath">The path Standalone sent, when present.</param>
    /// <param name="title">Fallback title.</param>
    /// <param name="year">Fallback year.</param>
    /// <param name="realFileName">The provider's real file name, when it is known.</param>
    /// <returns>A file name.</returns>
    public static string PlayFileName(string? planPath, string? title, int? year, string? realFileName = null)
    {
        var name = string.IsNullOrWhiteSpace(planPath) ? null : System.IO.Path.GetFileNameWithoutExtension(planPath);
        // A name with no letters or digits (".", "..") is not a file name.
        if (string.IsNullOrWhiteSpace(name) || !name!.Any(char.IsLetterOrDigit))
        {
            name = Folder(title, year);
        }

        foreach (var c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) name = name!.Replace(c, '-');
        var extension = ExtensionOf(realFileName);
        return name + (extension is null ? ".mkv" : "." + extension);
    }

    /// <summary>
    /// The file name of one episode of a file that covers several. The name
    /// differs from the first episode's so the two rows do not share a path,
    /// and the extension is kept so the container does not change.
    /// </summary>
    /// <param name="fileName">The file name of the first episode.</param>
    /// <param name="episode">The episode number.</param>
    /// <returns>A file name for this episode.</returns>
    public static string EpisodeFileName(string fileName, int episode)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var marked = name + " E" + episode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var extension = ExtensionOf(fileName);
        return extension is null ? marked : marked + "." + extension;
    }

    /// <summary>
    /// The container Jellyfin derives from a file name. The container has to be
    /// the real one: a request that does not name a container cannot be
    /// answered at all.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <returns>The container.</returns>
    public static string ContainerOf(string? fileName)
    {
        var extension = ExtensionOf(fileName);
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

    /// <summary>The lower-cased extension of a file name, without the dot.</summary>
    /// <param name="fileName">The file name.</param>
    /// <returns>The extension, or null when it has none.</returns>
    private static string? ExtensionOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var extension = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 ? null : extension;
    }

    private static string Folder(string? title, int? year)
    {
        var baseName = string.IsNullOrWhiteSpace(title) ? "Untitled" : title!.Trim();
        foreach (var c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) baseName = baseName.Replace(c, '-');
        return year is > 1900 and < 2200 ? $"{baseName} ({year})" : baseName;
    }
}

/// <summary>An item ready to be written to Jellyfin's database.</summary>
/// <param name="RelativePath">Where it lives under the library folder.</param>
/// <param name="ParentRelativePath">Its parent's relative path, or null for the library root.</param>
/// <param name="Key">Its stable key.</param>
/// <param name="Item">The populated item.</param>
/// <param name="Identity">What the item is, and never where it is served from.</param>
public sealed record VirtualItem(string RelativePath, string? ParentRelativePath, string Key, string FileName, MediaBrowser.Controller.Entities.BaseItem Item, string? Identity = null);

/// <summary>A whole desired library, parents first.</summary>
/// <param name="Movies">Movies.</param>
/// <param name="Series">Series.</param>
/// <param name="Seasons">Seasons.</param>
/// <param name="Episodes">Episodes.</param>
/// <param name="Skipped">Plan entries that could not be represented.</param>
public sealed record VirtualLibraryPlan(
    IReadOnlyList<VirtualItem> Movies,
    IReadOnlyList<VirtualItem> Series,
    IReadOnlyList<VirtualItem> Seasons,
    IReadOnlyList<VirtualItem> Episodes,
    int Skipped);
