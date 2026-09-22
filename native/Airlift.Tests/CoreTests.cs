using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Airlift.Core;
using Xunit;

namespace Airlift.Tests;
public sealed class CoreTests
{
    internal static string TestDirectory() { var root = Path.Combine(Path.GetTempPath(), "airlift-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    internal static CatalogApp App => Catalog.Load().First();
    internal static AppRelease Release(byte[] data) => new("v0.87.0", "0.87.0", "https://github.com/fezcode/Descry/releases/tag/v0.87.0", "", DateTimeOffset.UtcNow, false,
        [new(99, "Descry-Setup-0.87.0.exe", data.Length, "https://github.com/fezcode/Descry/releases/download/v0.87.0/Descry-Setup-0.87.0.exe", Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant())]);
    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)] [InlineData("1.0.0", "1.0.0-rc.1", 1)] [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)] [InlineData("v1.2.3", "1.2.3+build.7", 0)] [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    public void VersionsUseSemanticOrdering(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(SemVersion.Compare(a, b)));
    [Fact] public void UpdateComparisonUsesNormalizedVersionStrings() => Assert.True(SemVersion.IsNewer(" v2.0.0 ", " v1.0.0 "));
    [Fact] public async Task FailedReleaseChecksAreRetriedBeforeTheNormalSixHourCacheExpires()
    {
        var store = new StateStore(TestDirectory());
        store.Put("releases", App.Id + ":stable", new ReleaseCache(null, null, DateTimeOffset.UtcNow.AddMinutes(-2), "offline"));
        var requests = 0;
        using var http = new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new StringContent(ReleaseHistoryTests.ReleaseJson("v2.0.0")) }; }));
        var result = await new GitHubClient(http, store).GetReleaseAsync(App, false, false);
        Assert.Equal(1, requests); Assert.Null(result.Error); Assert.Equal("2.0.0", result.Release!.Version);
    }
    [Fact] public void ResolverNeverGuessesTheFirstExecutable()
    {
        var release = Release([1]); release.Assets.Insert(0, new(1, "helper.exe", 3, "https://example.com", null));
        Assert.Equal("Descry-Setup-0.87.0.exe", PackageResolver.Resolve(App, release, "windows", "x64").Asset.Name);
        Assert.Throws<NotSupportedException>(() => PackageResolver.Resolve(App, release, "windows", "arm64"));
        Assert.Throws<NotSupportedException>(() => PackageResolver.Resolve(App, release, "linux", "x64"));
        release.Assets.Add(release.Assets[1]); Assert.Throws<NotSupportedException>(() => PackageResolver.Resolve(App, release, "windows", "x64"));
    }
    [Fact] public void ResolverFollowsTheProjectWhenTheAppNameIsNoFileName()
    {
        var app = Catalog.Load().Single(a => a.Id == "io.fezcode.sirwordalot"); Assert.Contains(' ', app.Name);
        var release = new AppRelease("v0.1.1", "0.1.1", "https://github.com/fezcode/SirWordALot/releases/tag/v0.1.1", "", DateTimeOffset.UtcNow, false,
            [new(1, "SirWordALot-Setup-0.1.1.exe", 4, "https://github.com/fezcode/SirWordALot/releases/download/v0.1.1/SirWordALot-Setup-0.1.1.exe", null)]);
        Assert.Equal("SirWordALot-Setup-0.1.1.exe", PackageResolver.Resolve(app, release, "windows", "x64").Asset.Name);
    }
    [Theory] [InlineData("https://evil.example/app.exe")] [InlineData("https://github.com/other/App/releases/download/v1/x.exe")] [InlineData("http://github.com/fezcode/Descry/releases/download/v1/x.exe")]
    public void RejectsUntrustedAssetUrls(string url) => Assert.Throws<InvalidDataException>(() => GitHubClient.ValidateAssetUrl(url, App.Repository));
    [Fact] public void StorePersistsPinsAndRecoversInterruptedOperations()
    {
        var directory = TestDirectory(); var store = new StateStore(directory); store.Put("preferences", App.Id, new AppPreferences(true, true));
        store.Put("operations", "x", new Operation("x", App.Id, App.Name, "install", "1.0.0", "Installing", 50, "", DateTimeOffset.UtcNow));
        var reopened = new StateStore(directory); Assert.True(reopened.Get<AppPreferences>("preferences", App.Id)!.Pinned);
        using (var lease = store.AcquireOperationLock()) Assert.Throws<IOException>(reopened.RecoverInterruptedOperations);
        reopened.RecoverInterruptedOperations(); Assert.Equal("Interrupted", reopened.Get<Operation>("operations", "x")!.Status);
    }
    [Fact] public void ForgeInspectorValidatesIdentityArchitectureAndPayload()
    {
        var data = ForgeFixture(); var file = Path.Combine(TestDirectory(), "Setup.exe"); File.WriteAllBytes(file, data);
        var plan = PackageResolver.Resolve(App, Release(data), "windows", "x64"); Assert.Equal(App.Id, ForgeInspector.Verify(file, plan).Id);
        Assert.Throws<InvalidDataException>(() => ForgeInspector.Verify(file, plan with { App = App with { Id = "other.app" } }));
        data[140] ^= 1; File.WriteAllBytes(file, data); Assert.Throws<InvalidDataException>(() => ForgeInspector.Inspect(file));
    }
    [Fact] public async Task DownloaderRestartsIfRangeIsIgnoredAndRejectsHashMismatch()
    {
        var data = Encoding.UTF8.GetBytes("this is a complete payload"); var release = Release(data); var plan = PackageResolver.Resolve(App, release, "windows", "x64");
        var store = new StateStore(TestDirectory()); var dir = Path.Combine(store.Root, "downloads", App.Id, "99"); Directory.CreateDirectory(dir);
        var partial = Path.Combine(dir, plan.Asset.Name + ".part"); await File.WriteAllBytesAsync(partial, data[..5]);
        await File.WriteAllTextAsync(partial + ".identity", $"99|{data.Length}|{plan.Asset.Sha256}|{plan.Asset.Url}");
        using var http = new HttpClient(new Handler(request => { Assert.Equal(5, request.Headers.Range!.Ranges.Single().From); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; }));
        var file = await new Downloader(http, store).DownloadAsync(plan, (_, _) => { }, default); Assert.Equal(data, await File.ReadAllBytesAsync(file));
        File.Delete(file);
        using var bad = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[data.Length]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new Downloader(bad, store).DownloadAsync(plan, (_, _) => { }, default)); Assert.False(File.Exists(file));
    }
    [Fact] public async Task DownloaderRejectsRedirectToUntrustedHost()
    {
        using var http = new HttpClient(new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("https://attacker.example/file"); return r; }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new Downloader(http, new StateStore(TestDirectory())).DownloadAsync(PackageResolver.Resolve(App, Release([1]), "windows", "x64"), (_, _) => { }, default));
    }
    [Fact] public async Task GitHubUsesCachedMetadataWithoutNetworkAndRevalidatesWithEtag()
    {
        var store = new StateStore(TestDirectory()); var release = Release([1]); var requests = 0;
        store.Put("releases", App.Id + ":stable", new ReleaseCache(release, "\"abc\"", DateTimeOffset.UtcNow, null));
        using var http = new HttpClient(new Handler(request => { requests++; Assert.Equal("\"abc\"", request.Headers.IfNoneMatch.Single().ToString()); return new(HttpStatusCode.NotModified); }));
        var client = new GitHubClient(http, store); Assert.Equal(release.Tag, (await client.GetReleaseAsync(App, false, false)).Release!.Tag); Assert.Equal(0, requests);
        store.Put("releases", App.Id + ":stable", new ReleaseCache(release, "\"abc\"", DateTimeOffset.UtcNow.AddHours(-7), null));
        Assert.Equal(release.Tag, (await client.GetReleaseAsync(App, false, true)).Release!.Tag); Assert.Equal(1, requests);
    }
    [Theory] [InlineData("../outside")] [InlineData("/absolute")] [InlineData("folder/../../outside")] [InlineData("C:\\outside")]
    public void ArchivePathsStayWithinOwnedDirectory(string name) => Assert.Throws<InvalidDataException>(() => PathSafety.Child(TestDirectory(), name));
    [Fact] public async Task PortableArchiveRejectsSymlinks()
    {
        var root = TestDirectory(); var file = Path.Combine(root, "archive.tar.gz");
        await using (var output = File.Create(file)) await using (var gzip = new GZipStream(output, CompressionMode.Compress))
        using (var writer = new TarWriter(gzip)) writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "typewriter") { LinkName = "/etc/passwd" });
        var destination = Path.Combine(root, "out"); Directory.CreateDirectory(destination);
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableProvider.ExtractPortableAsync(file, destination, default));
    }
    [Fact] public void ImportRejectsUnknownAndDuplicateApps()
    {
        Assert.Throws<InvalidDataException>(() => SetupDocument.Parse("{\"schemaVersion\":1,\"apps\":[{\"appId\":\"evil.app\",\"preferences\":{}}]}", Catalog.Load()));
        var entries = new List<SetupEntry> { new(App.Id, null, new()), new(App.Id, null, new()) };
        Assert.Throws<InvalidDataException>(() => SetupDocument.Parse(JsonSerializer.Serialize(new SetupDocument(1, entries), JsonData.Options), Catalog.Load()));
    }
    [Fact] public async Task OperationDoesNotRecordSuccessWhenInstallerFails()
    {
        if (HostPlatform.Os != "windows" || HostPlatform.Arch != "x64") return;
        var data = ForgeFixture(); var store = new StateStore(TestDirectory()); store.Put("releases", App.Id + ":stable", new ReleaseCache(Release(data), null, DateTimeOffset.UtcNow, null));
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }));
        using var manager = new PackageManager(store, http, new FailingProvider());
        await manager.ExecuteAsync(App, "install"); Assert.Equal("Failed", store.All<Operation>("operations").Single().Status); Assert.Null(manager.Installed(App));
    }
    [Fact] public async Task MissingDigestRequiresExplicitInstallConsent()
    {
        if (HostPlatform.Os != "windows" || HostPlatform.Arch != "x64") return;
        var store = new StateStore(TestDirectory()); var release = Release([1]); release.Assets[0] = release.Assets[0] with { Sha256 = null };
        store.Put("releases", App.Id + ":stable", new ReleaseCache(release, null, DateTimeOffset.UtcNow, null));
        using var http = new HttpClient(new Handler(_ => throw new Exception("Should not download"))); using var manager = new PackageManager(store, http, new FailingProvider());
        await manager.ExecuteAsync(App, "install"); Assert.Contains("explicitly allow", store.All<Operation>("operations").Single().Message);
    }
    internal static byte[] ForgeFixture(string appId = "com.fezcode.descry", string version = "0.87.0")
    {
        using var payload = new MemoryStream();
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(JsonSerializer.Serialize(new { app = new { id = appId, version }, install = new { default_dir = "${PROGRAMFILES}/Fixture" } }));
        var bytes = payload.ToArray(); var output = new byte[128 + bytes.Length + 72]; output[0] = (byte)'M'; output[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(60), 64); "PE\0\0"u8.CopyTo(output.AsSpan(64)); BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(68), 0x8664); bytes.CopyTo(output, 128);
        var trailer = output.AsSpan(output.Length - 72); "FORGE\0\0\0"u8.CopyTo(trailer); BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], 1); BinaryPrimitives.WriteUInt64LittleEndian(trailer[16..], 128); BinaryPrimitives.WriteUInt64LittleEndian(trailer[24..], (ulong)bytes.Length); SHA256.HashData(bytes).CopyTo(trailer[32..]); "\0\0\0FORGE"u8.CopyTo(trailer[64..]); return output;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request)); }
    private sealed class FailingProvider : IPlatformProvider
    {
        public InstalledApp? FindInstalled(CatalogApp app) => null;
        public Task<InstalledApp> InstallAsync(PackagePlan plan, string file, CancellationToken ct) => throw new IOException("Installer failed");
        public Task UninstallAsync(CatalogApp app, InstalledApp installed, CancellationToken ct) => throw new IOException();
    }
}
