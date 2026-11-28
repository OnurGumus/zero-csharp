#!/usr/bin/env bash
# Runs a built sample: natively on Linux, in the build container on macOS.
# With --wasm, runs bin/wasm/<Sample>.wasm with wasmtime (./setup.sh --wasm).
# With --dos, runs bin/dos/<Sample>.exe under FreeDOS in an emulator
# (./setup.sh --dos); --dos --window shows the DOS screen. See dos/run.sh.
# With --c64, runs bin/c64/<Sample>.prg on an emulated Commodore 64 (VICE);
# --c64 --window shows its screen, at its real speed. See c64/run.sh.
# With --pico2 or --r4, runs bin/<board>/<Sample>.elf in QEMU, on an emulated
# Cortex-M microcontroller. See arm/run.sh. --pico2 --board runs it on a real
# Raspberry Pi Pico 2 instead (arm/board.sh), and --pico2 --wokwi on Wokwi's
# simulated one (arm/wokwi.sh).
# With --win, runs bin/win/<Sample>.exe under Wine, in a container (see
# docker/wine). On Windows itself, run the .exe directly.
# With --mac, runs bin/mac/<Sample>, directly (it's a macOS program).
# With --ios, runs bin/ios/<Sample> in the iOS simulator (ios/run.sh).
# With --android, runs bin/android/<Sample> on an Android phone or emulator,
# through adb (android/run.sh).
# With --gpu, runs bin/gpu/<Sample>, a macOS program whose kernels run on the GPU.
# With --uefi, runs bin/uefi/<Sample>.efi in QEMU on UEFI firmware, x64 or
# arm64; --uefi --window shows the screen. See efi/run.sh.
# Usage: ./run.sh [--wasm | --dos [--window] | --c64 [--window] | --pico2 | --r4 | --win | --mac | --ios | --android | --gpu | --uefi [--window]] Arena
# Standard input reaches the program on every target (on DOS, from a file or a pipe).
# Env:   BFLAT_ARCH (see env.sh): on macOS, runs a cross-compiled binary under emulation
set -euo pipefail
cd "$(dirname "$0")"
source ./env.sh

if [ "${1:-}" = --dos ]; then
    shift
    exec dos/run.sh "$@"
fi

if [ "${1:-}" = --c64 ]; then
    shift
    exec c64/run.sh "$@"
fi

if [ "${1:-}" = --pico2 ] && [ "${2:-}" = --board ]; then
    shift 2
    exec arm/board.sh "$@"
fi

if [ "${1:-}" = --pico2 ] && [ "${2:-}" = --wokwi ]; then
    shift 2
    exec arm/wokwi.sh "$@"
fi

if [ "${1:-}" = --pico2 ] || [ "${1:-}" = --r4 ]; then
    board=${1#--}
    shift
    exec arm/run.sh "$board" "$@"
fi

if [ "${1:-}" = --uefi ]; then
    shift
    exec efi/run.sh "$@"
fi

if [ "${1:-}" = --mac ]; then
    shift
    if [ $# -lt 1 ]; then
        echo "Usage: ./run.sh --mac <Sample> [args...]" >&2
        exit 1
    fi
    name=$1
    shift
    exec "./bin/mac/$name" "$@"
fi

if [ "${1:-}" = --ios ]; then
    shift
    exec ios/run.sh "$@"
fi

if [ "${1:-}" = --android ]; then
    shift
    exec android/run.sh "$@"
fi

if [ "${1:-}" = --gpu ]; then
    shift
    if [ $# -lt 1 ]; then
        echo "Usage: ./run.sh --gpu <Sample> [args...]" >&2
        exit 1
    fi
    name=$1
    shift
    exec "./bin/gpu/$name" "$@"
fi

if [ "${1:-}" = --win ]; then
    shift
    if [ $# -lt 1 ]; then
        echo "Usage: ./run.sh --win <Sample> [args...]" >&2
        exit 1
    fi
    ensure_win_image
    name=$1
    shift
    # LANG: Wine converts arguments and file names between UTF-16 and the
    # container's bytes in its locale; without a UTF-8 one, "é" becomes "?".
    # --init: as PID 1, wine64 would ignore the signal test.sh's time limit
    # sends (docker passes it on), and a hung program would run on.
    exec docker run --rm -i --init --platform linux/amd64 -e LANG=C.UTF-8 -v "$PWD:/w" -w /w "$WIN_IMAGE" wine64 "bin/win/$name.exe" "$@"
fi

wasm=0
if [ "${1:-}" = --wasm ]; then
    wasm=1
    shift
fi
if [ $# -lt 1 ]; then
    echo "Usage: ./run.sh [--wasm | --dos [--window] | --c64 [--window] | --pico2 | --r4 | --win | --mac | --ios | --android | --gpu | --uefi [--window]] <Sample> [args...]" >&2
    exit 1
fi
name=$1
shift

if [ "$wasm" = 1 ]; then
    # --dir=.: the module may use files in the current directory, and only there.
    command=("$WASMTIME" run --dir=. "bin/wasm/$name.wasm" "$@")
else
    command=("./bin/$name" "$@")
fi

if [ "$(uname -s)" = Darwin ]; then
    # --init: without it the program is the container's process 1, which
    # ignores the SIGABRT it sends itself, so abort() falls back to a trap
    # instruction; under x64 emulation that hangs instead of ending the run.
    # A binary cross-compiled with BFLAT_ARCH runs in a plain container of that
    # architecture, emulated by Docker. (WASM modules run on the host's tools.)
    if [ "$wasm" = 0 ] && [ "$BFLAT_ARCH" != "$HOST_ARCH" ]; then
        case "$BFLAT_ARCH" in
            x64)   platform=linux/amd64 ;;
            arm64) platform=linux/arm64 ;;
        esac
        exec docker run --rm -i --init --platform "$platform" -v "$PWD:/w" -w /w ubuntu:22.04 "${command[@]}"
    fi
    ensure_image
    exec docker run --rm -i --init --platform "$DOCKER_PLATFORM" -v "$PWD:/w" -w /w "$IMAGE" "${command[@]}"
fi
exec "${command[@]}"
