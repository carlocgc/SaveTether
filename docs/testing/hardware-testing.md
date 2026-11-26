# Testing on real hardware

Unit and integration tests run in CI on Windows, Linux x64 and Linux ARM64 (`dotnet test`). They cannot show a window, a tray icon, a notification, a file manager, or how the app behaves on the Steam Deck. This page is how those are checked, on the test machines, for each feature. A new feature adds its flow to the last section.

The results go in the implementation plan, section 8 (hardware checklist) and under the item's "Progress" entry, with the date and what was and was not seen.

## The test machines

| Machine | What it is for | What may be changed |
|---|---|---|
| Windows PC | Where development happens. Builds, tests and the Windows app | Anything in the repository and in throwaway folders. The owner's real SaveTether setup only with the owner's leave |
| Linux Mint box (Cinnamon, X11) | Linux build and test: it has the .NET SDK and clang, so it builds the Native AOT binaries for Linux, and runs the full test suite. Its screen is usually locked | A clone of the repository, and throwaway folders. It also has a real SaveTether device set up against the shared sync folder: leave that alone |
| Steam Deck (SteamOS, KDE Plasma, Wayland with XWayland) | The real target: the app in Desktop Mode, the tray on KDE, SD cards, Flatpak emulators | It is the owner's real device with real saves. Throwaway folders under `~/sttest` only. Its real setup only with the owner's leave, after a backup, and compared with that backup after (see "Testing against a real setup") |

How to reach the Linux machines (hosts, users, SSH keys) is not in this public repository. It is in the owner's private notes, which an agent working for the owner has as memory. Ask the owner if it is missing.

There is no .NET on the Deck. Build on the Mint box and copy the binaries over through the PC.

## Rules

1. **Every remote script points SaveTether at a throwaway folder and checks it.** Set `SAVETETHER_HOME` to a folder under the scratch area, and stop if it is not. A script that forgot this once opened the Mint box's real local folder. Source an environment file like this at the top of each script:

   ```bash
   export SAVETETHER_HOME=$HOME/sttest/<feature>/home
   case "$SAVETETHER_HOME" in "$HOME"/sttest/*) ;; *) echo "SAVETETHER_HOME is not the scratch folder"; exit 1;; esac
   ```

2. **Throwaway sync folders too.** A test device gets its own sync folder under the scratch area, set up with `init --create-root` (which creates the `savetether` folder inside it, not the sync folder itself: `mkdir` that first). Never point a test device at the real sync folder, which syncs to every device.
3. **Clean up after.** Stop what was started, remove the scratch folder, and check that the real setup is unchanged: `savetether status` before and after, compared. Run that check in a fresh shell, not one that sourced the test's environment file: with `SAVETETHER_HOME` still set, `status` reads the scratch folder just removed and says the device is not set up.
4. **Name the process exactly.** `pgrep -x savetether-gui`, never `pgrep -f`/`pkill -f` with a pattern that also matches the remote `bash -c` command line: that kills the SSH session itself.
5. **Do not leave windows behind** on the owner's screen. If a test opens one that cannot be closed remotely (see "Driving the app"), say so.

## Building for Linux

On the Mint box, in its clone of the repository, on the branch under test:

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
git fetch -q origin && git checkout -q <branch> && git pull -q --ff-only
dotnet test --nologo                                   # the whole suite, on Linux
dotnet publish src/SaveTether.Gui/SaveTether.Gui.csproj -c Release -r linux-x64 -o ~/sttest-gui/gui
dotnet publish src/SaveTether.Cli/SaveTether.Cli.csproj -c Release -r linux-x64 -o ~/sttest-gui/cli
```

The app is three files: `savetether-gui`, `libSkiaSharp.so` and `libHarfBuzzSharp.so`. The CLI is one: `savetether`. Copy them to the Deck through the PC (`scp` from the Mint box to the PC, then to the Deck), each file by name: a brace pattern in a remote `scp` path does not expand. Remove `~/sttest-gui` on the Mint box when done.

On Windows, a Native AOT publish needs `vswhere.exe` on `PATH`: prepend `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`.

Run the app from the publish folder, never from `bin\Release\net10.0\win-x64\native\`. That folder holds the compiler's intermediate exe without the native Skia library, so Windows loads whatever `libSkiaSharp.dll` it finds on `PATH`. On the PC that is Git for Windows' old one (88.1), and the app dies at start with "The version of the native libSkiaSharp library (88.1) is incompatible". Windows logs it as an application crash with code `c0000409`, which is how a Native AOT app ends on any unhandled exception: start the exe from a terminal to see the exception on standard error.

Before taking crash events in the Application log as new, compare their report names (`AppCrash_savetether-gui…_<id>`). Windows Error Reporting logs its queued reports again each time it retries sending them, after a login and at intervals. So one crash can appear as event 1001 several times, hours apart, with the same report IDs. The `Report.wer` in each report's folder under `C:\ProgramData\Microsoft\Windows\WER` names the exe that crashed (`AppPath=`).

**The end of the session on Windows** can be tested without restarting. Start the app against a throwaway local folder, press X, then send `WM_QUERYENDSESSION` (0x11) to every top-level window of the process, as Windows does, with `SendMessageTimeout`. A reply of 0 from any window is a veto: Avalonia's hidden message window answers for the app. Then send `WM_ENDSESSION` (0x16, wParam 1): the process should end, and `gui.log` say "session ending". Windows itself records a real veto in the Application log as event 10001 from Winsrv: "The following application attempted to veto the shutdown".

## Running the app

The app needs the desktop session's environment, which an SSH shell does not have.

**Mint box:**

```bash
export DISPLAY=:0 XAUTHORITY=$HOME/.Xauthority DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u)/bus XDG_RUNTIME_DIR=/run/user/$(id -u)
nohup ~/sttest-gui/gui/savetether-gui < /dev/null > ~/sttest-gui/gui.out 2>&1 &
```

**Steam Deck:** SteamOS stops every process an SSH session started when the session ends (`KillUserProcesses=yes`), so start the app as a user unit instead. XAUTHORITY changes with each login: read it from Plasma's environment.

```bash
export DISPLAY=:0 WAYLAND_DISPLAY=wayland-0 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u)/bus XDG_RUNTIME_DIR=/run/user/$(id -u)
export XAUTHORITY=$(tr "\0" "\n" < /proc/$(pgrep -x plasmashell | head -1)/environ | sed -n "s/^XAUTHORITY=//p")
systemd-run --user --unit=savetether-gui-<feature> --collect \
  -E DISPLAY -E WAYLAND_DISPLAY -E XAUTHORITY -E DBUS_SESSION_BUS_ADDRESS -E XDG_RUNTIME_DIR -E SAVETETHER_HOME \
  ~/sttest/<feature>/gui/savetether-gui
systemctl --user stop savetether-gui-<feature>          # when done
```

Each local folder has its own single-instance claim, so a test copy with its own `SAVETETHER_HOME` runs beside a copy the owner has running against the real setup. There are then two tray icons.

## Driving the app and seeing it

**Input.** The app runs through XWayland on the Deck and X11 on Mint, so `xdotool` can click and type in it:

```bash
W=$(xdotool search --onlyvisible --name '^SaveTether' | head -1)   # the visible window; a tray-only copy has a hidden one
xdotool windowactivate --sync $W
xdotool mousemove --window $W <x> <y> click 1                       # coordinates inside the window, below the title bar
xdotool type --delay 40 '/some/path'; xdotool key Return
```

Find coordinates from a capture of the window: subtract the capture's margin and the title bar, or use `xdotool getwindowgeometry $W`. The Mint box has no `xdotool`, but it has `libXtst`: copy `docs/testing/xinput.py` there for clicks and key chords (`xinput.py click <screen x> <screen y>`, `chord Control_L q`) in screen coordinates, which are the window's `xwininfo` "Absolute upper-left" plus the position in a capture from `import -window`. `xinput.py type <text>` types a path or name (US layout: letters, digits, space and / . - _); click the field and wait a second before typing, or the first keys are lost. KDE's own apps (Dolphin) are native Wayland: `xdotool` can neither see nor close their windows.

**On Windows**, UI Automation from PowerShell (`Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes`) reads the app's window by process ID: every row, dialog text and button name, so no capture is needed. Buttons answer `InvokePattern`, choices `SelectionItemPattern`; "Show steps" is an expander and answers neither. The tray icon is a `NotifyItemIcon` button under `Shell_TrayWnd` named "SaveTether …": right-click its centre (`SetCursorPos` and `mouse_event`) and its menu (Open SaveTether, Check now, Quit) appears as the app's own window, where `Quit` can be invoked. Ctrl+Q quits from the main window. Running a Debug build points the autostart entry at it; starting the installed copy again points it back.

**Seeing it.**

- **Deck:** `spectacle -b -n -a -o /tmp/shot.png` captures the active window. A blank white image means the display is asleep: wake it first with `xdotool mousemove_relative 1 0`. A copy started from a scratch folder first asks "Install SaveTether for this user?": press Not now, since Install writes to the real `~/.local`. Menus and other popups are separate windows that `-a` leaves out: capture the whole screen with `-f` and crop to the app window's geometry before looking. That also keeps the rest of the owner's desktop out of view.
- **Mint:** the screen is usually locked, so a full-screen capture shows the lock screen. Capture the window itself: `import -window $(xwininfo -root -tree | grep '"SaveTether' | awk '{print $1}' | head -1) /tmp/shot.png`.

Then copy the image to the PC to look at it.

**What it said.** The app writes one line per operation, refusal and unexpected error to `gui.log` in its local folder. An unhandled crash goes to standard error: on the Deck, `journalctl --user -u savetether-gui-<feature>`.

**A page that greys out and stays so** has usually thrown inside work started with `_ = SomethingAsync()`: a task nobody awaits keeps its exception, so nothing is logged or shown. Look for a control read inside `Task.Run` (Avalonia refuses that off the UI thread): read the values first, then start the work. Found on the Deck in Add app.

**Coordinates.** Take them from a capture made after the last change of layout: a banner, a dialog or a page change moves everything below it. A click that lands beside a button does nothing and gives no error, which reads like a bug in the app.

**`xdotool` over SSH on the Deck** needs the session's `XAUTHORITY` as well as `DISPLAY`, read from Plasma's environment as in "Running the app"; without it every call fails with "Authorization required". Keep the exports in a small file that each helper sources, written with a quoted heredoc: inserting the line with `sed` mangled its `'\0'` and `'\n'`.

**The tray icon's image** can be read without a screenshot of the panel: `gdbus call --session --dest org.kde.StatusNotifierWatcher --object-path /StatusNotifierWatcher --method org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems` lists `org.kde.StatusNotifierItem-<pid>-<n>`; the same call on that service for `org.kde.StatusNotifierItem IconPixmap` returns each size as width, height and ARGB bytes in network order, which a few lines of Python turn into a PNG. On Windows, `WM_GETICON` on the app's main window returns its title bar and taskbar icons, and `[System.Drawing.Icon]::ExtractAssociatedIcon` reads a program's or installer's own icon.

**Helpers.** Two small scripts in the scratch folder save typing: `click.sh x y [wait]` (activate the window, click at window coordinates, wait) and `shot.sh name` (wake the display, activate the window, `spectacle -a` to `/tmp`). With `spectacle -a` on the Deck, the window's client area starts about 65 pixels right and 81 down from the capture's corner (shadow and title bar): subtract that from a point in the capture to get the coordinates to click.

## Testing against a real setup

Some things only show with real data: the first run of setup on the Deck crashed on the Windows PC's device file, which no fixture had. With the owner's leave:

1. Back up the local `bootstrap.json` and `state.json`, the device's file in the shared folder, and the output of `savetether status`, to a folder under `~/sttest`.
2. Make the change the test needs, for example move `bootstrap.json` aside so the device counts as not set up.
3. Run the test.
4. Compare: `status`, `state.json` and the device file against the backup. Expect and explain any difference (a take-over gives the device file a new `install_id`).
5. If the test fails part way, put the backed-up files back at once and check again.

## The Decky plugin in Game Mode

The plugin lives in the `SaveTether-Decky` repository; its phase file is `docs/plans/phase-4-decky-plugin.md`.

**The harness** (T8, in the plugin repository): `scripts/qam-check.mjs` runs on the PC (Node 22+, no install) and `scripts/deck-scenarios.sh` on the Deck. The Deck must be in Game Mode with Decky's "Allow Remote CEF Debugging" on.

1. Copy `deck-scenarios.sh` to `~/sttest` on the Deck and run `deck-scenarios.sh setup`. It builds four throwaway homes under `~/sttest/decky`, each with `ppsspp.savedata` and `dolphin.gc` linked: (a) as is; (b) the PPSSPP link replaced by a folder holding a file; (c) Dolphin's shared folder deleted; (d) the sync folder on a `fuse-overlayfs` mount whose process was then killed.
2. Save the real setup's status first: `savetether status --json > real-before.json`, in a shell without `SAVETETHER_HOME`.
3. Open the tunnel: `ssh -N -L 8081:localhost:8081 <deck>`.
4. For each of a to d: `deck-scenarios.sh use <x>` on the Deck, then `node scripts/qam-check.mjs --expect scripts/expected/<x>.json --shot <x>.png` on the PC. Exit 0 means the panel matched; 1 prints both what it showed and what was expected; 2 means the harness could not run.
5. (e) needs the owner's leave: it renames the real `~/.local/bin/savetether` to `savetether.st-away`, runs `--expect expected/e.json` (the "Install SaveTether in Desktop Mode" state), and renames it back.
6. (f): `deck-scenarios.sh real`, then `--status real-before.json`: every mapping has a card, and a card says Linked exactly when the CLI says `ok`. Then save `status --json` again and `diff` it with the first: the real setup must be unchanged.
7. `deck-scenarios.sh cleanup` (removes the plugin's `settings.json`, unmounts d, removes `~/sttest/decky`), stop the tunnel's own `ssh`, and switch CEF debugging off.

**Toasts** (phase 4.1 T8; a plugin build with T7 and CLI 0.7.0-beta2 or later). The plugin logs every toast it decides on in `SharedJSContext`: `[SaveTether] toast: <body>`, `toast muted: …` or `toast hidden by Steam: …`. The harness collects those logged during its run as `toasts`; an expected file is compared only on the keys it has (`overall`, `cards`, `muted`, `toasts`), so files (g) to (l) leave the cards out. The panel's opening check counts too: a run toasts at most once for a new problem, whichever of its two checks sees it first. Before step 7, with `deck-scenarios.sh toasts` (home `t`: five apps linked) and `use t`; `break <id>` and `fix <id>` replace one app's link by a folder holding a file, and put it back. Run each harness line straight after the change before it: a background check (every 5 minutes) that lands in between takes the toast, and the run then shows `toasts: []`; run that step again from its `fix`.

- (g) `--expect expected/g.json`: all linked, no toast.
- (h) `break ppsspp.savedata`, `--expect expected/h.json`: one `toast: 1 app needs attention.`; again with `expected/h-again.json`: none.
- (i) `fix ppsspp.savedata`, `break dolphin.gc`, `--expect expected/h.json`: one toast again.
- (j) `fix dolphin.gc`, then `--mute on --expect expected/j-mute.json` (the harness clicks the toggle before Check now); `SAVETETHER_HOME=~/sttest/decky/t/home savetether notifications --json` says `"on": false`. `break duckstation.memcards`, `--expect expected/j.json`: Mute on, and only `toast muted: …`. `--mute off --expect expected/j-unmute.json`: no toast (the problem was recorded while muted).
- (j2) The owner sets Steam's Settings → Notifications → "Show Notification Toasts" to Never. `break pcsx2.memcards`, `--expect expected/j2.json --shot j2.png`: Mute off, `toast hidden by Steam: 2 apps need attention.`, and no toast on screen. Back to Anytime.

The toggle shows a click before the CLI has written the switch, and a Check now pressed meanwhile can read the old value. So `muted` is checked in the run after the one that clicks it (`j.json`, `j2.json`), whose opening check reads the switch as written.
- (k) Slow, not in every run: with the QAM closed, start `--listen 400 --expect expected/k.json`, then `break ryujinx.saves`: the background check toasts `3 apps need attention.` within 5 minutes.
- (l) With the owner's leave, as (e): rename the CLI away, run `--expect expected/e.json` (two failed checks, no toast), then `--expect expected/l.json` (the third: `toast: Health check failed.`), and rename it back.

By hand, in T9: in Desktop Mode with the desktop app in the tray, break a link and count the notifications (one expected; two means Steam's desktop client shows Decky's toasts, concern 1 of the phase 4.1 file); after a reboot, an amber setup toasts once, about a minute after Game Mode comes up.

- *Run on the Deck, 2026-11-25 (phase 4.1 T9):* (a) to (l) all matched. In Desktop Mode, Decky and the plugin do load in the desktop Steam client (`DeckyPluginLoader` and `saveTetherTimer` are in its `SharedJSContext`, on the same port 8081) and log `toast: …`, but nothing shows: one notification, the desktop app's. To time it, start the app with `systemd-run --user --on-calendar=<hh:mm:ss>` just before the plugin's next tick, and give the owner that window to watch. After a reboot, the check at load once ran before Steam could show a toast (17 s after Decky loaded it, 3 s before Steam's notification store finished loading): the toast was logged and lost, and the dedupe then kept the panel quiet. The plugin now checks a minute after load. To see what the plugin logged at boot, `Runtime.enable` on a fresh connection replays the earlier console lines with their timestamps.
- The harness's `SharedJSContext` also shows Steam's own notification log lines ("NotificationStore", "SteamNotificationsStore"): useful for when Steam was ready.
- The tunnel dies with a reboot (and an earlier session's tunnel may still hold port 8081 on the PC: `netstat -ano | grep :8081`; reuse it).
- Staging on Windows: the scratch folder is case-insensitive, so the CLI binary `savetether` copied beside the plugin's `SaveTether/` folder lands inside it (and in the zip). Give the binary another name there.

**Reload checks** (T9): over SSH, `steamos-session-select plasma` switches to Desktop Mode and `steamos-session-select gamescope` back; once `ss -ltn | grep :8081` shows the debugger again, rerun (a). A reboot is the owner's: `systemctl reboot` over SSH is refused for want of a password. The tunnel dies with the reboot; open it again. On a Windows checkout made before the plugin repository's `.gitattributes`, `deck-scenarios.sh` has CRLF endings and fails with `bash\r`: check it out again, or copy it with `tr -d '\r'`.

The harness waits for the check the panel runs when it opens, presses Check now, and requires "Checking…" to show after the press: a `MutationObserver` catches it, because with the CLI missing the check ends within milliseconds and polling missed it. On Windows, end the script by closing its WebSockets and setting `process.exitCode`: `process.exit()` with a socket open trips a libuv assertion (`UV_HANDLE_CLOSING`). With a dead mount the CLI does not hang: it answers in under a second that the sync root cannot be read, so (d) never reaches the backend's 20 s timeout.

**The README's screenshot** comes from `deck-scenarios.sh showcase` (home `s`: six emulator links, one replaced), never the real setup. The QuickAccess page's viewport is 854×454 CSS pixels at a device pixel ratio of 1.5, too short for every card: before `Page.captureScreenshot`, `Emulation.setDeviceMetricsOverride` with width 854, height 780 and `deviceScaleFactor` 1.5 shows them all (`Emulation.clearDeviceMetricsOverride` after). The left nav strip stretches with it, so crop to the panel.

By hand (found in T6, 2026-11-25):

- **Installing:** `~/homebrew/plugins` is owned by root and `sudo` needs the owner's password, so an agent cannot install over SSH. Build the zip (a `SaveTether/` folder holding `dist/index.js`, `main.py`, `plugin.json`, `package.json`, `LICENSE`, `README.md`) in `~/sttest` with `python3 -m zipfile -c` (there is no `zip`), and the owner installs it from Decky's Developer menu.
- **Seeing the panel:** with Decky's "Allow Remote CEF Debugging" on, tunnel `ssh -L 8081:localhost:8081 <deck>` and use the debugger's WebSocket from Node 22+ (built-in `fetch` and `WebSocket`). In the `SharedJSContext` target, `DFL.Navigation.OpenQuickAccessMenu(999)` opens the Decky tab and `DeckyPluginLoader.deckyState.setActivePlugin("SaveTether")` opens the plugin. In the `QuickAccess` target, read `[data-st-overall]` and `[data-st-card]`, click the Check now button, and take `Page.captureScreenshot`. Stop only the tunnel's own `ssh` afterwards, and switch CEF debugging off when done: it is open to the whole network.
- **Test setups:** `~/homebrew/settings/SaveTether` is owned by `deck`. A `settings.json` there with `{"savetether_home": "<home under ~/sttest>"}` points the plugin at a throwaway home on the next check (the backend refuses anything outside `~/sttest`). Delete the file afterwards, so the plugin reads the real setup again.
- A CLI older than `status --sizes` (0.6.1 and before) answers exit 64; the backend retries without it and every card says "size unknown".

## Test flows by feature

Each flow lists what to set up, what to do, and what to check. Run the flows of every feature the change touches; the Deck flows matter most.

### CLI (any change to the core or the CLI)

- `dotnet test` on Windows and on the Mint box.
- The CI scenario against the native binary: `bash tests/e2e/scenario1.sh <path to savetether>`.
- For behaviour a test cannot reach (a real Flatpak, an SD card, a running emulator), the matching lines of plan section 8.

### Background status in the footer (from the first scan button on)

Start a scan on page 1 with the real bundle. Check: the footer's right corner says Idle, then "Scanning for saves…" with no count (no Cancel there), on every page at the Deck's window size, and nothing on the page moves when it changes; the count under the search field changes once, when the scan and the footer have both finished; the scan button reads "Scan for saves" and stands alone on the row, with no checkboxes or frame; the Scan button reads "Cancel scan" while one runs, and pressing it returns the footer to Idle and the button to "Scan for saves"; with a finished list on show, a rescan keeps that list until it ends and cancelling it leaves the list unchanged; Back, Add folder and the scan sit on one row at the Deck's window size; leave Add app and return mid-scan, the scan kept running; an Apply meanwhile is not blocked. After a very quick scan, or a quick Scan then Cancel, the button stays "Cancel scan" until the footer has shown its lines and returned to Idle (pressing it then skips the wait); it is never greyed out, and clicking repeatedly queues nothing.

Busy dialog: press Apply on a row and Apply all; the page is locked at once (no other button or page opens), a "Working" dialog with a spinner shows if it takes a moment, and every dialog that follows appears on the page you started from.

### App shell: tray, health checks, one copy at a time

Set up a throwaway device with the CLI: `init --create-root`, `map add`, a save file, `apply --copy`. Then:

- Start the app: the window shows the device and "All linked". The tray icon is registered: `dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems` lists `StatusNotifierItem-<pid>`.
- Start a second copy: it exits 0 and the first shows its window.
- Replace the link with a real folder holding a file, and check (second copy, or Check now): amber, "1 app needs attention", and a notification.
- Move the sync folder away and check: banner B3. Move it back.
- Close the window: the app stays in the tray. `kill -9` it and start again: it starts, despite the stale lock and socket.
- Settings, Desktop notifications off: close the window again from a fresh start; no notification. On again: it shows.

On Windows, check first that notifications are on at all: `HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications`, `ToastEnabled` 0 means every toast is dropped silently, and the PowerShell that sends them still exits 0 (found 2026-11-23: why none had ever been seen). The first close to the tray raises one, so it is the quickest check. Who sent it: the installed copy, started from its Start menu entry's program, sends as that entry and shows SaveTether's name and icon; any other copy (a build, an unpacked package) sends as `SaveTether`, registered under `HKCU\Software\Classes\AppUserModelId`, and shows the name without an icon. Windows 11 ignored an `IconUri` there (PNG or ICO, with or without `IconBackgroundColor`).

### Notifications switch and `operation_running` (CLI, and the app honouring the switch)

Set up a throwaway device with the CLI (`init --create-root`), a small mapping applied with `--copy`, and a mapping whose source holds about 30,000 small files (30 folders of 1,000), not yet applied. Then:

- `notifications`, then `notifications off`, `notifications on` and `notifications mute`: on, off (`settings.json` in the local folder has `"notifications": false`), on, and exit 64.
- `status --json`, idle: `operation_running` false and no "Another SaveTether process" line.
- Start `apply <big> --copy` in the background (about 8 s on the Deck) and poll `status --json` from the same shell while it runs: true on every poll once the lock is taken, the text line shows, `notifications off` still works, and a second `revert` is refused naming `cli apply`. After it: false, and no `operation-holder.json` left in the local folder.
- The app: replace the small mapping's link with a real folder holding a file. Write the bin folder's path into `install-declined.txt` in the local folder, or the unpacked copy stops at the install offer and never checks. Watch `dbus-monitor --session "type='method_call',interface='org.freedesktop.Notifications',member='Notify'"` while it starts as a user unit (see "Running the app"): with `notifications off`, no `Notify`; with `on`, one, "<app> <role>: not linked.". While it idles, `status` says `operation_running` false.

`--json` is indented: grep for `'"operation_running": *[a-z]*'`, with the space. An app older than this change (0.7.0-beta1 and before) leaves `operation-holder.json` behind after every operation. A new CLI then reports `operation_running` true for as long as that old app runs, if it was the last to take the lock.

- *Run on the Deck, 2026-11-25 (branch `feature/decky-notification-support`, 3ff410e, Native AOT built on the Mint box, where `dotnet test` passed 1025):* all of the above as described; 108 of 109 polls during the apply said true (the first came before the lock was taken). Not run: the app finishing an operation of its own and then idling with no holder file (it needs the app driven to Apply; covered by `OperationLockTests`), and a holder whose start time cannot be read.

### Backup settings (the app and the CLI)

A throwaway device with one linked mapping and one whose folder and shared folder both hold saves.

- `backup auto off` without `--confirm-unsafe`: exit 64, nothing written. With it: off; `backup keep 21`: exit 64; `backup keep 1`: 1. `settings.json` in the local folder holds both; an old `notifications.txt` is gone after the first change and its value is in `settings.json`.
- The app, Settings, Backups: the switch matches the CLI. Turn it on, then off: a red dialog asks for UNSAFE; Cancel leaves it on, nothing written. Typed: off, and the red line under the switch appears. Open any action's confirmation (Revert, Replace, Delete): "Unsafe mode enabled" in red at the top; a result dialog (one button) has none. Turn it on: the line and the red header are gone.
- Unsafe mode, Replace the shared copy on the second mapping: the steps have no backup, the notes end with the unsafe-mode sentence, and Backups lists nothing new.
- Max backups 1, Back up now three times on the linked row: Backups lists the first backup and the newest only. 0, twice more: all kept. Typing 25 or clearing the box puts back the saved value.
- *Run on Mint, 2026-11-26 (uncommitted work applied on `development` at `52e6ce7`, Native AOT, `dotnet test` 1050 passed on Linux):* CLI: an old `notifications.txt` saying off was read, then moved into `settings.json` on the first change; usage errors as above; unsafe-mode replace with no new backup, "No backup is taken." in its notes and no "Back up" step; `map delete` with no backup and the shared folder gone; keep 1 over three revert/apply cycles left the first and the newest. App: Cancel leaves the switch on with nothing written, UNSAFE turns it off with the red line; Revert's confirmation shows "Unsafe mode enabled" and the unsafe note, and has "A backup is taken first." again once the switch is back on; the arrows set the number; Back up now three times with 2 left the first and the newest 2. Found and fixed: the switch had no label under "Backups", the note under it read as if backups were on, the dialog said "Type UNSAFE to confirm." twice, and the CLI said "No backup is taken." twice for a replace. Re-run after those fixes, same day (1050 passed on Linux): the switch is labelled "Back up before actions", its note starts "On,", the dialog says the phrase once (in its red hint), and a dry-run replace in unsafe mode has one note on the replacement and the unsafe-mode note. The real local folder was unchanged both times.
- *Third run on Mint, 2026-11-26 (`feature/backup-controls` at `9a3bc92`, pushed; 1053 passed on Linux):* the CLI's `--no-copy` hints say "stay only in the backup" / "this device's stay in the backup" with backups on and "are deleted" in unsafe mode; the usage error and `--help` say "data can be lost for good"; an unsafe replace's notes are "The shared saves in the sync folder will be replaced." and "Unsafe mode: no backup is taken. This cannot be undone."; an unsafe `map delete` with a symlink left in the shared folder says "1 entry … that is not a plain file (a link, say) was left" and exits 1. App: in unsafe mode the choice dialog starts with "Unsafe mode enabled", says "This device's data is deleted." and has no "backed up first" line; with backups on it says "kept only in the backup" and "This device's data is backed up first, whichever you choose."; the red line reads "Unsafe mode: actions take no backup. Data can be lost for good."; the dialog reads "Actions will take no backup. Data they replace or delete cannot be recovered." and Escape leaves the switch on. The real local folder was unchanged. Driving it: the More menu is an unnamed 176x165 popup in `xwininfo -root -tree`, captured with `import -window <its id>`; its items are about 30 pixels apart. Settings scrolls by clicking the scrollbar's track.

### First-run setup

- **Fresh, throwaway:** an empty local folder and an empty sync folder. Type the sync folder, Next: "not set up for SaveTether yet". "This is my first device": the dialog opens with Cancel focused (outlined); Escape closes it and nothing is written. Confirm it, Return on step 2 (keeps the suggested name), Return on step 3 (Set up): "All linked", "No apps are set up yet", and `bootstrap.json` written.
- **Take-over, on the real Deck setup** (only with the owner's leave; see "Testing against a real setup"): set `bootstrap.json` aside, run setup with the real sync folder, take over the device's name. Step 2 must list every device, including the Windows PC, whose file holds Windows paths. Afterwards every mapping is Linked and `status` is unchanged.

### Apply, Revert, Recover

Throwaway device with `map add test.saves ... --process plasmashell` (always running in Desktop Mode, so the running-app refusal appears) and a folder holding two entries:

- **Apply:** the refusal names the app and the process looked for, with Cancel focused, Check again and "<app> is closed". Then "<app> is closed" (the refusal itself carries the warning; no second dialog), and the choice dialog, which offers exactly Share or Start empty, with Continue disabled until one is picked. Share, then the plan's confirmation with both folders, the backup note and "Show steps". Apply gives a result with the backup ID. On disk: the save folder is a link and the shared folder holds the files.
- **One dialog per thing, other devices only when there are some (principle 8):** with data on both sides, choose Replace. Expect the choice, then **one** dialog titled "Replace the shared copy of <role>?" with the folders, Show steps and the phrase box; "backed up" appears once in it. With only this device in the sync folder no line names other devices; copy a second device's file into `<sync>/savetether/.savetether/devices/` and the line "Other devices that use the sync folder get this change too." appears in the choice and the dialog. The same for restoring a shared-copy backup and for Delete on a mapping no device uses.
- **Pacing:** a dialog's buttons answer only after 600 ms. Click Cancel within 0.1 s of a dialog opening and it stays; a second later it closes. `spectacle` takes about 0.7 s per capture, too slow to catch the buttons greyed, so test with the click. Capture in a loop while an apply runs: the ticks advance a step at a time, not all at once.
- **Revert:** from More on the linked row. The same refusal, then the confirmation, then the result. On disk: a real folder with the files.
- **Recover:** make a second mapping with a large file (`fallocate -l 3G`), start `savetether apply <id> --copy --assume-closed` in the background, and `kill -9` it after 2 seconds. Check now: banner B2 names the operation and the row buttons are disabled. Recover, confirm: "Recovered: the unfinished apply was undone", the journal is closed, and the files are intact.

### Apps page

Two throwaway devices sharing one throwaway sync folder: the app's device with a mapping whose folder is empty (Apply all links it), one whose folder has files (needs a choice), and one already linked; the second device with a mapping only it has.

- The page: apps grouped, rows needing attention first, each row's sentence and folder, the main button, More. "Sync folders ignored by this device (1)" collapsed, saying "1 device has a link." (hover for the name); it stays open across Check now.
- More on the linked row: exactly Open folder, Open shared folder, Back up now, Backups, Revert. Open folder opens the file manager at the folder (capture the whole screen to see it; close the window by hand).
- Apply all: the confirmation names the one row it will link and the one that needs you; the summary says "1 linked. 1 not linked, each needs a choice or a fix first: …".
- Apply all with two new games that have saves only on this device (shared location empty) and one with saves on both sides: one question for the two ("Share this data", "Start empty", "Leave them"), then a confirmation listing each with its answer; the both-sides row is left for its own Apply and named in the summary. "Leave them" links nothing from the two and the summary names them. Check the shared folder holds the saves after "Share", and not after "Start empty" (this device's saves are in the backup).
- A linked row says "Symlink" (or "Junction" on Windows) and a ready row says "Original"; a ready row's More has Open folder and Remove (its confirmation, then the row moves to "Sync folders ignored by this device" and the folder's files are still on disk); the "Sync folders ignored by this device" rows have no state column and their sentence is level with the name; the page is titled "Manage links". An imported game (not built in), added then removed so it sits in "Sync folders ignored by this device": its Add opens page 2 with the saves ticked, not the hand form.
- **Back up now:** only a linked row's More has it. From there: "Backed up" naming the backup's folder, which exists, and Backups lists a `snapshot` whose Copy of is "Shared copy". Then `chmod 000` one file in the shared folder and Back up now again: "No backup was made" with a readable sentence (the system's text is in `gui.log`), and no new folder under the backup location. The AOT builds set `UseSystemResourceKeys`, so an `IOException`'s message there is a resource key ("UnauthorizedAccess_IODenied_Path, <path>"), not a sentence: a dialog that shows `e.Message` of a system exception shows that key. On Mint, a More menu is its own X window: find it with `xwininfo -root -tree` (an unnamed window of the app's ID prefix) and capture it with `import -window`.
- *Run on Mint, 2026-11-26 (branch `feature/back-up-now`, Native AOT, `dotnet test` 1032 passed):* the first build, which also offered it on a ready row and showed the resource key on failure. Then the build above: all as described, the result naming `<backups>/test.linked/<time>`.

### Backups and restore

A throwaway device whose apply replaced a shared copy, so it has a backup of each kind: a save file in the folder, a different file put in the shared folder by hand, `map add`, then `apply <id> --replace-shared --confirm-replace`. Change the shared file afterwards, so a restore of it shows.

- Backups (the page's button, or More on the row): one group, newest first, "Shared copy" and "This device", sizes, the first one marked with `*` and the footnote.
- Restore the "This device" backup while linked: refused, "<role> is linked, so restoring would write into the shared copy. Revert first.", with Cancel and Revert. Revert: its confirmation and result; the list now has a "before reverting" backup.
- Restore it again: the confirmation names the time and reason; afterwards the file on disk is the backed-up one, and there is a "before restoring" backup.
- Restore the shared-copy backup: "Restore the backup into <folder>?" first, then the typed dialog "Restore the shared saves for <role> into the sync folder?"; afterwards the shared folder holds the backed-up file.
- Delete on the first backup (`*`): the confirmation warns that it is usually the only copy from before SaveTether, and its button reads "Delete the first backup". Cancel keeps it. Delete on a later backup: its folder under the backup location is gone, and the list is read again.
- The CLI: `backup delete <id>` with standard input not a terminal is refused, naming `--confirm-delete`. At a terminal it asks; to answer from a script over SSH, run it under `script -qc "<command>" /dev/null <<< "yes"`, which gives it a terminal. And with `set -e` in a script, an expected refusal (exit 1) ends the script: leave `set -e` out of steps that test refusals.

### Device list (CLI)

Mint and Windows, CLI only. Set up two local folders, `home-a` and `home-b`, as two devices on one scratch sync folder (a guarded `SAVETETHER_HOME` for each, `--backup-root` under the scratch folder). On a: `init --create-root`, `map add` and `apply --copy` for one mapping, then `map add` and `map remove` for a second, which leaves it with no device using it. On b: `init`, `map add` of the first mapping. From a, `device list` and `device list --json` must show 2 devices, the shared mapping linked here, the one orphan, and the backups. Compare with `find` (or `Get-ChildItem -Recurse`) of the sync folder. `backup_bytes` is the size of the saved files; `du` of the backup root is larger because it also counts each backup's manifest. Compare `savetether status` of the real setup before and after, in a fresh shell, and remove the scratch folders.

### Device remove (CLI)

Mint, the Deck and Windows, CLI only. Two homes `home-a` and `home-b` on one scratch sync folder, which must exist before `init --create-root` (that creates only `savetether/` in it). On a: two mappings applied with `--copy`, `game.saves` (also added on b with `map add`) and `solo.saves` (only a). Copy a's source folders to `before/`. Then:
1. `device remove --delete-orphan solo.saves:saves`: a dry run, lists 2 folders, the orphan and 3 files; the sync folder's hash is the same after.
2. `--delete-orphan game.saves:saves --confirm-remove`: refused (b uses it), nothing changed.
3. `--delete-orphan solo.saves:saves --confirm-remove`: a's folders are real folders and equal `before/`; `devices/a.json`, a's markers, `solo.saves`' record and its shared folder are gone; `removed/a.json` is there; b's mapping and saves are as before; a's home has no `bootstrap.json`, `state.json` or `journal.json`, and the backups are kept.
4. `status` on a says not set up; `init --device a2` works; b's `status` still works.

In PowerShell, `cli` is an alias for `Clear-Item`: do not name a helper function `Cli`. Under `$ErrorActionPreference = 'Stop'` the CLI's stderr text ends the script: use `Continue` and join the output as strings. The held `operation.lock` file stays in a's home on Windows (it is open while the command runs); it is empty and harmless.

### Device remove: another device, and the tombstone checks (CLI)

Mint (and Windows), CLI only. The same two homes as "Device remove", one sync folder, `game.saves` applied on a and added on b. Keep a copy of `devices/a.json` first. Then:
1. From b, `device remove --other a` lists 2 files and changes nothing; with `--confirm-remove`, a's device file and seed marker are gone, `removed/a.json` has `removed_by` b, and a's source folder is still a link. `--other b` (own name) and an unknown name exit 1.
2. `status` on a says b removed it. `device remove --confirm-remove` on a finishes it: the folder is real, with the saves, and `bootstrap.json` and `state.json` are gone. Do this with a's backup folder not there (a device set up with `--backup-root`): the finish must still work.
3. Put the saved `devices/a.json` back: `status` and `device list` on b flag it ("came back", "(removed)").
4. Make an unfinished removal: copy `bootstrap.json` aside, run `device remove --confirm-remove`, put the copy back. `status` says the removal did not finish; `device remove --confirm-remove` again deletes the bootstrap.

### Device remove: delete everything (CLI)

Mint and Windows, CLI only. The same two homes as "Device remove", one sync folder, `game.saves` applied on a (`--copy`) and added on b. Copy a's source folder to `before/` with `cp -rL` (a plain `cp -r` copies the link, not the files, and the later `diff -r` then compares nothing). Then:
1. `device remove --delete-everything --confirm-remove --confirm-delete` on a: exit 1, names b, nothing changed. `device remove --other b --delete-everything` exits 64.
2. `--other b --confirm-remove`. Then with `--confirm-remove` alone: exit 1. The dry run says the whole folder would go. With both flags: `savetether/` is gone (the sync folder is empty), a's source folder is real and `diff -r` against `before/` shows nothing, no `bootstrap.json`.
3. Set a up again (`init --create-root`, `map add`, `apply --copy`), copy `bootstrap.json` aside, run it, put the copy back. `status` says the removal did not finish (exit 0, as a warning); the same command again leaves no bootstrap.
4. `init --create-root` and `map add` on the same sync folder: `find` shows only the new files.

On Mint, build the CLI from the branch with `dotnet publish ... -o ~/sttest-t7/cli` and check `status` of the real setup before and after in a fresh shell. The Mint clone is on another branch: put it back after.

### Maintenance (the app)

The Deck, the app driven with `xdotool`, two homes on one scratch sync folder (a is the app's, `game.saves` applied; b joined with `game.saves`; an extra `solo.saves` applied on a and then removed, to make an orphan). Never the real setup.
1. **Overview:** Maintenance opens from the bar. Its numbers match `device list --json` on a (devices, mappings, linked here, orphans, backups and size).
2. **Rename:** type `Bad Name` and the button stays disabled with the message; type `a2` and Rename works. The window title, the tray tooltip (`gdbus` on the item's `Title`, as in "The tray icon's image": its `ToolTip` is empty) and the page say `a2`; b's `device list` shows `a2`.
3. **Remove another device:** one dialog: the warning and its typed name (the button stays disabled until it matches). b's files are gone from the sync folder.
4. **Orphans:** Delete mapping removes the record and leaves `solo/saves` in the sync folder; Delete mapping and data needs `delete everything` and removes both.
5. **Clean up** on Backups: keep first, then delete all (one dialog: the warning and `delete all backups`).
6. **Remove this device:** page 1, page 2, the typed name, the result listing the folders. OK lands on setup with no restart, and the folders are real folders.
7. **Banners:** from the CLI, remove a from b (`device remove --other a`). On a, the "removed by b" page replaces the Apps page; Set up again lands on setup. For "Removal not finished", copy `bootstrap.json` aside, run the removal, put it back.
8. **Captures:** each page and dialog at the default window size, and a text dialog with the on-screen keyboard open (Steam+X). On Mint the same screens, clicks only.
9. **Typed guards on shared saves:** apply a mapping whose folder and shared folder both hold saves and choose "Replace the shared copy with this device's data": one dialog follows, "Replace the shared copy of ...?", with the folders, Show steps and the phrase box; its button stays disabled until `replace shared data` is typed (any case). Restoring a backup of the shared copy shows one dialog, "Restore the shared copy of ...?", asking for `restore shared data` the same way. Cancel changes nothing. To see the override: after device a shares `a.sav`, delete `a.sav` from the shared folder and put `c.sav` there; on device b, replace: the "may still be arriving" refusal appears, "They were deleted on purpose" re-plans (no dialog of its own), and the typed dialog adds a sentence saying the missing files are not in the backup, and still asks for `replace shared data`. Cancel leaves the shared folder untouched. *On the Mint box, to keep clicks off the owner's screen:* start `Xephyr :5 -screen 1100x800 -ac` (it needs `DISPLAY=:0` and `XAUTHORITY` to open its window), run the app with `DISPLAY=:5`, and drive it with `xinput.py` using screen coordinates (no window manager, so the app fills the display from 0,0); capture with `import -window root`. Stop only your own processes: `pgrep -x savetether-gui` also lists the owner's running copy.

### Start with the session

Never test against the real autostart folder. On Linux, export `XDG_CONFIG_HOME` to a folder under the scratch area in the environment file, check it in the same `case` as `SAVETETHER_HOME`, and pass it to `systemd-run` with `-E XDG_CONFIG_HOME`. List `~/.config/autostart` before and after, and compare. On Windows the switch writes the real Run value: test it only with the owner's leave, and check `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` before and after.

- Settings: the switch is off. Turn it on: `savetether.desktop` appears with `Exec="<the binary>" --minimized`. `savetether status` (with the same `XDG_CONFIG_HOME`) warns about nothing.
- Make the entry stale (point `Exec` at a missing path with `sed`): `status` warns, and `--json` has `autostart_missing`. In the app, Check now: banner B5. Fix it: the entry names the binary again.
- Stale again, then restart the app: the entry is rewritten at start, and `gui.log` says so.
- Stale again, Check now, Turn it off: the entry is gone.
- Not covered by this flow: logging in for real. That needs the owner's session, so it is done by the owner with the real entry.

### Add app

A throwaway device, and a shared definition in its sync folder's `.savetether/definitions` with paths under the scratch folder (`{home}/sttest/<feature>/...`): a native install whose folder is absent, and a portable one (`detect: { file: "{install_dir}/app.cfg" }`) holding `app.cfg` and a save. Built-in definitions are detected against the real home; that only reads, but do not add them.

- Add app: page 1 starts a scan at once (the strip shows it) and lists what it finds as it finds it, newest save first, each with "Last played <date>"; then "Already added". Imported apps are marked "Unverified". Typing in Search lists at most 50 matches over every definition and says "Showing 50 of N matches" when there are more; clearing it brings the found list back. Time the scan on the PC and on the Deck and note both.
- Add app's plan page (choose an app): the title, the "found at" line, and Back beside Add stay at the top while the folders to share scroll; Add is still the default for Enter.
- Add app, an app already added: its row in "Already added" says "Added, more folders to add" or "Added" and always opens (with every folder added, its plan shows them all "Added already.", Add off, and Add another folder); on its plan the added folders are ticked and locked and Add is off until a new folder is ticked; Add then writes only the new one and the old ones stay. "Find the app again" on a row whose folder moved still adds the new place.
- Delete, on a throwaway device: apply a mapping with saves, revert it, Remove it; its row sits in "Sync folders ignored by this device" saying "No device links this role.", with Add and a red Delete. Delete: the confirmation is the title "Delete <app role>, orphaned in the shared folder?" and "Any other devices that use the sync folder will see this deletion."; confirming backs up the shared saves (Backups shows "before deleting" with the files), then the row is gone, and `savetether/<app>/<folder>`, the mapping file and its `seed` folder are gone from the sync folder. A row another device has set up ("1 device has a link.") has no Delete. On the Deck check the backup lands on the Deck, not in the sync folder.
- Many devices: set a mapping up on twelve devices of one share; its row in "Sync folders ignored by this device" says "12 devices have a link." on one line, and hovering the sentence lists the twelve names, one per line (check hover on the Deck in Desktop Mode with the mouse, and that touch cannot show it); Add is the right-most button and Delete, where there is one, is to its left; the sentences and buttons of all rows line up.
- Delete while the sync tool is still delivering (throwaway): drop a file into the shared folder after Delete is confirmed (or between the backup and the delete, with a script): "Deleted only in part", the file and the row stay, the backup is named; run Delete again once it settles and it completes.
- Delete while a sync tool is still writing (throwaway; script the write after the dialog is open): a save that arrives or changes after the backup is left and the run says "Deleted only in part" with the backup named; run Delete again once it settles and it completes. The dialog tells you to make sure writes are complete and the sync tool has finished.
- Delete with no folder beside the sync folder (a sync folder at the root of a drive): it works; there is no staging.
- Found list: leave Add app mid-scan and come back: the rows found so far are there and the scan went on. Scan for saves lists an app whose install folder is gone as "Not installed"; it is disabled while one runs. `savetether app list --found` lists the same apps as the page.
- Page 2 of an imported app starts with the unverified note; of an app with `cloud_saves`, a note naming the stores.
- Open the test app: "was not found", the native install's reason, and only the Install folder field. Give the portable folder, Continue: the plan, with the default roles ticked and sized, an off-by-default role saying why, the quirks.
- Back, everywhere in Add app: the title and the button row (Back, and Add on the plan and the hand form) stay above the scrolling area at the same height on every page; headings read "<app> Select Install", "<app> Add Folders", "<app> Add Custom Folder". For an app whose only install needs a folder (PPSSPP on Windows) with one given before: the folder page comes first and Back goes to page 1; Continue, then Back, returns to the folder page with the folder given. From a chooser, Back from a plan, folder page or refusal returns to the chooser. Opened from an Apps-page row, Back from its first page returns to the Apps page.
- Tick and untick a role: the page is made again each time. A role that is not ticked (an off-by-default one) still shows its folder: "measuring…" for about half a second, then its size; a linked folder shows the shared copy's size, and "Added already." under it. Add: "Apply it now?" with Not now focused. Apply: the ordinary Apply flow; on disk the link and the shared copy; the row Linked.
- Add the app again with one role applied: the dialog names it under "Already linked here" and Apply goes to the other role only. With every role applied: "<App> added" with OK alone, nothing to apply.
- Cemu's `win-documents` and `linux-flatpak` installs (from the Ludusavi manifest, not yet tried on a real install): on a throwaway Windows user, make `Documents\mlc01\usr\save\00050000\<title>` and add Cemu: it is found with no folder asked for, and the plan links `Documents\mlc01\usr\save`. With `win-portable` also set, the choose-an-install page lists both. On the Deck, a Flatpak Cemu's `~/.var/app/info.cemu.Cemu/data/Cemu/mlc01` is found the same way; do not add it on the real Deck.
- Rename the install folder, Check now: Folder gone with Find the app again. It opens page 2 with "may have moved" and the old folder prefilled. Give the new one: "already a link". Add, Not now: the row is Linked at the new folder.
- Choose an install (Cemu, which lists `win-portable`, `win-documents` and on Linux `linux-flatpak`, `linux-native`): Add app, Cemu: with `Documents\mlc01\usr\save` made, "Choose an install of Cemu." lists `win-documents` with its path and `win-portable` as `<install folder>/mlc01/usr/save`. Use win-documents: the plan, no folder field; Back returns to the list. Use win-portable: "Cemu", the folder page, with "Select the folder containing \"mlc01/usr/save\"." and the stored folder filled in; give a folder holding `mlc01\usr\save` and Continue: the plan lists that folder. Add it, Remove it on the Apps page, add again: the list is shown again though `win-portable` was used last. Check the page for an imported game found in one place: no list. Give a folder that has `mlc01` but no `usr` (even on a device whose shared folder already has Cemu saves): the plan warns that the `usr` folder does not exist and Apply will refuse; with `usr` made and no `save`, there is no warning.

### A verified definition

Build the CLI on Mint from the branch, copy it to the device, and with a throwaway `SAVETETHER_HOME` (`init --sync-root <scratch>/sync --create-root`; the sync folder must exist first) run `app add <id> --install <install> --dry-run` for each install the device has. It resolves the folders and sizes them without changing anything. Save `savetether status` before and compare after.

- `{emudeck}` reads the Emulation folder from `~/.config/EmuDeck/settings.sh`: on the Deck it is on the SD card (`/run/media/deck/<uuid>/Emulation`), not `~/Emulation`. A scratch `HOME` hides that file, so EmuDeck installs are tried with the real `HOME` (the dry run only reads).
- A role with a new mapping ID whose target another mapping already uses is refused ("already uses the same target"), by `--dry-run` too, so the dry run finds such clashes.
- Read the emulator's own settings first: on the Deck several emulators point their folders at `~/Sync/...` by hand, so the default folders there hold nothing or old files.
- EmuDeck's `Emulation/saves/<emulator>/saves` is sometimes only a symlink into `Emulation/storage/<emulator>` (Azahar, xemu). `ls -la` it before writing an install: the definition names the real folder, not the link.
- EmuDeck's scripts in `~/.config/EmuDeck/backend/functions/EmuScripts/` say where each emulator's folders are. `linkToSaveFolder` leaves the data in the emulator's own folder and links `Emulation/saves` to it (Dolphin, PPSSPP, RetroArch, Ryujinx, shadPS4), so the native or Flatpak install is the one to use; `moveSaveFolder`, or a setting pointed at `Emulation/saves` or `Emulation/storage`, moves the data (PCSX2, MAME, ScummVM, RPCS3, Vita3K, Eden, Citron, yuzu), so the definition needs an `emudeck` install. Where both are found, `app add` asks which to use. Also look for settings pointed at `Emulation/bios`: that folder is shared by all EmuDeck's emulators, so an `emudeck` install leaves that role out (RetroArch's `system`). Check what the emulator really uses in its own config file (`retroarch.cfg`'s `*_directory` keys): a folder set there by hand is not the one the definition links. Also check that EmuDeck's change took: its Cemu script edits `settings.xml` with a bundled `xmlstarlet`, which fails on SteamOS 3.9.5 (`libxml2.so.2` missing) without stopping the reset; `~/.config/EmuDeck/logs/emudeckApp.log` shows such errors.
- Before giving a role an ID, read the shared `.savetether/mappings/` for hand mappings of that app. A role with the same ID and target takes the hand mapping over (`app add` reuses it and sets this device's folder). One with a new ID and the same target is refused ("already uses the same target"; tried on the Deck, 2026-11-22).
- A `detect` rule cannot use `{appdata}` or another Windows token on Linux, so an app under Wine with no Steam, shortcut or Lutris prefix to name (Flash Player) cannot be found by Add app there yet.
- A `detect` rule cannot look inside a Steam shortcut's prefix either. HorizonXI detects the launcher the community guide unpacks (`~/horizon-xi/lib/net45`) and finds the game through its base, a winpath with `"prefix": { "steam_shortcut": "HorizonXI" }`; the prefix is `compatdata/<id>`, where Steam numbers the shortcut itself.
- A change to a definition alone needs no build: copy the file into the scratch sync folder's `savetether/.savetether/definitions/` and run the installed CLI's `app add <id> --dry-run`. A shared definition is used before a built-in one. This works only while the installed CLI understands every field the definition uses.

### Add a folder by hand

Two throwaway devices sharing one throwaway sync folder, set up with the CLI: the "other" device with `map add other-game.saves --target other-game/Saves --source <its folder> --entry-depth 2`, so the device under test has a hand mapping with no definition; and on the device under test, a folder holding a file to add, for example `games/Dolphin/GC/MemoryCardA.USA.raw`.

- The way back is at the top, in the bar where the Apps page has its buttons, on every page (Add app, the hand form, Backups, Settings), and stays in view when the hand form is scrolled to its end.
- Add app: **Add folder** is the first button above the list. It opens the form with the focus in "Folder to share", and "Advanced" (entry depth) closed.
- Type the folder a character at a time (`xdotool type`): "Name for the shared folder" follows it to the folder's own name. Type an app name: "Shared as <app>/<name> (mapping <id>)" below. Choose running app: the list of running processes with the search box focused; type `plasma` and double-click `plasmashell`, which puts it in the field (it always runs in Desktop Mode, so the running-app refusal appears). Add: "<App> added. Apply it now?". Apply: the running-app refusal, then the ordinary Apply flow; on disk the link and the shared copy.
- The Apps page groups it under the app name typed ("Lantern Harbor", not `lantern-harbor`); so do the Apply dialogs and Backups. A mapping made by `map add --name` on another device shows its name in "Sync folders ignored by this device". A SaveTether before 2026-11-26 refuses the whole sync folder once a named mapping is in it (format 3). "Sync folders ignored by this device" lists the other device's mapping with Add. Add opens the form for it: "Set up on <other device>", its shared folder, "Entry depth 2". Add with the folder empty: the problem under the field, and the focus back in it. Give the folder and press Return: "Apply it now?".
- Choose the folder myself, for a definition's app that is not found: two throwaway devices on one throwaway sync folder; on the first, `map add ppsspp.savedata --target ppsspp/SAVEDATA --source <folder>` and `apply --copy`; start the app as the second, on a machine with no PPSSPP. Its row in "Sync folders ignored by this device" shows More and Add side by side, not overlapping (they overlapped in the first build). Add: "PPSSPP" with "No installations found on this device." in amber, no field. More, Choose the folder myself: "Folder for ppsspp.savedata", "Set up on <first>", shared as `ppsspp/SAVEDATA`. On Mint, where nothing can be typed, the form's Add is the CLI's `map add ppsspp.savedata --source <folder>` (no `--target`), then `apply --no-copy`: the first device's save is there.
- Linux portable installs, with real emulators (2026-11-23, Mint): download the official AppImages of DuckStation and Cemu into a scratch folder, one folder each; put `portable.txt` beside DuckStation's and make a `portable` folder beside Cemu's. Start each once in Xephyr with `HOME` set to a scratch folder (DuckStation writes `settings.ini`, `memcards` and the rest beside the AppImage; Cemu says "Cemu is running in portable mode" and makes `portable/mlc01` once its setup assistant is closed; its process is named `cemu`, so stop it by that). With device `pc` sharing `cemu.save` and `duckstation.memcards`, on the second device: Add on the ignored Cemu row lists linux-portable as `<install folder>/portable/mlc01/usr/save`, then asks for the install folder, and the plan says "found at .../portable/mlc01"; More, Choose the folder myself on the DuckStation row takes the `memcards` folder and the dialogs name DuckStation (they named the mapping ID before); Add app, DuckStation, finds the portable copy by `settings.ini` once its folder is given, with memcards "Added already". Both emulators still start with their folders linked.
- Apply that row, then remove only its link (`rm` on the link, not the folder it points to), Check now: Folder gone with Choose the folder again. It opens the form with the old folder prefilled and focused. Select all, type a new folder, Return, Not now: the device file has the new folder and the row says Link missing.
- Custom folder (no definition file needed): Add app, xemu on Windows (no install there): "xemu" with "No installations found on this device." in amber, ending with a grey rule across the box, "Custom folder" and Browse… (UI Automation name "Browse for a custom folder of xemu"; on a plan it sits above "Worth knowing"). Browse… opens the system folder picker titled "Custom folder of xemu"; pick a folder and the form "Add another folder of xemu" opens with it and its name filled in, and no app name field; cancel and nothing changes. The Windows picker could not be driven with UI Automation (its folder box has no value pattern), so pick by hand. In the form give a folder and a name ("Hard disk"): "Shared as xemu/Hard disk (mapping xemu.hard-disk)."; Add writes `mappings/xemu.hard-disk.json` and asks "xemu added. Apply it now?". Back from the form returns to the page it came from; check it from a choose-an-install page and a plan (Cemu) too, without adding there, since built-in definitions are detected in the real home. With UI Automation, find an edit field by name and type Edit: its label has the same name and comes first.

### Install for this user (Linux)

Never against the real `~/.local`: run the app with `HOME` set to a folder under the scratch area as well as `SAVETETHER_HOME`, so the install goes to `<scratch>/home/.local/...`. Before and after, list `~/.local/bin` and `~/.local/lib` and compare, and check that `~/.local/share/applications` has no `savetether.desktop`.

To see an update, build the same commit twice with different versions, for example `dotnet publish ... -p:Version=0.2.0 -o <scratch>/v0.2.0` and again with `0.2.1`, each with the CLI beside the app (an incomplete folder is not offered anything).

Start the app as a user unit with `-p KillMode=process` (and `-E HOME=<scratch>/home -E SAVETETHER_HOME=...`): an install starts the installed copy and quits, and the default kill mode would stop the new copy with the unit.

- Start the unpacked 0.2.0: "Install SaveTether for this user?", naming the folder it runs from and the install folders. Install: the window closes and the installed copy runs (`pgrep -a -x savetether-gui` shows its path in `.local/lib/savetether`). On disk: the app and its libraries in `.local/lib/savetether` with `version.txt`; `savetether` and a `savetether-gui` link in `.local/bin`; `savetether.desktop` in `.local/share/applications`.
- With that copy running, start the unpacked 0.2.1: "Update the installed SaveTether to 0.2.1 (...)?" Update: the running copy quits, the files are replaced with nothing left over (`ls -a`), `version.txt` says 0.2.1, the installed `savetether --version` says 0.2.1, and the installed copy runs again.
- Start the unpacked 0.2.1 again: no offer; the running copy's window comes forward.
- Remove the install, start the unpacked 0.2.0, Not now: the app starts as usual, and `install-declined.txt` in the local folder names the folder. Quit and start it again: no offer.
- Not now, then Settings > Install: after Not now, open Settings in the unpacked copy: an Install section shows the offer; its button installs, starts the installed copy and quits the unpacked one. In the installed copy the section is absent and Uninstall is there instead.
- Uninstall from Settings: Settings appears only on a set-up device, so set up the throwaway device first (the installed `savetether init ... --create-root`, with the same `HOME` and `SAVETETHER_HOME`, then Check now). Turn on Start with the session, then Uninstall SaveTether: the confirmation says what goes and what stays; afterwards the app has quit, `.local/lib/savetether`, both entries in `.local/bin`, the menu entry and `.config/autostart/savetether.desktop` are gone, and the local folder and sync folder are as they were.
- The app menu entry as KDE shows it needs the real `~/.local` (the menu reads only the real `~/.local/share/applications`), so only with the owner's leave: copy `~/.local/bin/savetether` aside, install with the real `HOME` and a scratch `SAVETETHER_HOME`, then `qdbus6 org.kde.krunner /App org.kde.krunner.App.query SaveTether` opens KRunner with the entry under Applications; capture the whole screen and crop. `qdbus6 org.kde.krunner /App org.kde.krunner.App.toggleDisplay` closes it. Uninstall with the installed `savetether uninstall --confirm-uninstall`, which asks the running installed copy to quit (only the copy for its `SAVETETHER_HOME`: run it with the same one). Put the CLI back and compare the listings.
- Uninstall from the CLI with the installed copy running (same `HOME` and `SAVETETHER_HOME`): the copy quits (`pgrep -a -x savetether-gui` shows nothing) and nothing is named as still running.
- *Run on Mint, 2026-11-21 (branch `settings-install-option`, version 0.3.1, scratch `HOME`):* the offer shows with the scratch folders; Not now writes `install-declined.txt`; after setting the device up, Settings in the unpacked copy shows Install; its button asks, then installs, and the installed copy runs (`shutdown requested: the installed copy was started`, `exited (0) 6 ms after ...` in `gui.log`); Settings in the installed copy shows Uninstall and no Install; Ctrl+Q logs `shutdown requested: quit` and `exited (0) 2 ms after ...`; the CLI `uninstall` leaves the scratch `.local` empty; the real `~/.local` (an installed 0.3.0-rc.2) was the same before and after. *Not run:* the message when the installed copy cannot be started (needs an installed binary that will not start), and the end of the session on X11.
- Uninstall from the CLI: install again, then the installed `savetether uninstall` with `< /dev/null` refuses, naming `--confirm-uninstall`; with the flag it lists what it removed; again, it says nothing is installed.

### Phase 3 exit test, and the sync folder going away

Each machine with a throwaway local folder, sync folder and save folder; every step in the app, none with the CLI. Wait for the app's own check (every five minutes) rather than pressing Check now: the test is that the tray changes by itself.

- **Windows:** put the sync folder on a drive of its own with `subst T: <scratch folder>`; `subst T: /D` takes it away and `subst T: <folder>` brings it back. The app's controls are reachable with UI Automation from PowerShell (`System.Windows.Automation`): fields are `Edit` elements named after their labels (a `Text` element has the same name, so pick the `Edit`), buttons are invoked by name, and a dialog's buttons are in the same window as the page, after it ("This is my first device" appears twice: the dialog's is the last). The tray icon's tooltip, "SaveTether (<device>): <state>", is the name of its button in the hidden icons flyout. Remove the junction with `cmd /c rmdir` before deleting the scratch folder: PowerShell 5.1's `Remove-Item -Recurse` follows junctions.
- **Deck:** mount the folder that holds the sync folder with `fuse-overlayfs` as the user (no root needed), started with `systemd-run --user`: a FUSE process started from an SSH shell is stopped when the session ends, which leaves a dead mount ("Transport endpoint is not connected"). `fusermount3 -u` leaves the empty mount point an unplugged drive leaves. `udisksctl loop-setup` needs a password from an SSH session, so it cannot be used. The tray item's `ToolTip` is empty over D-Bus: compare `IconPixmap` (an `md5sum` of the reply is enough) to see it change.
- **A mount that stopped answering:** `pkill -9` the `fuse-overlayfs` process instead of unmounting. `ls` on the mount point then says "Transport endpoint is not connected". `status` exits 0 and says the sync root cannot be read, with that reason; the app shows banner B3 with it; setup's step 1 on that folder says it cannot be read. `fusermount3 -u` clears the dead mount afterwards.

### Sync folder on a network share (Windows)

Phase 3.7; what users are told is in [sharing.md](../sharing.md#a-network-share).

**The share.** The Mint box serves a Samba share `sttest` (`path = ~/sttest/share`, `read only = No`, `valid users` the Mint user, `create mask = 0644`), already set up. To make it again on a box that has none (ask the owner first: it changes the machine): `sudo apt install samba`, add that section to `/etc/samba/smb.conf`, `sudo smbpasswd -a <user>`, `sudo systemctl restart smbd`, then check it with `testparm -s`. Windows keeps the share's credentials after the first `net use \\<mint>\sttest /user:<user>` or Explorer prompt. Use only folders under the share's `sttest-*` scratch names, and remove them after.

**The flow, on the PC, in PowerShell** (a scratch `SAVETETHER_HOME` and save folder under `%TEMP%`; `$share = '\\<mint>\sttest\sttest-<date>'`):

1. Reachability: `Test-Path $share` is true. `init --sync-root \\<mint>\nosuch\x` refuses with "does not answer" (`not-found`) within about 5 seconds.
2. Normal shell, Developer Mode off: `init --sync-root $share\sync --create-root` exits 1, naming Developer Mode and the administrator terminal; no local folder, no share folder and no `.link-probe-*` in `%TEMP%`. `--json`: `problem` `invalid`.
3. Developer Mode on (the owner switches it; or an elevated shell): make `$share\sync` first (`--create-root` makes only `savetether/` inside an existing sync root, and refuses with `not-found` otherwise), then the same `init` succeeds and prints the "Not recommended" note (`init.notes` in `--json`). `map add` a scratch save folder holding a file, `apply --copy`: `Get-Item` on the save folder shows `LinkType SymbolicLink` and the UNC target; the file is in `~/sttest/share/...` on Mint; a file written through the link appears there too. `status` is OK.
4. A mapped drive: `net use Z: \\<mint>\sttest`, `init` on a fresh scratch home with `Z:\sttest-<date>\sync2`: the stored sync root (in `bootstrap.json`) is the `\\<mint>\sttest\...` path. `net use Z: /delete` after.
5. The share offline (`sudo systemctl stop smbd` on Mint; it asks for the owner's password): `status` reports the mapping `Broken`, "the sync root is not available", and nothing on the PC changes. Start `smbd` again: `status` is OK with no command run. Without sudo, renaming `$share` on Mint (`mv`, and back) gives the same `Broken` state, but not a server that stops answering.
6. `revert` from a normal shell (with Developer Mode off, if the owner switches it back): a real folder with the same hashes (removing a link needs no privilege).
7. The app (the owner's run, or UI Automation on a scratch home): setup step 1 with the share, with `Z:`, with a share that does not answer, and with Developer Mode off then on, using **Check again** between. The texts are in the spec's step 1 table.

Clean up: the scratch homes, the save folder, `$share` on Mint, and `Z:`.

**Without the Mint box**
- A share needs no second machine: this PC's admin share `\\localhost\C$\<scratch path>` is a real UNC path to a local scratch folder, and a normal shell can read it. The tests use it too, and link through it only where a symlink can be made (CI runners are elevated); elsewhere they check the refusal.
- Whether this shell can make a symlink: elevation from `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')`, Developer Mode from `AllowDevelopmentWithoutDevLicense` (1) under `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock`.
- Normal shell, Developer Mode off: `init --sync-root \\localhost\C$\...\sync --create-root` exits 1 naming Developer Mode and the administrator terminal; no local folder is created and no `.link-probe-*` is left in `%TEMP%`.
- Developer Mode on (the owner switches it), or an elevated shell: the same `init`, then `map add`, `apply --copy`: the save folder is a symlink (`Get-Item` shows `LinkType SymbolicLink`) to the UNC path; `revert` from a normal shell gives a real folder.

### The README's screenshot

`docs/images/apps-page.png` is the Apps page on the Deck. To take it again, unpack a Linux package into `~/sttest/demo` and start `docs/testing/readme-demo.sh <package folder>` as a user unit, with the environment in "Running the app" (no `SAVETETHER_HOME`). It runs in a private mount namespace (`unshare`, no root) where folders under `~/sttest/demo` hide `~/Sync`, `~/.var`, `~/.local/share/savetether` and `~/.config/autostart`, so the app shows real paths while the real ones are never touched (before 2026-11-24 the autostart folder was not hidden, and the demo app pointed the real session entry at itself); it refuses to go on if a bind is missing. It links Dolphin's GC and Wii and PCSX2's memory cards, leaves PCSX2's save states ready to link, adds a Cemu mapping only device "pc" has, and starts the app. Press Not now on the install or update offer (Update would replace the real installed copy), resize the window with `xdotool windowsize <window> 1040 705` so the list fills it without a scrollbar, move the pointer away (which also wakes the display: a capture of a sleeping one is blank), and capture with `spectacle -b -n -a`. Crop to the pixels that are fully opaque: the margin `spectacle` adds is transparent. Stop the unit and remove `~/sttest/demo` afterwards; the binds end with it.

### Windows installer

Built here with `build/build-installer.ps1` from a package made by `build/package.sh` (Inno Setup 6.5.2 or newer, installed for the user). The default folder, `%LOCALAPPDATA%\Programs\SaveTether`, may already hold something (a CLI copied there by hand, say, perhaps on `Path`): never test over it. Pass `-Dir <scratch folder>` to `build/check-installer.ps1`, and `/DIR="<scratch folder>"` to a hand-run installer. The Start menu and Settings > Apps entries are real ones, so check they are gone afterwards. Before and after, note the raw user `Path` (`HKCU\Environment`, read without expanding) and its type, and the Run value `SaveTether`, and compare. A copy started with `SAVETETHER_HOME` set leaves that Run value alone (its log says "autostart entry left at"); before 2026-11-23 it rewrote the value to itself, and the uninstaller then deleted it. A copy started without it, on the real local folder, still takes the entry while "Start with the session" is on: save the value first and write it back afterwards. Installing over the Start menu entry `SaveTether.lnk` replaces it, and uninstalling removes it: copy it aside first. Notifications from the installed copy: close its window to the tray (the first close raises a toast); it should show SaveTether's name and icon.

- `build/check-installer.ps1 -Installer <setup.exe> -Version <version> -Dir <scratch>`: both rounds pass (install and uninstall; then with the folder already on `Path` by hand, which stays). It puts `Path` back exactly as it was in any case.
- Upgrade over a running app: install 0.2.0 into the scratch folder, start its `savetether-gui.exe` with `SAVETETHER_HOME` set to a scratch local folder, then run the 0.2.1 installer silently with `/CLOSEAPPLICATIONS`: the running copy is closed ("Shutting down applications using our files" in the `/LOG`), and the installed CLI reports 0.2.1. Uninstall with `unins000.exe /VERYSILENT` and check all is gone.
- The wizard on screen: start the installer with `/DIR=` only, and drive it with UI Automation from PowerShell (`System.Windows.Automation`): its window is named "Setup - SaveTether <version>"; select "I accept the agreement" (`SelectionItemPattern`) and invoke Next, Install and Finish (`InvokePattern`). Leave the `Path` task unticked. "Start SaveTether" on the last page is a list item without a toggle pattern: focus it and send a space to untick it, or the app starts against the real local folder. Capture each page with `CopyFromScreen` over the window's rectangle, after `SetProcessDPIAware`.
- The desktop shortcut (an unticked task, `/TASKS=desktopicon` when silent): it is created and removed with the install; `check-installer.ps1` checks both.
- Setup's children and junctions: a program Setup starts inherits Setup's mitigations (Inno Setup 6.5 turns RedirectionGuard on unless `RedirectionGuard=no`), which made the app's first run after an install refuse every junction. To check it, build the installer from a copy of `savetether.iss` with a `[Run]` entry that starts a `.cmd` doing `init`, `map add` and `apply --no-copy` with the installed `savetether.exe` in a throwaway `SAVETETHER_HOME` (`{cmd}`, `runhidden`), install it with `/VERYSILENT /DIR=...`, and read the output: with the directive the apply succeeds, without it it fails with "untrusted mount point" (found and fixed 2026-11-21).
- The Start menu entry: with `SAVETETHER_HOME` set to a scratch folder in the shell, `Start-Process` on `%APPDATA%\Microsoft\Windows\Start Menu\Programs\SaveTether.lnk`; the shortcut starts the installed copy with that environment. The window footer names the local folder: check it is the scratch one. The taskbar and tray buttons are found by UI Automation under `Shell_TrayWnd` ("SaveTether: <device> - 1 running window", "Show Hidden Icons"); invoking Show Hidden Icons opens the flyout (`TopLevelWindowForOverflowXamlIsland`), where the tray button is named after the tooltip.
