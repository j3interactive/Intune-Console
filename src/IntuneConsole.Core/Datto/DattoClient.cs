using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntuneConsole.Core.Graph;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Datto;

/// <summary>Datto RMM API v2 client. Each admin uses their own API key and secret
/// (generated under Setup &gt; Users in Datto), so Datto's activity log shows who ran what.</summary>
public sealed class DattoClient
{
    private readonly HttpClient _http;
    private readonly string _apiUrl;      // e.g. https://merlot-api.centrastage.net
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public DattoClient(HttpClient http, string apiUrl, string apiKey, string apiSecret)
    {
        _http = http;
        _apiUrl = apiUrl.TrimEnd('/');
        _apiKey = apiKey;
        _apiSecret = apiSecret;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiUrl) && !string.IsNullOrWhiteSpace(_apiKey);

    private async Task<string> GetTokenAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/auth/oauth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("public-client:public")));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = _apiKey,
            ["password"] = _apiSecret,
        });
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new DattoException($"Datto sign-in failed ({(int)resp.StatusCode}). Check the API URL, key and secret in Settings.");

        var node = JsonNode.Parse(text);
        _token = node.Str("access_token") ?? throw new DattoException("Datto did not return a token.");
        var expiresIn = node.Num("expires_in") ?? 3600;
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
        return _token;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string pathOrUrl, object? body, CancellationToken ct)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : $"{_apiUrl}/api{pathOrUrl}";
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
                req.Content = new StringContent(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body),
                    Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _token = null; // token expired; get a new one and retry once
                continue;
            }
            if (resp.StatusCode == (HttpStatusCode)429 && attempt < 2)
            {
                // Datto asks clients to wait 60 seconds after a rate-limit response.
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                continue;
            }

            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new DattoException($"Datto returned {(int)resp.StatusCode}: {Trim(text)}");
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    /// <summary>All devices in the account (paged, 250 per page).</summary>
    public async Task<List<DattoDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var list = new List<DattoDevice>();
        string? next = "/v2/account/devices?max=250&page=0";
        for (var guard = 0; next is not null && guard < 200; guard++)
        {
            var page = await SendAsync(HttpMethod.Get, next, null, ct);
            if (page?["devices"] is JsonArray arr)
                list.AddRange(arr.Where(d => d is not null).Select(d => MapDevice(d!)));
            var nextUrl = page?["pageDetails"].Str("nextPageUrl");
            next = string.IsNullOrWhiteSpace(nextUrl) || nextUrl == "null" ? null : nextUrl;
        }
        return list;
    }

    internal static DattoDevice MapDevice(JsonNode d)
    {
        DateTimeOffset? lastSeen = null;
        var ls = d.Num("lastSeen");
        if (ls is > 0) lastSeen = DateTimeOffset.FromUnixTimeMilliseconds((long)ls.Value);
        return new DattoDevice
        {
            Uid = d.Str("uid") ?? "",
            Hostname = d.Str("hostname") ?? "",
            Online = d.Bool("online") ?? false,
            LastSeen = lastSeen,
            SiteName = d.Str("siteName"),
            PortalUrl = d.Str("portalUrl"),
            WebRemoteUrl = d.Str("webRemoteUrl"),
        };
    }

    public async Task<List<DattoComponent>> GetComponentsAsync(CancellationToken ct = default)
    {
        var list = new List<DattoComponent>();
        string? next = "/v2/account/components?max=250&page=0";
        for (var guard = 0; next is not null && guard < 50; guard++)
        {
            var page = await SendAsync(HttpMethod.Get, next, null, ct);
            if (page?["components"] is JsonArray arr)
                list.AddRange(arr.Where(c => c is not null).Select(c => new DattoComponent
                {
                    Uid = c.Str("uid") ?? "",
                    Name = c.Str("name") ?? ""
                }));
            var nextUrl = page?["pageDetails"].Str("nextPageUrl");
            next = string.IsNullOrWhiteSpace(nextUrl) || nextUrl == "null" ? null : nextUrl;
        }
        return list;
    }

    /// <summary>Runs a component on one device right away. Variables arrive in the component's
    /// PowerShell as environment variables ($env:Name).</summary>
    public async Task<string> RunQuickJobAsync(string deviceUid, string jobName, string componentUid,
        IDictionary<string, string>? variables = null, CancellationToken ct = default)
    {
        var vars = new JsonArray();
        if (variables is not null)
            foreach (var (k, v) in variables)
                vars.Add(new JsonObject { ["name"] = k, ["value"] = v });

        var body = new JsonObject
        {
            ["jobName"] = jobName,
            ["jobComponent"] = new JsonObject
            {
                ["componentUid"] = componentUid,
                ["variables"] = vars,
            }
        };
        var resp = await SendAsync(HttpMethod.Put, $"/v2/device/{deviceUid}/quickjob", body, ct);
        return resp?["job"].Str("uid") ?? resp.Str("uid") ?? "";
    }

    public async Task<DattoJobResult> GetJobAsync(string jobUid, CancellationToken ct = default)
    {
        var n = await SendAsync(HttpMethod.Get, $"/v2/job/{jobUid}", null, ct);
        return new DattoJobResult { JobUid = jobUid, Status = n.Str("status") ?? "" };
    }

    /// <summary>Polls a job until it finishes or the timeout passes. Returns the last status seen.</summary>
    public async Task<DattoJobResult> WaitForJobAsync(string jobUid, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        DattoJobResult last = new() { JobUid = jobUid, Status = "unknown" };
        while (DateTimeOffset.UtcNow < until)
        {
            last = await GetJobAsync(jobUid, ct);
            if (last.IsFinished) return last;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return last;
    }

    /// <summary>Matches Intune devices to Datto devices by hostname.</summary>
    public static void MatchDevices(IEnumerable<ManagedDevice> intune, IEnumerable<DattoDevice> datto)
    {
        var byHost = datto
            .Where(d => d.Hostname != "")
            .GroupBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.LastSeen ?? DateTimeOffset.MinValue).First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var dev in intune)
            dev.Datto = byHost.TryGetValue(dev.DeviceName, out var d) ? d : null;
    }
}

public sealed class DattoException(string message) : Exception(message);
