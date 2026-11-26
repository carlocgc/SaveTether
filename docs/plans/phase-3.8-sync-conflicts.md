# SaveTether phase 3.8: sync-tool conflict copies ("File conflict")

Section 5d of the [implementation plan](implementation-plan.md) points here. This file holds the whole phase: the goal, the owner's decisions, the design, the tasks (T1 to T5), the exit test, and each task's progress. A delegated task needs only this file and the files it names.

Planned 2026-11-26 by the owner and the agent. T1 to T4 done; the exit test on the Mint box and T5 (the plugin) next.

**Goal.** When a sync tool cannot merge two edits, it keeps both and renames one. Syncthing writes `save.sync-conflict-20261126-101500-ABCDEFG.dat`; Dropbox and Nextcloud write `save (conflicted copy 2026-11-26 101500).dat`; ownCloud and older Nextcloud clients write `save_conflict-20261126-101500.dat`. A conflict copy in a shared folder means one device's save lost to another's, and today SaveTether still shows that folder as green "Linked". With an opt-in device setting (off by default), SaveTether looks for conflict copies in each linked shared folder. If it finds any, the row shows a new amber state, **"File conflict"**, and the usual toast follows, in the desktop app and in the Decky plugin.

## Decisions

| # | Decision |
|---|---|
| D1 | **Opt-in, off by default.** A device setting, `detect_sync_conflicts`, like the notifications switch. Set in the app's Settings, with `savetether sync-conflicts on\|off`, and with a toggle in the Decky plugin (owner, 2026-11-26) |
| D2 | **Only flagged when the mapping is otherwise Linked** (owner, 2026-11-26). The other amber states already need the owner first and keep their own word |
| D3 | **Patterns** (owner, 2026-11-26): a file name containing `.sync-conflict-` (Syncthing), `conflicted copy` (Dropbox and current Nextcloud clients) or `_conflict-` (ownCloud and older Nextcloud clients, e.g. `save_conflict-20261126-101500.dat`), ignoring case. Checked in T1: ownCloud's current docs (desktop 5.3 and 6.0, "Manage Synchronisation Conflicts") name the copy with "conflicted copy" as well, which the second pattern covers. `_conflict-` could not be confirmed from a source; it stays for older clients, since nothing else is likely to use that name. OneDrive's `name-PCNAME.ext` and Google Drive's `name (1).ext` are not matched, because games make such names themselves |
| D4 | **Resolved by hand.** The row names the file and opens the shared folder. SaveTether deletes nothing. Once the copies are gone, the next check shows Linked |
| D5 | **Toasts come from the existing rules.** A new amber word is a new problem key in `HealthMonitor` (and the plugin's `monitor.ts`), so it is notified once, and the notifications switch mutes it |

Not in this phase: deleting or merging conflict copies, the OneDrive and Google Drive patterns, and an ignore list.

## Design

Paths are under `src/` unless named otherwise.

**Where it plugs in.**
- `SaveTether.Core/Health/HealthCheck.cs` `Check` is the only place states are decided. It is called once, from `Workspace.Status` (`SaveTether.Core/Operations/Workspace.cs`, the `HealthCheck.Check` call).
- `SaveTether.Gui.Presentation/StateWords.cs` maps a state to a word and a shade. An amber shade goes to the tray and to `OverallStatus.Attention`.
- `SaveTether.Gui.Presentation/HealthMonitor.cs` `Accept`/`Problems` toast when a new `{mappingId}:{word}` key turns amber.
- `SaveTether.Core/Storage/DeviceSettings.cs` holds the device's settings. The new one follows `Notifications`.

**The scan.** It runs only in the `Ok` branch of `Check` (the link is in place, the shared folder is there, there is one seeder), and only when the setting is on. It lists file names only:

```csharp
Directory.EnumerateFiles(target, "*", new EnumerationOptions
{
    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
})
```

and matches `Path.GetFileName` against D3. It does not use `SafeFs.EnumerateTree`: that walk stats each entry and reads `FileInfo.Length`, so a file deleted between the listing and the stat throws, and the whole walk is lost. A listing that only reads is not a delete, move or link, so the `SafeFs` rule does not apply. It does not follow links, as `EnumerateTree` does not. An `IOException` or `UnauthorizedAccessException` that still escapes counts as no conflict this round, as `CliApp.WithSize` handles its walk.

**Effect on the sync tool and on performance.**
- Nothing is opened, locked or written. Only directory entries are read. On Windows the directory handle shares read, write and delete; Linux has no locks. No mtime changes, so the sync tool has nothing to sync. Syncthing's watcher listens for create, write, remove and rename, not reads, so no rescan is triggered.
- A file deleted or renamed during the listing is seen under its old name or not at all. At worst, a conflict copy just deleted is flagged for one more check. Syncthing's temp files (`.syncthing.*.tmp`, `~syncthing~*.tmp`) and SaveTether's `.*.tmp` do not match.
- Listing names does not download OneDrive or Dropbox placeholders.
- Cost: one recursive listing per linked mapping per check. The app checks every 5 minutes (`App.CheckInterval`), and so does the plugin in the background (`CHECK_EVERY_MS`), as well as when its panel opens. This is new work: today the plugin's background checks run without `--sizes` and walk nothing, so with the setting on, the Deck lists every shared folder every 5 minutes, also while a game runs. It stays small, because it reads names only (no stat per file, unlike `--sizes`), and save folders are tens to a few thousand files. It runs only when the owner turns the setting on.
- Known limit: a network share that stops answering in the middle of the listing can stall it, like any other read there. The app's check runs off the UI thread and is never started twice; the plugin's backend kills the CLI on timeout. A share already down is caught earlier, as Broken.

## Tasks

### T1 Core: state, setting, scan (M)
- `HealthCheck.cs`: `HealthState.FileConflict`, `[JsonStringEnumMemberName("file-conflict")]`. `MappingHealth` gains `IReadOnlyList<string>? Conflicts = null` (paths relative to the shared folder, `/`-separated, sorted). `Check(..., bool detectConflicts = false)`: in the `Ok` branch, when on, `ConflictCopies(target)`. If it finds any, the state is `FileConflict` with the detail "The sync tool left a conflict copy in the shared folder, '{first}'. Keep the right one and delete the other." for one copy, and "The sync tool left {n} conflict copies in the shared folder, such as '{first}'. Keep the right ones and delete the others." for more. No `NextStep`: `NextStepKind` has no kind for opening a folder, and the app's row action `OpenSharedFolder` covers it.
- `public static bool IsConflictCopy(string fileName)`: the three patterns of D3.
- `DeviceSettings.cs`: `DetectSyncConflicts => Read().DetectSyncConflicts ?? false`, `SetDetectSyncConflicts(bool)`, `bool? DetectSyncConflicts` on `SettingsFile`.
- `Workspace.Status`: read the setting once and pass it to `Check`.
- Tests: `IsConflictCopy` cases (Syncthing, `conflicted copy`, `_conflict-20261126-101500` true; `save (1).dat`, `save-PC.dat`, `.syncthing.save.dat.tmp` false); a linked temp mapping with a conflict file is `FileConflict` with the setting on and `Ok` with it off; the one-copy and two-copy detail sentences; the setting defaults to off and round-trips.

### T2 Presentation and app (M)
- `StateWords.cs`: `(HealthState.FileConflict, _) => new("File conflict", Shade.Amber)`.
- `AppsPage.cs`, beside `DoubleSeed`: the sentence "Conflict copy from the sync tool: {first}." for one copy, and "Conflict copies from the sync tool: {first} and {n - 1} more." for more, main action `RowAction.OpenSharedFolder`, others `Backups` and `Details`.
- `SettingsScreen.cs`, after Backups: the heading "Sync conflicts", a switch bound to the setting, and a note (texts in `SettingsTexts.cs`). If the other switches trigger a check, this one does too; otherwise the next background check picks it up.
- Tests: the word and the row for `FileConflict`, with one copy and with two; `HealthMonitor` toasts once for a new `FileConflict` and not again on the next check.

### T3 CLI and JSON (S)
- `Output.cs`: `HealthState.FileConflict => "File conflict"`; human `status` lists the conflict paths under the row.
- `savetether sync-conflicts [on|off]`, modelled on `Notifications()` in `CliApp.cs`: no setup, no lock; JSON `{ "sync_conflicts": { "on": bool } }`.
- `JsonContractTests`: the new state value, the `conflicts` field, and the command.

### T4 Docs (S)
- `docs/design/desktop-app-spec.md`: the row in section 5.1's table and the setting in section 11.
- `docs/cli.md` (the command, and `status`), `docs/cli-json.md` (the state, the field, the command).
- `docs/testing/hardware-testing.md`: the test flow under "Exit test". This file's progress, and the implementation plan's section 5d.

### T5 Decky plugin (M; separate repo `SaveTether-Decky`, own branch and PR)
An older plugin already maps an unknown state to amber "Needs attention" with the CLI's sentence, and toasts it (`wordFor` in `src/states.ts`, `src/monitor.ts`). The plugin then warns, but with the wrong word. Changes:
- `src/states.ts` `wordFor`: `case "file-conflict": return { word: "File conflict", shade: "amber" };`, next to `double-seed`.
- `MappingJson`: `conflicts?: string[]`, documented as absent from an older CLI. The card shows `detail` as now; the core sentence already names the first file and the count. `withoutCliHint` leaves it alone, since it holds no `'savetether ` command.
- `tests/states.test.ts`: `["file-conflict", undefined, "File conflict", "amber"]` in the word table; an `overall` case giving "1 app needs attention". A monitor test: ok → file-conflict toasts once and stays quiet on the next check.
- **The toggle,** built like the Mute switch. Like Mute (phase 4.1, point 6), it writes only the device's `settings.json` through the CLI. It touches no saves or links, so the plugin's "cannot harm the setup" promise still holds.
  - `main.py`: `async def sync_conflicts(self, on: bool | None = None)` runs `_run("sync-conflicts", *value, "--json")`, as `notifications` does. A CLI without the command exits 64, which is passed through.
  - `src/index.tsx`: `const syncConflicts = callable<[on?: boolean], BackendResult<{ sync_conflicts?: { on?: boolean } }>>("sync_conflicts")`. Read it in `check()`'s `Promise.all`, next to `notifications()`. `View.conflictCheck?: boolean`, from a `conflictCheckFrom(n)` helper like `mutedFrom`: undefined for an older CLI (exit 64) or a failure, which hides the toggle. `setConflictCheck(on)` works like `setMute`: it shows the new value at once, writes it, falls back to the value read back, then runs a check (`run()`), so the cards show the result straight away.
  - The panel: `<ToggleField label="Detect sync conflicts" description="Warn when the sync tool leaves conflict copies." …>` under Mute, in a `<div data-st-conflicts={…}>` for the harness.
  - Tests: `tests/test_main.py`, `sync_conflicts` reads and sets (`sync-conflicts --json`, `sync-conflicts on --json`, `sync-conflicts off --json`), and exit 64 passes through, as the notifications tests do. `scripts/qam-check.mjs`: check that the toggle is there and flips, as it does for Mute.
- `README.md`: list the state, add the toggle to the feature list, and change "It only reads, apart from the device's notifications switch" to name both switches. Say the toggle is hidden with an older CLI.
- Order: SaveTether's PR (T1 to T4) first, then the plugin's. An older plugin shows the state as amber "Needs attention"; a newer plugin with an older CLI hides the toggle.

## Exit test

On the Mint box, with a throwaway `SAVETETHER_HOME` under `~/sttest`:
1. Link a mapping, then `touch "<shared folder>/save.sync-conflict-20261126-101500-ABC.dat"`.
2. `savetether status` still says Linked (the setting is off).
3. `savetether sync-conflicts on`; `status` says File conflict and lists the file.
4. In the app the row and the tray are amber, and one toast shows.
5. Delete the file; the next check is Linked, and no toast repeats.

On the Deck, in `~/sttest` only: the same, plus the plugin. Turn the setting on with the plugin's toggle (not the CLI), and check that `savetether sync-conflicts` then says on. The QAM card says "File conflict" in amber, and one toast shows (log line `[SaveTether] toast: 1 app needs attention.`).

Verification before each PR: `dotnet build -warnaserror` and `dotnet test`; for T5, the plugin's own tests.

## Progress

- **T1 done** (2026-11-26). `HealthState.FileConflict` (`file-conflict`), `MappingHealth.Conflicts`, `HealthCheck.IsConflictCopy` and the name-only scan in the `Ok` branch. `DeviceSettings.DetectSyncConflicts` (`detect_sync_conflicts`, off by default), read once in `Workspace.Status`. Tests: `tests/SaveTether.Tests/Health/ConflictCopyTests.cs` (the patterns; off, on, one copy, two, deleted again) and `DeviceSettingsTests`. Build with `-warnaserror` and all 1065 tests pass. Not yet run on the Mint box or the Deck.
- Pulled forward from T2 and T3, because the tests that walk every `HealthState` (`ActionTests`, `StatusCheckTests`, `JsonContractTests`) require them: the word in `StateWords`, the row in `AppsPage` (`ConflictSentence`, main action `OpenSharedFolder`), the CLI label in `Output.Describe`, and the state and `conflicts` field in `docs/cli-json.md`. T2 still has the Settings switch and the `HealthMonitor` test; T3 has the `sync-conflicts` command and the conflict paths in the human `status`.
- **T2 done** (2026-11-26). The "Sync conflicts" section in Settings (`SettingsTexts.SyncConflicts`, `DetectSyncConflicts`, `DetectSyncConflictsNote`); the switch asks for a check after saving (`App.CheckNow`, which skips while an operation runs or a check is already running, so the next check then shows it). `HealthMonitorTests`: a File conflict is notified once, and again only after it was resolved. On the Mint box, the branch at `2a6c79a` passed all 1066 tests on Linux, and both binaries built (Native AOT). The app has not been driven there yet.
- **T3 done** (2026-11-26). `savetether sync-conflicts [on|off]` (`SyncConflictsOutput`, JSON `sync_conflicts.on`), sharing one argument parser (`OnOff`) with `notifications`. Human `status` lists the copies under the row. Tests: `SyncConflictsCliTests`; `JsonContractTests` records `sync-conflicts` and `status-file-conflict` in `json-shapes.txt`; `ArgParserTests`.
- **T4 done** (2026-11-26). `docs/cli.md` (the command, the state), `docs/cli-json.md` (the command, the state, `conflicts`), the spec's section 11, the design's 5.5 table, and the test flow in `docs/testing/hardware-testing.md`. Found: the spec has no section 5.1 any more. Sections 5 to 7 (the Apps page and its state table, first-run setup, the operation flow) were deleted by accident in `2df0a86` (#77), so the File conflict row is in the design's 5.5 table instead. Restoring those sections is separate work.
- Build with `-warnaserror` and all 1068 tests pass on Windows. Next: the exit test on the Mint box (CLI and app), then T5 in the plugin's repo.
