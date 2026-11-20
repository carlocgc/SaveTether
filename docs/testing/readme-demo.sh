#!/bin/bash
# The README's Apps page demo, on the Deck, all under ~/sttest/demo: device "deck" with Dolphin (gc, wii) and
# PPSSPP (savedata, states) linked, Eden (saves) ready to link, and a Cemu mapping only device "pc" has.
set -e
D=$HOME/sttest/demo
export SAVETETHER_HOME=$D/home
case "$SAVETETHER_HOME" in "$HOME"/sttest/*) ;; *) echo "not scratch"; exit 1;; esac
ST=$D/savetether-0.2.0-linux-x64/savetether
mkdir -p $D/Sync $D/userhome
for f in dolphin/gc dolphin/wii eden/saves ppsspp/savedata ppsspp/states; do mkdir -p $D/apps/$f; echo save > $D/apps/$f/slot1.sav; done
$ST init --sync-root $D/Sync --device deck --create-root >/dev/null
defs=$D/Sync/savetether/.savetether/definitions; mkdir -p $defs
def() { # id name role...
  local id=$1 name=$2; shift 2; local roles=""
  for r in "$@"; do roles="$roles${roles:+,}{\"id\":\"$r\",\"class\":\"save\",\"default\":true,\"path\":\"$r\",\"target\":\"$r\"}"; done
  printf '{"schema":1,"id":"%s","name":"%s","kind":"emulator","installs":[{"id":"demo","os":"linux","detect":{"dir":"{home}/sttest/demo/apps/%s"},"base":"{home}/sttest/demo/apps/%s"}],"roles":[%s]}\n' "$id" "$name" "$id" "$id" "$roles" > $defs/$id.jsonc
}
def dolphin Dolphin gc wii; def eden Eden saves; def ppsspp PPSSPP savedata states
for m in dolphin.gc dolphin.wii eden.saves ppsspp.savedata ppsspp.states; do
  app=${m%%.*}; role=${m#*.}
  $ST map add $m --target $app/$role --source $D/apps/$app/$role >/dev/null
done
for m in dolphin.gc dolphin.wii ppsspp.savedata ppsspp.states; do $ST apply $m --copy --assume-closed >/dev/null; done
# Another device, with a mapping this one does not have.
mkdir -p $D/pc-local $D/pc-cemu
SAVETETHER_HOME=$D/pc-local $ST init --sync-root $D/Sync --device pc >/dev/null
SAVETETHER_HOME=$D/pc-local $ST map add cemu.save --target cemu/save --source $D/pc-cemu >/dev/null
$ST status
