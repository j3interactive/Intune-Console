using System.Globalization;
using System.Text.Json.Nodes;
using IntuneConsole.Core.Graph;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Assets;

/// <summary>Reads and writes asset records and history in the SharePoint lists.
/// Column internal names match scripts\sharepoint\Setup-AssetRegister.ps1.</summary>
public sealed class AssetStore
{
    private readonly SharePointLists _sp;
    private readonly string _assetsList;
    private readonly string _historyList;

    public AssetStore(SharePointLists sp, string assetsList = "Assets", string historyList = "Asset History")
    {
        _sp = sp;
        _assetsList = assetsList;
        _historyList = historyList;
    }

    public async Task<List<AssetRecord>> GetAssetsAsync(CancellationToken ct = default) =>
        (await _sp.GetItemsAsync(_assetsList, ct)).Select(FromFields).ToList();

    public async Task<List<AssetHistoryEntry>> GetHistoryAsync(CancellationToken ct = default) =>
        (await _sp.GetItemsAsync(_historyList, ct)).Select(HistoryFromFields).OrderByDescending(h => h.When).ToList();

    /// <summary>Creates or updates the record and writes history entries in the same call.</summary>
    public async Task SaveAsync(AssetRecord record, IEnumerable<AssetHistoryEntry> history, CancellationToken ct = default)
    {
        var fields = ToFields(record);
        if (record.ItemId is null) record.ItemId = await _sp.CreateItemAsync(_assetsList, fields, ct);
        else await _sp.UpdateItemAsync(_assetsList, record.ItemId, fields, ct);

        foreach (var h in history)
            await _sp.CreateItemAsync(_historyList, HistoryToFields(h), ct);
    }

    // ---------- Mapping ----------

    public static JsonObject ToFields(AssetRecord r)
    {
        var f = new JsonObject
        {
            ["Title"] = r.SerialNumber.Trim().ToUpperInvariant(),
            ["AssetTag"] = r.AssetTag ?? "",
            ["Status"] = r.Status,
            ["AssignedTo"] = r.AssignedTo ?? "",
            ["AssignedName"] = r.AssignedName ?? "",
            ["Model"] = r.Model ?? "",
            ["DeviceName"] = r.DeviceName ?? "",
            ["Supplier"] = r.Supplier ?? "",
            ["PONumber"] = r.PONumber ?? "",
            ["Location"] = r.Location ?? "",
            ["CostCenter"] = r.CostCenter ?? "",
            ["WarrantyLevel"] = r.WarrantyLevel ?? "",
            ["Notes"] = r.Notes ?? "",
        };
        // Dates and numbers: only send when set (SharePoint rejects empty strings for these types).
        if (r.Cost is not null) f["Cost"] = r.Cost.Value;
        void Date(string key, DateTimeOffset? v) { if (v is not null) f[key] = v.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"); }
        Date("PurchaseDate", r.PurchaseDate);
        Date("ShipDate", r.ShipDate);
        Date("WarrantyEnd", r.WarrantyEnd);
        Date("WarrantyChecked", r.WarrantyChecked);
        return f;
    }

    public static AssetRecord FromFields(JsonObject f) => new()
    {
        ItemId = f.Str("_itemId"),
        SerialNumber = (f.Str("Title") ?? "").Trim(),
        AssetTag = Blank(f.Str("AssetTag")),
        Status = Blank(f.Str("Status")) ?? AssetStatus.InUse,
        AssignedTo = Blank(f.Str("AssignedTo")),
        AssignedName = Blank(f.Str("AssignedName")),
        Model = Blank(f.Str("Model")),
        DeviceName = Blank(f.Str("DeviceName")),
        PurchaseDate = f.Date("PurchaseDate"),
        Cost = f.Num("Cost"),
        Supplier = Blank(f.Str("Supplier")),
        PONumber = Blank(f.Str("PONumber")),
        Location = Blank(f.Str("Location")),
        CostCenter = Blank(f.Str("CostCenter")),
        ShipDate = f.Date("ShipDate"),
        WarrantyEnd = f.Date("WarrantyEnd"),
        WarrantyLevel = Blank(f.Str("WarrantyLevel")),
        WarrantyChecked = f.Date("WarrantyChecked"),
        Notes = Blank(f.Str("Notes")),
    };

    public static JsonObject HistoryToFields(AssetHistoryEntry h) => new()
    {
        ["Title"] = h.SerialNumber.Trim().ToUpperInvariant(),
        ["EventType"] = h.Event,
        ["FromValue"] = h.FromValue ?? "",
        ["ToValue"] = h.ToValue ?? "",
        ["ChangedBy"] = h.By ?? "",
        ["EventDate"] = h.When.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["Notes"] = h.Notes ?? "",
    };

    public static AssetHistoryEntry HistoryFromFields(JsonObject f) => new()
    {
        SerialNumber = (f.Str("Title") ?? "").Trim(),
        Event = f.Str("EventType") ?? "",
        FromValue = Blank(f.Str("FromValue")),
        ToValue = Blank(f.Str("ToValue")),
        By = Blank(f.Str("ChangedBy")),
        When = f.Date("EventDate") ?? f.Date("Created") ?? DateTimeOffset.MinValue,
        Notes = Blank(f.Str("Notes")),
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
