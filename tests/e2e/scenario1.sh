#!/usr/bin/env bash
# End-to-end check of the published native binary: two-device scenario 1 of the
# implementation plan (M6). A PC seeds a mapping with --copy; after "the sync tool"
# (cp) delivers it, a Deck with saves of its own stops at the guard, is refused
# --copy, and links with --no-copy.
#
# Usage: scenario1.sh <path to the savetether binary>
set -euo pipefail

bin="$1"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# On Windows (Git Bash) the binary needs Windows paths.
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi; }
work="$(native "$work")"

id=ppsspp.savedata
for device in pc deck; do
  mkdir -p "$work/$device/sync" "$work/$device/apps/ppsspp"
done

# Runs the binary as one device. Sets $code and $out; never aborts the script.
run() {
  local device="$1"; shift
  set +e
  out="$(SAVETETHER_HOME="$work/$device/local" "$bin" "$@" 2>&1)"
  code=$?
  set -e
  printf '[%s] savetether %s -> exit %s\n%s\n' "$device" "$*" "$code" "$out"
}

expect() {
  local want="$1"; shift
  run "$@"
  if [ "$code" -ne "$want" ]; then
    echo "FAILED: expected exit $want, got $code" >&2
    exit 1
  fi
}

contains() {
  if ! printf '%s' "$out" | grep -qF -- "$1"; then
    echo "FAILED: output does not contain: $1" >&2
    exit 1
  fi
}

# The PC: first device, its saves become the shared copy.
expect 0 pc init --sync-root "$work/pc/sync" --device pc --create-root
expect 0 pc map add "$id" --target ppsspp/SAVEDATA --source "$work/pc/apps/ppsspp/SAVEDATA"
mkdir -p "$work/pc/apps/ppsspp/SAVEDATA/GAME1"
printf 'pc-1' > "$work/pc/apps/ppsspp/SAVEDATA/GAME1/save.bin"
expect 0 pc apply "$id" --copy
[ "$(cat "$work/pc/sync/savetether/ppsspp/SAVEDATA/GAME1/save.bin")" = "pc-1" ]

# The sync tool delivers.
cp -R "$work/pc/sync/." "$work/deck/sync/"

# The Deck: has saves of its own.
expect 0 deck init --sync-root "$work/deck/sync" --device deck
expect 0 deck map add "$id" --source "$work/deck/apps/ppsspp/SAVEDATA"
mkdir -p "$work/deck/apps/ppsspp/SAVEDATA/GAME1"
printf 'deck-1' > "$work/deck/apps/ppsspp/SAVEDATA/GAME1/save.bin"

expect 1 deck apply "$id"
contains "The shared location already holds saves"
contains "--no-copy | --add-missing | --replace-shared --confirm-replace"

expect 1 deck apply "$id" --copy
contains "--copy is refused"
[ "$(cat "$work/deck/apps/ppsspp/SAVEDATA/GAME1/save.bin")" = "deck-1" ]

expect 0 deck apply "$id" --no-copy
[ "$(cat "$work/deck/apps/ppsspp/SAVEDATA/GAME1/save.bin")" = "pc-1" ]

expect 0 deck status --json
contains '"state": "ok"'
expect 0 deck backup list
contains "pre-apply"
expect 0 deck recover
contains "Nothing to recover"

echo "scenario 1 passed"
