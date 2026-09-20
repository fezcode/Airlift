using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Airlift.Core;
using Xunit;

namespace Airlift.Tests;

public sealed class ReleaseHistoryTests
{
    internal static string ReleaseJson(string tag, bool preview = false, bool draft = false) => JsonSerializer.Serialize(new
    {
        tag_name = tag, html_url = $"https://github.com/{CoreTests.App.Repository}/releases/tag/{tag}",
        body = "## What’s new\n\nA **more thoughtful** writing experience.\n\n- Faster startup\n- Improved `keyboard` navigation\n\n[Full changelog](https://github.com/fezcode/Descry/compare/v1.0.0...v2.0.0)",
        published_at = "2026-09-14T10:00:00Z", prerelease = preview, draft,
        assets = new[] { new { id = 100, name = $"Descry-Setup-{tag.TrimStart('v')}.exe", size = 12582912, state = "uploaded",
            browser_download_url = $"https://github.com/{CoreTests.App.Repository}/releases/download/{tag}/Descry-Setup-{tag.TrimStart('v')}.exe", digest = "sha256:" + new string('a', 64) } }
    });
    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    [Fact]
    public async Task APausedGitHubCheckIsRecordedInsteadOfLookingLikeASuccess()
    {
        var store = new StateStore(CoreTests.TestDirectory());
        var apps = Catalog.Load();
        var tripping = apps[0];
        var paused = apps[1];
        // The second repository already has a good release saved from an earlier check.
        var key = paused.Id + ":stable";
        store.Put("releases", key, new ReleaseCache(
            new AppRelease("v1.0.0", "1.0.0", "https://github.com/a/b/releases/tag/v1.0.0", "", DateTimeOffset.UtcNow.AddDays(-1), false, []),
            "etag", DateTimeOffset.UtcNow.AddHours(-2), null));

        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString());
            return response;
        }));
        var github = new GitHubClient(http, store);

        var tripped = await github.GetReleaseAsync(tripping, false, true);
        Assert.Contains("rate limit", tripped.Error);

        // Every later repository is refused locally, without reaching the network.
        var result = await github.GetReleaseAsync(paused, false, true);
        Assert.Equal(1, calls);
        Assert.Contains("paused", result.Error);

        // The UI reads release state from the store, so a pause that never lands there
        // is indistinguishable from a successful check of stale data.
        var stored = store.Get<ReleaseCache>("releases", key)!;
        Assert.NotNull(stored.Error);
        Assert.Contains("paused", stored.Error);
        Assert.Equal("1.0.0", stored.Release!.Version); // The saved release is kept, not discarded.
    }

    [Fact]
    public async Task HistoryPaginatesRevalidatesAndPreservesOfflineCache()
    {
        var store = new StateStore(CoreTests.TestDirectory()); var app = CoreTests.App; var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            if (requests == 1)
            {
                Assert.EndsWith("per_page=20&page=1", request.RequestUri.Query);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + ReleaseJson("v2.0.0") + "]") };
                response.Headers.ETag = new EntityTagHeaderValue("\"history\"");
                // Pagination reads only the relation; the next request is constructed from the configured repo.
                response.Headers.TryAddWithoutValidation("Link", "<https://evil.example/steal>; rel=\"next\""); return response;
            }
            if (requests == 2)
            {
                Assert.EndsWith("per_page=20&page=2", request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = new StringContent("[" + ReleaseJson("v1.0.0") + "]") };
            }
            Assert.Equal("\"history\"", request.Headers.IfNoneMatch.Single().Tag);
            return new(requests == 3 ? HttpStatusCode.NotModified : HttpStatusCode.ServiceUnavailable);
        }));
        var client = new GitHubClient(http, store);
        var first = await client.GetHistoryAsync(app); Assert.True(first.HasMore);
        Assert.Equal("v2.0.0", (await client.GetHistoryAsync(app)).Releases.Single().Tag); Assert.Equal(1, requests);
        var second = await client.GetHistoryAsync(app, 2); Assert.False(second.HasMore); Assert.Equal("v1.0.0", second.Releases.Single().Tag);
        store.Put("release-history", app.Id + ":1", first with { CheckedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        var revalidated = await client.GetHistoryAsync(app, force: true); Assert.Null(revalidated.Error); Assert.Equal("v2.0.0", revalidated.Releases.Single().Tag);
        var oldDate = DateTimeOffset.UtcNow.AddDays(-1); store.Put("release-history", app.Id + ":1", revalidated with { CheckedAt = oldDate });
        var offline = await client.GetHistoryAsync(app, force: true); Assert.NotNull(offline.Error); Assert.Equal(oldDate, offline.CheckedAt); Assert.Equal("v2.0.0", offline.Releases.Single().Tag);
    }
    [Fact]
    public async Task HistoryShowsPreviewAndNamedReleasesButExcludesDraftsWithoutChangingInstallChannel()
    {
        var store = new StateStore(CoreTests.TestDirectory());
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[" + string.Join(",", ReleaseJson("nightly", true), ReleaseJson("v1.0.0"), ReleaseJson("v2.0.0", draft: true)) + "]") }));
        using var manager = new PackageManager(store, http);
        var page = await manager.GetReleaseHistoryAsync(CoreTests.App);
        Assert.Equal(2, page.Releases.Count); Assert.True(page.Releases.Single(r => r.Tag == "nightly").Prerelease);
        Assert.Null(manager.Release(CoreTests.App)); Assert.False(manager.Preferences(CoreTests.App).IncludePrerelease);
        using var json = JsonDocument.Parse(ReleaseJson("nightly"));
        Assert.Throws<InvalidDataException>(() => GitHubClient.ParseRelease(json.RootElement, CoreTests.App.Repository));
    }
    [Fact]
    public async Task HistoryRateLimitBackoffIsSharedWithLatestReleaseChecks()
    {
        var requests = 0; var store = new StateStore(CoreTests.TestDirectory());
        using var http = new HttpClient(new Handler(_ => { requests++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)); return response; }));
        var client = new GitHubClient(http, store);
        Assert.NotNull((await client.GetHistoryAsync(CoreTests.App)).Error);
        Assert.NotNull((await client.GetReleaseAsync(CoreTests.App, false, true)).Error);
        Assert.NotNull((await client.GetHistoryAsync(CoreTests.App, 2)).Error); Assert.Equal(1, requests);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetHistoryAsync(CoreTests.App, ct: cancel.Token));
    }
}
