using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Plugin.Standalone.TorBox.Model;
using Jellyfin.Plugin.Standalone.TorBox.Providers;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Standalone.TorBox.Virtual;

/// <summary>Builds a candidate plan using Jellyfin's release-name parsers.</summary>
public sealed class LocalPlanBuilder
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".mov", ".webm", ".mpeg", ".mpg", ".wmv", ".flv",
    };
    private readonly EpisodeResolver _episodes = new(new NamingOptions());
    private readonly Func<string, ItemLookupInfo> _parseName;

    /// <summary>Initializes a new instance of the <see cref="LocalPlanBuilder"/> class.</summary>
    /// <param name="parseName">Jellyfin's movie and series name parser.</param>
    public LocalPlanBuilder(Func<string, ItemLookupInfo> parseName) => _parseName = parseName;

    /// <summary>Builds a plan from a complete provider listing.</summary>
    /// <param name="provider">The debrid provider.</param>
    /// <param name="files">The account's files.</param>
    /// <returns>A plan and the count of files that could not be classified.</returns>
    public (JellyfinPlan Plan, int Skipped) Build(string provider, IReadOnlyList<DebridFile> files)
    {
        var result = new List<PlanItem>();
        var skipped = 0;
        foreach (var file in files)
        {
            if (!PlanKey.TryParse(file.Key, out var parts) || parts.Provider != provider || string.IsNullOrWhiteSpace(file.Name)
                || !VideoExtensions.Contains(Path.GetExtension(file.Name)))
            {
                skipped++;
                continue;
            }
            var release = string.IsNullOrWhiteSpace(file.ReleaseName) ? Path.GetFileNameWithoutExtension(file.Name) : file.ReleaseName;
            var episode = _episodes.Resolve("/" + release!.Replace('/', '_') + "/" + Path.GetFileName(file.Name), false, null, false, null, true);
            if (episode is not null && episode.SeasonNumber is >= 0 && episode.EpisodeNumber is > 0 && !string.IsNullOrWhiteSpace(episode.SeriesName))
            {
                var name = _parseName(episode.SeriesName.Replace('.', ' ').Replace('_', ' '));
                result.Add(new PlanItem
                {
                    Key = file.Key, Library = "shows", Kind = "episode", SeriesTitle = string.IsNullOrWhiteSpace(name.Name) ? episode.SeriesName : name.Name,
                    SeriesYear = name.Year, Season = episode.SeasonNumber, Episode = episode.EpisodeNumber, EpisodeEnd = episode.EndingEpisodeNumber, Path = file.Name,
                });
                continue;
            }
            var movie = _parseName(Path.GetFileNameWithoutExtension(file.Name).Replace('.', ' ').Replace('_', ' '));
            if (string.IsNullOrWhiteSpace(movie.Name)) { skipped++; continue; }
            result.Add(new PlanItem { Key = file.Key, Library = "movies", Kind = "movie", Title = movie.Name, Year = movie.Year, Path = file.Name });
        }
        return (new JellyfinPlan { SchemaVersion = JellyfinPlan.CurrentSchemaVersion, Provider = provider, Items = result }, skipped);
    }
}
