# SaveTether phase 4.1: Decky plugin toasts

The [phase 4 file](phase-4-decky-plugin.md) left "background polling and toasts" out of scope; this file holds that follow-up: the owner's decisions, the tasks (T1 to T10, with T6b) with the model for each, the checks, and progress. A delegated task needs only this file and the files it names.

Planned 2026-11-25 by the owner and the agent. Nothing is built yet.

## Context

The desktop app notifies when a check finds a **new** amber problem (desktop-app-spec 3.1): one notification per check at most, each problem once, once at start if amber, nothing on turning green, nothing while another process holds the operation lock, "Health check failed" after 3 failures in a row, and a "Desktop notifications" switch (`notifications.txt`, spec section 11). The logic is `HealthMonitor` (`src/SaveTether.Gui.Presentation/HealthMonitor.cs`), the switch `NotificationSetting.cs` beside it.

In Game Mode the desktop app isn't running, so the Deck gets no warning when a link breaks. The Decky plugin (repo `SaveTether-Decky`, v1 read-only) put "background polling and toasts" out of scope (phase-4 file, line 183). This phase adds them with the same rules.

Owner decisions (2026-11-25):
- **One switch per device**: the plugin honours `notifications.txt` and shows a toggle that writes it, through the CLI.
- **Lock guard as on the desktop**: `status --json` gains `operation_running`; the plugin doesn't toast while it is true.
- **A "Mute notifications" toggle in the QAM**, which is that same switch shown inverted (muted = `notifications.txt` says `off`). Shown in the mockup.
- **Respect Steam's Do Not Disturb.** Steam keeps it as `bDoNotDisturbMode` in the friends settings (typed in `@decky/ui` `globals/steam-client/FriendSettings.d.ts`). Nothing in `@decky/api`'s `Toaster` mentions DND, so whether Decky's toaster already respects it is **unknown**. T6b finds out.

Muted and DND behave like the desktop's switch: the check still runs and the problem is recorded as notified. It is not queued for later. The panel still shows it.

## Design (smallest complete)

- **Where the logic lives**: a TS port of `HealthMonitor`'s accept/dedupe rules in the plugin (`src/monitor.ts`), as T5 of phase 4 ported `StateWords` to `states.ts`. Pure, no React, tested with `node --test`. (Moving the dedupe into the core would need persisted state for a stateless CLI; not worth it.)
- **Where it runs**: the frontend, at module scope of `definePlugin` (it loads with Steam, not with the panel). One monitor instance; a `setInterval` of 5 min plus one check at load; cleared in `onDismount`. The panel's open-check and Check now feed the **same** monitor (desktop: window-shown checks count too). A check already running is not started twice.
- **Toast**: `toaster.toast({ title: "SaveTether", body })` from `@decky/api`. Click does nothing (desktop parity, decision 2). Body text built exactly as `HealthMonitor.Problems`: `"{App} {role}: {word lowercased}."` joined with spaces; device-wide conditions use the headline + ".". Each toast also does `console.log("[SaveTether] toast: " + body)` so the harness can see it.
- **Polling cost**: background checks call `status --json` **without `--sizes`** (sizes walk every shared folder on the SD card). One AOT process every 5 min.
- **Old CLI tolerance**: `operation_running` absent → false. `notifications` exit 64 (unknown command) → treat as on, hide the toggle.

## Tasks

Model key: **Opus** = judgement or a critical behaviour; **Sonnet** = fully specified here; **Haiku** = mechanical. No task is given to Haiku where a mistake is user-visible or touches the JSON contract.

### In SaveTether (one PR, branch `feature/decky-notification-support` from `development`)

**T1 — Move `NotificationSetting` to the core (Sonnet)**
- Move `src/SaveTether.Gui.Presentation/NotificationSetting.cs` to `src/SaveTether.Core/Startup/NotificationSetting.cs` (namespace `SaveTether.Core.Startup`), code unchanged. Fix usings in `src/SaveTether.Gui/Platform.cs:36`, `SettingsScreen.cs:76`, `tests/SaveTether.Tests/Gui/NotificationSettingTests.cs` (move the test next to core tests if that is the folder convention).
- Acceptance: build `-warnaserror`, existing `NotificationSettingTests` pass unchanged in logic.

**T2 — CLI `notifications [on|off]` (Sonnet, after T1)**
- `src/SaveTether.Cli/ArgParser.cs`: new entry like `status` (`new("notifications", 0, 1, [], …)`); handler in `CliApp.cs`. No arg: report. `on`/`off`: `NotificationSetting.Set`, a failure → exit 1 with its sentence.
- JSON: `{ "notifications": { "on": true } }` (after a set: the new value). Text: `Desktop notifications: on`.
- Local folder: the same one the app uses (`SAVETETHER_HOME` honoured, as every command).
- Docs: `docs/cli.md`, `docs/cli-json.md`, `json-shapes.txt`.
- Tests (automated): new `NotificationsCliTests`: default on; `off` then report says off and the file says `off`; `on` again; bad arg → exit 64; unwritable folder → exit 1. `JsonContractTests` scenario `notifications`.

**T3 — `operation_running` in `status --json` (Sonnet; Opus reviews the diff)**
- In the status handler: `operation_running = OperationLock.HeldByAnotherProcess(local) is not null` (`src/SaveTether.Core/Storage/OperationLock.cs:136`). Boolean, always present. Text output: one line "Another SaveTether process is running an operation." only when true.
- Confirm `status` doesn't take the lock itself (it must not see itself).
- Docs: `docs/cli-json.md` status table ("Added 2026-11-…"), `json-shapes.txt`.
- Tests (automated): lock held by a second process (or the test's existing way of faking a holder, as `HealthMonitorTests` or `OperationLock` tests do) → `true`; free → `false`; contract scenario updated.
- Why reviewed: frozen contract v1, and the lock is shared with apply/revert.

**T4 — Docs (Sonnet, with T1–T3)**
- New `docs/plans/phase-4.1-decky-notifications.md`: this plan, self-contained, in the phase-4 file's style.
- `phase-4-decky-plugin.md`: out-of-scope line "Background polling and toasts" → points to 4.1.
- `desktop-app-spec.md` section 11: the switch also governs the Decky plugin's toasts; `CLAUDE.md` index row; `implementation-plan.md` progress entry.
- `docs/design/decky-mockup.html` (Sonnet): add a "Mute notifications" `ToggleField` row under Check now in both states, and a third small panel showing an example Steam toast ("SaveTether" / "Stardew Valley saves: not linked."). Update the ASCII mockup in the phase file to match. Acceptance: the page opens standalone in a browser, both states plus the toast visible, colours unchanged from the existing page.
- Version bump + tag `v0.7.0-beta2` on `development` after merge (owner says go; `docs/releasing.md`).

### In SaveTether-Decky (branch `feature/toasts` from `development`)

**T5 — Backend (Sonnet)**
- `main.py`: `status(sizes: bool = True)` (default keeps the panel as is); new `notifications(on: bool | None = None)` → runs `notifications --json` or `notifications on|off --json`. Same `{ok, exit_code, json | error}` shape, same 20 s timeout, same `sttest` home guard.
- Tests (`tests/test_main.py`, fake CLI): `status(False)` omits `--sizes`; `notifications()` and `notifications(False)` pass the right args; exit 64 passed through as `ok: true, exit_code: 64` (or as the current contract does).

**T6 — `src/monitor.ts` (Opus)** — the critical behaviour
- `createMonitor()` returning `{ accept(result): string | null }` where `result` is the backend's status result plus app names. Port of `HealthMonitor.Accept/Failed/Problems`:
  - problems = amber mappings keyed `${mapping_id}:${word}`, or one `condition:${headline}` for device-wide conditions; a toast names only keys not in the last accepted set; the set is replaced each accepted check.
  - `operation_running === true` → return null, state untouched.
  - `ok: false` → failure count; at 3 in a row accept a "Health check failed" condition; before that keep state, return null. A success resets the count.
  - First accepted check that is amber → toast (the "at start" rule falls out of the empty initial set).
- Reuse `cardsFor`/`overall`/`wordFor` from `states.ts` for words and headlines; no duplicate tables.
- Tests (`node --test`, fixtures from `json-shapes.txt`): green → null; first amber → text matches the desktop format exactly (one fixture copied from `HealthMonitorTests`); same amber again → null; a second problem appears → only the new one; fixed then broken again → toast again; lock held → null and the next free check still toasts; two failures → null, third → "Health check failed."; failure then success resets; unknown state → amber, toasts with its word, no crash.

**T6b — Do Not Disturb spike (Opus, short, before T7)**
- Read decky-loader's frontend toaster source (GitHub `SteamDeckHomebrew/decky-loader`, `frontend/src/toaster.tsx` or similar). Answer two questions: does it go through Steam's own notification path, which honours DND? And how does a plugin read `bDoNotDisturbMode`? (Candidates: the friends settings store, or `SteamClient.Settings`.)
- On the Deck through the CEF debugger: switch DND on in the QAM, then read the value with the candidate call.
- Output: a short note in the phase file giving the call to use, and whether Decky already suppresses toasts during DND. If it does, T7 still checks DND itself, so the log line (below) stays truthful and the harness can tell the cases apart.

**T7 — Wiring and toggle (Opus)**
- `src/index.tsx`: module-level monitor + interval in `definePlugin`, cleared in `onDismount`; guard against a second interval on hot reload. Panel checks call `accept` too. Background check: `status(false)` + `apps()` + `notifications()`.
- Gate in one place, `notify(body)`: muted → log `[SaveTether] toast muted: …`; DND (T6b's call, read at toast time; if it throws, treat as not-DND) → log `[SaveTether] toast dnd: …`; else `toaster.toast(...)` and log `[SaveTether] toast: …`.
- QAM: a `ToggleField` "Mute notifications" under Check now (as in the mockup), bound to `notifications(!muted)`; hidden when the CLI lacks the command (exit 64). `data-st-muted` attribute for the harness.
- Small pure helper `gate(muted, dnd) → "show" | "muted" | "dnd"` in `monitor.ts`, unit-tested (4 cases), so the rule isn't only checked on hardware.
- Acceptance: `pnpm run build`, `tsc --noEmit`, all tests pass.

**T8 — Harness and test doc (Opus)**
- `scripts/qam-check.mjs`: subscribe to `Runtime.consoleAPICalled` on SharedJSContext and collect `[SaveTether] toast:` lines; expected files gain `toasts: [...]`.
- New scenarios via `deck-scenarios.sh` (throwaway `~/sttest` homes, the existing override):
  - (g) all linked → Check now → no toast.
  - (h) break a link → Check now → exactly one toast with the desktop text; Check now again → none.
  - (i) fix it, break a different one → one toast naming only the new one.
  - (j) Mute on (the harness clicks the toggle) → break another → `toast muted:` log only; `savetether notifications --json` says off; Mute off.
  - (j2) Steam DND on (harness sets it, or the owner by hand if it can't be set over CEF) → break another → `toast dnd:` log, no toast on screen (screenshot); DND off.
  - (k) background timer: QAM closed, break a link, wait ≤ 6 min → one toast. (`--slow` flag, not in the default run.)
  - (l) `CLI renamed away` ×3 checks → one "Health check failed" toast.
- `docs/testing/hardware-testing.md`, "The Decky plugin in Game Mode": the new scenarios and the Desktop-Mode check below.

**T9 — Hardware run (Opus, with the owner)**
- CLI `0.7.0-beta2` and the plugin build on the Deck; run (a)–(l). Then: Desktop Mode with the desktop app's tray running, break a link → count notifications (see concern 1). Reboot → an amber setup toasts once after boot.
- Record in the phase file and section 8 checklist; each bug found → a test in T5/T6.

**T10 — Plugin release (Sonnet)**: tag `v0.2.0` (after owner OK) from the plugin's `main` per its `release.yml`.

### Order

T1 → T2; T3 and T5 in parallel with T1; T4 with the PR. T6 can start at once (fixtures only). T6b can start at once. T7 needs T5, T6, T6b; it is built to the updated mockup. T8 needs T7; T9 needs T8 and the beta2 CLI; T10 last.

## Verification

1. SaveTether: `dotnet build -warnaserror`, `dotnet test` (new `NotificationsCliTests`, status lock tests, contract scenarios), CI on all three targets.
2. Plugin: CI runs build, `tsc`, `node --test` (monitor tests), `python -m unittest`.
3. Deck: `node scripts/qam-check.mjs` scenarios (a)–(l) match, the real setup's `status --json` identical before and after.

Not automatable: what the toast looks like over a running game, and the Desktop-Mode double-notify check — manual in T9.

## Concerns / trade-offs for the owner

1. **Double notifications in Desktop Mode (unknown).** If Decky's frontend also loads in the Desktop-Mode Steam client, and the desktop app runs in the tray, one break gives two notifications. Verify in T9; if it happens, the cheapest fix is the plugin skipping background toasts outside Game Mode.
2. **This reverses the "never polls" decision** from phase 4. Cost: one `status` without sizes every 5 min (milliseconds, no disk walk). I think it's fine; the alternative (check only on panel open) can't warn anyone, which defeats the feature.
3. **Toasts appear over games.** Parity with the desktop app; dedupe makes them rare. Not added: a "quiet while playing" option.
4. **Dedupe state is in memory.** After a reboot or plugin reload, a still-amber setup toasts once again — same as the desktop app at login.
5. **Two CLI releases in flight.** The plugin must work with CLI 0.7.0-beta1 (no toggle, no lock guard) until the Deck updates; T5–T7 handle exit 64 / absent field.
6. **Shared switch means the plugin now writes one SaveTether file** (`notifications.txt`, via the CLI). Still nothing touches saves or links, so the "read-only, cannot harm the setup" promise holds for saves.

7. **"Mute" in the QAM vs "Desktop notifications" in the app**: one switch, two labels, opposite polarity. Muting on the Deck also silences the desktop app on that device. If you'd rather they be independent, the toggle moves to Decky's plugin settings and T1/T2 shrink to just the lock field.
8. **DND suppresses, doesn't queue.** A link that breaks during DND isn't toasted after DND ends (it is in the panel). Queuing would need a "pending" list plus a DND-ended watcher; I'd skip it.
9. **DND read is an undocumented Steam internal.** A Steam update could rename it; the code treats a failed read as "not DND", so the worst case is a toast during DND, never a crash.

## Out of scope (named, not built)

- Click-to-open on a toast (the desktop app's toast does nothing on click either)
- A configurable check interval
- A "quiet while playing" mode
- Queuing toasts suppressed by Do Not Disturb or mute

## Progress

- 2026-11-25: planned with the owner. Next: T1 to T4 (one SaveTether PR), with T5, T6 and T6b in the plugin repo in parallel.
- 2026-11-25: T1 to T4 done on `feature/decky-notification-support`: `NotificationSetting` in the core, `savetether notifications [on|off]`, `status.operation_running`, docs and the mockup. Found in T3: the lock's holder file stayed after release, so a running desktop app that had finished an operation would have read as `operation_running` for as long as it ran; `OperationLock.Dispose` now deletes it, with a test (the lock is shared with apply and revert: review that diff). `dotnet build -warnaserror` and `dotnet test` pass on Windows; not run on the test machines. Left: the PR, then the version bump and the `v0.7.0-beta2` tag after merge (owner's go). Next: T5, T6 and T6b in the plugin repo.
- 2026-11-25: fixed from a code review of T1 to T4: `status` looks at the lock holder before and after reading the links; an unreadable holder file names no holder; releasing the lock retries the holder file's delete while a reader has it open.
- 2026-11-25: fixed from a second review: releasing the lock always lets it go, even when something other than a file sits at the holder path; a release that must wait for a reader finishes on its own thread, not the app's UI thread; `status` also compares the lock file's modified time (stamped on every take) before and after, catching an operation that started and ended while it read; a holder whose start time cannot be read counts only when its process is a SaveTether binary.
- 2026-11-25: hardware run for T1 to T3 on the Deck (3ff410e; `dotnet test` 1025 passed on the Mint box): `notifications` on, off and a bad value; `operation_running` true throughout a slow apply run by another process, false before and after, with no holder file left; the app sends no notification when the switch is off and one when it is on. Flow and results in `docs/testing/hardware-testing.md`, "Notifications switch and `operation_running`". The Deck's real setup was unchanged. Not run on hardware: the app idling after an operation of its own.
