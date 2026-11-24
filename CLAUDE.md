# SaveTether: notes for agents

SaveTether redirects game save folders into a synced folder, so several devices share one copy. A .NET 10 core with a CLI (`savetether`) and an Avalonia desktop app (`savetether-gui`), both shipped as Native AOT binaries for Windows and Linux, including the Steam Deck.

## Where things are

| What | Where |
|---|---|
| What SaveTether does and why | `docs/design/save-redirection-design.md` (sections like "5.3" refer to it) |
| The desktop app: every screen, state and refusal | `docs/design/desktop-app-spec.md`; "As built" notes record where the build differs |
| What is built, what is next, the hardware checklist | `docs/plans/implementation-plan.md` (section 5 is the desktop app, section 8 the checklist) |
| A phase's tasks, for delegation: one self-contained file per phase | `docs/plans/phase-3.5-ludusavi-definitions.md` (definitions from the Ludusavi manifest, tasks T1 to T9); `docs/plans/phase-3.6-device-maintenance.md` (device removal, reset, rename and maintenance, tasks T1 to T10); `docs/plans/phase-3.7-network-share.md` (sync folder on a Windows network share, tasks T1 to T8) |
| Every CLI command, for people, with examples | `docs/cli.md` |
| Ways to share the sync folder, and what each needs | `docs/sharing.md` |
| The `--json` contract | `docs/cli-json.md`, checked by `JsonContractTests` |
| How to test on the real machines, and the test flow for each feature | `docs/testing/hardware-testing.md` |
| Releasing | `docs/releasing.md` |

Code: `src/SaveTether.Core` (all logic), `src/SaveTether.Cli`, `src/SaveTether.Gui.Presentation` (the app's logic and texts, no UI toolkit, tested), `src/SaveTether.Gui` (Avalonia views, built in code), `tests/SaveTether.Tests`.

## How work is done here

- Branch from `development`; open the PR against `development`; it is squash-merged once CI passes on all three targets (win-x64, linux-x64, linux-arm64).
- Stage files by name, never `git add -A`.
- Before a PR: `dotnet build -warnaserror` and `dotnet test`. A change to the app also runs its flow in `docs/testing/hardware-testing.md` on the test machines, and the results go in the plan. Bugs found that way get a regression test.
- Docs change with the code: the spec's "As built" notes, the plan's progress entry, `docs/cli.md` for any command or option change, `docs/cli-json.md` for any `--json` change.
- Every delete, move or link goes through `SafeFs` (a test fails otherwise). The app holds no logic of its own: it calls the core, as the CLI does.
- No new dependency without the owner's approval. Avalonia's build telemetry is switched off in `Directory.Build.targets`; keep it so.

## Recording what you learn

What a session learns about testing or developing SaveTether is written down in the same PR, so the next session starts with it:

- How to test, tool and platform quirks, a new feature's test flow: `docs/testing/hardware-testing.md`.
- Where the build differs from the spec, and why: the spec's "As built" note for that section.
- What was built, tested and found: the plan's progress entry and hardware checklist.
- A change to how work is done here: this file. Keep it short; it is an index, loaded into every session.
- Hosts, users, keys and other private details: the owner's private notes, never this public repository.

## Test machines

A Linux Mint box (builds and tests on Linux) and the owner's Steam Deck (the real target). How to reach them is in the owner's private notes, not in this public repository. `docs/testing/hardware-testing.md` says what each is for, what may be changed on it, and how to run, drive and capture the app there. The Deck holds real saves: use throwaway folders under `~/sttest` with a guarded `SAVETETHER_HOME`, and touch its real setup only with the owner's leave.
