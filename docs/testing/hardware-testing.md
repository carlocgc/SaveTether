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

2. **Throwaway sync folders too.** A test device gets its own sync folder under the scratch area, set up with `init --create-root`. Never point a test device at the real sync folder, which syncs to every device.
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
W=$(xdotool search --onlyvisible --name '^SaveTether$' | head -1)   # the visible window; a tray-only copy has a hidden one
xdotool windowactivate --sync $W
xdotool mousemove --window $W <x> <y> click 1                       # coordinates inside the window, below the title bar
xdotool type --delay 40 '/some/path'; xdotool key Return
```

Find coordinates from a capture of the window: subtract the capture's margin and the title bar, or use `xdotool getwindowgeometry $W`. The Mint box has no `xdotool`: there, check what draws, and test interaction on the Deck. KDE's own apps (Dolphin) are native Wayland: `xdotool` can neither see nor close their windows.

**Seeing it.**

- **Deck:** `spectacle -b -n -a -o /tmp/shot.png` captures the active window. A blank white image means the display is asleep: wake it first with `xdotool mousemove_relative 1 0`. Menus and other popups are separate windows that `-a` leaves out: capture the whole screen with `-f` and crop to the app window's geometry before looking. That also keeps the rest of the owner's desktop out of view.
- **Mint:** the screen is usually locked, so a full-screen capture shows the lock screen. Capture the window itself: `import -window $(xwininfo -root -tree | grep '"SaveTether"' | awk '{print $1}' | head -1) /tmp/shot.png`.

Then copy the image to the PC to look at it.

**What it said.** The app writes one line per operation, refusal and unexpected error to `gui.log` in its local folder. An unhandled crash goes to standard error: on the Deck, `journalctl --user -u savetether-gui-<feature>`.

**A page that greys out and stays so** has usually thrown inside work started with `_ = SomethingAsync()`: a task nobody awaits keeps its exception, so nothing is logged or shown. Look for a control read inside `Task.Run` (Avalonia refuses that off the UI thread): read the values first, then start the work. Found on the Deck in Add app.

**Coordinates.** Take them from a capture made after the last change of layout: a banner, a dialog or a page change moves everything below it. A click that lands beside a button does nothing and gives no error, which reads like a bug in the app.

**`xdotool` over SSH on the Deck** needs the session's `XAUTHORITY` as well as `DISPLAY`, read from Plasma's environment as in "Running the app"; without it every call fails with "Authorization required". Keep the exports in a small file that each helper sources, written with a quoted heredoc: inserting the line with `sed` mangled its `'\0'` and `'\n'`.

**Helpers.** Two small scripts in the scratch folder save typing: `click.sh x y [wait]` (activate the window, click at window coordinates, wait) and `shot.sh name` (wake the display, activate the window, `spectacle -a` to `/tmp`). With `spectacle -a` on the Deck, the window's client area starts about 65 pixels right and 81 down from the capture's corner (shadow and title bar): subtract that from a point in the capture to get the coordinates to click.

## Testing against a real setup

Some things only show with real data: the first run of setup on the Deck crashed on the Windows PC's device file, which no fixture had. With the owner's leave:

1. Back up the local `bootstrap.json` and `state.json`, the device's file in the shared folder, and the output of `savetether status`, to a folder under `~/sttest`.
2. Make the change the test needs, for example move `bootstrap.json` aside so the device counts as not set up.
3. Run the test.
4. Compare: `status`, `state.json` and the device file against the backup. Expect and explain any difference (a take-over gives the device file a new `install_id`).
5. If the test fails part way, put the backed-up files back at once and check again.

## Test flows by feature

Each flow lists what to set up, what to do, and what to check. Run the flows of every feature the change touches; the Deck flows matter most.

### CLI (any change to the core or the CLI)

- `dotnet test` on Windows and on the Mint box.
- The CI scenario against the native binary: `bash tests/e2e/scenario1.sh <path to savetether>`.
- For behaviour a test cannot reach (a real Flatpak, an SD card, a running emulator), the matching lines of plan section 8.

### App shell: tray, health checks, one copy at a time

Set up a throwaway device with the CLI: `init --create-root`, `map add`, a save file, `apply --copy`. Then:

- Start the app: the window shows the device and "All linked". The tray icon is registered: `dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems` lists `StatusNotifierItem-<pid>`.
- Start a second copy: it exits 0 and the first shows its window.
- Replace the link with a real folder holding a file, and check (second copy, or Check now): amber, "1 app needs attention", and a notification.
- Move the sync folder away and check: banner B3. Move it back.
- Close the window: the app stays in the tray. `kill -9` it and start again: it starts, despite the stale lock and socket.

### First-run setup

- **Fresh, throwaway:** an empty local folder and an empty sync folder. Type the sync folder, Next: "not set up for SaveTether yet". "This is my first device": the dialog opens with Cancel focused (outlined); Escape closes it and nothing is written. Confirm it, Return on step 2 (keeps the suggested name), Return on step 3 (Set up): "All linked", "No apps are set up yet", and `bootstrap.json` written.
- **Take-over, on the real Deck setup** (only with the owner's leave; see "Testing against a real setup"): set `bootstrap.json` aside, run setup with the real sync folder, take over the device's name. Step 2 must list every device, including the Windows PC, whose file holds Windows paths. Afterwards every mapping is Linked and `status` is unchanged.

### Apply, Revert, Recover

Throwaway device with `map add test.saves ... --process plasmashell` (always running in Desktop Mode, so the running-app refusal appears) and a folder holding two entries:

- **Apply:** the refusal names the app and the process looked for, with Cancel focused, Check again and "<app> is closed". Then "<app> is closed", its confirmation, and the choice dialog, which offers exactly Share or Start empty, with Continue disabled until one is picked. Share, then the plan's confirmation with both folders, the backup note and "Show steps". Apply gives a result with the backup ID. On disk: the save folder is a link and the shared folder holds the files.
- **Pacing:** a dialog's buttons answer only after 600 ms. Click Cancel within 0.1 s of a dialog opening and it stays; a second later it closes. `spectacle` takes about 0.7 s per capture, too slow to catch the buttons greyed, so test with the click. Capture in a loop while an apply runs: the ticks advance a step at a time, not all at once.
- **Revert:** from More on the linked row. The same refusal, then the confirmation, then the result. On disk: a real folder with the files.
- **Recover:** make a second mapping with a large file (`fallocate -l 3G`), start `savetether apply <id> --copy --assume-closed` in the background, and `kill -9` it after 2 seconds. Check now: banner B2 names the operation and the row buttons are disabled. Recover, confirm: "Recovered: the unfinished apply was undone", the journal is closed, and the files are intact.

### Apps page

Two throwaway devices sharing one throwaway sync folder: the app's device with a mapping whose folder is empty (Apply all links it), one whose folder has files (needs a choice), and one already linked; the second device with a mapping only it has.

- The page: apps grouped, rows needing attention first, each row's sentence and folder, the main button, More. "Not on this device (1)" collapsed, saying "Set up on <other device>, not here."; it stays open across Check now.
- More on the linked row: exactly Open folder, Open shared folder, Revert. Open folder opens the file manager at the folder (capture the whole screen to see it; close the window by hand).
- Apply all: the confirmation names the one row it will link and the one that needs you; the summary says "1 linked. 1 need you: …".
- Stop using here on a ready row: its confirmation, then the row moves to "Not on this device", and the folder's files are still on disk.

### Backups and restore

A throwaway device whose apply replaced a shared copy, so it has a backup of each kind: a save file in the folder, a different file put in the shared folder by hand, `map add`, then `apply <id> --replace-shared --confirm-replace`. Change the shared file afterwards, so a restore of it shows.

- Backups (the page's button, or More on the row): one group, newest first, "Shared copy" and "This device", sizes, the first one marked with `*` and the footnote.
- Restore the "This device" backup while linked: refused, "<role> is linked, so restoring would write into the shared copy. Revert first.", with Cancel and Revert. Revert: its confirmation and result; the list now has a "before reverting" backup.
- Restore it again: the confirmation names the time and reason; afterwards the file on disk is the backed-up one, and there is a "before restoring" backup.
- Restore the shared-copy backup: "Restore the shared saves for <role> on every device?" first, then the plan's confirmation; afterwards the shared folder holds the backed-up file.
- Delete on the first backup (`*`): the confirmation warns that it is usually the only copy from before SaveTether, and its button reads "Delete the first backup". Cancel keeps it. Delete on a later backup: its folder under the backup location is gone, and the list is read again.
- The CLI: `backup delete <id>` with standard input not a terminal is refused, naming `--confirm-delete`. At a terminal it asks; to answer from a script over SSH, run it under `script -qc "<command>" /dev/null <<< "yes"`, which gives it a terminal. And with `set -e` in a script, an expected refusal (exit 1) ends the script: leave `set -e` out of steps that test refusals.

### Start with the session

Never test against the real autostart folder. On Linux, export `XDG_CONFIG_HOME` to a folder under the scratch area in the environment file, check it in the same `case` as `SAVETETHER_HOME`, and pass it to `systemd-run` with `-E XDG_CONFIG_HOME`. List `~/.config/autostart` before and after, and compare. On Windows the switch writes the real Run value: test it only with the owner's leave, and check `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` before and after.

- Settings: the switch is off. Turn it on: `savetether.desktop` appears with `Exec="<the binary>" --minimized`. `savetether status` (with the same `XDG_CONFIG_HOME`) warns about nothing.
- Make the entry stale (point `Exec` at a missing path with `sed`): `status` warns, and `--json` has `autostart_missing`. In the app, Check now: banner B5. Fix it: the entry names the binary again.
- Stale again, then restart the app: the entry is rewritten at start, and `gui.log` says so.
- Stale again, Check now, Turn it off: the entry is gone.
- Not covered by this flow: logging in for real. That needs the owner's session, so it is done by the owner with the real entry.

### Add app

A throwaway device, and a shared definition in its sync folder's `.savetether/definitions` with paths under the scratch folder (`{home}/sttest/<feature>/...`): a native install whose folder is absent, and a portable one (`detect: { file: "{install_dir}/app.cfg" }`) holding `app.cfg` and a save. Built-in definitions are detected against the real home; that only reads, but do not add them.

- Add app: page 1 lists the apps and labels each; typing in Search filters them.
- Open the test app: "was not found", the native install's reason, and only the Install folder field. Give the portable folder, Look again: the plan, with the default roles ticked and sized, an off-by-default role saying why, the quirks.
- Tick and untick a role: the page is made again each time. Add: "Apply it now?" with Not now focused. Apply: the ordinary Apply flow; on disk the link and the shared copy; the row Linked.
- Rename the install folder, Check now: Folder gone with Find the app again. It opens page 2 with "may have moved" and the old folder prefilled. Give the new one: "already a link". Add, Not now: the row is Linked at the new folder.

### Add a folder by hand

Two throwaway devices sharing one throwaway sync folder, set up with the CLI: the "other" device with `map add other-game.saves --target other-game/Saves --source <its folder> --entry-depth 2`, so the device under test has a hand mapping with no definition; and on the device under test, a folder holding a file to add, for example `games/Dolphin/GC/MemoryCardA.USA.raw`.

- Add app: "Add a folder by hand" is a link below the list. It opens the form with the focus in "Folder to share", and "Advanced" (entry depth) closed.
- Type the folder a character at a time (`xdotool type`): "Name for the shared folder" follows it to the folder's own name. Type an app name: "Shared as <app>/<name> (mapping <id>)" below. Choose running app: the list of running processes with the search box focused; type `plasma` and double-click `plasmashell`, which puts it in the field (it always runs in Desktop Mode, so the running-app refusal appears). Add: "<App> added. Apply it now?". Apply: the running-app refusal, then the ordinary Apply flow; on disk the link and the shared copy.
- The Apps page groups it under its ID. "Not on this device" lists the other device's mapping with Add. Add opens the form for it: "Set up on <other device>", its shared folder, "Entry depth 2". Add with the folder empty: the problem under the field, and the focus back in it. Give the folder and press Return: "Apply it now?".
- Apply that row, then remove only its link (`rm` on the link, not the folder it points to), Check now: Folder gone with Choose the folder again. It opens the form with the old folder prefilled and focused. Select all, type a new folder, Return, Not now: the device file has the new folder and the row says Link missing.
