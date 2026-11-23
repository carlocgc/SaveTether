#!/bin/bash
# The README's Apps page demo, on the Deck. Usage: readme-demo.sh <unpacked Linux package folder>
# Runs in a private mount namespace where folders under ~/sttest/demo hide ~/Sync, ~/.var and
# ~/.local/share/savetether, so the app shows the real paths while the real ones are never touched.
# Device "deck": Dolphin (GC, Wii) and PCSX2 memory cards linked, PCSX2 save states ready to link; a Cemu
# mapping only device "pc" has. Then starts the app in the same namespace.
set -e
D=$HOME/sttest/demo
if [ -z "$DEMO_NS" ]; then
  [ -x "$1/savetether-gui" ] || { echo "usage: $0 <unpacked package folder>"; exit 1; }
  mkdir -p $D/Sync $D/var $D/local
  # Root in an outer user namespace to bind-mount, then back to this user's id in an inner one.
  exec env DEMO_NS=1 unshare -U -r -m sh -c '
    mount --bind "$1/Sync" "$HOME/Sync" && mount --bind "$1/var" "$HOME/.var" &&
    mount --bind "$1/local" "$HOME/.local/share/savetether" &&
    exec unshare -U --map-user="$2" --map-group="$3" "$4" "$5"' sh "$D" "$(id -u)" "$(id -g)" "$(realpath "$0")" "$(realpath "$1")"
fi
for p in Sync:Sync .var:var .local/share/savetether:local; do # refuse unless each bind is in place
  [ "$(stat -c %d:%i "$HOME/${p%%:*}")" = "$(stat -c %d:%i "$D/${p#*:}")" ] || { echo "not scratch: ~/${p%%:*}"; exit 1; }
done
ST=$1/savetether
V=$HOME/.var/app
map() { # mapping, source folder; linked unless a third argument is given
  mkdir -p "$2"; echo save > "$2/slot1.sav"
  $ST map add $1 --target ${1%%.*}/${1#*.} --source "$2" >/dev/null
  [ -n "$3" ] || $ST apply $1 --copy --assume-closed >/dev/null
}
$ST init --sync-root $HOME/Sync --device deck --create-root >/dev/null
map dolphin.gc $V/org.DolphinEmu.dolphin-emu/data/dolphin-emu/GC
map dolphin.wii $V/org.DolphinEmu.dolphin-emu/data/dolphin-emu/Wii
map pcsx2.memcards $V/net.pcsx2.PCSX2/config/PCSX2/memcards
map pcsx2.sstates $V/net.pcsx2.PCSX2/config/PCSX2/sstates unlinked
# Another device, with a mapping this one does not have.
mkdir -p $D/pc-local $D/pc-cemu
SAVETETHER_HOME=$D/pc-local $ST init --sync-root $HOME/Sync --device pc >/dev/null
SAVETETHER_HOME=$D/pc-local $ST map add cemu.save --target cemu/save --source $D/pc-cemu >/dev/null
$ST status
exec "$1/savetether-gui"
