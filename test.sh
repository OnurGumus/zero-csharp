#!/usr/bin/env bash
# Builds every sample and checks that it still behaves: each must exit with 0
# (the test samples exit with their number of failed checks), and those whose
# output is the same on every run must print exactly what tests/expected holds.
#
# A sample with a tests/input/<Sample>.txt reads it as standard input; the
# others get an empty one.
#
# Guards is also run once per line of tests/expected/Guards.fail, with the
# line's first field as its argument. Each of those runs makes a mistake that
# lib/ must end the program for: the exit code must not be 0, and the output
# must hold every other field of the line (fields are separated by "|").
#
# Usage:  ./test.sh            native, for this machine (or BFLAT_ARCH)
#         ./test.sh --wasm     WebAssembly (needs ./setup.sh --wasm)
#         ./test.sh --dos      DOS, on an emulated 386 with 4 MB (needs ./setup.sh --dos)
#         ./test.sh --c64      Commodore 64, in VICE (needs ./setup.sh --c64): the
#                              samples that fit in it, which the Small ones are made to
#         ./test.sh --pico2    Raspberry Pi Pico 2 (Cortex-M33), in QEMU (needs ./setup.sh --arm)
#         ./test.sh --r4       Arduino Uno R4 (Cortex-M4, 32 KB of RAM), in QEMU: the
#                              samples that fit in it
#         ./test.sh --win      Windows x64, under Wine in a container
#         ./test.sh --mac      macOS, natively (on a Mac with Apple Silicon)
#         ./test.sh --uefi     UEFI, in QEMU on OVMF, for BFLAT_ARCH (x64 or arm64)
#         ./test.sh --all      native, the other architecture (macOS only, run
#                              under emulation), Windows, macOS (on Apple
#                              Silicon), and WebAssembly, DOS, the C64 and
#                              the microcontrollers if set up
#         ./test.sh --update   native, and rewrite tests/expected from the output
# Env:    BFLAT_DIR, BFLAT_FORK, BFLAT_ARCH, WASM_TOOLS (see env.sh)
set -euo pipefail
cd "$(dirname "$0")"
source ./env.sh

# The Small samples are tests sized for a machine with little memory: what the
# larger ones check, in pieces that each fit in a Commodore 64.
# The lists have one sample per line, so pull requests that add different
# samples change different lines and merge without conflicts.
SMALL_SAMPLES=(
    SmallStrings
    SmallBuilders
    SmallIntegers
    SmallDoubles
    SmallObjects
    SmallArena
    SmallLists
    SmallDicts
    SmallStrDicts
    SmallText
    SmallFloats
    SmallUnicode
    SmallEncoding
    SmallFormats
    SmallDoubleFormats
    SmallDecimalFormats
    SmallKeys
)
SAMPLES=(
    Tiny
    Minimal
    Arena
    Collections
    VirtualTest
    Casts
    Core
    Bits
    Files
    Dice
    Pipe
    Env
    Simd
    Kernels
    Strings
    QuizStep1
    QuizStep2
    QuizStep3
    Quiz
    Guards
    Queries
    Threads
    Fibers
    Records
    Shapes
    Refs
    Patterns
    Scratch
    Links
    Llama
    Unicode
    Numerics
    Formats
    Keys
    Tensors
    TensorsAvx
    "${SMALL_SAMPLES[@]}"
)

# Samples WebAssembly can't run (as in build.sh): Fibers switches stacks.
NOT_WASM=" Fibers "
# Simd and Kernels print timings, so only their exit code is checked.
TIMED=" Simd Kernels Fibers "
# Arena and Collections print byte counts that depend on the pointer size, so
# on WebAssembly, DOS and microcontrollers (32-bit) only their exit code is checked.
POINTER_SIZED=" Arena Collections "
# Simd and Kernels aren't built for DOS: they benchmark SIMD instructions,
# which a 386 doesn't have.
# Vga is a game for DOS and UEFI; "Vga test" plays by itself and reports.
DOS_SAMPLES=(
    Tiny
    Minimal
    Arena
    Collections
    VirtualTest
    Casts
    Core
    Bits
    Files
    Dice
    Pipe
    Env
    Strings
    QuizStep1
    QuizStep2
    QuizStep3
    Quiz
    Guards
    Queries
    Threads
    Records
    Shapes
    Refs
    Patterns
    Scratch
    Links
    Llama
    Unicode
    Numerics
    Formats
    Keys
    Tensors
    "${SMALL_SAMPLES[@]}"
    Vga
)
# A C64 has 50 KB for a program and its memory: these fit. C64Demo and Paddles
# are for the C64 only; they check themselves, at the key tests/input types.
# (QuizStep3 fits too, but its input has bytes no C64 keyboard can type.)
C64_SAMPLES=(
    Tiny
    Minimal
    VirtualTest
    QuizStep1
    QuizStep2
    Refs
    "${SMALL_SAMPLES[@]}"
    C64Demo
    Paddles
)
# Microcontrollers: no SIMD to benchmark, no PC screen for Vga. Blink is
# theirs only, and checks itself. The Uno R4
# has 32 KB of RAM for a program's data, stacks and heap: these fit.
ARM_SAMPLES=(
    Tiny
    Minimal
    Arena
    Collections
    VirtualTest
    Casts
    Core
    Bits
    Files
    Dice
    Pipe
    Env
    Strings
    QuizStep1
    QuizStep2
    QuizStep3
    Quiz
    Guards
    Queries
    Threads
    Records
    Shapes
    Refs
    Patterns
    Scratch
    Links
    Llama
    Unicode
    Numerics
    Formats
    Keys
    Tensors
    "${SMALL_SAMPLES[@]}"
    Blink
)
# Arena, Collections, Files, Strings, Queries, Threads, Patterns, Scratch and
# Unicode need more (Arena, for one, allocates a 16 KB array).
R4_SAMPLES=(
    Tiny
    Minimal
    VirtualTest
    Casts
    Core
    Bits
    Dice
    Pipe
    Env
    QuizStep1
    QuizStep2
    QuizStep3
    Quiz
    Guards
    Records
    Shapes
    Refs
    "${SMALL_SAMPLES[@]}"
    Blink
)

EXPECTED=tests/expected
failures=0

# $1: a label for the report; $2: "native", "wasm", "dos", ...; $3: "check" or "update"
run_all() {
    local label=$1 target=$2 mode=$3 name out status expected argument input line missing i
    local fields=()
    local run=(./run.sh)
    local samples=("${SAMPLES[@]}")
    if [ "$target" = wasm ]; then
        run+=(--wasm)
        samples=()
        for name in "${SAMPLES[@]}"; do
            [[ "$NOT_WASM" == *" $name "* ]] || samples+=("$name")
        done
    fi
    [ "$target" = win ] && run+=(--win)
    [ "$target" = mac ] && run+=(--mac)
    if [ "$target" = uefi ]; then
        run+=(--uefi)
        samples=("${SAMPLES[@]}" Vga)
    fi
    if [ "$target" = dos ]; then
        run+=(--dos)
        samples=("${DOS_SAMPLES[@]}")
    fi
    if [ "$target" = c64 ]; then
        run+=(--c64)
        samples=("${C64_SAMPLES[@]}")
    fi
    if [ "$target" = pico2 ]; then
        run+=(--pico2)
        samples=("${ARM_SAMPLES[@]}")
    fi
    if [ "$target" = r4 ]; then
        run+=(--r4)
        samples=("${R4_SAMPLES[@]}")
    fi

    echo
    echo "== $label"
    for name in "${samples[@]}"; do
        status=0
        argument=
        [ "$name" = Vga ] && argument=test
        input=tests/input/$name.txt
        [ -f "$input" ] || input=/dev/null
        # shellcheck disable=SC2086
        out=$("${run[@]}" "$name" $argument < "$input" 2>&1) || status=$?
        # The C library's puts ends lines with "\r\n" on Windows, as it should.
        [ "$target" = win ] && out=${out//$'\r'/}
        expected=$EXPECTED/$name.txt

        if [ "$status" != 0 ]; then
            echo "FAIL $name: exit code $status"
            echo "$out" | tail -5 | sed 's/^/       /'
            failures=$((failures + 1))
        elif [[ "$TIMED" == *" $name "* ]] || { { [ "$target" = wasm ] || [ "$target" = dos ] || [ "$target" = pico2 ] || [ "$target" = r4 ]; } && [[ "$POINTER_SIZED" == *" $name "* ]]; }; then
            echo "ok   $name (exit code only)"
        elif [ "$mode" = update ]; then
            mkdir -p "$EXPECTED"
            echo "$out" > "$expected"
            echo "ok   $name (expected output updated)"
        elif [ ! -f "$expected" ]; then
            echo "FAIL $name: no $expected (run ./test.sh --update)"
            failures=$((failures + 1))
        elif [ "$out" != "$(cat "$expected")" ]; then
            echo "FAIL $name: output differs from $expected"
            diff <(echo "$out") "$expected" | head -10 | sed 's/^/       /' || true
            failures=$((failures + 1))
        else
            echo "ok   $name"
        fi
    done

    # Guards and Files don't fit in a C64.
    [ "$target" = c64 ] && return

    while IFS= read -r line; do
        [ -n "$line" ] || continue
        IFS='|' read -r -a fields <<< "$line"
        name=${fields[0]}
        status=0
        out=$("${run[@]}" Guards "$name" < /dev/null 2>&1) || status=$?
        missing=
        for ((i = 1; i < ${#fields[@]}; i++)); do
            [[ "$out" == *"${fields[$i]}"* ]] || missing=${fields[$i]}
        done
        if [ "$status" = 0 ]; then
            echo "FAIL Guards $name: the program wasn't stopped"
            failures=$((failures + 1))
        elif [ -n "$missing" ]; then
            echo "FAIL Guards $name: stopped (exit code $status) without the message \"$missing\""
            echo "$out" | tail -3 | sed 's/^/       /'
            failures=$((failures + 1))
        else
            echo "ok   Guards $name (stopped, as it must be)"
        fi
    done < "$EXPECTED/Guards.fail"

    # A file name outside ASCII must reach the system as it was written: Files
    # makes one and leaves it, and it must be here under exactly that name.
    # (Not on DOS, whose names are 8.3, nor UEFI, whose files are on a disk image.)
    # (Nor the Uno R4, which Files doesn't fit in.)
    if [ "$target" != dos ] && [ "$target" != uefi ] && [ "$target" != r4 ]; then
        name="zero é☃𝄞.tmp"
        rm -f -- "$name"
        status=0
        "${run[@]}" Files --keep "$name" < /dev/null > /dev/null 2>&1 || status=$?
        if [ "$status" != 0 ]; then
            echo "FAIL Files --keep: exit code $status"
            failures=$((failures + 1))
        elif [ ! -f "$name" ]; then
            echo "FAIL Files --keep: no file named \"$name\" (the name was garbled)"
            failures=$((failures + 1))
        else
            echo "ok   Files --keep (a name outside ASCII)"
        fi
        rm -f -- "$name"
    fi

    # A fiber that overflows its stack must stop the program: the guard page
    # does it where there is one, and on UEFI, which has none, the check of
    # the stack's bottom does, with a message.
    if [[ " ${samples[*]} " == *" Fibers "* ]]; then
        status=0
        out=$("${run[@]}" Fibers overflow < /dev/null 2>&1) || status=$?
        if [ "$status" = 0 ]; then
            echo "FAIL Fibers overflow: the program wasn't stopped"
            failures=$((failures + 1))
        elif [ "$target" = uefi ] && [[ "$out" != *"Fiber 1 overflowed its stack"* ]]; then
            echo "FAIL Fibers overflow: stopped (exit code $status) without the message"
            echo "$out" | tail -3 | sed 's/^/       /'
            failures=$((failures + 1))
        else
            echo "ok   Fibers overflow (stopped, as it must be)"
        fi
    fi
}

test_native() {
    local status=0
    ./build.sh > /dev/null
    run_all "native $BFLAT_ARCH" native "${1:-check}"
    # What the compiler must refuse (tests/must-not-compile).
    ./build.sh --must-not-compile || status=$?
    failures=$((failures + status))
}

test_wasm() {
    ./build.sh --wasm > /dev/null
    run_all "WebAssembly" wasm check
}

# The GPU samples print timings, so their output isn't compared with a file:
# each checks its kernels against the CPU itself and ends with OK or FAILED.
test_gpu() {
    local name out status
    ./build.sh --gpu > /dev/null
    echo
    echo "== GPU (Metal), macOS arm64"
    for name in Gpu; do
        status=0
        out=$(./run.sh --gpu "$name" < /dev/null 2>&1) || status=$?
        if [ "$status" != 0 ] || [[ "$out" != *"OK: GPU matches CPU"* ]]; then
            echo "FAIL $name: exit code $status"
            echo "$out" | tail -8 | sed 's/^/       /'
            failures=$((failures + 1))
        else
            echo "ok   $name (its own checks)"
        fi
    done
}

# The GPU build up to AIR, with no Mac (CI runs it on Linux): every kernel
# of a GPU sample must still turn into one, so a change to zerocore, lib/ or
# the compiler that makes NativeAOT-LLVM's output something ll2air doesn't
# know fails here rather than on someone's Mac. ./test.sh --gpu goes on to
# compile the kernels with Metal and run them.
test_gpu_air() {
    local name want got
    echo
    echo "== GPU kernels up to AIR (no Metal)"
    if ! ./build.sh --gpu-air > "$TMPDIR_GPU_AIR" 2>&1; then
        echo "FAIL ./build.sh --gpu-air"
        tail -8 "$TMPDIR_GPU_AIR" | sed 's/^/       /'
        failures=$((failures + 1))
        return
    fi
    for name in Gpu; do
        want=$(grep -c '\[RuntimeExport("' "samples/$name.cs")
        got=$(grep -c '^define void @' "bin/gpu/obj/$name/kernels.ll")
        if [ "$got" != "$want" ]; then
            echo "FAIL $name: $got kernels in AIR, $want in the source"
            failures=$((failures + 1))
        else
            echo "ok   $name ($got kernels)"
        fi
    done
}

test_mac() {
    ./build.sh --mac > /dev/null
    run_all "macOS arm64" mac check
}

test_win() {
    ./build.sh --win > /dev/null
    # Build the Wine image now, if needed, so its build log doesn't end up in
    # the first program's output.
    ensure_win_image > /dev/null
    run_all "Windows x64 (Wine)" win check
}

test_uefi() {
    ./build.sh --uefi > /dev/null
    run_all "UEFI $BFLAT_ARCH (QEMU, OVMF)" uefi check
}

# build.sh --dos also checks every program for instructions newer than a 386.
test_dos() {
    ./build.sh --dos > /dev/null
    local emulator cpu=486
    emulator=$(find_dos_emulator)
    [ "$emulator" = bochs ] && cpu=i386
    run_all "DOS ($emulator, ${DOS_CPU:-$cpu}, ${DOS_MEMORY:-4} MB)" dos check
}

test_c64() {
    ./build.sh --c64 > /dev/null
    run_all "Commodore 64 (VICE)" c64 check
}

test_pico2() {
    ./build.sh --pico2 > /dev/null
    run_all "Raspberry Pi Pico 2, Cortex-M33 (QEMU mps2-an505)" pico2 check
}

test_r4() {
    ./build.sh --r4 > /dev/null
    run_all "Arduino Uno R4, Cortex-M4 with 32 KB of RAM (QEMU mps2-an386)" r4 check
}

case "${1:-}" in
    "")       test_native ;;
    --update) test_native update ;;
    --wasm)   test_wasm ;;
    --dos)    test_dos ;;
    --c64)    test_c64 ;;
    --pico2)  test_pico2 ;;
    --r4)     test_r4 ;;
    --win)    test_win ;;
    --mac)    test_mac ;;
    --gpu)    test_gpu ;;
    --gpu-air) TMPDIR_GPU_AIR=$(mktemp); test_gpu_air; rm -f "$TMPDIR_GPU_AIR" ;;
    --uefi)   test_uefi ;;
    --all)
        test_native
        if [ "$(uname -s)" = Darwin ]; then
            host=$BFLAT_ARCH
            if [ "$host" = arm64 ]; then other=x64; else other=arm64; fi
            export BFLAT_ARCH=$other
            test_native
            # Leave bin/ holding binaries for this machine again.
            export BFLAT_ARCH=$host
            ./build.sh > /dev/null
        else
            echo
            echo "(skipping the other architecture: running it needs emulation, which this script only sets up on macOS)"
        fi
        if [ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ]; then
            test_mac
            if [ -x "$WASM_ILC" ] && xcrun -f metal > /dev/null 2>&1; then
                test_gpu
            else
                echo
                echo "(skipping the GPU: run ./setup.sh --wasm and xcodebuild -downloadComponent MetalToolchain first)"
            fi
        fi
        if command -v docker >/dev/null 2>&1 && find_dos_clang > /dev/null; then
            test_win
        else
            echo
            echo "(skipping Windows: needs Docker and a clang with the x86 target)"
        fi
        if [ -x "$WASM_ILC" ] && [ -x "$WASI_SDK/bin/clang" ]; then
            test_wasm
        else
            echo
            echo "(skipping WebAssembly: run ./setup.sh --wasm first)"
        fi
        if [ -x "$WASM_ILC" ] && [ -f "$DOS_BOOT" ] && find_dos_clang > /dev/null && find_dos_emulator > /dev/null; then
            test_dos
        else
            echo
            echo "(skipping DOS: run ./setup.sh --dos first)"
        fi
        if [ -x "$WASM_ILC" ] && [ -x "$LLVM_MOS/bin/mos-c64-clang" ] && find_c64_emulator > /dev/null; then
            test_c64
        else
            echo
            echo "(skipping the Commodore 64: run ./setup.sh --c64 first)"
        fi
        if [ -x "$WASM_ILC" ] && [ -x "$ARM_TOOLCHAIN/bin/clang" ] && command -v qemu-system-arm > /dev/null; then
            test_pico2
            test_r4
        else
            echo
            echo "(skipping the microcontrollers: run ./setup.sh --arm first)"
        fi
        ;;
    *) echo "Usage: ./test.sh [--wasm | --dos | --c64 | --pico2 | --r4 | --win | --mac | --gpu | --gpu-air | --uefi | --all | --update]" >&2; exit 2 ;;
esac

echo
if [ "$failures" = 0 ]; then
    echo "All tests passed."
else
    echo "$failures test(s) FAILED."
    exit 1
fi
