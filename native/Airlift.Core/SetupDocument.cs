namespace Airlift.Core;
public sealed record SetupEntry(string AppId, string? InstalledVersion, AppPreferences Preferences);
public sealed record SetupDocument(int SchemaVersion, List<SetupEntry> Apps)
{
    public static SetupDocument Parse(string json, IReadOnlyList<CatalogApp> catalog)
    {
        if (json.Length > 1_000_000) throw new InvalidDataException("Setup file exceeds size limit.");
        var result = JsonData.Read<SetupDocument>(json);
        if (result.SchemaVersion != 1 || result.Apps == null || result.Apps.Count > 1000 || result.Apps.Any(a => a.Preferences == null || !catalog.Any(c => c.Id == a.AppId)) || result.Apps.Select(a => a.AppId).Distinct().Count() != result.Apps.Count)
            throw new InvalidDataException("Unknown app, duplicate entry, or unsupported setup schema.");
        return result;
    }
}
