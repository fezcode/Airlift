using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Airlift.Core;

public interface IPlatformProvider
{
    InstalledApp? FindInstalled(CatalogApp app);
    Task<InstalledApp> InstallAsync(PackagePlan plan, string file, CancellationToken ct);
    Task UninstallAsync(CatalogApp app, InstalledApp installed, CancellationToken ct);
}
public interface IProcessRunner
{
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, bool elevated, string workingDirectory);
}
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, bool elevated, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = workingDirectory };
        if (elevated && OperatingSystem.IsWindows()) start.Verb = "runas";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The process could not be started.");
        // Deliberately no cancellation/kill once an installer starts modifying the system.
        await process.WaitForExitAsync(); return process.ExitCode;
    }
}
[SupportedOSPlatform("windows")]
public sealed class WindowsForgeProvider(StateStore store, IProcessRunner runner) : IPlatformProvider
{
    public InstalledApp? FindInstalled(CatalogApp app)
    {
        foreach (var machine in new[] { false, true }) foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + app.Id);
            if (key?.GetValue("InstallLocation") is not string directory || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) continue;
            if (key.GetValue("DisplayVersion") is not string version) continue;
            var executable = Path.Combine(directory, app.Name + ".exe");
            return new(app.Id, version, Path.GetFullPath(directory), "forge", File.Exists(executable) ? executable : null, machine);
        }
        return null;
    }
    public async Task<InstalledApp> InstallAsync(PackagePlan plan, string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var info = ForgeInspector.Verify(file, plan);
        var previous = FindInstalled(plan.App);
        if (previous != null && !SemVersion.IsNewer(plan.Release.Version, previous.Version)) throw new InvalidOperationException("This version is already installed or older than the installed version.");
        if (previous != null) RequireAppClosed(previous.Directory);
        // Keep Forge's wizard: it handles licenses, custom inputs, and choices using the actual manifest.
        // Elevate the process we launch, so its process handle represents the real operation.
        var args = ProfileArguments();
        var exit = await runner.RunAsync(file, args, info.NeedsElevation || previous?.Machine == true, Path.GetDirectoryName(file)!);
        if (exit != 0) throw new InvalidOperationException($"Forge returned exit code {exit}.");
        var installed = FindInstalled(plan.App);
        if (installed is null || SemVersion.Compare(installed.Version, plan.Release.Version) != 0)
            throw new InvalidOperationException("Setup was closed or did not install the requested version. The inventory was not updated.");
        return installed;
    }
    public async Task UninstallAsync(CatalogApp app, InstalledApp installed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireAppClosed(installed.Directory);
        var source = Path.Combine(installed.Directory, "uninstall.exe");
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The installed Forge uninstaller is missing or redirected.");
        // Forge's --from-temp mode performs work in the process we can wait for, without its self-relay.
        var relay = Path.Combine(store.Root, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(relay);
        var executable = Path.Combine(relay, "uninstall.exe"); File.Copy(source, executable);
        try
        {
            var args = new List<string> { "--app-id", app.Id, "--from-temp", "--silent" }; args.AddRange(ProfileArguments());
            var exit = await runner.RunAsync(executable, args, installed.Machine, relay);
            if (exit != 0 || FindInstalled(app) != null) throw new InvalidOperationException($"Uninstall did not complete (exit {exit}). Refresh inventory and review Forge's diagnostics.");
        }
        finally { if (File.Exists(executable)) File.Delete(executable); if (Directory.Exists(relay)) Directory.Delete(relay); }
    }
    private static List<string> ProfileArguments() => ["--user-localappdata", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "--user-appdata", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)];
    private static void RequireAppClosed(string directory)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? executable;
                try { executable = process.MainModule?.FileName; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { continue; }
                if (executable != null && PathSafety.IsWithin(directory, executable)) throw new InvalidOperationException($"Close {process.ProcessName} and save your work before continuing.");
            }
        }
    }
}
public static class PathSafety
{
    public static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    public static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')) throw new InvalidDataException("Unsafe archive path.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(root, full)) throw new InvalidDataException("Archive entry escapes its destination.");
        return full;
    }
}
public sealed class PortableProvider(StateStore store) : IPlatformProvider
{
    public InstalledApp? FindInstalled(CatalogApp app)
    {
        var record = store.Get<InstalledApp>("installed", app.Id);
        return record?.Provider == "portable" && record.Executable != null && File.Exists(record.Executable) && PathSafety.IsWithin(Path.Combine(store.Root, "apps"), record.Directory) ? record : null;
    }
    public async Task<InstalledApp> InstallAsync(PackagePlan plan, string file, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() || plan.Format != "portable-tar" || plan.App.Id != "com.fezcode.typewriter") throw new NotSupportedException("No native adapter for this package.");
        var root = Path.Combine(store.Root, "apps", plan.App.Id); Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var destination = Path.Combine(root, plan.Release.Version);
        var previous = FindInstalled(plan.App);
        try
        {
            await ExtractPortableAsync(file, stage, ct);
            var executable = Path.Combine(stage, "typewriter");
            if (!File.Exists(executable)) throw new InvalidDataException("Expected typewriter executable is missing.");
            if (Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Any(p => p != executable)) throw new InvalidDataException("This portable package contains files outside its explicit package mapping.");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Directory.CreateDirectory(destination);
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Managed installation directory is redirected.");
            // Typewriter writes its preferences beside the executable. Preserve that sidecar across upgrades.
            var previousSettings = previous == null ? "" : Path.Combine(previous.Directory, "typewriter.ini");
            var settings = Path.Combine(destination, "typewriter.ini");
            if (File.Exists(previousSettings) && !File.Exists(settings)) File.Copy(previousSettings, settings);
            File.Move(executable, Path.Combine(destination, "typewriter"), true);
            var installed = new InstalledApp(plan.App.Id, plan.Release.Version, destination, "portable", Path.Combine(destination, "typewriter"));
            store.Put("installed", plan.App.Id, installed);
            if (previous?.Executable is { } old && old != installed.Executable && PathSafety.IsWithin(root, old) && File.Exists(old)) File.Delete(old);
            return installed;
        }
        finally { if (Directory.Exists(stage) && PathSafety.IsWithin(root, stage)) Directory.Delete(stage, true); }
    }
    public static async Task ExtractPortableAsync(string file, string destination, CancellationToken ct)
    {
        await using var input = File.OpenRead(file); await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip); long total = 0; var entries = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (++entries > 1000 || (total += entry.Length) > 512 * 1024 * 1024) throw new InvalidDataException("Archive extraction limit exceeded.");
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            var path = PathSafety.Child(destination, name);
            if (entry.EntryType is TarEntryType.Directory) { Directory.CreateDirectory(path); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("Archive contains links or unsupported special files.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (entry.DataStream != null) await entry.DataStream.CopyToAsync(output, ct);
        }
    }
    public Task UninstallAsync(CatalogApp app, InstalledApp installed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var root = Path.Combine(store.Root, "apps", app.Id);
        if (installed.Provider != "portable" || !PathSafety.IsWithin(root, installed.Directory)) throw new InvalidDataException("Installation is not owned by Airlift.");
        // Remove only the mapped executable. Preserve settings and any documents the user saved beside it.
        var executable = Path.Combine(installed.Directory, "typewriter");
        if (File.Exists(executable)) File.Delete(executable);
        if (Directory.Exists(installed.Directory) && !Directory.EnumerateFileSystemEntries(installed.Directory).Any()) Directory.Delete(installed.Directory);
        return Task.CompletedTask;
    }
}
