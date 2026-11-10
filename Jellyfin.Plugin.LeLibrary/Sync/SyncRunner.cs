using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Client;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Providers;

namespace Jellyfin.Plugin.LeLibrary.Sync;

/// <summary>Anything that can hand over a plan.</summary>
public interface IPlanSource
{
    /// <summary>Fetches the plan.</summary>
    /// <param name="setupUrl">The setup install URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The plan and its source.</returns>
    Task<PlanFetch> FetchAsync(string? setupUrl, CancellationToken cancellationToken);
}

/// <summary>What one sync was asked to do.</summary>
/// <param name="Root">The library folder.</param>
/// <param name="PlanUrl">The setup install URL.</param>
/// <param name="PlayBaseUrl">The Jellyfin server URL the .strm files point at.</param>
public sealed record SyncRequest(string Root, string PlanUrl, string PlayBaseUrl);

/// <summary>What one sync did.</summary>
/// <param name="Ok">False when nothing could be trusted and nothing was changed.</param>
/// <param name="Error">Why, when !Ok.</param>
/// <param name="Added">Files written.</param>
/// <param name="Removed">Files deleted.</param>
/// <param name="Moved">Files renamed.</param>
/// <param name="Unchanged">Files already right.</param>
/// <param name="Skipped">Plan items with no matching file in the account.</param>
public sealed record SyncResult(bool Ok, string? Error, int Added, int Removed, int Moved, int Unchanged, int Skipped);

/// <summary>
/// One sync: fetch the plan, list the account, reconcile, write.
///
/// The rule that matters most is here: nothing is deleted unless the plan was
/// fetched successfully *and* the provider answered with a complete listing.
/// Every other failure keeps the library exactly as it is.
/// </summary>
public sealed class SyncRunner
{
    private static readonly Regex PlayKey = new(@"/LeLibrary/play/(?<key>[^/\s""'?#]+)", RegexOptions.Compiled);

    private readonly IPlanSource _plans;
    private readonly IDebridClient _provider;
    private readonly Action<string>? _log;

    /// <summary>Initializes a new instance of the <see cref="SyncRunner"/> class.</summary>
    /// <param name="plans">The plan source.</param>
    /// <param name="provider">The debrid client.</param>
    /// <param name="log">Optional log sink.</param>
    public SyncRunner(IPlanSource plans, IDebridClient provider, Action<string>? log = null)
    {
        _plans = plans;
        _provider = provider;
        _log = log;
    }

    /// <summary>Runs one sync.</summary>
    /// <param name="request">What to sync.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    public async Task<SyncResult> RunAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Root))
        {
            return Failed("the library folder is not set");
        }

        var fetch = await _plans.FetchAsync(request.PlanUrl, cancellationToken).ConfigureAwait(false);
        if (!fetch.Ok)
        {
            return Failed(fetch.Error ?? "the plan could not be read");
        }

        if (!string.IsNullOrEmpty(fetch.Plan.Provider) && !string.Equals(fetch.Plan.Provider, _provider.Provider, StringComparison.Ordinal))
        {
            return Failed($"the plan is for {fetch.Plan.Provider}, but this plugin is set to {_provider.Provider}");
        }

        var listing = await _provider.ListLibraryAsync(cancellationToken).ConfigureAwait(false);
        if (!listing.IsHealthy)
        {
            return Failed(listing.Error ?? "the debrid account could not be listed");
        }

        var available = listing.Files.Select(file => file.Key).ToHashSet(StringComparer.Ordinal);
        var desired = new List<Desired>();
        var skipped = 0;
        var takenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in fetch.Plan.Items)
        {
            if (!available.Contains(item.Key))
            {
                skipped += 1;
                continue;
            }

            var relative = string.Equals(item.Kind, "episode", StringComparison.OrdinalIgnoreCase)
                ? PathBuilder.EpisodeRelative(item)
                : PathBuilder.MovieRelative(item);
            // Two keys can only land on one path when LeLibrary already failed
            // to collapse copies; the first wins and the rest are reported.
            if (!takenPaths.Add(relative))
            {
                skipped += 1;
                continue;
            }

            desired.Add(new Desired(item.Key, relative, item));
        }

        var existing = Scan(request.Root);
        var plan = Reconciler.Reconcile(desired, existing);

        foreach (var move in plan.Moves)
        {
            MoveFile(request.Root, move.From.RelativePath, move.To.RelativePath);
            MoveFile(request.Root, NfoPath(move.From.RelativePath), NfoPath(move.To.RelativePath));
        }

        foreach (var delete in plan.Deletes)
        {
            DeleteFile(request.Root, delete.RelativePath);
            DeleteFile(request.Root, NfoPath(delete.RelativePath));
        }

        foreach (var write in plan.Writes)
        {
            WriteLibraryFiles(request, write);
        }

        var result = new SyncResult(
            true,
            null,
            plan.Writes.Count,
            plan.Deletes.Count,
            plan.Moves.Count,
            desired.Count - plan.Writes.Count - plan.Moves.Count,
            skipped);
        _log?.Invoke($"added {result.Added}, removed {result.Removed}, renamed {result.Moved}, unchanged {result.Unchanged}, skipped {result.Skipped}");
        return result;
    }

    /// <summary>
    /// Every .strm under the root that this plugin wrote: the key is read back
    /// out of the play URL. Anything else is left alone forever.
    /// </summary>
    /// <param name="root">The library folder.</param>
    /// <returns>What is on disk.</returns>
    public static List<ExistingFile> Scan(string root)
    {
        var found = new List<ExistingFile>();
        if (!Directory.Exists(root)) return found;
        foreach (var path in Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories))
        {
            string content;
            try { content = File.ReadAllText(path); }
            catch (IOException) { continue; }

            var match = PlayKey.Match(content);
            if (!match.Success) continue;
            found.Add(new ExistingFile(
                Uri.UnescapeDataString(match.Groups["key"].Value),
                Relative(root, path)));
        }

        return found;
    }

    private void WriteLibraryFiles(SyncRequest request, Desired write)
    {
        var playUrl = $"{request.PlayBaseUrl.TrimEnd('/')}/LeLibrary/play/{write.Key}";
        WriteAtomic(request.Root, write.RelativePath, playUrl);

        var item = write.Item;
        var nfo = NfoPath(write.RelativePath);
        if (string.Equals(item.Kind, "episode", StringComparison.OrdinalIgnoreCase))
        {
            WriteAtomic(request.Root, nfo, NfoBuilder.Episode(item));
            var seriesDir = SeriesFolder(write.RelativePath);
            if (seriesDir is not null)
            {
                WriteAtomic(request.Root, $"{seriesDir}/tvshow.nfo", NfoBuilder.Series(item));
            }
        }
        else
        {
            WriteAtomic(request.Root, nfo, NfoBuilder.Movie(item, playUrl));
        }
    }

    private static void WriteAtomic(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content + Environment.NewLine);
        File.Move(temp, path, true);
    }

    private static void MoveFile(string root, string from, string to)
    {
        var source = Path.Combine(root, from.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(root, to.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, true);
    }

    private static void DeleteFile(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* next sync tries again */ }
    }

    /// <summary>"Show (Year)/Season 01/Show S01E03.strm" -> "Show (Year)".</summary>
    private static string? SeriesFolder(string relativePath)
    {
        var parts = relativePath.Split('/');
        return parts.Length >= 3 ? string.Join('/', parts.Take(parts.Length - 2)) : null;
    }

    private static string NfoPath(string strmPath)
        => strmPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            ? strmPath[..^".strm".Length] + ".nfo"
            : strmPath + ".nfo";

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static SyncResult Failed(string error) => new(false, error, 0, 0, 0, 0, 0);
}
