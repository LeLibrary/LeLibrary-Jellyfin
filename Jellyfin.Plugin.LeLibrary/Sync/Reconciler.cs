using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LeLibrary.Model;

namespace Jellyfin.Plugin.LeLibrary.Sync;

/// <summary>One file the plan wants on disk.</summary>
/// <param name="Key">The provider-scoped plan key.</param>
/// <param name="RelativePath">Where the file belongs, relative to the library root.</param>
/// <param name="Item">The plan item it came from.</param>
public sealed record Desired(string Key, string RelativePath, PlanItem Item);

/// <summary>One file already on disk, with the key stored inside it.</summary>
/// <param name="Key">The provider-scoped plan key.</param>
/// <param name="RelativePath">Where the file currently is.</param>
public sealed record ExistingFile(string Key, string RelativePath);

/// <summary>A file that stays but moves.</summary>
/// <param name="From">Where it is now.</param>
/// <param name="To">Where the plan wants it.</param>
public sealed record Move(ExistingFile From, Desired To);

/// <summary>What the sync has to do to make the folder match the plan.</summary>
/// <param name="Writes">New files.</param>
/// <param name="Deletes">Files the plan no longer wants.</param>
/// <param name="Moves">Files that keep their key and change path.</param>
public sealed record ReconcilePlan(
    IReadOnlyList<Desired> Writes,
    IReadOnlyList<ExistingFile> Deletes,
    IReadOnlyList<Move> Moves);

/// <summary>
/// The diff between the plan and what is on disk. Pure: the caller does the IO.
/// </summary>
public static class Reconciler
{
    /// <summary>
    /// Works out the smallest set of changes.
    /// </summary>
    /// <param name="desired">What the plan wants.</param>
    /// <param name="existing">What is on disk.</param>
    /// <returns>The changes.</returns>
    public static ReconcilePlan Reconcile(IEnumerable<Desired> desired, IEnumerable<ExistingFile> existing)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);

        var wanted = new List<Desired>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in desired)
        {
            if (!seenPaths.Add(item.RelativePath))
            {
                throw new InvalidOperationException($"duplicate path in plan: {item.RelativePath}");
            }

            wanted.Add(item);
        }

        var byKey = new Dictionary<string, ExistingFile>(StringComparer.Ordinal);
        foreach (var file in existing)
        {
            byKey[file.Key] = file;
        }

        var writes = new List<Desired>();
        var moves = new List<Move>();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in wanted)
        {
            keep.Add(item.Key);
            if (!byKey.TryGetValue(item.Key, out var current))
            {
                writes.Add(item);
                continue;
            }

            // Case-only differences are the same path on every filesystem that
            // matters here; moving for them would churn the library every sync.
            if (!string.Equals(Normalize(current.RelativePath), Normalize(item.RelativePath), StringComparison.OrdinalIgnoreCase))
            {
                moves.Add(new Move(current, item));
            }
        }

        var deletes = wanted
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var removed = byKey.Values
            .Where(file => !deletes.Contains(file.Key))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReconcilePlan(writes, removed, moves);
    }

    private static string Normalize(string path) => Path.DirectorySeparatorChar == '\\'
        ? path.Replace('/', '\\')
        : path.Replace('\\', '/');
}
