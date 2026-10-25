using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Graph;

/// <summary>Windows LAPS passwords and BitLocker recovery keys from Entra ID.
/// Every reveal is recorded in Entra's audit log under the signed-in admin.</summary>
public sealed class SecretsService
{
    private const string V1 = "https://graph.microsoft.com/v1.0/";

    // Optional on these APIs, but they make the console identifiable in Microsoft's logs.
    private static readonly IReadOnlyDictionary<string, string> ClientHeaders = new Dictionary<string, string>
    {
        ["ocp-client-name"] = "Intune Troubleshooting Console",
        ["ocp-client-version"] = "1.0",
    };

    private readonly GraphClient _graph;

    public SecretsService(GraphClient graph) => _graph = graph;

    /// <summary>Gets the LAPS password(s) for a device by its Entra device ID (Intune's azureADDeviceId).
    /// Needs DeviceLocalCredential.Read.All, and the admin needs the Cloud Device Administrator or
    /// Intune Administrator role in Entra.</summary>
    public async Task<LapsInfo?> GetLapsAsync(string entraDeviceId, CancellationToken ct = default)
    {
        JsonNode? n;
        try
        {
            n = await _graph.GetAsync($"{V1}directory/deviceLocalCredentials/{entraDeviceId}?$select=credentials,deviceName,lastBackupDateTime,refreshDateTime",
                ct, ClientHeaders);
        }
        catch (GraphException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Some tenants key this by the Entra object ID instead; look it up and try once more.
            var objectId = await FindEntraObjectIdAsync(entraDeviceId, ct);
            if (objectId is null) return null;
            try
            {
                n = await _graph.GetAsync($"{V1}directory/deviceLocalCredentials/{objectId}?$select=credentials,deviceName,lastBackupDateTime,refreshDateTime",
                    ct, ClientHeaders);
            }
            catch (GraphException ex2) when (ex2.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }
        return ParseLaps(n?["value"] ?? n);
    }

    /// <summary>When the LAPS password was last backed up, without reading the password itself
    /// (so nothing is revealed). Null if none is backed up.</summary>
    public async Task<DateTimeOffset?> GetLapsLastBackupAsync(string entraDeviceId, CancellationToken ct = default)
    {
        try
        {
            var n = await _graph.GetAsync($"{V1}directory/deviceLocalCredentials/{entraDeviceId}?$select=lastBackupDateTime", ct, ClientHeaders);
            return (n?["value"] ?? n).Date("lastBackupDateTime");
        }
        catch (GraphException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var objectId = await FindEntraObjectIdAsync(entraDeviceId, ct);
            if (objectId is null) return null;
            try
            {
                var n = await _graph.GetAsync($"{V1}directory/deviceLocalCredentials/{objectId}?$select=lastBackupDateTime", ct, ClientHeaders);
                return (n?["value"] ?? n).Date("lastBackupDateTime");
            }
            catch (GraphException ex2) when (ex2.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
    }

    public static LapsInfo? ParseLaps(JsonNode? v)
    {
        if (v is null) return null;
        var creds = (v["credentials"] as JsonArray ?? new JsonArray())
            .Where(c => c is not null)
            .Select(c => new LapsCredential
            {
                AccountName = c.Str("accountName") ?? "",
                Password = Decode(c.Str("passwordBase64")),
                BackupTime = c.Date("backupDateTime"),
            })
            .OrderByDescending(c => c.BackupTime)
            .ToList();
        return new LapsInfo
        {
            DeviceName = v.Str("deviceName") ?? "",
            LastBackup = v.Date("lastBackupDateTime"),
            NextRotation = v.Date("refreshDateTime"),
            Credentials = creds,
        };
    }

    private static string Decode(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { return ""; }
    }

    /// <summary>Lists BitLocker keys for a device (without the key values).</summary>
    public async Task<List<BitLockerKey>> ListBitLockerKeysAsync(string entraDeviceId, CancellationToken ct = default)
    {
        var filter = Uri.EscapeDataString($"deviceId eq '{entraDeviceId}'");
        var nodes = await _graph.GetAllAsync($"{V1}informationProtection/bitlocker/recoveryKeys?$filter={filter}", ct: ct, headers: ClientHeaders);
        return nodes.Select(n => new BitLockerKey
        {
            Id = n.Str("id") ?? "",
            Created = n.Date("createdDateTime"),
            VolumeType = n.Str("volumeType") ?? "",
        }).OrderByDescending(k => k.Created).ToList();
    }

    /// <summary>Reads one recovery key value. Needs BitlockerKey.Read.All plus a role such as
    /// Helpdesk Administrator, Cloud Device Administrator or Intune Administrator.</summary>
    public async Task<string> GetBitLockerKeyAsync(string keyId, CancellationToken ct = default)
    {
        var n = await _graph.GetAsync($"{V1}informationProtection/bitlocker/recoveryKeys/{keyId}?$select=key", ct, ClientHeaders);
        return (n?["value"] ?? n).Str("key") ?? throw new InvalidOperationException("Entra didn't return a key value.");
    }

    private async Task<string?> FindEntraObjectIdAsync(string entraDeviceId, CancellationToken ct)
    {
        try
        {
            var filter = Uri.EscapeDataString($"deviceId eq '{entraDeviceId}'");
            var page = await _graph.GetAsync($"{V1}devices?$filter={filter}&$select=id", ct);
            return (page?["value"] as JsonArray)?.FirstOrDefault().Str("id");
        }
        catch (GraphException) { return null; }
    }
}
