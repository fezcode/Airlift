using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Airlift.Core;
using Xunit;

namespace Airlift.Tests;

public sealed class ReleaseHistoryTests
{
    internal static string ReleaseJson(string tag, bool preview = false, bool draft = false, string? repository = null, string? published = null)
    {
        repository ??= CoreTests.App.Repository;
        var name = repository.Split('/')[1];
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, html_url = $"https://github.com/{repository}/releases/tag/{tag}",
            body = "## What’s new\n\nA **more thoughtful** writing experience.\n\n- Faster startup\n- Improved `keyboard` navigation\n\n[Full changelog](https://github.com/fezcode/Descry/compare/v1.0.0...v2.0.0)",
            published_at = published ?? "2026-09-14T10:00:00Z", prerelease = preview, draft,
            assets = new[] { new { id = 100, name = $"{name}-Setup-{tag.TrimStart('v')}.exe", size = 12582912, state = "uploaded",
                browser_download_url = $"https://github.com/{repository}/releases/download/{tag}/{name}-Setup-{tag.TrimStart('v')}.exe", digest = "sha256:" + new string('a', 64) } }
        });
    }
    /// <summary>A release.json as the Release index workflow writes it.</summary>
    internal static string Index(params (string Repository, bool Available, string[] Releases)[] apps) =>
        "{\"schema\":1,\"generated\":\"2026-11-27T00:00:00Z\",\"apps\":{" + string.Join(",", apps.Select(a =>
            $"{JsonSerializer.Serialize(a.Repository)}:{{\"available\":{(a.Available ? "true" : "false")},\"releases\":[{string.Join(",", a.Releases)}]}}")) + "}}";
    /// <summary>An index listing only the first catalog app.</summary>
    internal static string IndexJson(params string[] releases) => Index((CoreTests.App.Repository, true, releases));
    internal static HttpResponseMessage IndexResponse(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        if (etag != null) response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }
    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    [Fact]
    public async Task OneRequestServesEveryAppAndOnlyTheIndexIsEverAsked()
    {
        var store = new StateStore(CoreTests.TestDirectory()); var apps = Catalog.Load(); var requests = 0;
        var second = apps[1];
        using var http = new HttpClient(new Handler(request =>
        {
            requests++; Assert.Equal(GitHubClient.IndexUrl, request.RequestUri!.AbsoluteUri);
            return IndexResponse(Index((CoreTests.App.Repository, true, [ReleaseJson("v2.0.0")]),
                (second.Repository, true, [ReleaseJson("v0.4.0", repository: second.Repository)])));
        }));
        var github = new GitHubClient(http, store);
        foreach (var app in apps) await github.GetReleaseAsync(app, false, true);
        await github.GetReleaseAsync(SelfUpdater.App, false, true);
        await github.GetHistoryAsync(second);
        Assert.Equal(1, requests);
        Assert.Equal("2.0.0", store.Get<ReleaseCache>("releases", apps[0].Id + ":stable")!.Release!.Version);
        Assert.Equal("0.4.0", store.Get<ReleaseCache>("releases", second.Id + ":stable")!.Release!.Version);
    }

    [Fact]
    public async Task PrivateOrUnlistedRepositoriesAreUnavailableWithoutAnError()
    {
        var store = new StateStore(CoreTests.TestDirectory()); var apps = Catalog.Load();
        var hidden = apps[1]; var unlisted = apps[2];
        using var http = new HttpClient(new Handler(_ => IndexResponse(Index((CoreTests.App.Repository, true, [ReleaseJson("v2.0.0")]), (hidden.Repository, false, [])))));
        var github = new GitHubClient(http, store);
        foreach (var app in new[] { hidden, unlisted })
        {
            var result = await github.GetReleaseAsync(app, false, true);
            Assert.True(result.Unavailable); Assert.Null(result.Error); Assert.Null(result.Release);
        }
        using var manager = new PackageManager(store, http);
        Assert.DoesNotContain(hidden, manager.VisibleApps); Assert.Contains(apps[0], manager.VisibleApps);
        Assert.Equal(PackageManager.NotPublic, Assert.Throws<InvalidOperationException>(() => manager.Plan(hidden)).Message);
        store.Put("installed", hidden.Id, new InstalledApp(hidden.Id, "1.0.0", "fixture", "fixture"));
        Assert.Contains(hidden, manager.VisibleApps); // An installed copy stays manageable.
    }

    [Fact]
    public async Task AFailedIndexKeepsSavedReleasesAndRecordsTheError()
    {
        var store = new StateStore(CoreTests.TestDirectory()); var app = Catalog.Load()[1];
        var key = app.Id + ":stable";
        store.Put("releases", key, new ReleaseCache(
            new AppRelease("v1.0.0", "1.0.0", "https://github.com/a/b/releases/tag/v1.0.0", "", DateTimeOffset.UtcNow.AddDays(-1), false, []),
            null, DateTimeOffset.UtcNow.AddHours(-7), null));
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }));
        var github = new GitHubClient(http, store);
        var result = await github.GetReleaseAsync(app, false, true);
        Assert.Contains("HTTP 503", result.Error);
        // The UI reads release state from the store, so the failure has to land there too.
        var stored = store.Get<ReleaseCache>("releases", key)!;
        Assert.Contains("HTTP 503", stored.Error);
        Assert.Equal("1.0.0", stored.Release!.Version); // The saved release is kept, not discarded.
        // A failed index is retried after a minute, not by every app in the same check.
        await github.GetReleaseAsync(Catalog.Load()[0], false, true); await github.GetHistoryAsync(app, force: true);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ASavedIndexIsUsedWhenTheNextOneCannotBeFetched()
    {
        var store = new StateStore(CoreTests.TestDirectory()); var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            if (requests == 1) return IndexResponse(IndexJson(ReleaseJson("v2.0.0")), "\"one\"");
            Assert.Equal("\"one\"", request.Headers.IfNoneMatch.Single().Tag);
            return new(HttpStatusCode.ServiceUnavailable);
        }));
        var github = new GitHubClient(http, store);
        Assert.Null((await github.GetReleaseAsync(CoreTests.App, false, true)).Error);
        var index = store.Get<ReleaseIndexCache>("release-index", "main")!;
        store.Put("release-index", "main", index with { CheckedAt = DateTimeOffset.UtcNow.AddHours(-7) });
        store.Put("releases", CoreTests.App.Id + ":stable", store.Get<ReleaseCache>("releases", CoreTests.App.Id + ":stable")! with { CheckedAt = DateTimeOffset.UtcNow.AddHours(-7) });
        var stale = await github.GetReleaseAsync(CoreTests.App, false, true);
        Assert.Equal(2, requests); Assert.Equal("2.0.0", stale.Release!.Version); Assert.Contains("saved release data", stale.Error);
    }

    [Fact]
    public async Task HistoryListsEveryIndexedReleaseNewestFirstOnOnePage()
    {
        var store = new StateStore(CoreTests.TestDirectory()); var app = CoreTests.App; var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            return IndexResponse(IndexJson(ReleaseJson("v1.0.0", published: "2026-01-01T00:00:00Z"), ReleaseJson("v2.0.0", published: "2026-06-01T00:00:00Z")));
        }));
        var client = new GitHubClient(http, store);
        var first = await client.GetHistoryAsync(app);
        Assert.Equal(["v2.0.0", "v1.0.0"], first.Releases.Select(r => r.Tag)); Assert.False(first.HasMore); Assert.Null(first.Error);
        Assert.Empty((await client.GetHistoryAsync(app, 2)).Releases);
        await client.GetHistoryAsync(app); Assert.Equal(1, requests);
    }

    [Fact]
    public async Task HistoryShowsPreviewAndNamedReleasesButExcludesDraftsWithoutChangingInstallChannel()
    {
        var store = new StateStore(CoreTests.TestDirectory());
        using var http = new HttpClient(new Handler(_ => IndexResponse(IndexJson(ReleaseJson("nightly", true), ReleaseJson("v1.0.0"), ReleaseJson("v2.0.0", draft: true)))));
        using var manager = new PackageManager(store, http);
        var page = await manager.GetReleaseHistoryAsync(CoreTests.App);
        Assert.Equal(2, page.Releases.Count); Assert.True(page.Releases.Single(r => r.Tag == "nightly").Prerelease);
        Assert.Null(manager.Release(CoreTests.App)); Assert.False(manager.Preferences(CoreTests.App).IncludePrerelease);
        using var json = JsonDocument.Parse(ReleaseJson("nightly"));
        Assert.Throws<InvalidDataException>(() => GitHubClient.ParseRelease(json.RootElement, CoreTests.App.Repository));
    }

    [Fact]
    public async Task ChannelsPickTheHighestVersionAndSkipReleasesThatFailValidation()
    {
        var store = new StateStore(CoreTests.TestDirectory());
        var foreign = ReleaseJson("v9.0.0").Replace($"github.com/{CoreTests.App.Repository}/releases/download", "github.com/other/App/releases/download");
        using var http = new HttpClient(new Handler(_ => IndexResponse(IndexJson(ReleaseJson("v1.10.0"), ReleaseJson("v1.9.0"), ReleaseJson("v2.0.0-rc.1", true), ReleaseJson("nightly", true), foreign))));
        var github = new GitHubClient(http, store);
        Assert.Equal("1.10.0", (await github.GetReleaseAsync(CoreTests.App, false, true)).Release!.Version);
        Assert.Equal("2.0.0-rc.1", (await github.GetReleaseAsync(CoreTests.App, true, true)).Release!.Version);
    }

    [Fact]
    public async Task AnIndexInAnUnknownFormatIsRejectedAndCancellationIsHonoured()
    {
        var store = new StateStore(CoreTests.TestDirectory());
        using var http = new HttpClient(new Handler(_ => IndexResponse("{\"schema\":2,\"apps\":{}}")));
        var client = new GitHubClient(http, store);
        Assert.Contains("format", (await client.GetReleaseAsync(CoreTests.App, false, true)).Error);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetHistoryAsync(CoreTests.App, ct: cancel.Token));
    }
}
