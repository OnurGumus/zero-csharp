#!/usr/bin/env bash
# Checks the C64's integer division and multiplication (c64/runtime.c, the
# section from "integer division" to "floating point") against answers
# worked out in Python: 14,000 pairs of numbers from 8 to 64 bits, in
# llvm-mos's 6502 simulator (mos-sim), in about ten seconds. The routines
# are hand-written 6502 assembly, where a slip shows only for some
# numbers. ./test.sh --c64 runs it first. Needs ./setup.sh --c64.
set -euo pipefail
cd "$(dirname "$0")/../.."
source ./env.sh

if [ ! -x "$LLVM_MOS/bin/mos-sim" ]; then
    echo "Error: no llvm-mos in $LLVM_MOS. Run ./setup.sh --c64" >&2
    exit 1
fi
work=bin/c64/math
mkdir -p "$work"

# The section of runtime.c, on its own: the rest is the C64's.
python3 - "$work/arith.c" <<'PY'
import sys
s = open("c64/runtime.c").read()
start = s.index("// ---- integer division")
end = s.index("// ---- floating point")
types = "typedef long i32;\ntypedef unsigned long u32;\ntypedef long long i64;\ntypedef unsigned long long u64;\n"
open(sys.argv[1], "w").write(types + s[start:end])
PY

failed=0
total=0
# Twice: as usual, and with the table of quarter squares a project can ask
# for (ZeroC64FastMultiply), which multiplies differently.
for variant in "" -DZERO_FAST_MULTIPLY; do
for spec in "8 800" "16 800" "32 600" "64 300"; do
    set -- $spec
    bits=$1 size=$2 batch=0
    while python3 tests/c64-math/gen.py "$bits" "$batch" "$size" "$work/cases.h"; do
        "$LLVM_MOS/bin/mos-sim-clang" -Os $variant -I "$work" tests/c64-math/check.c "$work/arith.c" -o "$work/check"
        if ! out=$("$LLVM_MOS/bin/mos-sim" "$work/check"); then
            echo "$out" | head -20
            failed=1
        fi
        total=$((total + $(echo "$out" | awk '/^bad/ { print $4 }')))
        batch=$((batch + 1))
    done
done
done
if [ "$failed" -ne 0 ]; then
    echo "FAIL C64 division and multiplication (c64/runtime.c)"
    exit 1
fi
echo "PASS C64 division and multiplication ($total pairs, 8 to 64 bits, with and without the table)"
