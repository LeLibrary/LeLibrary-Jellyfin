using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Standalone.Premiumize.Providers;

/// <summary>Reads Premiumize cloud files and resolves their delivery links.</summary>
public sealed class PremiumizeClient : IDebridClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    /// <summary>Initializes a new instance of the <see cref="PremiumizeClient"/> class.</summary>
    public PremiumizeClient(HttpClient http, string apiKey) { _http = http; _apiKey = apiKey; }
    /// <inheritdoc />
    public string Provider => "premiumize";

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        return await _http.SendAsync(request, token).ConfigureAwait(false);
    }
    private static ProviderHealth Status(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderHealth.InvalidKey,
        HttpStatusCode.TooManyRequests => ProviderHealth.RateLimited,
        _ => ProviderHealth.Unavailable,
    };
    private static bool Text(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
    private static bool Success(JsonElement element) => Text(element, "status", out var status) && status == "success";
    private static ProviderListing Failed(ProviderHealth health) => new(health, Array.Empty<DebridFile>(), "Premiumize did not return a complete listing");

    /// <inheritdoc />
    public async Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetAsync("account/info", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Status(response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return Success(json.RootElement) ? ProviderHealth.Healthy : ProviderHealth.Unavailable;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return ProviderHealth.Unavailable; }
    }
    /// <inheritdoc />
    public async Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetAsync("item/listall", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Failed(Status(response.StatusCode));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = json.RootElement;
            if (!Success(root) || !root.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array) return Failed(ProviderHealth.Unavailable);
            var files = new List<DebridFile>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in array.EnumerateArray())
            {
                if (!Text(file, "id", out var id) || !Text(file, "name", out var name) || !seen.Add(id)) return Failed(ProviderHealth.Unavailable);
                var key = "premiumize:" + Convert.ToHexString(Encoding.UTF8.GetBytes(id)) + ":0";
                var size = file.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var value) ? value : 0;
                var release = Text(file, "path", out var path) ? System.IO.Path.GetDirectoryName(path.Replace('\\', '/')) : null;
                files.Add(new DebridFile(key, name, size, release));
            }
            return new ProviderListing(ProviderHealth.Healthy, files, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return Failed(ProviderHealth.Unavailable); }
    }
    /// <inheritdoc />
    public async Task<MintResult> MintAsync(string key, CancellationToken cancellationToken)
    {
        if (!PlanKey.TryParse(key, out var parsed) || parsed.Provider != Provider || parsed.FileId != "0") return MintResult.Fail(MintStatus.NotFound);
        try
        {
            var id = Encoding.UTF8.GetString(Convert.FromHexString(parsed.ItemId));
            using var response = await GetAsync("item/details?id=" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return MintResult.Fail(MintStatus.NotFound);
            if (!response.IsSuccessStatusCode) return MintResult.Fail(MintStatus.Error);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return Success(json.RootElement) && Text(json.RootElement, "link", out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? new MintResult(MintStatus.Ok, url) : MintResult.Fail(MintStatus.Error);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException) { return MintResult.Fail(MintStatus.Error); }
    }
}
