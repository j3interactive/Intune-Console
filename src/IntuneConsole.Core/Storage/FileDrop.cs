using System.Security.Cryptography;

namespace IntuneConsole.Core.Storage;

/// <summary>Stages a file in an Azure Blob container so a Datto component can download it.
/// Uses two SAS tokens on the same container: an upload token (create/write/delete) kept on the
/// admin's machine, and a read-only token that is handed to the device. Devices never see the
/// upload token.</summary>
public sealed class FileDrop
{
    private readonly HttpClient _http;
    private readonly string _containerUrl;
    private readonly string _uploadSas;
    private readonly string _readSas;

    public FileDrop(HttpClient http, string containerUrl, string uploadSas, string readSas)
    {
        _http = http;
        _containerUrl = containerUrl.TrimEnd('/');
        _uploadSas = uploadSas.TrimStart('?');
        _readSas = readSas.TrimStart('?');
    }

    public sealed record StagedFile(string BlobName, string DownloadUrl, string Sha256, long Size);

    public async Task<StagedFile> UploadAsync(string localPath, CancellationToken ct = default)
    {
        var info = new FileInfo(localPath);
        if (info.Length > 2L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Files over 2 GB aren't supported by the console's file drop.");

        string sha;
        await using (var fs = File.OpenRead(localPath))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));

        var blobName = $"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}/{Uri.EscapeDataString(info.Name)}";
        var blobUrl = $"{_containerUrl}/{blobName}";

        await using (var fs = File.OpenRead(localPath))
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{blobUrl}?{_uploadSas}");
            req.Headers.Add("x-ms-blob-type", "BlockBlob");
            req.Headers.Add("x-ms-version", "2021-08-06");
            req.Content = new StreamContent(fs);
            req.Content.Headers.ContentLength = info.Length;
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Upload to the file drop failed ({(int)resp.StatusCode}). The upload token may have expired; update it in Settings.");
        }

        return new StagedFile(blobName, $"{blobUrl}?{_readSas}", sha, info.Length);
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_containerUrl}/{blobName}?{_uploadSas}");
        req.Headers.Add("x-ms-version", "2021-08-06");
        using var resp = await _http.SendAsync(req, ct);
        // Best effort; a lifecycle rule on the container cleans up anything missed.
    }
}
