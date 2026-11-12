# Design brief: cross-device save-folder redirection manager

> Prompt for Claude Fable 5.2. Paste everything below the line.

---

You are a senior software architect. I need you to **design** (not implement yet) a tool that replaces my hand-written symlink scripts for syncing game and emulator saves between devices. Give me several viable solution designs, compare them honestly, and recommend one. If you think the premise is wrong (for example, that symlinks are the wrong mechanism, or that I should adopt an existing tool instead of building one), say so plainly and explain why.

## The problem

I play the same emulated and standalone PC games on several devices and want to pick up where I left off on any of them:

- **Windows 11 desktop** (x64). Emulators live mostly under `E:\LaunchBox\Emulators\...`; some games are on `G:\`.
- **Steam Deck** running SteamOS (x64, Arch-based, immutable root filesystem, `deck` user). Emulators are mostly Flatpaks or EmuDeck-managed. Some data is on a microSD card mounted at `/run/media/deck/<uuid>/`. Windows games run through Proton, so their saves live in `~/.local/share/Steam/steamapps/compatdata/<appid>/pfx/drive_c/users/steamuser/...`.
- **Possibly a Steam Frame** later. Treat its details as unconfirmed: check what you know about its OS and CPU architecture (I believe it runs SteamOS on ARM64), say how sure you are, and design so supporting it does not need a rewrite.

One folder on each device is managed by **Syncthing**: `E:\CloudSaveEmulation` on Windows and `/home/deck/Sync` on the Deck. Syncthing copies it between the devices and to my **Unraid** server, which also keeps backups.

### Current solution

Each game or emulator has its own `.bat` (Windows) or `.sh` (Deck) script. The script deletes or moves the app's save folder and replaces it with a directory symlink pointing into the Syncthing folder. On the Deck, a `run_all_enabled.sh` script runs every script in an `enabled/` folder. Representative mappings:

| App | Device | Original location (becomes a link) | Target in the sync folder |
|---|---|---|---|
| yuzu / yuzu-EA / Citron / Eden (Switch) | Windows | `<emu>\user\nand\user\save`, `<emu>\user\nand\system`, `<emu>\user\load` | `CloudSaveEmulation\<emu>\save`, `system`, `load` |
| Eden (Switch) | Deck | `<sdcard>/Emulation/storage/yuzu/nand/user/save`, `nand/system`, `load` | `~/Sync/yuzu/save`, `system`, `load` |
| PPSSPP | Deck (Flatpak) | `~/.var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP/{SAVEDATA,PPSSPP_STATE,GAME,TEXTURES,SCREENSHOT}` | `~/Sync/PPSSPP/PSP/...` |
| RetroArch | Deck (Flatpak) | No link. The script edits `savefile_directory` and `savestate_directory` in `retroarch.cfg` | `~/Sync/Retroarch/saves`, `states` |
| Final Fantasy IX (Steam) | Windows | `%USERPROFILE%\AppData\LocalLow\SquareEnix\FINAL FANTASY IX` | `CloudSaveEmulation\FINAL FANTASY IX` |
| Final Fantasy IX (Steam) | Deck (Proton) | `compatdata/377840/pfx/.../AppData/LocalLow/SquareEnix/FINAL FANTASY IX` | `~/Sync/FINAL FANTASY IX` |
| FFXI (HorizonXI private server) | Windows | `G:\Windows Games\HorizonXI\...\FINAL FANTASY XI\USER` (macros) | `CloudSaveEmulation\FINAL FANTASY XI\USER` |
| Visual novels (DohnaDohna, Rondo2) | Both | Game-specific `SaveData` folders, on the SD card on the Deck | `Sync/VisualNovels/<game>/SaveData` |

### Known weaknesses of the scripts

Use these as requirements, not just background:

1. **Inconsistent backups.** Some scripts copy the data before deleting it. Others (FF9 on Windows, PPSSPP on the Deck) `rm -rf` the original with no backup at all.
2. **Not idempotent.** Running a script twice can back up the link instead of the data, or delete a link and recreate it blindly. Nothing records what was done or how to undo it.
3. **No first-run policy across devices.** When the second device links, its local saves are thrown away or backed up silently. Nothing decides which device's data is authoritative, or warns when both have diverged.
4. **Hard-coded, machine-specific paths.** These include drive letters, SD card UUIDs, Steam app IDs and Flatpak IDs, and the scripts drift apart (the Eden Deck script still uses yuzu paths and links folders the Windows script has commented out).
5. **Windows needs admin rights** for `mklink /D`. Directory junctions or Developer Mode might avoid that; evaluate both.
6. **Too much data gets synced.** Some links pull in folders I don't need (shader caches, textures, screenshots, DLC/`load` mods). I want the sync folder kept to genuine save data plus anything really needed to resume. **Exception: emulator NAND folders (for example Eden's `nand/system`) are acceptable to sync** where saves or profiles depend on them.
7. **No health check.** If an emulator update, Proton prefix rebuild, or reinstall replaces the link with a real folder, nothing notices. Saves then silently stop syncing.
8. **Secrets in the synced folder.** A script in the synced folder holds a plaintext network-share credential (it adds a CIFS mount to `fstab`). The new design must keep credentials out of the Syncthing folder and out of any shared config or manifest.

## What I want

- **A standalone app** that runs on Windows and SteamOS (and later the Steam Frame), replacing the per-game scripts.
- **Configuration:** choose the sync root per device, then add games or emulators and choose which of their folders to sync.
- **Autodetection** of installed known apps and their save locations: emulators (Flatpak, AppImage, EmuDeck, portable Windows installs) and standalone/Steam/Proton games. The detection data should be extensible without rebuilding the app.
- **One-click apply** that redirects the chosen folders, plus a matching **one-click revert**.
- **A mandatory backup before any change** to a game or emulator folder. It must be verified, timestamped and kept *outside* the sync folder, so a mistake can't propagate to other devices. It must also be restorable from inside the app.
- **Minimal sync footprint** (see weakness 6), ideally with per-app guidance on what counts as a save and what is cache.
- **Usable on the Deck**, ideally from Game Mode with a controller, or at least with very little Desktop Mode work.

## Topics your designs must address

1. **Redirection mechanism.** Compare symlinks, Windows junctions, bind mounts, emulator config redirects (as with RetroArch), and copy-based sync wrapped around game launch (for example a Steam launch-option wrapper that copies saves in before launch and out after). Cover Syncthing's behaviour with each, how emulators react to each (atomic-rename saves, apps that delete and recreate folders), and Proton/Wine handling of links inside prefixes. A hybrid where each app definition picks its mechanism is allowed.
2. **App-definition / manifest format.** How a known app is described: path templates with tokens such as `{steam_compatdata:<appid>}`, `{flatpak:<id>}`, `{sd_card}`, `{home}` and `{appdata}`, which folders to include or exclude, the mechanism to use, version quirks, and how the same logical app maps across OSes. Show a concrete example for one Switch emulator, PPSSPP, RetroArch, and one Proton game. Consider whether existing community data (for example the Ludusavi manifest, built from PCGamingWiki) can be reused, and what its licence allows.
3. **Safe apply/revert as a transaction.** Preconditions (sync root exists and is reachable, enough free space, app not running, target not already linked), backup, checksum verification, the switch, post-verification, a journal for crash recovery, and rollback. Spell out the dangerous edge cases explicitly: `rm -rf` following a link, an empty or not-yet-synced sync folder on a new device, two devices both holding newer saves, and the sync folder on a removable drive that isn't mounted.
4. **Multi-device onboarding.** A "seed from this device" versus "join existing" flow, how divergent saves are detected and shown to me, and how Syncthing `.sync-conflict` files are surfaced.
5. **Drift detection / health status** for each mapping, and how the app reports it (on demand, on startup, or a background service), with the resource cost on a handheld.
6. **Per-device state versus shared config.** Decide what lives in the sync folder (shared app definitions, the device-agnostic logical mapping list) and what stays local (resolved paths, backups, journal). Avoid committing machine paths into synced files.
7. **Tech stack and distribution** for Windows x64, Linux x64 and Linux ARM64 on an immutable SteamOS.
   - **My skills:** I work mainly in **C#, C++ and C**, but I'm happy to learn a mainstream stack if it suits the project better. Don't pick C# or C++ just because I know them. Pick what is best for portability, maintainability and attracting open-source contributors, and state my learning curve as a cost. Note which parts must use particular languages whatever the core is written in (for example, a Decky Loader plugin needs Python and TypeScript).
   - **Candidates to compare at minimum:**
     - C#/.NET (self-contained single-file or Native AOT publish), with Avalonia or another cross-platform UI, or no GUI at all.
     - C++ with Qt (including its LGPL licensing and build and packaging effort), or with a lighter UI such as Dear ImGui (whose controller support is relevant for Game Mode).
     - Rust, either as a CLI-only core or with Tauri or a native Rust GUI.
     - Go, with a CLI core and a thin UI.
     - Any other mainstream option you think is competitive, for example Python or Electron. Say plainly if one should be ruled out.
   - **For each candidate, cover:**
     - How it creates, checks and inspects symlinks, Windows junctions (junctions may need native Win32 calls) and file metadata on each OS.
     - Cross-compiling or CI builds for all three targets.
     - Binary size and startup time on the Deck.
     - Controller and Game Mode usability.
     - Testability.
     - How many third-party dependencies it brings in and how mature they are.
   - **Distribution:** weigh Flatpak (including whether its sandbox gets in the way of making links into other apps' Flatpak data and Proton prefixes), AppImage, a single self-contained binary, and a Decky Loader plugin.
   - **UI shape:** desktop GUI, controller-friendly UI, or a CLI core with thin UIs.
   - **Privileges:** admin/UAC on Windows; none on SteamOS if possible.
   - **Long-term maintenance:** how each choice's dependencies affect it.
8. **Build versus adopt.** Evaluate existing tools honestly against my requirements: Ludusavi, EmuDeck's cloud save feature, Syncthing-only approaches, and anything else relevant you know of. If the best answer is "use X plus a thin wrapper", say so.
9. **Migration** from my current script-based setup without losing data, including adopting links that already exist instead of recreating them.
10. **Testing strategy** for a tool whose main risk is data loss: dry-run mode, fixture file trees, and cross-OS CI.
11. **Open-source project setup.** I intend to publish this as an open-source project. Cover:
    - **Licence:** for example MIT, Apache-2.0 or GPL-3.0, and how it interacts with each dependency's licence and with any reused data (check the licences of the Ludusavi manifest and the PCGamingWiki data it is built from; don't assume they allow reuse).
    - **Community-maintained app definitions:** a repository layout where definitions live in their own files, a schema contributors validate against, a review process for new entries, and possibly versioning the definitions separately from the app.
    - **Builds and signing:** public CI that builds and tests on Windows, Linux x64 and Linux ARM64. Reproducible release builds, and code signing (Windows SmartScreen warnings for unsigned binaries) with what it costs.
    - **Distribution channels:** GitHub Releases, Flathub, the Decky plugin store, winget or Scoop. Note what each one requires.
    - **User data:** no telemetry by default; the sample config holds no personal paths or credentials.
    - **Contributor accessibility:** how the stack choice affects who can contribute.

## Deliverables

1. **Restated requirements and assumptions.** List anything you had to assume and the questions you would ask me.
2. **Two to four distinct solution designs.** For each: architecture overview, redirection mechanism(s), data model, apply/revert flow, UX on Windows and on the Deck, stack, and an effort estimate in rough sizes (S/M/L) per component.
3. **A comparison table** covering safety, footprint, Deck usability, maintainability, effort, and Steam Frame readiness. Add a **separate table comparing the tech stacks** from topic 7. Score each one on portability, maintainability, contributor appeal for an open-source project, Deck and Game Mode fit, and my learning cost (I know C#, C++ and C).
4. **A recommendation** with its main tradeoffs and the risks that would make you change it.
5. **For the recommended design:** a component diagram (Mermaid), the manifest schema with the four examples above, the apply/revert state machine, and a phased MVP plan where phase 1 replaces my current scripts safely before any autodetection work.
6. **Open questions and unknowns.** Separate verified facts from inferences, and say what would confirm each inference.

Be concrete and skeptical. Prefer simple, safe, maintainable designs over clever ones. Data safety outranks convenience.
