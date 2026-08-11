#!/bin/bash
# Rebuilds the prebuilt helper binary that RumpSharp.dll embeds.
#
# Only contributors run this - it needs the Xcode command line tools. Consumers of the NuGet package
# get the result, committed at src/RumpSharp/runtimes/osx/native/rumpsharp-helper.gz.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
staging="$here/.build/rumpsharp-helper.stripped"
target="$here/../../src/RumpSharp/runtimes/osx/native/rumpsharp-helper.gz"

cd "$here"

# Every byte here ships inside RumpSharp.dll, so the build is tuned for size rather than speed - the
# helper does nothing hot enough to care. Measured, universal and signed:
#
#   -O, unstripped (the obvious build)      556 KB
#   -Osize -dead_strip, unstripped          510 KB
#   -Osize -dead_strip, stripped            318 KB
#   ... then gzipped, which is what ships     88 KB
#
# -Osize costs nothing noticeable for a process that spends its life blocked on a pipe, and the
# symbol table is over a third of the binary: __LINKEDIT alone was bigger than all the code.
#
# One universal binary, so the same bundle works on Apple silicon and Intel regardless of which
# machine built it and which architecture the host process runs as.
flags=(-Xswiftc -Osize -Xlinker -dead_strip)

swift build -c release --arch arm64 --arch x86_64 "${flags[@]}"

built="$(swift build -c release --arch arm64 --arch x86_64 "${flags[@]}" --show-bin-path)/rumpsharp-helper"

cp "$built" "$staging"
chmod 755 "$staging"

# An executable needs no symbol table: nothing links against it. The .dSYM next to the build output
# keeps whatever a crash report would want, and stays out of the repository.
strip "$staging"

# Ad-hoc signature so the binary is not a completely unsigned Mach-O inside a signed bundle. It has to
# come after stripping, which would otherwise invalidate it, and before compressing, so that what
# AppBundle writes out is signed. AppBundle re-signs the whole bundle anyway; this just keeps the
# artefact loadable on its own.
codesign --force --sign - --timestamp=none "$staging"

# Committed compressed, because a Mach-O is three quarters air: an application that never needs the
# helper - one that already ships as a .app - still carries this blob around, so it should be as small
# as possible. gzip rather than the better brotli because gzip is on every machine and brotli is not,
# and -n omits the timestamp so the output is reproducible.
mkdir -p "$(dirname "$target")"
gzip -9 -n -c "$staging" > "$target"

# The committed artefact is the one thing here nobody can eyeball, so prove it round-trips.
gunzip -c "$target" | cmp - "$staging"

echo "built    $(file -b "$staging")"
echo "stripped $(stat -f%z "$staging") bytes"
echo "shipped  $(stat -f%z "$target") bytes compressed (verified to round-trip)"
echo "wrote    $target"
echo
echo "To inspect the committed artefact:"
echo "  gunzip -c '$target' | file -"
