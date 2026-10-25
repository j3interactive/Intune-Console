using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using IntuneConsole.Core.Graph;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Assets;

/// <summary>Dell's Warranty API (TechDirect). One client ID and secret per organization.</summary>
public sealed class DellWarranty
{
    private const string TokenUrl = "https://apigtwb2c.us.dell.com/auth/oauth/v2/token";
    private const string WarrantyUrl = "https://apigtwb2c.us.dell.com/PROD/sbil/eapi/v5/asset-entitlements";
    public const int MaxTagsPerCall = 100;

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public DellWarranty(HttpClient http, string clientId, string clientSecret)
    {
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}")));
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Dell sign-in failed ({(int)resp.StatusCode}). Check the TechDirect client ID and secret in Settings.");
        var n = JsonNode.Parse(text);
        _token = n.Str("access_token") ?? throw new InvalidOperationException("Dell didn't return a token.");
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, (n.Num("expires_in") ?? 3600) - 120));
        return _token;
    }

    /// <summary>Looks up warranties for service tags, 100 per call.</summary>
    public async Task<List<WarrantyInfo>> LookupAsync(IEnumerable<string> serviceTags, CancellationToken ct = default)
    {
        var tags = serviceTags.Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length is >= 5 and <= 10).Distinct().ToList();
        var results = new List<WarrantyInfo>();
        foreach (var chunk in tags.Chunk(MaxTagsPerCall))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{WarrantyUrl}?servicetags={Uri.EscapeDataString(string.Join(",", chunk))}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Dell warranty lookup failed ({(int)resp.StatusCode}): {(text.Length > 200 ? text[..200] : text)}");
            results.AddRange(Parse(JsonNode.Parse(text)));
        }
        return results;
    }

    /// <summary>Parses Dell's response: an array of assets, each with an "entitlements" array.
    /// The latest entitlement end date is the warranty end.</summary>
    public static List<WarrantyInfo> Parse(JsonNode? response)
    {
        var list = new List<WarrantyInfo>();
        if (response is not JsonArray arr) return list;
        foreach (var a in arr)
        {
            if (a is null) continue;
            var ents = (a["entitlements"] as JsonArray ?? new JsonArray()).Where(e => e is not null)
                .Select(e => (End: e.Date("endDate"), Level: e.Str("serviceLevelDescription")))
                .Where(e => e.End is not null)
                .OrderByDescending(e => e.End)
                .ToList();
            list.Add(new WarrantyInfo
            {
                ServiceTag = (a.Str("serviceTag") ?? "").ToUpperInvariant(),
                ShipDate = a.Date("shipDate"),
                WarrantyEnd = ents.FirstOrDefault().End,
                ServiceLevel = ents.FirstOrDefault().Level,
                Product = a.Str("productLineDescription") ?? a.Str("systemDescription"),
                Invalid = a.Bool("invalid") ?? false,
            });
        }
        return list;
    }
}
