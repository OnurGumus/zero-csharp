#!/usr/bin/env bash
# Runs bin/c64/<Sample>.prg on an emulated Commodore 64 (VICE). Called by
# ../run.sh --c64; ./setup.sh --c64 says what is needed.
#
# Without --window the machine runs as fast as the host can, and this script
# prints what the program printed and exits with the program's exit code. A
# C64 has neither a standard output nor an exit code, so both are observed
# from outside:
#   - Output: every character goes through one function in c64/runtime.c
#     (zero_out). VICE's monitor is told to log the processor's registers
#     each time that function starts, and the character is in register A.
#   - Exit code: the runtime writes it to an address VICE's "debug cartridge"
#     watches; VICE then quits with that code.
# VICE has no mode without a window, so one opens, minimized, for the run.
#
# With --window, VICE runs at a real C64's speed, with its screen, sound and
# keyboard; close the window to end.
#
# Usage: c64/run.sh [--window] <Sample> [< input]
# When standard input is a file or a pipe, its text is typed into the C64's
# keyboard for the program to read (as lines, with In.ReadLine).
# Env:   C64_EMULATOR, C64_TOOLS (see ../env.sh)
#        C64_VICE_OPTIONS  more options for VICE, such as an REU:
#                      "-reu -reusize 512 -reuimage /abs/path/image.reu"
#        C64_TIMEOUT   seconds of C64 time before a run without a window is
#                      stopped (default 600; it passes much faster than that)
set -euo pipefail
cd "$(dirname "$0")/.."
source ./env.sh

window=0
if [ "${1:-}" = --window ]; then
    window=1
    shift
fi
if [ $# -lt 1 ]; then
    echo "Usage: ./run.sh --c64 [--window] <Sample>" >&2
    exit 1
fi
name=$1
if [ $# -gt 1 ]; then
    echo "Error: a C64 program can't be given arguments." >&2
    exit 1
fi

program=bin/c64/$name.prg
if [ ! -f "$program" ]; then
    echo "Error: no $program. Run ./build.sh --c64 $name" >&2
    exit 1
fi
if ! emulator=$(find_c64_emulator); then
    echo "Error: no VICE (x64sc). macOS: brew install vice. Or set C64_EMULATOR." >&2
    exit 1
fi

# VICE types the name into the C64 to start the program, in the C64's own
# character set: keep it short and plain. It is also where the paths below
# must be absolute, since VICE changes its directory.
work=$PWD/bin/c64/run
mkdir -p "$work"
cp "$program" "$work/program.prg"

# -default: this machine's saved VICE settings don't apply.
# -autostartprgmode 1: put the program into memory directly, no disk drive.
options=(-default +saveres +confirmonexit -autostartprgmode 1 -autostart "$work/program.prg")
# shellcheck disable=SC2206 # split on purpose: a list of options
[ -n "${C64_VICE_OPTIONS:-}" ] && options+=(${C64_VICE_OPTIONS})

# The ROMs ./setup.sh --c64 fetched on Linux, before VICE's own system files.
if [ -d "$C64_ROMS/C64" ]; then
    roms=$C64_ROMS
    [[ "$roms" = /* ]] || roms=$PWD/$roms
    share=$(cd "$(dirname "$(command -v "$emulator")")/../share/vice" 2>/dev/null && pwd || true)
    options+=(-directory "$roms${share:+:$share:$share/C64:$share/DRIVES}")
fi

if [ "$window" = 1 ]; then
    # -sound: VICE (3.10 on macOS, at least) starts with its sound off.
    exec "$emulator" "${options[@]}" -sound > /dev/null 2>&1
fi

address=$("$LLVM_MOS/bin/llvm-nm" "$program.elf" | awk '$3 == "zero_out" { print substr($1, length($1) - 3) }')
if [ -z "$address" ]; then
    echo "Error: no zero_out in $program.elf; rebuild with ./build.sh --c64 $name" >&2
    exit 1
fi
echo "trace exec $address" > "$work/monitor.txt"
rm -f "$work/monitor.log"

# Text for the program to read: typed into the keyboard buffer, which VICE
# refills as the C64 empties it.
keys=()
if [ -f /dev/stdin ] || [ -p /dev/stdin ]; then
    typed=$(python3 c64/keys.py)
    [ -n "$typed" ] && keys=(-keybuf "$typed")
fi

# A PAL C64 runs 985248 processor cycles a second.
cycles=$(( ${C64_TIMEOUT:-600} * 985248 ))

code=0
"$emulator" "${options[@]}" ${keys[@]+"${keys[@]}"} -minimized -warp +sound -debugcart -jamaction 5 \
    -limitcycles "$cycles" -moncommands "$work/monitor.txt" -monlogname "$work/monitor.log" -monlog \
    > "$work/vice.log" 2>&1 < /dev/null || code=$?

# The log has, for each character printed, a line like
#   .C:950c  86 34       STX $34        - A:61 X:00 Y:00 SP:de ..-...ZC    3249844
#
# A program that stops (a NullReferenceException, Environment.FailFast)
# prints "at $0A80 $19AF ...": the calls it was in, innermost first (see
# trace in c64/runtime.c). That line becomes the methods' names, from the
# program's symbols, without the helpers that stopped it and the startup
# code that called Main.
if [ -f "$work/monitor.log" ]; then
    "$LLVM_MOS/bin/llvm-nm" -n "$program.elf" > "$work/symbols.txt"
    python3 - "$work/monitor.log" "$address" "$work/symbols.txt" <<'EOF'
import bisect
import re
import sys

line = re.compile(rb"^\.C:" + sys.argv[2].encode() + rb" .* A:([0-9A-Fa-f]{2}) ")
out = bytearray()
with open(sys.argv[1], "rb") as log:
    for text in log:
        m = line.match(text)
        if m:
            out.append(int(m.group(1), 16))

functions = []
with open(sys.argv[3]) as symbols:
    for text in symbols:
        parts = text.split()
        if len(parts) == 3 and parts[1] in "tTW":
            functions.append((int(parts[0], 16), parts[2]))
starts = [address for address, _ in functions]

def name(address):
    i = bisect.bisect_right(starts, address) - 1
    return functions[i][1] if i >= 0 else f"${address:04X}"

def methods(match):
    names = [name(int(h, 16)) for h in re.findall(rb"\$([0-9A-F]{4})", match.group(0))]
    while names and re.search(r"FailFast|ThrowHelpers|RhpThrow", names[0]):
        names.pop(0)
    shown = []
    for n in names:
        if "__Module___MainMethodWrapper" in n or "__Module___StartupCodeMain" in n:
            break
        shown.append(b"  at " + n.encode())
    return b"\n".join(shown) if shown else match.group(0)

sys.stdout.buffer.write(re.sub(rb"(?m)^at( \$[0-9A-F]{4})+$", methods, bytes(out)))
EOF
fi

if grep -qi "cycle limit" "$work/vice.log"; then
    echo "Error: stopped after ${C64_TIMEOUT:-600} seconds of C64 time (C64_TIMEOUT)." >&2
    exit 124
fi
exit "$code"
