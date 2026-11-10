using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LeLibrary.Model;

namespace Jellyfin.Plugin.LeLibrary.Sync;

/// <summary>
/// The file names and folders written under the library root. The same rules as
/// LeLibrary's own builder, so both sides agree on every path: a plan path is
/// used when it is safe, and rebuilt from the item's fields when it is not.
/// </summary>
public static class PathBuilder
{
    private const int MaxComponentLength = 120;

    private static readonly Regex UnsafeCharacters = new(@"[\\/:*?""<>|]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex EdgeDotsAndSpaces = new(@"^[.\s]+|[.\s]+$", RegexOptions.Compiled);
    private static readonly Regex KeyPattern = new(@"^(?<provider>[a-z]+):(?<item>[A-Za-z0-9]+):(?<file>\d+)$", RegexOptions.Compiled);
    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal)
    {
        "torbox", "realdebrid", "alldebrid", "premiumize",
    };

    /// <summary>
    /// Gets the provider/item/file parts of a plan key.
    /// </summary>
    /// <param name="value">The key string.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns>True when the key is well formed.</returns>
    public static bool TryParseKey(string? value, out (string Provider, string ItemId, string FileId) key)
    {
        key = default;
        var match = KeyPattern.Match(value ?? string.Empty);
        if (!match.Success) return false;
        var provider = match.Groups["provider"].Value;
        if (!Providers.Contains(provider)) return false;
        key = (provider, match.Groups["item"].Value, match.Groups["file"].Value);
        return true;
    }

    /// <summary>
    /// Makes one path component safe for Windows, SMB and every NAS a Jellyfin
    /// server might run on.
    /// </summary>
    /// <param name="value">The raw name.</param>
    /// <returns>A safe component.</returns>
    public static string Sanitize(string? value)
    {
        var text = UnsafeCharacters.Replace(value ?? string.Empty, "-");
        text = Whitespace.Replace(text, " ");
        text = EdgeDotsAndSpaces.Replace(text, string.Empty);
        if (text.Length > MaxComponentLength) text = text[..MaxComponentLength];
        return EdgeDotsAndSpaces.Replace(text, string.Empty);
    }

    /// <summary>
    /// Gets the relative path for a movie item.
    /// </summary>
    /// <param name="item">The plan item.</param>
    /// <returns>A relative .strm path.</returns>
    public static string MovieRelative(PlanItem item)
    {
        return IsSafeRelative(item.Path) ? Normalize(item.Path) : BuildMovie(item);
    }

    /// <summary>
    /// Gets the relative path for an episode item.
    /// </summary>
    /// <param name="item">The plan item.</param>
    /// <returns>A relative .strm path.</returns>
    public static string EpisodeRelative(PlanItem item)
    {
        return IsSafeRelative(item.Path) ? Normalize(item.Path) : BuildEpisode(item);
    }

    /// <summary>
    /// True when a plan-supplied path cannot escape the library folder or name a
    /// file Jellyfin will not read.
    /// </summary>
    /// <param name="path">The plan path.</param>
    /// <returns>True when it is safe to use as-is.</returns>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        if (path.Contains('\\')) return false;
        if (path.Contains(':')) return false; // drive letters and streams
        if (!path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0) return false;
            if (part is "." or "..") return false;
            if (part.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0) return false;
            if (part.EndsWith(' ') || part.EndsWith('.')) return false;
        }

        return true;
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string BuildMovie(PlanItem item)
    {
        var folder = Folder(item.Title, item.Year);
        var suffix = string.IsNullOrWhiteSpace(item.Quality)
            ? string.Empty
            : $" - {item.Quality}" + (string.IsNullOrWhiteSpace(item.Hdr) ? string.Empty : $" {item.Hdr.ToUpperInvariant()}");
        return $"{folder}/{folder}{suffix}.strm";
    }

    private static string BuildEpisode(PlanItem item)
    {
        var season = Math.Max(0, item.Season ?? 0);
        var episode = Math.Max(0, item.Episode ?? 0);
        var end = Math.Max(0, item.EpisodeEnd ?? 0);
        var folder = Folder(item.SeriesTitle, item.SeriesYear);
        var baseName = Sanitize(item.SeriesTitle) is { Length: > 0 } name ? name : "Untitled";
        var episodes = end > episode
            ? $"E{episode.ToString("D2", CultureInfo.InvariantCulture)}-E{end.ToString("D2", CultureInfo.InvariantCulture)}"
            : $"E{episode.ToString("D2", CultureInfo.InvariantCulture)}";
        var seasonText = season.ToString("D2", CultureInfo.InvariantCulture);
        return $"{folder}/Season {seasonText}/{baseName} S{seasonText}{episodes}.strm";
    }

    private static string Folder(string? title, int? year)
    {
        var name = Sanitize(title);
        if (name.Length == 0) name = "Untitled";
        return year is > 1900 and < 2200 ? $"{name} ({year})" : name;
    }
}
