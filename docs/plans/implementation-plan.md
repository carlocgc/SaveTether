# SaveTether: implementation and test plan

Status 2026-11-17: phase 1 (M0 to M7) is built and trialled on both devices. Phase 2 is built except for its hardware checks and exit test (section 4). Phases 3 to 5 were reordered on 2026-11-17: the desktop app is phase 3, the Decky plugin phase 4, the public release phase 5. Phase 3.5, definitions from the Ludusavi manifest, was added on 2026-11-19 (section 5a). Phase 3's exit test passed on 2026-11-20 on Windows and the Deck (section 8). Earlier: phase 1 was built and phase 2 planned and not started. Written 2026-11-12, after the design in [save-redirection-design.md](../design/save-redirection-design.md) was merged. The design says *what* and *why*; this says *in what order*, *in which files*, and *how each piece is proven*. Section numbers such as "design 5.3" refer to the design document.

Detail falls off on purpose. Phase 1 was planned to the level of types and test cases and is built. Phase 2 was planned to milestone level (section 4). Phases 3 to 5 are planned to the level of work items and exit tests, and each starts with a short step that turns its outline into the same level of detail.

## 1. Decisions this plan rests on

Settled on 2026-11-12, in addition to the design's section 7.

| Decision | Choice | Notes |
|---|---|---|
| Test framework | xUnit | Approved dependency, test project only. Nothing from it ships |
| CLI argument parsing | Hand-written | No dependency. Help text is written by hand and tested |
| Definition file format (phase 2) | JSON with comments, built-in parser | No third-party dependency. YAML with YamlDotNet was approved here on 2026-11-12 and dropped by the owner on 2026-11-13, before it was built (design 5.2) |
| Branch flow | Feature branches, PRs straight to `main` | A `development` branch is introduced in phase 4, when releases are tagged |
| JSON | `System.Text.Json` with source generation | Built in. Reflection-based serialisation does not work under Native AOT |
| CI | GitHub Actions from the first PR | The repository is public, so hosted Linux ARM64 runners are available **[K, confirm in M0]** |
| Product direction | UI first for people, CLI as the core | Decided by the owner 2026-11-17. The desktop app (window and tray icon) is phase 3; the CLI stays the core for scripts, SSH and LLM agents. Design 5.14 |
| Snapshots and scheduled jobs | Dropped | Decided by the owner 2026-11-17: SaveTether is a redirection tool, not a backup solution. Built in PR #20 and closed unmerged. The backups taken before each operation stay, with retention |
| Decky plugin | Status first | Decided by the owner 2026-11-17: typing in Game Mode is fiddly, so only status and actions that need no input. Design 5.11 |
| Windows installer (phase 3) | Inno Setup 6, build time only | Approved by the owner 2026-11-19. Installed on the CI and release Windows runners (`choco install innosetup` when the image lacks it); nothing of it ships. `build/windows/savetether.iss` |
| GUI toolkit (phase 3) | Avalonia 12, with Native AOT | Approved by the owner 2026-11-17, after a trial (section 5, step 1). The only new runtime dependency, used by the desktop app only; the CLI stays dependency-free. Packages: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, pinned to one version. Design 5.14 |

Still open, and not blocking:

- ~~Which real mapping is the first trial in M7.~~ Settled 2026-11-12: Cemu's save folder (`mlc01/usr/save`), mapping `cemu.save`.
- The two Deck checks from the design. Flatpak reach: checked 2026-11-12 for Xemu, Dolphin, PPSSPP, melonDS and Citra; each has `home` or `host` in its permissions, so each can see `~/Sync/savetether`. A link inside a Flatpak app's folder has still not been tried. Process names: only Cemu's is confirmed in use; the others are still to be read from the Deck when each is mapped.
- ~~The config-mechanism questions.~~ Settled 2026-11-12: config redirects are deferred, and links are the only mechanism. Design 5.13 has the reasons and the trigger for revisiting.

## 2. Conventions for every phase

**Repository layout.**

```
SaveTether.sln
Directory.Build.props        # net10.0, nullable, warnings as errors, invariant globalization
src/SaveTether.Core/         # library: no console, no UI, no process exit
src/SaveTether.Cli/          # argument parsing, calls Core, formats output. Native AOT
tests/SaveTether.Tests/      # xUnit, real temporary directories
docs/design/  docs/plans/
.github/workflows/ci.yml
```

**One PR per milestone step**, small enough to review in one sitting. Each PR must:

1. Build with no warnings on all three CI targets.
2. Add the tests listed for its step, and pass them on all three targets.
3. Leave the delete-audit test (M1) green.
4. Update the design or this plan if behaviour changed.

**Test categories.**

| Category | What it is | Runs |
|---|---|---|
| Unit | Pure logic: validation, classification, plan building, parsing | every CI run |
| Filesystem | Real temporary directories with real links and junctions. No mocks for link behaviour | every CI run, all three OS targets |
| Fault injection | Abort after each journal step, then recover | every CI run |
| End to end | The CLI entry point run in-process against temporary directories, including two simulated devices | every CI run |
| AOT smoke | The published native binary runs `--version` and one `status --json` | every CI run |
| Hardware | Manual checklist on the Windows PC and the Deck (section 8) | before each phase exit |

**Global test rule.** Every test that creates links also creates a canary directory outside its tree and asserts at the end that the canary is intact.

## 3. Phase 1: link-only CLI

Goal: the transaction engine, proven, and usable by hand on both devices. Scope is design 5.12. Eight milestones, in dependency order. Sizes are relative (S, M, L), not time estimates.

### M0. Scaffold and CI (S)

Deliver:
- Solution and three projects as laid out above. `LICENSE` (MIT), `README.md` stub, `.gitignore`, `.editorconfig`, `.gitattributes` with `* text=auto` so line endings stop depending on each machine's git settings.
- `ci.yml`: matrix of `windows-latest`, `ubuntu-latest`, and a hosted Linux ARM64 runner. Steps: restore, build, test, `dotnet publish` Native AOT, run the published binary.
- CLI prints a version and exits 0.

Tests: one trivial unit test, so the pipeline is proven end to end. AOT smoke.

Done when: CI is green on all three targets, including the native binary running on ARM64.

### M1. Safe filesystem layer (L)

The only code allowed to delete, rename or create links (design 5.3, invariant 1). Everything later depends on it.

Deliver, in `Core/FileSystem/`:
- `EntryKind`: Missing, File, Directory, Symlink, Junction, OtherReparsePoint. `SafeFs.Probe(path)` returns it without following a link.
- `ReadLinkTarget`, `CreateDirectoryLink` (junction through P/Invoke on Windows, symlink elsewhere), `RemoveLink` (non-recursive, refuses anything that is not a link).
- `DeleteTree`: probes every entry first; unlinks links, never descends them; clears read-only attributes; refuses a path inside the sync root unless the caller passes the explicit replace-shared capability.
- `RenameDirectory` (same parent only), `CopyTree` (links copied as links, never followed), `EnumerateTree`.
- `PathRules`: containment ("is A inside B") without following the final link, per-OS case rules, detection of names differing only by case.

Tests (filesystem category):
- **Canary matrix.** `DeleteTree`, `RemoveLink`, `CopyTree` and `RenameDirectory`, each against a tree containing: a directory symlink, a junction (Windows), a file symlink, a broken link, a link cycle, a link nested three levels down. The canary survives every one.
- `Probe` returns the right kind for each of those entries.
- `RemoveLink` refuses a real directory. `DeleteTree` refuses a sync-root path without the capability.
- Read-only files, paths over 260 characters (Windows), non-ASCII names, names differing only by case (Linux).
- Junction creation reports `Junction`, not `Symlink`, and its target reads back exactly.
- **Delete audit.** A test scans `src/` and fails if `Directory.Delete`, `File.Delete`, `Directory.Move`, `File.Move`, `CreateSymbolicLink` or `FileSystemInfo.Delete` appear anywhere outside `Core/FileSystem/`. This is how "one delete routine" stays true.

Not covered by CI: GitHub's Windows runners are elevated, so CI cannot prove junctions work *without* privilege. That is checked by running the suite in a normal, non-admin terminal on the Windows PC, and it is on the hardware checklist.

Done when: all of the above pass on three targets, and the non-admin run passes on the Windows PC.

### M2. Backup store (M)

Deliver, in `Core/Backup/`:
- `BackupStore.Create(mappingId, source, reason)`: copies the tree to `<backup_root>/<mapping-id>/<UTC timestamp>/data/`, writes `manifest.json` (relative path, size, SHA-256, source path, reason).
- `Verify(backupId)`: re-reads the copy and compares hashes.
- `List`, and `Prune` (keep newest N, default 5; never the first backup of a mapping).
- Refuses a `backup_root` that is missing or inside the sync root.

Tests:
- Round trip: backup, verify, byte-compare with the source.
- A flipped byte or a missing file in the copy fails `Verify`.
- A failure halfway removes only the partial backup, through `SafeFs`.
- Links inside the source are recorded and copied as links; the canary survives.
- `Prune` keeps the first backup however many follow.
- Both refusals above.

Done when: tests pass on three targets.

### M3. Files and model (M)

Deliver, in `Core/Model/` and `Core/Storage/`:
- Records and source-generated JSON for: `bootstrap.json`, mapping files, device files, seed markers (including `superseded_by`), `state.json`.
- Validation: mapping ID shape, `target` rules (relative, two segments, no `..`, no leading dot, no case-only clash with an existing target), device name shape.
- Schema handling: a file with a newer `schema` than the tool knows is refused, never rewritten.
- Writes go to a temporary name in the same folder, then rename, through `SafeFs`.
- Device identity: install ID generation, takeover of an existing device name, mismatch detection (design 5.6).

Tests (unit, plus filesystem for the writes):
- A validation table: every rule, one accepted and one rejected case each.
- Round trip of every file type; unknown fields are preserved or rejected consistently (decide in this PR and document it).
- Newer schema is refused without touching the file.
- Two "devices" writing their own device and seed files never touch each other's file.
- Takeover without confirmation is refused; install ID mismatch is detected.

Done when: tests pass, and the AOT smoke test reads and writes one of each file (this is where a reflection mistake would show).

### M4. Planner, classification and comparison (L)

Pure decision logic. Nothing in this milestone changes the disk.

Deliver, in `Core/Planning/`:
- `Snapshot`: what the planner sees. S's kind and whether it holds data, T's state, active seed markers, running processes. Built by a reader; injectable in tests.
- `Classifier`: the six ordered rows of design 5.3. First match wins.
- `ChoiceRules`: which of copy, no-copy, add-missing, replace-shared are allowed for a classification, including the `--assume-synced` requirement.
- `Comparison`: by top-level entry, then file (path, size, hash), with newest modification time per side.
- `Prechecks`: every precondition in design 5.3, each returning a named reason.
- `Plan`: an ordered list of typed steps (BackUp, VerifyBackup, CopyIntoTarget, RenameAside, CreateLink, VerifyLink, WriteState, WriteSeedMarker, SupersedeMarkers, DeleteAside). `PlanRenderer` prints it as text or JSON. Dry run is this output and nothing else.
- `ProcessCheck` behind an interface: `/proc` on Linux, process list on Windows.

Tests (unit):
- One test per classification row, plus one per pair of rows that can both match, proving the order.
- Every choice against every classification: allowed ones produce the expected step list, refused ones produce a refusal naming the allowed choices.
- Marker cases: active with T empty (stop); active with T missing some files (links, blocks add-missing and replace-shared without `--assume-synced`); superseded (ignored); two active (double seed).
- Each precondition failing on its own, with its reason. Includes: S inside the sync root, T outside `savetether/`, S's parent missing, one path containing the other, app running, not enough free space, unfinished journal.
- Comparison: only-local, only-shared, differing, identical, and empty sides.
- Add-missing plan never contains a step that writes to an entry T already has.

Done when: tests pass; the planner has no reference to `SafeFs` write methods (checked by a test on the assembly's types, or by code review if that proves awkward).

### M5. Executor, journal and recovery (L)

Deliver, in `Core/Execution/`:
- `Executor`: runs a `Plan` step by step through `SafeFs` and `BackupStore`. Verifies after linking (kind is link, target equals T, listing through S equals T).
- `Journal`: intent written and flushed before each step, done written after. One open transaction at most.
- `Recovery`: rolls an unfinished transaction back per design 5.3. Data already copied into T is left and reported.
- `Revert` flow, and `backup restore` as a transaction that first backs up what it overwrites.
- A fault hook (an interface the tests implement) that throws after a chosen step.

Tests (filesystem and fault injection):
- **Fault matrix.** For each choice (copy, no-copy, add-missing, replace-shared) and for revert and restore: abort after each step in turn, run recovery, then assert (a) every file that existed before still exists, byte for byte, in S, T or the backup store, (b) S is either the original folder or a correct link, never missing, (c) no `*.savetether-old` or `*.savetether-new` is left without being reported, (d) the canary is intact.
- Round trip: apply then revert leaves S byte-identical to the start, and T untouched.
- Apply twice is a no-op the second time (row 1).
- Replace-shared: T is backed up locally first; other devices' markers gain `superseded_by`; none is deleted.
- While a journal entry is open, every operation except recovery refuses.
- Losing `state.json` and re-running records the existing link again.
- Linux: a link whose target is on another filesystem (tmpfs) still applies and reverts.

Done when: the whole matrix passes on three targets.

As built (differences from the list above, and decisions made while building):
- Revert and restore are planned like apply: `Planner.PlanRevert` and `RestorePlanner` produce the same kind of step list, so their dry runs and their journal entries use one code path. Three step kinds were added for them (copy-to-staging, remove-link, rename-into-place).
- The fault hook has two points per step: after the intent is journalled, and after the step ran but before "done" is journalled. The matrix crashes at both, for eight scenarios (the four choices, a link-only apply, revert, and restore of a local and of a shared folder).
- When recovery undoes and when it finishes an operation is recorded in design 5.3, with the restore rules in 5.12.
- Not done here: recreating links recorded in a backup when it is restored. A restore lists them instead. Moved to M6.
- Not covered by a test: a crash in the middle of a single step's copy or delete. The matrix interrupts between steps. The partial states that matter (a half-made junction, an aside folder whose original reappeared) have their own tests.

### M6. CLI (M)

Deliver, in `Cli/`:
- `ArgParser`: commands, flags, values, `--` handling, unknown-flag errors. No dependency.
- Commands: `init`, `map add`, `map remove`, `status`, `apply` (with `--all`), `revert`, `backup list`, `backup restore`, `recover`, `--version`, `--help`.
- Output: text by default, one JSON object with `--json`. Every JSON object carries a `schema` number.
- Confirmation: interactive prompt in text mode; in `--json` mode or without a terminal the tool never prompts and requires the explicit flag (`--confirm-replace`, `--assume-synced`, `--assume-closed`, `--take-over`).
- Exit codes 0 to 3 as in design 5.12.
- Carried over from M5: `backup restore` recreates the links recorded in a backup's manifest (local folders only), instead of only listing them.

Tests (end to end, in-process):
- Parser table: every command and flag, plus malformed input.
- Each command's exit code for success, refusal, rolled-back failure and recovery-needed.
- JSON output for each command checked against a stored expected shape, so an accidental change is caught before the Decky plugin depends on it.
- No prompt is ever issued in `--json` mode.
- **Two-device scenarios**, with two local-config folders and two "sync roots" that a test helper copies between, so "not yet delivered" can be staged:
  1. Windows `--copy`, deliver, Deck with data stops at the guard, `--copy` refused, `--no-copy` links.
  2. Same, `--add-missing`: only the missing entries arrive; existing ones are untouched.
  3. Deck runs before delivery of data but after the marker: stop (row 3).
  4. Both `--copy` before any delivery: both markers arrive, `status` says Double seed, `--replace-shared` clears it.
  5. Wipe: delete the Deck's local config, state and links; `init --device deck --take-over`, `apply --all`; every mapping links with no question.
  6. `--no-copy` into an empty T prints the "app will see no saves" warning.

Done when: tests pass on three targets and the AOT binary runs scenario 1 from a shell script in CI.

As built (differences from the list above, and decisions made while building):
- The commands' logic is in the core (`Core/Operations/Workspace.cs`, `Core/Health/HealthCheck.cs`); the CLI project holds the parser, the dispatch, the prompts and the output. The JSON object's shape per command is stored in `tests/SaveTether.Tests/Cli/json-shapes.txt`; an intended change is accepted by running the tests once with `SAVETETHER_UPDATE_SHAPES=1`.
- Usage errors exit 64. The other rules settled here (when the tool asks, what refuses while an operation is unfinished, where local files live) are in design 5.12.
- Found by scenario 4: `apply --replace-shared` did nothing on a device that was already linked, so it could not clear a double seed. It now does, without touching saves; design 5.3 describes it and the longer route for when the other device's files won. Both are tested.
- A first `--copy` is confirmed only at a terminal. There is no flag for it in non-interactive use; it goes ahead with the warning.
- The shell scenario is `tests/e2e/scenario1.sh`, run by CI against the published binary on all three targets.
- Rollback and recovery exit codes are tested through a fault hook passed in the CLI's test environment, not by provoking real disk errors.

### M7. Real-device trial (S, manual)

1. Windows PC, non-admin terminal: full test suite, then a throwaway folder mapping: apply, status, revert. *Done 2026-11-12; see the Windows entries in section 8.*
2. Run the two Deck checks. Copy the Linux binary from the CI artifact to `~/.local/bin`. *Done 2026-11-12, binary from the CI run of `a3cbf74`.*
3. One real, low-stakes mapping: `init --create-root` and `apply --copy` on Windows; after the sync tool delivers, `init` and `apply` on the Deck. *Done 2026-11-12 with `cemu.save`: `E:\LaunchBox\Emulators\CEMU\Cemu\mlc01\usr\save` with sync root `E:\CloudSaveEmulation` on Windows, `~/.local/share/Cemu/mlc01/usr/save` with sync root `~/Sync` on the Deck.*
4. Play on each device once and confirm the save crosses over. *Done 2026-11-12, both directions.*
5. `revert` and re-apply on one device. *Done 2026-11-12, on the Deck.*
6. Simulated wipe on the Deck (scenario 5). *Done 2026-11-12: local folder moved aside, link removed, empty save folder created; `init --take-over` and `apply --all` restored it with no questions.*

Steps 2 to 6 were run by the owner on the two devices and reported as working; no output was captured.

Found during the trial, for phase 2:
- **`--add-missing` does not merge per game for Cemu.** It works on the top-level entries of the save folder (design 5.3), and Cemu's are `00050000`, `00050010` and `system`, with the games one level down. Both devices have all three, so nothing would be added. Mapping `usr/save/00050000` would give one entry per game. App definitions need a way to say at which depth an app's per-game entries sit.
- The design's Windows inventory (appendix B) lists Cemu's `settings.xml` as holding the sync folder path. On 2026-11-12 its `mlc_path` was empty and Cemu was using its own `mlc01`.

**Phase 1 exit:** all of the above done, `status` clean on both devices, and the hardware checklist (section 8) ticked.

Status 2026-11-12: M0 to M7 are done and `status` was clean on both devices. Four Deck checklist items in section 8 are not ticked (running over SSH, a link inside a Flatpak app's folder, a source on the SD card, and the sync root unmounted), so the exit condition as written is not fully met.

## 4. Phase 2: entry depth, definitions, resolver

Goal: stop typing absolute paths. Exit test: a device restored after its SD card ID or install folder changed, with no hand edits.

Planned to milestone level on 2026-11-12, after the M7 trial. Eight milestones in dependency order, numbered P2.0 to P2.7. Sizes are relative (S, M, L). The conventions of section 2 apply unchanged: one PR per milestone, all three CI targets, the delete audit green.

Two things were part of this phase and were dropped: config redirects on 2026-11-12 (design 5.13), and YAML definitions with their dependency and Native AOT spike on 2026-11-13 (design 5.2). One ordering is deliberate:

- **By-hand first, definitions second.** P2.2 makes entry depth usable from `map add` with typed paths, as phase 1 did for links, and it fixes something the trial found. Definitions and the resolver (P2.3 and P2.4) then remove the typing.

### P2.0 Close out phase 1 on hardware (S, manual)

The four Deck items section 8 still has open. None needs new code.

1. Run the binary over SSH.
2. One mapping for a Flatpak emulator, so a link sits inside `~/.var/app/<id>/` and the app is seen to load a save through it.
3. One mapping whose save folder is on the SD card, with the sync root on internal storage.
4. Rename the sync root, run `status` and `apply`, and confirm both refuse; rename it back.

Done when: section 8 is ticked for phase 1 and dated. Item 2 should come before any Flatpak emulator is mapped for real.

Done 2026-11-17: all four ticked in section 8. Item 4 led to a change: `status` now reports instead of refusing when the sync root is unavailable (PR #25).

### P2.1 Specify entry depth; settle config redirects (S, design)

Design 5.13. Entry depth is specified there. Config redirects, which this plan first scheduled for phase 2, are deferred: the owner decided on 2026-11-12 that links cover the apps in question and that editing app config files is not worth its cost and risk yet. 5.13 records the reasons, what to do by hand for apps whose setting was changed by hand, and when to revisit.

Done when: the owner has confirmed 5.13.

### P2.2 Entry depth (S)

Deliver:
- Mapping file `schema: 2` with `entry_depth` (default 1). A schema 1 file is still read, as depth 1.
- `Comparison` and the add-missing plan work on entries at that depth. `CopyIntoTarget` takes entry paths, not only top-level names, and creates parent folders.
- `map add --entry-depth <n>`, which may also change the depth of an existing mapping (design 5.13).

Tests:
- Comparison at depth 1, 2 and 3: an entry present on one side only, on both and identical, on both and different; a file sitting above the entry depth is its own entry.
- Add-missing at depth 2 on a Cemu-shaped tree adds one game and writes nothing into a game the shared copy already has. Names differing only by case still count as present.
- A schema 1 mapping file reads as depth 1; a schema 3 file is refused.
- Two-device scenario 2 repeated at depth 2.

Done when: tests pass on three targets, and the existing `cemu.save` mapping is set to depth 2 and `--add-missing` is seen to work per game on the real devices.

As built (differences from the list above, and decisions made while building):
- A mapping at depth 1 is still written as `schema: 1`, with no `entry_depth`. Only a mapping that uses a depth above 1 is written as `schema: 2`. Devices on an older SaveTether therefore keep working until a deeper mapping is actually created. Design 5.13 says what stops working for them then.
- Entries are paths, so `CopyIntoTarget` and the journal carry values such as `00050000/101c9500`. A plan at depth 1 is unchanged.
- A folder that is empty on one side and holds entries on the other is not compared as an entry itself; only what is inside it is.
- Where letter case matters (Linux), an entry whose parent folder exists in the shared copy under a name differing only by case is refused, with a message naming both, instead of creating a second folder beside it.
- Before copying anything, the executor checks every entry it is about to add against the shared folder, ignoring case, so a game the sync tool delivered after planning stops the copy before it starts.
- JSON output of `map add` and `map remove` gained `entry_depth` and `entry_depth_changed`. Nothing was removed or renamed.
- Not yet done: setting the real `cemu.save` mapping to depth 2 on the two devices. That is the owner's step, after both have the new binary.

### P2.3 Definitions (M)

Deliver: the loader for the format in design 5.2 (JSON with comments and trailing commas allowed, through the built-in parser and source generation, so it works under Native AOT like the other files), a JSON Schema file, and validation errors that name the file and line.

Tests: every example in design 5.2 loads, comments included; a table of invalid files, each with its expected message; an unknown field is an error; the self-test loads one definition, so CI proves it in the native binary on three targets.

As built (differences from the list above, and decisions made while building):
- The loader does not use source generation. It reads the file with the built-in tokenizer into a small tree that keeps line numbers, then validates that by hand. The built-in document model keeps no positions, and "name the file and line" needs them. No reflection is involved, so it runs in the native binary; the self-test proves it there.
- All problems in a file are reported together, in line order, not only the first.
- A test loads every `jsonc` block in design 5.2, so the examples in the design cannot drift from what the loader accepts.
- Found by that test: the PPSSPP and Final Fantasy IX examples had role targets (`PSP/SAVEDATA`, `.`) that could never become valid mapping targets, which are exactly `<app>/<save folder>`. A role's target is now one folder name, and the examples are corrected.
- The JSON Schema is `definitions/definition.schema.json`, kept in step with the loader by a test. No definition files are shipped yet; where they live and how the tool finds them is settled with `app add` (P2.5).
- Tokens inside paths are not validated here. That belongs to the resolver (P2.4).

### P2.4 Tokens and resolver (L)

Deliver: `{home}`, `{appdata}`, `{localappdata}`, `{locallow}`, `{documents}`, `{xdg_*}`, `{flatpak:id}`, `{install_dir}`, `{sd_card}`, `{emudeck}`, `{steam_compatdata:id}`; `winpath` with `steam`, `steam_shortcut`, `lutris` and `path` prefixes.

Tests: fixture directory trees and fixture `libraryfolders.vdf`, `shortcuts.vdf` and Lutris config files, including a game on a second Steam library and a missing SD card. No real Steam needed.

Split on 2026-11-17 into two PRs, so each stays reviewable:

- **P2.4a** tokens and winpath: every token except `{steam_compatdata}`, and winpath with a `path` prefix or a prefix set for the device.
- **P2.4b** Steam and Lutris: `libraryfolders.vdf`, `{steam_compatdata}`, the `steam` and `steam_shortcut` prefixes (binary `shortcuts.vdf`), and the `lutris` prefix.

As built, P2.4a (differences from the list above, and decisions made while building):
- `PathResolver` in `SaveTether.Core.Resolution`, with a `ResolverEnvironment` that tests point at fixture trees. Rules are in design 5.2, "Resolving paths".
- A result is a path or a reason, one of: invalid, not on this OS, needs a value, missing, ambiguous, not yet supported. Nothing is guessed: two SD cards or two Wine users are reported.
- `{steam_compatdata}` and the `steam`, `steam_shortcut` and `lutris` prefixes return "not yet supported" until P2.4b. A Wine prefix set for the device works for all of them meanwhile.
- `{documents}` and the other Windows folders are refused on Linux outside a winpath, rather than mapped to `~/Documents`. No definition needs them there yet.
- Values set per device (`{install_dir}`, SD card, Wine prefix) are passed in; storing them is P2.5.
- A test checks that every path in the design's example definitions is a valid template on both systems.
- The self-test resolves a role and `{home}`, so the native binary proves the resolver on three targets.
- `{emudeck}` reads `emulationPath` from EmuDeck's `settings.sh`; that location is an inference until seen on the Deck.

As built, P2.4b:
- `SteamLibraries` reads `libraryfolders.vdf` and `shortcuts.vdf`; `LutrisGames` reads Lutris game configs. Both are fed only from files below the home folder, so the tests build fixture homes. Rules are in design 5.2, "Resolving paths".
- A small reader for Valve's KeyValues, text and binary (`Vdf`), written here rather than taken as a dependency. Binary 32-bit integers are read unsigned, as Steam uses shortcut app IDs.
- Lutris configs are YAML; only the `game:` section's own `prefix:` line is read, so phase 2 still adds no dependency.
- Fixtures cover a game on a second library (the SD card case), a game not listed but with a compatdata folder, a game listed twice, the pre-2021 `libraryfolders.vdf`, Flatpak Steam, a broken `libraryfolders.vdf`, a shortcut before its first run, two shortcuts with one name, and Lutris configs for other slugs, without a prefix, or in two files that disagree.
- Checked on the owner's Deck over SSH on 2026-11-17, with `app add --dry-run` and a throwaway definition, sync root and local folder (removed afterwards): `{emudeck}`, `{sd_card}`, `{steam_compatdata}` and the `steam` prefix (game 1426210, through `libraryfolders.vdf` with a second library on the SD card), `{flatpak:...}` and the `lutris` prefix (Flatpak Lutris) all resolved to the right folders. The `steam_shortcut` prefix read the binary `shortcuts.vdf` and found the shortcut's app ID; none of the shortcuts has a Proton prefix, so that last step was seen to refuse correctly but not to succeed.
- Found on the Deck and fixed: EmuDeck writes `emulationPath="/run/media/deck/<uuid>"/Emulation`, with quotes around part of the value, which `{emudeck}` could not read; and Flatpak Lutris keeps its game configs in `~/.var/app/net.lutris.Lutris/config/lutris/games`, which was not searched. Also found: `init` failed when `.savetether` existed without its `devices` folder; shared files now create their folder.
- Steam is reached through `~/.steam/steam`, a link to `~/.local/share/Steam`; paths are reported through that link. Native Lutris is still unchecked.

### P2.5 `app add` (M)

Deliver: add an app from a definition. Resolve its install, show the roles with their sizes, write the mapping and device entries.

Tests: end to end against fixture trees; an ambiguous install asks, never guesses; with `--json` it refuses instead of asking.

Decided by the owner on 2026-11-17 before building: definitions are built in and shared (shared wins); `app add` records how it found the app in the device file; Cemu is the only shipped definition.

As built (differences from the list above, and decisions made while building):
- Commands `app add` and `app list`. `app add --dry-run` shows the plan and writes nothing; it is allowed while an operation is unfinished, like `status`.
- Re-running `app add` is how a moved install or a changed SD card is picked up: remembered values fill in missing options, the install used last time breaks a tie, and a save folder that is not applied moves to where the app is found now. One that is applied must be reverted first, unless the link is no longer at the old place because the folder moved with it. This is the route to the phase 2 exit test; no separate "refresh" command was added.
- The device file is `schema: 2` only once it has `apps`.
- An existing mapping's entry depth and a device's process names are kept, not overwritten from the definition. Definitions have no process names yet, so `apply` cannot tell that an app added this way is running until `map add --process` is used.
- Steam detection works on Linux only. On Windows an install with a `steam` detect rule must be named with `--install`.
- `definitions/cemu.jsonc` is the design's Cemu example; a test checks every file in `definitions/` is built in and loads.

### P2.6 Flatpak precheck (S)

Deliver: read the app's permissions; refuse with the exact `flatpak override --user` command when T is unreachable.

Tests: parser tests on captured `flatpak info --show-permissions` output, including the five emulators read on 2026-11-12; live check on the Deck.

As built (differences from the list above, and decisions made while building):
- The output read on 2026-11-12 was not kept. The fixtures are instead the Flathub builds' own permissions for the same five emulators, read on 2026-11-17 with `flatpak remote-info --show-metadata flathub <id>` on Mint; the `[Context]` group has the same format as `flatpak info --show-permissions`, which was confirmed on two apps installed there.
- **Found:** as Flathub ships them, Xemu, Dolphin, PPSSPP and Citra have `host:ro`, which is read-only, and only melonDS has `home`. A link into the sync folder would let those four read saves but not write them on a fresh install.
- **Read on the Deck, 2026-11-17** (`flatpak info --show-permissions`, over SSH): every emulator there has a user override of `host` in `~/.local/share/flatpak/overrides/`, apparently from EmuDeck, which replaces `host:ro`. Xemu, Dolphin, PPSSPP, melonDS, Citra, RetroArch, MAME and Supermodel can write `~/Sync`. RPCS3 has `host` and `home:ro` together; the check takes the more specific entry, so it refuses RPCS3 until it is given `~/Sync` (or the `savetether` folder) explicitly. Which entry Flatpak itself lets win there was not tested; this reading can only refuse where it might have allowed. These permission lines are test fixtures.
- Where several entries cover T, the most specific one decides (a deeper mount is placed over a shallower one).
- The check runs only for `apply`, only when S is inside `~/.var/app/<id>/`, and only on Linux. It needs write access to T as the link names it and, when a link in that path leads elsewhere (`~/Sync` to `/data/Sync`), to where it leads.
- The fix command grants the `savetether` folder, not just T, so one override covers every mapping of that app. Paths are quoted for the shell.
- Permissions that cannot be read (no `flatpak` command, app not installed) add a note to the plan instead of refusing.
- Added on 2026-11-17 at the owner's request: SaveTether can make the grant itself. When the sandbox is the only precheck that fails, apply asks at a terminal, or goes ahead with `--grant-sandbox`; with neither it refuses as before, and a dry run never grants. The grant is the `savetether` folder only (never `host`, which EmuDeck uses but which opens the whole filesystem), for this user, and the permissions are read again before linking. `revert` offers to take the grant back once no other applied mapping links from inside that app; without a terminal it prints the `--nofilesystem` command instead. Only a grant of exactly the `savetether` folder is treated as SaveTether's, so EmuDeck's overrides are never offered for removal. No state is kept: the grant is found in `flatpak override --user --show`.
- Covered entry forms: `host` (except the folders the sandbox keeps its own), `home`, `~/...`, absolute paths, `xdg-config`, `xdg-data`, `xdg-cache` and the user folders (`xdg-documents` and so on, read from `user-dirs.dirs`), each with `:ro`, `:rw` or `:create`, and `!entry` removing an entry. Anything else is treated as not covering T, which can only refuse, never wrongly allow.
- Live check on Mint (2026-11-17) with the installed Discord Flatpak: apply refused and printed the override command; nothing was changed. Not yet checked on the Deck.

### P2.7 Backup retention (S)

Originally "Snapshots and the login-time check": on-demand and scheduled copies of T to the backup store, and a Task Scheduler entry and systemd user timer running `status`. Built in PR #20, verified on real systemd (Mint) and Task Scheduler (Windows), and closed unmerged on 2026-11-17 by the owner's decision: SaveTether is a redirection tool, so there are no snapshots, and the desktop app's tray icon (phase 3) replaces a scheduled check. The branch `feat/p2-snapshots` is kept for reference.

What remains, built in PR #21: the backups taken before each apply, revert and restore had no retention. The rule in design 5.12 (keep a mapping's first backup and the newest five) existed in `BackupStore.Prune` but was never called. It now runs after each completed operation. Found while testing it on Linux and fixed: a new backup could take the ID of one pruning had just removed.

**Phase 2 exit:** a device restored after its SD card ID or install folder changed, with no hand edits, and the phase 2 lines of the hardware checklist ticked.

Met 2026-11-17 on the install folder side (section 8, Windows PC), which also found a way to lose shared saves on Windows and led to PR #26. An SD card ID change was not tried on hardware; it takes the same `app add` path, with `--sd-card` in place of `--install-dir`, and is covered by the resolver tests. The phase 2 checklist lines are ticked except one, deferred by the owner: a game under Proton using a link (section 8).

**Gaps found on hardware, 2026-11-17.** None blocks a first CLI release; each is a later change.

- A definition cannot detect one Lutris game, only the Lutris app (`detect: { "flatpak": "net.lutris.Lutris" }`). A detect rule for a Lutris game would let `app add` say "not installed" before resolving the prefix.
- There is no token for a Steam game's install folder (`steamapps/common/<installdir>`), where mods and textures usually live. Only the Proton prefix can be named. Until then, such a folder needs `--install-dir`.
- Definitions carry no process names, so `app add` sets none; `map add <id> --process <name>` was needed for each mapping (`eden`, `Ron2.exe`, `Ron2`). A per-install `processes` field would fix it.
- Eden keeps a `cache/` folder inside `nand/user/save`, which syncs with the saves and is written by every device. The definition should leave it out, which needs either a narrower role or the sync-tool ignore list (design 5.2, `ignore`).
- *(Fixed 2026-11-21: `map delete` removes a mapping no device uses, with a backup first, and the app has a red Delete for it. Deck run, 2026-11-21, two throwaway devices on one share: the row of a mapping nobody uses had Add and a red Delete, one another device used had no Delete; Cancel changed nothing; Delete took the backup (2 files, in Backups as "before deleting", on the Deck and not in the sync folder), removed the shared folder, its app folder, the markers and the record, and the row went; the CLI refused the other device's mapping by name. The real setup was unchanged. The design settled after review as: no files are set aside while deleting; the app and the interactive CLI ask the owner to make sure all writes to the role's shared folder are complete and the sync tool has finished, and `--confirm-delete` asserts it; see the progress entry "Apps page and Apply all, follow-up". Runs of the final build are recorded there.)* No command deleted a shared mapping. `map remove` drops only this device's entry, so a mapping no device uses stays in every `status` as "Not applied". Renaming `rondo2.flash` to `flashplayer.data` on 2026-11-17 needed the mapping file, its seed marker, its shared folder and the definition deleted by hand; the `apps` record for `rondo2` stays in each device file, since only a hand edit could remove it.
- A Windows save took about four minutes to reach the Deck, through the Unraid server. That is the sync tool's pace, not SaveTether's, but it is why `apply` checks for an unfinished sync.

## 5. Phase 3: desktop app

Added on 2026-11-17 (design 5.14). Goal: a person who has never used a terminal can set up a device, add an app, apply it and see at a glance that it stays healthy, on Windows and Linux.

Starts with two steps before any app code:
1. **Toolkit decision** by the owner. *Done 2026-11-17: Avalonia approved after the trial below.* A Game Mode check was planned here and dropped the same day: the owner decided the app is for the desktop only, which on the Deck means Desktop Mode (design 5.14).
2. **Specification** of the app's screens and states to the level of design 5.12, including every refusal the CLI can give and how the window shows it. *Done 2026-11-17 in `docs/design/desktop-app-spec.md`; the owner accepted all eleven proposed decisions (its section 15). Its section 13 adds seven small core and CLI changes to come before or with the app, the first being core messages without CLI wording.*

Toolkit trial, 2026-11-17. A throwaway window-and-tray app on the branch `spike/avalonia-tray` (`spikes/AvaloniaTray`, not in the solution; removed since, and its README's findings are below). Avalonia 12.1.5, .NET 10, Native AOT:

| | Windows 11 | Linux Mint 22.3, Cinnamon, X11 | Steam Deck, Desktop Mode, KDE Plasma, Wayland |
|---|---|---|---|
| Native AOT build | no warnings | no warnings | the Mint build, no missing libraries |
| Window | shown | shown | shown, through XWayland |
| Tray icon | created | registered with the desktop's StatusNotifierWatcher while running, gone after quitting | registered with KDE's StatusNotifierWatcher |
| Green to amber, clean exit | yes | yes | yes |
| Size, no debug symbols | 34.7 MB | 31.7 MB | same as Mint |

- The app is four files, not one: the executable plus native Skia and HarfBuzz (and ANGLE on Windows). Acceptable in the release archive; the CLI is unaffected at about 6 MB.
- On the Deck's Wayland desktop Avalonia runs through XWayland. Over SSH it needs the session's `XAUTHORITY`; started from the desktop or by autostart it has it.
- Checked by the runs: the window was created and shown, and the tray item registered with each desktop's tray host. Not yet checked: how it looks on each screen. Game Mode is not a target (design 5.14), so the Game Mode check planned here was dropped. The trial binary at `~/sttest-spike` on the Deck and the branch `spike/avalonia-tray` (last commit `e1da00b`) were removed on 2026-11-17; the findings above are what they recorded.
- Known limits taken into account in design 5.14: the tray icon's own menu is a plain system menu (text, separators, ticks), so anything richer is our own window; where the tray icon is on screen is not reliably known on Linux, so a click opens the main window rather than a panel beside the icon; desktop notifications need the small per-platform code (`notify-send`, a PowerShell toast); bindings must be compiled bindings for Native AOT.
- Alternatives considered: Tauri (small and web-styled, but a second language and toolchain, all actions through the CLI's JSON, and WebKitGTK unconfirmed on SteamOS), Eto.Forms (native widgets, less styling freedom, GTK and AOT on SteamOS unproven), Uno Platform (heavier for a small app), Photino (no tray), Electron (too large). .NET MAUI has no Linux desktop and WPF and WinForms are Windows only.

| Item | Deliver | Tests |
|---|---|---|
| JSON contract freeze | Document every `--json` output and input flag as version 1; additive changes only after this. Moved here from the Decky phase, because scripts and agents use the CLI now | The stored-shape tests from M6 become the contract tests |
| Operation lock | A lock file in the local folder held for the length of an operation; a second process is refused. Used by the CLI and the app | Two processes started together: one runs, one is refused, nothing is half done; a lock left by a killed process does not block forever |
| App shell | One process, one copy at a time; tray icon green or amber from the 5.5 states; health re-checked every few minutes, on open and after each operation; a notification when the state turns amber | State-to-colour table tests; the notification fires once per change, not per check |
| First-run setup | Choose the sync folder, name the device, create the shared folder on the first device only | Same refusals as `init`; scripted against fixture folders |
| Apps and actions | Each app's roles with health and the matching action; Apply with the comparison and only the allowed choices; Revert; Recover; the Flatpak grant | View-model tests against fixture devices; each planner refusal kind is shown |
| Add app | A guided `app add`: installs found, roles with sizes, fields only for values that cannot be found | The `app add` test cases, driven through the view model |
| Backups | List and restore | Restore of a shared-folder backup asks for the deliberate confirmation |
| Start with the session | Opt-in autostart entry (XDG `.desktop`, `HKCU\...\Run`), re-written with the current path at each start; `status` warns when it points to a missing binary | Generated entries compared with expected text; install and remove on real hardware |
| Release archives | Both binaries in each platform's archive; the app offers to move them to the per-user location | Archive contents checked in CI |

Progress:
- *Operation lock: done 2026-11-17 (PR #36), with the other core changes of the app spec's section 13, items 1 to 4 and 6 to 7 (PRs #33 to #38). Item 5 waits for the app to write autostart entries.*
- *JSON contract freeze: done 2026-11-18 in `docs/cli-json.md`. `JsonContractTests` checks the output against `json-shapes.txt`: a recorded path that goes or changes type fails as a breaking change, and a new one fails until it is recorded and documented. Further tests check that every recorded field, every enum value the output can hold and every exit code is named in the document. The recordings now also cover `app add`, `app list`, a precondition with its facts, a store conflict and status with the sync root unavailable.*
- *App shell: built 2026-11-18. `SaveTether.Gui.Presentation` (no Avalonia, tested) holds section 3's overall state, 5.1's state words, the health monitor (no overlapping checks, one notification per new problem, none while another process holds the operation lock, "Health check failed" after three failures in a row), `gui.log` capped at 1 MB, and the single-instance claim. `savetether-gui` (Avalonia 12.1.5, Native AOT) has the tray icon and menu, a check every 5 minutes, on showing the window, on opening the menu and on "Check now", notifications, close-to-tray, and a window with the header, the device-wide banners and each mapping's state. New in the core: `LocalStore.DefaultFolder` (shared with the CLI), `OperationLock.HeldByAnotherProcess`, which reads only, and `SafeFs.DeleteSocket`. CI now publishes the app with Native AOT on all three targets. Avalonia's build telemetry is turned off (`Directory.Build.targets`). On hardware: see section 8, phase 3.*
- *First-run setup: built 2026-11-18 (spec section 6, "As built"). Tested against fixture folders: each finding of step 1, nothing written before Set up, the first device only after its confirmation, take-over with the device file's backup location, a location that is not there noted and not created, and refusals placed at their step with nothing written. Step 1 seen on Mint.*
  - *On the Deck, by the owner's leave, driven with `xdotool`: its `bootstrap.json` was set aside and setup run against the real `~/Sync`. The first run crashed on step 2. The Windows PC's device file holds `E:\...` paths, which the core refused as not absolute on Linux, and the exception ended the app. Fixed: a device file's paths are checked as absolute on either system (`PathRules.IsAbsoluteOnAnySystem`); one unreadable device file is listed with its problem instead of hiding the others; and an unexpected exception on the UI thread is logged and shown while the app keeps running (spec section 10).*
  - *The second run took over `deck`: the app showed all six mappings Linked, `status` was identical to before, and in the shared `deck.json` only `install_id` changed.*
  - *It also found that Escape did not reach the dialogs and Cancel was not focused, since disabling the page left focus on nothing. Fixed and seen working on a first-device setup with a throwaway folder, done by keyboard alone. And `gui.log` began with a byte-order mark; fixed.*
- *Apps and actions, first part: built 2026-11-18 (spec section 7, "As built").*
  - *What it covers: Apply (refusals with only the actions each allows, the choice dialog with exactly the allowed choices, the replace confirmation, the Flatpak grant, the plan's confirmation with its steps), Revert (with the offer to take back a Flatpak grant), Recover from banner B2, progress and results.*
  - *Tests: every refusal kind, precondition, step, result and setup choice has an app text (a new value without one fails); the choices equal `AllowedChoices` for the first-device and guard cases; and Apply, Revert and Recover run through a session against fixture devices, with the lock refusing the CLI meanwhile.*
  - *On the Deck, with a throwaway device driven by `xdotool`: Apply went through the app-running refusal, "the app is closed", the choice dialog, the confirmation with its steps and the result, leaving the link and the shared copy on disk. Revert left a real folder with the saves. Recover, after a CLI apply killed part way, undid it with the files intact. Two wordings were fixed from this: the app-running sentence named the role instead of the app, and recovery's result read as a failure.*
  - *Still to come: the other row actions, Apply all, Restore and Add app.*
- *Apps page: built 2026-11-18 (spec sections 5 and 9.4, "As built").*
  - *What it covers: rows grouped by app with attention first; section 5.1's sentences, including "Set up on <devices>" and a foreign link's target; More with Open folder, Open shared folder, their parents, Stop using here and Details; "Not on this device (n)" collapsed; and Apply all.*
  - *Tests: the 5.1 table for every state and reason, the grouping and order, Apply all's planning against a fixture device, and Stop using here refused while linked and done when ready.*
  - *On the Deck, with throwaway devices: the page and its More menu, Open folder (Dolphin opened at the folder), Apply all (linked the row that needed no choice and named the one that did), and Stop using here (the folder was forgotten, its files kept).*
  - *Fixed from the run: Apply all was hidden with one row left; the collapsed section closed at each check; and its rows did not name their app.*
- *Apps page and Apply all, follow-up: built 2026-11-21 (PRs 78 and 79, and the clean-up after them).*
  - *What changed: operations run behind a busy dialog and lock the page (Apply, Apply all, Revert, Restore, Delete backup); the Scan button is Cancel while a scan or the footer's lines run; a rescan keeps its earlier list until it finishes; the footer reads "Scanning for saves…" with no count; Backups and Settings have their titles above the scrolling area; app names are a tinted band; imported apps show their name; Apply all asks one question for every mapping with saves only on this device (Share these saves, Start empty, Leave them); a row says what its folder is (Symlink, Junction or Original); "Not on this device" rows lose the state column; Stop using here is renamed Remove.*
  - *Tests: `StatusPacerTests`, `FoundListTests` (a cancelled, finished and failed rescan), `AppsPageTests` (a mixed batch for both answers, and a mapping that stops being a first setup while the question is open), `AppNames`.*
  - *On the Deck, 2026-11-21, throwaway device under `~/sttest`, driven by `xdotool`, build of `78bc2f2`: Apply all with two mappings that had saves only on the Deck and one with saves already shared. One question named the two; Share linked both, put the saves in the shared folder and backed them up first, and left the third untouched; Leave them linked nothing and named all three in the summary; Start empty linked both to empty shared folders with the saves in the backups only. The confirmation listed each with its answer. The real setup's `status` was identical before and after.*
  - *On the Deck, 2026-11-21, second run (throwaway device, build of `e4e948a`, `xdotool`): the page title, the app bands and the readable name of an imported app; Remove (confirmation, the row moved to "Not on this device"); an imported app's Add opened page 2 (it reported not found: the game is not installed on the Deck, so its ticked saves were not seen); Add app opened with the default catalog and the scan button on the one row; the scan showed Cancel and "Scanning for games…" while the count held, a second press skipped the footer's wait, five quick presses queued nothing; the Backups and Settings headers; the Apply confirmation locked the page. The busy dialog did not show (every step was under its 250 ms delay). The real setup's `status` was unchanged. Found: a long app name in "Not on this device" was cut off with no gap before the sentence (fixed in the next change).*
  - *On the Deck, 2026-11-21, with a throwaway share (builds of `61ca3ba` and `e1f4650`, `xdotool`): twelve other devices on one mapping. The not-here sentences and buttons lined up, Add was right-most with Delete to its left, the sentence said "12 devices have a link." and hovering it listed all twelve names, one per line (an earlier wording that named three devices and had a Devices button wrapped to two lines or broke names mid-word, and was dropped for this). Delete of the final build: the CLI without the flag (no terminal) was refused with the message saying to confirm only once writes are complete and the sync tool has finished; `--confirm-delete` on a mapping another device used was refused by naming that device; the dialog gave the title and the two sentences; Cancel changed nothing; Delete took a backup (three files, one in a nested folder), removed the shared folder, the markers and the record, and left the other mapping's folder, markers and record and the shared app folder; no staging folder appeared anywhere; Backups listed the copy as "before deleting". The real setup's `status` was identical before and after.*
  - *On this Windows PC, 2026-11-21, build of `2ad6ee2` (a normal build of `development`, a throwaway local folder and sync folder, driven with Windows UI Automation, never touching the real setup): the scan took about a second; the count under the search field held at "18 found" while the footer said "Scanning for saves…", then changed to "71 found" as the footer went to Idle; the plan page for Final Fantasy IX (an imported game with Steam installed) had the title, the found-at line and Back beside Add above the scrolling folder list; Add recorded the mapping and asked "Apply it now?" (Not now: nothing was applied, so no real saves were touched); the row said "Ready to link" and "Original"; More offered Open folder and Remove; Remove moved the row to "Sync folders ignored by this device (1)" with a red Delete and Add right-most; Add on that imported-app row opened the plan page with "Share save-2" ticked (the imported-app routing fix), not the hand form; Delete of a mapping whose shared folder did not exist worked (record only); a mapping applied to a throwaway folder showed "Linked" and "Junction" (the folder is a junction); after revert and Remove its Delete took a `pre-delete` backup of two files, emptied the share and removed the record, with the local save folder intact and no staging folder beside the sync folder.*
  - *Not yet run on a device: the busy dialog (every step finished under its 250 ms delay on the Deck and here); the hover list with more than one device on Windows, and on a touch screen, which has no hover; Apply all on Windows; the scan Cancel button on Windows (the scan finished in about a second).*
- *Add app: built 2026-11-18 (spec section 8, "As built"), without "Add a folder by hand".*
  - *What it covers: page 1 with the catalog, a search and Found / Not found / Added filled in the background; page 2 for each `app add` outcome (the plan with role checkboxes, sizes, notes and quirks; the install choice; location fields; conflicts and definitions with mistakes); Add, which plans again under the lock and writes only the plan shown; "Apply it now?", which runs the Apply flow for each role added; and the rows' Find the app again and Add.*
  - *Tests: `AddAppFlowTests` drives the view model through the `app add` cases against fixture devices: the catalog, defaults and ticking, two installs, a needed value, a moved install, a conflict, a definition with mistakes, the lock held, and a plan that changed before Add.*
  - *On the Deck, with a throwaway device and a shared test definition, driven by `xdotool`: page 1 and its search; not found with the Install folder field; the plan page; Add, then Apply with the choice dialog and confirmation, leaving the link and the shared copy on disk; then the install folder renamed, Folder gone with Find the app again, the remembered folder prefilled, the new one given, "already a link", Add, and the row Linked again. The real setup was unchanged.*
  - *Fixed from the run: Use folder read the text boxes off the UI thread, which Avalonia refuses, and the error vanished with the page left disabled (the role checkboxes had the same flaw); a needed value was worded with the core's `{install_dir}`; a ticked role still said "Off:".*
  - *Afterwards, found by the owner: an app was still "Added" after Stop using here on all its folders; it now counts as added only while it has a folder on this device.*
- *Backups: built 2026-11-18 (spec section 9.7, "As built").*
  - *What it covers: the Backups page from the Apps page and from a row's More, grouped by mapping, newest first, with reason, shared copy or this device, size and the kept first backup; the app filter; Open folder; Restore of either kind, with the shared copy's confirmation, and Revert offered when the folder is linked.*
  - *Tests: `BackupsPageTests` against a device whose apply replaced a shared copy: the grouping and marks, a restore refused while linked with Revert offered, then done after the revert with what was there backed up, and a shared-copy restore needing its confirmation.*
  - *On the Deck, with a throwaway device driven by `xdotool`: the page; Restore of this device's backup while linked, refused with Revert; the revert, after which the list showed its new backup; the restore, which put the file back; and the shared-copy restore through its confirmation, which put the shared file back. The real setup was unchanged.*
  - *Fixed from the run: a local restore's confirmation said twice that a backup is taken first.*
- *Backup delete: added 2026-11-18 at the owner's request, in the core, the CLI (`backup delete`, `--confirm-delete`, `backup_deleted` in `--json`) and the Backups page (spec section 9.7, "As built").*
  - *Tests: `BackupDeleteTests` (refused without a person or the flag; the prompt, with the first-backup warning, deleting only on yes; `--json`; an unknown ID; refused while an operation is unfinished, in the CLI and in the core), and `BackupsPageTests` for the app's words and the delete through a session. The JSON contract records `backup-delete`.*
  - *On the Deck, with a throwaway device: the CLI refused without a terminal or the flag, and at a terminal warned about the first backup, cancelled on no and deleted on yes. In the app, Delete on the first backup showed the stronger warning and its own button; Cancel kept it; Delete on a later one removed its folder and the list was read again. The real setup was unchanged.*
  - *Also fixed: the lock test picked a protected Windows process (audiodg) whose start time can be read but whose exit state cannot, which the core counts as the holder. It failed on the PC after audiodg restarted; it now picks a process that answers both.*
- *Start with the session: built 2026-11-18 (spec section 11, "As built"), with the app spec's section 13 item 5.*
  - *What it covers: the core writes, reads and removes the entry (XDG desktop entry on Linux, the HKCU Run value on Windows); `status` reports `autostart_missing` and warns; the app's Settings page has the switch and About; the entry is rewritten with the app's path at each start while on; banner B5 with Fix it and Turn it off.*
  - *Tests: the entries compared with expected text; the program read back from each, including a Linux path with spaces, quotes, `$` and a backslash; the Linux store written, rewritten and removed, and a link in its place refused; the Windows store under a throwaway key; `status` with and without a stale entry, recorded in the JSON contract; the switch, the rewrite at start and B5 in `SessionStartTests`.*
  - *On the Deck, with a throwaway device and `XDG_CONFIG_HOME` in the scratch folder: the switch wrote the entry with the binary's path; `status` warned about a stale entry in text and `--json`; B5 appeared after Check now; Fix it, a restart and Turn it off each did what they say. The real autostart folder and setup were unchanged.*
  - *Not yet: a real login starting the app (Deck or Windows), and the switch on Windows, where the tests cover the registry code under a test key only.*
- *The end of the session no longer vetoed: 2026-11-18, found by the owner (spec section 2, "As built"). Windows named SaveTether as preventing a restart: the app cancelled the window's close to hide to the tray, also when Windows was ending the session. Tests: `WindowCloseTests`. On Windows, with a throwaway local folder: after X, `WM_QUERYENDSESSION` sent to each of the app's windows was vetoed by Avalonia's message window before the fix and answered "ok to end" after it, and `WM_ENDSESSION` then ended the process; X and minimise behave as before. A real restart with the fixed build, the same evening, started from the Debug build in the startup entry: `gui.log` said "session ending: quitting", Windows logged no veto, and the app started again at login without crashing. Also reported: after starting with the session on Windows, the X and minimise buttons and the taskbar's Close seemed to do nothing. Not reproduced by the agent (started normally or with `--minimized`), nor by the owner on the latest build after two restarts; left open, to be reported again if it comes back.*
- *Release archives, first part: 2026-11-18 (`docs/releasing.md`). Each platform's package holds the CLI, the app with its native libraries, README and LICENSE, without symbols, built by `build/package.sh` and checked against the exact file list by `build/check-package.sh` in CI on every pull request and in the release workflow.*
  - *Checked: the check fails on a missing library and on a stray file. The Windows package, unpacked, ran the CLI and the app, with its own Skia rather than the one on `PATH` that crashes the intermediate exe. The Linux package, built on the Mint box and unpacked on the Deck, kept both programs executable; the CLI reported its version and the app opened its window from it.*
  - *Not yet: a prerelease tag through the whole release workflow. Installing: built 2026-11-19, below.*
  - *v0.2.0 released 2026-11-19 from these packages: a manual run of the release workflow first (nothing published), then the tag. Its packages passed on Windows, the Mint box and the Deck; arm64 in CI only.*
- *A network sync folder refused on Windows at setup: 2026-11-19, asked for by the owner (spec section 6.1, "As built"). A junction cannot point to a network drive, so `apply` already refused one; `init` and the app's setup accepted it. `PathRules.IsOnNetworkDrive` is now the one check, used by both and by `apply`. It decides from the path and the drive's type alone. Before this, `init`'s device listing reached for an unreachable share and stalled for about 4 seconds. Also fixed: the old check took the long form of a local path (`\\?\C:\...`) for a network path. Tests: `NetworkDriveTests`, with `\\server\share` paths that need no network. On the PC: `init` on a mapped NAS drive letter was refused at once, with nothing written on the share or locally. `docs/sharing.md` describes the ways to share the folder (sync tool, cloud folder, network share on Linux, one machine, removable drive), with what is tested.*
- *Add a folder by hand: built 2026-11-19 (spec section 8, "As built"), with section 5.1's row actions for mappings with no definition.*
  - *What it covers: the link at the bottom of Add app's page 1; the form (the folder to share, which may hold saves, mods or anything else; app name; a name for the shared folder; process names; entry depth under Advanced) with the mapping ID and shared folder worked out from the two names; join mode for a mapping another device made by hand, from its row's Add or Choose the folder again; the question before sharing a folder another device already shares under the same names; Add under the lock, then "Apply it now?".*
  - *Tests: `ManualAddFlowTests` (the names to an ID and a shared folder, including accented letters; the name following the folder as typed; each field's problem; Add writing the mapping and the device's folder and linking nothing; the lock held and a core refusal; join mode by the row and by the same names, keeping the entry depth; a clash of letter case), and the row actions in `AppsPageTests` and `AddAppFlowTests`.*
  - *On the Deck, with two throwaway devices sharing one throwaway sync folder, driven by `xdotool`: the link and the form; a folder added by hand, then Apply through the running-app refusal, the choice dialog and the confirmation, leaving the link and the shared copy; the other device's hand mapping joined from its row, with an empty folder refused at the field first; its folder moved away after applying, and Choose the folder again recording the new one. The real setup was unchanged.*
  - *Fixed from the run: the saves' name stopped following the folder after its first characters; and opened from a row, the folder field had no focus, so Return added the old folder unchanged.*
- *Process picker: built 2026-11-19 at the owner's request (spec section 8, "As built"). "Choose running app" beside the hand form's process names, listing what the running-app check sees. Tests: `ProcessChoicesTests` (Linux's two names per process, cut-off names, kernel threads and Wine paths reduced to one name each; Windows names differing in case; the search; adding a name once). On the Deck, with a throwaway device: the list, the search box focused, "plasma" narrowing it, a double click putting `plasmashell` in the field, and Apply then refusing because `plasmashell` runs. Not tried: picking an emulator or a game under Proton, which would have meant starting one on the owner's Deck.*
- *A network folder to link refused, and a failed apply's copy undone: 2026-11-19, found by the owner. A folder on a mapped network drive, added by hand on Windows, was copied into the shared copy by Apply, which then failed to make the junction ("Access is denied") and reported "undone", while the copied files stayed in the shared folder and the sync tool took them to every device. Now `map add`, `app add` (`not-local`), the hand form and Apply's prechecks (`source-not-local`) refuse a folder on a network drive on Windows before anything is written; and a failed apply removes from the shared folder what it copied there while still identical to this device's, and a first-device copy's shared folder once empty (design 5.3, invariant 2's second exception), saying "except in the shared folder" when something had to stay. Tests: the planner precheck; `map add` and the form with `\\server\share` paths (Windows only, as the paths are told apart from their text); a copy rolled back with no shared folder left; one where a copied file changed and another arrived, which both stay and are named; add-missing removing only what it added; recovery after a crash after the copy; and the CLI's rollback and recovery scenarios. Not tried on hardware: a real network drive, which would write to the owner's NAS.*
- *The way back at the top: 2026-11-19, asked for by the owner (spec section 5, "As built"). Back and Back to apps were at the bottom of Add app, its hand form, Backups and Settings, and scrolled with the page. The window now has a bar above the scrolling area holding the Apps page's buttons, or the open page's way back. On the Deck, with a throwaway device: each page's way back in the place of the Apps page's buttons, still in view with the hand form scrolled to its end, and each returning where it should. The real setup was unchanged.*
- *Installing: built 2026-11-19 (spec section 11, "As built"). First as an app that installs itself on both systems; then, decided by the owner, a conventional installer on Windows and the self-install with an uninstall on Linux. Other decisions: the Linux layout (`~/.local/lib/savetether`, the CLI and a link in `~/.local/bin`), a Linux menu entry, offering at any start until declined, and offering to update an older install.*
  - *Tests: `InstallerTests` (the layout; version order; nothing offered from an incomplete package or from the installed copy; install and update putting each program in place, the link, the menu entry and executable bits, no leftovers; uninstall removing only what install put there and the autostart entry for it, and leaving the owner's files; the CLI's confirmation; `SafeFs.ReplaceFile` refusing a folder; the offer, its "Not now" per folder and the update offer; the installer's file list equal to `build/check-package.sh`'s), the single-instance quit message in `ProcessAndLogTests`, and `uninstall` in the JSON contract. `build/check-installer.ps1` installs the Windows installer silently with the `Path` task, checks the files, the Start menu and Settings > Apps entries and the `Path`, uninstalls, and checks all is gone and `Path` is exactly as before; then again with the folder already on `Path` by hand, which must stay. CI runs it on every pull request.*
  - *On the owner's PC, with leave, into a throwaway folder (`/DIR=`), since the owner's own CLI is in the default one: both rounds of `check-installer.ps1`; and 0.2.0 installed and running, upgraded by a 0.2.1 installer, which closed it through the Restart Manager. Afterwards no install, Start menu or Settings > Apps entry, marker or Run value was left, the `Path` was identical, and the owner's CLI folder was untouched. Found: the first script removed the folder from `Path` on uninstall even when it had been there before; it now removes only what it added.*
  - *On the Deck, with a throwaway `HOME` and local folder: the offer from an unpacked 0.2.0; Install, after which the installed copy ran from `~/.local/lib/savetether` with the link, the CLI and the menu entry in place; an unpacked 0.2.1 offering the update while 0.2.0 ran, closing it, replacing the files with nothing left over and starting 0.2.1; a second start of the same version showing the running window; "Not now" starting the app as usual and not asking again from that folder; Settings > Uninstall with starting with the session on, removing everything including the autostart entry and quitting, with the setup kept; and `savetether uninstall`, refused without the flag and no terminal, then removing everything. The real `~/.local` and setup were unchanged. Fixed from the runs: the offer named the folder with a trailing separator.*
  - *On screen, 2026-11-19, into a throwaway folder: the wizard's pages (licence, the `Path` task unticked, ready, finished), and SaveTether started from its Start menu entry against a throwaway local folder. Found: the wizard still showed Inno Setup's own pictures; it now shows the app's icon (spec section 3, "As built"). Afterwards nothing was left and `Path` was identical.*
- *The header when nothing is linked: 2026-11-20, found by the owner (spec section 3, "As built"). With every link reverted the app said "All linked". It stays green, since nothing needs the owner, but now counts the folders ready to link: "6 ready to link", or "4 linked, 2 ready to link"; "Not on this device" is not counted. Test: `StatusCheckTests` (none, one of two, and all linked, with another device's mapping beside them). On Windows, with a throwaway device of two folders: the header and tray said "1 linked, 1 ready to link" with one applied, and "All linked" with both.*
- *The app's icon: 2026-11-19 (spec section 3, "As built"). The owner chose a "linked floppy" from three concepts. `build/render-icon.cs` draws it with SkiaSharp (already brought by Avalonia) into the `.ico`, PNGs and SVG in `src/SaveTether.Gui/Assets`, with a simpler drawing at 24 pixels and under. The exe, the installer, the windows and the taskbar show it; the tray shows it with a green or amber badge; the Linux menu entry names the installed PNG. Checked on Windows: the icon read back from the published `savetether-gui.exe` and from the installer, and the running app's window icons (16 and 24 pixels, from the simpler drawing). On the Deck, with a throwaway `HOME`: the title bars of the install offer and the main window, the menu entry's `Icon=` with the PNG beside the app, and the tray icon read from KDE's StatusNotifierItem over D-Bus, amber and then green. On screen, 2026-11-19: on Windows the title bar, the taskbar button, and the tray icon with its amber badge (in the hidden icons, at 16 pixels: the badge reads); on the Deck, with the owner's leave, installed into the real `~/.local` and found by KRunner under Applications with the icon, then uninstalled with the CLI and the owner's own CLI put back (the real setup unchanged). Found: `savetether uninstall` left a running installed copy running from its removed files, until it was quit or the session ended. Fixed: it now asks the copy to quit first and names one that did not (spec section 11, "As built"); tests in `InstallerTests`; on the Deck, with a throwaway `HOME`, the installed copy ran, the CLI's uninstall ended it, and the real `~/.local` was unchanged.*
- *The app's icon above the title of `README.md`: 2026-11-19, asked for by the owner. The wizard's pictures are PNG (one 7 KB side picture, and `savetether.png` in the corner) rather than a BMP per display scale (1.4 MB), so the installer needs Inno Setup 6.5.2 or newer.*
- *Adding an app whose folders are already linked: 2026-11-19, found by the owner with Cemu (spec section 8, "As built"). Adding again an app added and applied before said "Nothing is linked yet" and offered Apply for every folder. Add app now tells the folders that are already links from the rest, as `app add` does: Apply is offered only for the rest, and with none left the dialog says there is nothing to apply. Test: `AddAppFlowTests`, partly and fully linked. On the Deck, with a throwaway device and a shared definition of two roles: with one role applied, Add said "Already linked here: testapp.saves" and Apply opened the confirmation for `states` only; with both applied, "Test App added" with OK alone. The real setup was unchanged.*
- *Add app begins with the installs: 2026-11-21, asked for by the owner (spec section 8, "As built"). Re-adding Cemu after Remove went straight to the plan with the stored install folder and no way to change it, and giving a second folder asked for the folder first and the choice after. Add app now opens on "Choose an install of <app>." (found installs with their paths, and the install folder one shown as `<install folder>/mlc01/usr/save`), then the folder page with "Select the folder containing ..." for that one, then the plan; Back returns to the install page. Test: `AddAppFlowTests`, the choose page, the folder page, Back, and the install used last time not skipping the page. On the Mint box (2026-11-21, throwaway device and home): Cemu with a Flatpak and a native install listed both with their paths; a known one went to the plan with no folder field and Back returned to the list; a hand-written Test Emu listed `known` and `portable` as `<install folder>/mlc01/usr/save`, the portable one went to the folder page with the stored folder filled in and the hint, and Use folder made the plan. The real Flatpak Cemu was found read-only by `app add cemu --dry-run`. The real setup was unchanged. The missing-folder warning on the Mint box (2026-11-21, throwaway device): a portable folder holding `mlc01` but no `usr/save` showed the amber warning above "Folders to share" with the folder "not there yet"; the known install, which has saves, showed none. That first version warned when no device had shared the mapping, and on the owner's Windows PC it stayed silent (the Deck had added `cemu.save`), so it now warns when the save folder's parent does not exist, which is what Apply refuses on. Mint re-run (2026-11-21, throwaway devices, the shared `testemu.save` already added by a second device as the Deck did for Cemu): a portable folder with `mlc01` and no `usr` showed "The folder .../mlc01/usr does not exist, so Apply will refuse to link save."; with `usr` made the warning was gone and the save folder said "not there yet". On the Deck (2026-11-21, throwaway device and home under `~/sttest`; the real setup was unchanged): the chooser, folder page, plan and the missing-folder warning laid out well at the default window size. A found path of 100 characters (an SD card's length) was cut off at the window's edge on the chooser, so it now wraps; checked again on the Deck. On the Windows PC (2026-11-22, owner, a debug build of the merged branch with a Cemu folder holding a fake `Documents\mlc01`): the flow was run and the owner found it fine; `win-documents` has not been tried on a real non-portable Cemu install.*
- *Readable progress and dialogs: 2026-11-18, asked for by the owner (spec section 9.3, "As built"). The step list ticks at least 300 ms apart and holds the finished list 800 ms; dialogs answer only after 600 ms. Tests: `PacedProgressTests` with a clock that moves on request. On the Deck, with a throwaway device: a Cancel clicked at once was ignored and one clicked a second later closed the dialog; captures during an apply showed two steps ticked, then seven, then the result.*

- *Too-broad save folder refused: 2026-11-22, found by an audit. `apply` refuses a source that is the home folder, holds it, or is a drive root (`source-unusable`; test in `PrecheckAndComparisonTests`). The definitions workflow's PR body now lists the changed definition lines, since the bundle is gzip. Not run on hardware; the workflow is untested until its next monthly run.*
- *Autostart and menu `Exec=` lines write `%` as `%%`, so a percent sign in the install path is not read as a field code: 2026-11-22, found by an audit. Tests in `AutostartTests`. Not checked by launching from a Linux desktop.*
- *Choose the folder myself: 2026-11-23, found by the owner on Mint with PPSSPP (spec section 5, "As built"). A definition's app not where the definition looks could not be added from its row: Add ended at "No install of PPSSPP was found" with no field, since no built-in definition has a Linux install that asks for an install folder (all 23 that use one are Windows installs). Its rows in "Sync folders ignored by this device" and Folder gone now have Choose the folder myself under More, opening the form for a folder by hand for that mapping. Test: `AppsPageTests`. On the Mint box (throwaway devices `pc` and `mint-test` on one throwaway sync folder): the first build drew More over Add in the not-here row; fixed, they sit side by side; More listed the action and it opened "Folder for ppsspp.savedata", set up on pc, shared as `ppsspp/SAVEDATA`. Typing is not possible there, so the form's Add was done as `map add ppsspp.savedata --source` and `apply --no-copy`: pc's save appeared. The real setup was unchanged. Not done: the form filled in and added on screen (the Deck can type), and Linux portable installs in the definitions, which would ask for the folder within Add app.*
Exit test: on Windows and on Linux, a new user sets up a device, adds an app and applies it without the CLI; the tray icon turns amber when the sync root is unmounted and green again when it returns. *Passed 2026-11-20 on `5d53e2c`; see section 8.*

## 5a. Phase 3.5: definitions from the Ludusavi manifest

Added 2026-11-19 by the owner. Goal: Add app and `app add` know most PC games that keep saves in a folder, without anyone checking each game by hand, and the emulators the manifest lacks have verified definitions of our own. Starts once phase 3's open items are done: "Add a folder by hand", the offer to move the programs to the per-user location, and phase 3's exit test.

Everything else for this phase is in [phase-3.5-ludusavi-definitions.md](phase-3.5-ludusavi-definitions.md): the trial of 2026-11-19, the owner's decisions, the conversion rules, tasks T1 to T9, and the progress of each task.

Exit test: on Windows and on the Deck, a game that has no hand-written definition is found by Add app, near the top when it was played recently, applied, and its saves load on the other device.

**More emulator definitions, 2026-11-22** (issue 108, branch `feature/emulator-definitions`). Twenty-two built-in definitions: Dolphin, PrimeHack, PCSX2, PPSSPP, RetroArch, RPCS3, Vita3K, shadPS4, Xenia Canary, Eden, Citron, yuzu, Lichii, Ryujinx, ES-DE, ScummVM, Flycast, MAME, Supermodel, Model 2 Emulator, Mesen 2 and Flash Player. Eight of them (Dolphin, PCSX2, PPSSPP, RetroArch, RPCS3, Ryujinx, Vita3K, yuzu) take over a Ludusavi entry that listed saves only; the built-in one adds states, BIOS, mods, textures and media as roles that are off by default, and leaves each device's settings out. Each file's header names the source files its folders were read from, and EmuDeck's script for that emulator, read on the Deck.
- *Decisions:* role IDs and targets match the owner's hand mappings on the Deck (`eden.saves`, `eden.nand-system`, `ppsspp.savedata`, `.states`, `.shots`, `.textures`, `flashplayer.data`), so adding the app later reuses them. Eden, Citron and yuzu share `nand/system` whole (profiles, system saves, firmware), as the owner does, rather than the profiles folder alone. A role that saves depend on is on by default with them: saves are found by profile ID, so Eden, Citron and yuzu's `nand-system` is on, and so is Ryujinx's `system` (its `Profiles.json`, and the keys). Ryujinx's three save folders are all on too: the save index in `bis/system/save` names the numbered folders in `bis/user/save`. In Visual Studio the definition files appear under `Definitions/Shipped` in the core project (`LinkBase`); on disk they stay in `definitions/`. shadPS4's current saves are in `home/<user>/savedata`; the older `savedata` folder is a role that is off by default. Flash Player is Windows-only for now: a detect rule cannot look inside a Wine prefix, so on Linux it is still added by hand.
- *Left out:* Redream (its VMU and flash saves are files beside redream.exe, not a folder), TeknoParrot (each game keeps its saves in its own folder), Neko Project 21/W (the saves are the disk images themselves), mGBA (saves sit beside the ROM), RMG (on Linux its saves go to the data folder itself), and the Flatpaks of Ryujinx, yuzu, Eden and Citron (IDs not checked).
- *Verified:* `dotnet build -warnaserror`; `dotnet test` (987 passed); the built-in test now also refuses a role folder inside another role's. On the Deck, with a CLI built on Mint from the branch and a throwaway `SAVETETHER_HOME`, `app add --dry-run` for each app: Dolphin, PrimeHack, PPSSPP and RetroArch (Flatpak), RPCS3 and Citron (EmuDeck), shadPS4, ES-DE and MAME found their folders; PCSX2, Eden and yuzu were found twice (native and EmuDeck) and asked which to use, and `--install emudeck` resolved the EmuDeck folders. The real setup's `status` was the same before and after. On the Windows PC, with a debug CLI and a throwaway `SAVETETHER_HOME`, `app add --install-dir --dry-run` against the owner's portable copies under LaunchBox: Lichii, Supermodel, Model 2, Eden, Citron, yuzu, Ryujinx, PCSX2, PPSSPP, Dolphin, RPCS3, RetroArch, MAME, DuckStation, Xenia Canary (inside Xenia Manager's Emulators folder) and shadPS4 (the Qt launcher's `user/home/<user>/savedata`) found their folders; Eden, PPSSPP and DuckStation were already links. Vita3K's `pref-path` points at its own folder, which led to the `custom-pref-path` install; that folder is on a network drive there, which SaveTether refuses. *Not run:* any apply, the Windows AppData and Documents installs, and ScummVM, Flycast and Mesen 2 anywhere.

**Typed guards on replacing shared saves, 2026-11-23**, asked for by the owner. Replacing the shared data when applying ("Replace in the sync folder") needs `replace shared data` typed, and restoring a backup of the shared copy needs `restore shared data`, like every other deletion of saves. "Use this device's copy" stays a plain confirm: it deletes no files. The typed dialog is the last one before the run (after the choice and the plan's confirmation), as for every text-guarded action; it is asked for any plan with a `ClearTarget` step. The override of the may-still-be-arriving refusal ("The shared saves really were deleted") stays a plain confirm, but the typed dialog of a replace that follows it adds that files another device shared are not in this device's backup.
- *Verified:* `dotnet build -warnaserror` (Release), the phrase test. *Tested 2026-11-23 on the Mint box* (scratch folders, the app in a nested X display; the real `mint` status unchanged before and after): a wrong phrase leaves the button disabled and Enter does nothing; Cancel leaves the shared folder untouched; the right phrase replaces it with a backup of the shared copy taken first. *Found:* restoring a shared-copy backup asks up front (`RestoreAsync`) and skipped the refusal path the first build guarded, so it showed a plain confirm; fixed. Then the order was changed (typed dialog last) and both flows were re-run on Mint through to the result: apply replace gave the plan dialog, then the phrase, then the run; restore of a shared-copy backup the same. Not run on the Deck.

**Shorter dialogs, other devices only when there are some, 2026-11-23**, asked for by the owner (desktop-app-spec.md, principle 8). A plan that empties the shared folder (replace, restore of the shared copy) has one dialog, the confirmation with the typed phrase in it, instead of a confirmation and then the phrase. Remove another device and Clean up's Delete all are one dialog each. The pre-lock "Use this device's copy" dialog, and the confirmations after "<app> is closed" and "They were deleted on purpose", are gone: their warnings are in the refusal, and the plan's confirmation still follows. The backup is said once per flow. The core's notes the app restates are named (`Planner.RevertLeavesSharedNote` and three more) and left out of the confirmation; other notes still show. `OperationSession.HasOtherDevices` (another device file in the sync folder; true if it cannot be read) decides whether a dialog names other devices; dialogs that need a second device say "if you use a sync tool". The vocabulary is sync folder, shared folder, shared copy, this device's data. The CLI's texts are unchanged.
- *Verified:* `dotnet build -warnaserror`; `dotnet test` (992 passed) on Windows and on the Mint box. *Tested 2026-11-23 on the Mint box* (scratch homes under `~/sttest/trim`, the app in Xephyr, the owner's running copy left alone): with one device, Apply with data on both sides showed the choice with no other-device line, then one dialog "Replace the shared copy of game saves?" with the phrase box; typing `replace shared data` ran it, and the shared folder then held this device's files. With a second device registered by `init`, Restore of the shared-copy backup showed one dialog with "Other devices that use the sync folder get this change too.", and restored the shared copy. Revert showed its own sentence once, without the core's note. Delete on an unused mapping was not run in the app (unit tests only). *Tested 2026-11-23 on the Deck* (a Mint build of the branch, scratch `SAVETETHER_HOME` under `~/sttest`, the real setup untouched): the replace dialog showed "Type replace shared data to confirm." in the lighter red, readable on the dark dialog; once the phrase matched the hint cleared and **Replace** turned red; Escape left the shared copy as it was.

## 5b. Phase 3.6: device removal, reset and maintenance

Added 2026-11-21 by the owner; planned 2026-11-22. Goal: a device can remove itself from the sync folder (sold, decommissioned or set up again), the last device can delete everything SaveTether keeps there, a device can be renamed, old backups can be cleared, and a Maintenance screen shows what is registered. After a removal the app starts as on first launch.

Everything else for this phase is in [phase-3.6-device-maintenance.md](phase-3.6-device-maintenance.md): the owner's decisions, the design of each flow, the app's screens and dialogs, tasks T1 to T10 with a hardware check for each, and the progress of each task.

Exit test: on a throwaway folder shared by the Deck and the Windows PC, rename the Deck, remove it (the PC is untouched; the Deck keeps ordinary save folders and opens on setup), clear the PC's backups but the first, reset the PC with "Delete everything" (no `savetether/` left), then set both up again from scratch.

Progress: not started.

## 6. Phase 4: Decky plugin

Goal, revised 2026-11-17: status in Game Mode, with only the actions that need no input (design 5.11). Starts with a short design step against the then-current Decky API.

| Item | Deliver | Tests |
|---|---|---|
| Plugin backend | Python: runs the CLI, passes JSON through. No logic | Unit tests with a fake CLI executable: success, each exit code, timeout, binary missing |
| Plugin frontend | Health list sorted with problems first; Recover; Apply only when the planner needs no choice. No full-screen pages, no text entry | Component tests against recorded JSON; manual Game Mode checklist |
| Detection | Moved to phase 3.5 on 2026-11-19 (section 5a) | |
| Ludusavi hints | Replaced on 2026-11-19 by phase 3.5's imported definitions, converted in CI (section 5a) | |

Exit test: every mapping's status readable in Game Mode with the controller; Recover and a choice-free Apply work from there.

## 7. Phase 5: public release

| Item | Deliver | Tests |
|---|---|---|
| Name check | GitHub, NuGet, Scoop, winget, trademark search | Recorded in the design before anything is published under the name |
| Release workflow | Tag-triggered build of three targets, checksums, release notes. `development` branch introduced. Brought forward by the owner on 2026-11-17, for a v0.1.0 of the CLI after phase 2 (`docs/releasing.md`) | A prerelease tag produces installable artifacts; checksums verify |
| Signing | Apply to SignPath Foundation; sign Windows binaries | Signature verifies on a clean Windows machine |
| Packages | Scoop and winget manifests | Install, run, upgrade and uninstall on a clean Windows machine |
| Definitions repository | Split out, schema check in its CI, "tested on" line required | A deliberately broken definition fails its CI |
| Upgrade safety | A newer tool reads older files; an older tool refuses newer ones | Fixture sets of every released schema version, kept forever |
| Docs | README, install, first-run walkthrough, CONTRIBUTING, SECURITY | A person who has not seen the project follows the walkthrough on one device |

Exit test: first outside definition merged.

## 8. Hardware checklist

Run before each phase exit. Record the date and tool version in the PR that closes the phase.

**Windows PC**
- [x] Test suite passes in a non-admin terminal (junctions without privilege). *2026-11-12, 0.1.0 at `a3cbf74`: 328 passed, not elevated, Developer Mode off.*
- [x] Apply and revert on a folder on `E:` with the sync root on `E:`, and on a folder on `C:` (cross-volume junction). *2026-11-12. Same volume: the real `cemu.save` mapping, folder and sync root both on `E:`, applied here; its revert and re-apply were done on the Deck, not on this PC. Throwaway folders with the native binary: applied and reverted with both on `C:`, and with the folder on `C:` and the sync root on `G:` (cross-volume).*
- [x] Apply is refused while the app is running. *2026-11-12: refused against a running process name (`explorer`), and went ahead with `--assume-closed`. Not yet tried with a real emulator's process name.*
- [x] Phase 2 exit test: an install folder moves; `app add --install-dir` and `apply --all` bring it back with no hand edits. *2026-11-17, native build of `3c5d84e`, a throwaway device, sync root and Eden folder with the shared Eden definition. Same drive (C: to C:): the junctions moved with the folder, `app add` saw them as links, `apply --all` recorded them, status OK. To another drive with Explorer: the junctions were left behind and nothing was copied through them; `app add` and `apply --all` linked the new folder, shared saves intact. To another drive with PowerShell `Move-Item` and with `robocopy /MOVE`: both copied the shared saves through the junctions into real folders, then **deleted them through the junctions**, emptying the shared folder. SaveTether refused to apply afterwards, but advised waiting for the sync; it now says what probably happened and how to recover (design 5.2, "When an app's folder moves").*
- [x] Phase 3.5 exit test, with the Deck: a game with no hand-written definition is found, applied on both devices, and its saves load on each. *2026-11-22, 0.3.0-rc.2, the real setups, at the owner's request: Stellar Blade (`stellar-blade-3489700`, imported). Details in the phase file, T9.*
- [x] Phase 3.6 exit test: rename, remove the Deck, clear backups, reset the PC, set both up again. *2026-11-22, the Deck side passed (the app driven on the Deck, a second home standing in for the PC, throwaway folders); the owner then passed it on the Windows PC (2026-11-22) and skipped the Deck's on-screen keyboard check. Details in the phase file, T10.*

**Steam Deck**
- [x] Native binary runs from `~/.local/bin` in Desktop Mode and over SSH. *2026-11-12: ran in Desktop Mode from Konsole for the whole trial. 2026-11-17: a newer native build ran over SSH (from `~/sttest`, not `~/.local/bin`): self-test, `app list`, `app add --dry-run` and `apply --dry-run`.*
- [x] Flatpak app: link inside `~/.var/app/<id>/` resolves inside the sandbox. *2026-11-17: every emulator's permissions were read on the Deck; all but RPCS3 can write `~/Sync` (P2.6). PPSSPP (Flatpak) applied with a shared definition: `SAVEDATA` and `PPSSPP_STATE` linked inside `~/.var/app/org.ppsspp.PPSSPP/`; an in-game save and a save state both landed in `~/Sync`.*
- [x] Source on the SD card, sync root on internal storage. *2026-11-17: Eden from EmuDeck, save folders under `Emulation/storage/eden/nand` on the SD card. `app add eden --install emudeck-eden`, then both roles applied with `--no-copy`; the Deck then loaded and saved under the Windows profile, and the save reached Windows. Note: Eden keeps a `cache/` folder inside `nand/user/save`, so it syncs too; the definition should leave it out.*
- [x] Sync root unmounted or renamed: every command that touches shared files refuses, and `status` reports the links as Broken. *2026-11-17: `~/Sync` renamed away, and then an empty `~/Sync` in its place. Every command refused with exit 1, nothing was created in or at the sync root, and the links were untouched. `status` refused too then; it now reports instead (design 5.12, "Sync root unavailable"). Rerun on the Deck with that build the same day: all five applied mappings Broken, `sync_root_available: false`, OK again once restored.*
- [x] From phase 2: a Lutris mapping. *2026-11-17: Rondo Duo, a Flash projector game run by the Lutris Flatpak in the Wine prefix `~/Documents/visualnovelengine`, with a shared definition whose base is a winpath with a `lutris` prefix. `app add` read the prefix from Lutris's game config and chose `drive_c/users/deck` (the prefix also has `Public` and `steamuser`). The Deck's `Flash Player` folder became the shared copy; Windows joined with `--no-copy`, loaded that progress and saved. Flash on Windows wrote into the Deck's folder (`#SharedObjects/XDPPBU44`) rather than its own (`662PUZJB`), because the Flash settings that name that folder are shared too; the save reached the Deck about four minutes later.*
- [x] From phase 2: a Proton prefix is found. *2026-11-17, read-only: `app add --dry-run` with throwaway definitions for Persona 5 Royal (1687950), once as a winpath with a `steam` prefix and once with `{steam_compatdata:1687950}`. Both found `compatdata/1687950/pfx/drive_c/users/steamuser/AppData/Roaming/SEGA` in the internal library; a game that is not installed was refused, naming its app ID. The path was reported through `~/.steam/steam`, a link to `~/.local/share/Steam`. Not tried: a `steam_shortcut` prefix (no non-Steam shortcut on the Deck runs under Proton), a library on the SD card (it holds no games).*
- [ ] From phase 2, deferred: a game under Proton reads and writes through a link out of its prefix. *Deferred by the owner on 2026-11-17: Steam games keep saves in Steam Cloud, so the first real use is likely a mod or texture folder. Test it with that folder; the open question is whether Steam's runtime container shows the link's target, which matters most for a sync root outside the home folder.* *2026-11-22, in part: phase 3.5's exit test linked Stellar Blade's save folder in its Proton prefix to `~/Sync`; the game loaded the save, and the shared folder changed after each launch (Steam Cloud also writes there, so which of the two wrote it is not known). A sync root outside the home folder is still untried.*
- [ ] From phase 3: the desktop app on Windows, Mint and the Deck in Desktop Mode; the tray icon on KDE and Cinnamon. *2026-11-18, the shell only, each with a throwaway device:*
  - *Windows 11: the app stays in the tray, and a second copy shows the first one's window and exits 0.*
  - *Mint, Cinnamon on X11: all tests pass; the tray item registered with the StatusNotifierWatcher; the window drawn as specified, green, then amber when a link was replaced, and with banner B3 when the sync folder was moved away; a notification arrived; closing the window kept the app in the tray; after `kill -9` the stale lock and socket did not stop the next start.*
  - *Deck, Desktop Mode on KDE Wayland: the tray item registered; a second copy showed the window and exited 0; the window opened with the amber icon and a notification appeared. Spectacle run over SSH returned blank images, so the content was checked on the screen instead: the owner started the app from Dolphin against the real device, and the window showed all six mappings Linked, in the dark theme, with the green icon in the tray. SteamOS stops processes started over SSH when the session ends (`KillUserProcesses`), so tests there start the app with `systemd-run --user`; started from the desktop or by autostart, it is in the graphical session.*
  - *Not yet: the shell on the Deck's own screen rather than an external monitor.*
  - *2026-11-23, Windows 11 notifications: none had ever been seen because notifications were off for the whole PC (`ToastEnabled` 0). Turned on, PowerShell's toast showed, but as "Windows PowerShell". A dev build with a throwaway local folder now shows "SaveTether" (the name only) on its first close to the tray; a toast sent as the installed copy's Start menu entry showed the name and icon. The Settings switch was seen, on by default. A temporary Start menu shortcut to a build in another folder had that build's path as its app ID, so an install with `/DIR=` is recognised through the uninstall entry's `InstallLocation`. Not yet seen: the installed copy of a build with this change sending its own, and the switch silencing a toast.*
- [x] Phase 3 exit test. *2026-11-20, build `5d53e2c`, each machine with a throwaway local folder, sync folder and save folder, and only the app (the CLI only read the results). The app was not touched while it went amber and green: those came from its own check every five minutes.*
  - *Windows 11: the sync folder on a `subst` drive (`T:\Sync`). Setup ("This is my first device", a device name, Set up), Add app, Add a folder by hand ("Exit Game", its `Saves` folder), Apply it now, Share this device's saves, Apply: "Exit Game saves is linked" with a backup; on disk a junction into `T:\Sync\savetether\exit-game\Saves`. The tray said "All linked" with the green badge. `subst T: /D`: at the next check the tray said "Sync folder not available" with the amber badge, the window showed banner B3 and the row "Shared folder missing". `subst` again: green at the next check, the save intact.*
  - *Deck, Desktop Mode: started from the unpacked package with a throwaway `HOME`, so the first screen was the install offer: Install, then the same flow by `xdotool`. The sync folder was inside a `fuse-overlayfs` mount, mounted as the user; `fusermount3 -u` left an empty mount point, as an unplugged drive does. The tray icon, read over D-Bus, went from the green badge to amber at the next check, with banner B3, and back to green at the check after remounting; the save read through the link.*
  - *Found, and fixed the same day (spec section 4.1 and section 8, "As built"; design 5.12): a FUSE mount whose process had died ("Transport endpoint is not connected") made setup's step 1 show "Something went wrong", and `status` failed with exit 1; behind "Apply it now?" the page still said "No apps are set up yet"; banner B3's second line repeated its first. Fixing the first, a new Linux test found the per-mapping check also threw when resolving a link through the unreadable folder. Tests: `SharedStoreTests`, `SetupFlowTests` and `StatusCheckTests`, with a link that points at itself standing in for the dead mount (Linux only). On the Deck with a real dead mount (the `fuse-overlayfs` process killed): `status` exits 0 and says the sync root cannot be read, giving the system's reason; the app shows B3 as one sentence with that reason; step 1 says the folder cannot be read; and on a fresh device the page behind "Apply it now?" lists the app just added, and the Apply after it links as before.*
- [ ] From phase 4: status, Recover and a choice-free Apply from the Decky plugin in Game Mode.

**Both**
- [x] A save made on one device loads on the other. *2026-11-12, Cemu, both directions. 2026-11-17: Eden (Windows portable and the Deck's EmuDeck install, sharing the Windows profile) and Rondo Duo (Windows and the Deck's Lutris prefix), each saved on one device and loaded on the other.*
- [x] Simulated wipe restores with `init` and `apply --all`. *2026-11-12, on the Deck.*

## 9. Risks to watch

| Risk | Where it bites | Mitigation |
|---|---|---|
| Junction P/Invoke is subtly wrong (buffer layout, path prefix) | M1 | Read the target back through an independent API and through `fsutil reparsepoint query` in a test |
| Hosted ARM64 runner unavailable or flaky | M0 | Fall back to cross-publishing for ARM64 and running tests under emulation; note the gap |
| Fault matrix grows too slow | M5 | Small fixture trees; run steps in parallel per choice |
| A sync tool syncs the tool's temporary files mid-write | M3 | Temporary names start with a dot and end `.tmp`; readers ignore them; rename is atomic |
| Process-name check misses a running app | M4, M7 | Names recorded per device and verified on hardware; output lists what was looked for |
| Decky API changes with SteamOS | Phase 3 | Plugin holds no logic, so a break costs the UI only |

## 10. Stretch goals

Added 2026-11-19 by the owner. Taken up once the planned phases are done, unless a real need comes first: someone who must run from a network share on Windows, or an app that keeps one save file among files that must not be shared. Neither blocks planned work. A sync folder on a local drive, kept in step by a sync tool (for a NAS, Syncthing running on it), covers the network-share case today (`docs/sharing.md`).

| Item | Deliver | Tests |
|---|---|---|
| Symbolic links on Windows, for a network share | A sync root on a network share on Windows, which a junction cannot point to. The link type is chosen per mapping at apply: a junction for a local target, as now, and a directory symbolic link for a network target, only when this user may create one (Developer Mode on, or elevated) and the policy lets a local link point to a remote target (`fsutil behavior query SymlinkEvaluation`, local-to-remote; on by default). Otherwise the refusal names what is missing. Setup's refusal of a network sync root on Windows then applies only without that privilege. `SafeFs` already makes, recognises and removes symbolic links on Windows; the planner, the checks at setup and apply, and the health check's "this link is ours" are what change. A design change: requirement R5 (no admin) becomes "no admin; Developer Mode only for a sync root on a network share", recorded in design 2.1 | The planner picks each link type; refused without the privilege, and when the policy turns local-to-remote links off; apply, revert and recover through a symbolic link. CI's Windows runners are elevated, so they cover the symbolic link path, beside the existing test that junctions need no privilege. On hardware, with Developer Mode on: a sync root on a NAS share, apply, revert and recover, the share unplugged (links `Broken`, amber), and a second machine sharing the same folder |
| Single-file mappings | A mapping of one file, for an app that keeps a single save file among files that must not be shared, where linking the directory and filtering with the sync tool's ignore patterns (design 2.1) is not enough. None is known yet. A file symbolic link: on Linux with no privilege, on Windows with the same privilege as the row above. **Hard links only for an app shown to write that file in place.** Most apps save by writing a temporary file and renaming it over the old one, which replaces the file and silently ends a hard link's sharing after the first save (design 2.1, "File-level links on Windows") | An app-style save (write a temporary file, rename over) through a file symbolic link keeps sharing. A hard link replaced that way is reported as drift by the health check, not trusted. File mappings in backups, restore and the comparison. On hardware, the app that needed it |
