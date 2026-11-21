# Command line reference

`savetether` is the command line half of SaveTether. It does everything the desktop app does, and the app calls the same code. `savetether --help` prints a summary of every command; this page has the details, with examples.

For scripts, every command also has a JSON form, described in [cli-json.md](cli-json.md).

- [Concepts](#concepts)
- [Options every command takes](#options-every-command-takes)
- Commands: [init](#init) · [map add](#map-add) · [map remove](#map-remove) · [map delete](#map-delete) · [app add](#app-add) · [app list](#app-list) · [status](#status) · [apply](#apply) · [revert](#revert) · [backup list](#backup-list) · [backup restore](#backup-restore) · [backup delete](#backup-delete) · [recover](#recover) · [uninstall](#uninstall)
- [Workflows](#workflows)
- [Files and environment](#files-and-environment)
- [Exit codes](#exit-codes)

## Concepts

| Term | Meaning |
|---|---|
| **Sync root** | A folder kept the same on every device: by a sync tool such as Syncthing, a cloud storage folder, or a network share on Linux ([ways to share the folder](sharing.md)). SaveTether uses one folder inside it, `savetether/`, and touches nothing else there. |
| **Device** | One machine, with a name such as `deck` or `desktop`. Each device records its own save folder paths; no device reads another's paths. |
| **Mapping** | One save folder that is shared, named `app.role`, for example `ppsspp.savedata`. Its **target** is where the shared copy lives in the sync root (`ppsspp/SAVEDATA` means `<sync root>/savetether/ppsspp/SAVEDATA`). Its **source** is the app's save folder on this device. |
| **Apply** | Move the save folder's contents into the shared copy (or start from the shared copy) and put a link where the folder was. The app keeps reading and writing the same path. |
| **Revert** | Give the device a real folder again, holding a copy of the shared saves. |
| **Backup** | A verified copy taken before any change, kept outside the sync root on this device. Retention keeps each mapping's first backup and the newest five. |
| **Definition** | A file describing an app: where its installs are found and which save folders it has. It lets `app add` find folders instead of you typing them. |

SaveTether never deletes data it has not backed up, never picks between two copies of saves on its own, and changes nothing while it is unsure. When it stops, it says why and what to do.

## Options every command takes

| Option | |
|---|---|
| `--json` | Print one JSON object on standard output instead of text, and never ask anything. The format is a stable contract: [cli-json.md](cli-json.md). |
| `--help` | Print the summary of every command. |
| `--dry-run` | On `apply`, `revert`, `backup restore` and `app add`: show what would be done, and change nothing. |

`savetether --version` prints the version.

At a terminal, some commands ask before they go ahead: a device name, which install to use, or whether to replace shared saves. Without a terminal (a script, a pipe) or with `--json`, nothing is asked, and the command needs the flag that answers the question instead.

## init

```
savetether init --sync-root <path> [--device <name>] [--create-root] [--take-over] [--backup-root <path>]
```

Sets this device up: ties it to a sync root and gives it a name.

| Option | |
|---|---|
| `--sync-root <path>` | The folder your sync tool keeps in step. |
| `--device <name>` | This device's name: lowercase letters, digits, `-` and `_`, starting with a letter or digit, up to 40 characters. Asked for at a terminal when left out. |
| `--create-root` | Create `savetether/` in the sync root. **Only on the first device.** Every other device waits until the sync tool has delivered that folder; setting it up twice would make two separate setups. |
| `--take-over` | Claim a name another install already uses: this device set up again after a reinstall. Two running machines must never share a name. |
| `--backup-root <path>` | Where backups go. Default: `backups` in the local folder. Must be outside the sync root. |

Running `init` again with the same values changes nothing. A different sync root is refused while any mapping on this device is linked into the old one: revert those first. On Windows, a sync root on a network drive (a mapped drive letter or a `\\server\share` path) is refused, because Windows junctions can only point to local drives; [ways to share the folder](sharing.md#a-network-share) has the alternatives.

```bash
# The first device:
savetether init --sync-root ~/Sync --device desktop --create-root

# Every other device, once ~/Sync/savetether has arrived:
savetether init --sync-root ~/Sync --device deck

# The same device after a reinstall:
savetether init --sync-root ~/Sync --device deck --take-over
```

## map add

```
savetether map add <id> --source <path> [--target <app>/<folder>] [--process <name>]... [--entry-depth <n>]
```

Adds a mapping, or sets this device's save folder for a mapping another device added. It writes records only; `apply` makes the link.

| Option | |
|---|---|
| `<id>` | `app.role`, lowercase, for example `ppsspp.savedata`. |
| `--source <path>` | The app's save folder on this device. It need not exist yet. On Windows it must be on a local drive: a junction cannot be made on a network drive, so a network folder is refused. |
| `--target <app>/<folder>` | Where the shared copy lives under `<sync root>/savetether/`. Needed only when the mapping is new. |
| `--process <name>` | The app's process name, repeatable. `apply`, `revert` and `backup restore` refuse while it runs. |
| `--entry-depth <n>` | How many folder levels down one game's saves sit (default 1). It is the unit `--add-missing` copies in, so it is never mixing two games' files. Give it again later to change it. |

```bash
savetether map add ppsspp.savedata --target ppsspp/SAVEDATA \
  --source ~/.var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP/SAVEDATA --process PPSSPPSDL

# On another device, the mapping exists already: only this device's folder is needed.
savetether map add ppsspp.savedata --source "D:\Emulators\PPSSPP\memstick\PSP\SAVEDATA"

# Cemu keeps each title's saves two levels down (title ID, then user):
savetether map add cemu.save --target cemu/save --source ~/.local/share/Cemu/mlc01/usr/save --entry-depth 2
```

## map remove

```
savetether map remove <id>
```

Forgets this device's save folder for a mapping. Nothing is deleted: the folder, the shared copy and other devices are untouched. Refused while the folder is linked; `revert` it first.

```bash
savetether map remove eden.saves
```

## map delete

```
savetether map delete <id> [--confirm-delete]
```

Deletes a mapping no device uses from the sync folder, **on every device's sync folder**: its shared copy, its record and its seed markers. Each device's own save folders and backups are not touched: a device that reverted the mapping keeps its own copy. It is the way to clean up a mapping that every device has reverted and removed, which otherwise stays in `status` as "Not applied" with its saves still in the sync folder. The shared saves are backed up on this device first and the backup is verified (`backup list` shows it as `pre-delete`); only then is anything deleted. It cannot be undone from the share, so it asks first, or needs `--confirm-delete`.

Refused while any device, this one included, has a folder set for the mapping: `revert` and `map remove` it there first. Refused too when the shared folder is reached through a link (an app folder that is a symlink or junction), and it deletes only the files the backup holds, each moved aside first and deleted only if it still matches the backup: a save the sync tool delivered or changed after the backup is left (or kept aside under `.savetether/deleting/` if the sync tool already refilled its place), with the mapping record, and the command says so; run it again once the sync tool has finished, which first settles anything an earlier run left aside. The check reads the device files in the sync folder, so a device that has not yet synced its own file cannot be seen; check that the sync tool has caught up before you delete. Safe to run again if it stopped part way.

```bash
savetether map delete eden.saves --confirm-delete
```

## app add

```
savetether app add <app> [--install <id>] [--role <id>]... [--install-dir <path>] [--sd-card <path>] [--wine-prefix <path>] [--dry-run]
```

Adds an app from its definition: finds its install on this device, shows its save folders with their sizes, and adds a mapping for each. Links nothing; run `apply` next.

| Option | |
|---|---|
| `--install <id>` | Which install to use when more than one is found (a terminal asks instead). |
| `--role <id>` | Add this role, repeatable. Without it, the definition's default roles are added; caches and media are off by default. |
| `--install-dir <path>` | Where a portable app was unpacked. |
| `--sd-card <path>` | The SD card, when it is not found from the mount table. |
| `--wine-prefix <path>` | The Wine prefix (the folder holding `drive_c`) for a Windows game on Linux. |

The three path options give what cannot be found by looking. They are remembered for this device, so running `app add` again later, with a new path, follows a moved install.

Definitions come from `<sync root>/savetether/.savetether/definitions/<app>.jsonc` first, then those built in. Only Cemu is built in so far. A shared definition reaches every device through the sync tool. The format is in the [design, section 5.2](design/save-redirection-design.md).

```bash
savetether app add cemu --dry-run     # what it found, with sizes; nothing written
savetether app add cemu
savetether app add mygame --install-dir "D:\Games\MyGame" --role screenshots
```

## app list

```
savetether app list [--found] [--search <text>]
```

Lists the apps that have a definition, and whether each is built in, shared or imported. Imported definitions are converted from the Ludusavi manifest and show as `imported (unverified)`: nobody has tried them by hand, so `app add` says so in its plan and you should check the save folders it shows. A shared definition with mistakes is listed with them.

`--search` keeps the apps whose name or ID contains the text, ignoring case, or whose Steam app ID is the text. The list is long without it.

`--found` lists only the apps found on this device, as the desktop app's Add app page does: an app is found when one of its installs for this system is detected and a save folder of it exists. For an imported definition with a Steam ID, detected also means that Steam has the game installed (when this device has Steam). Newest save first, each with its install, its save folder and when that folder last changed; imported ones show `(unverified)`. It only reads, and it can take a few seconds because every definition is looked at. `--search` then keeps the found apps whose name or ID contains the text.

## status

```
savetether status [<id>]
```

Shows each mapping's state on this device, or one mapping's. It only reads. It warns when an earlier operation did not finish, when the sync root cannot be used, when this device's name is in use by another install, and when the desktop app's start-with-the-session entry points to a program that is gone.

| State | Meaning | Usually |
|---|---|---|
| `OK` | The save folder is a link to the shared copy. | Nothing to do. |
| `Not applied` | No folder set here, or set but not linked yet. | `map add` or `app add`, then `apply`. |
| `Replaced` | The link has gone: the folder holds files, is empty, is missing, or something else is there. An app or tool may have recreated the folder. | `apply` (it compares and asks), or `app add` again if the app moved. |
| `Broken` | The link is there, but the shared folder or the whole sync root is missing. | Wait for the sync tool, or connect the drive. |
| `Retargeted` | The link points somewhere other than the shared folder. | Remove that link by hand, then `apply`. |
| `Double seed` | More than one device made its own saves the shared copy. | Choose which to keep: `apply <id> --replace-shared --confirm-replace` on that device. |
| `Foreign link` | The folder is a link SaveTether did not make. | Remove it by hand, then `apply`. |

```bash
savetether status
savetether status cemu.save
```

## apply

```
savetether apply <id> [--copy | --no-copy | --add-missing | --replace-shared --confirm-replace] [--assume-synced] [--assume-closed] [--grant-sandbox] [--dry-run]
savetether apply --all [--assume-synced] [--assume-closed] [--grant-sandbox] [--dry-run]
```

Links the save folder to the shared copy. A backup is taken first wherever there are saves. When the folder is empty, or the shared copy is the same, it just links. When this device has saves of its own, `apply` compares the two sides, game by game, and stops to let you choose:

| Choice | What happens | Offered when |
|---|---|---|
| `--copy` | This device's saves become the shared copy. | The shared copy is empty: this is the first device. |
| `--no-copy` | Use the shared saves. This device's are kept in the backup only. | Always. |
| `--add-missing` | Use the shared saves, and also copy in the games only this device has. Nothing in the shared copy is changed or deleted. | The shared copy has saves. |
| `--replace-shared --confirm-replace` | This device's saves replace the shared copy **on every device**. The shared copy is backed up here first. | The shared copy has saves. Needs both flags (or `yes` at a terminal). |

Only the choices that are safe for the situation are accepted; the refusal lists them.

| Option | |
|---|---|
| `--assume-synced` | Go ahead although the shared copy may not have fully arrived, for example after shared saves were deleted on purpose. |
| `--assume-closed` | Go ahead although the app appears to be running. Process names are a guess; use this when the guess is wrong. |
| `--grant-sandbox` | When a Flatpak app cannot write the shared folder, allow it, that folder only and for this user, instead of stopping. |
| `--all` | Every mapping with a folder on this device. Stops at the first that needs a choice. |

```bash
savetether apply ppsspp.savedata --copy        # first device
savetether apply ppsspp.savedata               # other devices: links, or stops to ask
savetether apply ppsspp.savedata --add-missing
savetether apply --all --dry-run
```

## revert

```
savetether revert <id> [--assume-closed] [--dry-run]
```

Makes the save folder a real folder again, holding a copy of the shared saves. The link is removed; the shared copy and other devices are untouched. Refused while the app runs, unless `--assume-closed`.

```bash
savetether revert eden.saves
```

## backup list

```
savetether backup list [<id>]
```

Lists backups, oldest first: ID, time, reason (`pre-apply`, `pre-revert`, `pre-restore`, `baseline`, `snapshot`), size, and the folder it was taken from. Each backup is a plain copy of the folder under `<backup root>/<mapping>/<time>/data/`, so it can always be copied back by hand.

## backup restore

```
savetether backup restore <backup-id> [--confirm-replace] [--assume-closed] [--dry-run]
```

Puts a backup back where it was taken from. What is there now is backed up first. For a backup of this device's folder, links that were inside it are recreated; none are ever created in the sync root.

- A backup of **this device's folder** needs that folder not to be linked: `revert` first, or restoring would write into the shared copy.
- A backup of **the shared copy** replaces it on every device, so it needs `--confirm-replace` (or `yes` at a terminal).

```bash
savetether backup list cemu.save
savetether backup restore cemu.save/20261118T183614Z --dry-run
savetether backup restore cemu.save/20261118T183614Z
```

## backup delete

```
savetether backup delete <backup-id> [--confirm-delete]
```

Deletes one backup. It cannot be undone, so it asks at a terminal, or needs `--confirm-delete`. Deleting a mapping's first backup gets a stronger warning: it is usually the only copy of the folder from before SaveTether changed it. Refused while an operation is unfinished.

```bash
savetether backup delete cemu.save/20261118T183614Z
```

## recover

```
savetether recover
```

Finishes or undoes an operation that was interrupted (a crash, a power cut, a killed process), whichever its journal shows is safe. Nothing is lost either way. While an operation is unfinished, every command that could change something refuses and says to run this; `status`, `backup list`, `app list` and `app add --dry-run` still work.

## uninstall

```
savetether uninstall [--confirm-uninstall]
```

Linux only. Removes what the desktop app's "Install SaveTether for this user" put in place: the app and its libraries in `~/.local/lib/savetether`, `savetether` and the `savetether-gui` link in `~/.local/bin` (the link only while it points at the installed app), the app menu entry, and the autostart entry when it starts the installed app. A running SaveTether is asked to quit first, as from its tray menu; one that is still running after ten seconds is named, to quit by hand. Your setup, linked folders, backups and the sync folder are not touched: linked folders stay linked and keep syncing, but nothing checks on them. A file in the install folder that is not SaveTether's is left and named. It asks at a terminal, or needs `--confirm-uninstall`. On Windows, SaveTether is uninstalled from Settings > Apps > Installed apps, and this command refuses.

```bash
savetether uninstall
```

## Workflows

**Share an emulator's saves between a PC and a Deck.** On the PC, the first device:

```bash
savetether init --sync-root ~/Sync --device desktop --create-root
savetether map add ppsspp.savedata --target ppsspp/SAVEDATA --source "D:\Emulators\PPSSPP\memstick\PSP\SAVEDATA" --process PPSSPPWindows64
savetether apply ppsspp.savedata --copy
```

Let the sync tool deliver `~/Sync/savetether` to the Deck, then:

```bash
savetether init --sync-root ~/Sync --device deck
savetether map add ppsspp.savedata --source ~/.var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP/SAVEDATA --process PPSSPPSDL
savetether apply ppsspp.savedata      # stops and asks if the Deck already has saves
```

Play on one device at a time, and let the sync tool finish before switching.

**Set up a new or reinstalled device.** `init` (with `--take-over` for the old name), then `apply --all` once the sync tool has caught up.

**An app's folder moved.** Run `app add <app>` with the new location (`--install-dir`, `--sd-card` or `--wine-prefix`), then `apply --all`. On Windows, move the folder to another drive with Explorer, or `revert` its mappings first: PowerShell `Move-Item` and `robocopy /MOVE` follow the links, copy the shared saves into the new folder and then delete them from the shared folder, and the sync tool deletes them on every other device. If that has happened, `apply` says so and how to make the moved copy the shared one again.

**Something went wrong.** `status` says what each mapping's state is and what to do. `backup list` and `backup restore` bring back any folder SaveTether changed.

## Files and environment

| Where | What |
|---|---|
| `<sync root>/savetether/<app>/<folder>/` | The shared copies. |
| `<sync root>/savetether/.savetether/` | Shared records: the mappings, one file per device, and shared definitions. A mapping holds no paths; each device's file holds only that device's own. |
| Local folder | This device's own files: its setup (`bootstrap.json`), what it linked (`state.json`), the journal, the operation lock, and by default `backups/`. `%LOCALAPPDATA%\SaveTether` on Windows; `$XDG_DATA_HOME/savetether` (by default `~/.local/share/savetether`) on Linux. |

| Variable | |
|---|---|
| `SAVETETHER_HOME` | Use this folder as the local folder instead. Handy for trying SaveTether on a throwaway setup without touching the real one. |

One operation runs at a time on a device, across the CLI and the app: a second is refused until the first finishes.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Done. |
| 1 | Refused. Nothing was changed. The message says why and what to do. |
| 2 | Failed part way and was rolled back. Nothing was changed. |
| 3 | An operation is unfinished. Run `savetether recover`. |
| 64 | The command line was not understood. |
