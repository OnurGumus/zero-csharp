#!/usr/bin/env bash
# Builds the samples with the bflat fork, which compiles C# 15 with the updated
# memory safety rules in one step. They run without .NET's runtime: the core
# library is zerocore/ (see zerocore/README.md), compiled from source here.
#
# On macOS bflat runs in a Linux container (see docker/Dockerfile); the
# binaries in ./bin are Linux binaries, so use ./run.sh to run them.
#
# With --wasm, builds WebAssembly modules (bin/wasm/*.wasm, WASI) instead, with
# NativeAOT-LLVM and the WASI SDK (./setup.sh --wasm downloads them).
#
# With --dos, builds 32-bit DOS programs for a 386 and later (bin/dos/*.exe),
# with NativeAOT-LLVM and a clang that has the x86 target (./setup.sh --dos).
#
# With --c64, builds Commodore 64 programs (bin/c64/*.prg), with NativeAOT-LLVM
# and llvm-mos, a compiler for the 6502 (./setup.sh --c64).
#
# With --pico2 or --r4, builds programs for a Cortex-M microcontroller: the
# Raspberry Pi Pico 2 or the Arduino Uno R4 (bin/pico2/*.elf, bin/r4/*.elf),
# with NativeAOT-LLVM and Arm Toolchain for Embedded (./setup.sh --arm).
#
# With --win, builds 64-bit Windows programs (bin/win/*.exe), with bflat and
# a clang that has the x86 target for the small C and assembly part.
#
# With --uefi, builds UEFI programs (bin/uefi/*.efi), which the firmware
# starts without an operating system, for BFLAT_ARCH (x64 or arm64).
#
# With --gpu (on a Mac with Apple Silicon), builds macOS programs whose C#
# kernels run on the GPU (bin/gpu/*; lib/Gpu.cs, gpu/), with NativeAOT-LLVM
# (./setup.sh --wasm) and Apple's Metal Toolchain
# (xcodebuild -downloadComponent MetalToolchain).
#
# With --mac (on a Mac with Apple Silicon), builds macOS programs (bin/mac/*)
# that run directly, without the container: bflat compiles, Xcode's clang links.
# With MAC_OBJECTS_PREBUILT=1 it only links, using the object files already in
# bin/mac/obj (from `./build.sh --native-mac-objects <samples>` on Linux), and
# needs neither bflat nor Docker: that is how CI's macOS job works.
#
# With --ios (on a Mac with Apple Silicon), builds iOS programs for the
# simulator (bin/ios/*), from the --mac objects; ./run.sh --ios runs them.
#
# With --android, builds command-line programs for Android on arm64
# (bin/android/*), with bflat and Android's C library from its layout, no NDK;
# ./run.sh --android runs them on a phone or emulator, through adb.
#
# Usage:  ./build.sh [--wasm | --dos | --c64 | --pico2 | --r4 | --win | --mac | --ios | --android | --gpu | --uefi] [Sample ...]
#         ./build.sh --gpu-air [Sample ...]  the GPU build up to AIR, without a Mac (no program)     default: all samples
#         ./build.sh --cuda-ptx [Sample ...] experimental: the kernels as PTX for NVIDIA GPUs, checked by ptxas
#         ./build.sh --cuda [Sample ...]     experimental: Linux programs that run them through CUDA;
#                                            not yet run on an NVIDIA GPU (see "CUDA (experimental)")
#         ./build.sh --sdk-payload <dir>     what the ZeroCSharp.Sdk package carries (sdk/)
#         ./build.sh --must-not-compile      check that tests/must-not-compile/*.cs are refused
# Env:    BFLAT_DIR, BFLAT_FORK, BFLAT_ARCH, WASM_TOOLS, DOS_CLANG (see env.sh),
#         BFLAT_EXTRA_FLAGS (see bflat_flags), KEEP_SYMBOLS (see LINK_FLAGS)
set -euo pipefail
cd "$(dirname "$0")"
source ./env.sh

# The Small samples are tests sized for a machine with little memory: what
# Strings, Casts, Collections and the others check, in pieces that each fit
# in a Commodore 64. They run on every target.
# The lists have one sample per line, so pull requests that add different
# samples change different lines and merge without conflicts.
SMALL_SAMPLES=(
    SmallStrings
    SmallBuilders
    SmallIntegers
    SmallDoubles
    SmallObjects
    SmallArena
    SmallNative
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
    SmallParsing
    SmallKeys
    SmallFibers
    SmallChannels
    SmallScratch
    SmallCatches
    SmallScratchBuffer
    SmallDot
)
ALL_SAMPLES=(
    Tiny
    Minimal
    Hello
    Arena
    Collections
    VirtualTest
    Casts
    Core
    Bits
    Files
    Paths
    Streams
    Catches
    Network
    Directories
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
    Timers
    Tasks
    Records
    Shapes
    Refs
    Patterns
    Scratch
    Links
    Llama
    Unicode
    Numerics
    Crypto
    CryptoErrors
    Certificates
    Https
    CryptoRandom
    Formats
    Parsing
    Cultures
    Dates
    Casing
    CustomFormats
    Keys
    Tensors
    TensorsAvx
    Avx2Ops
    TensorTypes
    "${SMALL_SAMPLES[@]}"
)
# Samples WebAssembly can't run: the fiber samples switch stacks, which a WebAssembly
# program has no way to do, and Catches, CryptoErrors and Cultures catch exceptions with Catch.Run,
# which jumps back up the stack, which it can't do either.
NOT_WASM=" Fibers Timers Tasks SmallFibers SmallChannels SmallScratch SmallCatches Network Catches CryptoErrors Cultures Https "
# Programs with kernels for the GPU (Metal): only --gpu builds them.
GPU_SAMPLES=(Gpu)
# Simd and Kernels benchmark SIMD instructions, which a 386 doesn't have.
# Vga is a game for DOS and UEFI (lib/Dos.cs, lib/Uefi.cs: graphics, keyboard, clock).
DOS_SAMPLES=(
    Tiny
    Minimal
    Hello
    Arena
    Collections
    VirtualTest
    Casts
    Core
    Bits
    Files
    Paths
    Streams
    Catches
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
    Fibers
    Timers
    Tasks
    Scratch
    Links
    Llama
    Unicode
    Numerics
    Crypto
    CryptoErrors
    Certificates
    Formats
    Parsing
    Cultures
    Dates
    Casing
    CustomFormats
    Keys
    Tensors
    TensorTypes
    "${SMALL_SAMPLES[@]}"
    Vga
)
# A C64 program has about 50 KB for its code, data, heap and stack together,
# and the default is the samples that fit. Any sample can be asked for by
# name; the linker says by how much it is too big. C64Demo, Paddles and C64Reu
# are for the C64 only (lib/C64.cs: the screen, the video chip, the keyboard,
# an REU).
C64_SAMPLES=(
    Tiny
    Minimal
    Hello
    VirtualTest
    QuizStep1
    QuizStep2
    Refs
    "${SMALL_SAMPLES[@]}"
    C64Demo
    Paddles
    C64Reu
    C64RomOut
)
# A microcontroller has no SIMD to benchmark, and Vga is a PC game. Blink is
# for microcontrollers only (lib/Board.cs: the LED, registers, a timer). The Uno R4
# has 32 KB of RAM, for the data, the stacks and the heap: these fit.
ARM_SAMPLES=(
    Tiny
    Minimal
    Hello
    Arena
    Collections
    VirtualTest
    Casts
    Core
    Bits
    Files
    Paths
    Streams
    Catches
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
    Fibers
    Timers
    Tasks
    Records
    Shapes
    Refs
    Patterns
    Scratch
    Links
    Llama
    Unicode
    Numerics
    Crypto
    CryptoErrors
    Certificates
    Formats
    Parsing
    Cultures
    Dates
    Casing
    CustomFormats
    Keys
    Tensors
    TensorTypes
    "${SMALL_SAMPLES[@]}"
    Blink
)
# Arena, Collections, Files, Strings, Queries, Threads, Fibers, Patterns,
# Scratch, Links and Unicode need more (Arena, for one, allocates a 16 KB
# array; Fibers runs three fibers of 5 KB at once). Links passes, but with
# less than 2 KB to spare, so a small change elsewhere would break it.
R4_SAMPLES=(
    Tiny
    Minimal
    Hello
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
    Cultures
    Dates
    Casing
    CustomFormats
    "${SMALL_SAMPLES[@]}"
    Blink
)
# UEFI gets every sample but these, and Vga too (lib/Uefi.cs has what it uses
# of lib/Dos.cs). Directories needs directories, which lib/ has only on Linux,
# macOS, Windows and WebAssembly.
NOT_UEFI=" Directories Network Https "
UEFI_SAMPLES=(Vga)
for name in "${ALL_SAMPLES[@]}"; do
    [[ "$NOT_UEFI" == *" $name "* ]] || UEFI_SAMPLES+=("$name")
done

# Language version and memory safety rules for every sample.
CSHARP_FLAGS=(--langversion preview --compiler-feature updated-memory-safety-rules)

# zerocore/ is the core library: object, string, Span<T>, and the runtime code
# the compiler expects to find (allocation, casts, static fields). bflat only
# accepts a custom core library under the name System.Private.CoreLib.
# --stdlib:none means "no built-in library": every build references this one.
CORE=bin/System.Private.CoreLib.dll
CORE_FLAGS=(--stdlib:none --langversion preview)

# lib/ is compiled once into an IL assembly that the samples reference with -r,
# the way a C program links a library, except the assembly's metadata replaces
# the header files. It holds IL, not native code: each sample's bflat build
# compiles the parts it uses, for its own -m flags, into a single binary.
# -O matters: unlike bflat build, build-il doesn't optimize unless asked.
LIB=bin/BflatLib.dll

# What every native build needs besides the sources:
# --no-reflection   bflat assumes a library named System.Private.CoreLib is
#                   .NET's and would look for its reflection support
# -i libc ...       bind [DllImport("libc")] at link time; binding at run time
#                   needs runtime support zerocore doesn't have
NATIVE_FLAGS=(--no-reflection -i libc -i libm.so.6)

check_sample() {
    case " ${ALL_SAMPLES[*]} " in
        *" $1 "*) ;;
        *) echo "Unknown sample: $1 (known: ${ALL_SAMPLES[*]})" >&2; return 1 ;;
    esac
}

bflat_flags() {
    # BFLAT_EXTRA_FLAGS: more flags for every sample, e.g. -O0 to test the
    # unoptimized code that the SDK's Debug builds make.
    echo ${BFLAT_EXTRA_FLAGS:-}
    case "$1" in
        Tiny) echo -Os ;;
        # Zero mode has no runtime CPU detection, so SIMD extensions must be part of
        # the baseline. x64: AVX2/FMA (Haswell, 2013, or newer). arm64 always has
        # NEON; dotprod (SDOT, for int8) needs ARMv8.2+, e.g. Apple M1 or Cortex-A76.
        # TensorsAvx is Tensors built for AVX, so both of TensorPrimitives' x64
        # paths are tested: SSE2 (Tensors) and AVX (TensorsAvx).
        Simd | Kernels | Llama | TensorsAvx | Avx2Ops)
            if [ "$BFLAT_ARCH" = x64 ]; then echo -m x86-64-v3; else echo -m dotprod; fi ;;
    esac
}

# arm64 links with 64 KB segment alignment by default, which pads even Tiny
# (~5 KB of code) to ~200 KB. 4 KB pages give the same sizes as x64, but the
# binaries then won't load on 16 KB/64 KB-page kernels (Asahi Linux, some
# servers); set ARM64_MAX_PAGE_SIZE=65536 for those.
ARM64_MAX_PAGE_SIZE=${ARM64_MAX_PAGE_SIZE:-4096}
# zerocore's few assembly routines (zerocore/native), linked into every binary.
CORE_NATIVE=bin/zerocore-$BFLAT_ARCH.o
LINK_FLAGS=(--ldflags "$CORE_NATIVE")
if [ "$BFLAT_ARCH" = arm64 ]; then
    LINK_FLAGS+=(--ldflags "-z max-page-size=$ARM64_MAX_PAGE_SIZE")
fi
# Linux programs are built as releases: without debug information and with no
# symbol table (the linker drops it, so this works whatever the architecture),
# and not position-independent, which leaves out the relocations ASLR needs.
# Together that is 55 to 70% of a program. KEEP_SYMBOLS=1 keeps all three,
# for gdb or perf.
if [ -z "${KEEP_SYMBOLS:-}" ]; then
    LINK_FLAGS+=(--no-debug-info --no-pie --ldflags --strip-all)
fi

# The sources of a sample: samples/<name>.cs, plus anything generated for it;
# TensorsAvx is samples/Tensors.cs again, built with other flags.
# Llama compiles its model and tokenizer in as byte literals, generated from
# models/ into bin/gen/Stories260K.cs (scripts/llama-model-cs.sh).
sample_sources() {
    if [ "$1" = TensorsAvx ]; then
        echo samples/Tensors.cs
        return
    fi
    echo "samples/$1.cs"
    if [ "$1" = Llama ]; then
        scripts/llama-model-cs.sh bin/gen/Stories260K.cs
        echo bin/gen/Stories260K.cs
    fi
}

check_bflat() {
    if [ ! -x "$BFLAT_DIR/bflat" ]; then
        echo "Error: no bflat at $BFLAT_DIR" >&2
        echo "       Build the fork's layouts (README, \"Getting bflat\") or set BFLAT_DIR." >&2
        exit 1
    fi
}

# $1: output assembly; the rest: extra flags (-d WASM for the WebAssembly build).
build_core() {
    local out=$1
    shift
    mkdir -p "$(dirname "$out")"
    echo "[bflat] zerocore/ -> $out"
    # shellcheck disable=SC2046
    "$BFLAT_DIR/bflat" build-il $(find zerocore -name '*.cs' | sort) -O "${CORE_FLAGS[@]}" "$@" -o "$out"
}

# Any assembler for the target will do: the cross one from the build image, or
# plain `as` when building on the target's own architecture.
build_core_native() {
    local triple as
    case "$BFLAT_ARCH" in
        x64)   triple=x86_64-linux-gnu ;;
        arm64) triple=aarch64-linux-gnu ;;
    esac
    if command -v "$triple-as" >/dev/null 2>&1; then
        as=$triple-as
    elif [ "$BFLAT_ARCH" = "$HOST_ARCH" ] && command -v as >/dev/null 2>&1; then
        as=as
    else
        echo "Error: no assembler for $BFLAT_ARCH. Install binutils (binutils-$triple to cross-compile)." >&2
        exit 1
    fi
    echo "[as]    zerocore/native/$BFLAT_ARCH.S -> $CORE_NATIVE"
    "$as" "zerocore/native/$BFLAT_ARCH.S" -o "$CORE_NATIVE"
    # A native library's startup (NativeLib=Shared), for the SDK's payload.
    if [ -n "${LIBRARY_NATIVE:-}" ]; then
        echo "[as]    zerocore/native/library-$BFLAT_ARCH.S -> $LIBRARY_NATIVE"
        "$as" "zerocore/native/library-$BFLAT_ARCH.S" -o "$LIBRARY_NATIVE"
    fi
}

# Your own libraries: each folder in libs/ is compiled, like lib/, into an IL
# assembly, bin/libs/<folder>.dll, and every program references all of them.
# A library can use zerocore, lib/ and the libraries before it in name order.
# LIB_REFS holds the -r flags for lib/ and these.
LIB_REFS=(-r "$LIB")

build_lib() {
    local dir name refs
    echo "[bflat] lib/*.cs -> $LIB"
    "$BFLAT_DIR/bflat" build-il lib/*.cs -O --stdlib:none -r "$CORE" "${CSHARP_FLAGS[@]}" --target Shared -o "$LIB"
    LIB_REFS=(-r "$LIB")
    for dir in libs/*/; do
        [ -d "$dir" ] || continue
        name=$(basename "$dir")
        refs=("${LIB_REFS[@]}")
        mkdir -p bin/libs
        echo "[bflat] libs/$name/*.cs -> bin/libs/$name.dll"
        "$BFLAT_DIR/bflat" build-il "$dir"*.cs -O --stdlib:none -r "$CORE" "${refs[@]}" "${CSHARP_FLAGS[@]}" \
            --target Shared -o "bin/libs/$name.dll"
        LIB_REFS+=(-r "bin/libs/$name.dll")
    done
}

build_native() {
    local name
    check_bflat
    build_core "$CORE"
    build_core_native
    build_lib
    for name in "$@"; do
        echo "[bflat] $name -> bin/$name"
        # shellcheck disable=SC2046
        "$BFLAT_DIR/bflat" build $(sample_sources "$name") "${LIB_REFS[@]}" -r "$CORE" --stdlib:none "${CSHARP_FLAGS[@]}" \
            "${NATIVE_FLAGS[@]}" --arch "$BFLAT_ARCH" $(bflat_flags "$name") "${LINK_FLAGS[@]}" -o "bin/$name"
    done
}

# zerocore's WebAssembly build differs in how it finds static fields (-d WASM).
WASM_CORE=bin/wasm/System.Private.CoreLib.dll

check_wasm() {
    if [ ! -x "$WASM_ILC" ] || [ ! -x "$WASI_SDK/bin/clang" ]; then
        echo "Error: no WebAssembly toolchain in $WASM_TOOLS. Run ./setup.sh --wasm" >&2
        exit 1
    fi
}

# Source -> IL (bflat, as for the native build) -> LLVM bitcode (NativeAOT-LLVM).
# $1: sample; $2: directory for the output; $3: zerocore's WebAssembly build;
# $4: how to optimize (default -O; --Os favors size over speed); the rest:
# more ILC flags.
# Leaves $name.N.bc (bitcode: the code) and two WASM object files that need no
# compiling: $name.bc (the program's data) and $name.external.o.
compile_bitcode() {
    local name=$1 obj=$2 core=$3 optimize=${4:--O}
    shift $(($# < 4 ? $# : 4))
    rm -rf "$obj"
    mkdir -p "$obj"

    "$BFLAT_DIR/bflat" build-il $(sample_sources "$name") "${LIB_REFS[@]}" -r "$CORE" -O --stdlib:none "${CSHARP_FLAGS[@]}" -o "$obj/$name.dll"

    # --directpinvoke binds [DllImport("libc")] and ("libm.so.6") at link time,
    # to the target's C library.
    if ! "$WASM_ILC" "$obj/$name.dll" "${LIB_REFS[@]}" -r "$core" --reflectiondata:none \
            --generateunmanagedentrypoints:System.Private.CoreLib \
            --targetos:wasi --targetarch:wasm "$optimize" --wasm-global-base:1024 \
            --codegenopt:Target=wasm32-unknown-wasip2 --codegenopt:LlvmExceptionHandlingModel=emulated \
            --directpinvoke:libc --directpinvoke:libm.so.6 "$@" -o:"$obj/$name.bc" > "$obj/ilc.log" 2>&1; then
        cat "$obj/ilc.log" >&2
        exit 1
    fi
}

# Bitcode (against zerocore's WebAssembly build) -> WASM objects (clang) -> one
# module, linked with wasm/runtime.c (zerocore's native half there),
# wasm/simd.c (SIMD kernels for TensorPrimitives and lib/Kernels.cs) and
# WASI's C library.
build_wasm() {
    local name obj bc
    check_bflat
    check_wasm
    build_core "$CORE"
    # WASM_SIMD: only this build has wasm/simd.c linked in, so only here does
    # zerocore's PackedSimd.IsSupported say true (DOS, C64 and ARM build
    # zerocore with -d WASM too).
    build_core "$WASM_CORE" -d WASM -d WASM_SIMD
    build_lib
    # simd128 is in every WebAssembly engine since 2021 (Chrome 91, Firefox 89,
    # Safari 16.4, wasmtime, Node 16.4).
    "$WASI_SDK/bin/clang" -target wasm32-wasip1 -O2 -msimd128 -Wall -Wextra -c wasm/simd.c -o bin/wasm/simd.o
    for name in "$@"; do
        echo "[wasm]  $name -> bin/wasm/$name.wasm"
        obj=bin/wasm/obj/$name
        compile_bitcode "$name" "$obj" "$WASM_CORE"

        for bc in "$obj/$name".*.bc; do
            "$WASI_SDK/bin/clang" "$bc" -c -o "${bc%.bc}.o" -target wasm32-unknown-wasip2 -O2 \
                -mllvm -combiner-global-alias-analysis=false -mllvm -disable-lsr
        done
        cp "$obj/$name.bc" "$obj/$name.data.o"

        # wasip1, not wasip2: a WASI 0.2 exit only says success or failure, and the
        # samples report failures in their exit code. --no-stack-first: WASI SDK 34
        # puts the stack first by default, which clashes with the 1 KB global base.
        # --fatal-warnings: if wasm/runtime.c's signature for a function no longer
        # matches what NativeAOT-LLVM's code calls, wasm-ld only warns and links a
        # trap; this makes it a build error instead.
        "$WASI_SDK/bin/clang" -target wasm32-wasip1 -Os "$obj"/*.o bin/wasm/simd.o wasm/runtime.c -lm -o "bin/wasm/$name.wasm" \
            -Wl,--no-stack-first -Wl,--global-base=1024 -Wl,-z,stack-size=1048576 \
            -Wl,--strip-all -Wl,--gc-sections -Wl,--fatal-warnings
    done
}

# ---- DOS ---------------------------------------------------------------------
#
# The same bitcode as for WebAssembly, compiled for a 386 instead. Three steps,
# because on macOS the first and last run in the build container (bflat,
# NativeAOT-LLVM and bflat's linker are Linux programs) and the middle one on
# the Mac (its clang has the x86 target; the WASI SDK's doesn't):
#
#   build_dos_bitcode   C# -> bitcode, against zerocore's single-threaded
#                       32-bit build (the WebAssembly one)
#   build_dos_objects   bitcode -> 386 object files; the program's data, which
#                       NativeAOT-LLVM writes as a WebAssembly object file, is
#                       rewritten as assembly by zero-bitcode (wasm2i386)
#   build_dos_link      -> a 32-bit Windows console program, which the HX DOS
#                       extender runs under DOS, with dos/runtime.c as its C
#                       library and zerocore's native half

# zero-bitcode (sdk/ZeroCSharp.Bitcode): turns NativeAOT-LLVM's output into
# sources for DOS and the C64. A .NET program, built here on first use; it runs
# on the host, not in the build container.
build_bitcode_tool() {
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "Error: the .NET SDK (10 or newer) is needed to build zero-bitcode, the tool in sdk/ZeroCSharp.Bitcode" >&2
        exit 1
    fi
    mkdir -p bin/tools
    if ! dotnet build sdk/ZeroCSharp.Bitcode -c Release -o bin/tools/zero-bitcode -v q -nologo > bin/tools/zero-bitcode.log 2>&1; then
        cat bin/tools/zero-bitcode.log >&2
        exit 1
    fi
}

zero_bitcode() {
    dotnet bin/tools/zero-bitcode/zero-bitcode.dll "$@"
}

DOS_CORE=bin/dos/System.Private.CoreLib.dll
# Windows object files (COFF), C calling convention, nothing newer than a 386.
DOS_TARGET=(-target i386-pc-windows-gnu -march=i386)

check_dos_ilc() {
    if [ ! -x "$WASM_ILC" ]; then
        echo "Error: no NativeAOT-LLVM in $WASM_TOOLS. Run ./setup.sh --dos" >&2
        exit 1
    fi
}

build_dos_bitcode() {
    local name
    check_bflat
    check_dos_ilc
    build_core "$CORE"
    build_core "$DOS_CORE" -d WASM
    build_lib
    for name in "$@"; do
        echo "[dos]   $name -> bitcode"
        compile_bitcode "$name" "bin/dos/obj/$name" "$DOS_CORE"
    done
}

# $1: clang; the rest: samples.
build_dos_objects() {
    local clang=$1 name obj out source bc
    shift
    "$clang" "${DOS_TARGET[@]}" -O2 -ffreestanding -fno-builtin -Wall -Wextra -c dos/runtime.c -o bin/dos/obj/runtime.o
    for name in "$@"; do
        echo "[dos]   $name -> 386 code"
        obj=bin/dos/obj/$name
        out=$obj/i386
        mkdir -p "$out"

        zero_bitcode wasm2i386 "$obj/$name.bc" "$out/data"
        if [ -f "$obj/$name.external.o" ]; then
            zero_bitcode wasm2i386 "$obj/$name.external.o" "$out/external"
        fi
        for source in "$out"/*.s; do
            "$clang" "${DOS_TARGET[@]}" -c "$source" -o "$source.o"
        done
        for source in "$out"/*.c; do
            [ -f "$source" ] || continue
            "$clang" "${DOS_TARGET[@]}" -O2 -ffreestanding -c "$source" -o "$source.o"
        done

        for bc in "$obj/$name".*.bc; do
            source=$out/$(basename "${bc%.bc}")
            # The flag compiled code checks after every call (RhpExceptionThrown)
            # is declared thread-local. These programs have one thread, and
            # Windows-style thread-local storage costs three more memory reads
            # per check, so make it an ordinary variable: bitcode -> text ->
            # edit -> object file.
            "$clang" "${DOS_TARGET[@]}" -Wno-override-module -S -emit-llvm "$bc" -o "$source.bc.ll"
            sed -E 's/^(@[A-Za-z0-9_.$]+ = external) thread_local /\1 /' "$source.bc.ll" > "$source.ll"
            rm "$source.bc.ll"
            # As a word: a string constant's symbol can contain it ("thread-local").
            if grep -qw thread_local "$source.ll"; then
                echo "Error: thread-local variables left in $source.ll" >&2
                exit 1
            fi
            "$clang" "${DOS_TARGET[@]}" -O2 -c "$source.ll" -o "$source.o"
        done
    done
}

build_dos_link() {
    local name
    check_bflat
    for name in "$@"; do
        echo "[dos]   $name -> bin/dos/$name.exe"
        # /safeseh:no: no table of Windows exception handlers; there are none.
        "$BFLAT_DIR/bin/lld" -flavor link /nologo "/out:bin/dos/$name.exe" /subsystem:console \
            /entry:start /nodefaultlib /safeseh:no /stack:262144 /opt:ref \
            bin/dos/obj/runtime.o "bin/dos/obj/$name/i386"/*.o "$BFLAT_DIR/lib/windows/x86/kernel32.lib"
    done
}

# "Runs on a 386" is easy to break without noticing: one atomic operation or
# byte swap and the compiler emits a 486 instruction, and most emulators run it
# anyway. So disassemble each program and refuse anything newer than a 386 and
# its 387 coprocessor. $1: llvm-objdump; the rest: samples.
NEWER_THAN_386='^(cmov|fcmov|fu?comip? |fisttp|bswap|cmpxchg|xadd|cpuid|rdtsc|rdmsr|wrmsr|rdpmc|invd|wbinvd|invlpg|sysenter|sysexit|ud2|prefetch|cvt|nopw|nopl|[lms]fence|pause|emms|fxsave|fxrstor|popcnt|lzcnt|tzcnt|movbe|<unknown>)|xmm|ymm|%mm[0-7]'

check_386() {
    local objdump=$1 name bad
    shift
    for name in "$@"; do
        bad=$("$objdump" -d --no-show-raw-insn "bin/dos/$name.exe" \
            | awk '/^ *[0-9a-f]+:/ { print $2, $3, $4 }' | grep -E "$NEWER_THAN_386" | sort | uniq -c || true)
        if [ -n "$bad" ]; then
            echo "Error: bin/dos/$name.exe has instructions a 386 can't run:" >&2
            echo "$bad" >&2
            exit 1
        fi
    done
}

# ---- Commodore 64 ------------------------------------------------------------
#
# The same bitcode again, for a processor with 16-bit addresses. Two steps:
# on macOS the first runs in the build container and the second on the Mac,
# where llvm-mos is.
#
#   build_c64_bitcode   C# -> bitcode, against zerocore's single-threaded
#                       32-bit build (the WebAssembly one)
#   build_c64_programs  bitcode -> .prg: zero-bitcode's ll2mos rewrites the
#                       code for 2-byte pointers, its wasm2mos the data, and
#                       llvm-mos compiles and links both with c64/runtime.c
#                       (zerocore's native half) and its C library

C64_CORE=bin/c64/System.Private.CoreLib.dll

check_c64() {
    if [ ! -x "$LLVM_MOS/bin/mos-c64-clang" ]; then
        echo "Error: no llvm-mos in $C64_TOOLS. Run ./setup.sh --c64" >&2
        exit 1
    fi
}

build_c64_bitcode() {
    local name
    check_bflat
    check_dos_ilc
    build_core "$CORE"
    build_core "$C64_CORE" -d WASM -d C64
    build_lib
    for name in "$@"; do
        echo "[c64]   $name -> bitcode"
        # --Os: for size, not speed. The compiler then keeps one copy of each
        # loop (for speed it makes a second one without bounds checks).
        compile_bitcode "$name" "bin/c64/obj/$name" "$C64_CORE" --Os
    done
}

build_c64_programs() {
    local name
    for name in "$@"; do
        echo "[c64]   $name -> bin/c64/$name.prg"
        # zero-bitcode (sdk/ZeroCSharp.Bitcode/C64.cs): the bitcode, rewritten
        # for the 6502 (ll2mos, wasm2mos) and linked by llvm-mos with
        # c64/runtime.c, retrying a method that runs out of the 6502's
        # registers on its own. The linker also leaves bin/c64/$name.prg.elf,
        # with the symbols: c64/run.sh looks an address up in it.
        zero_bitcode c64-link "bin/c64/obj/$name" "$name" c64/runtime.c "bin/c64/$name.prg" --llvm-mos "$LLVM_MOS"
    done
}

# ---- Cortex-M microcontrollers -----------------------------------------------
#
# The same bitcode once more, for a 32-bit Arm microcontroller (see env.sh for
# the boards). Little-endian with 4-byte pointers, like WebAssembly, so clang
# compiles it as it is. Two steps, as for the C64:
#
#   build_arm_bitcode   C# -> bitcode (in the build container on macOS), the
#                       same for every board: bin/arm/obj/<Sample>
#   build_arm_programs  bitcode -> an ELF program for one board, with Arm
#                       Toolchain for Embedded's clang, on this machine:
#                       zero-bitcode's wasm2arm writes the data as assembly,
#                       and arm/runtime.c is zerocore's native half

ARM_CORE=bin/arm/System.Private.CoreLib.dll

check_arm() {
    if [ ! -x "$ARM_TOOLCHAIN/bin/clang" ]; then
        echo "Error: no Arm Toolchain for Embedded in $ARM_TOOLS. Run ./setup.sh --arm" >&2
        exit 1
    fi
}

build_arm_bitcode() {
    local name
    check_bflat
    check_dos_ilc
    build_core "$CORE"
    build_core "$ARM_CORE" -d WASM
    build_lib
    for name in "$@"; do
        echo "[arm]   $name -> bitcode"
        compile_bitcode "$name" "bin/arm/obj/$name" "$ARM_CORE"
    done
}

# $1: the board; the rest: samples.
build_arm_programs() {
    local board=$1 name obj out source bc
    local clang=$ARM_TOOLCHAIN/bin/clang
    shift
    arm_board "$board"
    mkdir -p "bin/$board/obj"
    "$clang" "${ARM_TARGET[@]}" "${ARM_DEFINES[@]}" -Os -Wall -Wextra -c arm/runtime.c -o "bin/$board/obj/runtime.o"
    "$clang" "${ARM_TARGET[@]}" "${ARM_DEFINES[@]}" -Os -Wall -Wextra -c arm/semihost.c -o "bin/$board/obj/semihost.o"
    # picolibc's linker script places everything; the board's says where its
    # flash and RAM are.
    "$clang" -E -P -x c -DLIBC_LD_FILE=picolibcpp.ld "$ARM_LAYOUT" -o "bin/$board/obj/layout.ld"
    # For the Pico 2, the real chip's too: its memory map, its boot block,
    # and arm/rp2350.c instead of semihosting.
    if [ "$board" = pico2 ]; then
        mkdir -p "bin/$board/board"
        "$clang" "${ARM_TARGET[@]}" "${ARM_DEFINES[@]}" -Os -Wall -Wextra -c arm/rp2350.c -o "bin/$board/obj/rp2350.o"
        "$clang" "${ARM_TARGET[@]}" -c arm/rp2350.S -o "bin/$board/obj/rp2350-boot.o"
        "$clang" -E -P -x c -DLIBC_LD_FILE=picolibcpp.ld arm/rp2350.ld.in -o "bin/$board/obj/rp2350.ld"
    fi
    for name in "$@"; do
        echo "[$board] $name -> bin/$board/$name.elf"
        obj=bin/arm/obj/$name
        out=bin/$board/obj/$name
        rm -rf "$out"
        mkdir -p "$out"

        zero_bitcode wasm2arm "$obj/$name.bc" "$out/data"
        if [ -f "$obj/$name.external.o" ]; then
            zero_bitcode wasm2arm "$obj/$name.external.o" "$out/external"
        fi
        for source in "$out"/*.s; do
            "$clang" "${ARM_TARGET[@]}" -c "$source" -o "$source.o"
        done
        for source in "$out"/*.c; do
            [ -f "$source" ] || continue
            "$clang" "${ARM_TARGET[@]}" -Os -ffreestanding -c "$source" -o "$source.o"
        done
        for bc in "$obj/$name".*.bc; do
            source=$out/$(basename "${bc%.bc}")
            # One thread, so the flag compiled code checks after every call
            # (RhpExceptionThrown) needs no thread-local storage (see DOS).
            "$clang" "${ARM_TARGET[@]}" -Wno-override-module -S -emit-llvm "$bc" -o "$source.bc.ll"
            sed -E 's/^(@[A-Za-z0-9_.$]+ = external) thread_local /\1 /' "$source.bc.ll" > "$source.ll"
            rm "$source.bc.ll"
            if grep -qw thread_local "$source.ll"; then
                echo "Error: thread-local variables left in $source.ll" >&2
                exit 1
            fi
            "$clang" "${ARM_TARGET[@]}" -Wno-override-module -Os -c "$source.ll" -o "$source.o"
        done

        # crt0-semihost: picolibc's startup code, which gets the command line
        # from the host; libsemihost: its file functions, done by the host.
        # --wrap=read: arm/semihost.c reads piped standard input from a file.
        "$clang" "${ARM_TARGET[@]}" -nostartfiles -lcrt0-semihost -lsemihost \
            -T "bin/$board/obj/layout.ld" -Wl,--gc-sections -Wl,--wrap=read \
            -o "bin/$board/$name.elf" "bin/$board/obj/runtime.o" "bin/$board/obj/semihost.o" "$out"/*.o

        # The real Pico 2: picolibc's crt0-hosted (the plain crt0 never returns
        # from main, so it would never exit), arm/rp2350.c for
        # the chip, and the program again as a UF2 file, for copying to the
        # board when it shows up as a USB drive.
        if [ "$board" = pico2 ]; then
            "$clang" "${ARM_TARGET[@]}" -nostartfiles -lcrt0-hosted \
                -T "bin/$board/obj/rp2350.ld" -Wl,--gc-sections \
                -o "bin/$board/board/$name.elf" "bin/$board/obj/runtime.o" "bin/$board/obj/rp2350.o" \
                "bin/$board/obj/rp2350-boot.o" "$out"/*.o
            zero_bitcode elf2uf2 "bin/$board/board/$name.elf" "bin/$board/board/$name.uf2"
        fi
    done
}

# Flash (code and data) and RAM (data, the stacks and what's left for the heap
# is the rest) of each program. $1: the board; the rest: samples.
print_arm_sizes() {
    local board=$1 name
    shift
    echo
    for name in "$@"; do
        "$ARM_TOOLCHAIN/bin/llvm-size" "bin/$board/$name.elf" | awk -v name="$name" \
            'NR == 2 { printf "%-14s flash %7d bytes, RAM %7d bytes before the heap\n", name, $1 + $2, $2 + $3 }'
    done
}

# Windows, x64 only. The same bflat compiles the samples, against the same
# zerocore, with two Windows object files linked in:
#   win/start.c, win/runtime.c  the entry point; write/read/clock_gettime
#                               on top of kernel32 (the rest of the C library
#                               is Windows' own Universal CRT)
#   zerocore/native/win-x64.S   x64.S for the Windows calling convention
# clang compiles both on the host (Xcode's on macOS), like dos/runtime.c.
WIN_OBJ=bin/win/obj
WIN_TARGET=(-target x86_64-pc-windows-msvc)

# $1: clang
build_win_objects() {
    local clang=$1
    mkdir -p "$WIN_OBJ"
    local name
    echo "[clang] win/{start,runtime,library}.c, zerocore/native/win-x64.S -> $WIN_OBJ"
    for name in start runtime library; do
        "$clang" "${WIN_TARGET[@]}" -O2 -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Wall -Wextra -c "win/$name.c" -o "$WIN_OBJ/$name.obj"
    done
    "$clang" "${WIN_TARGET[@]}" -c zerocore/native/win-x64.S -o "$WIN_OBJ/zerocore.obj"
}

build_win() {
    local name
    check_bflat
    build_core "$CORE"
    build_lib
    for name in "$@"; do
        echo "[bflat] $name -> bin/win/$name.exe"
        # /entry: bflat names __managed__Main, which expects argc and argv;
        # zero_start (win/start.c) makes them first. The later /entry wins.
        # shellcheck disable=SC2046
        "$BFLAT_DIR/bflat" build $(sample_sources "$name") "${LIB_REFS[@]}" -r "$CORE" --stdlib:none \
            "${CSHARP_FLAGS[@]}" "${NATIVE_FLAGS[@]}" --os windows --arch x64 $(BFLAT_ARCH=x64 bflat_flags "$name") \
            --ldflags "/entry:zero_start /wrap:getenv /wrap:fopen /wrap:remove /wrap:mkdir /wrap:rmdir /wrap:rename /wrap:getcwd /wrap:chdir /wrap:socket /wrap:bind /wrap:listen /wrap:accept /wrap:connect /wrap:sendto /wrap:recvfrom /wrap:shutdown /wrap:close /wrap:setsockopt /wrap:getsockopt /wrap:getsockname /wrap:getpeername /wrap:getaddrinfo /wrap:freeaddrinfo /wrap:gethostname $WIN_OBJ/start.obj $WIN_OBJ/runtime.obj $WIN_OBJ/zerocore.obj" -o "bin/win/$name.exe"
        rm -f "bin/win/$name.pdb"
    done
}

# macOS on Apple Silicon. bflat has no Mach-O linker, so it only compiles
# (--os macos -c, in the container), and the Mac's own clang and linker do
# the rest, with two macOS files:
#   mac/start.c, mac/runtime.c  main; clock_gettime for the samples' clock
#   zerocore/native/mac-arm64.S arm64.S, as a Mach-O object
MAC_OBJ=bin/mac/obj

build_mac_objects() {
    local name
    check_bflat
    build_core "$CORE"
    build_lib
    mkdir -p "$MAC_OBJ"
    for name in "$@"; do
        echo "[bflat] $name -> $MAC_OBJ/$name.o"
        # shellcheck disable=SC2046
        "$BFLAT_DIR/bflat" build $(sample_sources "$name") "${LIB_REFS[@]}" -r "$CORE" --stdlib:none \
            "${CSHARP_FLAGS[@]}" "${NATIVE_FLAGS[@]}" --os macos --arch arm64 -c \
            $(BFLAT_ARCH=arm64 bflat_flags "$name") -o "$MAC_OBJ/$name.o"
    done
}

# $1: clang; the rest: samples.
# $1: clang. What every macOS program links besides its own object.
build_mac_runtime() {
    local clang=$1
    echo "[clang] mac/start.c, mac/runtime.c, zerocore/native/mac-arm64.S -> $MAC_OBJ"
    "$clang" -arch arm64 -O2 -Wall -Wextra -c mac/start.c -o "$MAC_OBJ/start.o"
    "$clang" -arch arm64 -O2 -Wall -Wextra -c mac/runtime.c -o "$MAC_OBJ/runtime.o"
    "$clang" -arch arm64 -c zerocore/native/mac-arm64.S -o "$MAC_OBJ/zerocore.o"
}

build_mac_link() {
    local clang=$1 name
    shift
    build_mac_runtime "$clang"
    for name in "$@"; do
        echo "[ld]    $name -> bin/mac/$name"
        # zerocore's module list: mac/start.c copies the section's entries
        # into a table in mac-arm64.S (lld, which the SDK uses, can't -alias).
        "$clang" -arch arm64 -o "bin/mac/$name" "$MAC_OBJ/$name.o" "$MAC_OBJ/start.o" "$MAC_OBJ/runtime.o" "$MAC_OBJ/zerocore.o" \
            -Wl,-dead_strip
        # Stripped like the Linux programs, unless KEEP_SYMBOLS is set.
        if [ -z "${KEEP_SYMBOLS:-}" ]; then strip "bin/mac/$name"; fi
    done
}

# ---- iOS simulator -------------------------------------------------------------
#
# iOS programs, for the simulator on a Mac with Apple Silicon (bin/ios/*). The
# program's object is the macOS one (build_mac_objects): iOS runs the same
# arm64 code, but the object says it was built for macOS, and Apple's linker
# won't put it in an iOS program, so zero-bitcode macho-platform marks a copy
# as built for the simulator. mac/'s C files compile again, for the simulator.
# The programs run with ./run.sh --ios, through `xcrun simctl spawn`, without
# an app bundle (ios/launch.c starts them in the caller's directory).
IOS_OBJ=bin/ios/obj
# The oldest iOS the programs start on.
IOS_MIN=${IOS_MIN:-17.0}

build_ios_link() {
    local clang=$1 name sdk target
    shift
    sdk=$(xcrun --sdk iphonesimulator --show-sdk-path)
    target=arm64-apple-ios$IOS_MIN-simulator
    mkdir -p "$IOS_OBJ"
    echo "[clang] mac/start.c, mac/runtime.c, zerocore/native/mac-arm64.S -> $IOS_OBJ (iOS simulator)"
    "$clang" -target "$target" -isysroot "$sdk" -O2 -Wall -Wextra -c mac/start.c -o "$IOS_OBJ/start.o"
    "$clang" -target "$target" -isysroot "$sdk" -O2 -Wall -Wextra -c mac/runtime.c -o "$IOS_OBJ/runtime.o"
    "$clang" -target "$target" -isysroot "$sdk" -c zerocore/native/mac-arm64.S -o "$IOS_OBJ/zerocore.o"
    # ios/run.sh's launcher, which starts a program in the caller's directory.
    "$clang" -target "$target" -isysroot "$sdk" -O2 -Wall -Wextra ios/launch.c -o "$IOS_OBJ/launch"
    for name in "$@"; do
        cp "$MAC_OBJ/$name.o" "$IOS_OBJ/$name.o"
        zero_bitcode macho-platform "$IOS_OBJ/$name.o" ios-simulator "$IOS_MIN"
        echo "[ld]    $name -> bin/ios/$name"
        "$clang" -target "$target" -isysroot "$sdk" -o "bin/ios/$name" \
            "$IOS_OBJ/$name.o" "$IOS_OBJ/start.o" "$IOS_OBJ/runtime.o" "$IOS_OBJ/zerocore.o" -Wl,-dead_strip
        if [ -z "${KEEP_SYMBOLS:-}" ]; then strip "bin/ios/$name"; fi
    done
}

# ---- Android -----------------------------------------------------------------
#
# Command-line programs for Android on arm64 (bin/android/*), the kind adb
# shell runs; no app. bflat compiles and links them as for Linux, against
# Android's C library, Bionic (--libc bionic), which its layout carries, so
# no Android NDK is needed. android/runtime.c gives lib/ the glibc names
# Bionic lacks; any clang compiles it (on the host, as win/'s files).
ANDROID_OBJ=bin/android/obj

# $1: clang
build_android_objects() {
    mkdir -p "$ANDROID_OBJ"
    echo "[clang] android/runtime.c -> $ANDROID_OBJ/runtime.o"
    "$1" -target aarch64-linux-android21 -O2 -ffreestanding -fPIC -Wall -Wextra -c android/runtime.c -o "$ANDROID_OBJ/runtime.o"
}

build_android() {
    local name strip=()
    check_bflat
    build_core "$CORE"
    BFLAT_ARCH=arm64 CORE_NATIVE=$ANDROID_OBJ/zerocore.o build_core_native
    build_lib
    # Stripped as the Linux programs are, but position-independent: Android
    # runs nothing else.
    [ -z "${KEEP_SYMBOLS:-}" ] && strip=(--no-debug-info --ldflags --strip-all)
    for name in "$@"; do
        echo "[bflat] $name -> bin/android/$name"
        # 16 KB pages: Android 15 and later run on phones with them, and
        # Google Play asks for it; a 4 KB-page phone loads these too.
        # shellcheck disable=SC2046
        "$BFLAT_DIR/bflat" build $(sample_sources "$name") "${LIB_REFS[@]}" -r "$CORE" --stdlib:none \
            "${CSHARP_FLAGS[@]}" "${NATIVE_FLAGS[@]}" --os linux --arch arm64 --libc bionic \
            $(BFLAT_ARCH=arm64 bflat_flags "$name") "${strip[@]}" \
            --ldflags "$ANDROID_OBJ/zerocore.o $ANDROID_OBJ/runtime.o -z max-page-size=16384" -o "bin/android/$name"
    done
}

# ---- GPU (Metal) -------------------------------------------------------------
#
# A macOS program whose kernels (its [RuntimeExport] methods) also run on the
# GPU. The same C# is compiled twice:
#   build_gpu_bitcode   C# -> bitcode, as for WebAssembly (in the build
#                       container on macOS); the kernels are taken from it
#   build_mac_objects   C# -> a macOS object, the whole program (as --mac)
# and on the Mac, build_gpu_link:
#   bitcode -> text IR (clang) -> the kernels as AIR, the LLVM dialect Apple's
#   Metal compiler reads (zero-bitcode ll2air) -> metallib (metal, metallib),
#   which the program carries; then the object + gpu/metal.m (Metal behind the
#   C functions lib/Gpu.cs calls) + mac/'s runtime -> bin/gpu/<Sample>
GPU_OBJ=bin/gpu/obj

build_gpu_bitcode() {
    local name
    check_bflat
    check_dos_ilc
    build_core "$CORE"
    build_core "$WASM_CORE" -d WASM
    build_lib
    for name in "$@"; do
        echo "[gpu]   $name -> bitcode"
        # ILC compiles a program's [RuntimeExport] methods, called or not, when
        # asked for its entry points: the kernels need nothing on the CPU to call them.
        compile_bitcode "$name" "$GPU_OBJ/$name" "$WASM_CORE" -O "--generateunmanagedentrypoints:$name"
    done
}

# The GPU build up to AIR, where no Mac is needed (./build.sh --gpu-air; CI's
# Linux runners): the bitcode as text, by the WASI SDK's clang. ll2air then
# runs on the host.
build_gpu_ir() {
    local name bc
    check_wasm
    build_gpu_bitcode "$@"
    for name in "$@"; do
        rm -rf "$GPU_OBJ/$name/ll" && mkdir -p "$GPU_OBJ/$name/ll"
        for bc in "$GPU_OBJ/$name/$name".*.bc; do
            "$WASI_SDK/bin/clang" -Wno-override-module -S -emit-llvm "$bc" -o "$GPU_OBJ/$name/ll/$(basename "${bc%.bc}").ll"
        done
    done
}

# Apple's Metal compiler, which Xcode downloads separately.
check_metal() {
    if ! xcrun -f metal > /dev/null 2>&1 || ! xcrun -f metallib > /dev/null 2>&1; then
        echo "Error: no Metal compiler. Install Xcode, then: xcodebuild -downloadComponent MetalToolchain" >&2
        exit 1
    fi
}

# $1: clang; the rest: samples.
build_gpu_link() {
    local clang=$1 name obj
    shift
    build_mac_runtime "$clang"
    for name in "$@"; do
        obj=$GPU_OBJ/$name
        # The program's [RuntimeExport] methods -> AIR -> a metallib, in
        # kernels.o; and gpu/metal.m -> metal.o (sdk/ZeroCSharp.Bitcode/Gpu.cs).
        echo "[gpu]   $name kernels -> $obj/kernels.metallib"
        zero_bitcode gpu-kernels "$obj" "$name" gpu/metal.m "$obj"
        echo "[ld]    $name -> bin/gpu/$name"
        "$clang" -arch arm64 -o "bin/gpu/$name" "$MAC_OBJ/$name.o" "$MAC_OBJ/start.o" "$MAC_OBJ/runtime.o" \
            "$MAC_OBJ/zerocore.o" "$obj/metal.o" "$obj/kernels.o" -framework Metal -framework Foundation -Wl,-dead_strip
        if [ -z "${KEEP_SYMBOLS:-}" ]; then strip "bin/gpu/$name"; fi
    done
}

# ---- UEFI --------------------------------------------------------------------
#
# Programs the firmware starts, before any operating system: bin/uefi/*.efi,
# for x64 or arm64 (BFLAT_ARCH). bflat compiles and links them (--os uefi: the
# Windows calling convention, an EFI application, no import libraries), and
# efi/ stands in for the C library, which doesn't exist there:
#   efi/start.c, efi/<arch>.S   the entry point, arguments, a larger stack
#   efi/runtime.c               memory, write/read, the clock, pthreads, abort,
#                               on top of the firmware's boot services
#   efi/machine.c               what lib/Uefi.cs calls: screen, keys, files
#   efi/libm/                   the math functions, from musl (efi/libm/COPYRIGHT)
#   zerocore/native/win-<arch>.S  zerocore's routines, Windows calling convention
# clang compiles them on the host (Xcode's on macOS), like win/, and bflat's
# linker bundles libm into a library, so a program only gets what it calls.
UEFI_OBJ=bin/uefi/obj-$BFLAT_ARCH
# Each function in its own section, so the linker leaves out what isn't called.
UEFI_CFLAGS=(-O2 -ffreestanding -fno-builtin -fno-stack-protector -fno-math-errno -ffunction-sections -fdata-sections)

uefi_triple() {
    case "$BFLAT_ARCH" in
        x64)   echo x86_64-pc-windows-msvc ;;
        arm64) echo aarch64-pc-windows-msvc ;;
    esac
}

# $1: clang
build_uefi_objects() {
    local clang=$1 triple name source object
    triple=$(uefi_triple)
    mkdir -p "$UEFI_OBJ/libm"
    echo "[clang] efi/{start,runtime,machine}.c, efi/$BFLAT_ARCH.S, zerocore/native/win-$BFLAT_ARCH.S -> $UEFI_OBJ"
    for name in start runtime machine; do
        "$clang" -target "$triple" "${UEFI_CFLAGS[@]}" -Wall -Wextra -c "efi/$name.c" -o "$UEFI_OBJ/$name.obj"
    done
    "$clang" -target "$triple" -c "efi/$BFLAT_ARCH.S" -o "$UEFI_OBJ/stack.obj"
    # ZERO_UEFI: no thread information block for the fiber switch to update.
    "$clang" -target "$triple" -DZERO_UEFI -c "zerocore/native/win-$BFLAT_ARCH.S" -o "$UEFI_OBJ/zerocore.obj"
    echo "[clang] efi/libm/*.c -> $UEFI_OBJ/libm"
    for source in efi/libm/*.c; do
        object=$UEFI_OBJ/libm/$(basename "$source" .c).obj
        if [ "$source" -nt "$object" ] || [ efi/libm/include/libm.h -nt "$object" ]; then
            # -w: musl's code, kept as it is
            "$clang" -target "$triple" "${UEFI_CFLAGS[@]}" -std=c99 -w -Iefi/libm/include -c "$source" -o "$object"
        fi
    done
}

# $1: a folder of objects (build_uefi_objects's), whose libm/*.obj become libm.lib
build_uefi_lib() {
    check_bflat
    echo "[lld]   $1/libm/*.obj -> $1/libm.lib"
    "$BFLAT_DIR/bin/lld" -flavor link /lib /out:"$1/libm.lib" "$1"/libm/*.obj
}

build_uefi() {
    local name
    check_bflat
    build_core "$CORE"
    build_lib
    build_uefi_lib "$UEFI_OBJ"
    for name in "$@"; do
        echo "[bflat] $name -> bin/uefi/$name.efi"
        # shellcheck disable=SC2046
        "$BFLAT_DIR/bflat" build $(sample_sources "$name") "${LIB_REFS[@]}" -r "$CORE" --stdlib:none \
            "${CSHARP_FLAGS[@]}" "${NATIVE_FLAGS[@]}" --os uefi --arch "$BFLAT_ARCH" $(bflat_flags "$name") \
            --ldflags "$UEFI_OBJ/start.obj $UEFI_OBJ/runtime.obj $UEFI_OBJ/machine.obj $UEFI_OBJ/stack.obj $UEFI_OBJ/zerocore.obj $UEFI_OBJ/libm.lib" \
            -o "bin/uefi/$name.efi"
        rm -f "bin/uefi/$name.pdb"
    done
}

# ---- The SDK's payload -------------------------------------------------------
#
# What the ZeroCSharp.Sdk package (sdk/) carries, so that users build without
# this repository: zerocore and lib/ as IL assemblies, zerocore's assembly
# routines and a native library's startup (library-*.S) for each Linux
# architecture, and win/ and mac/'s C files (start, runtime, library) and the Windows
# routines as COFF objects. $1: the payload directory.
build_sdk_payload() {
    local dir=$1 arch
    check_bflat
    build_core "$CORE"
    build_lib
    mkdir -p "$dir/ref"
    cp "$CORE" "$LIB" "$dir/ref/"
    # The C64: zerocore's build for bitcode targets (single-threaded, 32-bit:
    # the WebAssembly one), which NativeAOT-LLVM compiles against, and the
    # native half llvm-mos links in.
    build_core "$dir/native/c64/System.Private.CoreLib.dll" -d WASM -d C64
    cp c64/runtime.c "$dir/native/c64/"
    # The microcontrollers (pico2, r4): the same zerocore, and arm/'s runtime,
    # boot block and board layouts, which zero-bitcode arm-link compiles.
    mkdir -p "$dir/native/arm"
    cp "$dir/native/c64/System.Private.CoreLib.dll" "$dir/native/arm/"
    cp arm/runtime.c arm/semihost.c arm/rp2350.c arm/rp2350.S arm/an505.ld.in arm/an386.ld.in arm/rp2350.ld.in "$dir/native/arm/"
    # GPU kernels (ZeroGpu): compiled against that same zerocore, and Metal
    # behind lib/Gpu.cs's C functions, as source: it needs Apple's headers, so
    # it's compiled on the Mac that builds the program.
    mkdir -p "$dir/native/gpu"
    cp gpu/metal.m "$dir/native/gpu/"
    for arch in x64 arm64; do
        mkdir -p "$dir/native/linux-$arch"
        BFLAT_ARCH=$arch CORE_NATIVE=$dir/native/linux-$arch/zerocore.o \
            LIBRARY_NATIVE=$dir/native/linux-$arch/library.o build_core_native
    done
}

# tests/must-not-compile/*.cs: programs the compiler must refuse, each with
# the error code it must give on its first line (// expect: CS8352). They
# check guarantees that rest on the compiler, such as Arena.Scratch's: that
# nothing allocated from a ScratchScope can outlive the callback. Prints ok or
# FAIL for each; the exit code is the number of failures.
build_must_not_compile() {
    local file name expected out failures=0
    check_bflat
    build_core "$CORE" > /dev/null
    build_lib > /dev/null
    for file in tests/must-not-compile/*.cs; do
        name=$(basename "$file" .cs)
        expected=$(sed -n '1s|^// expect: ||p' "$file")
        if out=$("$BFLAT_DIR/bflat" build-il "$file" "${LIB_REFS[@]}" -r "$CORE" --stdlib:none "${CSHARP_FLAGS[@]}"                 -o /tmp/must-not-compile.dll 2>&1); then
            echo "FAIL must-not-compile $name: it compiled"
            failures=$((failures + 1))
        elif [ -z "$expected" ] || [[ "$out" != *"error $expected"* ]]; then
            echo "FAIL must-not-compile $name: not refused with ${expected:-(no // expect: line)}"
            echo "$out" | grep -m 2 "error" | sed 's/^/       /'
            failures=$((failures + 1))
        else
            echo "ok   must-not-compile $name ($expected)"
        fi
    done
    return "$failures"
}

# ---- CUDA (experimental) ----------------------------------------------------
#
# The GPU kernels again, for NVIDIA's GPUs: the same bitcode as --gpu-air,
# rewritten for LLVM's NVPTX backend (zero-bitcode ll2ptx, Ptx.cs), compiled
# to PTX by llc, and checked by NVIDIA's ptxas, which needs no GPU. --cuda then
# links a Linux program with the PTX in it and gpu/cuda.c, which runs it
# through the CUDA driver (libcuda, opened at run time).
#
# Untested on hardware: CI checks that the PTX assembles and the program
# links, but no kernel has yet run on an NVIDIA GPU.
#
# llc (with the NVPTX target), ptxas and a C compiler come from the PATH when
# there (CI: apt's llvm-15 and pip's nvidia-cuda-nvcc-cu12), else from the
# zero-cuda-tools image (docker/cuda.Dockerfile), built on first use.
CUDA_OBJ=bin/cuda/obj
CUDA_ARCH=${CUDA_ARCH:-sm_70}

cuda_tools() {
    if command -v ptxas >/dev/null 2>&1 && command -v llc >/dev/null 2>&1 && llc --version | grep -q nvptx64; then
        bash -c "$1"
        return
    fi
    if ! docker image inspect zero-cuda-tools >/dev/null 2>&1; then
        echo "[cuda]  building the zero-cuda-tools image (docker/cuda.Dockerfile)"
        docker build -q --platform "$DOCKER_PLATFORM" -t zero-cuda-tools -f docker/cuda.Dockerfile docker >/dev/null
    fi
    docker run --rm --platform "$DOCKER_PLATFORM" -v "$PWD:/w" -w /w zero-cuda-tools bash -c "$1" 2>&1 \
        | grep -v -e '^$' -e '^==' -e 'NVIDIA' -e 'CUDA Version' -e 'governed' -e 'license' -e 'Container' \
            -e 'WARNING: The NVIDIA Driver' -e 'Use the NVIDIA' -e 'docs.nvidia' -e 'is made available' || true
}

# $@: samples. The kernels as PTX, checked by ptxas: $CUDA_OBJ/<name>/kernels.ptx.
build_cuda_ptx() {
    local name dir
    for name in "$@"; do
        dir="$CUDA_OBJ/$name"
        mkdir -p "$dir"
        zero_bitcode ll2ptx "$dir/kernels.ll" "$name" "$GPU_OBJ/$name"/ll/*.ll
        cuda_tools "llc -march=nvptx64 -mcpu=$CUDA_ARCH -O3 $dir/kernels.ll -o $dir/kernels.ptx \
            && ptxas -arch=$CUDA_ARCH $dir/kernels.ptx -o $dir/kernels.cubin"
        [ -f "$dir/kernels.cubin" ] || { echo "Error: ptxas failed for $name" >&2; exit 1; }
        echo "[cuda]  $name: $(grep -c '^\.visible \.entry' "$dir/kernels.ptx") kernels -> $dir/kernels.ptx ($CUDA_ARCH, checked by ptxas)"
    done
}

# $@: samples. gpu/cuda.c and the PTX (zero-terminated, as cuModuleLoadData
# wants it) as objects for the program.
build_cuda_objects() {
    local name dir
    for name in "$@"; do
        dir="$CUDA_OBJ/$name"
        printf '\t.section .rodata\n\t.globl gpu_ptx\n\t.p2align 4\ngpu_ptx:\n\t.incbin "%s"\n\t.byte 0\n' "$dir/kernels.ptx" > "$dir/kernels.s"
        # arm64 gcc calls libgcc for atomics unless told to inline them; bflat's
        # link has no libgcc.
        local flags=""
        [ "$BFLAT_ARCH" = arm64 ] && flags=-mno-outline-atomics
        cuda_tools "cc -O2 -Wall -Wextra $flags -c gpu/cuda.c -o $dir/cuda.o && as $dir/kernels.s -o $dir/kernels.o"
    done
}

# $@: samples. Each program, linked with its kernels and gpu/cuda.c (in the container).
build_cuda_link() {
    local name
    check_bflat
    build_core "$CORE"
    build_core_native
    build_lib
    mkdir -p bin/cuda
    for name in "$@"; do
        echo "[bflat] $name -> bin/cuda/$name (experimental: untested on an NVIDIA GPU)"
        # shellcheck disable=SC2046
        "$BFLAT_DIR/bflat" build $(sample_sources "$name") "${LIB_REFS[@]}" -r "$CORE" --stdlib:none "${CSHARP_FLAGS[@]}" \
            "${NATIVE_FLAGS[@]}" --arch "$BFLAT_ARCH" $(bflat_flags "$name") "${LINK_FLAGS[@]}" \
            --ldflags "$CUDA_OBJ/$name/cuda.o" --ldflags "$CUDA_OBJ/$name/kernels.o" --ldflags -ldl -o "bin/cuda/$name"
    done
}

# Internal entry points used inside the container.
if [ "${1:-}" = "--native-must-not-compile" ]; then
    shift
    build_must_not_compile
    exit $?
fi
if [ "${1:-}" = "--native-dos-bitcode" ]; then
    shift
    build_dos_bitcode "$@"
    exit 0
fi
if [ "${1:-}" = "--native-dos-link" ]; then
    shift
    build_dos_link "$@"
    exit 0
fi
if [ "${1:-}" = "--native-c64-bitcode" ]; then
    shift
    build_c64_bitcode "$@"
    exit 0
fi
if [ "${1:-}" = "--native-arm-bitcode" ]; then
    shift
    build_arm_bitcode "$@"
    exit 0
fi
if [ "${1:-}" = "--native" ]; then
    shift
    build_native "$@"
    exit 0
fi
if [ "${1:-}" = "--native-gpu-ir" ]; then
    shift
    build_gpu_ir "$@"
    exit 0
fi
if [ "${1:-}" = "--native-gpu-bitcode" ]; then
    shift
    build_gpu_bitcode "$@"
    exit 0
fi
if [ "${1:-}" = "--native-cuda-link" ]; then
    shift
    build_cuda_link "$@"
    exit 0
fi
if [ "${1:-}" = "--native-mac-objects" ]; then
    shift
    build_mac_objects "$@"
    exit 0
fi
if [ "${1:-}" = "--native-win" ]; then
    shift
    build_win "$@"
    exit 0
fi
if [ "${1:-}" = "--native-android" ]; then
    shift
    build_android "$@"
    exit 0
fi
if [ "${1:-}" = "--native-uefi" ]; then
    shift
    build_uefi "$@"
    exit 0
fi
if [ "${1:-}" = "--native-uefi-lib" ]; then
    shift
    build_uefi_lib "$@"
    exit 0
fi
if [ "${1:-}" = "--native-sdk-payload" ]; then
    shift
    build_sdk_payload "$@"
    exit 0
fi
if [ "${1:-}" = "--native-wasm" ]; then
    shift
    build_wasm "$@"
    exit 0
fi

TARGET=native
case "${1:-}" in
    --wasm) TARGET=wasm; shift ;;
    --dos)  TARGET=dos; shift ;;
    --c64)  TARGET=c64; shift ;;
    --pico2) TARGET=pico2; shift ;;
    --r4)   TARGET=r4; shift ;;
    --win)  TARGET=win; shift ;;
    --mac)  TARGET=mac; shift ;;
    --ios)  TARGET=ios; shift ;;
    --android) TARGET=android; shift ;;
    --gpu)  TARGET=gpu; shift ;;
    --gpu-air) TARGET=gpu-air; shift ;;
    --cuda-ptx) TARGET=cuda-ptx; shift ;;
    --cuda) TARGET=cuda; shift ;;
    --uefi) TARGET=uefi; shift ;;
    --must-not-compile) TARGET=must-not-compile; shift ;;
    --sdk-payload)
        TARGET=sdk-payload
        PAYLOAD_DIR=${2:?--sdk-payload needs a directory, inside this one}
        # The directory is replaced: only an empty one or an earlier payload.
        if [ -d "$PAYLOAD_DIR" ] && [ -n "$(ls -A "$PAYLOAD_DIR")" ] && [ ! -f "$PAYLOAD_DIR/VERSIONS.txt" ]; then
            echo "Error: $PAYLOAD_DIR isn't an SDK payload (no VERSIONS.txt) and isn't empty; it would be replaced. Pick an empty or new directory." >&2
            exit 1
        fi
        shift 2 ;;
esac

if [ $# -eq 0 ]; then
    if [ "$TARGET" = dos ]; then
        set -- "${DOS_SAMPLES[@]}"
    elif [ "$TARGET" = c64 ]; then
        set -- "${C64_SAMPLES[@]}"
    elif [ "$TARGET" = pico2 ]; then
        set -- "${ARM_SAMPLES[@]}"
    elif [ "$TARGET" = r4 ]; then
        set -- "${R4_SAMPLES[@]}"
    elif [ "$TARGET" = uefi ]; then
        set -- "${UEFI_SAMPLES[@]}"
    elif [[ " gpu gpu-air cuda cuda-ptx " == *" $TARGET "* ]]; then
        set -- "${GPU_SAMPLES[@]}"
    elif [ "$TARGET" = wasm ]; then
        set --
        for name in "${ALL_SAMPLES[@]}"; do
            [[ "$NOT_WASM" == *" $name "* ]] || set -- "$@" "$name"
        done
    else
        set -- "${ALL_SAMPLES[@]}"
    fi
fi
for name in "$@"; do
    if [[ " gpu gpu-air cuda cuda-ptx " == *" $TARGET "* ]]; then
        if [[ " ${GPU_SAMPLES[*]} " != *" $name "* ]]; then
            check_sample "$name"
            echo "$name has no GPU kernels; ./build.sh --gpu builds ${GPU_SAMPLES[*]}" >&2
            exit 1
        fi
    elif [[ " ${GPU_SAMPLES[*]} " == *" $name "* ]]; then
        echo "$name runs kernels on the GPU: ./build.sh --gpu $name" >&2
        exit 1
    elif [ "$TARGET" = wasm ] && [[ "$NOT_WASM" == *" $name "* ]]; then
        echo "$name isn't built for WebAssembly: it switches stacks, which WebAssembly can't do." >&2
        exit 1
    elif [ "$TARGET" = dos ]; then
        if [[ " ${DOS_SAMPLES[*]} " != *" $name "* ]]; then
            check_sample "$name"
            echo "$name isn't built for DOS: it benchmarks SIMD instructions, which a 386 doesn't have." >&2
            exit 1
        fi
    elif [ "$TARGET" = uefi ] && [[ " ${UEFI_SAMPLES[*]} " == *" $name "* ]]; then
        :
    elif [ "$TARGET" = c64 ] && [[ " ${C64_SAMPLES[*]} " == *" $name "* ]]; then
        :
    elif { [ "$TARGET" = pico2 ] || [ "$TARGET" = r4 ]; } && [[ " ${ARM_SAMPLES[*]} " == *" $name "* ]]; then
        :
    elif [ "$TARGET" = pico2 ] || [ "$TARGET" = r4 ]; then
        check_sample "$name"
        echo "$name isn't built for a microcontroller: it is a PC program (Vga) or benchmarks SIMD instructions." >&2
        exit 1
    elif [ "$TARGET" != pico2 ] && [ "$TARGET" != r4 ] && [[ " ${ARM_SAMPLES[*]} " == *" $name "* ]] && [[ " ${ALL_SAMPLES[*]} " != *" $name "* ]]; then
        echo "$name is a microcontroller program: ./build.sh --pico2 $name or ./build.sh --r4 $name" >&2
        exit 1
    elif [[ " ${C64_SAMPLES[*]} " == *" $name "* ]] && [[ " ${ALL_SAMPLES[*]} " != *" $name "* ]]; then
        echo "$name is a Commodore 64 program: ./build.sh --c64 $name" >&2
        exit 1
    elif [[ " ${DOS_SAMPLES[*]} " == *" $name "* ]] && [[ " ${ALL_SAMPLES[*]} " != *" $name "* ]]; then
        echo "$name is a DOS and UEFI program: ./build.sh --dos $name or ./build.sh --uefi $name" >&2
        exit 1
    else
        check_sample "$name"
    fi
done

case "$TARGET" in
    wasm)   output() { echo "bin/wasm/$1.wasm"; } ;;
    dos)    output() { echo "bin/dos/$1.exe"; } ;;
    c64)    output() { echo "bin/c64/$1.prg"; } ;;
    pico2 | r4) output() { echo "bin/$TARGET/$1.elf"; } ;;
    win)    output() { echo "bin/win/$1.exe"; } ;;
    mac)    output() { echo "bin/mac/$1"; } ;;
    android) output() { echo "bin/android/$1"; } ;;
    ios)    output() { echo "bin/ios/$1"; } ;;
    gpu)    output() { echo "bin/gpu/$1"; } ;;
    gpu-air) output() { echo "bin/gpu/obj/$1/kernels.ll"; } ;;
    cuda-ptx) output() { echo "bin/cuda/obj/$1/kernels.ptx"; } ;;
    cuda)   output() { echo "bin/cuda/$1"; } ;;
    uefi)   output() { echo "bin/uefi/$1.efi"; } ;;
    native) output() { echo "bin/$1"; } ;;
esac

# Runs ./build.sh with the given arguments in the build container (macOS).
in_container() {
    # WASM_TOOLS must be inside this directory to be visible in the container.
    docker run --rm --platform "$DOCKER_PLATFORM" \
        -v "$PWD:/w" -v "$(cd "$BFLAT_DIR" && pwd):/bflat:ro" -w /w \
        -e BFLAT_DIR=/bflat -e BFLAT_ARCH="$BFLAT_ARCH" -e ARM64_MAX_PAGE_SIZE="$ARM64_MAX_PAGE_SIZE" \
        -e WASM_TOOLS="$WASM_TOOLS" -e BFLAT_EXTRA_FLAGS="${BFLAT_EXTRA_FLAGS:-}" \
        -e KEEP_SYMBOLS="${KEEP_SYMBOLS:-}" \
        "$IMAGE" ./build.sh "$@"
}

if { [ "$TARGET" = mac ] || [ "$TARGET" = ios ]; } && [ -n "${MAC_OBJECTS_PREBUILT:-}" ]; then
    run() { :; } # the objects are in bin/mac/obj already
elif [ "$(uname -s)" = Darwin ]; then
    check_bflat
    ensure_image
    run() { in_container "--native-$1" "${@:2}"; }
else
    run() { "build_${1//-/_}" "${@:2}"; }
fi

case "$TARGET" in
    sdk-payload)
        if ! WIN_CLANG=$(find_dos_clang); then
            echo "Error: no clang that can compile for x86 Windows. Install clang (Xcode's works)." >&2
            exit 1
        fi
        rm -rf "$PAYLOAD_DIR"
        mkdir -p "$PAYLOAD_DIR/native/win-x64"
        run sdk-payload "$PAYLOAD_DIR"
        # F# projects: zerocore as fsc wants it, the BCL types fsc needs, and
        # FSharp.Core. Only the .NET SDK is needed, so it runs on the host.
        fsharp/build-core.sh "$PAYLOAD_DIR/ref/System.Private.CoreLib.dll" "$PAYLOAD_DIR/ref/BflatLib.dll" "$PAYLOAD_DIR/ref/fsharp"
        build_win_objects "$WIN_CLANG"
        cp "$WIN_OBJ/start.obj" "$WIN_OBJ/runtime.obj" "$WIN_OBJ/library.obj" "$WIN_OBJ/zerocore.obj" "$PAYLOAD_DIR/native/win-x64/"
        # macOS: Mach-O objects from the same clang (no Apple headers needed),
        # and our libSystem stub, for ld64.lld to link osx-arm64 programs.
        mkdir -p "$PAYLOAD_DIR/native/osx-arm64"
        echo "[clang] mac/{start,runtime,library}.c, zerocore/native/mac-arm64.S -> $PAYLOAD_DIR/native/osx-arm64"
        for name in start runtime library; do
            "$WIN_CLANG" -target arm64-apple-macos12 -O2 -Wall -Wextra -c "mac/$name.c" -o "$PAYLOAD_DIR/native/osx-arm64/$name.o"
        done
        "$WIN_CLANG" -target arm64-apple-macos12 -c zerocore/native/mac-arm64.S -o "$PAYLOAD_DIR/native/osx-arm64/zerocore.o"
        cp mac/libSystem.tbd "$PAYLOAD_DIR/native/osx-arm64/"
        # The iOS simulator: the same C files and stub (which names both
        # platforms) for ld64.lld, and ios/launch.c, which dotnet run starts
        # the program through, as ./run.sh --ios does.
        mkdir -p "$PAYLOAD_DIR/native/iossimulator-arm64"
        echo "[clang] mac/{start,runtime}.c, ios/launch.c, zerocore/native/mac-arm64.S -> $PAYLOAD_DIR/native/iossimulator-arm64"
        for name in start runtime; do
            "$WIN_CLANG" -target "arm64-apple-ios$IOS_MIN-simulator" -O2 -Wall -Wextra -c "mac/$name.c" -o "$PAYLOAD_DIR/native/iossimulator-arm64/$name.o"
        done
        "$WIN_CLANG" -target "arm64-apple-ios$IOS_MIN-simulator" -O2 -Wall -Wextra -c ios/launch.c -o "$PAYLOAD_DIR/native/iossimulator-arm64/launch.o"
        "$WIN_CLANG" -target "arm64-apple-ios$IOS_MIN-simulator" -c zerocore/native/mac-arm64.S -o "$PAYLOAD_DIR/native/iossimulator-arm64/zerocore.o"
        cp mac/libSystem.tbd ios/boot.sh "$PAYLOAD_DIR/native/iossimulator-arm64/"
        # ZeroGpu=Cuda: gpu/cuda.c for each Linux architecture, from the same
        # clang (it declares the few libc functions it calls, so no headers).
        # arm64: atomics inline, as bflat's link has no libgcc for the calls.
        echo "[clang] gpu/cuda.c -> $PAYLOAD_DIR/native/linux-{x64,arm64}/cuda.o"
        "$WIN_CLANG" -target x86_64-linux-gnu -ffreestanding -O2 -Wall -Wextra -c gpu/cuda.c -o "$PAYLOAD_DIR/native/linux-x64/cuda.o"
        "$WIN_CLANG" -target aarch64-linux-gnu -ffreestanding -mno-outline-atomics -O2 -Wall -Wextra -c gpu/cuda.c -o "$PAYLOAD_DIR/native/linux-arm64/cuda.o"
        # zero-bitcode, which builds C64 programs from the IL (a .NET program,
        # the same on every host).
        echo "[dotnet] sdk/ZeroCSharp.Bitcode -> $PAYLOAD_DIR/tools/zero-bitcode"
        dotnet publish sdk/ZeroCSharp.Bitcode -c Release -o "$PAYLOAD_DIR/tools/zero-bitcode" -v q -nologo
        rm -f "$PAYLOAD_DIR/tools/zero-bitcode/"*.pdb
        # The [LibraryImport] generator, which the C# compiler loads.
        echo "[dotnet] sdk/ZeroCSharp.LibraryImport -> $PAYLOAD_DIR/analyzers"
        dotnet build sdk/ZeroCSharp.LibraryImport -c Release -o "$PAYLOAD_DIR/analyzers" -v q -nologo
        rm -f "$PAYLOAD_DIR/analyzers/"*.pdb "$PAYLOAD_DIR/analyzers/"*.deps.json
        # UEFI: efi/'s objects for each architecture, and libm as one library.
        for arch in x64 arm64; do
            BFLAT_ARCH=$arch UEFI_OBJ=$PAYLOAD_DIR/native/uefi-$arch.obj build_uefi_objects "$WIN_CLANG"
            BFLAT_ARCH=$arch run uefi-lib "$PAYLOAD_DIR/native/uefi-$arch.obj"
            mkdir -p "$PAYLOAD_DIR/native/uefi-$arch"
            mv "$PAYLOAD_DIR/native/uefi-$arch.obj"/{start,runtime,machine,stack,zerocore}.obj \
                "$PAYLOAD_DIR/native/uefi-$arch.obj/libm.lib" "$PAYLOAD_DIR/native/uefi-$arch/"
            rm -rf "$PAYLOAD_DIR/native/uefi-$arch.obj"
        done
        {
            echo "zero-csharp $(git rev-parse HEAD)"
            # BFLAT_COMMIT when the layouts came without their checkout (CI's cache).
            echo "bflat ${BFLAT_COMMIT:-$(git -C "${BFLAT_FORK:-../bflat}" rev-parse HEAD 2>/dev/null || echo unknown)}"
        } > "$PAYLOAD_DIR/VERSIONS.txt"
        exit 0
        ;;
    must-not-compile)
        status=0
        if [ "$(uname -s)" = Darwin ]; then in_container --native-must-not-compile || status=$?; else build_must_not_compile || status=$?; fi
        exit "$status" ;;
    wasm)   run wasm "$@" ;;
    native)
        if [ "$(uname -s)" = Darwin ]; then in_container --native "$@"; else build_native "$@"; fi ;;
    mac)
        if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
            echo "Error: --mac builds on a Mac with Apple Silicon: it links with Apple's linker." >&2
            exit 1
        fi
        run mac-objects "$@"
        build_mac_link "${MAC_CC:-clang}" "$@"
        ;;
    ios)
        if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
            echo "Error: --ios builds on a Mac with Apple Silicon: it links with Apple's linker, for the iOS simulator." >&2
            exit 1
        fi
        if ! xcrun --sdk iphonesimulator --show-sdk-path > /dev/null 2>&1; then
            echo "Error: no iOS simulator SDK. Install Xcode with the iOS platform (Xcode > Settings > Components)." >&2
            exit 1
        fi
        build_bitcode_tool
        run mac-objects "$@"
        build_ios_link "${MAC_CC:-clang}" "$@"
        ;;
    gpu)
        if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
            echo "Error: --gpu builds on a Mac with Apple Silicon: the kernels run on its GPU, through Metal." >&2
            exit 1
        fi
        check_metal
        build_bitcode_tool
        mkdir -p bin/gpu
        run gpu-bitcode "$@"
        run mac-objects "$@"
        build_gpu_link "${MAC_CC:-clang}" "$@"
        ;;
    gpu-air)
        # Everything but Metal: what can break when zerocore, lib/ or the
        # compiler change, checked on any machine with ./setup.sh --wasm.
        build_bitcode_tool
        run gpu-ir "$@"
        for name in "$@"; do
            zero_bitcode ll2air "$GPU_OBJ/$name/kernels.ll" "$name" "$GPU_OBJ/$name"/ll/*.ll
            echo "[gpu]   $name: $(grep -c '^define void @' "$GPU_OBJ/$name/kernels.ll") kernels -> $GPU_OBJ/$name/kernels.ll"
        done
        ;;
    cuda-ptx | cuda)
        build_bitcode_tool
        run gpu-ir "$@"
        build_cuda_ptx "$@"
        if [ "$TARGET" = cuda ]; then
            build_cuda_objects "$@"
            run cuda-link "$@"
        fi
        ;;
    win)
        if ! WIN_CLANG=$(find_dos_clang); then
            echo "Error: no clang that can compile for x86 Windows. Install clang (Xcode's works)." >&2
            exit 1
        fi
        build_win_objects "$WIN_CLANG"
        run win "$@"
        ;;
    android)
        if ! ANDROID_CLANG=$(find_dos_clang); then
            echo "Error: no clang that can compile for arm64 Android. Install clang (Xcode's works)." >&2
            exit 1
        fi
        build_android_objects "$ANDROID_CLANG"
        # The container (on macOS) builds for arm64 whatever the host is.
        BFLAT_ARCH=arm64 run android "$@"
        ;;
    uefi)
        if ! UEFI_CLANG=$(find_dos_clang); then
            echo "Error: no clang that can compile for Windows targets. Install clang (Xcode's works)." >&2
            exit 1
        fi
        mkdir -p bin/uefi
        build_uefi_objects "$UEFI_CLANG"
        run uefi "$@"
        ;;
    dos)
        if ! DOS_CLANG=$(find_dos_clang); then
            echo "Error: no clang that can compile for 32-bit x86. Run ./setup.sh --dos" >&2
            exit 1
        fi
        build_bitcode_tool
        run dos-bitcode "$@"
        build_dos_objects "$DOS_CLANG" "$@"
        run dos-link "$@"
        objdump=$(dirname "$DOS_CLANG")/llvm-objdump
        [ -x "$objdump" ] || objdump=$(command -v llvm-objdump || true)
        if [ -n "$objdump" ]; then
            check_386 "$objdump" "$@"
            echo "[dos]   checked: no instruction newer than a 386/387"
        else
            echo "[dos]   (not checked for instructions newer than a 386: no llvm-objdump found)"
        fi
        ;;
    c64)
        check_c64
        build_bitcode_tool
        run c64-bitcode "$@"
        build_c64_programs "$@"
        ;;
    pico2 | r4)
        check_arm
        build_bitcode_tool
        run arm-bitcode "$@"
        build_arm_programs "$TARGET" "$@"
        print_arm_sizes "$TARGET" "$@"
        exit 0
        ;;
esac

echo
for name in "$@"; do
    printf '%-12s %8d bytes\n' "$name" "$(wc -c < "$(output "$name")")"
done
