namespace Airlift.Core;

public sealed record CachedInstaller(string Path, string AppId, string Version, long Bytes);
public sealed record CacheCleanupResult(int Removed, long Bytes, int Skipped);

/// <summary>Only completed, identifiable Forge installers in Airlift's cache are eligible.</summary>
public sealed class InstallerCache(StateStore store)
{
    public IReadOnlyList<CachedInstaller> FindOld(IReadOnlyDictionary<string, string> installedVersions)
    {
        var root = Path.Combine(store.Root, "downloads");
        var result = new List<CachedInstaller>();
        if (!Directory.Exists(root) || !IsOrdinaryPath(root)) return result;
        var activeApps = store.All<Operation>("operations").Where(o => o.Active).Select(o => o.AppId).ToHashSet();
        foreach (var appDir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(appDir);
            if (activeApps.Contains(id) || !installedVersions.TryGetValue(id, out var current) || !IsOrdinaryPath(appDir)) continue;
            foreach (var assetDir in Directory.EnumerateDirectories(appDir))
            {
                if (!long.TryParse(Path.GetFileName(assetDir), out var assetId) || assetId <= 0 || !IsOrdinaryPath(assetDir)) continue;
                foreach (var file in Directory.EnumerateFiles(assetDir, "*.exe"))
                {
                    try
                    {
                        if (!IsOrdinaryPath(file)) continue;
                        var info = ForgeInspector.Inspect(file);
                        if (info.Id == id && SemVersion.IsNewer(current, info.Version))
                            result.Add(new(file, id, info.Version, new FileInfo(file).Length));
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                    { /* Unknown, incomplete or inaccessible files are retained. */ }
                }
            }
        }
        return result;
    }

    public CacheCleanupResult ClearOld(IReadOnlyDictionary<string, string> installedVersions, IReadOnlySet<string> selectedPaths)
    {
        // Reinspect under the caller's operation lock. A preview is never deletion authority by itself.
        var removed = 0; long bytes = 0;
        foreach (var installer in FindOld(installedVersions).Where(i => selectedPaths.Contains(i.Path)))
        {
            try { File.Delete(installer.Path); removed++; bytes += installer.Bytes; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Setup may still be using it. */ }
        }
        return new(removed, bytes, selectedPaths.Count - removed);
    }

    private static bool IsOrdinaryPath(string path)
    {
        // Check every ancestor as well: the cache itself or an app directory can be a junction.
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
