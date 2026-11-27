# The `--json` contract, version 1

Every `savetether` command accepts `--json` and then prints one JSON object on standard output. Scripts, agents, the Decky plugin and anything else that drives SaveTether should read this, not the text output. This page is the contract. It was frozen as version 1 on 2026-11-18 (implementation plan, section 5).

## The promise

Within version 1 (`"schema": 1`):

- No field documented here is removed, renamed or given another type.
- No documented value of a field is removed or renamed.
- New fields may appear, and listed values may gain new members. A reader must ignore fields it does not know and treat an unknown value as "something else": show the message, do not crash.
- The command-line flags below keep their meaning. New flags may be added.

A change that breaks any of this needs `schema` 2 and a note in the release.

Not part of the contract: the wording of any message (`error`, `message`, `detail`, notes, `reason` texts), the order of keys in an object, the text output without `--json`, and anything written to standard error.

The contract is checked by `JsonContractTests` against `tests/SaveTether.Tests/Cli/json-shapes.txt`. That file records each command's output as property paths with their types. A recorded line that disappears fails the build. A new line fails until it is recorded and documented here (see "Changing the contract" at the end).

## How output is written

- `--json` may appear anywhere before a `--`. With it, the command never asks a question: where it would ask, it refuses instead (exit 1) and says what to add.
- Property names are `snake_case`. Values from a fixed set are `kebab-case` strings.
- A field with no value is left out rather than written as `null`. A list with nothing in it is written as `[]`.
- Paths are absolute, in the operating system's own form. Sizes are in bytes. Times are ISO 8601 in UTC, for example `2026-11-18T09:30:00Z` or with a fractional second and an offset of `+00:00`.
- Messages are single strings. Where the core suggests a next step, the command line's hint (the command to type) follows the message text in the same string.
- `savetether --version`, `--help` and `--self-test` print text only. They take no `--json`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Done, or nothing needed doing. A dry run that planned something. |
| 1 | Refused: a precondition failed, a choice or confirmation is needed, or a store check said no. Nothing was changed. |
| 2 | An operation failed part way and was rolled back. Nothing is left changed. |
| 3 | An operation is unfinished: run `savetether recover`. Given by `status` while the journal is open, and by any command that writes. |
| 64 | The command line was not understood. Nothing was done. |
| 70 | `--self-test` found that this build does not work on this machine. |

`exit_code` in the object is always the same as the process's exit code.

## The object every command prints

| Field | Type | When |
|---|---|---|
| `schema` | number | Always. `1`. |
| `command` | string | Always. The command as typed in one or two words, for example `apply` or `backup restore`. `usage` when the command line was not understood. |
| `exit_code` | number | Always. |
| `error` | string | When the command as a whole did not do what was asked. Problems of one operation are in `operations` instead. |
| `problem` | string | When a store check refused the command. One of the store problems below. |
| `conflict` | object | When `problem` is `conflict`: what is in the way. See "Conflict". |
| `message` | string | A sentence the owner should read: `apply --all` with nothing to apply, or `app list` when the shared definitions could not be read. |

Then one section, depending on the command: `init`, `mapping`, `app`, `apps`, `found`, `status`, `operations` or `backups`.

## Commands

### `init`

Flags: `--sync-root <path>` (required), `--device <name>`, `--create-root`, `--take-over`, `--backup-root <path>`. With `--json`, `--device` is required.

`init` object:

| Field | Type | |
|---|---|---|
| `sync_root` | string | The sync folder this device now uses. |
| `device_name` | string | |
| `backup_root` | string | Where this device keeps its backups. |
| `created_root` | boolean | True when this run created SaveTether's folder in the sync folder (`--create-root` on the first device). |
| `new_device` | boolean | True when the device name was not set up before. |
| `took_over` | boolean | True when the name was taken over from another install (`--take-over`). |
| `devices` | array of string | Every device set up in the sync folder, this one included. |
| `notes` | array of string | Things the owner should know, such as a backup location whose drive is not connected, a link left behind by a sync folder change, or, for a sync folder on a Windows network share, that a share is not recommended. |

A sync folder on a Windows network share that cannot be linked to is refused with `problem` `not-found` (the share does not answer) or `invalid` (Windows policy, or no Developer Mode and no elevation); `error` names the fix. Store problems specific to `init`: `not-initialized` (the sync folder has no SaveTether folder, and `--create-root` was not given), `name-in-use` (the device name belongs to another install), `mappings-applied` (a new sync folder while mappings are still linked into the old one; revert them first).

### `map add` and `map remove`

`map add <id>` flags: `--source <path>` (required), `--target <app>/<save folder>` (required for a new mapping), `--process <name>` (repeatable), `--entry-depth <n>` (1 to 8), `--name <app name>` (added 2026-11-26; no field in the answer). `map remove <id>` takes no flags.

`mapping` object:

| Field | Type | |
|---|---|---|
| `id` | string | The mapping ID, for example `ppsspp.savedata`. |
| `target` | string | The shared folder, relative to SaveTether's folder in the sync folder, for example `ppsspp/SAVEDATA`. |
| `source` | string | This device's save folder. |
| `process_names` | array of string | |
| `entry_depth` | number | |
| `created_mapping` | boolean | True when `map add` created the shared mapping, false when it only set this device's save folder. |
| `entry_depth_changed` | boolean | |
| `removed` | boolean | True for `map remove`. |

A refusal has `problem` `conflict` with a `conflict` object, or `invalid` or `not-found`.

### `map delete`

`map delete <id>`, with `--confirm-delete`, which says that every write to the role's shared folder is complete and the sync folder is up to date on every device that uses it. With `--json` nothing is asked, so without `--confirm-delete` it is refused (exit 1) and nothing is deleted. Refused (exit 1, `problem` `conflict`, `conflict.kind` `in-use-on-devices`) while any device, this one included, has a folder set for the mapping, and (exit 3) while an operation is unfinished. A deletion that stops part way (a file arrived after the backup, or a delete failed) is a refusal (exit 1) whose message says what was removed and names the backup; running it again finishes. Added 2026-11-21.

`mapping_deleted` object, present when the mapping was deleted from the shared folder:

| Field | Type | |
|---|---|---|
| `mapping_id` | string | The mapping that was deleted. |
| `target_folder` | string | The shared folder that held its saves. |
| `target_exists` | boolean | False when the shared folder was already gone, which a `map delete` that stopped part way leaves; there is then no backup. |
| `files` | number | Files the shared folder held. |
| `bytes` | number | Their total size. |
| `backup` | object or null | The verified backup of the shared folder taken on this device first, with the fields of `backups[]` above (`reason` is `pre-delete`); null when the folder held no files. |

### `app add` and `app list`

`app add <app>` flags: `--install <id>`, `--role <id>` (repeatable), `--install-dir <path>`, `--sd-card <path>`, `--wine-prefix <path>`, `--dry-run`.

`app` object:

| Field | Type | |
|---|---|---|
| `app_id` | string | As given. |
| `added` | boolean | True when the mappings and this device's entries were written. False for a dry run and for a refusal. |
| `plan` | object | What was, or would be, written. Absent when refused. |
| `problem` | string | Why there is no plan. One of the app add problems below. The reason in words is the top-level `error`. |
| `conflict` | object | When `problem` is `conflict`. See "Conflict". |
| `candidates` | array of object | Each install of the app for this operating system, and whether it was found. |

`plan`:

| Field | Type | |
|---|---|---|
| `app_id`, `app_name` | string | |
| `definition_source` | string | `built-in`, `shared` or `imported`. |
| `verified` | boolean | False when the definition was converted from the Ludusavi manifest and has not been tried by hand (`definition_source` is `imported`). |
| `definition_location` | string | The shared definition's file, `built-in <file name>`, or `imported <file name>`. |
| `install` | string | The install ID used. |
| `values` | object | What this device records for the app: `install`, and `install_dir`, `sd_card`, `wine_prefix` when given or found. |
| `roles` | array of object | One per role to add. |
| `quirks` | array of string | The definition's notes about the app. |

`roles[]`: `role_id`, `class` (a role class below), `mapping_id`, `target` (relative, as in `mapping`), `source` (the save folder here), `exists` (false when the folder is not there yet), `is_link` (already a link), `bytes`, `files` (size and file count, not following links inside), `creates_mapping`, `entry_depth`, and `note` (a sentence, when there is one).

`candidates[]`: `id`, `is_detected`, `found` (what showed the install is there, when found), `reason` (why not, when not found), `problem` (why not, as a resolve problem below).

`app list` takes `--search <text>`. `apps` is an array of `id`, `name`, `source` (`built-in`, `shared` or `imported`), `verified` (false for `imported`), and `error` when the definition has mistakes (then `name` may be absent).

`app list --found` has `found` instead of `apps`: an array of `id`, `name`, `source` (as above), `verified`, `install` (the install ID found), `save_folder` (the folder found) and `newest_write` (when that folder last changed, UTC; absent when it holds no file), newest first. The `--search` filter applies to it.

### `device list`

`device list`, no flags. `devices` object:

| Field | Type | |
|---|---|---|
| `sync_root` | string | |
| `devices[]` | array | `name`, `install_id`, `last_activity_utc` (the newest of its device file and its seed markers), `mapping_count` (mappings it has a save folder for), `is_this_device`, `tombstoned` (the device was removed, and its file came back). |
| `mappings` | array of string | Every mapping ID in the shared list. |
| `mappings_here` | array of string | Those this device has a save folder for. |
| `linked_here` | array of string | Those this device has applied. |
| `orphans` | array of string | Mappings no device has a save folder for. |
| `backup_count` | number | This device's backups. |
| `backup_bytes` | number | Their total size. |

### `device rename`

`device rename <new-name>`, no flags. `renamed` object: `old_name` and `new_name` (strings; equal when only files left by an earlier rename were cleared). Exit 3 while an operation is unfinished.

### `device remove`

`device remove [--delete-orphan <mapping>[:saves]]... [--delete-backups] [--delete-everything] [--confirm-remove] [--confirm-delete]` (added 2026-11-22; `--delete-everything` adds no field, and exits 1 while another device is registered). `device remove --other <name> [--confirm-remove]` removes a device that is gone: `removal` has an empty `given_back`, `not_linked` and `orphans`, `shared_files` lists that device's deleted files, `backups_deleted` is 0. Exit 1 when the name is this device's own, unknown, or a file left by a rename. Under `--json` without `--confirm-remove` nothing is changed and the answer has `dry_run` true. Exit 1 when a link cannot be reverted (the error says which and why) or a `--delete-orphan` names a mapping another device uses; 2 when a revert failed and was rolled back; 3 while an operation is unfinished. A failed revert leaves everything in the sync folder as it was. `removal` object:

| Field | Type | |
|---|---|---|
| `dry_run` | boolean | True when nothing was changed. |
| `given_back` | array of strings | The save folders that are (or would be) real folders again. |
| `not_linked` | array of strings | Mapping IDs this device has a folder for that are not linked here, and were skipped. |
| `orphans` | array of objects | Mappings only this device used: `mapping_id`, and `choice`: `keep`, `delete-mapping` or `delete-with-saves`. After a run, only the ones deleted. |
| `shared_files` | array of strings | This device's files in the sync folder (its seed markers and its device file). |
| `backups_deleted` | number | The backups deleted, or with `dry_run` the ones that would be; 0 when they are kept. |
| `other` | string | Only with `--other`: the name of the device removed. Absent when this device was removed. |

### `status`

`status [<id>] [--sizes]`. Exit 3 while an operation is unfinished.

`status` object:

| Field | Type | |
|---|---|---|
| `device_name` | string | |
| `sync_root` | string | |
| `sync_root_available` | boolean | |
| `sync_root_problem` | string | Why the sync folder cannot be used, when it cannot. `mappings` then holds only what this device's own record says it linked. |
| `device_file_mismatch` | boolean | The device name now belongs to another install. |
| `journal_open` | boolean | An operation is unfinished. |
| `mappings` | array of object | One per mapping. |
| `removal` | string | Present when this install's ID is in a record of removed devices: `removed-by-another` (the device `removed_by` removed this one) or `unfinished` (this device's own removal did not finish). `mappings` is then empty if this device's file is gone. Added 2026-11-22. |
| `removed_by` | string | Present with `removal`: the name of the device that did the removal. |
| `returned_devices` | array of string | Devices whose file is in the sync folder although they were removed: a sync tool put it back. Empty when none. |
| `operation_running` | boolean | Another SaveTether process holds the operation lock, or took it while the report was read, so links may be part way through a change: do not alert on this report. `status` does not take the lock. Added 2026-11-25. |
| `autostart_missing` | string | Present only when the desktop app is set to start with the session and the program the entry names is missing: that program's path. `status` only reports it; it does not change the entry. Added 2026-11-18. |

`mappings[]`: `mapping_id`, `state` (a health state below), `reason` (for `not-applied`, `replaced` and `broken` only: a health reason below), `source` (absent when this device has no save folder set), `target` (the full path of the shared folder), `detail` (a sentence for the owner), `name` (the app's name as typed when the mapping was added by hand, absent otherwise and when the sync folder cannot be read; added 2026-11-26), `conflicts` (for `file-conflict` only: array of string, the conflict copies relative to the shared folder, `/`-separated and sorted; added 2026-11-26). With `--sizes` only: `bytes` (number, the shared copy's total size) and `files` (number, its file count), both absent when the shared copy is not there. Added 2026-11-25.

### `notifications`

`notifications [on|off]`: the device's "Desktop notifications" switch, shared by the desktop app and the Decky plugin. With no value it reports; with one it sets, then reports. It needs no set-up device and takes no operation lock. A setting that cannot be saved is refused (exit 1); any other value is a usage error (exit 64). Added 2026-11-25.

`notifications` object:

| Field | Type | |
|---|---|---|
| `on` | boolean | True unless turned off. |

Since 2026-11-26 the switch is kept in the local folder's `settings.json`, with the backup settings; an older `notifications.txt` is read until the next change moves it there.

### `sync-conflicts`

`sync-conflicts [on|off]`: the device's "Detect sync conflicts" switch, shared by the desktop app and the Decky plugin, kept in `settings.json` as `detect_sync_conflicts`. On, `status` reports a linked mapping whose shared folder holds a sync tool's conflict copies as `file-conflict`, with the copies in `conflicts`. With no value it reports; with one it sets, then reports. Like `notifications`, it needs no set-up device and takes no operation lock; a setting that cannot be saved is refused (exit 1); any other value is a usage error (exit 64). A CLI older than this has no command and exits 64. Added 2026-11-26.

`sync_conflicts` object:

| Field | Type | |
|---|---|---|
| `on` | boolean | False unless turned on. |

### `backup auto`, `backup keep`

`backup auto [on|off] [--confirm-unsafe]` and `backup keep [<0-20>]`: the device's backup settings, shared with the desktop app. With no value they report; with one they set, then report. `backup auto off` (unsafe mode: no action takes a backup) needs `--confirm-unsafe`. Like `notifications`, they need no set-up device and take no operation lock. A setting that cannot be saved is refused (exit 1); a missing confirmation or any other value is a usage error (exit 64). Added 2026-11-26.

`backup_settings` object, the same for both:

| Field | Type | |
|---|---|---|
| `before_actions` | boolean | True unless unsafe mode is on. |
| `max_per_folder` | number | Backups of each folder kept besides its first, 0 to 20; 0 keeps all. Default 5. |

### `apply`, `revert`, `backup restore`, `recover`

Flags:

- `apply <id>` or `apply --all`: `--copy`, `--no-copy`, `--add-missing`, `--replace-shared` (at most one, and none with `--all`), `--confirm-replace` (only with `--replace-shared`), `--assume-synced`, `--assume-closed`, `--grant-sandbox`, `--dry-run`. `--all` stops at the first mapping that does not end with exit 0.
- `revert <id>`: `--assume-closed`, `--dry-run`.
- `backup restore <backup-id>`: `--confirm-replace` (needed for a backup of a shared folder), `--assume-closed`, `--dry-run`.
- `recover`: no flags.

`operations` is an array with one object per mapping handled (one, except for `apply --all`):

| Field | Type | |
|---|---|---|
| `mapping_id` | string | Absent only when `recover` found nothing to do. |
| `operation` | string | `apply`, `revert` or `restore`. Absent only when `recover` found nothing to do. |
| `outcome` | string | An operation outcome below. |
| `plan` | object | What was, or would be, done. Absent when refused. |
| `refusal` | object | Why the planner said no, with the choices and the comparison. |
| `error` | string | Why it did not complete, when there is no planner refusal to say so. |
| `backup_ids` | array of string | Backups taken. |
| `messages` | array of string | What the executor reported. |

`plan`:

| Field | Type | |
|---|---|---|
| `mapping_id` | string | |
| `operation` | string | A plan operation below. |
| `classification` | string | A classification below. |
| `choice` | string | A setup choice below, when one applies. |
| `source` | string | Apply and revert: the save folder. Restore: the backup's data folder. |
| `target` | string | Apply and revert: the shared folder. Restore: the folder being restored. |
| `steps` | array of object | In order. Each: `kind` (a step kind below), `path`, `other_path` (the second path, where a step has one), `entries` (for `copy-into-target` with only some entries). |
| `notes` | array of string | |
| `needs_first_seed_confirmation` | boolean | The owner should confirm no other device has shared these saves yet (design 5.3). |
| `no_backups` | boolean | Unsafe mode: the plan has no backup steps, and a note says so. Added 2026-11-26. |

`refusal`:

| Field | Type | |
|---|---|---|
| `mapping_id` | string | |
| `operation` | string | A plan operation below. |
| `kind` | string | A refusal kind below. |
| `classification` | string | |
| `message` | string | |
| `failures` | array of object | For `precondition`: every check that failed. |
| `allowed_choices` | array of string | The setup choices the owner may give. |
| `assume_synced_would_help` | boolean | `--assume-synced` would let it go ahead. |
| `comparison` | object | This device's saves against the shared copy, when both matter. |

`failures[]`: `kind` (a precondition kind below), `message`, and these facts as fields:

- `processes`, for `app-running`: `running` (the process names found) and `looked_for` (every name the mapping lists).
- `space`, for `not-enough-space`: `path` (the folder whose drive lacks room), `needed_bytes`, `free_bytes`.

`comparison`: `entries`, `local_newest_utc` and `shared_newest_utc` (the newest file time on each side, when it has files). `entries[]`: `name` (the entry, at the mapping's entry depth), `presence` (an entry presence below), `files`. `files[]`: `path`, `kind` (a file difference below), `local_size` and `shared_size` (absent on the side without the file).

### `backup list`

`backup list [<id>]`, no flags. `backups[]`: `id` (give it to `backup restore`), `mapping_id`, `created_utc`, `reason` (a backup reason below), `source_path`, `file_count`, `total_bytes`, `location` (the backup's folder; its `data` subfolder is a plain copy).

### `backup delete`

`backup delete <backup-id>`, with `--confirm-delete`. With `--json` nothing is asked, so without `--confirm-delete` it is refused (exit 1) and nothing is deleted. Refused too while an operation is unfinished (exit 3), since recovering it may need its backups. Added 2026-11-18.

`backup_deleted` object, present when the backup was deleted:

| Field | Type | |
|---|---|---|
| `backup` | object | The backup that was deleted, with the fields of `backups[]` above. |
| `was_first` | boolean | It was its mapping's first backup, which retention never removes: usually the only copy of the folder from before SaveTether changed it. |
| `remaining` | number | How many backups of the mapping are left. |

`backup delete --all | --keep-first | --keep-last [--mapping <id>]` deletes many (added 2026-11-22). `--all` without `--confirm-delete` under `--json` deletes nothing and answers with `dry_run` true; the two `--keep-` modes need no flag. `backups_cleaned` object:

| Field | Type | |
|---|---|---|
| `removed` | array of objects | The backups deleted, or with `dry_run` the ones that would be, each with the fields of `backups[]` above. |
| `dry_run` | boolean | True when nothing was deleted. |

### `uninstall`

`uninstall`, with `--confirm-uninstall`. With `--json` nothing is asked, so without `--confirm-uninstall` it is refused (exit 1) and nothing is removed. Refused (exit 1) when nothing is installed, and on Windows, where the installer's uninstaller does this. Added 2026-11-19.

`uninstalled` object, present when SaveTether was uninstalled:

| Field | Type | |
|---|---|---|
| `removed` | array of strings | Each file, link, folder or autostart entry removed. |
| `left` | array of strings | Each one left, with the reason: a file in the install folder that is not SaveTether's, a link SaveTether did not make, or an installed SaveTether still running after it was asked to quit (named by process id). |

## Conflict

The `conflict` object, at the top level for a store refusal and in `app` for `app add`:

| Field | Type | |
|---|---|---|
| `kind` | string | A conflict kind below. |
| `mapping_id` | string | The mapping whose record is in the way: this one, or for `folder-in-use` and `target-in-use` the other one. |
| `folder` | string | The save folder on this device, for `applied-elsewhere` and `folder-in-use`. |
| `target` | string | The shared folder as recorded, for `target-differs` and `target-in-use`. |

## Values

Every value `--json` can print, by field. A test checks that each value in the code is listed here.

**Operation outcome** (`operations[].outcome`): `planned` (dry run), `completed`, `refused`, `cancelled`, `rolled-back`, `recovery-needed`, `nothing-to-recover`.

**Plan operation** (`operation`): `apply`, `revert`, `restore`.

**Classification** (`classification`, design 5.3): `not-classified` (structural checks failed), `already-applied`, `broken-link`, `foreign-link`, `not-arrived`, `link-only`, `first-device`, `guard`.

**Setup choice** (`choice`, `allowed_choices[]`): `copy`, `no-copy`, `add-missing`, `replace-shared`.

**Step kind** (`steps[].kind`): `back-up-source`, `back-up-target`, `ensure-target`, `clear-target`, `copy-into-target`, `rename-aside`, `create-link`, `verify-link`, `write-seed-marker`, `supersede-markers`, `write-state`, `delete-aside`, `copy-to-staging`, `remove-link`, `rename-into-place`, `recreate-links`.

**Refusal kind** (`refusal.kind`): `precondition`, `choice-needed`, `choice-not-allowed`, `confirmation-needed`, `not-arrived`, `foreign-link`, `broken-link`.

**Precondition kind** (`failures[].kind`): `sync-root-not-initialized`, `unfinished-operation`, `target-outside-data-root`, `source-inside-sync-root`, `paths-overlap`, `source-parent-missing`, `source-unusable`, `target-unusable`, `app-running`, `backup-location-unusable`, `target-not-local` (the sync folder is on a network share that Windows policy will not follow a link to), `source-not-local` (the folder to link is on a network drive, on Windows), `symlink-not-allowed` (the sync folder is on a network share, and this Windows process can make no symlink: Developer Mode is off and it is not elevated), `not-enough-space`, `sandbox-cannot-reach`, `not-applied`, `backup-unusable`, `restore-destination-unusable`.

**Entry presence** (`comparison.entries[].presence`): `only-local`, `only-shared`, `identical`, `different`.

**File difference** (`comparison.entries[].files[].kind`): `only-local`, `only-shared`, `different`.

**Store problem** (top-level `problem`): `not-found`, `invalid`, `schema-too-new` (a file was written by a newer SaveTether; it is left as it is), `not-initialized`, `conflict`, `name-in-use`, `operation-running` (another SaveTether process holds the operation lock), `mappings-applied`.

**Conflict kind** (`conflict.kind`): `target-differs`, `applied-elsewhere`, `folder-in-use`, `target-in-use`, `in-use-on-devices` (`map delete`: a device still has a folder set for the mapping; `conflict.mapping_id` is that mapping).

**Removal** (`status.removal`): `removed-by-another`, `unfinished`.

**Health state** (`status.mappings[].state`, design 5.5): `ok`, `not-applied`, `replaced`, `broken`, `retargeted`, `foreign-link`, `file-conflict` (linked, but the shared folder holds conflict copies a sync tool left; only reported when the device's "Detect sync conflicts" setting is on; added 2026-11-26). `double-seed` was removed on 2026-11-27: seed markers no longer produce a state.

**Health reason** (`status.mappings[].reason`): for `not-applied`, `no-save-folder`, `save-folder-not-absolute`, `not-linked-yet`; for `replaced`, `folder-holds-files`, `folder-empty`, `folder-missing`, `not-a-folder`; for `broken`, `shared-folder-missing`, `sync-root-unavailable`.

**Backup reason** (`backups[].reason`): `pre-apply`, `pre-revert`, `pre-restore`, `pre-delete` (the shared folder, before `map delete` removed it), `baseline`, `snapshot` (this version never takes one; it is listed because a backup folder may hold one made by a test build).

**App add problem** (`app.problem`): `unknown-app`, `invalid-definition`, `unknown-install`, `not-installed`, `install-choice-needed`, `unknown-role`, `unresolved`, `conflict`, `not-local` (a role's folder is on a network drive, on Windows).

**Role class** (`app.plan.roles[].class`): `save`, `system`, `state`, `cache`, `mod`, `media`.

**Definition source** (`definition_source`, `apps[].source`): `built-in`, `shared`, `imported`.

**Resolve problem** (`app.candidates[].problem`): `invalid`, `not-on-this-os`, `needs-value`, `missing`, `ambiguous`, `not-yet-supported`.

## Changing the contract

1. Add the field or value in the code. Never remove, rename or retype a documented one within version 1.
2. Document it on this page.
3. If a test scenario produces it, run the tests once with `SAVETETHER_UPDATE_SHAPES=1` to record it in `json-shapes.txt`, and commit that file. The update only adds lines: it refuses while a recorded line is missing.
4. If a recorded line disappears because a test scenario changed, not the output, edit `json-shapes.txt` by hand and say why in the pull request.
