using System.Globalization;
using System.Text;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Assets;

/// <summary>CSV export and import for the asset register (for bulk edits in Excel).</summary>
public static class AssetCsv
{
    public static readonly string[] Columns =
    [
        "SerialNumber", "AssetTag", "Status", "AssignedTo", "AssignedName", "DeviceName", "Model", "PurchaseDate", "Cost",
        "Supplier", "PONumber", "Location", "CostCenter", "ShipDate", "WarrantyEnd", "WarrantyLevel", "Notes",
    ];

    public static string Export(IEnumerable<AssetRecord> records)
    {
        var sb = new StringBuilder(string.Join(",", Columns)).Append("\r\n");
        foreach (var r in records)
            sb.Append(string.Join(",", new[]
            {
                r.SerialNumber, r.AssetTag, r.Status, r.AssignedTo, r.AssignedName, r.DeviceName, r.Model, D(r.PurchaseDate),
                r.Cost?.ToString(CultureInfo.InvariantCulture), r.Supplier, r.PONumber, r.Location, r.CostCenter, D(r.ShipDate),
                D(r.WarrantyEnd), r.WarrantyLevel, r.Notes,
            }.Select(Quote))).Append("\r\n");
        return sb.ToString();
    }

    private static string? D(DateTimeOffset? d) => d?.ToLocalTime().ToString("yyyy-MM-dd");

    private static string Quote(string? s)
    {
        s ??= "";
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;   // spreadsheet formula injection guard
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    /// <summary>RFC 4180-style parser: quoted fields, doubled quotes, commas and line breaks inside quotes.</summary>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows.Where(r => r.Any(f => f.Trim().Length > 0)).ToList();
    }

    public sealed class ImportResult
    {
        public List<(AssetRecord Before, AssetRecord After)> Updates { get; } = new();
        public List<AssetRecord> Creates { get; } = new();
        public List<string> Errors { get; } = new();
        public int Unchanged { get; set; }
    }

    /// <summary>Plans an import against existing records (matched on SerialNumber). Only columns present
    /// in the file are changed; blank cells leave the existing value alone. Nothing is saved here.</summary>
    public static ImportResult PlanImport(string csvText, IEnumerable<AssetRecord> existing)
    {
        var result = new ImportResult();
        var rows = Parse(csvText.TrimStart('﻿'));
        if (rows.Count == 0) { result.Errors.Add("The file is empty."); return result; }

        var header = rows[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                      || h.Replace(" ", "").Equals(name, StringComparison.OrdinalIgnoreCase));
        var serialCol = Col("SerialNumber") >= 0 ? Col("SerialNumber") : Col("Serial") >= 0 ? Col("Serial") : Col("ServiceTag");
        if (serialCol < 0) { result.Errors.Add("No SerialNumber column found."); return result; }

        var byKey = existing.GroupBy(r => AssetRegister.Key(r.SerialNumber)).ToDictionary(g => g.Key, g => g.First());
        var seenInFile = new HashSet<string>();

        for (var i = 1; i < rows.Count; i++)
        {
            var cells = rows[i];
            string? Cell(string name)
            {
                var c = Col(name);
                if (c < 0 || c >= cells.Count) return null;
                var v = cells[c].Trim();
                if (v.StartsWith('\'') && v.Length > 1 && "=+-@".Contains(v[1])) v = v[1..];
                return v.Length == 0 ? null : v;
            }

            var serial = AssetRegister.Key(serialCol < cells.Count ? cells[serialCol] : "");
            if (serial.Length == 0) { result.Errors.Add($"Row {i + 1}: no serial number."); continue; }
            if (!seenInFile.Add(serial)) { result.Errors.Add($"Row {i + 1}: {serial} appears more than once; only the first is used."); continue; }

            var exists = byKey.TryGetValue(serial, out var before);
            var after = exists ? before!.Clone() : new AssetRecord { SerialNumber = serial };
            var rowErrors = new List<string>();

            void Text(string col, Action<string> set) { if (Cell(col) is { } v) set(v); }
            void Date(string col, Action<DateTimeOffset> set)
            {
                if (Cell(col) is not { } v) return;
                if (DateTimeOffset.TryParse(v, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d)
                    || DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out d)) set(d);
                else rowErrors.Add($"{col} \"{v}\" isn't a date");
            }

            Text("AssetTag", v => after.AssetTag = v);
            if (Cell("Status") is { } st)
            {
                var match = AssetStatus.All.FirstOrDefault(s => s.Equals(st, StringComparison.OrdinalIgnoreCase));
                if (match is null) rowErrors.Add($"Status \"{st}\" should be one of: {string.Join(", ", AssetStatus.All)}");
                else after.Status = match;
            }
            Text("AssignedTo", v => after.AssignedTo = v);
            Text("AssignedName", v => after.AssignedName = v);
            Text("DeviceName", v => after.DeviceName = v);
            Text("Model", v => after.Model = v);
            Date("PurchaseDate", d => after.PurchaseDate = d);
            if (Cell("Cost") is { } cost)
            {
                var cleaned = cost.Replace("$", "").Replace("£", "").Replace("€", "").Replace(",", "").Trim();
                if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) after.Cost = c;
                else rowErrors.Add($"Cost \"{cost}\" isn't a number");
            }
            Text("Supplier", v => after.Supplier = v);
            Text("PONumber", v => after.PONumber = v);
            Text("Location", v => after.Location = v);
            Text("CostCenter", v => after.CostCenter = v);
            Text("Notes", v => after.Notes = v);

            if (rowErrors.Count > 0) { result.Errors.Add($"Row {i + 1} ({serial}): {string.Join("; ", rowErrors)}. Row skipped."); continue; }

            if (!exists) result.Creates.Add(after);
            else if (AssetRegister.Changes(before, after, "").Count > 0 || !string.Equals(before!.DeviceName, after.DeviceName) || !string.Equals(before.Model, after.Model))
                result.Updates.Add((before!, after));
            else result.Unchanged++;
        }
        return result;
    }
}
