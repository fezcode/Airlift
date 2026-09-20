using Airlift.Core;
using Xunit;

namespace Airlift.Tests;

public sealed class InstallerCacheTests
{
    private readonly StateStore _store = new(CoreTests.TestDirectory());
    private string Put(long assetId, string version, string? embeddedId = null, string? name = null)
    {
        var dir = Path.Combine(_store.Root, "downloads", CoreTests.App.Id, assetId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name ?? $"Setup-{version}.exe");
        File.WriteAllBytes(path, CoreTests.ForgeFixture(embeddedId ?? CoreTests.App.Id, version));
        return path;
    }
    private Dictionary<string, string> Versions(string version) => new() { [CoreTests.App.Id] = version };

    [Fact]
    public void OnlyOlderVerifiedInstallersAreRemoved()
    {
        var old = Put(1, "1.9.0"); var current = Put(2, "1.10.0"); var newer = Put(3, "2.0.0");
        var mismatch = Put(4, "1.0.0", "other.app"); var partial = Put(5, "1.0.0", name: "Setup.exe.part");
        var unknown = Put(6, "1.0.0"); File.WriteAllText(unknown, "not a Forge installer");
        var cache = new InstallerCache(_store);
        var preview = cache.FindOld(Versions("1.10.0"));
        Assert.Equal(old, Assert.Single(preview).Path);
        var result = cache.ClearOld(Versions("1.10.0"), preview.Select(i => i.Path).ToHashSet());
        Assert.Equal(1, result.Removed); Assert.True(result.Bytes > 0); Assert.False(File.Exists(old));
        foreach (var path in new[] { current, newer, mismatch, partial, unknown }) Assert.True(File.Exists(path));
    }

    [Fact]
    public void UninstalledAppsAndActiveOperationsAreRetained()
    {
        var old = Put(1, "1.0.0"); var cache = new InstallerCache(_store);
        Assert.Empty(cache.FindOld(new Dictionary<string, string>()));
        _store.Put("operations", "active", new Operation("active", CoreTests.App.Id, "Test", "update", "2.0.0", "Installing", 100, "", DateTimeOffset.UtcNow, old));
        Assert.Empty(cache.FindOld(Versions("2.0.0")));
        Assert.Equal(0, cache.ClearOld(Versions("2.0.0"), new HashSet<string> { old }).Removed);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void CleanupRechecksVersionsAndLimitsDeletionToPreview()
    {
        var old = Put(1, "1.0.0"); var cache = new InstallerCache(_store);
        var preview = cache.FindOld(Versions("2.0.0")).Select(i => i.Path).ToHashSet();
        Assert.Equal(0, cache.ClearOld(Versions("1.0.0"), preview).Removed);
        var later = Put(2, "0.9.0");
        Assert.Equal(1, cache.ClearOld(Versions("2.0.0"), preview).Removed);
        Assert.True(File.Exists(later));
    }

    [Fact]
    public void ManagerIncludesAirliftAndSharesTheOperationLock()
    {
        var dir = Path.Combine(_store.Root, "downloads", SelfUpdater.App.Id, "99"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "old.exe"); File.WriteAllBytes(file, CoreTests.ForgeFixture(SelfUpdater.App.Id, "0.0.1"));
        using var manager = new PackageManager(_store, provider: new EmptyProvider());
        Assert.Equal(file, Assert.Single(manager.FindOldInstallers()).Path);
        using var lease = _store.AcquireOperationLock();
        Assert.Throws<IOException>(() => manager.FindOldInstallers());
        Assert.Throws<IOException>(() => manager.ClearOldInstallers([]));
    }

    [Fact]
    public void LockedInstallersAreKept()
    {
        var file = Put(1, "1.0.0");
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Empty(new InstallerCache(_store).FindOld(Versions("2.0.0")));
        Assert.True(File.Exists(file));
    }

    private sealed class EmptyProvider : IPlatformProvider
    {
        public InstalledApp? FindInstalled(CatalogApp app) => null;
        public Task<InstalledApp> InstallAsync(PackagePlan plan, string file, CancellationToken ct) => throw new NotSupportedException();
        public Task UninstallAsync(CatalogApp app, InstalledApp installed, CancellationToken ct) => throw new NotSupportedException();
    }
}
