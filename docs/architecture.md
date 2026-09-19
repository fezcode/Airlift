# Airlift: product and desktop architecture

## Implemented in 0.2.0

Native Airlift is implemented under `native/` with Desktop, Core, Cli and Tests projects. Core currently contains infrastructure/platform adapters behind explicit interfaces. The UI uses code-defined Avalonia 12 views and core state notifications. The [native guide](../native/README.md) describes implemented behavior, validation and limitations; the rest of this document preserves the broader product plan.

Implemented: public GitHub tracking/cache/channels, exact package mapping, streamed/resumable downloads, SHA-256/Forge inspection, SQLite state/journal, real Windows inventory and install/update/uninstall, portable Typewriter installation on macOS/Linux, version pins, collections, and preference import/export. The Windows adapter uses a directly awaited installer process and an installed-uninstaller relay, so no Forge source changes were necessary. Mac/Linux native-device testing, other package formats, richer events, signed distribution and release-manifest ingestion remain future work.

## Product direction

Airlift is a graphical package manager backed by public GitHub Releases, with a matching CLI. The catalog is a curated set of repository identities and package policies; GitHub is the source of truth for published versions and downloadable assets. A working tree's version is not proof that a release exists.

The visual direction combines a charcoal and olive foundation, pale lime actions, restrained typography, actual app icons, and a distinctive Airlift arrow. Discovery is welcoming; management screens prioritize readable status and explicit operations. The UI should translate into Avalonia resources and templates, including keyboard focus, responsive layout, reduced motion, and screen-reader labels.

## Recommended stack

| Layer | Choice | Why |
|---|---|---|
| Desktop UI | Avalonia, current stable release validated at implementation time | Windows, macOS, Linux; fits existing clockt, Hisashi, and Cogas experience |
| Runtime | C# / .NET 10 LTS | Existing team ecosystem; suitable process, network, filesystem, and concurrency APIs |
| Presentation | MVVM with CommunityToolkit.Mvvm | Testable state and commands, thin views |
| Core | Plain C# library, independent of UI | One resolver and operation pipeline for desktop and CLI |
| Persistence | SQLite via Microsoft.Data.Sqlite | Installed inventory, release cache, operation journal, pins, collections |
| Release provider | GitHub REST API through HttpClient | Public releases without mandatory accounts |
| Downloads | HttpClient streaming + on-disk staging | Bounded memory, cancellation, hash validation, resume where supported |
| Windows provider | Forge installer processes | Reuse existing install/upgrade/rollback/uninstall semantics |
| Other platforms | Separate macOS and Linux adapters | Platform-specific package formats and privilege behavior |

Avalonia support: https://docs.avaloniaui.net/docs/supported-platforms
.NET lifecycle: https://dotnet.microsoft.com/en-us/platform/support/policy

The browser preview uses React 19, TypeScript and Vite as the approved design reference. The native shell and shared C# core now implement actual operations. Forge's Go implementation is consumed without a rewrite.

## Suggested solution boundaries

```text
src/Airlift.Desktop/          Avalonia views, resources, view models
src/Airlift.Core/             Catalog, version resolver, operation state machine
src/Airlift.Infrastructure/   SQLite, GitHub, downloads, process management
src/Airlift.Platform.Windows/ Forge adapter, inventory, elevation
src/Airlift.Platform.MacOS/   Signed app bundles / pkg support
src/Airlift.Platform.Linux/   AppImage initially; distro packages separately
src/Airlift.Cli/              airlift search/install/update/remove/export/import
tests/                       Resolver, journal recovery, download and adapter tests
```

These paths are a proposed C# solution structure, not existing implementation files.

## GitHub Releases workflow

1. Catalog maps stable app ID to a public `owner/repo`, display metadata, channel and package mapping policy. The initial ten repositories were verified against local Git remotes.
2. Query `/repos/{owner}/{repo}/releases/latest` for GitHub's designated latest stable release. For prerelease opt-in or full version history, use the paginated release list and an explicit version policy. Tags alone are not installable releases.
3. Cache release ID, tag, timestamps, ETag, asset IDs, URLs, sizes, digests, and channel. Use a configurable background interval, bounded concurrency, conditional requests, rate-limit headers, Retry-After, and backoff. Public unauthenticated requests have a shared IP limit of 60/hour, so do not poll every app every minute. Offer optional authentication via OS credential storage if needed; never ship a secret or require a login to browse.
4. Resolve OS, CPU architecture, channel and version against an explicit asset mapping. Never pick the first `.exe`, and never interpret the auto-generated source archives as installable packages. An unqualified legacy asset needs a catalog rule; an ambiguous asset is unavailable until mapped.
5. Show release notes, version changes, download size, source and requested installation scope. Support install, download-only, update, repair, pin version, and remove.
6. Download into a unique staging file. Validate expected size and SHA-256 from the release manifest or GitHub asset digest when present. Missing digests stay explicitly unverified; never present a checksum as publisher authentication. Authenticate publisher/platform signatures separately where applicable. Reject a changed asset, invalid redirect, path traversal, incompatible architecture, or hash mismatch before execution.
7. Run the appropriate provider through a reviewed execution plan. No shell evaluation of release metadata. UI runs without administrator privileges; request elevation only for the scoped operation that needs it.
8. Journal operation stages and actual exit status. Reconcile inventory after success and on restart. Never declare success merely because a process launched or a file finished downloading.

The browser prototype performs manual release checks. Native Airlift adds automatic selection of explicitly mapped packages and download verification.

GitHub API reference: https://docs.github.com/en/rest/releases/releases
Rate limits: https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api

## Release metadata contract (proposed)

Attach `airlift-release.json` to each release, or maintain equivalent explicit mapping in the catalog for existing releases:

```json
{
  "schemaVersion": 1,
  "appId": "com.fezcode.descry",
  "version": "0.87.0",
  "packages": [
    {
      "os": "windows",
      "arch": "x64",
      "format": "forge-exe",
      "asset": "Descry-Setup-0.87.0.exe",
      "sha256": "<SHA-256 computed from the final built asset>",
      "scope": "machine",
      "capabilities": ["install", "update", "repair", "uninstall"]
    }
  ]
}
```

This is an example, not existing release metadata. Scope and architecture must be derived from the actual build configuration. Reference the asset by exact name and resolve it within the same GitHub release; do not accept arbitrary execution URLs. Bind release version and app ID to expected catalog identity. Keep CLI arguments owned by trusted adapters, not supplied as shell snippets by release metadata. Multiple OS/architecture entries can coexist in one release. Absent platform entries mean unavailable.

## Forge integration

Existing Forge capabilities inspected in this workspace:

- `forge inspect <setup.exe>` and installer `--inspect` expose embedded manifest text.
- `/S` or `--silent`, `--dir`, `--set`, and `--accept-license` support unattended installs. License acceptance must reflect the user's actual consent, not a default hidden flag.
- `--reinstall` handles explicit reinstalls. The engine detects upgrades and supports rollback.
- Silent uninstall via installer, or the registered uninstaller's `--app-id <id> --silent`; optional `--purge-settings` only after explicit opt-in.
- Exit codes 0 (success), 2 (invalid arguments), 1602 (cancelled), 1603 (failure), 1605 (not installed).

Recommended additive changes, once implementation reaches the native adapter:

1. `forge inspect --json` with a versioned schema: identity, architecture, scope, required inputs, license, capabilities. Avoid parsing human diagnostics.
2. A dedicated JSON-lines event pipe/file: operation ID, phase, completed/total, cancellability, elevation handoff, result. Do not mix events with console logs; preserve communication across the UAC process hop.
3. A machine-readable inventory query with app ID, installed version, install path and log reference. Reconcile ARP data and Forge state; validate any uninstaller path before executing it.
4. Optional build output for release metadata, hashing the completed installer and emitting its package format and capabilities. This can initially live in release CI without a Forge change.
5. A cancellation contract at safe transaction boundaries; do not terminate an elevated installer midway through registry/filesystem changes.

No Forge source changes were needed for the implemented adapter. No sibling project source files were edited.

## Platform plan

| Platform | Initial execution provider | Follow-up |
|---|---|---|
| Windows | Forge EXE, per-user/per-machine inventory | Explicit MSI/MSIX/winget providers if desired |
| macOS | Published signed/notarized `.app` bundles with controlled installation; `.pkg` uses system installer | Homebrew adapter as an optional additional source |
| Linux | Published AppImage with managed files and desktop entries | Flatpak or distro package managers through explicit providers |

A portable app bundle, a system package and a Forge installer have different lifecycle semantics. Capability flags determine which actions the UI offers. Airlift supporting three operating systems does not make a Windows-only app cross-platform.

## Operation model

```text
queued → resolving → downloading → verifying → awaiting-consent
       → installing → reconciling → succeeded
failure → rolling-back → failed (with diagnostic detail)
```

Use bounded parallel downloads, serialize mutations to the same app, and maintain dependency/order constraints. Pause only phases that are genuinely resumable; cancellation of installs is negotiated with the provider. Record durable operation state and detect interrupted operations on restart. Settings/data survive uninstall unless explicitly selected for removal. Pins suppress automatic updates and the update-all set, and the UI explains how to unpin.

## Delivery sequence

1. **Design (this repository):** interactive screens, real catalog identities/icons, manual GitHub release lookup, explicit simulations.
2. **Native foundation:** Avalonia shell, C# core, SQLite inventory and persisted release tracking; read-only OS inventory.
3. **Windows vertical slice:** release selection, streaming downloads, verification, Forge install/update/uninstall; validate with disposable test apps before general execution.
4. **Cross-platform packages:** release publishing for macOS/Linux, adapters, platform CI and native runtime testing.
5. **Power-user features:** shared CLI, collection export/import lockfiles, rollback when packages support it, prerelease channels, scheduling and optional external package sources.
