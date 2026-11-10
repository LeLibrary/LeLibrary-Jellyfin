using System.Text;
using Jellyfin.Plugin.LeLibrary.Model;

namespace Jellyfin.Plugin.LeLibrary.Sync;

/// <summary>
/// The .nfo files Jellyfin reads next to each .strm. Images are remote URLs, so
/// Jellyfin downloads them once when it scans and caches them itself.
/// </summary>
public static class NfoBuilder
{
    /// <summary>Builds a movie nfo.</summary>
    /// <param name="item">The plan item.</param>
    /// <param name="playUrl">The play URL (kept for parity with episodes).</param>
    /// <returns>The nfo document.</returns>
    public static string Movie(PlanItem item, string playUrl)
    {
        _ = playUrl;
        var xml = new StringBuilder();
        xml.AppendLine("<movie>");
        Append(xml, "title", item.Title);
        Append(xml, "year", item.Year);
        Append(xml, "tmdbid", item.TmdbId);
        Append(xml, "imdbid", item.ImdbId);
        AppendThumb(xml, item.Poster);
        xml.AppendLine("</movie>");
        return xml.ToString();
    }

    /// <summary>Builds an episode nfo.</summary>
    /// <param name="item">The plan item.</param>
    /// <returns>The nfo document.</returns>
    public static string Episode(PlanItem item)
    {
        var xml = new StringBuilder();
        xml.AppendLine("<episodedetails>");
        Append(xml, "title", item.EpisodeTitle ?? (item.Episode is null ? null : $"Episode {item.Episode}"));
        Append(xml, "showtitle", item.SeriesTitle);
        Append(xml, "season", item.Season);
        Append(xml, "episode", item.Episode);
        Append(xml, "tmdbid", item.TmdbId);
        Append(xml, "tvdbid", item.TvdbId);
        AppendThumb(xml, item.Poster);
        xml.AppendLine("</episodedetails>");
        return xml.ToString();
    }

    /// <summary>Builds the tvshow nfo for a series folder.</summary>
    /// <param name="item">Any item of the series.</param>
    /// <returns>The nfo document.</returns>
    public static string Series(PlanItem item)
    {
        var xml = new StringBuilder();
        xml.AppendLine("<tvshow>");
        Append(xml, "title", item.SeriesTitle);
        Append(xml, "year", item.SeriesYear);
        Append(xml, "tmdbid", item.TmdbId);
        Append(xml, "tvdbid", item.TvdbId);
        AppendThumb(xml, item.Poster);
        xml.AppendLine("</tvshow>");
        return xml.ToString();
    }

    private static void AppendThumb(StringBuilder xml, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        xml.Append("<thumb aspect=\"poster\">").Append(Escape(url)).AppendLine("</thumb>");
    }

    private static void Append(StringBuilder xml, string tag, object? value)
    {
        if (value is null) return;
        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;
        xml.Append('<').Append(tag).Append('>').Append(Escape(text)).Append("</").Append(tag).AppendLine(">");
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;", System.StringComparison.Ordinal)
            .Replace("<", "&lt;", System.StringComparison.Ordinal)
            .Replace(">", "&gt;", System.StringComparison.Ordinal)
            .Replace("\"", "&quot;", System.StringComparison.Ordinal);
}
