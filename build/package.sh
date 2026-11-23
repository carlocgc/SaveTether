#!/usr/bin/env bash
# Builds one platform's release package: the CLI and the desktop app with its native libraries,
# README.md and LICENSE (and on Linux the app's icon, savetether.png), in a folder named after the package. Debug symbols are left out.
#
#   build/package.sh <rid> <package-name> <cli-publish-dir> <gui-publish-dir> <out-dir>
#
# Windows (rid win-*) gets <out-dir>/<package-name>.zip; Linux gets a .tar.gz with the
# programs executable. Used by the CI and release workflows; check the result with
# build/check-package.sh. docs/releasing.md says what a package holds.
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "usage: $0 <rid> <package-name> <cli-publish-dir> <gui-publish-dir> <out-dir>" >&2
  exit 64
fi

rid=$1 name=$2 cli=$3 gui=$4 out=$5
root=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$out"
out=$(cd "$out" && pwd)
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/$name"

# Everything a publish folder holds, but its debug symbols. Each publish folder is flat.
copy_binaries() {
  local from=$1 file
  for file in "$from"/*; do
    case "$file" in
      *.pdb | *.dbg) ;;
      *)
        if [[ ! -f "$file" ]]; then
          echo "unexpected entry in $from: $file" >&2
          exit 1
        fi
        cp "$file" "$stage/$name/"
        ;;
    esac
  done
}

copy_binaries "$cli"
copy_binaries "$gui"
cp "$root/README.md" "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$stage/$name/"

if [[ $rid == win-* ]]; then
  archive="$out/$name.zip"
  rm -f "$archive"
  if command -v 7z > /dev/null; then
    (cd "$stage" && 7z a -tzip "$archive" "$name" > /dev/null)
  else
    # Windows 10 and later ship bsdtar as tar.exe, which writes zip files; Git Bash's own tar does not.
    (cd "$stage" && "${WINDIR:-C:/Windows}/System32/tar.exe" -a -cf "$(cygpath -w "$archive" 2> /dev/null || echo "$archive")" "$name")
  fi
else
  # The app's icon, for the menu entry its install writes (Windows has it inside the program).
  cp "$root/src/SaveTether.Gui/Assets/savetether.png" "$stage/$name/"
  chmod +x "$stage/$name/savetether" "$stage/$name/savetether-gui"
  archive="$out/$name.tar.gz"
  tar -C "$stage" -czf "$archive" "$name"
fi

echo "$archive"
