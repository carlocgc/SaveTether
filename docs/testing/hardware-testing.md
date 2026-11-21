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
W=$(xdotool search --onlyvisible --name '^SaveTether$' | head -1)   # the visible window; a tray-only copy has a hidden one
xdotool windowactivate --sync $W
xdotool mousemove --window $W <x> <y> click 1                       # coordinates inside the window, below the title bar
xdotool type --delay 40 '/some/path'; xdotool key Return
```

Find coordinates from a capture of the window: subtract the capture's margin and the title bar, or use `xdotool getwindowgeometry $W`. The Mint box has no `xdotool`, but it has `libXtst`: copy `docs/testing/xinput.py` there for clicks and key chords (`xinput.py click <screen x> <screen y>`, `chord Control_L q`) in screen coordinates, which are the window's `xwininfo` "Absolute upper-left" plus the position in a capture from `import -window`. It cannot type text; for that, test on the Deck. KDE's own apps (Dolphin) are native Wayland: `xdotool` can neither see nor close their windows.

**Seeing it.**

- **Deck:** `spectacle -b -n -a -o /tmp/shot.png` captures the active window. A blank white image means the display is asleep: wake it first with `xdotool mousemove_relative 1 0`. A copy started from a scratch folder first asks "Install SaveTether for this user?": press Not now, since Install writes to the real `~/.local`. Menus and other popups are separate windows that `-a` leaves out: capture the whole screen with `-f` and crop to the app window's geometry before looking. That also keeps the rest of the owner's desktop out of view.
- **Mint:** the screen is usually locked, so a full-screen capture shows the lock screen. Capture the window itself: `import -window $(xwininfo -root -tree | grep '"SaveTether"' | awk '{print $1}' | head -1) /tmp/shot.png`.

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

## Test flows by feature

Each flow lists what to set up, what to do, and what to check. Run the flows of every feature the change touches; the Deck flows matter most.

### CLI (any change to the core or the CLI)

- `dotnet test` on Windows and on the Mint box.
- The CI scenario against the native binary: `bash tests/e2e/scenario1.sh <path to savetether>`.
- For behaviour a test cannot reach (a real Flatpak, an SD card, a running emulator), the matching lines of plan section 8.

### Background status in the footer (from the first scan button on)

Start a scan on page 1 with the real bundle. Check: the footer's right corner says Idle, then "Scanning for saves…" with no count (no Cancel there), on every page at the Deck's window size, and nothing on the page moves when it changes; the count under the search field changes once, when the scan and the footer have both finished; the scan button reads "Scan for saves" and stands alone on the row, with no checkboxes or frame; the Scan button reads "Cancel scan" while one runs, and pressing it returns the footer to Idle and the button to "Scan for saves"; with a finished list on show, a rescan keeps that list until it ends and cancelling it leaves the list unchanged; Back to apps, Add folder and the scan sit on one row at the Deck's window size; leave Add app and return mid-scan, the scan kept running; an Apply meanwhile is not blocked. After a very quick scan, or a quick Scan then Cancel, the button stays "Cancel scan" until the footer has shown its lines and returned to Idle (pressing it then skips the wait); it is never greyed out, and clicking repeatedly queues nothing.

Busy dialog: press Apply on a row and Apply all; the page is locked at once (no other button or page opens), a "Working" dialog with a spinner shows if it takes a moment, and every dialog that follows appears on the page you started from.

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

- The page: apps grouped, rows needing attention first, each row's sentence and folder, the main button, More. "Sync folders ignored by this device (1)" collapsed, saying "1 device has a link." (hover for the name); it stays open across Check now.
- More on the linked row: exactly Open folder, Open shared folder, Revert. Open folder opens the file manager at the folder (capture the whole screen to see it; close the window by hand).
- Apply all: the confirmation names the one row it will link and the one that needs you; the summary says "1 linked. 1 not linked, each needs a choice or a fix first: …".
- Apply all with two new games that have saves only on this device (shared location empty) and one with saves on both sides: one question for the two ("Share these saves", "Start empty", "Leave them"), then a confirmation listing each with its answer; the both-sides row is left for its own Apply and named in the summary. "Leave them" links nothing from the two and the summary names them. Check the shared folder holds the saves after "Share", and not after "Start empty" (this device's saves are in the backup).
- A linked row says "Symlink" (or "Junction" on Windows) and a ready row says "Original"; a ready row's More has Open folder and Remove (its confirmation, then the row moves to "Sync folders ignored by this device" and the folder's files are still on disk); the "Sync folders ignored by this device" rows have no state column and their sentence is level with the name; the page is titled "Manage links". An imported game (not built in), added then removed so it sits in "Sync folders ignored by this device": its Add opens page 2 with the saves ticked, not the hand form.

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

- Add app: page 1 starts a scan at once (the strip shows it) and lists what it finds as it finds it, newest save first, each with "Last played <date>"; then "Already added". Imported apps are marked "Unverified". Typing in Search lists at most 50 matches over every definition and says "Showing 50 of N matches" when there are more; clearing it brings the found list back. Time the scan on the PC and on the Deck and note both.
- Add app's plan page (choose an app): the title, the "found at" line, and Back beside Add stay at the top while the folders to share scroll; Add is still the default for Enter.
- Add app, an app already added: its row in "Already added" says "Added, more folders to add" and opens only when a folder is left (an app with every folder added is not clickable); on its plan the added folders are ticked and locked and Add is off until a new folder is ticked; Add then writes only the new one and the old ones stay. "Find the app again" on a row whose folder moved still adds the new place.
- Delete, on a throwaway device: apply a mapping with saves, revert it, Remove it; its row sits in "Sync folders ignored by this device" saying "No device links this role.", with Add and a red Delete. Delete: the confirmation is the title "Delete <app role>, orphaned in the shared folder?" and "This deletion will sync to all devices."; confirming backs up the shared saves (Backups shows "before deleting" with the files), then the row is gone, and `savetether/<app>/<folder>`, the mapping file and its `seed` folder are gone from the sync folder. A row another device has set up ("1 device has a link.") has no Delete. On the Deck check the backup lands on the Deck, not in the sync folder.
- Many devices: set a mapping up on twelve devices of one share; its row in "Sync folders ignored by this device" says "12 devices have a link." on one line, and hovering the sentence lists the twelve names, one per line (check hover on the Deck in Desktop Mode with the mouse, and that touch cannot show it); Add is the right-most button and Delete, where there is one, is to its left; the sentences and buttons of all rows line up.
- Delete while the sync tool is still delivering (throwaway): drop a file into the shared folder after Delete is confirmed (or between the backup and the delete, with a script): "Deleted only in part", the file and the row stay, the backup is named; run Delete again once it settles and it completes.
- Delete while a sync tool is still writing (throwaway; script the write after the dialog is open): a save that arrives or changes after the backup is left and the run says "Deleted only in part" with the backup named; run Delete again once it settles and it completes. The dialog tells you to make sure writes are complete and the sync tool has finished.
- Delete with no folder beside the sync folder (a sync folder at the root of a drive): it works; there is no staging.
- Found list: leave Add app mid-scan and come back: the rows found so far are there and the scan went on. Scan for saves lists an app whose install folder is gone as "Not installed"; it is disabled while one runs. `savetether app list --found` lists the same apps as the page.
- Page 2 of an imported app starts with the unverified note; of an app with `cloud_saves`, a note naming the stores.
- Open the test app: "was not found", the native install's reason, and only the Install folder field. Give the portable folder, Look again: the plan, with the default roles ticked and sized, an off-by-default role saying why, the quirks.
- Tick and untick a role: the page is made again each time. Add: "Apply it now?" with Not now focused. Apply: the ordinary Apply flow; on disk the link and the shared copy; the row Linked.
- Add the app again with one role applied: the dialog names it under "Already linked here" and Apply goes to the other role only. With every role applied: "<App> added" with OK alone, nothing to apply.
- Rename the install folder, Check now: Folder gone with Find the app again. It opens page 2 with "may have moved" and the old folder prefilled. Give the new one: "already a link". Add, Not now: the row is Linked at the new folder.
- Install folder on the plan page: add the portable test app, then Remove it on the Apps page and add it again: the plan opens with the stored Install folder filled in. Copy the folder, type the copy's path and press Use this folder: the plan lists the copy's save folder, and Add records it. Check the field is absent on the plan of an install that does not use `{install_dir}`.

### Add a folder by hand

Two throwaway devices sharing one throwaway sync folder, set up with the CLI: the "other" device with `map add other-game.saves --target other-game/Saves --source <its folder> --entry-depth 2`, so the device under test has a hand mapping with no definition; and on the device under test, a folder holding a file to add, for example `games/Dolphin/GC/MemoryCardA.USA.raw`.

- The way back is at the top, in the bar where the Apps page has its buttons, on every page (Add app, the hand form, Backups, Settings), and stays in view when the hand form is scrolled to its end.
- Add app: **Add folder** is the first button above the list. It opens the form with the focus in "Folder to share", and "Advanced" (entry depth) closed.
- Type the folder a character at a time (`xdotool type`): "Name for the shared folder" follows it to the folder's own name. Type an app name: "Shared as <app>/<name> (mapping <id>)" below. Choose running app: the list of running processes with the search box focused; type `plasma` and double-click `plasmashell`, which puts it in the field (it always runs in Desktop Mode, so the running-app refusal appears). Add: "<App> added. Apply it now?". Apply: the running-app refusal, then the ordinary Apply flow; on disk the link and the shared copy.
- The Apps page groups it under its ID. "Sync folders ignored by this device" lists the other device's mapping with Add. Add opens the form for it: "Set up on <other device>", its shared folder, "Entry depth 2". Add with the folder empty: the problem under the field, and the focus back in it. Give the folder and press Return: "Apply it now?".
- Apply that row, then remove only its link (`rm` on the link, not the folder it points to), Check now: Folder gone with Choose the folder again. It opens the form with the old folder prefilled and focused. Select all, type a new folder, Return, Not now: the device file has the new folder and the row says Link missing.

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

- **Windows:** put the sync folder on a drive of its own with `subst T: <scratch folder>`; `subst T: /D` takes it away and `subst T: <folder>` brings it back. The app's controls are reachable with UI Automation from PowerShell (`System.Windows.Automation`): fields are `Edit` elements named after their labels (a `Text` element has the same name, so pick the `Edit`), buttons are invoked by name, and a dialog's buttons are in the same window as the page, after it ("This is my first device" appears twice: the dialog's is the last). The tray icon's tooltip, "SaveTether: <state>", is the name of its button in the hidden icons flyout. Remove the junction with `cmd /c rmdir` before deleting the scratch folder: PowerShell 5.1's `Remove-Item -Recurse` follows junctions.
- **Deck:** mount the folder that holds the sync folder with `fuse-overlayfs` as the user (no root needed), started with `systemd-run --user`: a FUSE process started from an SSH shell is stopped when the session ends, which leaves a dead mount ("Transport endpoint is not connected"). `fusermount3 -u` leaves the empty mount point an unplugged drive leaves. `udisksctl loop-setup` needs a password from an SSH session, so it cannot be used. The tray item's `ToolTip` is empty over D-Bus: compare `IconPixmap` (an `md5sum` of the reply is enough) to see it change.
- **A mount that stopped answering:** `pkill -9` the `fuse-overlayfs` process instead of unmounting. `ls` on the mount point then says "Transport endpoint is not connected". `status` exits 0 and says the sync root cannot be read, with that reason; the app shows banner B3 with it; setup's step 1 on that folder says it cannot be read. `fusermount3 -u` clears the dead mount afterwards.

### The README's screenshot

`docs/images/apps-page.png` is the Apps page on the Deck. To take it again, unpack a Linux package into `~/sttest/demo` and run `docs/testing/readme-demo.sh` there: it sets up device "deck" with shared definitions for Dolphin, Eden and PPSSPP, links four folders, leaves Eden's ready to link, and adds a Cemu mapping only device "pc" has. Start the app as in "Running the app" with `SAVETETHER_HOME=~/sttest/demo/home` and `HOME=~/sttest/demo/userhome`, press Not now on the install offer, move the pointer off the buttons, and capture with `spectacle -b -n -a`. Crop to the window: the margin `spectacle` adds is transparent, so cut to the pixels that are fully opaque (1002 by 710 at the default window size). Remove `~/sttest/demo` afterwards.

### Windows installer

Built here with `build/build-installer.ps1` from a package made by `build/package.sh` (Inno Setup 6.5.2 or newer, installed for the user). The default folder, `%LOCALAPPDATA%\Programs\SaveTether`, may already hold something (a CLI copied there by hand, say, perhaps on `Path`): never test over it. Pass `-Dir <scratch folder>` to `build/check-installer.ps1`, and `/DIR="<scratch folder>"` to a hand-run installer. The Start menu and Settings > Apps entries are real ones, so check they are gone afterwards. Before and after, note the raw user `Path` (`HKCU\Environment`, read without expanding) and its type, and the Run value `SaveTether`, and compare.

- `build/check-installer.ps1 -Installer <setup.exe> -Version <version> -Dir <scratch>`: both rounds pass (install and uninstall; then with the folder already on `Path` by hand, which stays). It puts `Path` back exactly as it was in any case.
- Upgrade over a running app: install 0.2.0 into the scratch folder, start its `savetether-gui.exe` with `SAVETETHER_HOME` set to a scratch local folder, then run the 0.2.1 installer silently with `/CLOSEAPPLICATIONS`: the running copy is closed ("Shutting down applications using our files" in the `/LOG`), and the installed CLI reports 0.2.1. Uninstall with `unins000.exe /VERYSILENT` and check all is gone.
- The wizard on screen: start the installer with `/DIR=` only, and drive it with UI Automation from PowerShell (`System.Windows.Automation`): its window is named "Setup - SaveTether <version>"; select "I accept the agreement" (`SelectionItemPattern`) and invoke Next, Install and Finish (`InvokePattern`). Leave the `Path` task unticked. "Start SaveTether" on the last page is a list item without a toggle pattern: focus it and send a space to untick it, or the app starts against the real local folder. Capture each page with `CopyFromScreen` over the window's rectangle, after `SetProcessDPIAware`.
- The desktop shortcut (an unticked task, `/TASKS=desktopicon` when silent): it is created and removed with the install; `check-installer.ps1` checks both.
- Setup's children and junctions: a program Setup starts inherits Setup's mitigations (Inno Setup 6.5 turns RedirectionGuard on unless `RedirectionGuard=no`), which made the app's first run after an install refuse every junction. To check it, build the installer from a copy of `savetether.iss` with a `[Run]` entry that starts a `.cmd` doing `init`, `map add` and `apply --no-copy` with the installed `savetether.exe` in a throwaway `SAVETETHER_HOME` (`{cmd}`, `runhidden`), install it with `/VERYSILENT /DIR=...`, and read the output: with the directive the apply succeeds, without it it fails with "untrusted mount point" (found and fixed 2026-11-21).
- The Start menu entry: with `SAVETETHER_HOME` set to a scratch folder in the shell, `Start-Process` on `%APPDATA%\Microsoft\Windows\Start Menu\Programs\SaveTether.lnk`; the shortcut starts the installed copy with that environment. The window footer names the local folder: check it is the scratch one. The taskbar and tray buttons are found by UI Automation under `Shell_TrayWnd` ("SaveTether - 1 running window", "Show Hidden Icons"); invoking Show Hidden Icons opens the flyout (`TopLevelWindowForOverflowXamlIsland`), where the tray button is named after the tooltip.
