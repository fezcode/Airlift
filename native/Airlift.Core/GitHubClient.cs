using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Airlift.Core;

public sealed class GitHubClient(HttpClient http, StateStore store)
{
    private DateTimeOffset _blockedUntil;
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
    public async Task<ReleaseCache> GetReleaseAsync(CatalogApp app, bool prerelease, bool force, CancellationToken ct = default)
    {
        var key = app.Id + (prerelease ? ":preview" : ":stable");
        await _gate.WaitAsync(ct);
        try
        {
            var cached = store.Get<ReleaseCache>("releases", key);
            if (cached != null && DateTimeOffset.UtcNow - cached.CheckedAt < (force || cached.Error != null ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(6))) return cached;
            if (_blockedUntil > DateTimeOffset.UtcNow) return cached is null ? new(null, null, DateTimeOffset.UtcNow, $"GitHub requests paused until {_blockedUntil.LocalDateTime:t}.") : cached with { Error = $"GitHub requests paused until {_blockedUntil.LocalDateTime:t}; showing cached data." };
            var endpoint = $"https://api.github.com/repos/{ValidateRepository(app.Repository)}/releases" + (prerelease ? "?per_page=100" : "/latest");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.ParseAdd("application/vnd.github+json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            try
            {
                using var response = await http.SendAsync(request, ct);
                ReleaseCache next;
                if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) next = cached with { CheckedAt = DateTimeOffset.UtcNow, Error = null };
                else if (response.StatusCode == HttpStatusCode.NotFound) next = new(null, null, DateTimeOffset.UtcNow, "No public release found for this channel.");
                else
                {
                    if ((int)response.StatusCode is 403 or 429)
                    {
                        _blockedUntil = DateTimeOffset.UtcNow.AddHours(1);
                        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var unix)) _blockedUntil = DateTimeOffset.FromUnixTimeSeconds(unix);
                        if (response.Headers.RetryAfter?.Delta is { } delay) _blockedUntil = DateTimeOffset.UtcNow.Add(delay);
                        throw new HttpRequestException($"GitHub access/rate limit. Retry after {_blockedUntil.LocalDateTime:t}.");
                    }
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct);
                    if (json.Length > 8_000_000) throw new InvalidDataException("Release metadata is too large.");
                    using var document = JsonDocument.Parse(json);
                    AppRelease release;
                    if (prerelease)
                    {
                        var releases = document.RootElement.EnumerateArray().Where(x => !x.GetProperty("draft").GetBoolean())
                            .Select(x => ParseRelease(x, app.Repository)).ToList();
                        if (releases.Count == 0) throw new InvalidDataException("No published releases found.");
                        releases.Sort((a, b) => SemVersion.Compare(b.Version, a.Version)); release = releases[0];
                    }
                    else { release = ParseRelease(document.RootElement, app.Repository); if (release.Prerelease) throw new InvalidDataException("Stable endpoint returned a prerelease."); }
                    next = new(release, response.Headers.ETag?.ToString(), DateTimeOffset.UtcNow, null);
                }
                store.Put("releases", key, next); return next;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException or FormatException)
            {
                ct.ThrowIfCancellationRequested();
                var next = new ReleaseCache(cached?.Release, cached?.ETag, DateTimeOffset.UtcNow, error.Message);
                store.Put("releases", key, next); return next;
            }
        }
        finally { _gate.Release(); }
    }
    /// <summary>Browse public history independently of the install channel. Never follows a server-supplied next URL.</summary>
    public async Task<ReleaseHistoryPage> GetHistoryAsync(CatalogApp app, int page = 1, bool force = false, CancellationToken ct = default)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var key = $"{app.Id}:{page}";
        await _gate.WaitAsync(ct);
        try
        {
            var cached = store.Get<ReleaseHistoryPage>("release-history", key);
            if (cached != null && DateTimeOffset.UtcNow - cached.CheckedAt < (force ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(6))) return cached;
            if (_blockedUntil > DateTimeOffset.UtcNow)
                return new(cached?.Releases ?? [], cached?.HasMore ?? false, cached?.ETag, cached?.CheckedAt ?? DateTimeOffset.MinValue, $"GitHub requests paused until {_blockedUntil.LocalDateTime:t}. Showing saved releases.");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ValidateRepository(app.Repository)}/releases?per_page=20&page={page}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            try
            {
                using var response = await http.SendAsync(request, ct);
                ReleaseHistoryPage next;
                if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
                    next = cached with { CheckedAt = DateTimeOffset.UtcNow, Error = null };
                else
                {
                    if ((int)response.StatusCode is 403 or 429)
                    {
                        _blockedUntil = DateTimeOffset.UtcNow.AddHours(1);
                        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var unix)) _blockedUntil = DateTimeOffset.FromUnixTimeSeconds(unix);
                        if (response.Headers.RetryAfter?.Delta is { } delay) _blockedUntil = DateTimeOffset.UtcNow.Add(delay);
                        throw new HttpRequestException($"GitHub access/rate limit. Retry after {_blockedUntil.LocalDateTime:t}.");
                    }
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct);
                    if (json.Length > 8_000_000) throw new InvalidDataException("Release metadata is too large.");
                    using var document = JsonDocument.Parse(json);
                    var releases = document.RootElement.EnumerateArray().Where(x => !x.GetProperty("draft").GetBoolean())
                        .Select(x => ParseRelease(x, app.Repository, allowUnversioned: true)).OrderByDescending(x => x.Published).ToList();
                    var hasMore = response.Headers.TryGetValues("Link", out var links) && links.Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
                    next = new(releases, hasMore, response.Headers.ETag?.ToString(), DateTimeOffset.UtcNow, null);
                }
                store.Put("release-history", key, next); return next;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException or FormatException)
            {
                ct.ThrowIfCancellationRequested();
                // Keep successful data's timestamp, so an offline attempt cannot make stale releases appear current.
                return new(cached?.Releases ?? [], cached?.HasMore ?? false, cached?.ETag, cached?.CheckedAt ?? DateTimeOffset.MinValue, error.Message);
            }
        }
        finally { _gate.Release(); }
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

