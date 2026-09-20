using System.Collections.Concurrent;

namespace Airlift.Core;

public sealed class PackageManager : IDisposable
{
    public StateStore Store { get; }
    public IReadOnlyList<CatalogApp> Apps { get; }
    public SelfUpdater SelfUpdate { get; }
    public event Action? Changed;
    private readonly HttpClient _http;
    private readonly GitHubClient _github;
    private readonly Downloader _downloads;
    private readonly IPlatformProvider _provider;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellation = new();
    private readonly ConcurrentDictionary<string, byte> _queuedApps = new();
    public PackageManager(StateStore? store = null, HttpClient? http = null, IPlatformProvider? provider = null)
    {
        Store = store ?? new(); Apps = Catalog.Load(); _http = http ?? GitHubClient.CreateHttpClient();
        _github = new(_http, Store); _downloads = new(_http, Store);
        SelfUpdate = new(Store, _github, _downloads);
        _provider = provider ?? (OperatingSystem.IsWindows() ? new WindowsForgeProvider(Store, new ProcessRunner()) : new PortableProvider(Store));
        try { Store.RecoverInterruptedOperations(); } catch (IOException) { /* Another process owns an operation. Leave its journal untouched. */ }
    }
    public AppPreferences Preferences(CatalogApp app) => Store.Get<AppPreferences>("preferences", app.Id) ?? new();
    public void SetPreferences(CatalogApp app, AppPreferences preferences) { Store.Put("preferences", app.Id, preferences); Changed?.Invoke(); }
    public ReleaseCache? Release(CatalogApp app) => Store.Get<ReleaseCache>("releases", app.Id + (Preferences(app).IncludePrerelease ? ":preview" : ":stable"));
    public ReleaseHistoryPage? CachedHistory(CatalogApp app, int page = 1) => Store.Get<ReleaseHistoryPage>("release-history", $"{app.Id}:{page}");
    public Task<ReleaseHistoryPage> GetReleaseHistoryAsync(CatalogApp app, int page = 1, bool force = false, CancellationToken ct = default) => _github.GetHistoryAsync(app, page, force, ct);
    public InstalledApp? Installed(CatalogApp app) => Store.Get<InstalledApp>("installed", app.Id);
    public AppSettings Settings => Store.Get<AppSettings>("settings", "main") ?? new();
    public void SetSettings(AppSettings settings) { Store.Put("settings", "main", settings); Changed?.Invoke(); }
    public bool IsBusy(CatalogApp app) => _queuedApps.ContainsKey(app.Id);
    public bool HasActiveOperations => !_queuedApps.IsEmpty;
    public bool HasUpdate(CatalogApp app) => !Preferences(app).Pinned && Installed(app) is { } installed && Release(app)?.Release is { } release && SemVersion.IsNewer(release.Version, installed.Version);
    public PackagePlan Plan(CatalogApp app) => PackageResolver.Resolve(app, Release(app)?.Release ?? throw new InvalidOperationException("Check GitHub releases first."), HostPlatform.Os, HostPlatform.Arch);
    public bool RefreshInventory()
    {
        // Reconciliation and installer completion share the same cross-process
        // lock. An older snapshot must never overwrite a just-completed update.
        FileStream lease;
        try { lease = Store.AcquireOperationLock(); } catch (IOException) { return false; }
        using (lease)
        {
            var changed = false;
            foreach (var app in Apps)
            {
                var installed = _provider.FindInstalled(app);
                if (installed == Installed(app)) continue;
                if (installed != null) Store.Put("installed", app.Id, installed); else Store.Remove("installed", app.Id);
                changed = true;
            }
            if (changed) Changed?.Invoke();
            return changed;
        }
    }
    public async Task CheckReleasesAsync(bool force = false, CancellationToken ct = default)
    {
        foreach (var app in Apps) { ct.ThrowIfCancellationRequested(); await _github.GetReleaseAsync(app, Preferences(app).IncludePrerelease, force, ct); Changed?.Invoke(); }
    }
    public async Task CheckReleaseAsync(CatalogApp app, CancellationToken ct = default)
    {
        await _github.GetReleaseAsync(app, Preferences(app).IncludePrerelease, false, ct); Changed?.Invoke();
    }
    public void Pause(string id) { if (_cancellation.TryGetValue(id, out var cts) && Store.Get<Operation>("operations", id)?.CanCancel == true) cts.Cancel(); }
    public void ClearHistory() { foreach (var op in Store.All<Operation>("operations").Where(o => !o.Active)) Store.Remove("operations", op.Id); Changed?.Invoke(); }
    private Dictionary<string, string> CacheInstalledVersions()
    {
        var versions = new Dictionary<string, string>();
        foreach (var app in Apps.Append(SelfUpdater.App))
            if (_provider.FindInstalled(app) is { } installed) versions[app.Id] = installed.Version;
        if (!versions.TryGetValue(SelfUpdater.App.Id, out var registered) || SemVersion.IsNewer(AppVersion.Current, registered))
            versions[SelfUpdater.App.Id] = AppVersion.Current;
        return versions;
    }
    public IReadOnlyList<CachedInstaller> FindOldInstallers()
    {
        using var lease = Store.AcquireOperationLock();
        return new InstallerCache(Store).FindOld(CacheInstalledVersions());
    }
    public CacheCleanupResult ClearOldInstallers(IReadOnlyList<CachedInstaller> preview)
    {
        using var lease = Store.AcquireOperationLock();
        return new InstallerCache(Store).ClearOld(CacheInstalledVersions(), preview.Select(i => i.Path).ToHashSet());
    }
    public async Task ExecuteAsync(CatalogApp app, string action, bool allowUnverified = false, CancellationToken ct = default, bool silentUpdate = false)
    {
        if (action is not ("install" or "update" or "download" or "uninstall")) throw new ArgumentException("Unsupported action.");
        if (silentUpdate && action != "update") throw new ArgumentException("Silent mode is only available for updates.");
        if (!_queuedApps.TryAdd(app.Id, 0)) throw new InvalidOperationException("This app already has an operation queued.");
        var op = new Operation(Guid.NewGuid().ToString("N"), app.Id, app.Name, action, "", "Queued", 0, "Waiting for the package queue", DateTimeOffset.UtcNow);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); _cancellation[op.Id] = cancel;
        void Report(string status, double progress, string message, string? file = null)
        {
            op = op with { Status = status, Progress = progress, Message = message, File = file ?? op.File }; Store.Put("operations", op.Id, op); Changed?.Invoke();
        }
        var entered = false;
        try
        {
            Report("Queued", 0, "Waiting for the package queue"); await _operations.WaitAsync(cancel.Token); entered = true;
            using var lease = Store.AcquireOperationLock();
            cancel.Token.ThrowIfCancellationRequested();
            if (action == "uninstall")
            {
                var installed = _provider.FindInstalled(app) ?? throw new InvalidOperationException("This app is not installed.");
                Report("Uninstalling", 0, "Removing the application; keeping personal settings and data");
                await _provider.UninstallAsync(app, installed, CancellationToken.None);
                Store.Remove("installed", app.Id); Report("Succeeded", 100, "Application removed; settings preserved"); return;
            }
            if (action == "update" && Preferences(app).Pinned) throw new InvalidOperationException("Unpin this version before updating.");
            if (action == "update" && _provider.FindInstalled(app) == null) throw new InvalidOperationException("This app is no longer installed. Use Install to open its setup wizard.");
            var cache = await _github.GetReleaseAsync(app, Preferences(app).IncludePrerelease, false, cancel.Token);
            if (cache.Release == null) throw new InvalidOperationException(cache.Error ?? "No published release.");
            var plan = PackageResolver.Resolve(app, cache.Release, HostPlatform.Os, HostPlatform.Arch); op = op with { Version = plan.Release.Version };
            if (silentUpdate && (plan.Os != "windows" || plan.Format != "forge-exe")) throw new NotSupportedException("Silent updates require a Windows Forge installer.");
            if (action != "download" && plan.Asset.Sha256 == null && !allowUnverified) throw new InvalidOperationException("GitHub did not publish a SHA-256 digest. Review and explicitly allow this unverified download before installation.");
            if (action != "download" && _provider.FindInstalled(app) is { } prior && !SemVersion.IsNewer(plan.Release.Version, prior.Version)) throw new InvalidOperationException("The same or a newer version is already installed.");
            Report("Downloading", 0, "Connecting to GitHub");
            var file = await _downloads.DownloadAsync(plan, (p, m) => Report("Downloading", p, m), cancel.Token);
            Report("Verifying", 100, "Validating the package", file); cancel.Token.ThrowIfCancellationRequested();
            if (plan.Format == "forge-exe") await Task.Run(() => ForgeInspector.Verify(file, plan), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (action == "download") { Report("Succeeded", 100, plan.Asset.Sha256 == null ? "Downloaded; no publisher digest available" : "Download complete · SHA-256 verified", file); return; }
            Report("Installing", 100, silentUpdate ? "Updating silently in the existing installation folder…" : plan.Format == "forge-exe" ? "Complete the Forge setup window. Airlift is waiting for its result." : "Installing the managed application", file);
            var result = silentUpdate
                ? await _provider.UpdateSilentlyAsync(plan, file, CancellationToken.None)
                : await _provider.InstallAsync(plan, file, CancellationToken.None);
            Report("Reconciling", 100, "Verifying the installed version"); Store.Put("installed", app.Id, result);
            Report("Succeeded", 100, $"{app.Name} {result.Version} installed");
        }
        catch (OperationCanceledException) { Report("Paused", op.Progress, "Paused. Retry resumes the partial download when supported."); }
        catch (Exception error) { Report("Failed", op.Progress, error.Message); }
        finally
        {
            // Also reconcile cancelled/failed wizards that may have changed
            // Windows registration before returning an error.
            if (entered) { try { RefreshInventory(); } catch (Exception) { /* Preserve the operation's actual result. The next inventory refresh can retry. */ } }
            _cancellation.TryRemove(op.Id, out _); _queuedApps.TryRemove(app.Id, out _); if (entered) _operations.Release(); Changed?.Invoke();
        }
    }
    public void Dispose() { foreach (var cts in _cancellation.Values) cts.Cancel(); _http.Dispose(); }
}
