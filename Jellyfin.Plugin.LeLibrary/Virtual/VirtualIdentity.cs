using System;
using System.Linq;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LeLibrary.Virtual;

/// <summary>
/// Identity of a virtual item. Jellyfin derives the item id from the key, and
/// the key is also stored as a provider id so a resync can find its own items
/// without scanning the whole database.
/// </summary>
public static class VirtualIdentity
{
    /// <summary>Provider id that marks an item as ours and holds its plan key.</summary>
    public const string ProviderId = "LeLibraryKey";

    /// <summary>Provider id holding the series key for episodes.</summary>
    public const string SeriesId = "LeLibrarySeries";

    /// <summary>Provider id holding the season key for episodes.</summary>
    public const string SeasonId = "LeLibrarySeason";

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
    /// <param name="planPath">The path LeLibrary sent, when present.</param>
    /// <param name="title">Fallback title.</param>
    /// <param name="year">Fallback year.</param>
    /// <returns>A file name.</returns>
    public static string PlayFileName(string? planPath, string? title, int? year)
    {
        var name = string.IsNullOrWhiteSpace(planPath) ? null : System.IO.Path.GetFileNameWithoutExtension(planPath);
        // A name with no letters or digits (".", "..") is not a file name.
        if (string.IsNullOrWhiteSpace(name) || !name!.Any(char.IsLetterOrDigit))
        {
            name = Folder(title, year);
        }

        foreach (var c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) name = name!.Replace(c, '-');
        return name + ".mkv";
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
public sealed record VirtualItem(string RelativePath, string? ParentRelativePath, string Key, string FileName, MediaBrowser.Controller.Entities.BaseItem Item);

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
