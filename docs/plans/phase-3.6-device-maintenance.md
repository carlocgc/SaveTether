# SaveTether phase 3.6: device removal, reset and maintenance

Section 5b of the [implementation plan](implementation-plan.md) points here. This file holds the whole phase: the goal, the owner's decisions, the design of each flow, the app's screens and dialogs, the tasks (T1 to T10), the exit test, and each task's progress and "As built" notes. A delegated task needs only this file and the files it names.

Added 2026-11-21 by the owner; planned 2026-11-22. Replaces the stub that was in section 5b.

**Goal.** A device can take itself out of the sync folder, because it is being sold, decommissioned or set up again from scratch. The last device can remove everything SaveTether keeps in the sync folder. A device can be renamed. Old backups can be cleared. A Maintenance screen shows what is registered and holds these actions. After a removal, the app starts as it does on first launch: choose a sync folder and a device name.

## Decisions

Taken by the owner on 2026-11-22.

| # | Decision |
|---|---|
| D1 | **Local folders are the user's once they are no longer links.** SaveTether takes care of the data while it makes or undoes a link; after that, the folders belong to the user. Removal turns every link back into a real folder, using the existing revert (copied back and verified). There is no "wipe local saves" option. The result lists the folders that were given back, so the user can delete them (before selling a device, say) |
| D2 | **No export or import archive.** The stub's T2 and T7 are dropped. Removal leaves the saves as ordinary folders, and a device is added again with setup and Add app |
| D3 | **Removing another device is in scope**, from Maintenance, for a device that is gone (the old PC). It deletes only that device's file and its seed markers; nobody's links are touched |
| D4 | **Reset is not a flow of its own.** It is an option of Remove this device: "Delete everything SaveTether keeps in the sync folder". The option is offered only when this is the only device left. A device file that carries this install's ID, such as one left over from an unfinished rename, counts as this device. It deletes all of `<sync root>/savetether/`: mappings, the shared save folders, custom definitions and tombstones. Local links are reverted first, as in every removal, so the local copies survive (D1). While other device files exist, the option is shown disabled, with the reason ("2 other devices use this sync folder; remove them first") |
| D5 | **Orphaned mappings.** A mapping is orphaned when no device file has a `Paths` entry for it. Removal lists the mappings that only this device uses, and for each the user chooses one of three:<br>• Keep (the default)<br>• Delete the mapping<br>• Delete the mapping and its save folder<br>The last uses the existing `map delete` path and needs a typed confirmation. A mapping that another device uses is never offered. Maintenance also lists orphans that are already there, with the same actions |
| D6 | **Tombstones.** A removal writes `.savetether/removed/<device>.json`, holding the name, the install ID, the UTC time and `removed_by`, the name of the device that did the removal. Four checks read it:<br>• **The file comes back:** a device file whose install ID is tombstoned (an offline device's sync tool restored it). `status` and Maintenance flag it<br>• **Removal not finished:** a device finds its own install ID tombstoned, its bootstrap still there, and `removed_by` is its own name. It shows "Removal not finished", with **Finish removal**<br>• **Removed by another device:** the same, but `removed_by` is another name. It shows "This device was removed from the sync folder by <removed_by>", with **Finish removal here** and **Set up again**<br>• **Name reused:** `init` with that name removes the tombstone, since the new install has a new install ID |
| D7 | **Maintenance is a screen** (an `IScreen`, like Backups and Settings). It opens from a **Maintenance** button in the main page's bar, after Settings (`MainWindow.cs`, where the Settings button is made). Screens have no Back to another screen, so it is not reached through Settings |
| D8 | **Typed confirmations.** The text the user types:<br>• **Remove a device** (this one or another): the device's name<br>• **Delete everything** (D4), or **delete an orphan's save folder:** `delete everything`<br>• **Delete all backups:** a warning first, then `delete all backups`<br>The match ignores case and spaces at either end |
| D9 | **Backup cleanup** is on the **Backups** screen, as a **Clean up** button beside the list it changes. It covers the app the screen is filtered to, or all apps, in one of three modes: all, all but the first (the copy taken before the first link), or all but the newest. Maintenance shows the backup count and size, with a button that opens Backups |

Not in this phase:
- **Removal while the sync root cannot be reached.** A link cannot be copied back from a folder that is not there.
- **Open-folder buttons.**
- **Clearing history:** there is no history file; the journal is cleared by each operation.
- **Rewriting `SupersededBy` in other devices' seed markers after a rename.** Each device writes only its own files (design 5.6). The old name stays in those markers as history.
- **Maintenance in Game Mode or the Decky plugin** (phase 4).

## Design

What the code has today, and what each flow reuses. The paths are under `src/SaveTether.Core/`.

**What is stored where (design 5.6).**
- **Shared**, under `<sync root>/savetether/.savetether/` (`Storage/SharedStore.cs`):
  - `mappings/<id>.json`
  - `devices/<name>.json`, the `DeviceFile` record: name, install ID, backup root, `Paths`, `Apps`
  - `seed/<mapping>/<name>.json`, the seed markers
  - `definitions/`, the custom definitions
  - `deleting/<id>/`, the staging folder used by `map delete`
- **Local**, in the folder `LocalStore.DefaultFolder()` gives, or `SAVETETHER_HOME`:
  - `bootstrap.json`: sync root, device name, install ID
  - `state.json`
  - `journal.json`
  - `operation.lock` and `operation-holder.json`
  - `gui.log`
- **Backups**, at `DeviceFile.BackupRoot`: `<backup root>/<mapping>/<UTC time>/` (`Backup/BackupStore.cs`).

**Where the device name is a key,** so a rename must change it:
- the device file's name
- the seed markers' file names, and the `DeviceName` field inside them
- `BootstrapFile.DeviceName`
- `JournalFile.DeviceName`, which is why a rename is refused while a journal is open

`SeedMarker.SupersededBy` in other devices' markers is left as it is.

**Remove this device.** The removal is a sequence of steps, each safe to run again. It needs no new executor step kind:
1. **Refuse** when a journal is open (with Recover as the next step), when another process holds the lock, when the sync root cannot be reached (a sync root that is there with no `savetether/` folder in it is not this case; see "A crash" below), or when a mapping is in a state that revert refuses (name the mapping and its own fix). Then take `OperationLock` for the whole removal.
2. **Revert** each mapping linked here, through `Workspace.PlanRevert` and `Workspace.Run`. Each revert is its own journalled operation: back up, copy to staging, verify, remove the link, rename into place (`Planning/Planner.cs` `PlanRevert`). Mappings that are not linked here are skipped. A running app blocks its revert, as it does today.
3. **Orphans:** carry out the user's choices (D5) through `Workspace.DeleteMapping` (`Operations/Workspace.Delete.cs`). That takes a `PreDelete` backup first, as today.
4. **Tombstone:** write `.savetether/removed/<device>.json` (D6).
5. **Shared files:** delete this device's seed markers, then its device file, with `SafeFs.RemoveSharedFile`. Nothing else shared is deleted, except the orphans of step 3.
6. **Backups:** delete them if the user chose to, with `BackupStore.Remove` for each one.
7. **Local state:** delete `state.json`, `journal.json` and the lock files, and **`bootstrap.json` last**. That is the commit point: until it goes, the device still counts as set up and a re-run continues where it stopped. `gui.log` stays.
8. **With D4's option:** between steps 6 and 7, `SafeFs.DeleteTree(<sync root>/savetether, SharedDataDeletion.ExplicitlyConfirmed)`. This is refused when any device file exists whose install ID differs from this install's.

**A crash** leaves the bootstrap in place.
- Mappings that were already reverted are no longer links, so the re-run skips them. A revert that stopped part way is an open journal, which Recover handles.
- When the device file is gone and this install's tombstone is there with `removed_by` set to this device's name, the removal is unfinished. `status` says so, and the app shows a "Removal not finished" banner with **Finish removal**. When `removed_by` is another name, another device removed this one (D6).
- **After Delete everything's tree delete:** the sync root is there but `<sync root>/savetether/` is gone, and with it the tombstone. When `state.json` lists nothing linked here, this is an unfinished removal too. The re-run skips every shared step and does only step 7.
- **The re-run asks again.** The user's choices (orphans, backups, Delete everything) are not stored. **Finish removal** reopens Remove this device at page 1 with the defaults, so the user chooses again. In the CLI, `device remove` is run again with its flags. Choices that were already carried out show as done: an orphan that is already deleted is no longer listed.

**After the removal,** `StatusCheck.Run` (`Gui.Presentation/OverallState.cs`) finds no bootstrap and returns `NotSetUp`, and `MainWindow` shows `SetupView`, with no restart. The program stays installed: the result says that Settings → Uninstall removes it.

**Remove another device** (D3).
- It deletes that device's seed markers and device file, and writes its tombstone.
- It is refused for this device's own name.
- When that device is still alive, its next check finds its install ID tombstoned (D6), and the user chooses there.

**Rename.**
1. **Check:**
   - the name is valid (`Model/Identifiers.cs` `IsValidDeviceName`)
   - no device file has the name (`SharedStore.ListDeviceNames`), except one carrying this install's ID: that is this device's own unfinished rename, and the rename carries on
   - no journal is open
2. **Write the new files:** the device file under the new name with the same install ID, and a copy of each of this device's seed markers under the new name.
3. **Update `bootstrap.json`** (the commit point).
4. **Delete the old device file and markers.**

A crash after step 2 leaves two device files with this install ID. A second `device rename <new>` finishes the job, and `status` says so. Do not use Remove another device on the old name: it would write a tombstone with this install's ID, and this device would then think it had been removed. A crash after step 3 leaves the old files; a rename to the same name again deletes them.

**Backup cleanup** (D9). `BackupStore.List(mappingId)` is oldest first. For each mapping, the three modes keep nothing, the first backup, or the last one. Each goes through `BackupStore.Remove`. `Prune` (first plus the newest N, after each run) is unchanged.

## The app: screens and dialogs

**Maintenance screen** (D7), top to bottom:
1. **Overview**, read only:
   - the sync folder
   - the devices registered
   - the mappings in the sync folder
   - the mappings this device uses, and how many are linked here
   - the orphaned mappings
   - the backups: their count and size, and **Open Backups**
2. **This device:** its name and install ID, with **Rename** and **Remove this device**.
3. **Other devices:** one row each. A row shows the name, the last activity (the newest write time of the device's file or seed markers), its mapping count and **Remove**. When the device's install ID is tombstoned, the row says "Removed, but its file came back".
4. **Orphaned mappings:** one row each, with **Delete mapping** and **Delete mapping and saves**.

Actions are disabled, and say why, when the core would refuse them: a journal is open, the lock is held, or the sync root is gone. This is the same rule as the Apps page.

**Dialogs.** Every dialog is one that `Overlay` (`src/SaveTether.Gui/Overlay.cs`) already has, plus one new primitive:
- **New:** `Overlay.AskTextAsync(string title, string text, string confirmLabel, Func<string, string?> problem, bool danger)`, which returns the text, or null when cancelled. It is built on `ShowAsync` with a `TextBox`.
  - The confirm button is disabled while `problem(text)` returns a message, and that message is shown under the box.
  - Enter in the box does not confirm; Escape cancels, as in every dialog.
  - Focus starts in the box. This is the one exception to "focus on the safe choice", and it is allowed because typing cannot confirm by itself.
  - Rename uses it, with "not valid" and "taken" as the problems. So does every typed confirmation (D8), with "Type <phrase> to confirm" as the problem.
  - The checks are written in `Gui.Presentation`, where they are tested.
- **Reused:**
  - `ConfirmAsync(danger: true)`: the first warning before deleting all backups, and Remove for another device (before its typed name)
  - `InformAsync`: the results
  - the operation dialog with its paced step list (`ActionFlow`, spec 9.3): the removal and the cleanup
  - `ProcessChoices`: an app that is still running blocks a revert

**Remove this device** has too many choices for one dialog, so it is a screen of two pages, like Add app's plan:
- **Page 1, choices.** Each mapping only this device uses offers Keep (the default), Delete mapping, or Delete mapping and saves. Backups offer Keep (the default) or Delete. On the last device only, "Delete everything SaveTether keeps in the sync folder" (D4) appears and replaces the choices for each mapping.
- **Page 2, plan.** What it lists:
  - the links that become folders, with their paths
  - the shared files that will be deleted
  - what happens to the backups
  - what stays
  Then **Remove**.
- **Then:**
  1. the typed device name, plus `delete everything` when D4's option was chosen
  2. the operation dialog
  3. the result, with the folders given back in a selectable "Details", and a note that the program stays installed
  4. OK opens setup

**Banners and states:**
- **"Removal not finished"** (the tombstone's `removed_by` is this device's name), with **Finish removal**. It reopens Remove this device at page 1 with the defaults; the choices are not stored.
- **"This device was removed from the sync folder by <removed_by>"** (another name), in place of the Apps page, with **Finish removal here** and **Set up again** (D6).
- **After a rename:** the window title, the tray tooltip and the page show the new name.

**Steam Deck.** In Desktop Mode, text goes in through the on-screen keyboard (Steam+X). The text box must take it, and the dialog must stay clear of the keyboard at the Deck's window size.

## Tasks

Each task is one PR against `development`. Every task follows `CLAUDE.md`:
- Every delete, move or link goes through `SafeFs`.
- The app holds no logic of its own; it calls the core.
- Stage files by name.
- `dotnet build -warnaserror` and `dotnet test` before the PR.
- Change the docs in the same PR: `docs/cli.md` for each command, `docs/cli-json.md` and `JsonContractTests` for each `--json` change, the spec's "As built" notes for the app.

Each task records its progress below: what was built, the hardware check (commands, what was seen, the commit tested), and any "As built" note.

**Order.** T1 comes first. T2, T3 and T4 can then run side by side: they share only the command tables in `ArgParser.cs` and `CliApp.cs`, so the later one rebases. T5 follows T2. T6 and T7 follow T5. T8 needs T2 to T7, T9 follows T8, and T10 is last.

**Hardware checks.** Every task ends with a check its implementer runs on the test machines, by the rules in `docs/testing/hardware-testing.md`:
- a guarded `SAVETETHER_HOME` under `~/sttest/<task>/`, and a scratch sync folder there (`init --sync-root <scratch>/sync --create-root`)
- `savetether status` of the real setup saved before, then compared after in a fresh shell
- the scratch folders removed when done

**Two devices on one machine.** Two local folders, `home-a` and `home-b`, are set up against the same scratch sync folder with different device names. They stand in for two devices in every check that needs them; only T10 uses two real machines.

**What each machine can do:**
- **Mint** has no `xdotool` and cannot type text into the app. It checks the CLI, and the app's layout with clicks.
- **The Deck** checks text entry in the app (`xdotool type`).
- **Windows:** the CLI checks run in a checkout on the PC, in PowerShell, with a scratch `SAVETETHER_HOME`. A check of the app on Windows is for the owner to run.

Each check of the app ends with a capture (`spectacle -a` on the Deck, `import -window` on Mint), which is copied to the PC and looked at.

### T1: design section (S)

- **Deliver:** design section 5.15, "Device removal, rename and maintenance". It holds the decisions above, the step order of a removal and its commit point, the tombstone format (`schema: 1`, `device_name`, `install_id`, `removed_utc`, `removed_by`), and the rename steps. Add a line to 5.6 for `removed/`.
- **Files:** `docs/design/save-redirection-design.md`.
- **Done when:** the owner approves it. There is nothing to check on hardware.

### T2: overview (S)

- **Deliver:**
  - In the core, `Maintenance.Overview(Workspace)` returns a `MaintenanceOverview` with:
    - `SyncRoot`
    - `Devices`: for each, `Name`, `InstallId`, `LastActivityUtc`, `MappingCount`, `IsThisDevice`, `Tombstoned`
    - `Mappings`, `MappingsHere`, `LinkedHere`
    - `Orphans`: mapping IDs
    - `BackupCount`, `BackupBytes`
    It only reads.
  - The CLI gets `device list`, with `--json`.
  - D6 adds `Tombstoned`; it is false until T5 writes tombstones.
- **Files:**
  - `Core/Operations/Maintenance.cs` (new)
  - `Model/Files.cs`, if a record is needed
  - `Cli/ArgParser.cs`, `Cli/CliApp.cs`, `Cli/Output.cs`
  - `docs/cli.md`, `docs/cli-json.md`, `tests/SaveTether.Tests/Cli/json-shapes.txt`
- **Tests:**
  - **Counts:** a fixture sync folder with two devices, a shared mapping, a mapping only one device uses and an orphan gives the counts expected.
  - **Last activity:** the newest of the device file and its markers.
  - **Read-only:** the tree is hashed before and after, and is unchanged.
  - **Contract:** `JsonContractTests`.
- **Verify on hardware: Mint and Windows, CLI.** Set up `home-a` and `home-b` on one scratch sync folder. On a, `app add` a test definition and apply it. Then on a, `map add` a second mapping followed by `map remove`, which leaves it orphaned. From a, `device list` and `device list --json` show:
  - 2 devices, with b's last activity
  - a's mapping, linked here
  - 1 orphan
  - the backups' count and size
  The numbers must match `ls -R` of the sync folder and `du` of the backup root.

### T6: remove another device; tombstone checks — done

Branch `phase-3.6-t6-remove-other`.

- Core: `Workspace.RemoveOtherDevice(name, dryRun)` (tombstone first, then the markers and the file; refused for this device's name, an unknown name, and a file carrying this install's ID). `StatusReport` gains `Removal` (`removed-by-another` or `unfinished`, from this install's tombstone and its `removed_by`), `RemovedBy` and `ReturnedDevices` (device files whose install ID is tombstoned). `Init` clears the tombstone of a name a new install takes. `SharedStore.DeleteTombstone`. `OverallCondition.RemovedByAnother` and `RemovalUnfinished` (headlines only; the banners and their texts are T8).
- Finishing on a device another removed: `PlanDeviceRemoval` and `RemoveDevice` rebuild the missing device file in memory from `state.json` (the sources of what is linked), with the default backup folder (created if missing: the file that named the real one is gone) and no process names, so a running app is not noticed. The tombstone is not rewritten, so `removed_by` stays the other device's. The same path finishes a removal that stopped after the device file was deleted.
- CLI: `device remove --other <name> [--confirm-remove]`, `--json` as `removal` with `other` set. Docs: `cli.md`, `cli-json.md`, `json-shapes.txt`, `hardware-testing.md` ("Device remove: another device").
- Not done here: the "sync root there with no `savetether/` folder, nothing linked" case of an unfinished removal is T7's (it only arises after Delete everything, and an empty mount point looks the same, so it needs T7's care).
- Tests: `DeviceRemoveOtherTests` (remove other and what stays, refusals, removed by another then finished here with the backup folder gone, a file that comes back, an unfinished removal run again, a reused name). `dotnet build -warnaserror` clean; 955 tests pass (`InstallerTests.The_cli_asks_the_running_installed_app_to_quit_before_removing_it` failed once under load and passes alone).
- Hardware check (2026-11-22, CLI Release linux-x64 on Mint, built from a `git archive` of the working tree, `home-a` and `home-b` on one scratch sync folder): all four steps of the plan. The first run found a bug: finishing a removal on a device another removed was refused because the default backup folder did not exist; fixed (and a regression step added to the test), then re-run. It also showed the `--other` text saying "this device"; fixed. `status` of the real setup was the same before and after, and the scratch folder is gone. Windows not run.

### T3: rename (M)

- **Deliver:**
  - In the core, `Workspace.RenameDevice(string newName)`, by the rename steps above.
  - The CLI gets `device rename <new-name>`, with `--json`. Its refusals:
    - an invalid name
    - a name another device has
    - an open journal
    - a sync root that cannot be reached
  - Each refusal is a `StoreProblem` or an existing code, with its exit code in `docs/cli.md`.
- **Files:**
  - `Core/Operations/Workspace.cs`, or a new `Workspace.Device.cs`
  - `Storage/SharedStore.cs`: delete a device file, and delete this device's markers, through `SafeFs`
  - `Storage/LocalStore.cs`
  - the CLI files and docs as in T2
- **Tests:**
  - **Rename:** the files have the new names, and the bootstrap is updated.
  - **Refusals:** each one, with nothing changed.
  - **Re-run:** a crash after the new files are written (a fault hook, or the half-done state made by hand in the test) is finished by a second rename. The name check lets it through because the new device file carries this install's ID.
  - **Taken name:** a name whose device file has another install's ID is still refused.
  - **Health:** `status` stays healthy after a rename.
- **Verify on hardware: Mint and Windows, CLI, two homes.** On a, `device rename a2`. Afterwards:
  - the sync folder has `devices/a2.json` and `seed/*/a2.json`, and no `a.json` under either
  - `bootstrap.json` says `a2`
  - `status` on a2 is healthy
  - `device list` from b shows `a2`

  Run each refusal (the name `b`, an invalid name, and an open journal made by hand) and check its exit code. For the re-run, make the half-done state by hand: copy the device file and markers under the new name, and leave the bootstrap on the old one. `device rename a2` then finishes, leaving one set of files.

### T4: backup cleanup (S)

- **Deliver:**
  - In the core, `BackupStore.RemoveMany(string? mappingId, BackupCleanup mode)`, where `BackupCleanup` is `All`, `KeepFirst` or `KeepLast`. It returns what was removed. `Workspace.CleanUpBackups(...)` wraps it.
  - The CLI gets `backup delete --all | --keep-first | --keep-last [--mapping <id>]`. `--all` needs `--confirm-delete`, or the typed confirmation in a terminal (`Confirm` in `CliApp.cs`). Without either it is a dry run that lists what would go.
- **Files:**
  - `Backup/BackupStore.cs`, `Operations/Workspace.cs`
  - the CLI files and docs
- **Tests:**
  - **Modes:** each mode over 1, 2 and 5 backups. With 1 backup, keep-first and keep-last keep it.
  - **Scope:** `--mapping` leaves the other mappings alone.
  - **Partial backups:** a `.partial` folder is cleared, as in `Prune`.
  - **Dry run:** changes nothing.
- **Verify on hardware: Mint and Windows, CLI.** Make 4 backups of mapping X and 2 of mapping Y: apply, then revert and apply again. Then:
  1. `backup delete --keep-first --mapping X` leaves X with only its oldest backup, and Y untouched.
  2. Make the backups again. `--keep-last --mapping X` leaves only the newest.
  3. `--all` without `--confirm-delete` from a script deletes nothing and lists what it would delete.
  4. With `--confirm-delete`, the backup root holds no backup folders.

  `backup list` agrees each time.

### T5: remove this device (L)

- **Deliver:**
  - In the core, `Workspace.PlanDeviceRemoval(RemovalChoices)` returns the plan without touching disk. `RemovalChoices` holds, for each orphan, Keep, DeleteMapping or DeleteWithSaves, and `DeleteBackups`. The plan holds:
    - the reverts, with their paths
    - the mappings that are not linked here, which are skipped
    - the orphans and their choices
    - the shared files to delete
    - the backups
    - the local files
  - `Workspace.RemoveDevice(plan, progress)` runs the steps above. The tombstone is written by `SharedStore.WriteTombstone`.
  - The CLI gets `device remove [--delete-orphan <id>[:saves]]... [--delete-backups] [--confirm-remove]`. Without `--confirm-remove` it is a dry run. In a terminal, the user types the device name (D8). The result's `--json` lists the folders given back.
  - The lock is held across the reverts: check how `Workspace.Run` takes the lock, so that the removal does not try to take it twice.
- **Files:**
  - `Operations/Workspace.Remove.cs` (new)
  - `Storage/SharedStore.cs`: tombstone, and delete this device's files
  - `Storage/LocalStore.cs`: delete the local state, bootstrap last
  - the CLI files and docs
- **Tests:**
  - **Plan:**
    - a device with no mappings
    - one mapping linked here and one not
    - a mapping shared with another device, which is never offered as an orphan
  - **Run:** the source folders are real folders with the same files; the shared files and the local state are gone; the backups are kept, or deleted when chosen.
  - **Fault injection:** a fault at each step leaves nothing shared deleted before every revert is done. A second run, given the same choices, finishes the job, and a choice already carried out (a deleted orphan) is no longer listed.
  - **Refusals:** an open journal; a running app.
  - **No new deletes outside `SafeFs`:** the existing test enforces it.
- **Verify on hardware: Mint first, then the Deck (Linux links) and Windows (Windows links), CLI, two homes.** Set up a with two applied mappings: one shared with b, and one that only a uses. Give a some backups, and copy a's source folders through the links to `~/sttest/<task>/before/`.
  1. **Dry run:** `device remove` lists 2 reverts, 1 orphan and the files to delete. The scratch tree's hash is unchanged.
  2. **Run:** `device remove --delete-orphan <only-a>:saves --confirm-remove`. Afterwards:
     - a's source folders are real folders, and `diff -r` against `before/` shows no difference
     - `devices/a.json` and a's markers are gone, and `removed/a.json` is there
     - the orphan's mapping file and its save folder are gone
     - b's mapping and its saves have the same hash as before
     - a's local folder has no `bootstrap.json`, `state.json` or `journal.json`
     - the backups are still there
  3. **After:** `status` with a's home says the device is not set up, and `init` with a new name works.
  4. **Running app:** with a process holding a source folder open, `device remove` refuses and names it.
  5. **Re-run:** kill the CLI after the first revert (a large test folder makes the window wide enough). `device remove` again with the same flags finishes the job.

### T6: remove another device; tombstone checks (M)

- **Deliver:**
  - In the core, `Workspace.RemoveOtherDevice(string name)`, which is refused for this device's own name.
  - `HealthCheck` and `StatusCheck` gain three conditions, each with a hint:
    - **removed by another:** this install ID is tombstoned, the bootstrap is still there, and the tombstone's `removed_by` is another device's name
    - **file came back:** a device file whose install ID is tombstoned
    - **removal unfinished:** the same as removed by another, but `removed_by` is this device's name. Also unfinished: the sync root is there with no `savetether/` folder, and nothing is linked here (T7)
  - `device remove` on a device that was removed by another finishes the removal locally: the reverts and the local state, with no shared files left to delete.
  - The CLI gets `device remove --other <name> [--confirm-remove]`.
- **Files:**
  - `Operations/Workspace.Remove.cs`, `Health/HealthCheck.cs`
  - `Gui.Presentation/OverallState.cs`: the new `OverallCondition` values (texts come in T8)
  - the CLI files and docs
- **Tests:**
  - **Remove the other device:** its files go, its tombstone is written, and this device is unchanged.
  - **Own name:** refused.
  - **Conditions:** each of the three, from a fixture. The same tombstone with `removed_by` set to this device gives "not finished", and with another name gives "removed by another".
  - **Name reused:** `init` reusing a tombstoned name removes the tombstone.
- **Verify on hardware: Mint, CLI, two homes.**
  1. **Remove the other device:** from b, `device remove --other a --confirm-remove`. a's device file and markers are gone and its tombstone is there; a's links on disk are untouched.
  2. **Removed by another:** `status` with a's home reports that b removed it. `device remove --confirm-remove` on a finishes the removal: the links become folders, and the local state is gone.
  3. **File came back:** copy a saved `devices/a.json` back by hand. `status` and `device list` on b flag it.
  4. **Unfinished:** a bootstrap, no device file, and this install's tombstone with `removed_by` set to a. `status` says the removal is not finished, not that another device removed it.

### T7: delete everything (S)

- **Deliver:** `RemovalChoices.DeleteEverything` and the CLI flag `device remove --delete-everything`.
  - **Refused** while a device file with another install's ID exists, naming those devices. A leftover file with this install's ID (from an unfinished rename) does not count.
  - **The run:** the removal steps, then `SafeFs.DeleteTree` of `<sync root>/savetether` with `SharedDataDeletion.ExplicitlyConfirmed`.
  - **Confirmation:** in a terminal, type the device name, then `delete everything` (D8). From a script, `--confirm-remove` and `--confirm-delete` are both needed.
- **Files:** `Operations/Workspace.Remove.cs`, the CLI files and docs.
- **Tests:**
  - **Refusal:** another device is named, and nothing changes.
  - **Run:** the `savetether` folder is gone and the local saves are intact.
  - **From scratch:** `init --create-root` on the same sync folder, then adding an app, works with nothing left over.
  - **Fault injection:** a fault during the tree delete, and one just after it, before the local steps. In both cases, `status` says the removal is not finished (not "sync root unavailable"), and a second run finishes the job: the shared steps are skipped and the bootstrap goes.
  - **Leftover from a rename:** a second device file with this install's ID does not block the option.
- **Verify on hardware: Mint and Windows, CLI, two homes.**
  1. With b registered, `device remove --delete-everything` on a is refused and names b.
  2. Remove b with `--other`, then run it on a. The scratch `savetether/` folder is gone, and a's source folders match `before/` (`diff -r`).
  3. **Crash after the tree delete:** set a up again with one linked mapping and run Delete everything. Then put back its `bootstrap.json` from a copy taken before the run, which gives the state a crash leaves. `status` says the removal is not finished, and `device remove --delete-everything --confirm-remove --confirm-delete` again finishes it: no bootstrap is left.
  4. `init --create-root` on the same sync folder, then `app add`, works. `ls -R` of the sync folder shows only the new files.

### T8: the app's logic and texts (M)

- **Deliver**, in `Gui.Presentation`:
  - **`MaintenancePage.From(MaintenanceOverview, ...)`:** the overview lines, the device rows (with Remove disabled for this device, and the tombstoned mark), the orphan rows, and why each action is disabled.
  - **`RemoveDeviceFlow`:** page 1's choices, with the defaults and D4's option, which is offered or disabled with its reason. Page 2's plan lines, taken from T5's plan. The result's text, with the folders given back.
  - **Checks:** `DeviceNameProblem(string, IReadOnlyCollection<string> taken)` and `TypedPhraseProblem(string typed, string phrase)`.
  - **The backup Clean up choices**, with their texts.
  - **Texts** for every refusal, banner and state in "The app: screens and dialogs".
- **Files:** `Gui.Presentation/MaintenancePage.cs` and `RemoveDeviceFlow.cs` (new), `Texts.cs`, and `OverallState.cs` for the banners.
- **Tests:**
  - **Rows:** the rows and lines for each state; every refusal has a text.
  - **Typed phrase:** matches with any case and with spaces at either end; does not match with spaces inside the phrase.
  - **Device name:** invalid or taken names are refused.
  - **Delete everything:** D4's option is disabled with two devices and offered with one.
- **Verify on hardware: Mint.** The presentation has no window to drive. Run `dotnet test --filter "Maintenance|RemoveDevice"` on Mint as well as on Windows. Then, from a two-home scratch setup, check that the numbers `MaintenancePage` shows (from a test run against that folder, or the CLI's `device list --json`) match the folder.

### T9: the app's screens (M)

- **Deliver**, in `SaveTether.Gui`:
  - the **Maintenance** button and `MaintenanceScreen`
  - `Overlay.AskTextAsync`
  - `RemoveDeviceScreen`, with its two pages
  - **Clean up** on `BackupsScreen`
  - the two banners, and the "removed by another device" screen
  - the move to `SetupView` after a removal, with no restart
  - the title and tray tooltip refreshed after a rename
  - a new section in `docs/design/desktop-app-spec.md`, "Maintenance", with "As built" notes
- **Files:** `MainWindow.cs`, `Overlay.cs`, `MaintenanceScreen.cs` and `RemoveDeviceScreen.cs` (new), `BackupsScreen.cs`, `ActionFlow.cs`, the spec, and the "Test flows by feature" in `docs/testing/hardware-testing.md` (a new "Maintenance" flow).
- **Tests:** the presentation tests from T8 cover the logic; the views are checked on hardware.
- **Verify on hardware: the Deck, the app driven with `xdotool`, two homes on one scratch sync folder.**
  1. **Overview:** Maintenance opens from the bar, and the overview's numbers match `device list --json`.
  2. **Rename:** type an invalid name and the button stays disabled with the message. Type a good name, and the window title, the tray tooltip (read through `gdbus`, as in the testing doc) and the page show it.
  3. **Remove another device**, with its typed name.
  4. **Clean up:** on Backups, keep-first. Then delete all backups: the warning, then the typed phrase.
  5. **Remove this device:** page 1, then page 2, then the typed device name (the button stays disabled until it matches), the progress, and the result listing the folders. OK lands on setup with no restart.
  6. **Captures:** each page and dialog at the default window size, and the text dialog with the on-screen keyboard open (Steam+X).

  **Mint:** the same screens open and lay out (captures), with clicks only. **Windows, the owner:** the flow above once, with a debug build and a scratch `SAVETETHER_HOME`.

### T10: exit test (S, hardware)

- **Deliver:** the exit test below, on the Deck and the Windows PC, on a throwaway folder that both devices sync, as in phase 3's exit test. Record the results in the plan's section 8 and in this file. Bugs found get a regression test.
- **Files:** this file, `implementation-plan.md` section 8, `docs/testing/hardware-testing.md`.

## Exit test

On a throwaway folder shared by the Deck and the Windows PC, each with a test app applied and a second app that only the Deck uses:
1. **Rename:** rename the Deck in the app. The PC's Maintenance shows the new name.
2. **Remove the Deck,** deleting its orphan with saves. The PC is untouched and its saves load. The Deck holds its save folders as ordinary folders, its local folder holds no SaveTether state, and the app is on setup.
3. **Backups:** on the PC, delete all backups but the first.
4. **Reset:** on the PC, remove the device with "Delete everything". The sync folder holds no `savetether/`, and the PC's save folders are ordinary folders with their files.
5. **Again:** set up both devices from scratch on the same folder, and apply the test app on each.

## Progress

### T1: design section — done (2026-11-22)

Closed by the owner. Note: design section 5.15 and the 5.6 line for `removed/` were not written in `docs/design/save-redirection-design.md`; the design of the removal, tombstone and rename flows stands in this file (above) until they are moved there.

### T2: overview — done

Branch `phase-3.6-t2-overview`, commit db48b6e.

- `Core/Operations/Maintenance.cs`: `Maintenance.Overview(Workspace)`, read only. `SharedStore.LastActivityOf` takes the newest of the device file's write time and the device's seed markers' `SeededUtc` (not marker file times, which another device's supersede rewrites).
- `device list` (CLI, `--json` as `devices`), in `docs/cli.md`, `docs/cli-json.md`, `json-shapes.txt`. `Tombstoned` is always false until T5.
- Tests: `DeviceListTests` (counts, orphan, read-only), the contract, the command table. `dotnet build -warnaserror` clean; 920 tests pass.
- Hardware check (2026-11-22, commit db48b6e, CLI Debug build, `home-a` and `home-b` on one scratch sync folder; `map add` with a plain folder stands in for `app add`, which T2 does not touch): on a, `apply --copy` of one mapping, then a second `map add` and `map remove` to orphan it; b joined. `device list` and `--json` on Windows and on Mint showed 2 devices (1 mapping each), 2 mappings with 1 linked here, the orphan `solo.saves`, and 1 backup. The sync folder listing agrees (2 device files, 2 mapping files, 1 seed marker). Backup bytes are the saved files' size (10 on Windows, with CRLF; 8 on Mint) and match what was written; `du` of the backup root is larger because it counts the manifest (the plan's "match `du`" is read that way; the flow is in `hardware-testing.md`, "Device list"). `status` of the real setup was the same before and after on both, and the scratch folders are gone.

### T4: backup cleanup — done

Branch `backup-cleanup-t4`, commit 78d6584.

- Core: `BackupCleanup` (`All`, `KeepFirst`, `KeepLast`), `BackupStore.Select` (what a mode would delete, reads only) and `BackupStore.RemoveMany` (deletes through `SafeFs`, also clears `.partial` folders); `Workspace.DescribeBackupCleanup` and `Workspace.CleanUpBackups` (refused while a journal is open).
- CLI: `backup delete --all | --keep-first | --keep-last [--mapping <id>]`, `--json` as `backups_cleaned` (`removed`, `dry_run`). Decision: the two keep modes always leave a backup, so they need no confirmation (the hardware check in this plan runs them bare); `--all` asks, or needs `--confirm-delete`, or lists only. Mixing a mode with a backup ID or another mode is a usage error (64).
- Tests: `BackupStoreTests` (modes over 1, 2 and 5, scope, partials, `Select` changes nothing), `BackupDeleteTests` (dry run, flag, keep modes), the contract. Docs: `cli.md`, `cli-json.md`, `json-shapes.txt`.
- Hardware check (2026-11-22, commit 78d6584, CLI on Windows (Debug) and on Mint (Release, linux-x64, built from a `git archive` of the commit), one scratch home and sync folder each, mappings `x.saves` and `y.saves`): 4 backups of x and 2 of y, made with `apply --copy`, `revert`, then `apply --add-missing` and `revert` again. A second `apply --copy` after a revert stops at the "identical copies" guard and takes no backup, so `--add-missing` is what makes the later cycles. Then, the same on both:
  1. `--keep-first --mapping x.saves` left x with its oldest backup and y with 2.
  2. After making x's backups again, `--keep-last --mapping x.saves` left only the newest.
  3. `--all` with no terminal and no flag deleted nothing and printed "Would delete ..."; with `--json` it gave `dry_run: true`.
  4. `--all --confirm-delete` left no backup folder in the backup root; `backup list` said "No backups." and the saves were intact.

  `backup list` agreed each time. `status` of the real setup was the same before and after on both, and the scratch folders and the Mint build are gone. A first script looped for ever because it assumed each apply and revert cycle makes backups (see the `--add-missing` note); it was stopped and its processes ended.

### T5: remove this device — done

Branch `phase-3.6-t5-remove-device`, commit 9f0d9ae.

- Core: `Workspace.PlanDeviceRemoval(RemovalChoices)` (reads only) and `Workspace.RemoveDevice(plan, progress)` in `Operations/Workspace.Remove.cs`. Order: reverts (each its own journalled operation, run through `Workspace.Run`), orphan choices (`Keep`, `DeleteMapping` through `RetireMapping`, `DeleteWithSaves` through `DeleteMapping`), backups if chosen (before anything shared is deleted, so a failing backup root leaves the device re-runnable), tombstone, markers and device file, local files with the bootstrap last. A revert that does not complete returns the failure and nothing shared is deleted. `SharedStore.WriteTombstone`, `ReadTombstones`, `FilesOf`; `LocalStore.DeleteDeviceFiles`; `TombstoneFile` in `Model/Files.cs`. `Maintenance.Overview` now sets `Tombstoned` from the tombstones.
- Decisions: the lock is taken by the CLI for the whole command (`Workspace.Run` takes none), so there is no double take; a dry run also takes it. A revert refused only because a mapping is not linked is skipped; any other refusal stops the removal and is shown. A choice naming a mapping another device uses is refused; A choice naming a mapping that does not exist is refused as a typo, except when this install's tombstone exists (an earlier run got that far), so a run again with the same flags finishes. Unreadable tombstones are skipped. An orphan's `Paths` entry is dropped from the device file before its delete (a mapping is unused only when no device file has it). On Windows `operation.lock` stays (open while the command runs).
- CLI: `device remove [--delete-orphan <id>[:saves]]... [--delete-backups] [--confirm-remove]`, `--json` as `removal`. Terminal: type the device name, and `delete everything` when `:saves` is used. No terminal and no flag: dry run. Docs: `cli.md`, `cli-json.md`, `json-shapes.txt`, `hardware-testing.md` ("Device remove").
- Tests: `DeviceRemoveTests` (dry run, run, mapping-only delete, keep, shared mapping refused, a mistyped mapping ID, running app, open journal, backups, a failing backup step, an unreadable tombstone, a failed revert), the contract, the command table. `dotnet build -warnaserror` clean; 949 tests pass.
- Hardware check (2026-11-22, commit 9f0d9ae; the same flow on Mint and on the Deck, CLI Release linux-x64 built on Mint from a `git archive` of the commit and copied to the Deck through the PC; Windows ran an earlier build of the branch, CLI Debug). `home-a` and `home-b` on one scratch sync folder under `~/sttest`; a has `game.saves` (shared with b, process `sleepy`, a copy of `sleep`) and `solo.saves`:
  1. Dry run lists 2 folders, the orphan and 3 files; the sync folder's hash is unchanged.
  2. `--delete-orphan game.saves:saves` is refused (b uses it); a mistyped ID is refused; both leave the tree unchanged.
  3. Running app: with a `sleepy` process running, the run is refused and names it, the tree is unchanged and the link is kept.
  4. Run with `solo.saves:saves --confirm-remove`: both folders are real folders and `diff -r` against copies taken before the apply shows no difference; the sync folder holds only b's device file, `game.saves` with its saves, and `removed/a.json`; a's home is empty; the backups are kept.
  5. After: `status` on a says not set up, `init --device a2` works, b's `status` is unaffected.
  6. Killed CLI: 4000 files linked, `device remove` killed with `kill -KILL` during the first revert; the journal was open and the link still there; `recover` undid the revert, and `device remove` again finished (no bootstrap, files back as a real folder).
  
  Same results on Mint and the Deck; on Windows steps 1 to 5 (junctions). `status` of the real setup was the same before and after on every machine, and the scratch folders and the copied binary are gone. A first script compared against copies taken after the apply (a link), which proved nothing: the copies are made before it now.

### T3: rename — built, hardware check not run

Branch `phase-3.6-t3-rename` (not committed yet).

- `Core/Operations/Workspace.Device.cs`: `Workspace.RenameDevice(newName)`. Order: write the new device file and marker copies, then `bootstrap.json` (the commit point), then delete every other device file (and its markers) that carries this install's ID. That one sweep serves the normal rename, a rename that stopped after the new files were written, and a rename that stopped after the commit (run again with the name the device already has). `SharedStore.DeleteDevice` and `DeleteSeedMarker` go through `SafeFs.RemoveSharedFile`.
- Refusals: invalid name, a name whose device file has another install's ID (`NameInUse`), this device's file held by another install, an open journal (`Conflict`, Recover; the CLI answers exit 3 first), a sync root that cannot be read.
- CLI `device rename <new-name>`, `--json` as `renamed` (`old_name`, `new_name`); `docs/cli.md`, `docs/cli-json.md`, `json-shapes.txt`, command table.
- Tests: `DeviceRenameTests` (files, bootstrap, health, `device list`; refusals change nothing; both unfinished states). `dotnet build -warnaserror` clean; 924 tests pass.
- A review added: an unreadable device file is skipped by the leftover cleanup (it runs after the commit point and must not stop a re-run), and a test for a sync folder that cannot be reached.
- Hardware check (2026-11-22, commit 232dca1, CLI on Windows (Debug) and on Mint (Release, linux-x64, built from a `git archive` of the commit, not a clone), `home-a` and `home-b` on one scratch sync folder, a with one applied mapping): `device rename a2` left `devices/a2.json` and `seed/game.saves/a2.json` and no `a.json` under either; `bootstrap.json` says `a2`; `status` on a2 is OK; `device list` from b shows `a2`. Refusals: the name `b` exit 1, `Bad Name` exit 1, an open journal made by hand exit 3. Re-run: with `a3.json` and its marker copied by hand and the bootstrap still on `a2`, `device rename a3` finished with one set of files, and `status` stayed OK. Same results on both. `status` of the real setup was the same before and after on both, and the scratch folders and the Mint build are gone.
