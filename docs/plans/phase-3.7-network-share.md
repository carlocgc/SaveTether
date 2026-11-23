# SaveTether phase 3.7: sync folder on a network share (Windows)

Section 5c of the [implementation plan](implementation-plan.md) points here. This file holds the whole phase: the goal, the owner's decisions, the design, the app's changes, the tasks (T1 to T8), the exit test, and each task's progress. A delegated task needs only this file and the files it names.

Planned 2026-11-23 by the owner and the agent. T1 (design) and T2 (detection) are done; nothing else is built yet.

**Goal.** A Windows device can use `\\server\share\...`, or a mapped drive that points at one, as its sync folder. Save folders are linked there with symlinks, where Windows lets SaveTether make one. Where it does not, setup says exactly how to enable it. SaveTether never elevates itself. The user is told, where they choose it, that a share is not recommended.

## Why this is not supported today

On Windows the link in a game's save folder is a junction, and a junction cannot point at a network path. So a share is refused in three places: `Workspace.Init` and `SetupFlow.CheckSyncFolder` (by path text, through `PathRules.IsOnNetworkDrive`), and `SafeFs.CreateDirectoryLink` (`\\` targets). `Planner` refuses it as `TargetNotLocal`. The design says "`apply` never needs elevation" (design 5, `:843`); this phase changes that to "except a network share, without Developer Mode".

A directory **symlink** can point at a share. On Windows it needs an elevated process or Developer Mode. .NET's `Directory.CreateSymbolicLink` passes `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE`, so with Developer Mode on it needs no elevation.

**Is admin needed only to create the symlink?** Yes. Only to create one: Apply, Add app, and Recover re-creating a link. Reading and writing through it, the game, Revert (removing the link), backups, health checks, tray and autostart need none. The link stays after an elevated step.

## Decisions

| # | Decision |
|---|---|
| D1 | **No elevation code in SaveTether.** A share is allowed when a test symlink can be made: the process is elevated, or Developer Mode is on. Otherwise the app refuses with the fix. SaveTether does not turn Developer Mode on |
| D2 | **Two ways to enable it, both the user's:** Developer Mode (Settings, System, For developers; once, no prompts after), or run the CLI from an elevated terminal for the one-off Apply. The refusal names both |
| D3 | **No installer admin option and no run-as-admin flag** (owner, 2026-11-23). An admin installer does not make the app run as admin, and an always-elevated app costs more than it gives (D7). The installer stays per user. "No admin" in the README, spec section 11 and design `:843` becomes "no admin, except to link a network share without Developer Mode" |
| D4 | **Mapped drive letters are stored as UNC** (`\\server\share\x`), resolved at step 1 with WNetGetConnection, and the screen says so. A symlink to `Z:\` resolves per logon session and breaks under an elevated shell |
| D5 | **Only the sync folder may be a share.** A save folder or backup location on a network drive stays refused (`SourceNotLocal`) |
| D6 | **No copy fallback.** If the link cannot be made, nothing is linked |
| D7 | **The elevated helper is deferred** (T8, not built). It is built only if Developer Mode proves too much of a hurdle and the owner decides on code signing. Always-elevated is rejected: UAC on every launch, `Run` key autostart skipped for elevated programs, no drag and drop from Explorer, mapped drives invisible to the elevated process |
| D8 | **A share is allowed but not recommended, and the user is warned where they choose it:** setup step 1, `init`, on Windows and Linux alike. It does not block. The text, terse: "Not recommended: if the share is disconnected, games and emulators that save here can fail or misbehave. A folder on this device, kept in step by a sync tool, is safer." The same warning is in `docs/sharing.md` and shows once more on the first Apply to a share |

Not in this phase: any elevation, helper or prompt batching; save folders or backups on a network drive; share credentials (Windows holds them); a copy fallback; Game Mode.

## Design

What the code has today, and what changes. Paths are under `src/`.

**Where a network path is refused or detected.**
- `SaveTether.Core/FileSystem/PathRules.cs:27` `IsOnNetworkDrive`: `\\server\share`, `\\?\UNC\`, or a drive letter whose `DriveInfo.DriveType` is `Network`. Always false off Windows.
- `SaveTether.Core/FileSystem/SafeFs.cs:66`: `CreateDirectoryLink` throws for a `\\` target, then makes a junction (`WindowsReparse.SetJunction`) and verifies `EntryKind.Junction`. `RecreateLink` (`:764`) is the only code that makes a symlink on Windows.
- `SaveTether.Core/Operations/Workspace.cs:107` `NetworkSyncRootMessage`, `:115` `ExistingDevices`, `:135` `Init`; `Workspace.Apps.cs:309` and `Workspace.cs:314` (save folders; stay).
- `SaveTether.Core/Planning/SnapshotReader.cs:169` `IsOnLocalVolume`, and `Planner.cs:251` to `:260` (`TargetNotLocal`, `SourceNotLocal`).
- `SaveTether.Gui.Presentation/SetupFlow.cs:121` (`SyncFolderFinding.NetworkDrive`) and `:70` `SyncFolderIntro`.
- `SaveTether.Gui.Presentation/ManualAddFlow.cs:290`: a save folder; stays.

**The capability order,** used wherever a share is chosen or linked:
1. The path is a share (`PathRules.NetworkKind`, T2). A mapped letter is converted to UNC (D4).
2. Windows evaluates local-to-remote symlinks (`SymlinkLocalToRemoteEvaluation`, default on, can be turned off by policy). If not, refuse with the policy as the reason.
3. `SymlinkCapability.Probe()` makes a test symlink in a scratch folder under the backup root, to the share, and removes it through `SafeFs`. If it works (elevated, or Developer Mode), the share is allowed.
4. If it fails with Win32 error 1314 (`ERROR_PRIVILEGE_NOT_HELD`), refuse with the Developer Mode steps and the elevated-terminal alternative (D2).

**The link.** On Windows `SafeFs.CreateDirectoryLink` keeps the junction for a local target and makes a symlink for a network target, then verifies `EntryKind.Symlink` and the same target. The link type is recorded as `LinkType.Symlink` (`Executor.cs:548` already derives it from `Probe`). A device can hold both kinds. Revert, health checks and backups already handle `Symlink`.

**A share can be offline or slow.** Probes get a timeout (default 5 s), because probing a share that is not there can stall. An offline share maps to the existing `SyncRootUnavailable` health state. A game started while the share is down sees a dangling link and cannot save: D8 says so. Two devices on one share have no sync tool between them and both write one live copy, so playing the same game on two devices at once can corrupt a save; `docs/sharing.md` says so. Emulators that hold files open over SMB are unknown until T7.

### The deferred helper (T8, for the record)

One-shot `savetether link-helper --link <p> --target <t>`, started with the `runas` verb. It is a verb of the installed `savetether.exe`: no second binary, no temp copy, no shell or PowerShell child, no network, no autorun, fixed arguments validated with `SafeFs`. It takes no lock and writes no journal; the parent holds both and verifies the link afterwards. One UAC prompt per link. The unsigned exe shows "Unknown publisher" and carries antivirus reputation risk: `runas` consent prompts are normal and are not what heuristics target (they target UAC bypasses), but an unsigned, low-reputation AOT exe scores poorly on its own. Before release: scan with `MpCmdRun -Scan`, check VirusTotal for the installer and both exes, and submit any false positive to Microsoft. Code signing is the real fix. It touches `Executor` create and `Executor.Recovery`. Size L, Opus.

### Gaps in other location types

| Location | Today | This phase |
|---|---|---|
| UNC, mapped drive (Windows) | Refused | **Supported (T2 to T5)** |
| `subst` drive of a share | Accepted as local; the junction then fails oddly | Detect with `QueryDosDevice` (T2) |
| DFS, WebDAV, `\\.\` paths | Not detected | Detect the `\\` prefix and `GetDriveType` (T2) |
| SMB or NFS mount on Linux | Accepted, untested (`sharing.md:38`); D8 warning added | Test on Mint and the Deck (T7); no code expected |
| Linux sync root on FAT, exFAT or NTFS-FUSE | No filesystem detection | Advisory only if cheap (T6) |
| Case-insensitive Linux mounts | Treated as case-sensitive | Docs note |
| Removable or SD sync root | Only the "Missing" text | Docs note |
| OneDrive Files-On-Demand | Docs advise, no check | Advisory in T6 if cheap, else deferred |
| Flatpak sandbox as a sync root | Per-mapping checks only | Deferred |
| Different volume from the saves | Works | None |

## The app: what changes

- **Setup step 1.** A share is no longer a refusal. A new `SyncFolderFinding.NetworkShare` continues as `AlreadySetUp` or `NotSetUp` do, and the page shows the D8 warning as a visible note, and for a mapped letter: "Using `\\server\share` (the drive Z: points to)". `SyncFolderIntro` stops calling a share Linux-only.
- **A link that cannot be made** is a refusal with a button, in the style of `GiveAccess`: the text names Developer Mode (Settings, System, For developers) and the elevated terminal; the button is **Check again** (`RefusalAction.CheckAgain`).
- The spec's refusal tables (`desktop-app-spec.md:118`, `:195` to `:200`) and section 11 get the new rows and "As built" notes.

## Tasks

One PR each against `development`. Every task follows `CLAUDE.md`: every delete, move or link through `SafeFs`; the app holds no logic of its own; stage files by name; `dotnet build -warnaserror` and `dotnet test` before the PR; the docs change in the same PR.

Each task ends with a hardware check its implementer runs **before the PR**, by `docs/testing/hardware-testing.md`, and its results go in Progress.

| # | Task | Size | Model |
|---|---|---|---|
| T1 | Design section 5.16; amend the "no admin" lines (design `:843`, README `:51` and `:74`, spec section 11) | S | Sonnet |
| T2 | Detection: network kinds, mapped letter to UNC, `subst`, symlink-evaluation check, probe timeout | S | Sonnet |
| T3 | Windows symlink for a network target and `SymlinkCapability`; allow a share in `Init`, `ExistingDevices`, `Planner`; typed refusal for 1314 | M | Opus |
| T4 | The app: step 1 finding, the D8 warning, the Developer Mode refusal and its action, spec "As built" | M | Sonnet |
| T5 | CLI and JSON: `init` accepts a share with the D8 note, refusal kinds, `link_type` `symlink` on Windows | S | Sonnet |
| T6 | Docs and optional advisories: `sharing.md` share section, `hardware-testing.md` share flow | S | Sonnet |
| T7 | Exit test on hardware | S | Owner, with Opus |
| T8 | Elevated helper: **deferred, not built** | L | Opus |

**Who does what.** T3 is the only task with real risk (link creation order, failure cleanup and privilege errors inside the executor), so Opus. T1, T2, T4, T5 and T6 follow existing patterns (`SetupFlow`, `RefusalAction`, `Texts`, docs), and Sonnet can do them from this file alone. About 80% of the tokens are on Sonnet. Opus reviews a Sonnet task only if its hardware check fails.

**Order.** T1, T2, T3. Then T4 and T5 can run side by side; they share only the refusal kinds that T3 defines. Then T6, then T7.

**Test machines.**
- **Share host:** the Mint box with Samba and a share `sttest`. **Ask the owner before installing Samba:** it changes the machine. The PC reaches it by UNC and by a mapped drive.
- **The Windows PC:** PowerShell, a scratch `SAVETETHER_HOME`, one normal and one elevated shell; the owner switches Developer Mode. Runs of the app on Windows are the owner's.
- **Linux clients (T7):** Mint and the Deck, with an SMB or NFS mount.
- **No second machine:** a loopback share `\\localhost\sttest` (`net share`, needs admin once).

### T1: design section (S)

- **Deliver:** design section 5.16, "Sync folder on a network share (Windows)", holding D1 to D8, the capability order, the refusal text and the edited promise lines. A pointer in `implementation-plan.md`.
- **Files:** `docs/design/save-redirection-design.md`, `README.md`, `docs/design/desktop-app-spec.md` (section 11).
- **Done when:** every "no admin" line says "except a network share without Developer Mode".
- **Hardware:** none.

### T2: detection (S)

- **Deliver:**
  - `PathRules.NetworkKind(path)` returns `None`, `Unc`, `MappedDrive` or `Subst`.
  - `PathRules.ToUncIfMapped(path)` uses WNetGetConnection through `LibraryImport` (AOT-safe), and returns the path unchanged when it is not mapped.
  - `SymlinkPolicy.LocalToRemoteAllowed()` reads the registry value.
  - A probe with a timeout (default 5 s) that reports "unreachable" instead of stalling.
- **Files:** `FileSystem/PathRules.cs`, new `FileSystem/NetworkPaths.cs`, tests beside `tests/SaveTether.Tests/FileSystem/NetworkDriveTests.cs`.
- **Tests:** kinds from path text; a mapped letter converted (with a faked lookup); a timeout reports unreachable; off Windows always `None`.
- **Verify on hardware (PC):** `net use Z: \\mint\sttest`, and a `subst` drive. A unit run or a small command prints the kind for `\\mint\sttest`, `Z:\`, the `subst` drive and `C:\`. `fsutil behavior query SymlinkEvaluation` agrees with the policy read.

### T3: symlink for a network target (M)

- **Deliver:**
  - `SafeFs.CreateDirectoryLink` on Windows: a junction for a local target (unchanged), a symlink for a network target, verified as `EntryKind.Symlink` with the same target.
  - `SymlinkCapability.Probe()` returns available or `NeedsDevModeOrElevation`, by the test link described above.
  - A typed `SymlinkPrivilegeException` for Win32 error 1314.
  - `Workspace.Init`, `ExistingDevices`, `Planner` `TargetNotLocal` and `SnapshotReader.IsOnLocalVolume` accept a share when the capability and the evaluation policy allow it. `NetworkSyncRootMessage` is reworded to the Developer Mode fix.
- **Files:** `FileSystem/SafeFs.cs`, new `FileSystem/SymlinkCapability.cs`, `Operations/Workspace.cs`, `Planning/Planner.cs`, `Planning/SnapshotReader.cs`, tests in `SafeFsLinkTests.cs`.
- **Tests:**
  - a local target is still a junction
  - a UNC target is a symlink (CI runners are elevated; skipped off Windows)
  - error 1314 is mapped
  - a share is refused when the evaluation policy is off or the capability is missing
  - every create and delete still goes through `SafeFs`
- **Verify on hardware (PC, CLI):**
  1. From an elevated shell: `init --sync-root \\mint\sttest\sync --create-root`, add a mapping and `apply`. The save folder is a symlink to the UNC path, files written through it appear on Mint, and `revert` gives a real folder with the same hash.
  2. From a normal shell with Developer Mode off: the refusal names both fixes and nothing changes.
  3. With Developer Mode on, normal shell: it works with no prompt.
  4. A device with one junction mapping and one share mapping works, and Revert needs no elevation.

### T4: the app (M)

- **Deliver:**
  - The D8 warning as a visible, non-blocking note on step 1 (`Texts.NetworkShareWarning`, one string shared with the CLI), for every share, on Windows and Linux.
  - `SyncFolderFinding.NetworkShare`, which continues; its sentence explains what a share needs and shows "Using `\\server\share` (the drive Z: points to)" for a mapped letter.
  - A refusal with the Developer Mode steps and **Check again**.
  - `SyncFolderIntro` no longer calls a share Linux-only. The D8 warning shows once more on the first Apply to a share.
  - The spec's refusal tables and "As built".
- **Files:** `Gui.Presentation/SetupFlow.cs`, `ActionTexts.cs`, `Texts.cs`, `Gui/SetupView.cs`, `Gui/ActionFlow.cs`, the spec; Presentation tests.
- **Tests:** the finding for a UNC path, a mapped letter and a missing share; the refusal's text and action; the warning shown for a share and not for a local folder.
- **Verify on hardware:** Mint and the Deck check that Linux setup is unchanged, and that a mounted share shows the warning. Windows, run by the owner: setup with `\\mint\sttest`, with `Z:`, with a missing share and with the share offline; Add app with Developer Mode off, then on. A screenshot of each state.

### T5: CLI and JSON (S)

- **Deliver:** `init` accepts a share and prints the D8 warning as a note (in `--json`, a `warnings` entry; no extra confirmation); the new refusal kind in `--json`; `link_type` `symlink` on Windows. `docs/cli.md`, `docs/cli-json.md` and `JsonContractTests`.
- **Verify on hardware:** `dotnet test`; run `init` and `apply` against the share in both shells and compare the output with `docs/cli-json.md`.

### T6: docs and advisories (S)

- **Deliver:** a Windows share section in `docs/sharing.md` (setup, Developer Mode, offline behaviour, the two-device caution, no conflict copies, the D8 warning); the share test flow and the Samba setup in `docs/testing/hardware-testing.md`. Only if cheap: advisories for a FAT or exFAT Linux sync root and a cloud-placeholder sync root; otherwise list them as deferred here.
- **Verify:** follow the written flow once on the PC from scratch.

### T7: exit test (S, hardware)

- The PC (symlink) and Mint or the Deck (SMB or NFS mount) on one share, with one shared mapping: save on one, see it on the other, and back.
- The share goes offline: health shows `SyncRootUnavailable` and no data is touched; it comes back.
- An emulator holding a file open over SMB.
- Revert on the PC; remove the device.
- This also covers the Linux SMB and NFS gap in the table.

### T8: elevated helper (deferred, not scheduled)

See "The deferred helper". Built only on the owner's decision.

## Exit test

See T7.

## Progress

**T1 (done 2026-11-23).** Design section 5.16 written (D1 to D8, capability order, link, refusal text, offline). "No admin" lines amended: design R5, 5.12 link type, 5.14 distribution; README (two lines); spec section 11. Design line 66 (the symlink row of the comparison table) now says a network share is the exception. Left as they were, because they stay true: design lines 11 (junctions need no admin) and 900 (autostart needs none). The refusal text in 5.16 is proposed wording; T4 sets the final text. Design 5.15 (phase 3.6) was never written, so 5.16 follows 5.14. No hardware check (docs only).

**T2 (done 2026-11-23, hardware check partly open).** `NetworkKind` enum, `PathRules.NetworkKind` and `PathRules.ToUncIfMapped`, `SymlinkPolicy.LocalToRemoteAllowed` and a bounded `IsReachable` probe (5 s) in `FileSystem/NetworkPaths.cs`; WNetGetConnection and QueryDosDevice through `LibraryImport`. Decisions: a `subst` of a local folder is `None` (harmless to a junction), only a `subst` of a share is `Subst`; a letter is asked as subst first, because a subst of a share can report as a network drive; `IsOnNetworkDrive` now also true for `Subst`. Policy is read from `HKLM\...\Control\FileSystem\SymlinkLocalToRemoteEvaluation`, absent means on. The probe's abandoned check keeps running on a pool thread (a `shortcut:` comment says so). Tests in `NetworkPathsTests` with faked lookups. Checked on the PC: policy reads True and `fsutil behavior query SymlinkEvaluation` says local-to-remote ENABLED; real calls for `C:\`, an unused letter and a UNC path answer None, None, Unc. A real mapped drive (`I:` to an SMB share on the owner's NAS) reads as `MappedDrive`, converts to UNC, and is unreachable when access to it is refused. `MappedShare` also accepts error 1201 (remembered drive, share down), which the Windows docs say still names the share. **Not checked:** that 1201 case live (a removed mapping gives 2250 and is correctly `None`; an open session survives a restriction on the share), and a real `subst` of a share. T3 or T7 can cover them.

T3 to T8: not started. One entry per task, as in phase 3.6.

Not verified yet: that a Developer Mode symlink to a UNC target opens for a normal process, and the default of `SymlinkLocalToRemoteEvaluation`. T2 and T3 prove both on the PC first.
