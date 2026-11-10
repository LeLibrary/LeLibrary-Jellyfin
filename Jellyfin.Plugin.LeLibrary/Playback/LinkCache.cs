using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Providers;

namespace Jellyfin.Plugin.LeLibrary.Playback;

/// <summary>
/// Minted playback links, kept for fifteen minutes. Debrid links expire, so the
/// cache is short on purpose: a player holding an old URL gets a fresh mint.
/// </summary>
public sealed class LinkCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly Func<IDebridClient> _provider;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<MintResult>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="LinkCache"/> class.</summary>
    /// <param name="provider">Builds the debrid client for the current settings.</param>
    public LinkCache(Func<IDebridClient> provider)
    {
        _provider = provider;
    }

    /// <summary>Gets a link, minting it if needed. Failures are never cached.</summary>
    /// <param name="key">The plan key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mint result.</returns>
    public async Task<MintResult> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        {
            return new MintResult(MintStatus.Ok, entry.Url);
        }

        // One mint per key at a time: a player opening a file fires several
        // range requests at once and would otherwise burn four link mints.
        var pending = _inFlight.GetOrAdd(key, _ => Mint(key, cancellationToken));
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>Drops a cached link (used after an upstream 403/404).</summary>
    /// <param name="key">The plan key.</param>
    public void Forget(string key) => _entries.TryRemove(key, out _);

    private async Task<MintResult> Mint(string key, CancellationToken cancellationToken)
    {
        var result = await _provider().MintAsync(key, cancellationToken).ConfigureAwait(false);
        if (result.Status == MintStatus.Ok && !string.IsNullOrEmpty(result.Url))
        {
            _entries[key] = new Entry(result.Url, DateTimeOffset.UtcNow.Add(Lifetime));
        }

        return result;
    }

    private sealed record Entry(string Url, DateTimeOffset Expires);
}
