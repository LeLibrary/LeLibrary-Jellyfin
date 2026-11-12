using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Standalone.AllDebrid.Providers;

/// <summary>
/// Parses the plan key: provider:itemId:fileId. The provider and Standalone both
/// build the same string, and it is the only join between them.
/// </summary>
public static class PlanKey
{
    private static readonly Regex Pattern = new(@"^(?<provider>[a-z]+):(?<item>[A-Za-z0-9]+):(?<file>[A-F0-9]+)$", RegexOptions.Compiled);
    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal)
    {
        "torbox", "realdebrid", "alldebrid", "premiumize",
    };

    /// <summary>Parses a key.</summary>
    /// <param name="value">The key.</param>
    /// <param name="key">The parts.</param>
    /// <returns>True when the key is well formed and its provider known.</returns>
    public static bool TryParse(string? value, out (string Provider, string ItemId, string FileId) key)
    {
        key = default;
        var match = Pattern.Match(value ?? string.Empty);
        if (!match.Success) return false;
        var provider = match.Groups["provider"].Value;
        if (!Providers.Contains(provider)) return false;
        key = (provider, match.Groups["item"].Value, match.Groups["file"].Value);
        return true;
    }
}
