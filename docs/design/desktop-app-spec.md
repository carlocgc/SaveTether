# Desktop app specification (phase 3)

Status: draft, written 2026-11-17 for the owner's review. It specifies the desktop app of design 5.14 to the level of design 5.12. Each decision the owner has not yet made is marked **[owner]** and collected in section 15. Nothing here is built.

The design (`save-redirection-design.md`) says *what* SaveTether does and *why*. This document says what the window and tray icon show in every state, what each button does, and how every refusal the core can give reaches the person. Section numbers like "5.3" refer to the design.

## 1. Principles

1. **The app holds no logic of its own.** Every check, plan and change goes through the core (`Workspace`), exactly as the CLI does. The app decides only what to show and which choices to offer. If the app and the CLI disagree about what is allowed, the app is wrong.
2. **Only allowed choices are offered.** Where the CLI lists the allowed flags and refuses the others, the window offers only the allowed choices. A choice the core would refuse is never a button.
3. **The app speaks in its own words.** The core's messages are written for the CLI and name its flags (about 70 strings name a command or a flag today). The app writes its own sentence for each refusal *kind* and fills in the facts (paths, names, sizes) from the core's structured fields. The core's message is still shown, under "Details", so nothing the core knows is hidden. Section 13 lists the core change this needs.
4. **One operation at a time.** Within the app, and across processes, through the operation lock (section 9.1).
5. **Safe by default, with the keyboard alone.** Every dialog opens with focus on the choice that changes nothing. Escape always cancels or goes back. Nothing needs a mouse, a hover or a drag (section 12).
6. **Everything in one window.** Dialogs are overlays inside the main window, not separate OS windows. Under gamescope in Game Mode a second top-level window may open behind the game view or steal no focus at all (inference, to be checked with the Game Mode trial); an overlay avoids that question, and it is no worse on a desktop.

## 2. The process

- **One process, one copy.** `savetether-gui` holds a single-instance lock in the local folder (5.12, "Local files"). A second start sends "show the window" to the first copy and exits. Windows: a named mutex plus a named pipe. Linux: a lock file plus a Unix socket in the same folder. The operation lock (9.1) is separate: the app holds it only while an operation runs.
- **Start.** With `--minimized` (used by the autostart entry, section 11) the app starts with the tray icon only. Without it the window opens. Either way it runs a health check at once.
- **Closing the window** hides it and leaves the tray icon running. The first time this happens, a notification says: "SaveTether is still running in the tray. Quit it from the tray icon's menu." Where there is no tray host (stock GNOME, gamescope), closing the window quits. **[owner]**: detecting "no tray host" is not reliable on Linux (plan section 5, trial notes). Proposed rule: on Linux, the app checks for a `org.kde.StatusNotifierWatcher` owner on the session bus at start; if none, closing the window quits.
- **Quit** (tray menu, or Ctrl+Q in the window) is refused while an operation runs: "Wait for the current operation to finish." Operations are journalled transactions; stopping one half way is what `recover` exists for, not something to offer.
- **Log.** The app appends one line per operation, refusal and unexpected error to `gui.log` in the local folder, and keeps the last 1 MB. No paths beyond those already in the local files, no content of save files.

## 3. Overall state and the tray icon

The overall state is computed from one `Status()` report plus the journal, the lock and the setup state. The first row that matches wins.

| # | Condition | Icon | Tray menu first line | Window header |
|---|---|---|---|---|
| 1 | No `bootstrap.json` (device not set up) | amber | Not set up | the setup page (section 6) |
| 2 | A file has a newer schema than this version knows (`StoreProblem.SchemaTooNew`) | amber | Update SaveTether | banner B1 |
| 3 | Journal open (unfinished operation) | amber | An operation did not finish | banner B2 |
| 4 | Sync root unavailable (`SyncRootProblem` set) | amber | Sync folder not available | banner B3 |
| 5 | Device file mismatch | amber | Device name used by another install | banner B4 |
| 6 | Any mapping not OK and not Not applied | amber | *n* apps need attention | rows sorted to the top |
| 7 | Otherwise | green | All linked | "All linked" |

- **Not applied stays green** (5.14): a mapping set up on another device and not wanted here is normal.
- **The lock does not change the state.** While another process holds the operation lock (a CLI `apply`, the Decky plugin), the folder it is working on may briefly look Replaced (renamed aside, 5.3). So while the lock is held by another process, a check's result is shown in the window but does not change the icon or raise a notification; the next check after the lock is released does.
- **Tray menu**: the first line above (disabled, as a label), then "Open SaveTether", "Check now", a separator, "Quit". Clicking the icon opens the window; the trial found the icon's position cannot be known reliably on Linux, so there is no panel beside the icon (plan section 5).
- **Icons**: two images, green and amber, each with a distinct shape as well as colour (a tick and an exclamation mark), so the state does not depend on telling colours apart.

### 3.1 Checks and notifications

- **When**: at start, every 5 minutes, when the window is shown or the tray menu opens, and after every operation. "Check now" in the window header and the tray menu. A check takes milliseconds (5.5); it runs off the UI thread, and a check already running is not started twice.
- **Notification** when a check finds a mapping in an amber state that it was not in at the previous check, or a new amber condition from rows 1 to 5. One notification per check at most, summarising: "Ryujinx saves: not linked. Eden saves: shared folder missing." At start, an amber state is notified once: this is the login check that 5.5 replaced with the tray icon. Nothing is notified when the state turns green; the icon changes.
- **How**: Windows, a toast through PowerShell's `Windows.UI.Notifications` (no new dependency); Linux, `notify-send` when it is on `PATH`, otherwise none. A notification that cannot be shown is logged and otherwise ignored. Clicking a notification is not required to do anything **[owner]**: opening the window from a PowerShell toast needs an app ID registration, which the release archive does not do.

## 4. Window layout

Minimum 800 × 560, designed for 1280 × 800 (the Deck's screen) at 100 % scaling, base text 15 px.

- **Header**: device name, sync folder path, the overall state word with its icon, "Check now", and the time of the last check.
- **Banners**: zero or more, below the header, in the order of section 3's table. Each says what is wrong in one sentence and has its actions as buttons.
- **Pages**, as a tab strip under the banners: **Apps** (default), **Backups**, **Settings**. Ctrl+1, Ctrl+2, Ctrl+3 switch pages; the tab strip is also reachable with Tab.
- **Dialogs** are overlays over the page, with the page dimmed and not focusable underneath.
- **Busy**: while an operation runs, every action button in the window is disabled, and the operation's dialog shows its progress (section 9.3).

### 4.1 Banners

| # | When | Sentence | Actions |
|---|---|---|---|
| B1 | A shared or local file has a newer schema | "A newer version of SaveTether has written <file>. Update SaveTether on this device before going on." | Open the releases page **[owner]** (a link, so the only network access is the person's own browser), Details |
| B2 | Journal open | "<Operation> of <mapping> did not finish. Nothing else can be done until it is recovered." | **Recover** (section 9.5), Details |
| B3 | Sync root unavailable | "The sync folder <path> is not available: <reason>. If it is on a drive or a network share, connect it." The reason is "it does not exist" or "the SaveTether folder is not in it", from the core's two cases (5.12, "Sync root unavailable") | Check again, Open the folder's parent |
| B4 | Device file mismatch | "Another SaveTether install uses the device name '<name>'. Two machines with one name would apply each other's folders." | **This is that device** (take over, section 6.3), **Use another name** (section 6.2), Details |
| B5 | The autostart entry points to a missing file | "SaveTether is set to start with the session, but the entry points to <path>, which is missing." | **Fix it** (rewrite the entry, section 11), **Turn it off** |
| B6 | The lock is held by another process | "Another SaveTether operation is running (<command>, started <time>). Actions are paused until it finishes." | none; the app re-checks the lock every 3 seconds while the window is visible |

While B1, B2 or B3 is shown, every action on the Apps and Backups pages is disabled, except "Open folder". The core refuses these operations anyway; disabling them avoids a click that can only fail.

## 5. Apps page

Mappings are grouped by app: the part of the mapping ID before the dot, named from the app's definition when there is one ("Ryujinx"), otherwise the ID itself. Inside an app, one row per role (mapping), named from the definition's role name ("Saves") or the part after the dot.

Each row shows: the state word and icon, one sentence, and the folder on this device. Rows needing attention, and the apps holding them, sort to the top; the rest are in name order.

Page actions: **Add app** (section 8), **Apply all** (section 9.4, shown when at least one row is Ready to link).

### 5.1 States

The core's `HealthState` has seven values. Replaced and Not applied each cover situations that need different words and actions, so the app needs the core to say which (section 13, item 2).

| Core state | Situation | Word | Colour | Sentence | Main action | Other actions |
|---|---|---|---|---|---|---|
| OK | | Linked | green | "Saves here are the shared copy." | none | Open folder, Open shared folder, Backups, Revert |
| Not applied | no folder set on this device | Not on this device | grey | "Set up on <devices>, not here." | Add (opens Add app at this app) | none |
| Not applied | folder set, not linked | Ready to link | grey | "Not linked yet." | **Apply** | Open folder, Stop using here |
| Not applied | the folder in the device file is not an absolute path | Folder setting is wrong | amber **[owner]** | "This device's file has '<value>' as the folder." | Add again | Details |
| Replaced | the folder holds files | Not linked | amber | "The link was replaced by a real folder. Saves made here are not shared." | **Apply** (stops at the choice, section 9.2) | Open folder, Backups |
| Replaced | the folder is empty | Link missing | amber | "The link is gone and the folder is empty." | **Apply** | Open folder |
| Replaced | nothing at the folder | Folder gone | amber | "Nothing is at <path>. If the app has moved, find it again." | **Find the app again** (Add app, section 8) | Apply |
| Broken | | Shared folder missing | amber | "The link is in place, but the shared folder is missing. It may not have synced yet." | Check again | Open folder, Open shared folder's parent |
| Retargeted | | Linked elsewhere | amber | "The folder now links to <path> instead of the shared folder. Remove that link by hand, then apply." | Open folder | Details |
| Double seed | | Two shared copies | amber | "More than one device made its saves the shared copy: <devices>." | **Use this device's copy** (section 9.2, Double seed) | Backups |
| Foreign link | | Not SaveTether's link | amber | "The folder is a link SaveTether did not make, to <path>. Remove it by hand, then apply." | Open folder | Details |

"Not applied, no folder set" counts as grey but is listed after everything else, under a collapsed "Not on this device (*n*)" heading, so a device that uses three of ten shared apps is not cluttered.

**Stop using here** is `map remove`: refused by the core while applied, so it is only offered when Ready to link. It asks for confirmation: "Forget this device's folder for <role>? Nothing is deleted; the shared copy and other devices are not affected."

The "Folder setting is wrong" row turns the overall state amber, unlike the core's Not applied. **[owner]**: the alternative is to keep it green as the CLI does, at the cost of a silent misconfiguration.

## 6. First-run setup

Shown instead of the pages when there is no `bootstrap.json`, and reachable later from banner B4. Three steps, each a page with Back and Next; the result is one `Workspace.Init` call at the end, so nothing is written until the last step.

### 6.1 Sync folder

A path field and a "Choose folder" button. The folder picker is the OS dialog; under gamescope it may not work **[owner, Game Mode trial]**, so the field accepts a typed path too.

On Next the app checks the folder and shows one of:

| Found | Shown | Offered |
|---|---|---|
| `savetether/.savetether/` exists | "This sync folder is already set up for SaveTether, on: <device names>." | Next |
| the folder exists but has no SaveTether folder | "This sync folder is not set up for SaveTether yet." and the two cases below | **This is my first device** (create-root), or Back |
| the folder does not exist | "<path> does not exist. If it is on a drive or a network share, connect it." | Back |

"This is my first device" asks for confirmation: "Only do this on the first device. If SaveTether is set up on another device, wait until the sync tool has delivered the SaveTether folder here instead; setting it up twice creates two separate setups." This is the GUI form of 5.3 invariant 5's protection against an empty mount point.

### 6.2 This device's name

- Existing device files are listed first, each with "Take over" (used after a wipe, 5.4). Below them, a field for a new name, filled with a suggestion from the machine's host name, lowercased, with characters outside `a-z 0-9 - _` dropped.
- The name is checked as typed against `Identifiers.IsValidDeviceName`, with the rule under the field.
- **Take over** needs confirmation (5.6): "Only take over '<name>' if this is that device, set up again. Two machines with one name would apply each other's folders." Buttons: Cancel (focused), Take over.

### 6.3 Backups, and finish

- The backup location: the default, or the one in the device file being taken over, with "Change". A location from the device file that does not exist is shown with the core's note (5.12, `init`): its drive may not be connected.
- **Set up** runs `Init`. A `StoreException` is shown in place with the step it belongs to, and nothing moves on.
- After setup the window shows the Apps page. When the device was taken over and has folders set (a restore after a wipe), it also shows: "This device has <n> apps set up. Apply them all now?" with **Apply all** (section 9.4) and Not now.
- First run only, before step 6.1: the install step of section 11, when the app is not running from the per-user location.

## 7. Operation flow, common to every action

Apply, Revert, Restore, Recover, Add app, Stop using here, the Flatpak grant and its revoke, Take over and changing the backup location are operations. Each:

1. Takes the operation lock (9.1). If another process holds it: "Another SaveTether operation is running (<command>). Try again when it has finished." Nothing else happens.
2. Plans (where the core plans): `PlanApply`, `PlanRevert`, `PlanRestore`, `PlanAppAdd`.
3. On a refusal, shows it (section 9) and releases the lock.
4. On a plan, shows the confirmation for that action, with the plan's steps in plain words under "Show steps". The lock is held while the dialog is open, so the plan cannot go stale behind it; a dialog left open for 10 minutes is cancelled and the lock released **[owner]**.
5. Runs the plan with progress (9.3), then shows the result (9.3), releases the lock, and runs a health check.

## 8. Add app

A guided `app add` (5.2, "Adding an app"; plan P2.5). Two pages and a confirmation.

**Page 1, choose an app.** The catalog's apps (shared definitions, then built-in, as `app list` shows them), in name order, with a search field that filters as typed. Next to each, filled in the background: "Found", "Not found" or "Added" (already has a folder on this device). Opening the page does not wait for detection.

**Page 2, the plan.** `PlanAppAdd` with what is known so far; then, by outcome:

| Outcome | Shown | Offered |
|---|---|---|
| Plan | The install used and what showed it is there; each role with its class, folder, size and file count, "already a link" or "not there yet" where so; the definition's quirks; each role's note | Role checkboxes (the definition's defaults ticked; `cache` and `media` classes unticked and labelled why), **Add** |
| `install-choice-needed` | "More than one install of <app> was found." and the candidates, each with what was found | Pick one, then the plan |
| `not-installed` | "<App> was not found on this device." with each install looked for and why it was not found | The fields below, for installs whose `ResolveProblem` is `needs-value`; Back |
| `unresolved` | "<Role>'s folder could not be worked out: <reason>." | The field that would resolve it, if any; Back |
| `unknown-install`, `unknown-role` | Cannot happen from the window, which only offers what the definition lists. Shown as an unexpected error (section 10) | |
| `invalid-definition` | "The definition for <app> has mistakes, so it cannot be used." with the core's message | Back |
| `conflict` | "<Role> is already set up differently: <facts>." Covers another target recorded, a role applied at another folder (revert it first), and a folder another mapping uses | Back |

**Fields** only for what cannot be found by looking (5.14): install folder, SD card, Wine prefix. Each is a path field with "Choose folder". After a field changes, the plan is made again.

**Add** runs `AddApp`. It changes no save data, so the confirmation is the plan page itself. Afterwards: "<App> added. Apply it now?" with **Apply** (section 9.2, for each role added) and Not now.

**Re-running for a moved app.** "Find the app again" on a Folder gone row opens page 2 for that app directly. The core's rule for a folder that is applied at the old place (revert first, unless the link moved with the folder) arrives as `conflict`.

**Add a folder by hand** **[owner]**: a link at the bottom of page 1, for apps with no definition. A form for `map add`: mapping ID, target, save folder, process names, entry depth. Proposed: include it, since it is the only way to add an unlisted app without the CLI, but it can wait for a later step of phase 3.

## 9. Apply, revert, restore, recover

### 9.1 Operation lock

Plan section 5 lists the lock as its own item; this is the behaviour both front ends rely on.

- File `operation.lock` in the local folder, opened with an exclusive share mode for the length of the operation. The OS releases it when the process ends, however it ends, so a killed process never leaves a lock that blocks. (.NET implements `FileShare.None` on Linux with an advisory `flock`, which is enough because every party is SaveTether; to be proved by the lock's tests.)
- While held, the file contains one line of JSON: process ID, front end (`cli`, `gui`), command, start time. Read by the other process only for banner B6's text.
- Taken by every command that writes: `init`, `map add`, `map remove`, `app add`, `apply`, `revert`, `backup restore`, `recover`, the Flatpak grant and revoke. Not by `status`, `backup list`, `app list` or a dry run.
- CLI refusal: exit 1, "Another SaveTether operation is running (<front end> <command>, started <time>)." `--json`: a refusal with kind `operation-running`.

### 9.2 Apply

**Apply** on a row runs `PlanApply` with no choice. By outcome:

**A plan.** A confirmation: "Link <role> on this device to the shared folder?" with the save folder and the shared folder in full (5.3 requires the plan to print both), "A backup is taken first" when the folder has data, and the steps under "Show steps". Buttons: Cancel (focused), Apply.

When the plan has `NeedsFirstSeedConfirmation`, the confirmation adds: "If <role> was already set up on another device, cancel and wait until it has synced here. Otherwise both devices will make their own saves the shared copy." (5.3, the one race.)

**A refusal**, by `RefusalKind`:

| Kind | Shown | Offered |
|---|---|---|
| `choice-needed` | The choice dialog, below | |
| `choice-not-allowed` | Cannot happen: the dialog offers only `AllowedChoices`. Shown as an unexpected error | |
| `confirmation-needed` | The replace confirmation, below | |
| `not-arrived` | "A device has shared these saves, but the shared folder here is still empty. The sync tool has probably not delivered it yet." | **Check again** (focused), and under "More": "The shared saves really were deleted", which re-plans with `AssumeSynced` after a confirmation |
| `foreign-link` | The Foreign link sentence from 5.1 | Open folder |
| `broken-link` | The Broken sentence from 5.1 | Check again |
| `precondition` | Every failure, one line each, from the table below | The actions in the table |

**Precondition failures**, by `PrecheckKind`. Several can come together (5.3, "Order of checks"); each is listed.

| Kind | Sentence | Offered |
|---|---|---|
| `sync-root-not-initialized` | Banner B3 covers it | |
| `unfinished-operation` | Banner B2 covers it | |
| `app-running` | "Close <app> first. SaveTether looked for: <process names>." | **Check again** (focused), and under "More": "<App> is closed", which re-plans with `AssumeClosed` after a confirmation: "SaveTether thinks <app> is running. Linking while it runs can lose the saves it writes on exit. Go ahead only if it is closed." |
| `sandbox-cannot-reach` | "<App> is a Flatpak and cannot write to the shared folder. SaveTether can give it access to the SaveTether folder in the sync root, and nothing else." | **Give access** (the grant, then re-plan), Cancel. Offered only when the core says the grant is the only thing in the way, as the CLI does (5.3, Flatpak precondition); otherwise the sentence alone, with Details |
| `source-parent-missing` | "The folder that should hold the save folder does not exist: <path>. The app may have moved, or not been started yet." | Find the app again |
| `backup-location-unusable` | "Backups cannot be written to <path>: <fact>." | Open Settings |
| `not-enough-space` | "Not enough free space on <volume>: <need> needed, <free> free." | none |
| `target-not-local` | "On Windows the sync folder must be on a local drive, because a folder link cannot point to a network drive." | none |
| `source-inside-sync-root`, `paths-overlap`, `target-outside-data-root`, `source-unusable`, `target-unusable` | "<role> cannot be linked as it is set up: <fact>." | Details |
| `not-applied`, `backup-unusable`, `restore-destination-unusable` | Revert and restore only; see 9.6 and 9.7 | |

**The choice dialog** (`choice-needed`). Title: "<Role> on this device already has saves." Then the comparison and the allowed choices.

- **Comparison** (5.4), grouped by entry (5.13's entry depth): "Only on this device", "Only in the shared copy", "On both, different", and a collapsed "On both, the same (*n*)". Each entry expands to its files with size and time. Above the groups: "Newest here: <time>. Newest in the shared copy: <time>."
- **Choices**, one per `AllowedChoices` value, as a list where nothing is selected at first. Continue is disabled until one is.

| Choice | Label | Sentence |
|---|---|---|
| `copy` | Share this device's saves | "These saves become the shared copy for every device." |
| `no-copy` | Use the shared saves (when the shared folder has data); Start empty (when it is empty) | "This device's saves are kept in a backup only. <App> will see the shared saves." / "This device's saves are kept in a backup only. <App> will see no saves until another device shares some." |
| `add-missing` | Use the shared saves, and add games only this device has | "Copies entries the shared copy does not have. Nothing in the shared copy is changed or deleted." |
| `replace-shared` | Replace the shared saves with this device's | "Every device's copy is replaced. The shared saves are backed up on this device first." |

- Every option ends with: "This device's saves are backed up first, whichever you choose."
- When `AssumeSyncedWouldHelp`, `add-missing` and `replace-shared` are shown disabled with: "The shared copy may still be arriving: it lacks files a device shared. Wait for the sync tool, or confirm it has finished." and a checkbox "It has finished syncing", which re-plans with `AssumeSynced`.
- `replace-shared` leads to the replace confirmation.

**The replace confirmation.** Its own dialog, never combined with the choice: "Replace the shared saves for <role> on every device?" then: "The shared copy is backed up on this device first. Then it is deleted and replaced with this device's saves. The deletion reaches every device through the sync tool." Buttons: Cancel (focused), **Replace on every device**. **[owner]**: the CLI needs a typed "yes"; the window could ask for the app's name to be typed as well. Proposed: no typing, because typing is fiddly in Game Mode and the separate dialog with Cancel focused already makes it deliberate.

**Double seed.** "Use this device's copy" runs `PlanApply` with `replace-shared`. On a device where the folder is already the link (classification row 1) the confirmation instead says: "Make this device's copy the one every device keeps? No files change; the other devices' claims are marked as replaced." Where the sync tool kept the other device's files and this device's should win, the route is revert, restore and replace (5.3); the dialog says so and links to Backups.

### 9.3 Progress and results

- **Progress**: the plan's steps as a list, each ticked as the executor finishes it. Needs a progress callback from the executor (section 13, item 3).
- **Result**, by `ExecutionStatus`:

| Status | Shown |
|---|---|
| `Completed` | "<Role> is linked." (or reverted, restored), with the backup taken, and the executor's messages |
| `Refused` | As a refusal (9.2) |
| `RolledBack` | "<Operation> failed and was undone. Nothing has changed: <error>." with the executor's messages |
| `RecoveryNeeded` | Banner B2 appears; the dialog says "<Operation> stopped part way. Recover it before doing anything else." with **Recover** |

### 9.4 Apply all

For every row in the Ready to link, Not linked or Link missing state, one `PlanApply` with no choice, in name order, each its own operation. A mapping that needs a choice or is refused is skipped, and stays on the page with its reason. Rows that needed no choice are applied after one confirmation listing them all. Afterwards: "<n> linked. <m> need you:" and the list, each opening its own Apply.

This differs from the CLI's `apply --all`, which stops at the first mapping needing a choice. The window can show all of them at once, so it carries on.

### 9.5 Recover

Banner B2's **Recover** runs `Workspace.Recover()` after a confirmation that shows what is unfinished (the journal's operation and mapping) and that recovery either undoes or completes it (5.3, journal table). The result is shown as in 9.3. A shared-folder restore that recovery will not carry through reports the backup ID; the result shows it with "Open Backups".

### 9.6 Revert

**Revert** on a Linked row, after a confirmation: "Give this device its own copy of <role>? The shared saves are copied here and the link is removed. Other devices keep sharing." A refusal of kind `not-applied` (the folder is no longer this mapping's link) is shown with the row's current state.

After a revert, when the core says a Flatpak grant is no longer needed (`SandboxGrantNoLongerNeeded`), the result offers **Remove access** and Keep. Keep is focused, since removing it is only tidying up.

### 9.7 Backups page and restore

- A list grouped by mapping: time, reason (`pre-apply` "before linking", `pre-revert` "before reverting", `pre-restore` "before restoring", `baseline` "first backup", `snapshot` "snapshot"), whether it is of this device's folder or of the shared copy, size and file count. A filter by app. "Open folder" opens the backup's data folder: a backup is a plain tree (5.12) and can always be copied back by hand.
- **Restore**: `PlanRestore`. A refusal of kind `restore-destination-unusable` because the folder is a link: "<Role> is linked, so restoring would write into the shared copy. Revert first." with **Revert**. `backup-unusable`: "This backup is incomplete or damaged, so it cannot be restored." The first backup of a mapping is marked "Kept" (retention never removes it, 5.12).
- A backup of the shared copy needs a confirmation like the replace confirmation: "Restore the shared saves for <role> on every device?"

## 10. Errors outside the tables

- **`StoreException`**: shown in the dialog or page that caused it. Its `StoreProblem` picks the title (`NotFound` "Not found", `Invalid` "Not valid", `Conflict` "Already set up differently", `NotInitialized` banner B3, `SchemaTooNew` banner B1), and the message is the core's, which must therefore be free of CLI wording (section 13, item 1).
- **Anything else** (an `IOException` the core did not expect, a bug): "Something went wrong: <exception message>." with "Copy details" (type, message, stack trace) and a pointer to `gui.log`. The app keeps running and runs a health check, since the journal decides whether anything is unfinished. An exception from a background check is logged and the icon keeps its last state; three in a row turn it amber with "Health check failed".

## 11. Settings, installing, starting with the session

**Settings page.**

- **Start with the session**: a switch. On writes the entry, off removes it. Linux: `~/.config/autostart/savetether.desktop` (`$XDG_CONFIG_HOME` honoured), `Exec=<path> --minimized`. Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `SaveTether`, `"<path>" --minimized`. With the switch on, the app rewrites the entry with its current path at every start (5.14, "Distribution").
- **Backups**: the location, with **Change**, which runs `Init` again with the new backup root after a confirmation that existing backups stay where they are and are no longer listed. **[owner]**: moving them is not offered; the core has no operation for it.
- **This device**: name and sync folder, read only. Changing either is "Set up again", which shows the setup pages with the current values filled in. **[owner]**: the core's `Init` can point an install at a new sync root, but whether mappings applied under the old one should block that is not specified anywhere yet. Proposed: refuse while any mapping is applied here, as `map remove` does.
- **About**: version, the local folder (with Open), the licence.

**Installing.** The release archive is unpacked anywhere. On first run, when the app is not in the per-user location (`~/.local/bin` on Linux, `%LOCALAPPDATA%\Programs\SaveTether` on Windows), the setup starts with: "Install SaveTether for this user?" It copies both binaries (and the app's native libraries) there, starts the copy and quits; the unpacked files are left for the person to delete. Not now carries on from where it is. On Windows, an option "Make the `savetether` command available in terminals" adds the folder to the user's `PATH` **[owner]**: a persistent change to the user's environment, so it is opt-in and unticked.

**Autostart pointing to a missing binary.** The CLI's `status` reads the entry and warns when its path is missing (`autostart_missing: <path>` in `--json`); the app shows banner B5. Neither changes the entry without being asked.

## 12. Keyboard, focus and Game Mode

- Everything is reachable with Tab, Shift+Tab, the arrow keys, Enter and Escape, and focus is always visible (a 2 px outline in the accent colour).
- Lists move with the arrow keys; Enter opens a row's main action, and the Menu key or Shift+F10 opens its other actions as a list, never a hover menu.
- Every dialog opens with focus on its safe button. Escape is Cancel or Back everywhere. Enter never confirms a destructive choice from a text field.
- No tooltip carries information that is not also on screen.
- Text fields only where a value cannot be picked (device name, a path the picker cannot reach, search). Every path field has "Choose folder".
- **Game Mode.** The app is added as a non-Steam shortcut, with Steam Input mapping the controller to keys: d-pad to arrows, A to Enter, B to Escape, shoulder buttons to Tab and Shift+Tab. There is no tray there, so the app is just its window. The Game Mode trial (plan section 5) must confirm the window gets focus and the keys arrive; if it fails, the app is supported in Desktop Mode only on the Deck and the Decky plugin (phase 4) covers Game Mode.
- Screen readers: every control has an accessible name (Avalonia's `AutomationProperties.Name`). Not tested beyond that in phase 3.

## 13. Changes needed in the core and the CLI

These come before or with the app, each its own small PR.

1. **Messages without CLI wording.** Core messages state what is wrong and the facts; the flag or command to use is added by the CLI's formatter, keyed by the refusal kind, as the app adds its button. About 70 strings across 10 core files today. The CLI's output must not change, which the existing CLI tests check. This removes the string replace in `CliApp` that strips " Add --confirm-replace to go ahead." from a core message.
2. **Health reason.** `MappingHealth` gains a reason for the cases one state covers: Replaced (has files, empty, missing) and Not applied (no folder set, folder set, folder not absolute). Additive in `status --json`.
3. **Progress.** `Workspace.Run` and `Recover` take an optional `IProgress<PlanStep>`, reported after each step is journalled done. The CLI passes none.
4. **Operation lock** (9.1), in the core, used by the CLI and the app.
5. **Autostart check** in `status` (section 11), once the app writes entries.
6. **Facts as fields.** Where an app sentence above needs a fact the core only has inside a message (not-enough-space's sizes, app-running's process names, a conflict's other folder), the refusal carries it as a field. Found while writing the app's texts; additive.

The JSON contract freeze (plan section 5) should come after items 1, 2 and 6, since they add fields to the output.

## 14. Code layout and testing

- **Projects**: `SaveTether.Gui` (Avalonia: views, the tray icon, the single-instance and notification code), and `SaveTether.Gui.Presentation` (view models, the app's texts, the state tables of sections 3 and 5.1, the flows of sections 6 to 9) with no Avalonia reference. The presentation project depends on the core only; the GUI project on both. The CLI stays free of the UI toolkit, as approved.
- **Bindings** are compiled bindings (Native AOT, plan section 5).
- **Tests** for the presentation project go in the existing test project, against real temporary folders as everywhere else (5.8):
  - Section 3's table, row by row, and its order.
  - Section 5.1's table: every `HealthState` and reason maps to a word, a colour and a main action.
  - **Exhaustiveness**: every value of `RefusalKind`, `PrecheckKind`, `AppAddProblem`, `StoreProblem`, `ResolveProblem` and `ExecutionStatus` has an app text; a new enum value without one fails a test.
  - The choice dialog offers exactly `AllowedChoices`, for each classification and with and without `AssumeSyncedWouldHelp`.
  - Notifications fire once per change, not per check, and not while another process holds the lock.
  - Each flow of sections 6 to 9 driven through its view model against fixture devices, including each refusal.
  - Autostart entries compared with expected text.
- **Not testable in CI**: the tray icon, notifications, the folder picker, Game Mode. These are the phase 3 lines of the hardware checklist (plan section 8).

## 15. Decisions for the owner

| # | Question | Proposed |
|---|---|---|
| 1 | On Linux with no tray host, should closing the window quit? (2) | Yes, detected by a StatusNotifierWatcher on the session bus |
| 2 | Clicking a notification on Windows opens nothing (3.1) | Accept for phase 3 |
| 3 | Banner B1 links to the GitHub releases page (4.1) | Yes; it opens the person's browser, the app makes no request |
| 4 | "Folder setting is wrong" turns the icon amber, unlike the CLI's Not applied (5.1) | Yes |
| 5 | A confirmation dialog left open cancels after 10 minutes, releasing the lock (7) | Yes |
| 6 | "Add a folder by hand" in the window (8) | Yes, late in phase 3 |
| 7 | Replace confirmation without typing (9.2) | No typing |
| 8 | Changing the backup location does not move existing backups (11) | Accept, and say so |
| 9 | Changing the sync folder is refused while any mapping is applied (11) | Yes |
| 10 | Opt-in "add to PATH" on Windows (11) | Offer it, unticked |
| 11 | Two projects, view models without Avalonia (14) | Yes |
