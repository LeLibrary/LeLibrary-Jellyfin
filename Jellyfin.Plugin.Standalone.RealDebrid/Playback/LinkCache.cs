using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Standalone.RealDebrid.Providers;

namespace Jellyfin.Plugin.Standalone.RealDebrid.Playback;

/// <summary>
/// Minted playback links, kept for fifteen minutes. Debrid links expire, so the
/// cache is short on purpose: a player holding an old URL gets a fresh mint.
/// </summary>
public sealed class LinkCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly Func<IDebridClient> _provider;
    private readonly Func<string>? _credentialIdentity;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<MintResult>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="LinkCache"/> class.</summary>
    /// <param name="provider">Builds the debrid client for the current settings.</param>
    public LinkCache(Func<IDebridClient> provider, Func<string>? credentialIdentity = null)
    {
        _provider = provider;
        _credentialIdentity = credentialIdentity;
    }

    /// <summary>Gets a link, minting it if needed. Failures are never cached.</summary>
    /// <param name="key">The plan key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mint result.</returns>
    public async Task<MintResult> GetAsync(string key, CancellationToken cancellationToken)
    {
        var scopedKey = Scoped(key);
        if (_entries.TryGetValue(scopedKey, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        {
            return new MintResult(MintStatus.Ok, entry.Url);
        }

        // Lazy ensures competing GetOrAdd factories cannot start duplicate mints.
        // One caller cancelling its wait must not cancel the shared request.
        var pending = _inFlight.GetOrAdd(scopedKey, _ => new Lazy<Task<MintResult>>(
            () => Mint(key, scopedKey, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));
        var task = pending.Value;
        _ = task.ContinueWith(
            _ => ((ICollection<KeyValuePair<string, Lazy<Task<MintResult>>>>)_inFlight)
                .Remove(new KeyValuePair<string, Lazy<Task<MintResult>>>(scopedKey, pending)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops a cached link (used after an upstream 403/404).</summary>
    /// <param name="key">The plan key.</param>
    public void Forget(string key) => _entries.TryRemove(Scoped(key), out _);

    private string Scoped(string key)
    {
        var credential = _credentialIdentity?.Invoke() ?? string.Empty;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(credential));
        return Convert.ToHexString(hash) + ":" + key;
    }

    private async Task<MintResult> Mint(string key, string scopedKey, CancellationToken cancellationToken)
    {
        var result = await _provider().MintAsync(key, cancellationToken).ConfigureAwait(false);
        if (result.Status == MintStatus.Ok && !string.IsNullOrEmpty(result.Url))
        {
            _entries[scopedKey] = new Entry(result.Url, DateTimeOffset.UtcNow.Add(Lifetime));
        }

        return result;
    }

    private sealed record Entry(string Url, DateTimeOffset Expires);
}
