using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Airlift.Core;

/// <summary>
/// Release data for every app comes from one file, release.json in Airlift's own repository, which the
/// Release index workflow rebuilds whenever a catalog app's releases change. Airlift never asks each app's
/// repository: one request per check, no API rate limit, and private repositories are simply listed as
/// unavailable instead of failing. Releases keep GitHub's field names and are validated by
/// <see cref="ParseRelease"/> exactly as if they came from the API.
/// </summary>
public sealed class GitHubClient(HttpClient http, StateStore store)
{
    public const string IndexUrl = "https://raw.githubusercontent.com/fezcode/Airlift/main/release.json";
    /// <summary>Points at a local release.json instead of the published one (testing an index before it is pushed).</summary>
    public const string IndexOverride = "AIRLIFT_RELEASE_INDEX";
    private const int MaxIndexBytes = 8_000_000;
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6), Retry = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent); return client;
    }
    public static string ValidateRepository(string repository)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new InvalidDataException("Invalid repository identity.");
        return repository;
    }
    public static Uri ValidateAssetUrl(string value, string repository)
    {
        var uri = new Uri(value);
        if (uri.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo != "" || !uri.IsDefaultPort ||
            !uri.AbsolutePath.StartsWith($"/{ValidateRepository(repository)}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Asset URL is outside its configured GitHub repository.");
        return uri;
    }
    private static TimeSpan Age(DateTimeOffset checkedAt) => DateTimeOffset.UtcNow - checkedAt;

    public async Task<ReleaseCache> GetReleaseAsync(CatalogApp app, bool prerelease, bool force, CancellationToken ct = default)
    {
        var key = app.Id + (prerelease ? ":preview" : ":stable");
        await _gate.WaitAsync(ct);
        try
        {
            var cached = store.Get<ReleaseCache>("releases", key);
            if (cached != null && Age(cached.CheckedAt) < (force || cached.Error != null ? Retry : Fresh)) return cached;
            var next = Derive(app, prerelease, await LoadIndexAsync(force, ct), cached);
            store.Put("releases", key, next); return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Browse public history independently of the install channel. The index holds every release Airlift shows, so there is one page.</summary>
    public async Task<ReleaseHistoryPage> GetHistoryAsync(CatalogApp app, int page = 1, bool force = false, CancellationToken ct = default)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var key = $"{app.Id}:{page}";
        await _gate.WaitAsync(ct);
        try
        {
            var cached = store.Get<ReleaseHistoryPage>("release-history", key);
            if (cached != null && cached.Error == null && Age(cached.CheckedAt) < (force ? Retry : Fresh)) return cached;
            if (page > 1) return new([], false, null, DateTimeOffset.UtcNow, null);
            var index = await LoadIndexAsync(force, ct);
            // Keep successful data's timestamp, so an offline attempt cannot make stale releases appear current.
            if (index.Json is null) return new(cached?.Releases ?? [], false, null, cached?.CheckedAt ?? DateTimeOffset.MinValue, index.Error);
            try
            {
                var releases = ReadReleases(index.Json, app.Repository, out _).OrderByDescending(x => x.Published).ToList();
                var next = new ReleaseHistoryPage(releases, false, null, DateTimeOffset.UtcNow, index.Error);
                store.Put("release-history", key, next); return next;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                return new(cached?.Releases ?? [], false, null, cached?.CheckedAt ?? DateTimeOffset.MinValue, error.Message);
            }
        }
        finally { _gate.Release(); }
    }

    private static ReleaseCache Derive(CatalogApp app, bool prerelease, ReleaseIndexCache index, ReleaseCache? cached)
    {
        var now = DateTimeOffset.UtcNow;
        // Nothing to read: keep what was known and say why it could not be refreshed.
        if (index.Json is null) return new(cached?.Release, null, now, index.Error ?? "Airlift's release list is unavailable.", cached?.Unavailable ?? false);
        try
        {
            var releases = ReadReleases(index.Json, app.Repository, out var available);
            if (!available) return new(null, null, now, null, Unavailable: true);
            var candidates = releases.Where(r => SemVersion.TryNormalize(r.Version, out _) && (prerelease || !r.Prerelease)).ToList();
            if (candidates.Count == 0) return new(null, null, now, "No public release found for this channel.");
            candidates.Sort((a, b) => SemVersion.Compare(b.Version, a.Version));
            return new(candidates[0], null, now, index.Error);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new(cached?.Release, null, now, error.Message, cached?.Unavailable ?? false);
        }
    }

    /// <summary>The app's releases from the index. A release that fails validation is left out rather than trusted.</summary>
    private static List<AppRelease> ReadReleases(string json, string repository, out bool available)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement? entry = null;
        foreach (var property in document.RootElement.GetProperty("apps").EnumerateObject())
            if (property.Name.Equals(repository, StringComparison.OrdinalIgnoreCase)) { entry = property.Value; break; }
        available = entry is { } found && found.TryGetProperty("available", out var flag) && flag.ValueKind == JsonValueKind.True;
        var releases = new List<AppRelease>();
        if (!available) return releases;
        foreach (var item in entry!.Value.GetProperty("releases").EnumerateArray())
        {
            try { releases.Add(ParseRelease(item, repository, allowUnversioned: true)); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or UriFormatException) { }
        }
        return releases;
    }

    private static void ValidateIndex(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1
            || !root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Airlift's release list uses a format this version does not understand.");
    }

    private async Task<ReleaseIndexCache> LoadIndexAsync(bool force, CancellationToken ct)
    {
        var cached = store.Get<ReleaseIndexCache>("release-index", "main");
        if (cached != null && Age(cached.CheckedAt) < (force || cached.Error != null ? Retry : Fresh)) return cached;
        var now = DateTimeOffset.UtcNow;
        ReleaseIndexCache next;
        try
        {
            if (Environment.GetEnvironmentVariable(IndexOverride) is { Length: > 0 } local)
            {
                var text = await File.ReadAllTextAsync(local, ct); ValidateIndex(text);
                next = new(text, null, now, null);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, IndexUrl);
                if (cached?.Json != null && cached.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotModified && cached?.Json != null) next = cached with { CheckedAt = now, Error = null };
                else
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("Airlift's release list has not been published yet.");
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Airlift's release list could not be loaded (HTTP {(int)response.StatusCode}).");
                    if (response.Content.Headers.ContentLength > MaxIndexBytes) throw new InvalidDataException("Airlift's release list is too large.");
                    var text = await response.Content.ReadAsStringAsync(ct);
                    if (text.Length > MaxIndexBytes) throw new InvalidDataException("Airlift's release list is too large.");
                    ValidateIndex(text);
                    next = new(text, response.Headers.ETag?.ToString(), now, null);
                }
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            next = new(cached?.Json, cached?.ETag, now, cached?.Json != null ? $"{error.Message} Showing saved release data." : error.Message);
        }
        store.Put("release-index", "main", next); return next;
    }

    public static AppRelease ParseRelease(JsonElement root, string repository, bool allowUnversioned = false)
    {
        if (root.GetProperty("draft").GetBoolean()) throw new InvalidDataException("Draft releases are not installable.");
        var tag = root.GetProperty("tag_name").GetString()!;
        if (!SemVersion.TryNormalize(tag, out var version))
        {
            if (!allowUnversioned) throw new InvalidDataException($"Unsupported version tag: {tag}");
            version = tag;
        }
        var url = new Uri(root.GetProperty("html_url").GetString()!);
        if (url.Scheme != "https" || url.Host != "github.com" || url.UserInfo != "" || !url.IsDefaultPort || !url.AbsolutePath.StartsWith($"/{ValidateRepository(repository)}/releases/tag/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected release URL.");
        var assets = new List<ReleaseAsset>();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("state").GetString() != "uploaded") continue;
            var name = asset.GetProperty("name").GetString()!;
            if (name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new InvalidDataException("Invalid asset filename.");
            var assetUrl = ValidateAssetUrl(asset.GetProperty("browser_download_url").GetString()!, repository);
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha = digest != null && Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$") ? digest[7..].ToLowerInvariant() : null;
            var size = asset.GetProperty("size").GetInt64(); if (size <= 0) continue;
            assets.Add(new(asset.GetProperty("id").GetInt64(), name, size, assetUrl.AbsoluteUri, sha));
        }
        return new(tag, version, url.AbsoluteUri, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", root.GetProperty("published_at").GetDateTimeOffset(), root.GetProperty("prerelease").GetBoolean(), assets);
    }
}
