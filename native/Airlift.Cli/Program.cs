using System.Text.Json;
using Airlift.Core;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine($"""
    {AppVersion.Display} — apps from public GitHub releases

    airlift-cli list                       List catalog apps and installed versions
    airlift-cli search <text>              Search the catalog
    airlift-cli refresh                    Check releases and reconcile installed apps
    airlift-cli info <app>                 Show release, package, and inventory
    airlift-cli download <app>             Download and verify the current package
    airlift-cli install <app>              Download and launch installation
    airlift-cli update <app>               Update an unpinned app
    airlift-cli uninstall <app>            Remove app, preserve personal settings
    airlift-cli pin|unpin <app>            Control version pinning
    airlift-cli export <file.json>         Export installed app identities and preferences
    airlift-cli import <file.json>         Import preferences (does not install apps)

    Options: --yes (confirm operation), --allow-unverified (explicitly accept missing hash),
             --silent (updates only; accepts the update license and skips the wizard),
             --data-dir <path> (isolated workspace), --version, --help.
    Windows installation uses Forge's wizard by default.
    """); return 0;
}
if (args.Contains("--version") || args.Contains("-v")) { Console.WriteLine(AppVersion.Display); return 0; }
try
{
    var arguments = args.ToList(); string? dataDir = null; var dataIndex = arguments.IndexOf("--data-dir");
    if (dataIndex >= 0) { if (dataIndex + 1 >= arguments.Count) throw new ArgumentException("--data-dir requires a path."); dataDir = arguments[dataIndex + 1]; arguments.RemoveRange(dataIndex, 2); }
    var yes = arguments.Remove("--yes"); var allowUnverified = arguments.Remove("--allow-unverified");
    var silentUpdate = arguments.Remove("--silent");
    using var manager = new PackageManager(new StateStore(dataDir));
    var command = arguments[0]; var value = arguments.Count > 1 ? arguments[1] : "";
    if (silentUpdate && command != "update") throw new ArgumentException("--silent is only supported for updates.");
    if (arguments.Count > 2) throw new ArgumentException("Unexpected arguments. Use --help.");
    if (command is "list" or "search" or "refresh")
    {
        if (command == "refresh") await manager.CheckReleasesAsync(true);
        manager.RefreshInventory();
        foreach (var app in manager.Apps.Where(a => command != "search" || $"{a.Name} {a.Description}".Contains(value, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{app.Name,-15} installed: {manager.Installed(app)?.Version ?? "—",-12} release: {manager.Release(app)?.Release?.Tag ?? "—",-12} {manager.Release(app)?.Error}");
        return 0;
    }
    if (command is "export" or "import")
    {
        if (value == "") throw new ArgumentException("Provide a JSON file path.");
        if (command == "export")
        {
            manager.RefreshInventory();
            var setup = new SetupDocument(1, manager.Apps.Where(a => manager.Installed(a) != null || manager.Preferences(a) != new AppPreferences()).Select(a => new SetupEntry(a.Id, manager.Installed(a)?.Version, manager.Preferences(a))).ToList());
            if (File.Exists(value) && !yes) throw new IOException("Output exists. Use another filename or --yes to overwrite.");
            await File.WriteAllTextAsync(value, JsonSerializer.Serialize(setup, JsonData.Options));
        }
        else { var setup = SetupDocument.Parse(await File.ReadAllTextAsync(value), manager.Apps); foreach (var entry in setup.Apps) manager.SetPreferences(Catalog.Find(manager.Apps, entry.AppId), entry.Preferences); }
        Console.WriteLine("Setup " + (command == "export" ? "exported." : "preferences imported. No apps were installed.")); return 0;
    }
    var selected = Catalog.Find(manager.Apps, value);
    if (command is "pin" or "unpin") { manager.SetPreferences(selected, manager.Preferences(selected) with { Pinned = command == "pin" }); Console.WriteLine($"{selected.Name}: {command}"); return 0; }
    if (command == "info")
    {
        manager.RefreshInventory(); Console.WriteLine(JsonSerializer.Serialize(new { App = selected, Release = manager.Release(selected), Installed = manager.Installed(selected), Preferences = manager.Preferences(selected) }, JsonData.Options)); return 0;
    }
    if (command is not ("download" or "install" or "update" or "uninstall")) throw new ArgumentException("Unknown command. Use --help.");
    if (!yes)
    {
        Console.Write($"{command} {selected.Name} on this computer? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) return 2;
    }
    using var cancel = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    string? previous = null;
    manager.Changed += () => { var op = manager.Store.All<Operation>("operations").Where(o => o.AppId == selected.Id).MaxBy(o => o.Started); var line = op == null ? "" : $"{op.Status,-14} {op.Progress,5:F0}%  {op.Message}"; if (line != previous) { Console.WriteLine(line); previous = line; } };
    await manager.ExecuteAsync(selected, command, allowUnverified, cancel.Token, silentUpdate);
    return manager.Store.All<Operation>("operations").Where(o => o.AppId == selected.Id).MaxBy(o => o.Started)?.Status == "Succeeded" ? 0 : 1;
}
catch (Exception error) { Console.Error.WriteLine("airlift: " + error.Message); return 1; }

