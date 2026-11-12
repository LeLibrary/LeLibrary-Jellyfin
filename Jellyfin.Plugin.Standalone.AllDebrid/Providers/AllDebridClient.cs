using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Standalone.AllDebrid.Providers;

/// <summary>Lists AllDebrid magnets and resolves their files.</summary>
public sealed class AllDebridClient : IDebridClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _next;
    private readonly HttpClient _http;
    private readonly string _key;
    /// <summary>Initializes a new instance of the <see cref="AllDebridClient"/> class.</summary>
    public AllDebridClient(HttpClient http, string key) { _http = http; _key = key; }
    /// <inheritdoc />
    public string Provider => "alldebrid";

    private async Task<HttpResponseMessage> Post(string path, IEnumerable<KeyValuePair<string, string>> values, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try { var wait = _next - Environment.TickCount64; if (wait > 0) await Task.Delay((int)wait, token).ConfigureAwait(false); _next = Environment.TickCount64 + 250; }
        finally { Gate.Release(); }
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(values) };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
        return await _http.SendAsync(req, token).ConfigureAwait(false);
    }
    private static readonly KeyValuePair<string, string>[] Empty = Array.Empty<KeyValuePair<string, string>>();
    private static ProviderHealth Health(HttpStatusCode status) => status switch { HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderHealth.InvalidKey, HttpStatusCode.TooManyRequests => ProviderHealth.RateLimited, _ => ProviderHealth.Unavailable };
    private static ProviderListing Failed(ProviderHealth health) => new(health, Array.Empty<DebridFile>(), "AllDebrid did not return a complete listing");
    private static bool String(JsonElement e, string key, out string value)
    {
        value = ""; if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? ""; return !string.IsNullOrWhiteSpace(value);
    }
    private static bool Id(JsonElement e, out string id)
    {
        id = ""; if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var value)) return false;
        id = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n.ToString(CultureInfo.InvariantCulture) : "";
        return id.Length > 0 && id.All(char.IsAsciiDigit);
    }
    private static bool Data(JsonElement root, out JsonElement data)
    {
        data = default; return String(root, "status", out var status) && status == "success" && root.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }
    /// <inheritdoc />
    public async Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Post("v4/user", Empty, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Health(response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return Data(json.RootElement, out _) ? ProviderHealth.Healthy : ProviderHealth.Unavailable;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return ProviderHealth.Unavailable; }
    }
    /// <inheritdoc />
    public async Task<ProviderListing> ListLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Post("v4.1/magnet/status", Empty, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Failed(Health(response.StatusCode));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!Data(json.RootElement, out var data) || !data.TryGetProperty("magnets", out var magnets) || magnets.ValueKind != JsonValueKind.Array) return Failed(ProviderHealth.Unavailable);
            var ready = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var magnet in magnets.EnumerateArray())
            {
                if (!Id(magnet, out var id) || !magnet.TryGetProperty("statusCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var status)) return Failed(ProviderHealth.Unavailable);
                if (status == 4 && !ready.TryAdd(id, String(magnet, "filename", out var name) ? name : "")) return Failed(ProviderHealth.Unavailable);
            }
            var files = new List<DebridFile>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in ready.Keys.Chunk(20))
            {
                using var part = await Post("v4/magnet/files", batch.Select(id => new KeyValuePair<string, string>("id[]", id)), cancellationToken).ConfigureAwait(false);
                if (!part.IsSuccessStatusCode) return Failed(Health(part.StatusCode));
                using var detail = JsonDocument.Parse(await part.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (!Data(detail.RootElement, out var details) || !details.TryGetProperty("magnets", out var rows) || rows.ValueKind != JsonValueKind.Array) return Failed(ProviderHealth.Unavailable);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows.EnumerateArray())
                {
                    if (!Id(row, out var id) || !batch.Contains(id, StringComparer.Ordinal) || !seen.Add(id) || row.TryGetProperty("error", out _)
                        || !row.TryGetProperty("files", out var nodes) || !ReadTree(nodes, id, ready[id], files, keys, 0)) return Failed(ProviderHealth.Unavailable);
                }
                if (seen.Count != batch.Length) return Failed(ProviderHealth.Unavailable);
            }
            return new ProviderListing(ProviderHealth.Healthy, files, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return Failed(ProviderHealth.Unavailable); }
    }
    private static bool ReadTree(JsonElement nodes, string id, string release, List<DebridFile> output, HashSet<string> keys, int depth)
    {
        if (nodes.ValueKind != JsonValueKind.Array || depth > 32) return false;
        foreach (var node in nodes.EnumerateArray())
        {
            if (!String(node, "n", out var name)) return false;
            if (node.TryGetProperty("e", out var children)) { if (!ReadTree(children, id, release, output, keys, depth + 1)) return false; continue; }
            if (!String(node, "l", out var link) || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "alldebrid.com", StringComparison.OrdinalIgnoreCase)) return false;
            var key = "alldebrid:" + id + ":" + Convert.ToHexString(Encoding.UTF8.GetBytes(link));
            if (!keys.Add(key)) return false;
            var size = node.TryGetProperty("s", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var n) ? n : 0;
            output.Add(new DebridFile(key, name, size, release));
        }
        return true;
    }
    /// <inheritdoc />
    public async Task<MintResult> MintAsync(string key, CancellationToken cancellationToken)
    {
        if (!PlanKey.TryParse(key, out var parsed) || parsed.Provider != Provider) return MintResult.Fail(MintStatus.NotFound);
        try
        {
            var link = Encoding.UTF8.GetString(Convert.FromHexString(parsed.FileId));
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "alldebrid.com", StringComparison.OrdinalIgnoreCase)) return MintResult.Fail(MintStatus.Error);
            using var response = await Post("v4/link/unlock", new[] { new KeyValuePair<string, string>("link", link) }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return MintResult.Fail(MintStatus.Error);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return Data(json.RootElement, out var data) && String(data, "link", out var result) && Uri.TryCreate(result, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
                ? new MintResult(MintStatus.Ok, result) : MintResult.Fail(MintStatus.Error);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException) { return MintResult.Fail(MintStatus.Error); }
    }
}
