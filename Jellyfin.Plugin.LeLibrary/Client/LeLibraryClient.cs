using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Sync;

namespace Jellyfin.Plugin.LeLibrary.Client;

/// <summary>Where a plan came from.</summary>
public enum PlanSource
{
    /// <summary>A fresh copy from LeLibrary.</summary>
    Network,

    /// <summary>The last known good copy, because LeLibrary could not be reached.</summary>
    Cache,
}

/// <summary>The outcome of one plan fetch.</summary>
/// <param name="Ok">Whether there is a usable plan.</param>
/// <param name="Error">A readable reason when something went wrong.</param>
/// <param name="Plan">The plan (empty when !Ok).</param>
/// <param name="Source">Network or cache.</param>
/// <param name="Warnings">Notes from the parser.</param>
public sealed record PlanFetch(bool Ok, string? Error, JellyfinPlan Plan, PlanSource Source, IReadOnlyList<string> Warnings)
{
    /// <summary>Gets an empty failed fetch.</summary>
    /// <param name="error">Why.</param>
    /// <returns>The fetch result.</returns>
    public static PlanFetch Failed(string error) => new(false, error, new JellyfinPlan(), PlanSource.Network, Array.Empty<string>());
}

/// <summary>
/// Reads the plan from the setup install URL and keeps the last good copy on
/// disk. A failed fetch never replaces the cache: the sync then runs on the old
/// plan instead of on nothing.
/// </summary>
public sealed class LeLibraryClient : IPlanSource
{
    private readonly HttpClient _http;
    private readonly string _cachePath;

    /// <summary>Initializes a new instance of the <see cref="LeLibraryClient"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="cachePath">Where the last known good plan is kept.</param>
    public LeLibraryClient(HttpClient http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
    }

    /// <summary>Fetches the plan.</summary>
    /// <param name="setupUrl">The setup install URL (with or without /manifest.json).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The plan and where it came from.</returns>
    public async Task<PlanFetch> FetchAsync(string? setupUrl, CancellationToken cancellationToken)
    {
        var url = PlanUrl(setupUrl);
        if (url is null)
        {
            return PlanFetch.Failed("the LeLibrary URL is not set");
        }

        var cached = ReadCache();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(cached?.Etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached!.Etag);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception err) when (err is HttpRequestException or TaskCanceledException)
        {
            return Fallback(cached, "LeLibrary could not be reached");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Fallback(cached, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fallback(cached, $"LeLibrary answered {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            PlanParseResult parsed;
            try
            {
                parsed = PlanParser.Parse(body);
            }
            catch (PlanFormatException err)
            {
                // A 200 with an error (or garbage) is a failure, not an empty
                // library: never let it become "delete everything".
                return Fallback(cached, err.Message);
            }

            var etag = response.Headers.ETag?.ToString();
            WriteCache(new CachedPlan(etag, body));
            return new PlanFetch(true, null, parsed.Plan, PlanSource.Network, parsed.Warnings);
        }
    }

    /// <summary>
    /// Turns whatever the user pasted into the plan URL: the setup URL, with an
    /// optional trailing slash and an optional /manifest.json suffix.
    /// </summary>
    /// <param name="setupUrl">The pasted value.</param>
    /// <returns>The plan URL, or null.</returns>
    public static string? PlanUrl(string? setupUrl)
    {
        var value = setupUrl?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/manifest.json".Length];
        }

        return $"{uri.Scheme}://{uri.Authority}{path}/jellyfin/config.json";
    }

    private PlanFetch Fallback(CachedPlan? cached, string? error)
    {
        if (cached is null || string.IsNullOrWhiteSpace(cached.Body))
        {
            return PlanFetch.Failed(error ?? "no cached plan");
        }

        try
        {
            var parsed = PlanParser.Parse(cached.Body);
            return new PlanFetch(true, error, parsed.Plan, PlanSource.Cache, parsed.Warnings);
        }
        catch (PlanFormatException)
        {
            return PlanFetch.Failed(error ?? "the cached plan could not be read");
        }
    }

    private CachedPlan? ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            return JsonSerializer.Deserialize<CachedPlan>(File.ReadAllText(_cachePath));
        }
        catch (Exception err) when (err is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCache(CachedPlan plan)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(plan));
            File.Move(temp, _cachePath, true);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException)
        {
            // A cache write failure must not fail the sync; the next fetch just
            // starts from scratch.
        }
    }

    private sealed record CachedPlan(string? Etag, string? Body);
}
