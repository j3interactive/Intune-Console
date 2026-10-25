using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IntuneConsole.Core.Graph;

/// <summary>Thin Microsoft Graph REST client. Calls are made as the signed-in admin (delegated),
/// so the admin's Intune RBAC role decides what succeeds.</summary>
public sealed class GraphClient
{
    public const string BetaBase = "https://graph.microsoft.com/beta/";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _getToken;

    public GraphClient(HttpClient http, Func<CancellationToken, Task<string>> getToken)
    {
        _http = http;
        _getToken = getToken;
    }

    /// <summary>Header needed for directory advanced queries ($search, $count, some $filter).</summary>
    public static readonly IReadOnlyDictionary<string, string> EventualConsistency =
        new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" };

    public Task<JsonNode?> GetAsync(string relativeOrAbsoluteUrl, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Get, relativeOrAbsoluteUrl, null, ct, headers);

    public Task<JsonNode?> PostAsync(string relativeUrl, object? body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, relativeUrl, body, ct, null);

    /// <summary>GET a collection and follow @odata.nextLink until done (or maxItems reached).</summary>
    public async Task<List<JsonNode>> GetAllAsync(string relativeUrl, int maxItems = 20000, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var items = new List<JsonNode>();
        string? url = relativeUrl;
        while (url is not null && items.Count < maxItems)
        {
            var page = await GetAsync(url, ct, headers).ConfigureAwait(false);
            if (page?["value"] is JsonArray arr)
                foreach (var n in arr) if (n is not null) items.Add(n);
            url = page?["@odata.nextLink"]?.GetValue<string>();
        }
        return items;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers)
    {
        var uri = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? new Uri(url) : new Uri(new Uri(BetaBase), url);

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _getToken(ct).ConfigureAwait(false));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (headers is not null)
                foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            if (body is not null)
            {
                var json = body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            else if (method == HttpMethod.Post)
            {
                req.Content = new StringContent("", Encoding.UTF8, "application/json");
            }

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if ((resp.StatusCode == (HttpStatusCode)429 || resp.StatusCode == HttpStatusCode.ServiceUnavailable) && attempt < 4)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new GraphException(resp.StatusCode, ExtractError(text), uri.ToString());

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    private static string ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var msg = node?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg))
            {
                // Intune often nests a JSON blob inside the message; keep the readable part.
                var idx = msg.IndexOf(" - Operation ID", StringComparison.Ordinal);
                return idx > 0 ? msg[..idx] : msg;
            }
        }
        catch (JsonException) { }
        return body.Length > 400 ? body[..400] : body;
    }
}

public sealed class GraphException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string Url { get; }

    public GraphException(HttpStatusCode status, string message, string url)
        : base(Friendly(status, message))
    {
        StatusCode = status;
        Url = url;
    }

    private static string Friendly(HttpStatusCode status, string message) => status switch
    {
        HttpStatusCode.Forbidden => $"Access denied by Intune. Your Intune role may not allow this action. ({message})",
        HttpStatusCode.Unauthorized => $"Sign-in expired or the app registration is missing a permission. ({message})",
        HttpStatusCode.NotFound => $"Not found in Intune. ({message})",
        _ => $"Graph returned {(int)status}: {message}"
    };
}

internal static class JsonExt
{
    public static string? Str(this JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is null) return null;
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"');
    }

    public static bool? Bool(this JsonNode? n, string key)
    {
        var v = n?[key];
        return v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : null;
    }

    public static double? Num(this JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        }
        return null;
    }

    public static DateTimeOffset? Date(this JsonNode? n, string key)
    {
        var s = n.Str(key);
        if (string.IsNullOrEmpty(s)) return null;
        if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return null;
        // Intune uses 0001-01-01 for "never".
        return d.Year < 2000 ? null : d;
    }
}
