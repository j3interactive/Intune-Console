using System.Text.Json.Nodes;
using IntuneConsole.Core.Graph;

namespace IntuneConsole.Core.Assets;

/// <summary>Minimal SharePoint list access through Graph (delegated Sites.Selected).</summary>
public sealed class SharePointLists
{
    private const string V1 = "https://graph.microsoft.com/v1.0/";
    private readonly GraphClient _graph;
    private string? _siteId;
    private readonly Dictionary<string, string> _listIds = new(StringComparer.OrdinalIgnoreCase);

    public string SiteUrl { get; }

    public SharePointLists(GraphClient graph, string siteUrl)
    {
        _graph = graph;
        SiteUrl = siteUrl.TrimEnd('/');
    }

    /// <summary>Turns https://contoso.sharepoint.com/sites/IT into the Graph path "contoso.sharepoint.com:/sites/IT".</summary>
    public static string SitePath(string siteUrl)
    {
        if (!Uri.TryCreate(siteUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("The SharePoint site URL should look like https://contoso.sharepoint.com/sites/IT");
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Length == 0 ? uri.Host : $"{uri.Host}:{path}";
    }

    public async Task<string> GetSiteIdAsync(CancellationToken ct = default)
    {
        if (_siteId is not null) return _siteId;
        var n = await _graph.GetAsync($"{V1}sites/{SitePath(SiteUrl)}?$select=id", ct);
        return _siteId = n.Str("id") ?? throw new InvalidOperationException("SharePoint didn't return a site ID.");
    }

    private async Task<string> ListIdAsync(string listName, CancellationToken ct)
    {
        if (_listIds.TryGetValue(listName, out var id)) return id;
        var site = await GetSiteIdAsync(ct);
        var filter = Uri.EscapeDataString($"displayName eq '{listName.Replace("'", "''")}'");
        var page = await _graph.GetAsync($"{V1}sites/{site}/lists?$filter={filter}&$select=id,displayName", ct);
        id = (page?["value"] as JsonArray)?.FirstOrDefault().Str("id")
             ?? throw new InvalidOperationException($"The list \"{listName}\" wasn't found on {SiteUrl}. Run scripts\\sharepoint\\Setup-AssetRegister.ps1.");
        _listIds[listName] = id;
        return id;
    }

    /// <summary>All items with their fields. Each node is the item's "fields" object plus "_itemId".</summary>
    public async Task<List<JsonObject>> GetItemsAsync(string listName, CancellationToken ct = default)
    {
        var site = await GetSiteIdAsync(ct);
        var list = await ListIdAsync(listName, ct);
        var nodes = await _graph.GetAllAsync($"{V1}sites/{site}/lists/{list}/items?$expand=fields&$top=999", maxItems: 100000, ct: ct);
        var result = new List<JsonObject>();
        foreach (var n in nodes)
        {
            if (n["fields"] is not JsonObject f) continue;
            var copy = (JsonObject)f.DeepClone();
            copy["_itemId"] = n.Str("id");
            result.Add(copy);
        }
        return result;
    }

    public async Task<string> CreateItemAsync(string listName, JsonObject fields, CancellationToken ct = default)
    {
        var site = await GetSiteIdAsync(ct);
        var list = await ListIdAsync(listName, ct);
        var resp = await _graph.PostAsync($"{V1}sites/{site}/lists/{list}/items", new JsonObject { ["fields"] = fields }, ct);
        return resp.Str("id") ?? "";
    }

    public async Task UpdateItemAsync(string listName, string itemId, JsonObject fields, CancellationToken ct = default)
    {
        var site = await GetSiteIdAsync(ct);
        var list = await ListIdAsync(listName, ct);
        await _graph.PatchAsync($"{V1}sites/{site}/lists/{list}/items/{itemId}/fields", fields, ct);
    }
}
