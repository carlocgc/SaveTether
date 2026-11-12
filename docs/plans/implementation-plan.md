# SaveTether: implementation and test plan

Status: plan, nothing implemented. Written 2026-11-12, after the design in [save-redirection-design.md](../design/save-redirection-design.md) was merged. The design says *what* and *why*; this says *in what order*, *in which files*, and *how each piece is proven*. Section numbers such as "design 5.3" refer to the design document.

Detail falls off on purpose. Phase 1 is planned to the level of types and test cases, because it is next and its design is closed. Phases 2 to 4 are planned to the level of work items and exit tests, and each starts with a short step that turns its outline into the same level of detail.

## 1. Decisions this plan rests on

Settled on 2026-11-12, in addition to the design's section 7.

| Decision | Choice | Notes |
|---|---|---|
| Test framework | xUnit | Approved dependency, test project only. Nothing from it ships |
| CLI argument parsing | Hand-written | No dependency. Help text is written by hand and tested |
| Definition file format (phase 2) | YAML, with YamlDotNet | Approved dependency, shipped in the CLI from phase 2. Phase 1 stays JSON-only. Native AOT support must be proven first (step 2.2) |
| Branch flow | Feature branches, PRs straight to `main` | A `development` branch is introduced in phase 4, when releases are tagged |
| JSON | `System.Text.Json` with source generation | Built in. Reflection-based serialisation does not work under Native AOT |
| CI | GitHub Actions from the first PR | The repository is public, so hosted Linux ARM64 runners are available **[K, confirm in M0]** |

Still open, and not blocking:

- Which real mapping is the first trial in M7. Pick something small whose loss would not hurt.
- The two Deck checks from the design: whether the PPSSPP Flatpak can reach `~/Sync/savetether`, and the real process names. Needed before M7, not before.
- The config-mechanism questions in design 5.3. Section 3.1 below proposes answers; they are confirmed when phase 2 starts.

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
| Hardware | Manual checklist on the Windows PC and the Deck (section 7) | before each phase exit |

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

1. Windows PC, non-admin terminal: full test suite, then a throwaway folder mapping: apply, status, revert. *Done 2026-11-12; see the Windows entries in section 7.*
2. Run the two Deck checks. Copy the Linux binary from the CI artifact to `~/.local/bin`.
3. One real, low-stakes mapping: `init --create-root` and `apply --copy` on Windows; after the sync tool delivers, `init` and `apply` on the Deck.
4. Play on each device once and confirm the save crosses over.
5. `revert` and re-apply on one device.
6. Simulated wipe on the Deck (scenario 5).

**Phase 1 exit:** all of the above done, `status` clean on both devices, and the hardware checklist (section 7) ticked.

## 4. Phase 2: config redirects, definitions, resolver

Goal: stop typing absolute paths, and manage apps that redirect through a setting. Exit test: a device restored after its SD card ID or install folder changed, with no hand edits.

### 2.1 Specify the config mechanism (S, design work)

Proposed answers to the questions design 5.3 leaves open. Confirm or change them in a design PR before any code.

- **"This device's saves"** are the folder the key points at now, or the app's default folder (from its definition) when the key is unset.
- **Already applied** means the key's value resolves to T.
- **The old folder is never renamed or deleted.** A config redirect only changes a key, so there is no "aside" step. This also makes it safe when the old folder is itself inside the sync root, as RetroArch's is on the Deck today: the precondition "S is not inside the sync root" does not apply to this mechanism.
- **Setup choices** work as for links, applied to (old folder, T).
- **Revert** writes the recorded previous value back and nothing else. Copying T's content back into the old folder is offered only when that folder is empty.
- **The app must be closed**, checked as for links, because some apps rewrite their config on exit.

### 2.2 Prove YamlDotNet under Native AOT (S, spike)

Before building on it: parse one definition in the published native binary on all three targets. YamlDotNet's reflection-based deserialiser is not expected to work under AOT **[K]**; the options are its static generator or reading the document tree and mapping it by hand. If neither works cleanly, come back to the owner before going further (JSON with comments is the fallback).

### 2.3 Work items

| Item | Deliver | Tests |
|---|---|---|
| Config editors | Line-preserving edit of one key for `retroarch-cfg`, `ini`, `toml`; `xml` through the built-in XML reader with whitespace preserved | Golden files: edit a key and assert the rest of the file is byte-identical, including comments, ordering, line endings and encoding. Key missing, duplicated, commented out |
| Config transaction | Plan steps and executor support per 2.1 | Fault matrix extended to config steps; round trip; "old folder inside sync root" case |
| Definitions | YAML loader, JSON Schema file, validation errors with file and line | Every example in design 5.2 loads; a table of invalid files with the expected message |
| Tokens and resolver | `{home}`, `{appdata}`, `{localappdata}`, `{locallow}`, `{documents}`, `{xdg_*}`, `{flatpak:id}`, `{install_dir}`, `{sd_card}`, `{emudeck}`, `{steam_compatdata:id}`; `winpath` with `steam`, `steam_shortcut`, `lutris` and `path` prefixes | Fixture directory trees and fixture `libraryfolders.vdf`, `shortcuts.vdf` and Lutris config files, including a game on a second Steam library and a missing SD card. No real Steam needed |
| `app add` | Add an app from a definition: resolve, show roles with sizes, write mapping and device entries | End to end against fixture trees; ambiguous install asks, never guesses |
| Flatpak precheck | Read the app's permissions; refuse with the exact `flatpak override --user` command when T is unreachable | Parser tests on captured `flatpak info --show-permissions` output; live check on the Deck |
| Snapshots | On-demand and scheduled copy of T to the backup store, with retention | Retention and space reporting; never runs while a journal entry is open |
| Login-time check | Opt-in Task Scheduler entry on Windows, systemd user timer on Linux | Generated unit and task files compared with expected text; install and remove on real hardware |

## 5. Phase 3: Decky plugin and detection

Goal: apply and revert from Game Mode. Starts with a short design step for the plugin's screens against the then-current Decky API (the design's claim that plugins can register full-screen pages is unverified).

| Item | Deliver | Tests |
|---|---|---|
| JSON contract freeze | Document every `--json` output and input flag as version 1; additive changes only after this | The stored-shape tests from M6 become the contract tests |
| Plugin backend | Python: runs the CLI, passes JSON through. No logic | Unit tests with a fake CLI executable: success, each exit code, timeout, binary missing |
| Plugin frontend | List, row actions, full-screen pages for comparison, restore and add-app | Component tests against recorded JSON; manual Game Mode checklist |
| Detection | Scan for installed apps that have a definition | Fixture trees per install variant; nothing is changed by a scan (asserted by hashing the tree before and after) |
| Ludusavi hints | Optional run-time download of the manifest, as a hint source only | Parser tests on a small fixture manifest. Not started until its maintainer has been contacted (design 5.2) |

Exit test: a mapping applied and reverted using only the controller, in Game Mode.

## 6. Phase 4: public release

| Item | Deliver | Tests |
|---|---|---|
| Name check | GitHub, NuGet, Scoop, winget, trademark search | Recorded in the design before anything is published under the name |
| Release workflow | Tag-triggered build of three targets, checksums, release notes. `development` branch introduced | A prerelease tag produces installable artifacts; checksums verify |
| Signing | Apply to SignPath Foundation; sign Windows binaries | Signature verifies on a clean Windows machine |
| Packages | Scoop and winget manifests | Install, run, upgrade and uninstall on a clean Windows machine |
| Definitions repository | Split out, schema check in its CI, "tested on" line required | A deliberately broken definition fails its CI |
| Upgrade safety | A newer tool reads older files; an older tool refuses newer ones | Fixture sets of every released schema version, kept forever |
| Docs | README, install, first-run walkthrough, CONTRIBUTING, SECURITY | A person who has not seen the project follows the walkthrough on one device |

Exit test: first outside definition merged.

## 7. Hardware checklist

Run before each phase exit. Record the date and tool version in the PR that closes the phase.

**Windows PC**
- [x] Test suite passes in a non-admin terminal (junctions without privilege). *2026-11-12, 0.1.0 at `a3cbf74`: 328 passed, not elevated, Developer Mode off.*
- [ ] Apply and revert on a folder on `E:` with the sync root on `E:`, and on a folder on `C:` (cross-volume junction). *2026-11-12, native binary, throwaway folders: passed with both on `C:`, and with the folder on `C:` and the sync root on `G:` (cross-volume). `E:` itself has not been used; left unticked until the real mapping in M7 step 3.*
- [x] Apply is refused while the app is running. *2026-11-12: refused against a running process name (`explorer`), and went ahead with `--assume-closed`. Not yet tried with a real emulator's process name.*

**Steam Deck**
- [ ] Native binary runs from `~/.local/bin` in Desktop Mode and over SSH.
- [ ] Flatpak app: link inside `~/.var/app/<id>/` resolves inside the sandbox.
- [ ] Source on the SD card, sync root on internal storage.
- [ ] Sync root unmounted or renamed: every command refuses.
- [ ] From phase 2: a Proton prefix mapping; a Lutris mapping.
- [ ] From phase 3: everything above from Game Mode.

**Both**
- [ ] A save made on one device loads on the other.
- [ ] Simulated wipe restores with `init` and `apply --all`.

## 8. Risks to watch

| Risk | Where it bites | Mitigation |
|---|---|---|
| Junction P/Invoke is subtly wrong (buffer layout, path prefix) | M1 | Read the target back through an independent API and through `fsutil reparsepoint query` in a test |
| Hosted ARM64 runner unavailable or flaky | M0 | Fall back to cross-publishing for ARM64 and running tests under emulation; note the gap |
| Fault matrix grows too slow | M5 | Small fixture trees; run steps in parallel per choice |
| A sync tool syncs the tool's temporary files mid-write | M3 | Temporary names start with a dot and end `.tmp`; readers ignore them; rename is atomic |
| YamlDotNet and Native AOT | 2.2 | Spike before building; fallback agreed in advance |
| Process-name check misses a running app | M4, M7 | Names recorded per device and verified on hardware; output lists what was looked for |
| Decky API changes with SteamOS | Phase 3 | Plugin holds no logic, so a break costs the UI only |
