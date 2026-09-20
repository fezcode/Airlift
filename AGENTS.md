# Airlift working and release flows

## Scope and defaults

This repository is Airlift by Fezcode, a cross-platform app manager built on
C# / .NET 10 / Avalonia 12 / SQLite, with a shared `Airlift.Core` library, an
`Airlift.Desktop` GUI, and an `airlift-cli` companion. The React/Vite tree
(`src/`, `index.html`, `package.json`) is the original browser design reference
and is **not** part of a release; its `package.json` version is unrelated to the
app version and must not be bumped during RELEASE.

Preserve existing changes when working in a dirty checkout. Build scripts live at
the repository root; Forge is the sibling `../Forge` project. clockt is a
reference implementation of these flows, not Airlift's release target.

## Commit messages

Use a normal commit title and body only. Do not add `Co-Authored-By` trailers or
AI/assistant attribution (including Claude or Codex) to commits or release notes.
For multiline messages or messages containing quotes, write a temporary UTF-8
message file and use `git commit -F <file>`. Check the exit code and verify the
resulting commit before tagging; PowerShell 5.1 can split inline quoted messages.

## Version sources of truth

`native/Directory.Build.props` `<Version>` is the single source of truth. It
stamps every assembly Airlift ships, and `Airlift.Core.AppVersion` reads that
stamp at runtime, so the CLI help banner, both `--version`/`-v` outputs, the
GitHub `Airlift/x.y.z` User-Agent and the desktop About card all follow it.
Never hardcode a version in C#; use `AppVersion.Current`, `AppVersion.Display`
(`Airlift x.y.z`) or `AppVersion.UserAgent` (`Airlift/x.y.z`).

Three build inputs cannot read an assembly, so `version.ps1` synchronizes them:
the `forge.toml` `[app] version`, and the installer filename / title in
`README.md` and `native/README.md`.

```powershell
.\version.ps1                  # report every location + consistency
.\version.ps1 -Bump patch      # 0.2.2 -> 0.2.3 everywhere, then verify
.\version.ps1 -Set 0.3.0       # set an exact version, then verify
.\version.ps1 -Bump minor -DryRun
```

The script exits non-zero on any mismatch or missing location, so it is safe to
gate a release on. Edit versions through it rather than by hand, and read its
report rather than trusting the edit. Do not rewrite version numbers inside
`src/data/catalog.json`; those belong to the catalogued third-party apps, not to
Airlift.

## RELEASE workflow

Only an explicit request to **RELEASE** triggers the complete publishing flow.
Ordinary fixes, builds, and installer requests do not imply a version bump,
commit, push, tag, or GitHub release. When RELEASE is requested, perform these
steps in order and stop/report any failure before proceeding:

1. Run `./version.ps1 -Bump patch` by default, or `-Set x.y.z` for a requested
   version. Clarify conflicting/ambiguous version instructions. The script keeps
   `native/Directory.Build.props`, `forge.toml` and both READMEs synchronized;
   the assemblies and every version string in the app follow the props value
   through `Airlift.Core.AppVersion`. Run `./version.ps1` to verify.
2. Run `./build.ps1 -Test -Publish` to build, test, and publish the self-contained
   desktop and CLI executables into `dist/win-x64`. Do not skip tests
   for a release. Replacing this output closes any running Airlift window using
   it; report that effect and respect authorization already given.
3. Run `./build-installer.ps1 -SkipBuild` immediately afterward. Forge requires
   the sibling `../Forge/build/forge.exe`, produced by `gobake build` in Forge.
   Verify `dist/installer/Airlift-Setup-<version>.exe` exists, matches the
   new version, and surface that exact path for testing. During RELEASE, launch
   this new Setup executable for the user to install/test; an existing installed
   copy stays old until Setup is run. Keep the wizard's "Open Airlift" finish
   option enabled rather than restarting the old installed executable. Honor any
   requested test gate.
4. Review and commit the intended changes without attribution, using a message
   file. Verify the commit landed and record its hash before the next steps.
5. Push the commit to Airlift's configured remote/release branch (normally
   `origin main`). Inspect `git remote -v` and the branch first. This working copy
   publishes to `https://github.com/fezcode/Airlift` — if `.git` or a remote is missing, confirm
   the destination before publishing. Never borrow clockt's remote,
   or force-push.
6. Create the matching `vX.Y.Z` tag on the verified commit, push it, and create a
   GitHub release with `gh release create vX.Y.Z`, attaching only the matching
   `dist/installer/Airlift-Setup-X.Y.Z.exe` installer asset. Use title
   `Airlift vX.Y.Z`; notes start with `## Airlift vX.Y.Z`, followed by
   `### ✨ <feature>` sections and bullets. Supply notes through `--notes-file`
   to avoid PowerShell multiline argument splitting. Verify the published asset.

## Build and installer maintenance

- All distributable builds live under `dist/<runtime>/desktop` and
  `dist/<runtime>/cli`; Windows installers live in `dist/installer` as
  `Airlift-Setup-<version>.exe`, matching the other Fezcode apps. Keep Forge,
  build scripts, CI upload paths and both READMEs consistent with this layout.
- `artifacts` is for test fixtures, screenshots and the browser design preview.
  Vite must write to `artifacts/web-preview`, never `dist`: its output cleanup
  must not remove desktop builds or installers. Never clean the whole `dist`
  directory or delete previous installers as part of a build.
- Keep `forge.toml` on the Mica wizard theme, using Airlift's icon and identity.
- Keep the six wizard steps in order: welcome, license, folder, shortcuts,
  install, finish. Use `LICENSE.txt` for the MIT agreement, offer optional
  Desktop and Start Menu shortcuts, and keep Open Airlift selected on Finish.
  Include `LICENSE.txt` beside both published executables.
- Preserve the stable Forge ID `com.fezcode.airlift`; Forge detects installations
  through its automatically generated Windows uninstall record. Also maintain
  clockt-style HKCU `Software\fezcode\Airlift` InstallDir and Version values,
  using `${app.version}` for the version, and publish `airlift.ico` for shortcuts.
- Packaging must verify the embedded manifest and theme, not only the source
  TOML or EXE timestamp. Run `scripts/test-forge.ps1` after installer changes;
  it derives a disposable install/upgrade/uninstall fixture from Airlift's manifest
  and checks registry, shortcuts, custom paths, CLI files and settings retention.
  Run Windows installation checks and launch Setup in the normal user context,
  outside any development sandbox that hides the user's registry or AppData.
  A successful diagnostic is not a substitute for checking the visible wizard.
- Publish self-contained folders with `PublishSingleFile=false`. DLLs, native
  libraries, `.deps.json` and `.runtimeconfig.json` must remain alongside their
  executable; never distribute only the apphost EXE. Forge uses `[[dirs]]` to
  install the desktop folder at `${INSTALLDIR}` and the CLI folder at
  `${INSTALLDIR}/cli`. Keep shortcuts and the finish-page launch on the desktop
  executable. Validate both folders before creating Setup.
- Ship both the desktop app and `airlift-cli.exe`; the installer's file list must
  stay in step with the publish outputs `build.ps1` produces.
- Preserve user data under `%LOCALAPPDATA%/Fezcode/Airlift` unless the user
  chooses the installer's option to remove settings/data.
- Fail on inconsistent versions, failed tests/publishing, missing payload, or
  non-GUI Setup executables. Never report an old installer as a new success.
- Check the GUI Forge process exit code with `Start-Process -Wait -PassThru`.
  Quote arguments containing spaces and keep background build processes hidden.
- Scope build cleanup and process shutdown to this repository's `dist`
  output; preserve other installations, release installers, and unrelated files.
- `.github/workflows/native.yml` builds, tests, and publishes on win-x64,
  linux-x64, and osx-arm64 for every push. A red CI run blocks a release; the
  installer step is Windows-only and stays local.

These flows are adapted from clockt's `AGENTS.md`.

## Airlift self-updates and layout

- Silent installation is an explicit, unchecked choice for Windows updates only.
  Never silently install a missing app or downgrade. Recheck installed state
  immediately before launch and pass the detected installation directory to
  Forge; its silent default directory may differ from the user's custom folder.
  Keep the normal setup wizard available and preserve checksum verification.
- Silent Airlift self-updates require Forge's `silent-update-handoff-v1` bundle
  capability: `--wait-pid`, `--update-only`, and `--launch-after-install` allow a
  clean shutdown before replacing files and restart only after successful setup.
  Rebuild sibling Forge after runtime changes; do not package an older runtime.
- Inventory reconciliation must use the package operation lock so an old
  registry snapshot cannot overwrite a completed update. Refresh when returning
  to Airlift and after operations; version labels and update counts use that state.
- Installer cleanup is an explicit user action in Settings and Downloads. Delete
  only verified Forge installers in Airlift's download cache whose embedded
  identity matches the app and whose version is older than its installed version.
  Include Airlift itself; keep current/newer versions, unknown files, partial
  downloads, active operations and linked paths. Recheck eligibility under the
  package operation lock after the user reviews the cleanup preview. Never sweep
  general Windows temp folders, user settings, or `dist/installer` with this action.
- Sidebar navigation must scroll within its grid row at short window heights.
  Keep Settings/profile in a separate footer and verify navigation remains reachable.
- Airlift tracks its own stable releases from `fezcode/Airlift`, separately from
  the managed app catalog. Compare against `AppVersion.Current`, not an installed
  registry entry, when showing an available update.
- Windows x64 self-updates use `Airlift-Setup-<version>.exe`. Verify the digest
  (or collect explicit consent when absent) and Forge identity/version/architecture
  before launching. Reject downgrades against an existing installation. Close
  Airlift only after Setup starts; keep the app open on cancellation or failure.
  Other platforms offer release downloads until their installer mapping exists.
- Measure app cards using their actual available panel width. Do not derive card
  widths from window dimensions; scrollbars, borders and padding can force an
  unintended wrap. Keep resize/filter coverage in the native layout tests.

## Catalog icon maintenance

- App icons come from the `[app] icon` path in each catalogued sibling project's
  `forge.toml`. Use the actual source assets, not release screenshots or cached
  executable icons. Preserve each app's identity.
- Run `npm run icons:sync` (or `node scripts/sync-icons.mjs <workspace>`) to refresh
  every existing app's browser icon under `public/icons` and native PNG under
  `native/Airlift.Desktop/Assets/Icons`. The native export takes the largest
  PNG frame in an ICO. Missing/invalid assets fail the refresh instead of
  silently leaving stale desktop icons.
- An icon refresh must preserve catalog versions, descriptions, repositories,
  and other app metadata. `npm run catalog:sync` is a separate, broader import
  of sibling Forge manifests; it also refreshes both sets of icons.
- Rebuild and visually check the native catalog after an icon refresh. A
  maintenance build does not trigger RELEASE, a version bump, or publishing.
