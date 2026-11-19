#!/usr/bin/env bash
# Checks a release package holds exactly what a person needs, and nothing else: the CLI, the
# desktop app and its native libraries, README.md and LICENSE, in one folder named after the package.
# A missing library would only show when the app is started; a stray file (a .pdb, a test
# folder) would ship to everyone. Both fail here instead.
#
#   build/check-package.sh <rid> <archive>
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <rid> <archive>" >&2
  exit 64
fi

rid=$1 archive=$2
case "$archive" in
  *.zip) name=$(basename "$archive" .zip) ;;
  *.tar.gz) name=$(basename "$archive" .tar.gz) ;;
  *) echo "not a package: $archive" >&2; exit 1 ;;
esac

case "$rid" in
  win-*) expected=(LICENSE README.md av_libglesv2.dll libHarfBuzzSharp.dll libSkiaSharp.dll savetether-gui.exe savetether.exe) ;;
  linux-*) expected=(LICENSE README.md libHarfBuzzSharp.so libSkiaSharp.so savetether savetether-gui) ;;
  *) echo "no expected contents for $rid" >&2; exit 1 ;;
esac

if [[ $archive == *.zip ]]; then
  listing=$(unzip -Z1 "$archive")
else
  listing=$(tar -tzf "$archive")
fi

# Entries as paths, folders left out (they end in a slash), backslashes as slashes.
actual=$(printf '%s\n' "$listing" | tr '\\' '/' | sed 's|^\./||' | grep -v '/$' | LC_ALL=C sort)
wanted=$(printf "$name/%s\n" "${expected[@]}" | LC_ALL=C sort)

if [[ "$actual" != "$wanted" ]]; then
  echo "::error::$archive does not hold what a $rid package should." >&2
  diff <(echo "$wanted") <(echo "$actual") | sed 's/^</missing:/; s/^>/unexpected:/' | grep -E '^(missing|unexpected):' >&2 || true
  exit 1
fi

# On Linux both programs must be executable once unpacked.
if [[ $archive == *.tar.gz ]]; then
  for program in savetether savetether-gui; do
    mode=$(tar -tvzf "$archive" "$name/$program" | cut -c1-10)
    if [[ $mode != -rwx* ]]; then
      echo "::error::$name/$program is not executable in $archive ($mode)." >&2
      exit 1
    fi
  done
fi

echo "$archive holds the $rid package: ${expected[*]}"
