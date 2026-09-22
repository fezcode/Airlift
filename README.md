# Airlift by Fezcode

A modern app manager for Windows, macOS, and Linux. Discover independent apps, follow their public GitHub releases, and manage your setup from one place.

Built with C# / .NET 10 / Avalonia 12 / SQLite, with GitHub release tracking, downloads, installation, removal, and a shared CLI core. The React prototype remains a design reference.

[GitHub](https://github.com/fezcode/Airlift) · [Releases](https://github.com/fezcode/Airlift/releases) · [Issues](https://github.com/fezcode/Airlift/issues)

## Run the native app

Open `dist/win-x64/desktop/Airlift.exe`, or install `dist/installer/Airlift-Setup-0.3.3.exe`. Both are built locally and include the .NET runtime. Builds use ordinary folders, not bundled executables: keep the DLLs and runtime files beside each EXE. The installer packages the complete desktop and CLI folders.

```powershell
.\build.ps1 -Test -Run             # Build, test, launch (requires .NET 10 SDK)
.\build.ps1 -Publish               # Self-contained desktop and CLI
.\build-installer.ps1 -SkipBuild   # Package published Windows outputs using Forge
```

The native app includes real installed-app discovery, stable/prerelease channels, background release checks while open, semantic version comparison, pins, resumable downloads, checksum/Forge validation, a persistent queue, collections, and setup preferences export/import. Windows installations use Forge's wizard for license/options, wait for completion, and verify the result. Uninstalls preserve personal settings. macOS/Linux package installation currently covers Typewriter's explicitly mapped portable archives; other package types remain unavailable until mapped.

Read the [native build, behavior and validation guide](native/README.md). The sections below describe only the original browser prototype.

## Update Airlift

Open **Settings → Check for Airlift updates**, or use Airlift's card on the **Updates** page. Startup and background checks also track stable releases from `fezcode/Airlift`. Available updates show the new version and Markdown release notes before you proceed.

On Windows x64, Airlift downloads the matching Forge Setup, verifies its published digest (or requests explicit consent when none is published), and checks its embedded app identity, version, and architecture. Airlift closes only after Setup opens successfully; Setup installs the new version and offers to launch it. Installed copies can also select **Install this update silently and reopen Airlift**: a compatible Forge installer waits for Airlift to exit, updates the existing folder, and restarts it after success. A portable copy uses the normal wizard. Other platforms link to the release downloads until a native update installer is available. No published release is reported as unavailable, never as “up to date.”

Managed Windows apps offer an unchecked **Install this update silently** option during update review. Fresh installations always use the wizard. Silent updates preserve the current install folder and use the installer's default options; progress and the verified installed version appear in Downloads. The CLI equivalent is `airlift-cli update <app> --silent`.

Use **Settings → Installer cache → Clear old installers**, also available in Downloads, to review and remove cached Forge installers older than the installed versions of your apps, including Airlift. Current and newer installers, unfinished downloads, unknown files and active operations are kept. Cleanup affects only Airlift's download cache.

## Run the browser design reference

```powershell
npm install
npm run dev
```

Open http://127.0.0.1:1420. If the global npm wrapper on this machine fails, use `& 'C:\Program Files\nodejs\npm.cmd' run dev`.

```powershell
npm run catalog:sync  # Reimport sibling projects' Forge manifests and icons
npm run icons:sync    # Refresh browser + native icons only, preserving catalog metadata
npm run build        # TypeScript check + production bundle
npm test             # GitHub release validation tests (Node 22.18+)
```

A sibling project joins the catalog by inviting Airlift in: a `properties.piml` at its root holding `(airlift) true`. Projects without that line are left alone whatever their Forge manifest says, and an invited project with no manifest fails the import rather than quietly dropping out of it. An invited project that has no public GitHub repository yet is named as waiting and skipped, since Airlift follows public releases; it joins the catalog on the next sync after it is published.

The checked-in catalog and icons make the preview portable; Workhammer is needed only when reimporting. To import another directory, run `node scripts/sync-catalog.mjs <directory>`. For icons only, run `node scripts/sync-icons.mjs <directory>`; it reads each existing app's Forge icon and exports its largest PNG frame for the desktop. The browser build writes to `artifacts/web-preview`, keeping its cleanup separate from the native builds and installers in `dist`.

## Explore the browser prototype

- Discover: search, categories, Windows/macOS/Linux package filters, grid/list, app details.
- My library: simulated installs, version pinning, update and uninstall actions.
- Downloads: simulated queue, pause/resume/cancel, completion history.
- Collections: three curated bundles with batch preview installs.
- Sources: **real**, manual GitHub stable release checks and direct download links for the catalog repositories, including osXos; no credentials required.
- Settings: update policy preference, sample installed versions for testing the update flow, preview reset.
- Ctrl+K / Cmd+K focuses search. Dialogs support Escape, focus trapping, and keyboard navigation.

**Browser-only boundaries:** lifecycle actions are simulated and stored in localStorage. Native Airlift performs actual operations. GitHub asset links in the browser are real. Local manifest versions may be newer than published releases. Browser fonts use Google Fonts with system fallbacks; the native app bundles DM Sans and Manrope with their open-source licenses.

Read [the product and technical design](docs/architecture.md) for the desktop plan, release contract, and proposed Forge additions.

## License

Airlift is released under the [MIT License](LICENSE.txt). Bundled dependencies retain their own licenses.


