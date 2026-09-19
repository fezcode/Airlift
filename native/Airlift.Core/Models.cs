using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Airlift.Core;

public sealed record CatalogApp(string Id, string Name, string Version, string Publisher, string Category,
    string Tagline, string Description, string Color, string Icon, string Project, string Repository, string DefaultDirectory);
public sealed record CatalogDocument(List<CatalogApp> Apps);
public sealed record ReleaseAsset(long Id, string Name, long Size, string Url, string? Sha256);
public sealed record AppRelease(string Tag, string Version, string Url, string Notes, DateTimeOffset Published,
    bool Prerelease, List<ReleaseAsset> Assets);
public sealed record ReleaseCache(AppRelease? Release, string? ETag, DateTimeOffset CheckedAt, string? Error);
public sealed record ReleaseHistoryPage(List<AppRelease> Releases, bool HasMore, string? ETag, DateTimeOffset CheckedAt, string? Error);
public sealed record InstalledApp(string AppId, string Version, string Directory, string Provider, string? Executable = null, bool Machine = false);
public sealed record AppPreferences(bool Pinned = false, bool IncludePrerelease = false);
public sealed record AppSettings(bool CheckOnStartup = true, int CheckIntervalHours = 6);
public sealed record PackagePlan(CatalogApp App, AppRelease Release, ReleaseAsset Asset, string Os, string Arch, string Format);
public sealed record Operation(string Id, string AppId, string AppName, string Action, string Version, string Status,
    double Progress, string Message, DateTimeOffset Started, string? File = null)
{
    public bool Active => Status is "Queued" or "Downloading" or "Verifying" or "Installing" or "Uninstalling" or "Reconciling";
    public bool CanCancel => Status is "Queued" or "Downloading" or "Verifying";
}
public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Empty JSON document.");
}
public static class Catalog
{
    public static IReadOnlyList<CatalogApp> Load()
    {
        var assembly = typeof(Catalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("catalog.json")))!;
        return JsonSerializer.Deserialize<CatalogDocument>(stream, JsonData.Options)!.Apps;
    }
    public static CatalogApp Find(IEnumerable<CatalogApp> apps, string value) => apps.FirstOrDefault(a =>
        a.Id.Equals(value, StringComparison.OrdinalIgnoreCase) || a.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown app: {value}");
}
public static class HostPlatform
{
    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    public static string Arch => RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => "unsupported" };
}
public static partial class SemVersion
{
    [GeneratedRegex(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();
    public static bool TryNormalize(string value, out string normalized)
    {
        normalized = value.Trim().TrimStart('v');
        return Pattern().IsMatch(normalized);
    }
    public static int Compare(string left, string right)
    {
        var a = Pattern().Match(left); var b = Pattern().Match(right);
        if (!a.Success || !b.Success) throw new FormatException("A release version must use semantic versioning (major.minor.patch).");
        for (var i = 1; i <= 3; i++)
        {
            var comparison = System.Numerics.BigInteger.Parse(a.Groups[i].Value).CompareTo(System.Numerics.BigInteger.Parse(b.Groups[i].Value));
            if (comparison != 0) return comparison;
        }
        var ap = a.Groups[4].Value; var bp = b.Groups[4].Value;
        if (ap == bp) return 0;
        if (ap.Length == 0) return 1;
        if (bp.Length == 0) return -1;
        var aa = ap.Split('.'); var bb = bp.Split('.');
        for (var i = 0; i < Math.Min(aa.Length, bb.Length); i++)
        {
            var an = System.Numerics.BigInteger.TryParse(aa[i], out var av); var bn = System.Numerics.BigInteger.TryParse(bb[i], out var bv);
            var c = an && bn ? av.CompareTo(bv) : an ? -1 : bn ? 1 : string.CompareOrdinal(aa[i], bb[i]);
            if (c != 0) return c;
        }
        return aa.Length.CompareTo(bb.Length);
    }
    public static bool IsNewer(string available, string installed) => TryNormalize(available, out var normalizedAvailable) && TryNormalize(installed, out var normalizedInstalled) && Compare(normalizedAvailable, normalizedInstalled) > 0;
}
public static class PackageResolver
{
    // Explicit legacy rules. Unqualified Forge installers in this catalog are Windows x64 builds;
    // the downloaded PE and embedded Forge identity are checked before execution.
    public static PackagePlan Resolve(CatalogApp app, AppRelease release, string os, string arch)
    {
        if (arch is not ("x64" or "arm64")) throw new NotSupportedException("This processor architecture is not supported.");
        string? expected = os == "windows" && arch == "x64" ? $"{app.Name}-Setup-{release.Version}.exe" : null;
        var format = "forge-exe";
        if (app.Id == "com.fezcode.typewriter" && os == "linux" && arch == "x64") { expected = "typewriter-linux-x86_64.tar.gz"; format = "portable-tar"; }
        if (app.Id == "com.fezcode.typewriter" && os == "macos") { expected = "typewriter-macos-universal.tar.gz"; format = "portable-tar"; }
        if (expected is null) throw new NotSupportedException($"No mapped {os}/{arch} package for {app.Name}.");
        var matches = release.Assets.Where(a => a.Name.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) throw new NotSupportedException($"Release {release.Tag} has no unambiguous {os}/{arch} package for {app.Name}.");
        return new(app, release, matches[0], os, arch, format);
    }
}
