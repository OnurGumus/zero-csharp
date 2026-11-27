#!/usr/bin/env bash
# Checks that a C64 program hands BASIC back in working order when it ends:
# BASIC then runs a line typed after it. Hello fits in BASIC's 38,911 bytes;
# SmallFibers doesn't, which left BASIC with its program ending past its
# memory (?OUT OF MEMORY ERROR to every line). ./test.sh --c64 runs it, after
# ./build.sh --c64. Needs VICE.
set -euo pipefail
cd "$(dirname "$0")/../.."
source ./env.sh
emulator=$(find_c64_emulator)
# The ROMs ./setup.sh --c64 fetched on Linux, as c64/run.sh gives them.
options=()
if [ -d "$C64_ROMS/C64" ]; then
    roms=$C64_ROMS
    [[ "$roms" = /* ]] || roms=$PWD/$roms
    share=$(cd "$(dirname "$(command -v "$emulator")")/../share/vice" 2>/dev/null && pwd || true)
    options+=(-directory "$roms${share:+:$share:$share/C64:$share/DRIVES}")
fi
failed=0
for name in Hello SmallFibers; do
    if out=$(python3 tests/c64-exit/basic_after.py "$emulator" "$PWD/bin/c64/$name.prg" ${options[@]+"${options[@]}"}); then
        echo "ok   back to BASIC after $name"
    else
        echo "FAIL back to BASIC after $name: $out"
        failed=1
    fi
done
exit $failed
