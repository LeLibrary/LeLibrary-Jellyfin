using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LeLibrary.Providers;

/// <summary>How a provider answered a health check or a listing.</summary>
public enum ProviderHealth
{
    /// <summary>The key works and the account answered.</summary>
    Healthy,

    /// <summary>The key was rejected.</summary>
    InvalidKey,

    /// <summary>The account is rate limited; try again later.</summary>
    RateLimited,

    /// <summary>The provider could not be reached or returned a server error.</summary>
    Unavailable,
}

/// <summary>One playable file in the user's account.</summary>
/// <param name="Key">The plan key: provider:itemId:fileId.</param>
/// <param name="Name">The file name inside the download.</param>
/// <param name="Size">The size in bytes.</param>
public sealed record DebridFile(string Key, string Name, long Size);

/// <summary>The result of listing a debrid account.</summary>
/// <param name="Health">Whether the listing can be trusted.</param>
/// <param name="Files">Every file, keyed the way the plan keys them.</param>
/// <param name="Error">A readable reason when the listing failed.</param>
public sealed record ProviderListing(ProviderHealth Health, IReadOnlyList<DebridFile> Files, string? Error)
{
    /// <summary>Gets a value indicating whether the listing is complete and trustworthy.</summary>
    public bool IsHealthy => Health == ProviderHealth.Healthy;
}

/// <summary>
/// One debrid provider: list the account, mint a playback link for one file.
/// </summary>
public interface IDebridClient
{
    /// <summary>Gets the provider id used in plan keys.</summary>
    string Provider { get; }

    /// <summary>Lists every file in the account. Never throws for an API failure.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The listing and its health.</returns>
    Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken);

    /// <summary>Mints a fresh playback URL for one file.</summary>
    /// <param name="key">The plan key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The URL, or null when it cannot be minted.</returns>
    Task<string?> MintLinkAsync(string key, CancellationToken cancellationToken);

    /// <summary>Checks the key without listing the account.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The provider health.</returns>
    Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken);
}
