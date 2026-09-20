using System.Diagnostics;
using System.Security.Cryptography;

namespace Airlift.Core;

public sealed record PreparedSelfUpdate(PackagePlan Plan, string File, bool Silent = false);

public sealed class SelfUpdater(StateStore store, GitHubClient github, Downloader downloads)
{
    // The public release source is independent of the third-party app catalog.
    public const string Repository = "fezcode/Airlift";
    public static CatalogApp App { get; } = new("com.fezcode.airlift", "Airlift", AppVersion.Current,
        "Fezcode", "Utilities", "Your apps. In good hands.", "Discover and manage your apps.",
        "#D7F59A", "", "Airlift", Repository, "${LOCALAPPDATA}/Programs/Airlift");
    public ReleaseCache? Cached => store.Get<ReleaseCache>("releases", App.Id + ":stable");
    public bool HasUpdate => Cached?.Release is { } release && SemVersion.IsNewer(release.Version, AppVersion.Current);
    public Task<ReleaseCache> CheckAsync(bool force = false, CancellationToken ct = default) =>
        github.GetReleaseAsync(App, false, force, ct);

    public static PackagePlan Plan(AppRelease release, string os, string arch)
    {
        if (!SemVersion.IsNewer(release.Version, AppVersion.Current)) throw new InvalidOperationException("You are already running this version of Airlift or a newer one.");
        if (release.Prerelease) throw new InvalidOperationException("Airlift self-updates use stable releases.");
        return PackageResolver.Resolve(App, release, os, arch);
    }

    public async Task<PreparedSelfUpdate> PrepareAsync(AppRelease release, bool allowUnverified,
        Action<double, string> progress, CancellationToken ct = default)
    {
        var plan = Plan(release, HostPlatform.Os, HostPlatform.Arch);
        if (plan.Asset.Sha256 == null && !allowUnverified)
            throw new InvalidOperationException("Review and explicitly allow this update without a published SHA-256 digest.");
        var file = await downloads.DownloadAsync(plan, progress, ct);
        progress(100, "Verifying Airlift’s installer…");
        await Task.Run(() => ForgeInspector.Verify(file, plan), ct);
        ct.ThrowIfCancellationRequested();
        return new(plan, file);
    }
}

public interface ISelfUpdateLauncher
{
    bool SupportsSilentUpdate => false;
    void Start(PreparedSelfUpdate update);
}

public sealed class SelfUpdateLauncher(StateStore store) : ISelfUpdateLauncher
{
    public bool SupportsSilentUpdate => OperatingSystem.IsWindows() &&
        new WindowsForgeProvider(store, new ProcessRunner()).FindInstalled(SelfUpdater.App) != null;
    public void Start(PreparedSelfUpdate update)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Use the release downloads to update Airlift on this platform.");
        if (update.Plan.App.Id != SelfUpdater.App.Id) throw new InvalidDataException("This is not an Airlift update.");
        _ = SelfUpdater.Plan(update.Plan.Release, HostPlatform.Os, HostPlatform.Arch);
        // Revalidate immediately before handoff. Keep the installer read-locked until Windows opens it.
        using var input = File.Open(update.File, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (update.Plan.Asset.Sha256 is { } hash && !Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded installer changed after verification.");
        var info = ForgeInspector.Verify(update.File, update.Plan);
        if (update.Silent && !info.SupportsSilentHandoff) throw new InvalidOperationException("This release’s installer does not support silent self-updates. Clear the silent option to use its setup wizard.");
        var installed = new WindowsForgeProvider(store, new ProcessRunner()).FindInstalled(SelfUpdater.App);
        if (update.Silent && installed == null) throw new InvalidOperationException("Silent updates require an installed copy of Airlift. Use the setup wizard for this portable copy.");
        if (installed != null && !SemVersion.IsNewer(update.Plan.Release.Version, installed.Version))
            throw new InvalidOperationException("The same or a newer Airlift version is already installed. Open that installation instead.");
        var start = new ProcessStartInfo(update.File) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(update.File)! };
        if (WindowsForgeProvider.RequiresElevation(info, installed)) start.Verb = "runas";
        start.ArgumentList.Add("--user-localappdata"); start.ArgumentList.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        start.ArgumentList.Add("--user-appdata"); start.ArgumentList.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if (update.Silent)
        {
            foreach (var argument in new[] { "--silent", "--accept-license", "--update-only", "--dir", installed!.Directory,
                "--wait-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--launch-after-install" })
                start.ArgumentList.Add(argument);
            start.WindowStyle = ProcessWindowStyle.Hidden;
        }
        using var process = Process.Start(start) ?? throw new IOException("Airlift Setup could not be opened.");
        // The caller closes Airlift only after this succeeds. Setup owns installation and restart.
    }
}
