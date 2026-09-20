# Native Airlift 0.2.3

The desktop app and CLI use the same `Airlift.Core` library: catalog/version resolution, SQLite persistence, GitHub caching, downloads, package validation and OS providers. `Airlift.Desktop` contains Avalonia 12 views; `Airlift.Tests` covers core behavior and headless rendering. Available versions always come from published GitHub releases, not local Forge manifests.

## Build and run

The Settings and Updates pages include Airlift's own updater, tracking stable
releases at `fezcode/Airlift`. Windows x64 updates verify and open Forge Setup,
then close Airlift so Setup can replace its files. Cancelling or failing to open
Setup leaves Airlift running. The wizard offers to launch the installed version;
portable folders are not overwritten. Other platforms link to release downloads.

From the repository root, with .NET 10 SDK:

```powershell
.\build.ps1 -Test -Run
.\build.ps1 -Publish
.\build-installer.ps1 -SkipBuild
.\version.ps1                      # report the release version and verify it is consistent
.\version.ps1 -Bump patch          # bump it everywhere it is not derived from the assembly
```

Windows outputs:

- `dist/win-x64/desktop/Airlift.exe`
- `dist/win-x64/cli/airlift-cli.exe`
- `dist/installer/Airlift-Setup-0.2.3.exe`

The release version lives in `native/Directory.Build.props`; `Airlift.Core.AppVersion` reads the resulting assembly stamp, so the CLI banner, both `--version` outputs, the GitHub User-Agent and the desktop About card never hardcode it. `version.ps1` bumps that value and the three inputs that cannot read an assembly: `forge.toml` and the installer references in both READMEs. It exits non-zero if they disagree.

The installer uses six Mica wizard steps: Welcome, License Agreement, Select Folder, Optional Tasks, Installing, and Finish. It includes the desktop app, CLI, and MIT license, defaults to a per-user installation, and offers optional Desktop and Start Menu shortcuts. “Open Airlift” is selected on the finish page. Forge defaults to `../Forge/build/forge.exe`; override the installer script's `-Forge` argument if needed. Self-contained binaries do not require .NET to be preinstalled. Build artifacts are ignored by Git.

On PowerShell 7, use `./build.ps1 -Test -Publish -Runtime linux-x64` or `osx-arm64` for other platforms. Standard `dotnet build native/Airlift.slnx` also works; set `AVALONIA_TELEMETRY_OPTOUT=1` for restricted builds. The GitHub workflow builds/tests all three OS families and uploads artifacts, but has not been run remotely. OS signing/notarization is not configured.

## CLI

```powershell
.\dist\win-x64\cli\airlift-cli.exe refresh
.\dist\win-x64\cli\airlift-cli.exe info Descry
.\dist\win-x64\cli\airlift-cli.exe download Descry --yes
.\dist\win-x64\cli\airlift-cli.exe install Descry
.\dist\win-x64\cli\airlift-cli.exe pin Descry
.\dist\win-x64\cli\airlift-cli.exe export airlift-setup.json
```

Use `--help` for all commands. `--data-dir <path>` isolates state. `--allow-unverified` explicitly accepts a missing release digest. `--yes` confirms the requested operation; it does not accept a software license. Windows installation keeps Forge's wizard for licenses and options. Setup import applies pin/channel preferences only; it does not install software or restore historical versions.

## Package behavior

- At startup, Airlift reconciles installed versions and revalidates GitHub releases (when enabled). Newer versions appear as Update actions on cards, a sidebar count and a review banner on Discover/My library. Results appear during the check; missing/failed release data is reported separately from being up to date. Pinned apps remain excluded.
- Publish outputs are self-contained folders (`PublishSingleFile=false`), not bundled EXEs. Keep their libraries and runtime files together. Forge copies the desktop folder into `${INSTALLDIR}` and the CLI folder into `${INSTALLDIR}/cli`; the installed CLI is `cli/airlift-cli.exe`.
- Native publish outputs live in `dist/<runtime>/desktop` and `dist/<runtime>/cli`; Forge installers live in `dist/installer`. Tests and screenshots remain under `artifacts`. The browser design reference builds into `artifacts/web-preview` so it cannot erase native outputs.
- Refresh app icons with `npm run icons:sync` from the repository root. This reads the current icon path in each catalogued sibling app's `forge.toml`, updates browser icons, and losslessly extracts the largest PNG frame for native resources. It does not change catalog versions or metadata.
- Release notes use Markdig and native Avalonia text controls for headings, emphasis, nested lists, quotes, code blocks, tables and HTTPS links. Overview uses the same renderer as release history. HTML is displayed as text, and images are offered as links. Notes do not execute HTML or load remote images automatically.
- Pin and prerelease checkboxes persist immediately while keeping app details open. Changing channels refreshes that app's metadata and actions in place; a stale response from an earlier toggle cannot overwrite the current channel. Checkmarks use a dark glyph on the lime accent across pointer and keyboard states.
- App details include an Overview and an in-app Releases tab. History loads 20 releases at a time, includes labeled prereleases and non-versioned tags, caches pages with ETags, and preserves saved releases when offline. Notes and asset names/sizes appear directly in Airlift. Asset links in history open the browser; managed installs still use the selected channel's validated package. Sources opens history directly.
- Native typography uses bundled DM Sans and Manrope. The SVG folded-wing logo is the source for the native vector mark, web preview, PNG and Windows ICO; regenerate with `scripts/create-app-icon.ps1` after editing it. Font licenses ship inside the app resources.
- Stable/prerelease channels are per-app. Prerelease mode selects the highest semantic version among up to 100 recent published releases. Releases are cached in SQLite with ETags, six-hour freshness and rate-limit backoff. Background intervals are 6, 12 or 24 hours while the desktop is running. Background checks never install updates automatically.
- Explicit legacy rules map each known Forge `Name-Setup-Version.exe` to Windows x64. Typewriter's named macOS universal/Linux x64 archives use the portable adapter. No first-executable or guessed-architecture selection occurs.
- Downloads stream into partial files. Retry resumes where HTTP Range is supported and safely restarts if a server ignores it. Size and GitHub SHA-256 are checked before a file is promoted. Redirects are restricted to GitHub's known asset hosts. A hash is integrity metadata, not a publisher code signature; absent digests are disclosed and require explicit acceptance for installation.
- The read-only Forge inspector checks the PE, footer version/bounds, payload SHA-256, app ID and version without executing the downloaded file. The Windows provider launches the actual installer process with the required elevation/profile arguments, waits, and reconciles the installed version. A failed or cancelled setup never becomes a successful install merely because the first process exited.
- Installed-app discovery reads both registry scopes/views for the catalog's stable IDs. Airlift uses the installed Forge uninstaller in a controlled relay to await actual removal. Detected running apps must be closed first; settings are preserved and no purge flag is passed.
- Typewriter portable archives are extracted into Airlift-owned version directories with path/link/size limits and explicit payload checks. `typewriter.ini` is preserved across updates. Uninstall removes the mapped executable and preserves settings and documents beside it. Older settings directories can remain intentionally.
- Package operations are serialized across desktop/CLI processes by a process lock. Queue/download stages can be paused. Active installers are completed/cancelled in their wizard, not terminated by Airlift. Interrupted operations are marked for review on restart. Closed-process startup reconciliation reads the actual OS state.

Default data: `.NET LocalApplicationData/Fezcode/Airlift` (`%LOCALAPPDATA%\Fezcode\Airlift` on Windows). SQLite stores release metadata, inventory, preferences and journal entries. Downloads/staging are below the same directory. Download cache is retained for reuse. The app has no account requirement or background service.

## Validation

```powershell
.\build.ps1 -Test
.\scripts\test-forge.ps1
```

The suite covers semantic versions, exact package resolution, HTTP cache/ETags, release-history pagination/offline fallback/shared rate limits, range fallback, redirect/hash rejection, SQLite persistence and locks, archive safety, failed-installer reporting, setup import validation, native page rendering/search, transparent identity hover/press, keyboard focus and vertically centered navigation.

`test-forge.ps1` explicitly builds a disposable fixture with ID `com.fezcode.airlift.integration`, installs 1.0.0, upgrades to 1.1.0 and removes it in `artifacts/forge-fixture`. It uses a temporary per-user ARP entry and fixture-specific profile, never real catalog apps. Ordinary runs do not execute installers; the fixture path is an explicit opt-in.

Validated on this Windows machine: build/tests, native screenshots, real inventory, live public GitHub release checks, a real Descry download with SHA-256 and Forge verification, and the disposable Forge install/upgrade/uninstall cycle. macOS/Linux runtime behavior and UAC elevation under a different administrator account still require native-device validation.

Remaining extensions: more package formats/architectures, ingestion of `airlift-release.json`, code-signature policy, richer Forge progress/cancellation events, dependency graphs, rollback for portable packages, historical version installation, macOS app-bundle distribution and package publishing/signing automation. The original architecture document contains these longer-term designs; no Forge source changes were needed for the implemented workflows.
