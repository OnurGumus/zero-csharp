#!/usr/bin/env bash
# Runs bin/uefi/<Sample>.efi in QEMU, on OVMF, the UEFI firmware. Called by
# ../run.sh --uefi. The program's architecture (x64 or arm64) comes from the
# file itself.
#
# The machine gets one disk, made here, holding the program and STARTUP.NSH,
# which the UEFI Shell runs when it starts: it runs the program, keeps its exit
# code, and turns the machine off. Without --window there is no screen: the
# program's input, output and errors are files on the disk (IN.TXT, OUT.TXT,
# ERR.TXT), which this script fills and prints, and it exits with the
# program's exit code. With --window, QEMU's window shows the screen and the
# shell stays at its prompt afterwards (type `PROGRAM` to run it again).
#
# Usage: efi/run.sh [--window] <Sample> [args...] [< input]
# When standard input is a file or a pipe, the program reads it. When it is a
# terminal, the machine's console is this terminal, firmware messages and all,
# so the program reads the keyboard; otherwise its input is empty.
# Env:   UEFI_FIRMWARE  (see ../env.sh)
#        UEFI_TIMEOUT   seconds before a run without a window is stopped (default 300)
#        UEFI_MEMORY    the machine's memory in MB (default 1024)
set -euo pipefail
cd "$(dirname "$0")/.."
source ./env.sh

window=0
if [ "${1:-}" = --window ]; then
    window=1
    shift
fi
if [ $# -lt 1 ]; then
    echo "Usage: ./run.sh --uefi [--window] <Sample> [args...]" >&2
    exit 1
fi
name=$1
shift

efi=bin/uefi/$name.efi
if [ ! -f "$efi" ]; then
    echo "Error: no $efi. Run ./build.sh --uefi $name" >&2
    exit 1
fi

# The PE header's machine field: 0x8664 is x64, 0xAA64 arm64.
machine=$(python3 -c '
import struct, sys
data = open(sys.argv[1], "rb").read(4096)
pe = struct.unpack_from("<I", data, 0x3C)[0]
print({0x8664: "x64", 0xAA64: "arm64"}.get(struct.unpack_from("<H", data, pe + 4)[0], "unknown"))' "$efi")
if ! firmware=$(find_uefi_firmware "$machine"); then
    echo "Error: no UEFI firmware for $machine. Install QEMU (Homebrew's includes it) or the ovmf package, or set UEFI_FIRMWARE." >&2
    exit 1
fi

# No network card: the firmware would try to boot from the network first.
# Hardware acceleration when the program is for this machine's own processor:
# Hypervisor.framework on macOS, KVM on Linux. Otherwise QEMU emulates, with
# every instruction set extension it has (-cpu max), AVX2 included.
accel=(-accel tcg -cpu max)
if [ "$machine" = "$HOST_ARCH" ]; then
    if [ "$(uname -s)" = Darwin ]; then
        accel=(-accel hvf -cpu host)
    elif [ -w /dev/kvm ]; then
        accel=(-accel kvm -cpu host)
    fi
fi

work=bin/uefi/run
mkdir -p "$work"
disk=$work/disk.img
rm -f "$work"/*.TXT "$work/serial.log"

# Input: a file or a pipe becomes IN.TXT; a terminal is the console (below);
# anything else (/dev/null) is an empty IN.TXT.
interactive=0
if [ "$window" = 0 ]; then
    if [ -f /dev/stdin ] || [ -p /dev/stdin ]; then
        cat > "$work/IN.TXT"
    elif [ -t 0 ]; then
        interactive=1
    else
        : > "$work/IN.TXT"
    fi
fi

# The arguments as a shell command line: an argument with a space or a
# character the shell treats specially is quoted, and ^ escapes ", % and ^.
arguments=
for argument in "$@"; do
    escaped=$(printf '%s' "$argument" | sed 's/[\^"%]/^&/g')
    case "$argument" in
        "" | *[[:space:]\<\>\|\#\"%^]*) arguments+=" \"$escaped\"" ;;
        *) arguments+=" $escaped" ;;
    esac
done

{
    printf '@echo -off\r\nfs0:\r\n'
    if [ "$window" = 1 ]; then
        printf 'PROGRAM.EFI%s\r\n' "$arguments"
    elif [ "$interactive" = 1 ]; then
        printf 'PROGRAM.EFI%s\r\n' "$arguments"
        printf 'echo %%lasterror%% > CODE.TXT\r\nreset -s\r\n'
    else
        printf 'PROGRAM.EFI%s < IN.TXT > OUT.TXT 2> ERR.TXT\r\n' "$arguments"
        printf 'echo %%lasterror%% > CODE.TXT\r\nreset -s\r\n'
    fi
} > "$work/STARTUP.NSH"

files=("$efi=PROGRAM.EFI" "$work/STARTUP.NSH")
[ -f "$work/IN.TXT" ] && files+=("$work/IN.TXT")
python3 dos/fat16.py make "$disk" "${files[@]}"

memory=${UEFI_MEMORY:-1024}
# virtio-rng gives the firmware an EFI_RNG_PROTOCOL to draw random bytes
# from (RandomNumberGenerator, efi/runtime.c) on both architectures.
case "$machine" in
    x64)
        command=(qemu-system-x86_64 -machine q35 -m "$memory" "${accel[@]}" -nic none
            -drive "if=pflash,format=raw,readonly=on,file=$firmware"
            -drive "format=raw,file=$disk" -device virtio-rng-pci)
        ;;
    arm64)
        command=(qemu-system-aarch64 -machine virt -m "$memory" "${accel[@]}" -nic none -bios "$firmware"
            -drive "if=none,id=disk,format=raw,file=$disk" -device virtio-blk-pci,drive=disk
            -device virtio-rng-pci)
        # virt has no screen or keyboard of its own: ramfb is a screen the
        # firmware can draw on (Vga test reads it back, window or not).
        command+=(-device ramfb)
        if [ "$window" = 1 ]; then
            command+=(-device qemu-xhci -device usb-kbd -device usb-tablet)
        fi
        ;;
    *)
        echo "Error: $efi is neither an x64 nor an arm64 program" >&2
        exit 1
        ;;
esac

if [ "$window" = 1 ]; then
    echo "Starting QEMU ($machine). The program is FS0:\\PROGRAM.EFI; close the window to end." >&2
    "${command[@]}" -serial none > "$work/qemu.log" 2>&1 || true
    exit 0
fi

if [ "$interactive" = 1 ]; then
    # The serial port is the console: this terminal. The shell's 5-second
    # wait before STARTUP.NSH ends at the first key.
    "${command[@]}" -nographic -no-reboot || true
else
    # The shell waits 5 seconds for a key before it runs STARTUP.NSH. The
    # console includes the serial port, whose input comes from a FIFO here:
    # once the wait shows in the serial output, a key skips it. (Sent sooner,
    # the firmware would eat it before the shell starts.)
    rm -f "$work/key"
    mkfifo "$work/key"
    exec 3<>"$work/key"
    "${command[@]}" -display none -no-reboot \
        -chardev "file,id=serial,path=$work/serial.log,input-path=$work/key" -serial chardev:serial \
        < /dev/null > "$work/qemu.log" 2>&1 &
    pid=$!
    deadline=$((SECONDS + ${UEFI_TIMEOUT:-300}))
    sent=0
    crashed=0
    while kill -0 "$pid" 2>/dev/null; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            kill "$pid" 2>/dev/null || true
            break
        fi
        # A processor exception: the firmware prints it and stops for good.
        if grep -aq -e "!!!! X64 Exception" -e "Synchronous Exception" "$work/serial.log" 2>/dev/null; then
            sleep 0.5 # the rest of the report
            kill "$pid" 2>/dev/null || true
            crashed=1
            break
        fi
        if [ "$sent" = 0 ] && grep -q "Press ESC" "$work/serial.log" 2>/dev/null; then
            printf '\r' >&3
            sent=1
        fi
        sleep 0.1
    done
    wait "$pid" 2>/dev/null || true
    exec 3>&-
    rm -f "$work/key"

    # Whatever the program wrote before it ended, even if it didn't end well,
    # without the byte order mark the shell starts each file with.
    python3 dos/fat16.py read "$disk" OUT.TXT 2>/dev/null | python3 -c '
import sys
data = sys.stdin.buffer.read()
sys.stdout.buffer.write(data[2:] if data.startswith(b"\xff\xfe") else data)' || true
    python3 dos/fat16.py read "$disk" ERR.TXT 2>/dev/null | python3 -c '
import sys
data = sys.stdin.buffer.read()
sys.stdout.buffer.write(data[2:] if data.startswith(b"\xff\xfe") else data)' >&2 || true
fi

if [ "${crashed:-0}" = 1 ]; then
    echo "Error: $name stopped with a processor exception (QEMU, $machine):" >&2
    tr -d '\r' < "$work/serial.log" | grep -a -A3 -e "!!!! X64 Exception" -e "Synchronous Exception" | sed 's/\x1b\[[0-9;]*[a-zA-Z]//g' | head -4 >&2
    exit 125
fi

# The shell writes %lasterror% in hex, as UTF-16: "0x86" is 134.
if ! code=$(python3 dos/fat16.py read "$disk" CODE.TXT 2>/dev/null | python3 -c '
import sys
text = sys.stdin.buffer.read().decode("utf-16").strip()
print(int(text, 16) if text.startswith("0x") else int(text))' 2>/dev/null); then
    echo "Error: $name didn't finish (QEMU, $machine). See $work/serial.log" >&2
    exit 125
fi
# An exit code above 255 is a status the firmware or the shell set, not one
# from the program (start.c returns 0-255).
[ "$code" -le 255 ] 2>/dev/null || code=125
exit "$code"
