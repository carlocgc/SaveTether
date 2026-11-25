# SaveTether phase 4: Decky plugin (status first)

Section 6 of the [implementation plan](implementation-plan.md) points here. This file holds the whole phase: the owner's decisions, the UI, the tasks (T1 to T9) with the model for each, the checks, and progress. A delegated task needs only this file and the files it names.

Planned 2026-11-25 by the owner and the agent. T1 (`status --sizes`) and T2 (these docs) are done.

## Context

Design 5.11 (`docs/design/save-redirection-design.md`) already plans a status-first Decky plugin: Game Mode shows link health, and all logic stays in the core. The Python backend only runs `savetether ... --json`. This plan sets the first version's scope, UI, distribution and tests.

Decisions made with the owner (2026-11-25):

- **Distribution: GitHub releases only, not the Decky store.** The store rejects LLM-written plugins (wiki "Submitting Plugins", "AI, LLMs and so on"; the database PR checklist asks that "Generative AI was NOT used to write a majority of the code"). Store builds also come only from the database repo's own CI after a human-reviewed PR, so our CI could never publish to testing.deckbrew.xyz or plugins.deckbrew.xyz. Users install the zip with Decky's manual install (Developer mode).
- **Repo: a separate `SaveTether-Decky` repo**, MIT plus the template's BSD-3 notice. The plugin sits at the repo root, Node stays out of the .NET CI, and the only coupling is the frozen `--json` contract v1.
- **Scope v1: read-only.** Overview, a Check now button, and one card per link. Recover and the choice-free Apply from 5.11 move to a later phase. A read-only plugin cannot harm the real setup.
- **Sizes: a new opt-in `status --sizes`** adds `bytes` and `files` to each mapping. The desktop app measures today (`FolderSizes.cs`, `Workspace.Measure` at `Workspace.Apps.cs:436`), but the CLI does not.

## UI (from the survey of store plugins)

Store plugins (Ludusavi, Syncthing, ZeroTier) all use the same QAM idiom: the title with a back arrow, short grey info lines, one full-width primary button, uppercase section headers, and full-width rows with a status icon on the left. We follow it.

The mockup is the layout reference for T6 and the expected look in T9. Open [decky-mockup.html](../design/decky-mockup.html) in a browser; the text version below is the same layout.

```
 All linked                          Needs attention
┌───────────────────────────────┐   ┌───────────────────────────────┐
│ ← SaveTether                  │   │ ← SaveTether                  │
│ ┌───────────────────────────┐ │   │ ┌───────────────────────────┐ │
│ │ ● All 4 links healthy     │ │   │ │ ● 2 links need attention  │ │ (amber dot)
│ │ Sync folder               │ │   │ │ Sync folder               │ │
│ │ /run/media/deck/SD/Sync   │ │   │ │ /run/media/deck/SD/Sync   │ │
│ │ steamdeck · checked 14:21 │ │   │ │ steamdeck · checked 14:23 │ │
│ └───────────────────────────┘ │   │ └───────────────────────────┘ │
│ [ Check now              ⟳ ]  │   │ [ Check now              ⟳ ]  │
│ LINKS                         │   │ LINKS                         │
│ ▌● Hollow Knight      Linked  │   │ ▌● Stardew Valley  Not linked │ amber
│ ▌  2.1 MB · 14 files          │   │ ▌  18.4 MB · 62 files         │
│ ▌● Stardew Valley     Linked  │   │ ▌  The save folder holds      │
│ ▌  18.4 MB · 62 files         │   │ ▌  files again. Open the      │
│ ▌● Celeste            Linked  │   │ ▌  desktop app to relink.     │
│ ▌  96 KB · 3 files            │   │ ▌● Celeste  Shared folder     │ amber
│ ▌● Dolphin (GC)               │   │ ▌           missing           │
│ ▌     Not on this device      │   │ ▌  size unknown               │
│ ▌  412 MB · 9 files           │   │ ▌● Hollow Knight      Linked  │ green
└───────────────────────────────┘   │ ▌● Dolphin (GC)  Not on this… │ grey
                                    └───────────────────────────────┘
```

Each card is a dark row with a 3 px left bar in its shade colour (green, grey or amber). The state word sits right-aligned in the same colour.

- **Overview block:** a dot and a headline (overall state), the sync folder path (wraps), then `device · checked HH:MM`.
- **Check now:** a full-width `ButtonItem`. It shows "Checking…" while it runs.
- **LINKS section:** one focusable card per mapping.
  - Line 1: a dot, the app name, and the state word on the right.
  - Line 2: `size · N files`, or "size unknown".
  - Line 3: amber cards only, the `detail` sentence plus "Open the desktop app to fix" where the fix needs Desktop Mode.
  - Cards needing attention (amber) sort first, then by app name, as the desktop app sorts them.
- **States without a card list:**
  - CLI not found: "Install SaveTether in Desktop Mode" plus the path searched.
  - Not set up: shown as "Cannot check this device" with the CLI's sentence, because the CLI reports a missing setup as `not-found`, the same problem as a missing device file (found in T5).
  - Journal open: "Unfinished operation. Open the desktop app."
  - Sync folder unavailable.
  - Check failed (the CLI's `message`).
- Colours and words copy `StateWords.cs` exactly: green Linked; grey "Not on this device" and "Ready to link"; everything else amber. The overall order copies `OverallState.cs:156-162`.
- The plugin checks when the panel opens, and again only on Check now. It never polls in the background, which saves battery during games.

## Tasks

The model shown is the recommended implementer. Opus takes design-heavy or judgement work; Sonnet takes tasks fully specified here.

### In the SaveTether repo (done 2026-11-25, one PR)

**T1 — `status --sizes` (Sonnet)**
- Files:
  - `src/SaveTether.Cli/ArgParser.cs:69` (the `status` entry; add the flag the way other boolean options are declared)
  - the status handler in `src/SaveTether.Cli/CliApp.cs`
  - `docs/cli.md`, `docs/cli-json.md` (the status section, lines 180-200)
  - `tests/SaveTether.Tests/Cli/json-shapes.txt`, `JsonContractTests`
- Behaviour: with `--sizes`, each mapping gains `bytes` and `files`. They measure the `target` (the shared copy) with `Workspace.Measure`. They are left out when the target is missing or cannot be read, which matches the contract rule that a missing value is absent, not null. Without the flag, output is unchanged byte for byte.
- Acceptance:
  - `dotnet build -warnaserror` and `dotnet test` pass.
  - A new test covers an existing target, a missing target, and a run without the flag.
  - The shapes file records the new shape.

**T2 — Docs in SaveTether (Sonnet, after T1)**
- New `docs/plans/phase-4-decky-plugin.md`: this plan, self-contained, in the style of the 3.x phase files.
- New `docs/design/decky-mockup.html`: the planning mockup as a standalone page (both states side by side), with the ASCII version in the phase file.
- Design 5.11: a dated revision note covering read-only v1, GitHub-release distribution, the store's AI policy and the separate repo.
- `implementation-plan.md`: a phase 4 progress entry.
- `CLAUDE.md`: an index row for the phase file and the plugin repo.

### In the new `SaveTether-Decky` repo

**T3 — Scaffold (Sonnet)**
- Start from `SteamDeckHomebrew/decky-plugin-template`. Delete `backend/`, `defaults/`, and `assets/` unless used.
- `plugin.json`: name "SaveTether", flags `[]`. Leave out `_root`, so the backend runs as `deck` and `HOME` is `/home/deck`.
- `LICENSE`: MIT on top, the template's BSD-3 kept below it.
- README: what it is, how to install manually, and that the desktop app or CLI must be installed first.
- `pnpm-lock.yaml` uses lockfileVersion 9.0.
- Acceptance: `pnpm i && pnpm run build` produces `dist/index.js`.

**T4 — Python backend (Sonnet)**
- `main.py`, `class Plugin`, with two async methods:
  - `status()`: runs `<cli> status --json --sizes`. On exit 64 (an older CLI without `--sizes`) it retries without `--sizes`.
  - `apps()`: runs `<cli> app list --json`, for the id-to-name join on the part of the mapping id before the dot.
- Every call uses a 20 s timeout and returns `{ok, exit_code, json | error}`. The backend never interprets the JSON.
- CLI path: `$DECKY_USER_HOME/.local/bin/savetether`.
- Test-only override: an optional `savetether_home` in `$DECKY_PLUGIN_SETTINGS_DIR/settings.json`. It is used as `SAVETETHER_HOME` only if it resolves under `$DECKY_USER_HOME/sttest/`, and refused otherwise. This mirrors the guard in `hardware-testing.md:21-31`.
- Tests: `tests/test_main.py` with stdlib `unittest`, a stub `decky` module, and a fake CLI shell script. Cover success, timeout, CLI missing, exit 64 fallback, and an override outside `sttest` being refused.

**T5 — Presentation module (Opus)**
- `src/states.ts`, pure functions with no React:
  - `cardFor(mapping)` returns `{word, shade}`, a port of `StateWords.cs:6-42`.
  - `overall(statusJson, exitCode)` returns `{headline, shade}`, a port of the `OverallState.cs` order.
  - `sortCards`.
  - `formatSize`, using the same units as the desktop app.
- Unknown states fall back to amber with the `detail` sentence, so a newer CLI never crashes the plugin (contract line 11).
- Tests: `node --test --experimental-strip-types`, so no new dependency. Fixtures are copied from `json-shapes.txt` and cover every state and reason row.

**T6 — QAM UI (Opus, after T5)**
- `src/index.tsx`, built from `PanelSection`, `PanelSectionRow`, `ButtonItem` and `Focusable` from `@decky/ui`.
- Each card is focusable, so the controller can scroll through the list.
- Each card carries `data-st-card`, `data-st-state` and `data-st-name`, and the headline carries `data-st-overall`, for T8.
- Acceptance: it builds, and it looks like the mockup on the Deck at 1280×800 in both a healthy and an amber state.

**T7 — Release workflow (Sonnet)**
- `.github/workflows/release.yml`, on tag `v*.*.*`. It follows SaveTether's `release.yml` rule: a plain `vX.Y.Z` must be on `main` and becomes a release; `vX.Y.Z-suffix` from `development` becomes a prerelease.
- Steps: put the version from the tag into `package.json`, then `pnpm i --frozen-lockfile`, `pnpm run build`, the T4 and T5 tests, and zip `SaveTether/{dist/index.js,main.py,plugin.json,package.json,LICENSE,README.md}`.
- Publish `savetether-decky-vX.Y.Z.zip` and `SHA256SUMS.txt` with `gh release create`.
- Also add `ci.yml` on push and PR: build and tests.
- Acceptance: a `v0.1.0-test1` tag on a scratch branch produces a prerelease zip that installs through Decky's manual install.

**T8 — Game Mode test harness (Opus)**
- `scripts/qam-check.mjs` uses only Node 22 built-in `fetch` and `WebSocket`. It talks to the CEF debugger (port 8081, wiki "Frontend Debugging"), reached through `ssh -L 8081:localhost:8081 <deck>`. Steps:
  1. `GET /json`, then find the `QuickAccess` target.
  2. Open the QAM on our plugin through the SharedJSContext target (`window.DFL` exposes Navigation). Finding the exact call is part of this task.
  3. Press Check now by dispatching a click on the button.
  4. Read every `[data-st-card]` and `[data-st-overall]` as JSON and compare with an expected file.
  5. Save a screenshot with `Page.captureScreenshot`.
- Scenarios, each a throwaway `SAVETETHER_HOME` under `~/sttest`, set through the T4 override:
  - (a) all linked
  - (b) a save folder holds files again (`replaced`/`folder-holds-files`)
  - (c) the shared folder deleted (`broken`)
  - (d) the sync root on a dead `fuse-overlayfs` mount (existing recipe, `hardware-testing.md:316`). The panel must show "Sync folder unavailable" within the 20 s timeout and not freeze.
  - (e) the CLI renamed away, giving the "Install SaveTether" state.
  - (f) the real setup with no override. The panel must match `savetether status --json` run over SSH, and the setup is the same before and after.
- Write a new "Decky plugin in Game Mode" section in SaveTether's `docs/testing/hardware-testing.md`. It covers enabling Developer mode and CEF debugging (and turning it off after: it is open to the whole network), the tunnel, the scenarios, and the cleanup.

**T9 — Hardware run (Opus, with the owner)**
- Run T8 on the Deck in Game Mode with the T7 zip installed through manual install.
- Then switch to Desktop Mode and back, and reboot once to confirm the plugin reloads.
- Record the results in the phase file and the section 8 checklist. Every bug found gets a regression test in T4 or T5.

### Order and parallelism

- T1, T3 and T5 can start at once.
- T4 follows T3, and T6 follows T4 and T5.
- T7 can follow T3 at any time.
- T8 needs T6, and T9 needs everything.
- T2 lands with T1.

## Prerequisites needing the owner's leave

- Decky Loader installed on the Deck. Its Developer mode and "Allow Remote CEF Debugging" are switched on for testing and off after.
- A public GitHub repo `carlocgc/SaveTether-Decky`: created 2026-11-25.

## Verification (end to end)

1. SaveTether PR: `dotnet build -warnaserror`, `dotnet test`, CI green on all three targets. On the Deck, `savetether status --json --sizes` against a `~/sttest` home shows `bytes` and `files`.
2. Plugin repo: CI runs the build, `node --test` and `python -m unittest`.
3. Deck: install the prerelease zip, then run `node scripts/qam-check.mjs` for scenarios (a) to (f). All match their expected JSON, the screenshots are saved, and the real setup's `status` is unchanged before and after.

## Out of scope (named, not built)

- Recover and Apply buttons
- Per-card actions
- Full-screen pages
- Background polling and toasts
- Bundling the CLI in the plugin zip (the plugin relies on the installed CLI, like Decky Ludusavi does)
- Store submission

## Progress

- 2026-11-25: T1 done: `status --sizes` adds `bytes` and `files` per mapping (the shared copy, measured with `Workspace.Measure`; absent when the copy is not there); the text output adds "(N file(s), size)". `StatusSizesTests`, a `status-sizes` scenario in `JsonContractTests`, `docs/cli.md`, `docs/cli-json.md`. T2 done: this file, the mockup page, design 5.11's revision note, the plan's section 6.
- 2026-11-25: the owner created https://github.com/carlocgc/SaveTether-Decky (public) and installed Decky Loader on the Deck with Developer mode and CEF debugging on. Next: T3, T5 (and T7) in parallel.
- 2026-11-25: T3 and T5 done in SaveTether-Decky (PR to its `development`, which was created from `main` for this). T3: the template's build config, v9 lockfile and `decky.pyi` kept; `backend/`, `defaults/`, `assets/` and `py_modules/` dropped; no `_root` flag; MIT plus the BSD-3 notice. T5: `src/states.ts` with 8 tests under `node --test`, after `tsc --noEmit --skipLibCheck`, which is needed because the template's libraries have type errors of their own. Pnpm runs through `corepack pnpm@9`. Next: T4 (backend), T7 (release workflow).
- 2026-11-25: T4 and T7 written in SaveTether-Decky (branch `feature/backend-and-release`). T4: `main.py` returns `{ok, exit_code, json}` whatever the CLI's exit code, since the CLI prints JSON on failures too, or `{ok: false, exit_code, error, message}` with `error` one of `cli-missing` (message: the path searched), `timeout`, `bad-output` or `home-refused`. The CLI runs in its own process group, killed as a whole on timeout, so a child cannot hold the pipes open (found when the fake CLI's `sleep` outlived it). 8 `unittest` tests, run on the Deck in `~/sttest` (the Mint box was off). T7: `ci.yml` and `release.yml`, one job each. The T7 acceptance (a `v0.1.0-test1` prerelease installed through Decky) is not run yet. Next: T6, then the test tag.
- 2026-11-25: T6 done in SaveTether-Decky (branch `feature/qam-ui`). `src/index.tsx` has the overview block, Check now ("Checking…" while it runs) and one `Focusable` card per link with the `data-st-*` attributes; backend failures map through a new `failureOverall` in `states.ts` (CLI missing: "Install SaveTether in Desktop Mode"; timeout: "Sync folder not available"; otherwise "Check failed"), tested. The owner installed a hand-built zip on the Deck; the panel matched the mockup at 1280×800 for the real setup (All linked, 38 cards) and for a throwaway home with a `replaced` and a `broken` link (amber, sorted first, with the CLI's sentence). Found there: apps with several shared folders showed identical cards, so each card now shows its role in brackets, "Dolphin (GC)" style; this fix is built but not yet seen on the Deck. Also found by the owner with the controller: the cards took focus with nothing to show it, so focused cards now get a lighter background and a white outline (seen on the Deck, looks right); and moving up skipped the overview straight to the back button, leaving it half scrolled out, so the overview is now focusable too (not yet seen on the Deck). The Deck's CLI (0.6.1) has no `--sizes`, so every card said "size unknown", as designed. The T8 calls for opening the panel are found and in `hardware-testing.md` ("The Decky plugin in Game Mode"). Not checked: where focus goes when Check now is disabled during a check. Next: T7's test tag, then T8.
