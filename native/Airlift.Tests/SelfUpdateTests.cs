using System.Net;
using System.Security.Cryptography;
using Airlift.Core;
using Xunit;

namespace Airlift.Tests;

public sealed class SelfUpdateTests
{
    [Fact]
    public void UnsupportedSilentInstallerIsRejectedBeforeAirliftCloses()
    {
        if (HostPlatform.Os != "windows" || HostPlatform.Arch != "x64") return;
        var data = CoreTests.ForgeFixture(SelfUpdater.App.Id, "999.0.0");
        var store = new StateStore(CoreTests.TestDirectory());
        var file = Path.Combine(store.Root, "Setup.exe"); File.WriteAllBytes(file, data);
        var prepared = new PreparedSelfUpdate(SelfUpdater.Plan(Release(data), "windows", "x64"), file, Silent: true);
        Assert.False(ForgeInspector.Inspect(file).SupportsSilentHandoff);
        var error = Assert.Throws<InvalidOperationException>(() => new SelfUpdateLauncher(store).Start(prepared));
        Assert.Contains("does not support silent", error.Message);
    }

    internal static AppRelease Release(byte[] data, bool digest = true) => new("v999.0.0", "999.0.0",
        $"https://github.com/{SelfUpdater.Repository}/releases/tag/v999.0.0", "## Improvements\n\n**Faster** startup.", DateTimeOffset.UtcNow, false,
        [new(100, "Airlift-Setup-999.0.0.exe", data.Length, $"https://github.com/{SelfUpdater.Repository}/releases/download/v999.0.0/Airlift-Setup-999.0.0.exe", digest ? Convert.ToHexString(SHA256.HashData(data)) : null)]);

    [Fact]
    public void UpdateComparesRunningVersionAndRejectsDowngradesPreviewsAndWrongPlatforms()
    {
        var release = Release([1]);
        Assert.Throws<InvalidOperationException>(() => SelfUpdater.Plan(release with { Version = AppVersion.Current }, "windows", "x64"));
        Assert.Throws<InvalidOperationException>(() => SelfUpdater.Plan(release with { Prerelease = true }, "windows", "x64"));
        Assert.Throws<NotSupportedException>(() => SelfUpdater.Plan(release, "linux", "x64"));
        Assert.Throws<NotSupportedException>(() => SelfUpdater.Plan(release, "windows", "arm64"));
        var plan = SelfUpdater.Plan(release, "windows", "x64");
        Assert.Equal(SelfUpdater.App.Id, plan.App.Id);
        Assert.Equal("Airlift-Setup-999.0.0.exe", plan.Asset.Name);
    }

    [Fact]
    public async Task PreparationVerifiesIdentityAndRequiresMissingDigestConsent()
    {
        if (HostPlatform.Os != "windows" || HostPlatform.Arch != "x64") return;
        var data = CoreTests.ForgeFixture(SelfUpdater.App.Id, "999.0.0"); var requests = 0;
        using var http = new HttpClient(new ReleaseHistoryTests.Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; }));
        var store = new StateStore(CoreTests.TestDirectory());
        var updater = new SelfUpdater(store, new GitHubClient(http, store), new Downloader(http, store));
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.PrepareAsync(Release(data, false), false, (_, _) => { }));
        Assert.Equal(0, requests);
        var prepared = await updater.PrepareAsync(Release(data), false, (_, _) => { });
        Assert.Equal(SelfUpdater.App.Id, ForgeInspector.Inspect(prepared.File).Id);
        Assert.Null(store.Get<InstalledApp>("installed", SelfUpdater.App.Id)); // Preparing never claims Setup finished.
        data = CoreTests.ForgeFixture(); // Correct download hash but another application's installer.
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.PrepareAsync(Release(data), false, (_, _) => { }));
    }
}
