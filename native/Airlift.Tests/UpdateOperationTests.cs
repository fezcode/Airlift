using Airlift.Core;
using Xunit;

namespace Airlift.Tests;

public sealed class UpdateOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedUpdatesReplaceStaleInventoryAndClearUpdateBadge(bool silent)
    {
        if (HostPlatform.Os != "windows" || HostPlatform.Arch != "x64") return;
        var data = CoreTests.ForgeFixture();
        var store = new StateStore(CoreTests.TestDirectory());
        var provider = new UpdatingProvider();
        using var http = new HttpClient(new ReleaseHistoryTests.Handler(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data) }));
        using var manager = new PackageManager(store, http, provider);
        store.Put("releases", CoreTests.App.Id + ":stable", new ReleaseCache(CoreTests.Release(data), null, DateTimeOffset.UtcNow, null));
        manager.RefreshInventory(); Assert.True(manager.HasUpdate(CoreTests.App));
        provider.BeforeCompletion = () =>
        {
            // A concurrent refresh cannot publish an old registry snapshot while Setup owns the lock.
            var reads = provider.Reads;
            Assert.False(manager.RefreshInventory()); Assert.Equal(reads, provider.Reads);
        };
        await manager.ExecuteAsync(CoreTests.App, "update", silentUpdate: silent);
        Assert.Equal(silent, provider.Silent);
        Assert.Equal("0.87.0", manager.Installed(CoreTests.App)!.Version);
        Assert.False(manager.HasUpdate(CoreTests.App));
        Assert.Equal("Succeeded", Assert.Single(store.All<Operation>("operations")).Status);
        // External installers are reconciled too, without waiting for a GitHub release check.
        provider.Current = provider.Current! with { Version = "0.88.0" };
        Assert.True(manager.RefreshInventory()); Assert.Equal("0.88.0", manager.Installed(CoreTests.App)!.Version);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("download")]
    [InlineData("uninstall")]
    public async Task SilentModeCannotBeUsedForOtherOperations(string action)
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()), provider: new UpdatingProvider());
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ExecuteAsync(CoreTests.App, action, silentUpdate: true));
        Assert.Empty(manager.Store.All<Operation>("operations"));
    }

    [Fact]
    public async Task AnUninstalledAppCannotBecomeAFreshSilentInstall()
    {
        var provider = new UpdatingProvider { Current = null };
        using var http = new HttpClient(new ReleaseHistoryTests.Handler(_ => throw new Exception("Must not download")));
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()), http, provider);
        await manager.ExecuteAsync(CoreTests.App, "update", silentUpdate: true);
        Assert.Equal("Failed", Assert.Single(manager.Store.All<Operation>("operations")).Status);
        Assert.Equal(0, provider.Installs);
    }

    internal sealed class UpdatingProvider : IPlatformProvider
    {
        public InstalledApp? Current = new(CoreTests.App.Id, "0.86.1", "custom folder", "fixture");
        public bool Silent;
        public int Installs, Reads;
        public Action? BeforeCompletion;
        public InstalledApp? FindInstalled(CatalogApp app) { Reads++; return app.Id == CoreTests.App.Id ? Current : null; }
        public Task<InstalledApp> InstallAsync(PackagePlan plan, string file, CancellationToken ct)
        {
            BeforeCompletion?.Invoke(); Installs++;
            Current = Current! with { Version = plan.Release.Version }; return Task.FromResult(Current);
        }
        public Task<InstalledApp> UpdateSilentlyAsync(PackagePlan plan, string file, CancellationToken ct) { Silent = true; return InstallAsync(plan, file, ct); }
        public Task UninstallAsync(CatalogApp app, InstalledApp installed, CancellationToken ct) => throw new NotSupportedException();
    }
}
