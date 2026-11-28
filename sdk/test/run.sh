#!/usr/bin/env bash
# The tests of the Zero C# SDK packages (sdk/). Each test copies a project
# from sdk/test/fixtures into sdk/test/work, builds it with the dotnet CLI
# against the packages in sdk/feed, and checks the result.
#
# Usage:  sdk/test/run.sh                 pack (sdk/pack.sh), then run every test
#         sdk/test/run.sh name...         only these tests (names as in the list below)
# Env:    ZERO_NO_PACK=1                  use sdk/feed as it is (CI downloads it)
#         ZERO_HOSTS                      the host packages to expect (default: all,
#                                         "linux-x64 linux-arm64 win-x64"); also passed to pack.sh
#
# Exit code: the number of failed tests.
set -uo pipefail
cd "$(dirname "$0")/../.."
ROOT=$PWD
WORK=$ROOT/sdk/test/work
FEED=$ROOT/sdk/feed
VERSION=$(sed -n 's|.*<ZeroSdkVersion>\(.*\)</ZeroSdkVersion>.*|\1|p' sdk/version.props)
# The target framework: ZeroFramework (sdk/version.props), not made from the version.
TFM=$(sed -n 's|.*<ZeroFramework>\(.*\)</ZeroFramework>.*|\1|p' sdk/version.props)
case "$(uname -s)" in
    Linux)                    HOST_OS=Linux ;;
    Darwin)                   HOST_OS=Darwin ;;
    MINGW* | MSYS* | CYGWIN*) HOST_OS=Windows ;;   # Git Bash
    *)                        HOST_OS=$(uname -s) ;;
esac
export ZERO_HOSTS=${ZERO_HOSTS:-linux-x64 linux-arm64 win-x64 osx-arm64}
case "$(uname -m)" in
    x86_64 | amd64)  HOST_ARCH_NAME=x64;   DOCKER_ARCH=amd64; HOST_ELF_ARCH=x86-64 ;;
    *)               HOST_ARCH_NAME=arm64; DOCKER_ARCH=arm64; HOST_ELF_ARCH=aarch64 ;;
esac
case "$HOST_OS" in
    Windows) HOST_RID=win-x64 ;;
    Darwin)  HOST_RID=osx-$HOST_ARCH_NAME ;;     # osx-arm64; osx-x64 can't build
    *)       HOST_RID=linux-$HOST_ARCH_NAME ;;
esac

passed=0
failed=0
skipped=0
LOG=
pass() { echo "ok   $1"; passed=$((passed + 1)); }
fail() {
    echo "FAIL $1: $2"
    [ -n "$LOG" ] && [ -f "$LOG" ] && tail -20 "$LOG" | sed 's/^/     | /'
    failed=$((failed + 1))
}

skip() { echo "skip $1: $2"; skipped=$((skipped + 1)); }
# can_build <test name>: true on a host that builds native programs (Linux,
# Windows, an Apple Silicon Mac); on others, records a skip.
can_build() {
    [ "$HOST_OS" = Linux ] || [ "$HOST_OS" = Windows ] || [ "$HOST_RID" = osx-arm64 ] && return 0
    skip "$1" "builds native programs, which needs a Linux, Windows or Apple Silicon host"
    return 1
}
# linux_only <test name>: true on Linux; on other hosts, records a skip.
linux_only() {
    [ "$HOST_OS" = Linux ] && return 0
    skip "$1" "needs a Linux host"
    return 1
}
# exe <path>: the program for the host's own target (.exe on Windows).
exe() { if [ "$HOST_OS" = Windows ]; then echo "$1.exe"; else echo "$1"; fi; }
# is_elf <file> <x86-64|aarch64>, is_pe <file>, is_coff_x64 <file>: what a
# file is, from its header (Git Bash has no file command).
byte_at() { od -An -tu1 -j"$2" -N1 "$1" 2>/dev/null | tr -d ' '; }
is_elf() {
    [ "$(head -c 4 "$1" 2>/dev/null | tail -c 3)" = ELF ] || return 1
    case "$2" in x86-64) [ "$(byte_at "$1" 18)" = 62 ] ;; aarch64) [ "$(byte_at "$1" 18)" = 183 ] ;; *) return 1 ;; esac
}
is_pe() { [ "$(head -c 2 "$1" 2>/dev/null)" = MZ ]; }
is_coff_x64() { [ "$(byte_at "$1" 0)" = 100 ] && [ "$(byte_at "$1" 1)" = 134 ]; }
is_coff_arm64() { [ "$(byte_at "$1" 0)" = 100 ] && [ "$(byte_at "$1" 1)" = 170 ]; }
# A UEFI application for $2 (x64 or arm64): a PE file whose machine is that
# architecture and whose subsystem is 10 (EFI application).
is_efi_app() {
    local pe machine
    is_pe "$1" || return 1
    pe=$(od -An -tu4 -j60 -N4 "$1" | tr -d ' ')
    machine=$(od -An -tu2 -j$((pe + 4)) -N2 "$1" | tr -d ' ')
    case "$2" in
        x64)   [ "$machine" = 34404 ] || return 1 ;;
        arm64) [ "$machine" = 43620 ] || return 1 ;;
    esac
    [ "$(od -An -tu2 -j$((pe + 92)) -N2 "$1" | tr -d ' ')" = 10 ]
}
# is_macho_arm64 <file>: 64-bit Mach-O (cf fa ed fe) for arm64 (CPU type 0x0100000c).
is_macho_arm64() {
    [ "$(byte_at "$1" 0)" = 207 ] && [ "$(byte_at "$1" 1)" = 250 ] && [ "$(byte_at "$1" 2)" = 237 ] && [ "$(byte_at "$1" 3)" = 254 ] &&
        [ "$(byte_at "$1" 4)" = 12 ] && [ "$(byte_at "$1" 5)" = 0 ] && [ "$(byte_at "$1" 6)" = 0 ] && [ "$(byte_at "$1" 7)" = 1 ]
}
# macho_platform <file>: the platform its LC_BUILD_VERSION names (1 macOS,
# 2 iOS, 7 the iOS simulator), or nothing.
macho_platform() {
    local f=$1 count offset=32 i command size
    count=$(od -An -tu4 -j16 -N4 "$f" | tr -d ' ')
    for ((i = 0; i < count; i++)); do
        command=$(od -An -tu4 -j"$offset" -N4 "$f" | tr -d ' ')
        size=$(od -An -tu4 -j$((offset + 4)) -N4 "$f" | tr -d ' ')
        if [ "$command" = 50 ]; then od -An -tu4 -j$((offset + 8)) -N4 "$f" | tr -d ' '; return; fi
        [ "${size:-0}" -gt 0 ] || return
        offset=$((offset + size))
    done
}
# The host's own target's native library suffix (NativeLib=Shared).
case "$HOST_OS" in Windows) HOST_LIB_EXT=.dll ;; Darwin) HOST_LIB_EXT=.dylib ;; *) HOST_LIB_EXT=.so ;; esac
# is_elf_shared <file>, is_macho_dylib <file>, is_pe_dll <file>: a native
# library for Linux (ELF type 3, ET_DYN, which a PIE program is too, but with
# no program interpreter: a program names /lib.../ld-linux...), macOS
# (Mach-O type 6, MH_DYLIB) or Windows (PE with IMAGE_FILE_DLL, 0x2000, in its
# characteristics).
is_elf_shared() {
    [ "$(head -c 4 "$1" 2>/dev/null | tail -c 3)" = ELF ] && [ "$(byte_at "$1" 16)" = 3 ] && ! LC_ALL=C grep -aq '/ld-linux' "$1"
}
is_macho_dylib() { is_macho_arm64 "$1" && [ "$(byte_at "$1" 12)" = 6 ]; }
is_pe_dll() {
    local pe
    is_pe "$1" || return 1
    pe=$(( $(byte_at "$1" 60) + 256 * $(byte_at "$1" 61) ))
    [ $(( $(byte_at "$1" $((pe + 23))) & 32 )) -ne 0 ]
}
# is_host_program <file>: a program for the host's own target.
is_host_program() {
    case "$HOST_OS" in Windows) is_pe "$1" ;; Darwin) is_macho_arm64 "$1" ;; *) is_elf "$1" "$HOST_ELF_ARCH" ;; esac
}
# native_path <path>: the path as the dotnet CLI on this host writes it.
native_path() { if [ "$HOST_OS" = Windows ]; then cygpath -w "$1"; else echo "$1"; fi; }
# zip_list <zip>: the entries' names; zip_dir <dir> <zip>: zips a folder's contents.
zip_list() {
    if command -v unzip > /dev/null; then unzip -Z1 "$1"
    else powershell.exe -NoProfile -Command "Add-Type -A System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead('$(native_path "$1")').Entries.FullName" | tr -d '\r'; fi
}
zip_dir() {
    if command -v zip > /dev/null; then (cd "$1" && zip -qr "$2" .)
    else powershell.exe -NoProfile -Command "Add-Type -A System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('$(native_path "$1")', '$(native_path "$2")')"; fi
}

# fixture <fixture> [<dir under work>]: a fresh copy of a fixture, with the
# SDK version filled in and a nuget.config that finds the local feed.
fixture() {
    local from=$ROOT/sdk/test/fixtures/$1 to=$WORK/${2:-$1}
    rm -rf "$to"
    mkdir -p "$(dirname "$to")"
    cp -R "$from" "$to"
    find "$to" \( -name '*.csproj' -o -name '*.fsproj' \) -exec sed -i.bak "s|@ZERO_SDK_VERSION@|$VERSION|g" {} \; -exec rm -f {}.bak \;
    cat > "$to/nuget.config" <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="zero" value="$(native_path "$FEED")" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
    echo "$to"
}

# run_in <dir> <log name> dotnet args...: runs dotnet in <dir>, output to a log.
# same_output <output> <expected> <report>: whether the files hold the same
# lines (ignoring \r); if not, the report names the first line that differs.
# Plain bash (3.2 too): minimal containers (Fedora's) have no diff.
same_output() {
    local got want n=0 got_more want_more
    exec 5< <(tr -d '\r' < "$1") 6< <(tr -d '\r' < "$2")
    while :; do
        n=$((n + 1))
        got= want= got_more=1 want_more=1
        IFS= read -r got <&5 || [ -n "$got" ] || got_more=0
        IFS= read -r want <&6 || [ -n "$want" ] || want_more=0
        if [ $got_more = 0 ] && [ $want_more = 0 ]; then
            exec 5<&- 6<&-
            : > "$3"
            return 0
        fi
        [ $got_more = 1 ] || got="<missing>"
        [ $want_more = 1 ] || want="<missing>"
        if [ "$got" != "$want" ]; then
            exec 5<&- 6<&-
            printf 'line %d: got "%s", want "%s"\n' "$n" "$got" "$want" > "$3"
            return 1
        fi
    done
}

run_in() {
    local dir=$1
    LOG=$WORK/$2.log
    shift 2
    (cd "$dir" && dotnet "$@") > "$LOG" 2>&1
}

# run_limited <seconds> <output file> <program> [args]: runs it, output to the
# file, killed after <seconds> (exit 124): a deadlock fails a test instead of
# hanging it. macOS has no timeout command.
run_limited() {
    local secs=$1 out=$2 pid i=0
    shift 2
    "$@" > "$out" 2>&1 &
    pid=$!
    while kill -0 "$pid" 2> /dev/null; do
        if [ "$i" -ge $((secs * 10)) ]; then
            kill -9 "$pid" 2> /dev/null
            wait "$pid" 2> /dev/null
            return 124
        fi
        sleep 0.1
        i=$((i + 1))
    done
    wait "$pid"
}

# ---- Tests -------------------------------------------------------------------

test_hello_compiles() {
    local dir
    dir=$(fixture hello)
    if ! run_in "$dir" hello build; then fail "hello compiles" "dotnet build failed"; return; fi
    local dll=$dir/bin/Debug/$TFM/hello.dll
    if [ ! -f "$dll" ]; then fail "hello compiles" "no $dll"; return; fi
    # Metadata strings are NUL-terminated: an assembly reference is a whole one.
    if LC_ALL=C tr '\0' '\n' < "$dll" | grep -qx 'System.Runtime'; then
        fail "hello compiles" "the output references System.Runtime"; return
    fi
    pass "hello compiles"
}

test_console_is_an_error() {
    local dir
    dir=$(fixture console)
    if run_in "$dir" console build; then fail "Console.Error is an error" "the build succeeded"; return; fi
    if ! grep -q 'CS0117' "$LOG"; then fail "Console.Error is an error" "no CS0117"; return; fi
    pass "Console.Error is an error"
}

test_unions_compile() {
    local dir
    dir=$(fixture union)
    if ! run_in "$dir" union build; then fail "unions compile" "dotnet build failed"; return; fi
    pass "unions compile"
}

test_plain_packages_refused() {
    local dir
    dir=$(fixture plainpkg)
    if run_in "$dir" plainpkg restore; then fail "plain packages refused" "restore succeeded"; return; fi
    if ! grep -q 'NU1202' "$LOG"; then fail "plain packages refused" "no NU1202"; return; fi
    pass "plain packages refused"
}

test_payload_complete() {
    local payload=$ROOT/sdk/ZeroCSharp.Sdk/payload f
    LOG=
    # start: a program's entry point; library: a native library's startup
    # (NativeLib=Shared); runtime: what both share.
    for f in ref/System.Private.CoreLib.dll ref/BflatLib.dll \
             ref/fsharp/System.Runtime.dll ref/fsharp/System.Runtime.Extensions.dll ref/fsharp/FSharp.Core.dll \
             native/linux-x64/zerocore.o native/linux-x64/library.o \
             native/linux-arm64/zerocore.o native/linux-arm64/library.o \
             native/linux-x64/cuda.o native/linux-arm64/cuda.o \
             native/win-x64/start.obj native/win-x64/runtime.obj native/win-x64/library.obj native/win-x64/zerocore.obj \
             native/osx-arm64/start.o native/osx-arm64/runtime.o native/osx-arm64/library.o native/osx-arm64/zerocore.o \
             native/osx-arm64/libSystem.tbd VERSIONS.txt \
             native/iossimulator-arm64/{start,runtime,zerocore,launch}.o native/iossimulator-arm64/{libSystem.tbd,boot.sh} \
             native/uefi-x64/{start,runtime,machine,stack,zerocore}.obj native/uefi-x64/libm.lib \
             native/uefi-arm64/{start,runtime,machine,stack,zerocore}.obj native/uefi-arm64/libm.lib \
             native/c64/System.Private.CoreLib.dll native/c64/runtime.c native/gpu/metal.m \
             native/arm/System.Private.CoreLib.dll native/arm/{runtime,semihost,rp2350}.c native/arm/rp2350.S \
             native/arm/{an505,an386,rp2350}.ld.in \
             tools/zero-bitcode/zero-bitcode.dll tools/zero-bitcode/zero-bitcode.runtimeconfig.json \
             tools/zero-bitcode/SharpCompress.dll tools/zero-bitcode/THIRD-PARTY-NOTICES.txt \
             analyzers/ZeroCSharp.LibraryImport.dll; do
        if [ ! -s "$payload/$f" ]; then fail "payload complete" "missing $f"; return; fi
    done
    if ! is_elf "$payload/native/linux-x64/zerocore.o" x86-64; then fail "payload complete" "linux-x64 object isn't x86-64"; return; fi
    if ! is_elf "$payload/native/linux-arm64/zerocore.o" aarch64; then fail "payload complete" "linux-arm64 object isn't aarch64"; return; fi
    if ! is_elf "$payload/native/linux-x64/library.o" x86-64; then fail "payload complete" "linux-x64 library.o isn't x86-64"; return; fi
    if ! is_elf "$payload/native/linux-arm64/library.o" aarch64; then fail "payload complete" "linux-arm64 library.o isn't aarch64"; return; fi
    if ! is_elf "$payload/native/linux-x64/cuda.o" x86-64; then fail "payload complete" "linux-x64 cuda.o isn't x86-64"; return; fi
    if ! is_elf "$payload/native/linux-arm64/cuda.o" aarch64; then fail "payload complete" "linux-arm64 cuda.o isn't aarch64"; return; fi
    for f in start.obj runtime.obj library.obj; do
        if ! is_coff_x64 "$payload/native/win-x64/$f"; then fail "payload complete" "win-x64/$f isn't COFF"; return; fi
    done
    for f in start runtime machine stack zerocore; do
        if ! is_coff_x64 "$payload/native/uefi-x64/$f.obj"; then fail "payload complete" "uefi-x64/$f.obj isn't x64 COFF"; return; fi
        if ! is_coff_arm64 "$payload/native/uefi-arm64/$f.obj"; then fail "payload complete" "uefi-arm64/$f.obj isn't arm64 COFF"; return; fi
    done
    for f in start.o runtime.o library.o zerocore.o; do
        if ! is_macho_arm64 "$payload/native/osx-arm64/$f"; then fail "payload complete" "osx-arm64/$f isn't arm64 Mach-O"; return; fi
    done
    if ! grep -q "install-name: *'/usr/lib/libSystem.B.dylib'" "$payload/native/osx-arm64/libSystem.tbd" ||
       ! grep -q "arm64-macos" "$payload/native/osx-arm64/libSystem.tbd"; then
        fail "payload complete" "libSystem.tbd doesn't name /usr/lib/libSystem.B.dylib for arm64-macos"; return
    fi
    for f in start.o runtime.o zerocore.o launch.o; do
        if ! is_macho_arm64 "$payload/native/iossimulator-arm64/$f"; then fail "payload complete" "iossimulator-arm64/$f isn't arm64 Mach-O"; return; fi
        if [ "$(macho_platform "$payload/native/iossimulator-arm64/$f")" != 7 ]; then fail "payload complete" "iossimulator-arm64/$f isn't built for the iOS simulator"; return; fi
    done
    if ! grep -q "arm64-ios-simulator" "$payload/native/iossimulator-arm64/libSystem.tbd"; then
        fail "payload complete" "iossimulator-arm64/libSystem.tbd doesn't name arm64-ios-simulator"; return
    fi
    pass "payload complete"
}

# Trims the host's layout (sdk/trim-layout.sh) and builds with the result in a
# clean Ubuntu 24.04: nothing installed, so it must need nothing the layout or
# a stock system lacks. Slow (it traces builds in a container): run it by name.
test_trimmed_layout_builds() {
    local layout=${BFLAT_DIR:-$ROOT/../bflat/layouts/linux-glibc-$HOST_ARCH_NAME} out=$WORK/trimmed size
    LOG=$WORK/trim.log
    if ! sdk/trim-layout.sh "$layout" "$out" > "$LOG" 2>&1; then fail "trimmed layout builds" "trim-layout.sh failed"; return; fi
    size=$(du -sm "$out" | cut -f1)
    if [ "$size" -ge 150 ]; then fail "trimmed layout builds" "$size MB, not under 150"; return; fi
    LOG=$WORK/trim-build.log
    if ! docker run --rm --platform "linux/$DOCKER_ARCH" -v "$ROOT:/w" -v "$out:/bflat:ro" -w /w ubuntu:24.04 bash -c '
            set -e
            P=sdk/ZeroCSharp.Sdk/payload
            F="--stdlib:none --langversion preview --compiler-feature updated-memory-safety-rules --no-reflection -i libc -i libm.so.6"
            B="/bflat/bflat build samples/Strings.cs -r $P/ref/BflatLib.dll -r $P/ref/System.Private.CoreLib.dll $F"
            $B --arch arm64 --ldflags $P/native/linux-arm64/zerocore.o --ldflags "-z nostart-stop-gc" -o /tmp/s-arm64
            $B --arch x64 --ldflags $P/native/linux-x64/zerocore.o --ldflags "-z nostart-stop-gc" -o /tmp/s-x64
            $B --os windows --arch x64 --ldflags "/entry:zero_start $P/native/win-x64/start.obj $P/native/win-x64/runtime.obj $P/native/win-x64/zerocore.obj" -o /tmp/s.exe
            /tmp/s-'"$HOST_ARCH_NAME"' > /dev/null' > "$LOG" 2>&1; then
        fail "trimmed layout builds" "building or running in ubuntu:24.04 failed"; return
    fi
    pass "trimmed layout builds ($size MB)"
}

test_host_packages() {
    local rid pkg size listing
    LOG=
    # Checked where the packages are made (Linux CI and the Mac); the Windows
    # job only builds with them.
    if [ "$HOST_OS" = Windows ]; then skip "host packages" "checked on Linux"; return; fi
    for rid in $ZERO_HOSTS; do
        pkg=$FEED/ZeroCSharp.Bflat.$rid.$VERSION.nupkg
        if [ ! -f "$pkg" ]; then fail "host packages" "no $(basename "$pkg")"; return; fi
        size=$(( $(wc -c < "$pkg") / 1000000 ))
        if [ "$size" -ge 100 ]; then fail "host packages" "$(basename "$pkg") is $size MB, not under 100"; return; fi
        # Listed once: grep -q stops reading early, which pipefail counts as a failure.
        listing=$(zip_list "$pkg" | sed 's/^/ /')
        local x=; [ "$rid" = win-x64 ] && x=.exe
        if ! grep -q " tools/bflat/bflat$x\$" <<< "$listing"; then fail "host packages" "$rid: no tools/bflat/bflat$x"; return; fi
        if ! grep -q " tools/bflat/bin/lld$x\$" <<< "$listing"; then fail "host packages" "$rid: no tools/bflat/bin/lld$x"; return; fi
        # The AGPL package must point to its exact source.
        if ! unzip -p "$pkg" "ZeroCSharp.Bflat.$rid.nuspec" | grep -qE 'commit="[0-9a-f]{40}"'; then
            fail "host packages" "$rid: the nuspec doesn't name the fork's commit"; return
        fi
    done
    pass "host packages ($ZERO_HOSTS)"
}

# ---- The native step (Linux hosts) -----------------------------------------

test_native_hello_runs() {
    local name="native hello runs" dir exe out
    can_build "$name" || return
    dir=$(fixture native)
    if ! run_in "$dir" native build; then fail "$name" "dotnet build failed"; return; fi
    exe=$(exe "$dir/bin/Debug/$TFM/native")
    if [ ! -x "$exe" ]; then fail "$name" "no $exe"; return; fi
    out=$("$exe"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    if ! is_host_program "$exe"; then fail "$name" "not a program for $HOST_RID"; return; fi
    pass "$name"
}

test_cross_targets() {
    local name="cross targets" dir other other_arch
    can_build "$name" || return
    dir=$(fixture native cross)
    if ! run_in "$dir" cross-win build -p:ZeroTarget=win-x64; then fail "$name" "win-x64 build failed"; return; fi
    if ! is_pe "$dir/bin/Debug/$TFM/win-x64/native.exe"; then fail "$name" "native.exe isn't a Windows program"; return; fi
    if [ "$HOST_ARCH_NAME" = x64 ]; then other=linux-arm64; other_arch=aarch64; else other=linux-x64; other_arch=x86-64; fi
    if ! run_in "$dir" cross-linux build -p:ZeroTarget=$other; then fail "$name" "$other build failed"; return; fi
    if ! is_elf "$dir/bin/Debug/$TFM/$other/native" "$other_arch"; then fail "$name" "not an ELF for $other_arch"; return; fi
    for other in x64 arm64; do
        if ! run_in "$dir" cross-uefi-$other build -p:ZeroTarget=uefi-$other; then fail "$name" "uefi-$other build failed"; return; fi
        if ! is_efi_app "$dir/bin/Debug/$TFM/uefi-$other/native.efi" $other; then fail "$name" "native.efi isn't a UEFI application for $other"; return; fi
    done
    pass "$name"
}

# ZeroTarget=c64: the first build downloads NativeAOT-LLVM and llvm-mos into
# ZERO_TOOLS_CACHE (zero-bitcode's Tools.cs). A .prg starts with the address it
# loads at, $0801, where BASIC programs start. Linux x64 and Windows x64 run
# the compiler themselves; a Mac runs it in Docker, and Linux arm64 has no
# llvm-mos, which the build says.
test_c64_target() {
    local name="c64 target" dir prg
    can_build "$name" || return
    dir=$(fixture native c64)
    case "$HOST_RID" in
        linux-x64|win-x64) ;;
        osx-arm64)
            if ! docker info > /dev/null 2>&1; then skip "$name" "on a Mac the C64 needs Docker, which isn't running"; return; fi ;;
        linux-arm64)
            if run_in "$dir" c64 build -p:ZeroTarget=c64; then fail "$name" "the build succeeded on linux-arm64"; return; fi
            if ! grep -q "llvm-mos, the compiler for the 6502, has no build for Linux arm64" "$LOG"; then fail "$name" "no message about Linux arm64"; return; fi
            pass "$name"
            return ;;
        *) skip "$name" "not a host for the C64"; return ;;
    esac
    if ! run_in "$dir" c64 build -p:ZeroTarget=c64; then fail "$name" "dotnet build failed"; return; fi
    prg=$dir/bin/Debug/$TFM/c64/native.prg
    if [ ! -s "$prg" ]; then fail "$name" "no $prg"; return; fi
    if [ "$(head -c 2 "$prg" | od -An -tx1 | tr -d ' \n')" != 0108 ]; then fail "$name" "native.prg doesn't load at \$0801"; return; fi
    # A second build has nothing to do.
    if ! run_in "$dir" c64-again build -p:ZeroTarget=c64 -v:n; then fail "$name" "the second build failed"; return; fi
    if grep -q "c64-bitcode" "$LOG"; then fail "$name" "the second build compiled again"; return; fi
    # The project's options for the C64's stacks and multiplication reach
    # the link, and changing them links again.
    if ! run_in "$dir" c64-options build -p:ZeroTarget=c64 -p:ZeroC64FastMultiply=true -p:ZeroC64ShadowStack=512 -p:ZeroC64StackReserve=768 -v:n; then fail "$name" "the build with C64 options failed"; return; fi
    if ! grep -q -- "--shadow-stack 512 --stack-reserve 768 --fast-multiply" "$LOG"; then fail "$name" "the C64 options didn't reach the link"; return; fi
    pass "$name"
}

# ZeroGpu=Metal: dotnet new zerogpu makes a program whose C# kernels run on
# the GPU, and which checks them against the CPU itself (exit code 0). It
# builds on an Apple Silicon Mac with Xcode's Metal Toolchain and Docker
# (NativeAOT-LLVM, as for the C64). The project's name has a dot, which
# NativeAOT's symbol names turn into _, where the build finds the kernels.
test_gpu_template() {
    local name="gpu template" dir=$WORK/gputemplate app out
    can_build "$name" || return
    if [ "$HOST_RID" != osx-arm64 ]; then skip "$name" "needs an Apple Silicon Mac"; return; fi
    if ! xcrun -f metal > /dev/null 2>&1; then skip "$name" "no Metal compiler (xcodebuild -downloadComponent MetalToolchain)"; return; fi
    if ! docker info > /dev/null 2>&1; then skip "$name" "on a Mac the kernels need Docker, which isn't running"; return; fi
    rm -rf "$dir" && mkdir -p "$dir"
    fixture hello gputemplate-config > /dev/null    # just for its nuget.config
    cp "$WORK/gputemplate-config/nuget.config" "$dir/"
    dotnet new uninstall ZeroCSharp.Templates > /dev/null 2>&1 || true
    if ! run_in "$dir" gpu-install new install "$FEED/ZeroCSharp.Templates.$VERSION.nupkg"; then fail "$name" "install failed"; return; fi
    if ! run_in "$dir" gpu-new new zerogpu -o Gpu.Kernels; then fail "$name" "dotnet new zerogpu failed"; return; fi
    app=$dir/Gpu.Kernels
    if ! grep -q "ZeroCSharp.Sdk/$VERSION" "$app/Gpu.Kernels.csproj"; then fail "$name" "the project isn't filled in"; return; fi
    if ! run_in "$app" gpu-build build; then fail "$name" "dotnet build failed"; return; fi
    if ! out=$("$app/bin/Debug/$TFM/Gpu.Kernels" 2>&1); then fail "$name" "the program failed: $out"; return; fi
    if [[ "$out" != *"Saxpy: 1048576 elements"*", 0 wrong"* ]] || [[ "$out" != *"Sum:   4718580 (the CPU says 4718580)"* ]]; then
        fail "$name" "printed '$out'"; return
    fi
    # A second build has nothing to compile; a changed kernel compiles again.
    if ! run_in "$app" gpu-again build -v:n; then fail "$name" "the second build failed"; return; fi
    if grep -q "gpu-bitcode" "$LOG"; then fail "$name" "the second build compiled the kernels again"; return; fi
    sed -i.bak 's/y\[i\] = a \* x\[i\] + y\[i\]/y[i] = a * x[i] + y[i] + 1/' "$app/Program.cs" && rm -f "$app/Program.cs.bak"
    if ! run_in "$app" gpu-changed build -v:n; then fail "$name" "the build after a change failed"; return; fi
    if ! grep -q "gpu-bitcode" "$LOG"; then fail "$name" "a changed kernel wasn't compiled again"; return; fi
    out=$("$app/bin/Debug/$TFM/Gpu.Kernels" 2>&1)
    if [[ "$out" != *"Saxpy: 1048576 elements"*", 1048576 wrong"* ]]; then fail "$name" "the changed kernel printed '$out'"; return; fi
    pass "$name"
}

# What ZeroGpu=Metal says where it can't build, on any host.
test_gpu_errors() {
    local name="gpu errors" dir
    can_build "$name" || return
    dir=$(fixture native gpuerrors)
    if run_in "$dir" gpu-vulkan build -p:ZeroGpu=Vulkan; then fail "$name" "ZeroGpu=Vulkan built"; return; fi
    if ! grep -q "ZeroGpu 'Vulkan' isn't supported. Use Metal" "$LOG"; then fail "$name" "no message about Vulkan"; return; fi
    if run_in "$dir" gpu-linux build -p:ZeroGpu=Metal -p:ZeroTarget=linux-x64; then fail "$name" "ZeroGpu=Metal built for linux-x64"; return; fi
    if ! grep -q "ZeroGpu=Metal builds for ZeroTarget osx-arm64" "$LOG"; then fail "$name" "no message about the target"; return; fi
    if [ "$HOST_RID" != osx-arm64 ]; then
        if run_in "$dir" gpu-host build -p:ZeroGpu=Metal -p:ZeroTarget=osx-arm64; then fail "$name" "ZeroGpu=Metal built off a Mac"; return; fi
        if ! grep -q "ZeroGpu=Metal builds on an Apple Silicon Mac" "$LOG"; then fail "$name" "no message about the host"; return; fi
    fi
    pass "$name"
}

# ZeroGpu=Cuda (experimental): the zerogpu template's program, built for
# NVIDIA GPUs on Linux. It needs a clang 18 or later with the NVPTX backend
# (Ubuntu 24.04's runners have one) and NativeAOT-LLVM (Docker on a Mac). No
# runner has an NVIDIA GPU, so on Linux the program must say there's no
# driver and exit 1, which checks that it links and starts; the kernels are
# checked as PTX (and by ptxas, where there is one) while it builds.
test_cuda_build() {
    local name="cuda build" dir=$WORK/cudabuild app out target c clang=
    can_build "$name" || return
    for c in clang clang-22 clang-21 clang-20 clang-19 clang-18 /opt/homebrew/opt/llvm/bin/clang; do
        if "$c" --print-targets 2>/dev/null | grep -q nvptx64 \
            && [ "$("$c" --version | sed -n 's/.*clang version \([0-9]*\).*/\1/p' | head -1)" -ge 18 ] 2>/dev/null; then
            clang=$c; break
        fi
    done
    if [ -z "$clang" ]; then skip "$name" "no clang 18 or later with the NVPTX backend"; return; fi
    if [ "$HOST_RID" = osx-arm64 ] && ! docker info > /dev/null 2>&1; then skip "$name" "on a Mac the kernels need Docker, which isn't running"; return; fi
    case "$HOST_RID" in linux-arm64) target=linux-arm64 ;; *) target=linux-x64 ;; esac
    rm -rf "$dir" && mkdir -p "$dir"
    fixture hello cudabuild-config > /dev/null    # just for its nuget.config
    cp "$WORK/cudabuild-config/nuget.config" "$dir/"
    dotnet new uninstall ZeroCSharp.Templates > /dev/null 2>&1 || true
    if ! run_in "$dir" cuda-install new install "$FEED/ZeroCSharp.Templates.$VERSION.nupkg"; then fail "$name" "install failed"; return; fi
    if ! run_in "$dir" cuda-new new zerogpu -o Cuda.Kernels; then fail "$name" "dotnet new zerogpu failed"; return; fi
    app=$dir/Cuda.Kernels
    if ! run_in "$app" cuda-build build -p:ZeroGpu=Cuda -p:ZeroTarget=$target -p:ZeroCudaClang="$clang"; then fail "$name" "dotnet build failed"; return; fi
    out=$app/bin/Debug/$TFM/$target/Cuda.Kernels
    [ -f "$out" ] || out=$app/bin/Debug/$TFM/Cuda.Kernels
    if [ ! -f "$out" ]; then fail "$name" "no program"; return; fi
    if ! grep -q "Saxpy" "$app/obj/Debug/$TFM/$target/gpu/kernels.ptx" 2>/dev/null \
        && ! grep -rq "\.entry Saxpy" "$app/obj" 2>/dev/null; then fail "$name" "no Saxpy kernel in the PTX"; return; fi
    if [ "$HOST_OS" = Linux ] && [ "$HOST_RID" = "$target" ]; then
        if out=$("$out" 2>&1); then fail "$name" "the program said it ran on a GPU: $out"; return; fi
        if [[ "$out" != *"gpu: no NVIDIA driver"* ]] || [[ "$out" != *"No GPU."* ]]; then fail "$name" "printed '$out'"; return; fi
    fi
    pass "$name"
}

# What ZeroGpu=Cuda says where it can't build, on any host.
test_cuda_errors() {
    local name="cuda errors" dir
    can_build "$name" || return
    dir=$(fixture native cudaerrors)
    if run_in "$dir" cuda-mac build -p:ZeroGpu=Cuda -p:ZeroTarget=osx-arm64; then fail "$name" "ZeroGpu=Cuda built for osx-arm64"; return; fi
    if ! grep -q "ZeroGpu=Cuda builds for ZeroTarget linux-x64 or linux-arm64" "$LOG"; then fail "$name" "no message about the target"; return; fi
    # The clang is checked before NativeAOT-LLVM runs, so this one is quick.
    if run_in "$dir" cuda-clang build -p:ZeroGpu=Cuda -p:ZeroTarget=linux-x64 -p:ZeroCudaClang=no-such-clang; then fail "$name" "built with no clang"; return; fi
    if ! grep -q "ZeroCudaClang no-such-clang: not found" "$LOG"; then fail "$name" "no message about the clang"; return; fi
    if grep -q "gpu-bitcode" "$LOG"; then fail "$name" "NativeAOT-LLVM ran before the clang was checked"; return; fi
    pass "$name"
}

# ZeroTarget=pico2 and r4: the first build downloads NativeAOT-LLVM and Arm
# Toolchain for Embedded into ZERO_TOOLS_CACHE (zero-bitcode's Tools.cs); a
# Mac runs both in Docker. The pico2 build makes the real board's UF2 (the
# RP2350's family, the vector table first and the boot ROM's IMAGE_DEF block
# at 0x40), its ELF, and QEMU's ELF; the r4's is for QEMU. Where
# qemu-system-arm is installed, dotnet run runs each there: a C# interrupt
# handler counts ten ticks and turns the LED over each time.
test_arm_target() {
    local name="microcontroller targets" dir out target words
    can_build "$name" || return
    if [ "$HOST_RID" = osx-arm64 ] && ! docker info > /dev/null 2>&1; then
        skip "$name" "on a Mac the microcontrollers need Docker, which isn't running"; return
    fi
    dir=$(fixture blinky)
    if ! run_in "$dir" pico2 build -p:ZeroTarget=pico2; then fail "$name" "the pico2 build failed"; return; fi
    out=$dir/bin/Debug/$TFM/pico2
    for file in blinky.uf2 blinky.elf qemu/blinky.elf; do
        if [ ! -s "$out/$file" ]; then fail "$name" "no pico2/$file"; return; fi
    done
    # The first UF2 block: its magic, the family, and the IMAGE_DEF block in
    # the bytes it carries (at 32 in the block, 0x40 in flash).
    words=$(od -An -tx4 -N 32 "$out/blinky.uf2" | tr -s ' \n' ' ')
    if [[ "$words" != " 0a324655 9e5d5157 00002000 10000000 "*" e48bff59 " ]]; then fail "$name" "blinky.uf2's first block isn't an RP2350 one: $words"; return; fi
    words=$(od -An -tx4 -j $((32 + 0x40)) -N 20 "$out/blinky.uf2" | tr -s ' \n' ' ')
    if [ "$words" != " ffffded3 10210142 000001ff 00000000 ab123579 " ]; then fail "$name" "no IMAGE_DEF block at 0x40: $words"; return; fi
    if ! run_in "$dir" r4 build -p:ZeroTarget=r4; then fail "$name" "the r4 build failed"; return; fi
    if [ ! -s "$dir/bin/Debug/$TFM/r4/blinky.elf" ]; then fail "$name" "no r4/blinky.elf"; return; fi
    # A second build has nothing to do.
    if ! run_in "$dir" pico2-again build -p:ZeroTarget=pico2 -v:n; then fail "$name" "the second build failed"; return; fi
    if grep -q "arm-bitcode" "$LOG"; then fail "$name" "the second build compiled again"; return; fi
    if command -v qemu-system-arm > /dev/null 2>&1; then
        for target in pico2 r4; do
            if ! run_in "$dir" run-$target run -p:ZeroTarget=$target < /dev/null; then fail "$name" "dotnet run in QEMU ($target) failed"; return; fi
            if ! grep -q "ticks: 10, LED on: False" "$LOG"; then fail "$name" "dotnet run in QEMU ($target) printed something else"; return; fi
        done
    else
        echo "     (no qemu-system-arm: built, not run)"
    fi
    pass "$name"
}

test_bad_target() {
    local name="bad target" dir
    can_build "$name" || return
    dir=$(fixture native badtarget)
    if run_in "$dir" badtarget build -p:ZeroTarget=mac-arm64; then fail "$name" "the build succeeded"; return; fi
    if ! grep -q 'linux-x64, linux-arm64, win-x64' "$LOG"; then fail "$name" "the error doesn't list the targets"; return; fi
    pass "$name"
}

test_path_with_spaces() {
    local name="path with spaces" dir out
    can_build "$name" || return
    dir=$(fixture native "spaces dir/native")
    if ! run_in "$dir" spaces build; then fail "$name" "dotnet build failed"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/native")"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

test_compile_items() {
    local name="compile items" dir out
    can_build "$name" || return
    dir=$(fixture subfolders)
    if ! run_in "$dir" subfolders build; then fail "$name" "dotnet build failed"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/subfolders")"); if [ "$out" != "from b" ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# Whether the last log shows bflat being run.
ran_bflat() { grep -qE 'tools/bflat/bflat(\.exe)?" build' "$LOG"; }

test_incremental() {
    local name="incremental" dir
    can_build "$name" || return
    dir=$(fixture native incremental)
    if ! run_in "$dir" incremental-1 build -v:n || ! ran_bflat; then fail "$name" "the first build didn't run bflat"; return; fi
    if ! run_in "$dir" incremental-2 build -v:n || ran_bflat; then fail "$name" "an unchanged project ran bflat again"; return; fi
    sleep 1; touch "$dir/Program.cs"
    if ! run_in "$dir" incremental-3 build -v:n || ! ran_bflat; then fail "$name" "a changed source didn't run bflat"; return; fi
    pass "$name"
}

test_bflat_failure_fails_the_build() {
    local name="bflat failure fails the build" dir
    can_build "$name" || return
    dir=$(fixture badimport)
    if run_in "$dir" badimport build; then fail "$name" "the build succeeded"; return; fi
    if ! ran_bflat; then fail "$name" "it failed before bflat ran"; return; fi
    # The build fails because bflat did (its command exited with an error), over
    # the import it can't bind.
    if ! grep -qE 'tools/bflat/bflat(\.exe)?" build @.*exited with code' "$LOG"; then fail "$name" "the failing command wasn't bflat"; return; fi
    # Debug isn't optimized, and bflat then fails before naming the function: on
    # the runtime support that binding at run time would need (zerocore/README.md).
    if ! grep -q 'InteropHelpers' "$LOG"; then fail "$name" "Debug: the error doesn't name InteropHelpers"; return; fi
    if run_in "$dir" badimport-release build -c Release; then fail "$name" "the Release build succeeded"; return; fi
    if ! grep -qE 'tools/bflat/bflat(\.exe)?" build @.*exited with code' "$LOG"; then fail "$name" "Release: the failing command wasn't bflat"; return; fi
    if ! grep -q 'nosuchfunction' "$LOG"; then fail "$name" "Release: the error doesn't name nosuchfunction"; return; fi
    pass "$name"
}

test_size() {
    local name="size" dir
    can_build "$name" || return
    dir=$(fixture native size)
    if ! run_in "$dir" size build -v:n -p:ZeroOptimizeFor=Size; then fail "$name" "dotnet build failed"; return; fi
    if ! grep -q 'bflat arguments:.* -Os' "$LOG"; then fail "$name" "bflat wasn't given -Os"; return; fi
    pass "$name"
}

# Release builds leave out debug information and the symbol table
# (ZeroStripSymbols), and changing the setting relinks: on macOS too, where
# ld64.lld's command line is a file of its own. Both targets build anywhere.
test_strip_symbols() {
    local name="strip symbols" dir t out stripped kept
    can_build "$name" || return
    for t in linux-x64 osx-arm64; do
        dir=$(fixture native "strip-$t")
        out="$dir/bin/Release/$TFM/$t/native"
        if ! run_in "$dir" "strip-$t" build -c Release -p:ZeroTarget=$t; then fail "$name" "$t: dotnet build -c Release failed"; return; fi
        stripped=$(wc -c < "$out")
        if ! run_in "$dir" "strip-$t-kept" build -c Release -p:ZeroTarget=$t -p:ZeroStripSymbols=false; then fail "$name" "$t: the build with ZeroStripSymbols=false failed"; return; fi
        kept=$(wc -c < "$out")
        if [ "$kept" -le "$stripped" ]; then fail "$name" "$t: ZeroStripSymbols=false didn't relink with symbols ($stripped, then $kept bytes)"; return; fi
    done
    pass "$name"
}

# Debug builds aren't optimized (-O0), for debuggers; Release and a chosen
# ZeroOptimizeFor are; an unknown ZeroOptimizeFor is an error.
test_debug_unoptimized() {
    local name="debug unoptimized" dir
    can_build "$name" || return
    dir=$(fixture native debugopt)
    if ! run_in "$dir" debugopt-debug build -v:n; then fail "$name" "dotnet build failed"; return; fi
    if ! grep -q 'bflat arguments:.* -O0' "$LOG"; then fail "$name" "Debug: bflat wasn't given -O0"; return; fi
    if ! run_in "$dir" debugopt-release build -c Release -v:n; then fail "$name" "dotnet build -c Release failed"; return; fi
    if ! grep -q 'bflat arguments:' "$LOG"; then fail "$name" "Release: no bflat arguments line"; return; fi
    if grep -q 'bflat arguments:.* -O0' "$LOG"; then fail "$name" "Release: bflat was given -O0"; return; fi
    if ! run_in "$dir" debugopt-speed build -v:n -p:ZeroOptimizeFor=Speed; then fail "$name" "Debug with Speed failed"; return; fi
    if grep -q 'bflat arguments:.* -O0' "$LOG"; then fail "$name" "Debug with Speed: bflat was given -O0"; return; fi
    # dotnet publish builds Release (PublishRelease), as for .NET 8 and later.
    if ! run_in "$dir" debugopt-publish publish -v:n -o "$dir/out"; then fail "$name" "dotnet publish failed"; return; fi
    if ! grep -qE "Copying file from .*[/\\]bin[/\\]Release[/\\]$TFM[/\\]native(\.exe)?\"" "$LOG"; then fail "$name" "dotnet publish didn't publish the Release program"; return; fi
    if run_in "$dir" debugopt-bad build -p:ZeroOptimizeFor=Fast; then fail "$name" "ZeroOptimizeFor=Fast built"; return; fi
    if ! grep -q "ZeroOptimizeFor 'Fast' isn't supported" "$LOG"; then fail "$name" "ZeroOptimizeFor=Fast: no message"; return; fi
    pass "$name"
}

# Windows: the PDB next to a Debug program is the linker's, also after a build
# that changed nothing, and Microsoft's debugger engine (cdb, Windows SDK)
# stops on a line of Program.cs, reads its locals, and shows a span as text
# with the templates' zero.natvis, which VS Code's cppvsdbg loads.
test_windows_debugger() {
    local name="windows debugger" dir pdb line cdb natvis
    if [ "$HOST_OS" != Windows ]; then skip "$name" "Windows only"; return; fi
    dir=$(fixture debugme)
    pdb=$dir/bin/Debug/$TFM/debugme.pdb
    line=$(grep -n 'the breakpoint' "$dir/Program.cs" | cut -d: -f1)
    if ! run_in "$dir" debugme build; then fail "$name" "dotnet build failed"; return; fi
    if [ "$(head -c 24 "$pdb" 2> /dev/null)" != "Microsoft C/C++ MSF 7.00" ]; then fail "$name" "$pdb isn't the linker's PDB"; return; fi
    if ! run_in "$dir" debugme-again build; then fail "$name" "the second dotnet build failed"; return; fi
    if [ "$(head -c 24 "$pdb" 2> /dev/null)" != "Microsoft C/C++ MSF 7.00" ]; then fail "$name" "after a second build, $pdb isn't the linker's PDB"; return; fi
    cdb="/c/Program Files (x86)/Windows Kits/10/Debuggers/x64/cdb.exe"
    if [ ! -x "$cdb" ]; then fail "$name" "no cdb at $cdb"; return; fi
    natvis=$(native_path "$ROOT/sdk/ZeroCSharp.Templates/vscode/zero.natvis")
    LOG=$WORK/debugme-cdb.log
    (cd "$dir" && run_limited 300 "$LOG" "$cdb" -lines \
        -c ".nvload $natvis;bp \`debugme!Program.cs:$line\`;g;dv;dx word;q" "bin\\Debug\\$TFM\\debugme.exe")
    if ! grep -qE 'total = (0n)?45' "$LOG"; then fail "$name" "cdb didn't show total = 45 at Program.cs:$line"; return; fi
    if ! grep -q 'word *: *"zero" length=4' "$LOG"; then fail "$name" "with zero.natvis, cdb didn't show word as \"zero\""; return; fi
    pass "$name"
}

# A macOS program's debug information stays in bflat's object in obj/: building
# for another target keeps it, and a missing one is rebuilt.
test_mac_debug_object_kept() {
    local name="mac debug object kept" dir object
    can_build "$name" || return
    dir=$(fixture native macdebug)
    object=$dir/obj/Debug/$TFM/zero-native.osx-arm64.o
    if ! run_in "$dir" macdebug-mac build -p:ZeroTarget=osx-arm64; then fail "$name" "the osx-arm64 build failed"; return; fi
    if [ ! -f "$object" ]; then fail "$name" "no $object"; return; fi
    if ! run_in "$dir" macdebug-win build -p:ZeroTarget=win-x64; then fail "$name" "the win-x64 build failed"; return; fi
    if [ ! -f "$object" ]; then fail "$name" "building for win-x64 removed the osx-arm64 object"; return; fi
    rm -f "$object"
    if ! run_in "$dir" macdebug-again build -p:ZeroTarget=osx-arm64; then fail "$name" "the second osx-arm64 build failed"; return; fi
    if [ ! -f "$object" ]; then fail "$name" "a missing object wasn't rebuilt"; return; fi
    pass "$name"
}

test_macos_error() {
    local name="macos error" dir
    if [ "$HOST_RID" != osx-x64 ]; then skip "$name" "Intel Macs only"; return; fi
    dir=$(fixture native macos)
    if run_in "$dir" macos build; then fail "$name" "the build succeeded"; return; fi
    if ! grep -qF "Zero C# builds on Apple Silicon Macs, not Intel ones" "$LOG"; then
        fail "$name" "not the macOS message"; return
    fi
    dir=$(fixture hello macos-library)
    if ! run_in "$dir" macos-library build; then fail "$name" "a library didn't build on macOS"; return; fi
    pass "$name"
}

# ---- Libraries ----------------------------------------------------------------

# app_with_lib <work dir>: the program and the Geometry library (from libs/).
app_with_lib() {
    local dir
    dir=$(fixture app-with-lib "$1")
    cp "$ROOT"/libs/Geometry/*.cs "$dir/Geometry/"
    echo "$dir"
}

test_project_reference() {
    local name="project reference" dir out
    can_build "$name" || return
    dir=$(app_with_lib projref)
    if ! run_in "$dir/App" projref build; then fail "$name" "dotnet build failed"; return; fi
    out=$("$(exe "$dir/App/bin/Debug/$TFM/App")"); if [ "$out" != "Floor area: 37.00" ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

test_library_change_rebuilds() {
    local name="library change rebuilds" dir out
    can_build "$name" || return
    dir=$(app_with_lib libchange)
    if ! run_in "$dir/App" libchange-1 build; then fail "$name" "the first build failed"; return; fi
    sleep 1
    sed -i.bak 's/double total = 0;/double total = 1;/' "$dir/Geometry/Measure.cs" && rm -f "$dir/Geometry/Measure.cs.bak"
    if ! run_in "$dir/App" libchange-2 build -v:n || ! ran_bflat; then fail "$name" "changing the library didn't run bflat"; return; fi
    out=$("$(exe "$dir/App/bin/Debug/$TFM/App")"); if [ "$out" != "Floor area: 38.00" ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# add_source <nuget.config> <name> <dir>: one more package source.
add_source() {
    local path; path=$(native_path "$3" | sed 's|\\|\\\\|g')
    sed -i.bak "s|<clear />|<clear />\n    <add key=\"$2\" value=\"$path\" />|" "$1" && rm -f "$1.bak"
}

test_pack_and_package_reference() {
    local name="pack and package reference" dir app out
    can_build "$name" || return
    dir=$(app_with_lib pack)
    rm -rf "$WORK/libfeed" "${NUGET_PACKAGES:-$HOME/.nuget/packages}/zerotest.geometry"
    if ! run_in "$dir/Geometry" pack-lib pack -o "$WORK/libfeed"; then fail "$name" "dotnet pack failed"; return; fi
    zip_list "$WORK/libfeed/ZeroTest.Geometry.1.0.0.nupkg" | sed 's/^/ /' > "$WORK/pack-list.txt" 2>&1
    if ! grep -q " lib/$TFM/Geometry.dll$" "$WORK/pack-list.txt"; then fail "$name" "the package has no lib/$TFM/Geometry.dll"; return; fi
    app=$(fixture app-with-pkg)/App
    add_source "$app/../nuget.config" libs "$WORK/libfeed"
    if ! run_in "$app" pkgref build; then fail "$name" "the program didn't build"; return; fi
    out=$("$(exe "$app/bin/Debug/$TFM/App")"); if [ "$out" != "Floor area: 37.00" ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# A package made for another SDK version (lib/zero01/, 0.1's framework) must be refused.
test_version_mismatch() {
    local name="version mismatch" app pkg=$WORK/oldfeed
    rm -rf "$pkg" "${NUGET_PACKAGES:-$HOME/.nuget/packages}/zerotest.geometry"
    mkdir -p "$pkg/src/lib/zero01"
    cp "$ROOT/sdk/ZeroCSharp.Sdk/payload/ref/BflatLib.dll" "$pkg/src/lib/zero01/Geometry.dll"
    printf '%s\n' '<?xml version="1.0"?>' \
        '<package><metadata><id>ZeroTest.Geometry</id><version>1.0.0</version><authors>test</authors><description>A zero library built for SDK 0.1.</description></metadata></package>' \
        > "$pkg/src/ZeroTest.Geometry.nuspec"
    zip_dir "$pkg/src" "$pkg/ZeroTest.Geometry.1.0.0.nupkg"
    app=$(fixture app-with-pkg oldpkg)/App
    add_source "$app/../nuget.config" old "$pkg"
    if run_in "$app" oldpkg restore; then fail "$name" "restore succeeded"; return; fi
    if ! grep -q "NU1202" "$LOG" || ! grep -q "zero01" "$LOG" || ! grep -q "$TFM" "$LOG"; then
        fail "$name" "no NU1202 naming zero01 and $TFM"; return
    fi
    pass "$name"
}

# ---- Templates ----------------------------------------------------------------

test_templates() {
    local name="templates" dir=$WORK/templates out file
    can_build "$name" || return
    rm -rf "$dir" && mkdir -p "$dir"
    fixture hello templates-config > /dev/null    # just for its nuget.config
    cp "$WORK/templates-config/nuget.config" "$dir/"
    dotnet new uninstall ZeroCSharp.Templates > /dev/null 2>&1 || true
    if ! run_in "$dir" templates-install new install "$FEED/ZeroCSharp.Templates.$VERSION.nupkg"; then fail "$name" "install failed"; return; fi
    if ! run_in "$dir" templates-new new zero -o Hello; then fail "$name" "dotnet new zero failed"; return; fi
    if ! run_in "$dir/Hello" templates-build build; then fail "$name" "the program didn't build"; return; fi
    out=$(echo Ann | "$(exe "$dir/Hello/bin/Debug/$TFM/Hello")")
    if [[ "$out" != *"Hello, Ann!"* ]]; then fail "$name" "printed '$out'"; return; fi
    # VS Code debugging: the program's name and folder filled in, and the rest.
    for file in launch.json tasks.json settings.json extensions.json zero_lldb.py zero.natvis; do
        if [ ! -f "$dir/Hello/.vscode/$file" ]; then fail "$name" "no .vscode/$file"; return; fi
    done
    if ! grep -q "\"\${workspaceFolder}/bin/Debug/$TFM/Hello\"" "$dir/Hello/.vscode/launch.json"; then fail "$name" "launch.json doesn't start bin/Debug/$TFM/Hello"; return; fi
    if grep -q '@ZERO_' "$dir/Hello/.vscode/launch.json"; then fail "$name" "launch.json has a placeholder left"; return; fi
    if ! run_in "$dir" templates-newlib new zerolib -o Lib; then fail "$name" "dotnet new zerolib failed"; return; fi
    if ! run_in "$dir/Lib" templates-pack pack -o "$dir/feed"; then fail "$name" "the library didn't pack"; return; fi
    # The F# ones: the same names, with -lang F#.
    if ! run_in "$dir" templates-new-fs new zero -lang F# -o FsHello; then fail "$name" "dotnet new zero -lang F# failed"; return; fi
    if [ ! -f "$dir/FsHello/FsHello.fsproj" ] || [ ! -f "$dir/FsHello/.vscode/launch.json" ]; then fail "$name" "no FsHello.fsproj or .vscode"; return; fi
    if ! run_in "$dir/FsHello" templates-build-fs build; then fail "$name" "the F# program didn't build"; return; fi
    out=$(echo Ann | "$(exe "$dir/FsHello/bin/Debug/$TFM/FsHello")")
    if [[ "$out" != *"Hello, Ann!"* ]]; then fail "$name" "the F# program printed '$out'"; return; fi
    if ! run_in "$dir" templates-newlib-fs new zerolib -lang F# -o FsLib; then fail "$name" "dotnet new zerolib -lang F# failed"; return; fi
    if ! run_in "$dir/FsLib" templates-pack-fs pack -o "$dir/feed"; then fail "$name" "the F# library didn't pack"; return; fi
    pass "$name"
}

# ---- From the final review -----------------------------------------------------

test_custom_packages_folder() {
    local name="custom packages folder" dir out
    can_build "$name" || return
    dir=$(fixture native pkgfolder)
    rm -rf "$WORK/pkgfolder-packages"
    # No trailing slash, as CI setups often write it.
    if ! (export NUGET_PACKAGES=$(native_path "$WORK/pkgfolder-packages"); run_in "$dir" pkgfolder build); then fail "$name" "dotnet build failed"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/native")"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

test_dotnet_run() {
    local name="dotnet run" dir
    can_build "$name" || return
    dir=$(fixture native run)
    if ! run_in "$dir" run run; then fail "$name" "dotnet run failed"; return; fi
    if [ "$(tail -1 "$LOG")" != hi ]; then fail "$name" "didn't print hi"; return; fi
    pass "$name"
}

test_publish() {
    local name="publish" dir out
    can_build "$name" || return
    dir=$(fixture native publish)
    if ! run_in "$dir" publish publish -c Release -o "$dir/out"; then fail "$name" "dotnet publish failed"; return; fi
    if [ ! -x "$(exe "$dir/out/native")" ]; then fail "$name" "no native program in the publish folder"; return; fi
    if [ -e "$dir/out/native.dll" ]; then fail "$name" "the IL native.dll was published"; return; fi
    out=$("$(exe "$dir/out/native")"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

test_extra_dll_imports() {
    local name="extra dll imports" dir
    can_build "$name" || return
    dir=$(fixture native dllimports)
    # libdl.so.2 is a Linux library: build for Linux on every host.
    local target=$HOST_RID; [ "$HOST_OS" = Windows ] && target=linux-x64
    if ! run_in "$dir" dllimports build -v:n "-p:ZeroDllImports=libdl.so.2;" -p:ZeroTarget=$target; then fail "$name" "dotnet build failed"; return; fi
    if ! grep -q 'bflat arguments:.* -i libc .*-i libdl.so.2' "$LOG" && ! grep -q 'bflat arguments:.* -i libdl.so.2 .*-i libc ' "$LOG"; then
        fail "$name" "libc and libdl.so.2 weren't both passed"; return
    fi
    pass "$name"
}

# [LibraryImport]s become [DllImport]s that bflat compiles, under both memory
# safety rules; what zero mode can't pass is an error naming it.
test_library_import() {
    local name="library import" dir out generated expect
    can_build "$name" || return
    dir=$(fixture libraryimport)
    if ! run_in "$dir" libraryimport build -v:n; then fail "$name" "dotnet build failed"; return; fi
    generated=$(find "$dir/obj" -name LibraryImports.g.cs | head -1)
    if [ -z "$generated" ] || ! grep -q 'DllImport("libc", EntryPoint = "qsort"' "$generated"; then fail "$name" "the generator wrote no [DllImport] for qsort"; return; fi
    if ! grep -q "bflat arguments:.*LibraryImports.g.cs" "$LOG"; then fail "$name" "bflat didn't get the generated file"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/libraryimport")" | tr -d '\r')
    if [ "$out" != "$(printf 'abs 5 7\ndiv 3 2\nstrlen 4\nqsort 1 2 3 4')" ]; then fail "$name" "printed '$out'"; return; fi
    if ! run_in "$dir" libraryimport-2 build -v:n || ran_bflat; then fail "$name" "an unchanged project ran bflat again"; return; fi
    if ! run_in "$dir" libraryimport-release build -c Release; then fail "$name" "the Release build failed"; return; fi
    out=$("$(exe "$dir/bin/Release/$TFM/libraryimport")" | tr -d '\r')
    if [ "$out" != "$(printf 'abs 5 7\ndiv 3 2\nstrlen 4\nqsort 1 2 3 4')" ]; then fail "$name" "Release printed '$out'"; return; fi

    dir=$(fixture libraryimport-classic)
    if ! run_in "$dir" libraryimport-classic build; then fail "$name" "the build under C#'s earlier rules failed"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/libraryimport-classic")" | tr -d '\r')
    if [ "$out" != "abs 5 strlen 4" ]; then fail "$name" "under C#'s earlier rules, printed '$out'"; return; fi

    dir=$(fixture libraryimport-errors)
    if run_in "$dir" libraryimport-errors build; then fail "$name" "the build with unsupported imports succeeded"; return; fi
    for expect in "ZERO013: 'Bad.puts(string)': parameter 'text' is string" \
                  "ZERO013: 'Bad.isatty(int)': it returns bool" \
                  "ZERO013: 'Bad.frexp_ref(double, ref int)': parameter 'exponent' is ref" \
                  "ZERO013: 'Bad.close(int)': SetLastError isn't supported" \
                  "ZERO013: 'Bad.getpid()': strings aren't marshalled" \
                  "ZERO013: 'Bad.getppid()': .MarshalAs. isn't supported" \
                  "ZERO013: 'Bad.flags(Flags)': parameter 'value' is Flags, whose field 'On' is bool" \
                  "ZERO013: 'Bad.money(decimal)': parameter 'value' is decimal" \
                  "ZERO013: 'Bad.toggle(Switch)': parameter 'value' is Switch, whose field 'Up' is bool" \
                  "ZERO013: 'Bad.call(delegate\*<int, void>)': parameter 'callback' is delegate\*<int, void>, a managed function pointer" \
                  "ZERO012: 'Bad.generic<T>(int)': a .LibraryImport. method can't be generic" \
                  "ZERO012: 'NotPartial.Inner.abs(int)': its type 'NotPartial' must be partial"; do
        if ! grep -q "$expect" "$LOG"; then fail "$name" "no error: $expect"; return; fi
    done
    # A method ZERO013 refuses still gets a body, so it isn't also CS8795 (no implementation).
    if grep -q "CS8795: Partial method 'Bad.puts" "$LOG"; then fail "$name" "a refused import was also CS8795"; return; fi
    pass "$name"
}

test_inherited_settings() {
    local name="inherited settings" dir out
    can_build "$name" || return
    dir=$(fixture inherited)
    if ! run_in "$dir" inherited build; then fail "$name" "dotnet build failed"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/inherited")"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

test_clean_and_rebuild() {
    local name="clean and rebuild" dir
    can_build "$name" || return
    dir=$(fixture native clean)
    if ! run_in "$dir" clean-1 build; then fail "$name" "dotnet build failed"; return; fi
    if ! run_in "$dir" clean-2 clean; then fail "$name" "dotnet clean failed"; return; fi
    if [ -e "$(exe "$dir/bin/Debug/$TFM/native")" ]; then fail "$name" "dotnet clean left the native program"; return; fi
    if ! run_in "$dir" clean-3 build -v:n || ! ran_bflat; then fail "$name" "building after clean didn't run bflat"; return; fi
    pass "$name"
}

# ---- 0.1.1 ----------------------------------------------------------------------

# Like dotnet build -r: the default build's program is in bin/Debug/<framework>/,
# a ZeroTarget's in a folder named after it. Building for one keeps the others,
# and dotnet clean removes them all.
test_targets_side_by_side() {
    # The host's own target, run; and another one, checked: Windows from Linux,
    # Linux x64 from Windows.
    local name="targets side by side" dir out host=$HOST_RID other=win-x64 other_file
    can_build "$name" || return
    [ "$HOST_OS" = Windows ] && other=linux-x64
    dir=$(fixture native sidebyside)
    other_file=$dir/bin/Debug/$TFM/$other/native
    [ "$other" = win-x64 ] && other_file=$other_file.exe
    if ! run_in "$dir" sidebyside-1 build; then fail "$name" "the default build failed"; return; fi
    if ! run_in "$dir" sidebyside-2 build -p:ZeroTarget=$other; then fail "$name" "the $other build failed"; return; fi
    if ! run_in "$dir" sidebyside-3 build -p:ZeroTarget=$host; then fail "$name" "the $host build failed"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/native")" 2>/dev/null)
    if [ "$out" != hi ]; then fail "$name" "the default program is gone after building for other targets"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/$host/native")" 2>/dev/null)
    if [ "$out" != hi ]; then fail "$name" "no $host/native"; return; fi
    if [ "$other" = win-x64 ]; then is_pe "$other_file"; else is_elf "$other_file" x86-64; fi \
        || { fail "$name" "no $other program"; return; }
    if ! run_in "$dir" sidebyside-mac build -p:ZeroTarget=osx-arm64; then fail "$name" "the osx-arm64 build failed"; return; fi
    if ! is_macho_arm64 "$dir/bin/Debug/$TFM/osx-arm64/native"; then fail "$name" "no osx-arm64 program"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/$host/native")" 2>/dev/null)
    if [ "$out" != hi ]; then fail "$name" "$host/native is gone after building for osx-arm64"; return; fi
    if ! run_in "$dir" sidebyside-4 clean; then fail "$name" "dotnet clean failed"; return; fi
    if [ -e "$(exe "$dir/bin/Debug/$TFM/native")" ] || [ -e "$(exe "$dir/bin/Debug/$TFM/$host/native")" ] || [ -e "$other_file" ] ||
       [ -e "$dir/bin/Debug/$TFM/osx-arm64/native" ]; then
        fail "$name" "dotnet clean left a native program"; return
    fi
    pass "$name"
}

# bflat compiles with the project's language version and compiler features,
# not fixed ones.
test_language_settings_forwarded() {
    local name="language settings forwarded" dir
    can_build "$name" || return
    dir=$(fixture native langver)
    if ! run_in "$dir" langver build -v:n -p:LangVersion=14 "-p:Features=strict"; then fail "$name" "dotnet build failed"; return; fi
    if ! grep -q 'bflat arguments:.* --langversion 14 ' "$LOG"; then fail "$name" "bflat wasn't given --langversion 14"; return; fi
    if ! grep -q 'bflat arguments:.* --compiler-feature strict ' "$LOG"; then fail "$name" "bflat wasn't given --compiler-feature strict"; return; fi
    if grep -q 'bflat arguments:.* --langversion preview' "$LOG"; then fail "$name" "bflat was still given --langversion preview"; return; fi
    pass "$name"
}

test_target_framework_is_an_error() {
    local name="target framework is an error" dir
    dir=$(fixture tfm)
    if run_in "$dir" tfm build; then fail "$name" "the build succeeded"; return; fi
    if ! grep -q "ZERO001" "$LOG" || ! grep -q "TargetFramework" "$LOG"; then fail "$name" "no ZERO001 error naming TargetFramework"; return; fi
    pass "$name"
}

# An older .NET SDK (Linux distributions package 10.0.1xx) fails at restore
# with ZERO014, not later with NETSDK1005. The version is passed as a global
# property: CI has only the newest SDK installed.
test_old_dotnet_sdk_refused() {
    local name="old .NET SDK refused" dir v
    dir=$(fixture native oldsdk)
    for v in 10.0.115 10.0.302 10.0.100-rc.2.25502.107; do
        if run_in "$dir" oldsdk build -p:NETCoreSdkVersion=$v; then fail "$name" "the build with $v succeeded"; return; fi
        if ! grep -q "ZERO014" "$LOG" || ! grep -q "10.0.400 or newer" "$LOG"; then fail "$name" "no ZERO014 for $v"; return; fi
    done
    if ! run_in "$dir" oldsdk restore -p:NETCoreSdkVersion=10.0.400; then fail "$name" "restore with 10.0.400 failed"; return; fi
    pass "$name"
}

# bflat already executable in a read-only packages folder (a shared or mounted
# cache): the build mustn't try to change it. As root, permissions don't stop
# chmod, so this can only fail when run as another user (as CI's runners are).
test_read_only_packages_folder() {
    local name="read-only packages folder" dir pkgs=$WORK/ro-packages out
    linux_only "$name" || return
    dir=$(fixture native readonly)
    chmod -R u+w "$pkgs" 2>/dev/null; rm -rf "$pkgs"
    if ! (export NUGET_PACKAGES=$pkgs; run_in "$dir" readonly-1 build); then fail "$name" "the first build failed"; return; fi
    chmod -R a-w "$pkgs"/zerocsharp.bflat.*
    sleep 1; touch "$dir/Program.cs"
    (export NUGET_PACKAGES=$pkgs; run_in "$dir" readonly-2 build -v:n)
    local rc=$?
    LOG=$WORK/readonly-2.log # run_in set it in the subshell
    chmod -R u+w "$pkgs"
    if [ $rc -ne 0 ]; then fail "$name" "building with a read-only packages folder failed"; return; fi
    if ! ran_bflat; then fail "$name" "the second build didn't run bflat"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/native")"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# The release workflow publishes only when the tag names the version packed.
test_release_tag_check() {
    local name="release tag check"
    LOG=$WORK/tagcheck.log
    # BFLAT_REF named: CI's environment sets it (sdk.yml).
    if ! BFLAT_REF=master sdk/check-tag.sh "sdk-v$VERSION" > "$LOG" 2>&1; then fail "$name" "sdk-v$VERSION was refused"; return; fi
    if BFLAT_REF=master sdk/check-tag.sh "sdk-v0.0.9" > "$LOG" 2>&1; then fail "$name" "sdk-v0.0.9 was accepted for $VERSION"; return; fi
    if ! grep -q "$VERSION" "$LOG"; then fail "$name" "the error doesn't name $VERSION"; return; fi
    # A release carries bflat from the fork's master, never a branch build.
    if BFLAT_REF=windows-lld sdk/check-tag.sh "sdk-v$VERSION" > "$LOG" 2>&1; then fail "$name" "a release from the fork branch windows-lld was accepted"; return; fi
    if ! grep -q "BFLAT_REF" "$LOG"; then fail "$name" "the error doesn't name BFLAT_REF"; return; fi
    if ! BFLAT_REF=master sdk/check-tag.sh "sdk-v$VERSION" > "$LOG" 2>&1; then fail "$name" "BFLAT_REF=master was refused"; return; fi
    pass "$name"
}

# ./build.sh --sdk-payload replaces its directory: only an empty one or an
# earlier payload, never a folder of something else.
test_payload_folder_guard() {
    local name="payload folder guard" dir=$WORK/notapayload
    rm -rf "$dir" && mkdir -p "$dir" && echo keep > "$dir/keep.txt"
    LOG=$WORK/payloadguard.log
    if ./build.sh --sdk-payload "${dir#$ROOT/}" > "$LOG" 2>&1; then fail "$name" "build.sh accepted the folder"; return; fi
    if [ ! -f "$dir/keep.txt" ]; then fail "$name" "the folder's files were removed"; return; fi
    if ! grep -q "isn't an SDK payload" "$LOG"; then fail "$name" "not the guard's message"; return; fi
    pass "$name"
}

# ZeroTarget from Directory.Build.targets, imported after the project: still a
# chosen target, with a folder of its own.
test_target_from_directory_targets() {
    # A target other than the host's: Windows from Linux, Linux x64 from Windows.
    local name="target from Directory.Build.targets" dir target=win-x64 program
    can_build "$name" || return
    [ "$HOST_OS" = Windows ] && target=linux-x64
    dir=$(fixture dirtargets)
    sed -i.bak "s|<ZeroTarget>win-x64</ZeroTarget>|<ZeroTarget>$target</ZeroTarget>|" "$dir/Directory.Build.targets" && rm -f "$dir/Directory.Build.targets.bak"
    if ! run_in "$dir" dirtargets build; then fail "$name" "dotnet build failed"; return; fi
    program=$dir/bin/Debug/$TFM/$target/dirtargets
    if [ "$target" = win-x64 ]; then is_pe "$program.exe"; else is_elf "$program" x86-64; fi \
        || { fail "$name" "no $target program in $target/"; return; }
    pass "$name"
}

# A project built with 0.1.0 (on nuget.org), then with this version: the
# program the new build makes must still be there afterwards.
test_upgrade_from_0_1_0() {
    local name="upgrade from 0.1.0" dir out
    linux_only "$name" || return
    dir=$(fixture native upgrade)
    sed -i.bak "s|ZeroCSharp.Sdk/$VERSION|ZeroCSharp.Sdk/0.1.0|" "$dir/native.csproj" && rm -f "$dir/native.csproj.bak"
    if ! run_in "$dir" upgrade-1 build; then fail "$name" "the 0.1.0 build failed"; return; fi
    sed -i.bak "s|ZeroCSharp.Sdk/0.1.0|ZeroCSharp.Sdk/$VERSION|" "$dir/native.csproj" && rm -f "$dir/native.csproj.bak"
    if ! run_in "$dir" upgrade-2 build; then fail "$name" "the $VERSION build failed"; return; fi
    out=$("$dir/bin/Debug/$TFM/native" 2>/dev/null); if [ "$out" != hi ]; then fail "$name" "the program is gone after the $VERSION build"; return; fi
    pass "$name"
}

# ---- 0.2 ------------------------------------------------------------------------

# 40 sources in a folder with a long name: their full paths alone pass cmd.exe's
# 8191-character limit, so bflat must get its arguments from a response file.
test_long_command_line() {
    local name="long command line" dir sub i main
    can_build "$name" || return
    dir=$(fixture native manyfiles)
    rm -f "$dir/Program.cs"
    sub="$dir/$(printf 'a%.0s' $(seq 1 120))"
    mkdir -p "$sub"
    main='public static class Program { public static int Main() { long n = 0;'
    # 80 files of ~160 characters each: past 8191 even on a short checkout path.
    for i in $(seq 1 80); do
        echo "public static class Part$i { public static long One() => 1; }" > "$sub/Part$i.cs"
        main="$main n += Part$i.One();"
    done
    echo "$main Out.Write(n); Out.Line(); return 0; } }" > "$dir/Program.cs"
    if ! run_in "$dir" manyfiles build -v:n; then fail "$name" "dotnet build failed"; return; fi
    if ! grep -qE 'tools/bflat/bflat(\.exe)?" build @' "$LOG"; then fail "$name" "bflat didn't get a response file"; return; fi
    # Past cmd.exe's 8191-character limit, or the test proves nothing there.
    local len; len=$(wc -c < "$dir/obj/Debug/$TFM/zero-native.args" | tr -d ' ')
    if [ "$len" -le 8191 ]; then fail "$name" "the arguments are only $len characters"; return; fi
    local out; out=$("$(exe "$dir/bin/Debug/$TFM/native")" | tr -d '\r')
    if [ "$out" != 80 ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# From Windows, both Linux targets: ELF programs for their architectures.
test_windows_builds_linux() {
    local name="windows builds linux" dir
    if [ "$HOST_OS" != Windows ]; then skip "$name" "Windows only"; return; fi
    dir=$(fixture native winlinux)
    if ! run_in "$dir" winlinux-x64 build -p:ZeroTarget=linux-x64; then fail "$name" "the linux-x64 build failed"; return; fi
    if ! run_in "$dir" winlinux-arm64 build -p:ZeroTarget=linux-arm64; then fail "$name" "the linux-arm64 build failed"; return; fi
    if ! is_elf "$dir/bin/Debug/$TFM/linux-x64/native" x86-64; then fail "$name" "linux-x64/native isn't an x86-64 ELF"; return; fi
    if ! is_elf "$dir/bin/Debug/$TFM/linux-arm64/native" aarch64; then fail "$name" "linux-arm64/native isn't an aarch64 ELF"; return; fi
    # Kept for CI, which runs them on Linux (sdk.yml, windows-built-linux).
    mkdir -p "$WORK/windows-built/linux-x64" "$WORK/windows-built/linux-arm64"
    cp "$dir/bin/Debug/$TFM/linux-x64/native" "$WORK/windows-built/linux-x64/"
    cp "$dir/bin/Debug/$TFM/linux-arm64/native" "$WORK/windows-built/linux-arm64/"
    pass "$name"
}

# A packages folder with a space and a non-ASCII letter, as a Windows user
# profile can have (C:\Users\Zoë Ann\.nuget\packages).
test_unusual_packages_folder() {
    local name="unusual packages folder" dir out pkgs="$WORK/pkgs Zoë Ann"
    can_build "$name" || return
    dir=$(fixture native unusualpkgs)
    rm -rf "$pkgs"
    if ! (export NUGET_PACKAGES=$(native_path "$pkgs"); run_in "$dir" unusualpkgs build); then
        LOG=$WORK/unusualpkgs.log; fail "$name" "dotnet build failed"; return
    fi
    out=$("$(exe "$dir/bin/Debug/$TFM/native")"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# Lines piped from PowerShell or cmd end in \r\n: the program reads the name
# without the \r (the template's program, built for the host).
test_crlf_input() {
    local name="crlf input" dir=$WORK/crlf out
    can_build "$name" || return
    rm -rf "$dir" && mkdir -p "$dir"
    fixture hello crlf-config > /dev/null
    cp "$WORK/crlf-config/nuget.config" "$dir/"
    dotnet new uninstall ZeroCSharp.Templates > /dev/null 2>&1 || true
    if ! run_in "$dir" crlf-install new install "$FEED/ZeroCSharp.Templates.$VERSION.nupkg"; then fail "$name" "install failed"; return; fi
    if ! run_in "$dir" crlf-new new zero -o Hello; then fail "$name" "dotnet new zero failed"; return; fi
    if ! run_in "$dir/Hello" crlf-build build; then fail "$name" "the program didn't build"; return; fi
    out=$(printf 'Ann\r\n' | "$(exe "$dir/Hello/bin/Debug/$TFM/Hello")" | od -An -c | tr -s ' ')
    if [[ "$out" != *"A n n !"* ]]; then fail "$name" "printed:$out"; return; fi
    pass "$name"
}

# Build output in a folder with a space between the project and it (artifacts
# at the repository's root, in "art out"): bflat must hand the linker the
# runtime objects' paths whole. Same for the packages folder (above).
test_artifacts_path_with_spaces() {
    local name="artifacts path with spaces" dir program out
    can_build "$name" || return
    dir=$(fixture native artspace/native)
    rm -rf "$WORK/artspace/art out"
    if ! run_in "$dir" artifacts build -p:UseArtifactsOutput=true "-p:ArtifactsPath=$(native_path "$WORK/artspace/art out")"; then fail "$name" "dotnet build failed"; return; fi
    program=$(find "$WORK/artspace/art out/bin" -type f -name "$(basename "$(exe native)")" 2>/dev/null | head -1)
    if [ -z "$program" ]; then fail "$name" "no program under art out/bin"; return; fi
    out=$("$program"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# Static constructors reached from eight threads at once: each thread sees the
# type set up, the constructor runs once, re-entry and a two-thread cycle work.
# 20 runs, since a race shows up only sometimes.
test_static_constructor_threads() {
    local name="static constructor threads" dir out i status expected
    can_build "$name" || return
    dir=$(fixture cctor-threads)
    if ! run_in "$dir" cctor-threads build; then fail "$name" "dotnet build failed"; return; fi
    expected=$'values 7 7 7 7 7 7 7 7\nruns 1\nself ok\ncycle ok'
    for i in $(seq 1 20); do
        run_limited 30 "$WORK/cctor-threads.out" "$(exe "$dir/bin/Debug/$TFM/cctor-threads")"
        status=$?
        out=$(tr -d '\r' < "$WORK/cctor-threads.out")
        if [ "$status" -ne 0 ]; then fail "$name" "run $i: exit $status: $out"; return; fi
        if [ "$out" != "$expected" ]; then fail "$name" "run $i: $out"; return; fi
    done
    pass "$name"
}

# Console.Write shows its text at once, as in .NET, and a program that fails
# prints what Out still holds (part of a line) before the failure's message.
test_failure_keeps_output() {
    local name="console write and failure output" dir out status
    can_build "$name" || return
    dir=$(fixture failfast)
    if ! run_in "$dir" failfast build; then fail "$name" "dotnet build failed"; return; fi
    run_limited 30 "$WORK/failfast.out" "$(exe "$dir/bin/Debug/$TFM/failfast")"
    status=$?
    out=$(tr -d '\r' < "$WORK/failfast.out")
    if [ "$status" -eq 0 ]; then fail "$name" "exit 0: $out"; return; fi
    if [ "$out" != "a b c IndexOutOfRangeException" ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# ---- Native libraries (NativeLib=Shared) --------------------------------------

# call_native <call-native-lib.sh args>: the .NET caller (a fresh copy of its
# fixture, with the test feed's nuget.config) on a library; output to a log.
call_native() {
    local caller
    caller=$(fixture nativelib-caller)
    LOG=$WORK/call-native.log
    ZERO_CALLER_DIR=$caller "$ROOT/sdk/test/call-native-lib.sh" "$@" > "$LOG" 2>&1
}

# build_nativelib <dir> <log> [dotnet build args]: builds a copy of
# fixtures/nativelib; NATIVELIB is then the host target's library.
build_nativelib() {
    local dir=$1 log=$2
    shift 2
    NATIVELIB=$dir/bin/Debug/$TFM/native/nativelib$HOST_LIB_EXT
    run_in "$dir" "$log" build "$@"
}

test_native_library_dotnet() {
    local name="native library dotnet" dir
    can_build "$name" || return
    dir=$(fixture nativelib)
    if ! build_nativelib "$dir" nativelib; then fail "$name" "dotnet build failed"; return; fi
    if [ ! -f "$NATIVELIB" ]; then fail "$name" "no native/nativelib$HOST_LIB_EXT"; return; fi
    if ! call_native "$NATIVELIB"; then fail "$name" "the .NET caller didn't get what it should"; return; fi
    pass "$name"
}

# Exactly the [UnmanagedCallersOnly(EntryPoint)] methods are exported (on
# Windows the caller's "hidden ok" line checks zerocore's symbols instead).
test_native_library_exports() {
    local name="native library exports" dir exported expected
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-exports)
    if ! build_nativelib "$dir" nativelib-exports; then fail "$name" "dotnet build failed"; return; fi
    expected=$(printf '%s\n' nl_add nl_cctor_value nl_mul_long nl_negate nl_offset nl_remember nl_scale nl_slow_runs nl_slow_value nl_sum)
    case "$HOST_OS" in
        Linux)  exported=$(nm -D --defined-only "$NATIVELIB" | awk '{print $3}' | sed 's/@.*//' | sort) ;;
        Darwin) exported=$(nm -gU "$NATIVELIB" | awk '{print $3}' | sed 's/^_//' | sort) ;;
        *)      skip "$name" "checked by the caller's hidden line on Windows"; return ;;
    esac
    if [ "$exported" != "$expected" ]; then fail "$name" "exports: $(echo $exported)"; return; fi
    pass "$name"
}

# Two copies of one library in a process keep separate state.
test_native_library_two() {
    local name="native library two" dir
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-two)
    if ! build_nativelib "$dir" nativelib-two; then fail "$name" "dotnet build failed"; return; fi
    mkdir -p "$WORK/two"
    cp "$NATIVELIB" "$WORK/two/a$HOST_LIB_EXT"
    cp "$NATIVELIB" "$WORK/two/b$HOST_LIB_EXT"
    if ! call_native two "$WORK/two/a$HOST_LIB_EXT" "$WORK/two/b$HOST_LIB_EXT"; then fail "$name" "the copies share state"; return; fi
    pass "$name"
}

test_native_library_errors() {
    local name="native library errors" dir
    dir=$(fixture native nativelib-exe)
    if run_in "$dir" nativelib-exe build -p:NativeLib=Shared || ! grep -q "set OutputType to Library" "$LOG"; then
        fail "$name" "NativeLib with an Exe wasn't refused"; return
    fi
    dir=$(fixture nativelib nativelib-static)
    if run_in "$dir" nativelib-static build -p:NativeLib=Static || ! grep -q "NativeLib 'Static' isn't supported" "$LOG"; then
        fail "$name" "NativeLib=Static wasn't refused"; return
    fi
    dir=$(fixture nativelib nativelib-wasm)
    if run_in "$dir" nativelib-wasm build -p:ZeroTarget=wasm || ! grep -q "ZeroTarget 'wasm' has no shared libraries" "$LOG"; then
        fail "$name" "a wasm library wasn't refused"; return
    fi
    dir=$(fixture native nativelib-ref/App)
    fixture nativelib nativelib-ref/nativelib > /dev/null
    sed -i.bak 's|</Project>|  <ItemGroup><ProjectReference Include="../nativelib/nativelib.csproj" /></ItemGroup>\n</Project>|' "$dir/native.csproj" && rm -f "$dir/native.csproj.bak"
    if run_in "$dir" nativelib-ref build || ! grep -q "references a native library (NativeLib=Shared): nativelib" "$LOG"; then
        fail "$name" "a ProjectReference to a native library wasn't refused"; return
    fi
    dir=$(fixture nativelib nativelib-run)
    if run_in "$dir" nativelib-run run || ! grep -qi "library" "$LOG"; then
        fail "$name" "dotnet run of a native library didn't fail saying it's a library"; return
    fi
    pass "$name"
}

test_native_library_not_packable() {
    local name="native library not packable" dir
    dir=$(fixture nativelib nativelib-pack)
    run_in "$dir" nativelib-pack pack
    if [ -n "$(find "$dir" -name '*.nupkg')" ]; then fail "$name" "dotnet pack made a package"; return; fi
    pass "$name"
}

test_native_library_publish() {
    local name="native library publish" dir
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-publish)
    if ! run_in "$dir" nativelib-publish publish -c Release -o "$dir/out"; then fail "$name" "dotnet publish failed"; return; fi
    if [ ! -f "$dir/out/nativelib$HOST_LIB_EXT" ]; then fail "$name" "no nativelib$HOST_LIB_EXT in the publish folder"; return; fi
    if ! call_native "$dir/out/nativelib$HOST_LIB_EXT"; then fail "$name" "the published library doesn't work"; return; fi
    pass "$name"
}

test_native_library_incremental() {
    local name="native library incremental" dir
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-incremental)
    if ! run_in "$dir" nativelib-incremental-1 build -v:n || ! ran_bflat; then fail "$name" "the first build didn't run bflat"; return; fi
    if ! run_in "$dir" nativelib-incremental-2 build -v:n || ran_bflat; then fail "$name" "an unchanged library ran bflat again"; return; fi
    sleep 1; touch "$dir/Exports.cs"
    if ! run_in "$dir" nativelib-incremental-3 build -v:n || ! ran_bflat; then fail "$name" "a changed export didn't run bflat"; return; fi
    pass "$name"
}

test_native_library_clean() {
    local name="native library clean" dir
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-clean)
    if ! build_nativelib "$dir" nativelib-clean || [ ! -f "$NATIVELIB" ]; then fail "$name" "dotnet build failed"; return; fi
    if ! run_in "$dir" nativelib-clean-2 clean || [ -e "$NATIVELIB" ]; then fail "$name" "dotnet clean left the library"; return; fi
    pass "$name"
}

test_native_library_path_with_spaces() {
    local name="native library path with spaces" dir
    can_build "$name" || return
    dir=$(fixture nativelib "with spaces/native lib")
    if ! build_nativelib "$dir" nativelib-spaces; then fail "$name" "dotnet build failed"; return; fi
    if ! call_native "$NATIVELIB"; then fail "$name" "the .NET caller didn't get what it should"; return; fi
    pass "$name"
}

# A native library that uses a zero library (a ProjectReference) compiles it in.
test_native_library_with_zero_reference() {
    local name="native library with zero reference" dir
    can_build "$name" || return
    app_with_lib nativelib-zeroref > /dev/null
    dir=$(fixture nativelib nativelib-zeroref/nativelib)
    sed -i.bak 's|</Project>|  <ItemGroup><ProjectReference Include="../Geometry/Geometry.csproj" /></ItemGroup>\n</Project>|' "$dir/nativelib.csproj" && rm -f "$dir/nativelib.csproj.bak"
    cat > "$dir/Area.cs" <<'CS'
using System.Runtime.InteropServices;

public static class AreaExport
{
    [UnmanagedCallersOnly(EntryPoint = "nl_rect_area")]
    public static double RectArea(double width, double height) => new Geometry.Rect(width, height).Area;
}
CS
    if ! build_nativelib "$dir" nativelib-zeroref; then fail "$name" "dotnet build failed"; return; fi
    if ! call_native area "$NATIVELIB"; then fail "$name" "nl_rect_area didn't return 6"; return; fi
    pass "$name"
}

test_native_library_dotted_name() {
    local name="native library dotted name" dir lib
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-dotted)
    if ! run_in "$dir" nativelib-dotted build -p:AssemblyName=My.Native; then fail "$name" "dotnet build failed"; return; fi
    lib=$dir/bin/Debug/$TFM/native/My.Native$HOST_LIB_EXT
    if [ ! -f "$lib" ]; then fail "$name" "no native/My.Native$HOST_LIB_EXT"; return; fi
    if ! call_native "$lib"; then fail "$name" "the .NET caller didn't get what it should"; return; fi
    pass "$name"
}

# c_compiler: a C compiler for the host's own target, if there is one.
c_compiler() {
    if [ -n "${CC:-}" ]; then echo "$CC"
    elif command -v cc > /dev/null; then echo cc
    elif command -v clang > /dev/null; then echo clang
    elif command -v gcc > /dev/null; then echo gcc
    else return 1; fi
}

# A C program loads the library (dlopen, or LoadLibrary on Windows) and prints
# the first eight lines the .NET caller prints.
test_native_library_c() {
    local name="native library c" dir cc caller out ldl=
    can_build "$name" || return
    if ! cc=$(c_compiler); then skip "$name" "no C compiler"; return; fi
    dir=$(fixture nativelib nativelib-c)
    if ! build_nativelib "$dir" nativelib-c; then fail "$name" "dotnet build failed"; return; fi
    [ "$HOST_OS" = Linux ] && ldl=-ldl
    caller=$(exe "$WORK/nativelib-c-caller")
    LOG=$WORK/nativelib-c-cc.log
    if ! "$cc" "$(native_path "$ROOT/sdk/test/fixtures/nativelib-c/main.c")" -o "$(native_path "$caller")" $ldl > "$LOG" 2>&1; then
        fail "$name" "the C caller didn't compile"; return
    fi
    out=$("$caller" "$(native_path "$NATIVELIB")" 2>&1 | tr -d '\r')
    if [ "$out" != "$(head -8 "$ROOT/sdk/test/fixtures/nativelib-caller/expected.txt" | tr -d '\r')" ]; then fail "$name" "printed: $(echo $out)"; return; fi
    pass "$name"
}

# A C program linked against the library (the soname / install name, and an
# rpath to its folder).
test_native_library_c_linked() {
    local name="native library c linked" dir cc caller out
    can_build "$name" || return
    if [ "$HOST_OS" = Windows ]; then skip "$name" "Linux and macOS"; return; fi
    if ! cc=$(c_compiler); then skip "$name" "no C compiler"; return; fi
    dir=$(fixture nativelib nativelib-c-linked)
    if ! build_nativelib "$dir" nativelib-c-linked; then fail "$name" "dotnet build failed"; return; fi
    caller=$WORK/nativelib-c-linked-caller
    LOG=$WORK/nativelib-c-linked-cc.log
    if ! "$cc" -DLINKED "$ROOT/sdk/test/fixtures/nativelib-c/main.c" "$NATIVELIB" -Wl,-rpath,"$(dirname "$NATIVELIB")" -o "$caller" > "$LOG" 2>&1; then
        fail "$name" "the C caller didn't link"; return
    fi
    out=$("$caller" 2>&1)
    if [ "$out" != "$(head -8 "$ROOT/sdk/test/fixtures/nativelib-caller/expected.txt" | tr -d '\r')" ]; then fail "$name" "printed: $(echo $out)"; return; fi
    pass "$name"
}

# A zero program loads a zero native library with dlopen: each keeps its own
# zerocore and statics.
test_native_library_with_zero_exe() {
    local name="native library with zero exe" dir lib out
    can_build "$name" || return
    if [ "$HOST_OS" = Windows ]; then skip "$name" "zero programs import at link time; dlopen is Linux and macOS"; return; fi
    lib=$(fixture nativelib nativelib-zeroexe-lib)
    if ! build_nativelib "$lib" nativelib-zeroexe-lib; then fail "$name" "the library's dotnet build failed"; return; fi
    dir=$(fixture nativelib-zero-caller)
    if ! run_in "$dir" nativelib-zero-caller build; then fail "$name" "the program's dotnet build failed"; return; fi
    out=$("$dir/bin/Debug/$TFM/nativelib-zero-caller" "$NATIVELIB" 2>&1)
    if [ "$out" != $'add 5\nremember 3\nown 1' ]; then fail "$name" "printed: $(echo $out)"; return; fi
    pass "$name"
}

# ZeroLinkLibrary: a zero program linked with a C library (here the nativelib
# fixture, built as libzlink), by name from ZeroLibraryPath and by path, and
# the errors for one that isn't there. The program binds the import when it's
# linked; at run time the system's loader finds the library.
test_link_library() {
    local name="link library" lib app native out implib
    can_build "$name" || return
    lib=$(fixture nativelib linklib-lib)
    if ! run_in "$lib" linklib-lib build -p:AssemblyName=libzlink; then fail "$name" "the library didn't build"; return; fi
    native=$lib/bin/Debug/$TFM/native
    # What a program links with: the library itself, or on Windows its import library.
    implib=$native/libzlink$HOST_LIB_EXT
    [ "$HOST_OS" = Windows ] && implib=$native/libzlink.lib
    app=$(fixture linklib-app)
    run_app() {
        case "$HOST_OS" in
            Windows) cp "$native/libzlink.dll" "$app/bin/Debug/$TFM/" && "$(exe "$app/bin/Debug/$TFM/linklib-app")" | tr -d '\r' ;;
            Darwin)  DYLD_LIBRARY_PATH=$native "$app/bin/Debug/$TFM/linklib-app" ;;
            *)       LD_LIBRARY_PATH=$native "$app/bin/Debug/$TFM/linklib-app" ;;
        esac
    }
    # By path, with the import named by DllImport.
    if ! run_in "$app" linklib-path build -v:n "-p:ZlinkPath=$(native_path "$implib")"; then fail "$name" "the build by path failed"; return; fi
    if ! grep -q -- "-i zlink" "$LOG"; then fail "$name" "bflat didn't get -i zlink"; return; fi
    out=$(run_app); if [ "$out" != "add 5" ]; then fail "$name" "by path, printed '$out'"; return; fi
    # By name: libzlink.so or .dylib in ZeroLibraryPath (Windows looks for zlink.lib).
    if [ "$HOST_OS" != Windows ]; then
        rm -rf "$app/bin" "$app/obj"
        if ! run_in "$app" linklib-name build "-p:ZeroLibraryPath=$native"; then fail "$name" "the build by name failed"; return; fi
        out=$(run_app); if [ "$out" != "add 5" ]; then fail "$name" "by name, printed '$out'"; return; fi
        # The library is an input: rebuilding it relinks the program.
        touch "$native/libzlink$HOST_LIB_EXT"
        if ! run_in "$app" linklib-relink build -v:n "-p:ZeroLibraryPath=$native" || ! grep -q "bflat arguments" "$LOG"; then
            fail "$name" "a newer library didn't relink"; return
        fi
    fi
    # Not found: by name, and by a path that doesn't exist.
    if run_in "$app" linklib-missing build "-p:ZeroLibraryPath=$WORK/nowhere"; then fail "$name" "built with no zlink anywhere"; return; fi
    if ! grep -q "ZERO010" "$LOG" || ! grep -q "zlink" "$LOG"; then fail "$name" "no ZERO010 naming zlink"; return; fi
    if run_in "$app" linklib-badpath build "-p:ZlinkPath=$(native_path "$WORK/nowhere/libzlink.so")"; then fail "$name" "built with a missing file"; return; fi
    if ! grep -q "ZERO010" "$LOG"; then fail "$name" "no ZERO010 for the missing file"; return; fi
    # Building for another system searches only ZeroLibraryPath.
    if run_in "$app" linklib-cross build -p:ZeroTarget=uefi-x64; then fail "$name" "the uefi-x64 build found zlink"; return; fi
    if ! grep -q "only ZeroLibraryPath is searched" "$LOG"; then fail "$name" "no ZERO010 asking for ZeroLibraryPath"; return; fi
    pass "$name"
}

# sdl3_package <dir> <feed>: the SDL 3 bindings (with SDL's Windows files,
# fetched once into the source folder) copied to <dir>/Sdl3, version filled
# in, packed into <feed>.
sdl3_package() {
    local dir=$1 feed=$2
    rm -rf "$dir" "$feed" "${NUGET_PACKAGES:-$HOME/.nuget/packages}/zerocsharp.sdl3"
    mkdir -p "$dir"
    "$ROOT/sdk/ZeroCSharp.SDL3/fetch-windows-sdl.sh" > "$WORK/sdl3-fetch.log" 2>&1 || { cat "$WORK/sdl3-fetch.log"; return 1; }
    cp -R "$ROOT/sdk/ZeroCSharp.SDL3" "$dir/Sdl3"
    rm -rf "$dir/Sdl3/obj" "$dir/Sdl3/bin"    # pack.sh's stage, an earlier build
    sed -i.bak "s|@ZERO_SDK_VERSION@|$VERSION|g" "$dir/Sdl3/ZeroCSharp.SDL3.csproj" && rm -f "$dir/Sdl3/ZeroCSharp.SDL3.csproj.bak"
    fixture hello "$(basename "$dir")-config" > /dev/null    # just for its nuget.config
    cp "$WORK/$(basename "$dir")-config/nuget.config" "$dir/"
    run_in "$dir/Sdl3" "$(basename "$dir")-pack" pack -o "$feed"
}

# A C library built against the system's C library, newer than the one
# bflat links with (exp@GLIBC_2.29 here; SDL built on Ubuntu 24.04 has
# GLIBC_2.34 and later): the program links, and runs on this system.
test_link_library_newer_glibc() {
    local name="link library newer glibc" dir app out cc
    linux_only "$name" || return
    cc=$(command -v cc || command -v gcc || command -v clang) || { skip "$name" "no C compiler"; return; }
    dir=$WORK/linklib-glibc
    rm -rf "$dir" && mkdir -p "$dir"
    printf '%s\n' '#include <math.h>' 'double nl_exp(double x) { return exp(x); }' > "$dir/zexp.c"
    if ! "$cc" -O0 -shared -fPIC -o "$dir/libzexp.so" "$dir/zexp.c" -lm > "$WORK/linklib-glibc-cc.log" 2>&1; then fail "$name" "the C library didn't build"; return; fi
    if ! LC_ALL=C grep -aq 'GLIBC_2\.\(29\|3[0-9]\)' "$dir/libzexp.so"; then skip "$name" "this system's exp isn't newer than bflat's"; return; fi
    app=$(fixture linklib-app linklib-glibc/App)
    cat > "$app/Program.cs" <<'CS'
using System.Runtime.InteropServices;

public static class Program
{
    [DllImport("zexp")] static safe extern double nl_exp(double x);

    public static int Main()
    {
        Out.Line($"exp {(int)(nl_exp(2.0) * 1000)}");
        Out.Flush();
        return 0;
    }
}
CS
    printf '%s\n' "<Project Sdk=\"ZeroCSharp.Sdk/$VERSION\">" '  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>' \
        '  <ItemGroup><ZeroLinkLibrary Include="zexp" /></ItemGroup>' '</Project>' > "$app/linklib-app.csproj"
    if ! run_in "$app" linklib-glibc build "-p:ZeroLibraryPath=$dir"; then fail "$name" "the program didn't link"; return; fi
    out=$(LD_LIBRARY_PATH=$dir "$app/bin/Debug/$TFM/linklib-app"); if [ "$out" != "exp 7389" ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# A library's ZeroLinkLibrary items reach the programs using it: packed into
# its props file (names as they are, files inside the project packed with
# it, others not, with a warning), and through a ProjectReference.
test_link_library_passed_on() {
    local name="link library passed on" dir=$WORK/linklib-pass list props
    can_build "$name" || return
    if ! sdl3_package "$dir" "$WORK/linklib-feed"; then fail "$name" "the library didn't pack"; return; fi
    list=$(zip_list "$WORK/linklib-feed/ZeroCSharp.SDL3.$VERSION.nupkg")
    for f in buildTransitive/ZeroCSharp.SDL3.props native/win-x64/SDL3.lib native/win-x64/SDL3.dll native/win-x64/SDL-LICENSE.txt; do
        if ! grep -qx "$f" <<< "$list"; then fail "$name" "the package has no $f"; return; fi
    done
    # The props file as packed (the pack wrote it under obj/).
    props=$(cat "$dir"/Sdl3/obj/*/"$TFM"/zero-pack/ZeroCSharp.SDL3.props)
    if ! grep -q '<ZeroLinkLibrary Include="SDL3" ' <<< "$props"; then fail "$name" "the props don't name SDL3"; return; fi
    if ! grep -qF '<ZeroLinkLibrary Include="$(MSBuildThisFileDirectory)../native/win-x64/SDL3.lib" DllImport="SDL3" ZeroTarget="win-x64" RuntimeFile="$(MSBuildThisFileDirectory)../native/win-x64/SDL3.dll" />' <<< "$props"; then
        fail "$name" "the props don't point at the packed Windows files: $props"; return
    fi
    # A file outside the project isn't packed.
    sed -i.bak 's|<ZeroLinkLibrary Include="SDL3" />|<ZeroLinkLibrary Include="/no/such/libSDL3.so" />|' "$dir/Sdl3/ZeroCSharp.SDL3.csproj" && rm -f "$dir/Sdl3/ZeroCSharp.SDL3.csproj.bak"
    if ! run_in "$dir/Sdl3" linklib-pack-path pack -o "$WORK/linklib-feed2"; then fail "$name" "the library with a path didn't pack"; return; fi
    if ! grep -q "ZERO011" "$LOG"; then fail "$name" "no ZERO011 for a path"; return; fi
    # Through a ProjectReference: on Windows, the package's own files.
    sed -i.bak 's|<ZeroLinkLibrary Include="/no/such/libSDL3.so" />|<ZeroLinkLibrary Include="SDL3" />|' "$dir/Sdl3/ZeroCSharp.SDL3.csproj" && rm -f "$dir/Sdl3/ZeroCSharp.SDL3.csproj.bak"
    cp -R "$ROOT/sdk/test/fixtures/sdl3-app" "$dir/App"
    cp "$ROOT/sdk/ZeroCSharp.Templates/content/zerosdl/Program.cs" "$dir/App/"
    sed -i.bak 's|<PackageReference Include="ZeroCSharp.SDL3" Version="[^"]*" />|<ProjectReference Include="../Sdl3/ZeroCSharp.SDL3.csproj" />|; s|@ZERO_SDK_VERSION@|'"$VERSION"'|g' "$dir/App/App.csproj" && rm -f "$dir/App/App.csproj.bak"
    if ! run_in "$dir/App" linklib-projref build -v:n -p:ZeroTarget=win-x64; then fail "$name" "the win-x64 program didn't build through the reference"; return; fi
    if ! grep -q -- "-i SDL3" "$LOG" || [ ! -f "$dir/App/bin/Debug/$TFM/win-x64/SDL3.dll" ]; then fail "$name" "the program didn't get SDL3 from its reference"; return; fi
    pass "$name"
}

# The SDL 3 bindings package on Windows, built from any host: SDL's import
# library from the package, its DLL next to the program and in the publish
# folder; run on Windows (SDL's dummy video driver: CI has no display).
test_sdl3_windows() {
    local name="sdl3 windows" dir=$WORK/sdl3-win app out
    can_build "$name" || return
    if ! sdl3_package "$dir" "$WORK/sdl3-win-feed"; then fail "$name" "the bindings didn't pack"; return; fi
    app=$(fixture sdl3-app sdl3-win/App)
    cp "$ROOT/sdk/ZeroCSharp.Templates/content/zerosdl/Program.cs" "$app/"
    add_source "$app/nuget.config" sdl3 "$WORK/sdl3-win-feed"
    if ! run_in "$app" sdl3-win-build build -p:ZeroTarget=win-x64; then fail "$name" "the program didn't build"; return; fi
    if [ ! -f "$app/bin/Debug/$TFM/win-x64/SDL3.dll" ]; then fail "$name" "no SDL3.dll next to the program"; return; fi
    if ! run_in "$app" sdl3-win-publish publish -p:ZeroTarget=win-x64 -o "$(native_path "$app/pub")"; then fail "$name" "publish failed"; return; fi
    if [ ! -f "$app/pub/App.exe" ] || [ ! -f "$app/pub/SDL3.dll" ]; then fail "$name" "the publish folder lacks App.exe or SDL3.dll"; return; fi
    if ! ls "$app/bin/Debug/$TFM" | grep -qx ZeroCSharp.SDL3.dll; then fail "$name" "the bindings' IL isn't ZeroCSharp.SDL3.dll"; return; fi
    if [ "$HOST_OS" = Windows ]; then
        out=$(SDL_VIDEO_DRIVER=dummy SDL_RENDER_DRIVER=software "$app/pub/App.exe" --frames 30 2>&1 | tr -d '\r')
        if [ "$out" != "30 frames, square at 330,220" ]; then fail "$name" "printed '$out'"; return; fi
    fi
    pass "$name"
}

# The SDL 3 bindings package with the system's SDL 3, where it's installed:
# a program using it builds, and runs a few frames (Linux: offscreen; macOS:
# a real window).
test_sdl3() {
    local name="sdl3" dir=$WORK/sdl3 app out
    can_build "$name" || return
    case "$HOST_OS" in
        Linux)  compgen -G '/usr/lib/*/libSDL3.so' > /dev/null || [ -f /usr/local/lib/libSDL3.so ] || [ -f /usr/lib/libSDL3.so ] \
                    || { skip "$name" "SDL 3 isn't installed (libsdl3-dev)"; return; } ;;
        Darwin) [ -f /opt/homebrew/lib/libSDL3.dylib ] || { skip "$name" "SDL 3 isn't installed (brew install sdl3)"; return; } ;;
        *)      skip "$name" "Windows uses the package's own SDL (sdl3_windows)"; return ;;
    esac
    if ! sdl3_package "$dir" "$WORK/sdl3-feed"; then fail "$name" "the bindings didn't pack"; return; fi
    app=$(fixture sdl3-app sdl3/App)
    cp "$ROOT/sdk/ZeroCSharp.Templates/content/zerosdl/Program.cs" "$app/"
    add_source "$app/nuget.config" sdl3 "$WORK/sdl3-feed"
    if ! run_in "$app" sdl3-app build; then fail "$name" "the program didn't build"; return; fi
    if ls "$app/bin/Debug/$TFM" | grep -qx SDL3.dll; then fail "$name" "the Windows DLL was copied for $HOST_OS"; return; fi
    if [ "$HOST_OS" = Linux ]; then
        out=$(SDL_VIDEO_DRIVER=offscreen "$app/bin/Debug/$TFM/App" --frames 30 2>&1)
    else
        out=$("$app/bin/Debug/$TFM/App" --frames 30 2>&1)
    fi
    if [ "$out" != "30 frames, square at 330,220" ]; then fail "$name" "printed '$out'"; return; fi
    pass "$name"
}

# Every host builds the library for all four targets. CI runs the results on
# their own systems with sdk/test/call-native-lib.sh (sdk.yml: mac-programs,
# windows-built-linux, macos-built), from the folders these are copied to.
test_native_library_cross() {
    local name="native library cross" dir t f
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-cross)
    for t in linux-x64 linux-arm64 win-x64 osx-arm64; do
        if ! run_in "$dir" "nativelib-cross-$t" build -p:ZeroTarget=$t; then fail "$name" "the $t build failed"; return; fi
        case "$t" in
            linux-*)   f=$dir/bin/Debug/$TFM/$t/native/nativelib.so; is_elf_shared "$f" ;;
            win-x64)   f=$dir/bin/Debug/$TFM/$t/native/nativelib.dll; is_pe_dll "$f" ;;
            osx-arm64) f=$dir/bin/Debug/$TFM/$t/native/nativelib.dylib; is_macho_dylib "$f" ;;
        esac || { fail "$name" "$t's library isn't a $t shared library"; return; }
        case "$HOST_OS:$t" in
            Windows:linux-*)  mkdir -p "$WORK/windows-built/$t" && cp "$f" "$WORK/windows-built/$t/" ;;
            Darwin:linux-* | Darwin:win-x64) mkdir -p "$WORK/macos-built/$t" && cp "$f" "$WORK/macos-built/$t/" ;;
            *:osx-arm64)      [ "$HOST_OS" != Darwin ] && mkdir -p "$WORK/mac-built/$HOST_RID" && cp "$f" "$WORK/mac-built/$HOST_RID/" ;;
        esac
    done
    pass "$name"
}

# A name with a space: the output, the soname (Linux) and the install name
# (macOS) carry it whole.
test_native_library_name_with_spaces() {
    local name="native library name with spaces" dir lib
    can_build "$name" || return
    dir=$(fixture nativelib nativelib-spacename)
    if ! run_in "$dir" nativelib-spacename build "-p:AssemblyName=My Lib"; then fail "$name" "dotnet build failed"; return; fi
    lib="$dir/bin/Debug/$TFM/native/My Lib$HOST_LIB_EXT"
    if [ ! -f "$lib" ]; then fail "$name" "no native/My Lib$HOST_LIB_EXT"; return; fi
    if ! call_native "$lib"; then fail "$name" "the .NET caller didn't get what it should"; return; fi
    pass "$name"
}

# References the native-library check must let through: one only for build
# order (ReferenceOutputAssembly=false), and one to a project that doesn't
# exist (MSBuild warns about that one; the check mustn't make it an error).
test_native_library_reference_kinds() {
    local name="native library reference kinds" dir
    can_build "$name" || return
    dir=$(fixture native nativelib-order/App)
    fixture nativelib nativelib-order/nativelib > /dev/null
    sed -i.bak 's|</Project>|  <ItemGroup><ProjectReference Include="../nativelib/nativelib.csproj" ReferenceOutputAssembly="false" /></ItemGroup>\n</Project>|' "$dir/native.csproj" && rm -f "$dir/native.csproj.bak"
    if ! run_in "$dir" nativelib-order build; then fail "$name" "a build-order reference to a native library was refused"; return; fi
    if [ ! -f "$WORK/nativelib-order/nativelib/bin/Debug/$TFM/native/nativelib$HOST_LIB_EXT" ]; then fail "$name" "the build-order reference didn't build the library"; return; fi
    dir=$(fixture native nativelib-missing)
    sed -i.bak 's|</Project>|  <ItemGroup><ProjectReference Include="../nowhere/nowhere.csproj" /></ItemGroup>\n</Project>|' "$dir/native.csproj" && rm -f "$dir/native.csproj.bak"
    if run_in "$dir" nativelib-missing build; then :; fi
    if grep -q "MSB3202" "$LOG"; then fail "$name" "a missing project reference became an error (MSB3202)"; return; fi
    pass "$name"
}

# The docs' recipe: an ordinary .NET program whose ProjectReference builds the
# library and copies it next to the program (Targets="ZeroNativeLibraryFiles"),
# and which compiles the bindings the library's build writes, from a clean
# folder; .NET then finds the library by name. Publish takes the library too.
test_native_library_app_recipe() {
    local name="native library app recipe" dir out
    can_build "$name" || return
    dir=$(fixture nativelib-app)
    fixture nativelib nativelib-app/MathLib > /dev/null
    mv "$dir/MathLib/nativelib.csproj" "$dir/MathLib/MathLib.csproj"
    if ! run_in "$dir/App" nativelib-app build; then fail "$name" "dotnet build failed"; return; fi
    out=$(dotnet "$dir/App/bin/Debug/net10.0/App.dll" 2>&1 | tr -d '\r')
    if [ "$out" != $'add 5\noffset 11,22' ]; then fail "$name" "printed: $out"; return; fi
    if ! run_in "$dir/App" nativelib-app-publish publish -o "$dir/out"; then fail "$name" "dotnet publish failed"; return; fi
    if [ ! -f "$dir/out/MathLib$HOST_LIB_EXT" ]; then fail "$name" "no MathLib$HOST_LIB_EXT in the publish folder"; return; fi
    out=$(dotnet "$dir/out/App.dll" 2>&1 | tr -d '\r')
    if [ "$out" != $'add 5\noffset 11,22' ]; then fail "$name" "published, printed: $out"; return; fi
    pass "$name"
}

# The recipe as Visual Studio builds it, simulated: it builds the solution's
# projects itself (here, the library first) and asks a reference only for
# GetTargetPath, given the solution's configuration. The library answers with
# its native file, which is copied next to the program.
test_native_library_visual_studio() {
    local name="native library visual studio" dir lib app out
    can_build "$name" || return
    dir=$(fixture nativelib-app nativelib-vs)
    fixture nativelib nativelib-vs/MathLib > /dev/null
    mv "$dir/MathLib/nativelib.csproj" "$dir/MathLib/MathLib.csproj"
    lib=$(native_path "$dir/MathLib/MathLib.csproj"); app=$(native_path "$dir/App/App.csproj")
    cat > "$WORK/nativelib-vs.rsp" <<EOF
-p:BuildingInsideVisualStudio=true
"-p:CurrentSolutionConfigurationContents=<SolutionConfiguration><ProjectConfiguration Project=\\"{11111111-1111-1111-1111-111111111111}\\" AbsolutePath=\\"$lib\\" BuildProjectInSolution=\\"True\\">Debug|AnyCPU</ProjectConfiguration><ProjectConfiguration Project=\\"{22222222-2222-2222-2222-222222222222}\\" AbsolutePath=\\"$app\\" BuildProjectInSolution=\\"True\\">Debug|AnyCPU</ProjectConfiguration></SolutionConfiguration>"
EOF
    if ! run_in "$dir/MathLib" nativelib-vs-lib build "@$(native_path "$WORK/nativelib-vs.rsp")"; then fail "$name" "the library's build failed"; return; fi
    if ! run_in "$dir/App" nativelib-vs-app build "@$(native_path "$WORK/nativelib-vs.rsp")"; then fail "$name" "the program's build failed"; return; fi
    if [ ! -f "$dir/App/bin/Debug/net10.0/MathLib$HOST_LIB_EXT" ]; then fail "$name" "MathLib$HOST_LIB_EXT wasn't copied to the program"; return; fi
    out=$(dotnet "$dir/App/bin/Debug/net10.0/App.dll" 2>&1 | tr -d '\r')
    if [ "$out" != $'add 5\noffset 11,22' ]; then fail "$name" "printed: $out"; return; fi
    pass "$name"
}

# The bindings (bin/interoplib.Interop.cs) for every kind of type they copy,
# called by a .NET program that treats warnings as errors, through
# [LibraryImport] and again through [DllImport]; an export they can't declare
# is left out with ZERO008. The file is rewritten only when it
# changes, and dotnet clean removes it.
test_native_library_bindings() {
    local name="native library bindings" dir bindings generated
    can_build "$name" || return
    dir=$(fixture interoplib-app native-bindings/interoplib-app)
    fixture interoplib native-bindings/interoplib > /dev/null
    bindings=$WORK/native-bindings/interoplib/bin/interoplib.Interop.cs
    if ! run_in "$dir" native-bindings build; then fail "$name" "dotnet build failed"; return; fi
    if ! grep -q "ZERO008.*calendar_year.*CalendarTime" "$LOG"; then fail "$name" "no ZERO008 for calendar_year"; return; fi
    if grep -q "calendar_year" "$bindings"; then fail "$name" "the bindings declare calendar_year"; return; fi
    dotnet "$dir/bin/Debug/net10.0/interoplib-app.dll" > "$WORK/native-bindings.out" 2>&1
    if ! same_output "$WORK/native-bindings.out" "$dir/expected.txt" "$WORK/native-bindings.diff"; then
        fail "$name" "$(cat "$WORK/native-bindings.diff")"; return
    fi
    # [LibraryImport]: its generator wrote a file with the calls.
    generated=$dir/obj/Debug/net10.0/generated/Microsoft.Interop.LibraryImportGenerator
    if ! grep -rqs "shape_move" "$generated"; then fail "$name" "the bindings didn't use [LibraryImport]"; return; fi
    # ZERO_INTEROP_DLLIMPORT turns them back to [DllImport].
    rm -rf "$generated"
    if ! run_in "$dir" native-bindings-dllimport build -p:InteropDllImport=true; then fail "$name" "the [DllImport] build failed"; return; fi
    if grep -rqs "shape_move" "$generated"; then fail "$name" "ZERO_INTEROP_DLLIMPORT still used [LibraryImport]"; return; fi
    dotnet "$dir/bin/Debug/net10.0/interoplib-app.dll" > "$WORK/native-bindings.out" 2>&1
    if ! same_output "$WORK/native-bindings.out" "$dir/expected.txt" "$WORK/native-bindings.diff"; then
        fail "$name" "[DllImport]: $(cat "$WORK/native-bindings.diff")"; return
    fi
    sleep 1; touch "$WORK/native-bindings.stamp" "$WORK/native-bindings/interoplib/Api.cs"
    if ! run_in "$WORK/native-bindings/interoplib" native-bindings-2 build; then fail "$name" "the second build failed"; return; fi
    if [ "$bindings" -nt "$WORK/native-bindings.stamp" ]; then fail "$name" "unchanged bindings were written again"; return; fi
    if ! run_in "$WORK/native-bindings/interoplib" native-bindings-clean clean || [ -e "$bindings" ] || [ -e "${bindings%.cs}.fs" ]; then
        fail "$name" "dotnet clean left the bindings"; return
    fi
    pass "$name"
}

# ---- F# ----------------------------------------------------------------------

# An .fsproj: unions, records, lists, closures across two files, an
# [<EntryPoint>] that gets the arguments and returns the exit code.
test_fsharp_program() {
    local name="F# program" dir exe out code
    can_build "$name" || return
    dir=$(fixture fsapp)
    if ! run_in "$dir" fsapp build; then fail "$name" "dotnet build failed"; return; fi
    # The IL refers to zerocore by its own name, not the host's System.Runtime
    # (what fsc picks when it isn't given zerocore as System.Runtime.dll).
    if LC_ALL=C tr '\0' '\n' < "$dir/bin/Debug/$TFM/fsapp.dll" | grep -qx 'System.Runtime'; then
        fail "$name" "the IL references System.Runtime"; return
    fi
    exe=$(exe "$dir/bin/Debug/$TFM/fsapp")
    if ! is_host_program "$exe"; then fail "$name" "no program for $HOST_RID at $exe"; return; fi
    out=$("$exe" a b | tr -d '\r'); code=$?
    local want=$'args: 2\nsum: 100\nrecords equal\narea: 7\nF# zero'
    if [ "$out" != "$want" ]; then fail "$name" "printed: $out"; return; fi
    "$exe" > /dev/null; code=$?
    if [ "$code" != 7 ]; then fail "$name" "exit code $code, not 7"; return; fi
    pass "$name"
}

# Top-level code in the last file, no [<EntryPoint>]: fsc's main@, in a class
# C# can't name, is reached through [UnsafeAccessor]; dotnet run runs it.
test_fsharp_top_level() {
    local name="F# top-level program" dir out
    can_build "$name" || return
    dir=$(fixture fstoplevel)
    if ! run_in "$dir" fstoplevel run; then fail "$name" "dotnet run failed"; return; fi
    out=$(tr -d '\r' < "$LOG" | tail -1)
    if [ "$out" != "Hello, top-level F#!" ]; then fail "$name" "printed: $out"; return; fi
    pass "$name"
}

# printf, sprintf, ksprintf through an inline wrapper, interpolated strings,
# and up to ten arguments: the text .NET's printf gives (expected.txt), and
# eprintfn on standard error.
test_fsharp_printf() {
    local name="F# printf" dir exe
    can_build "$name" || return
    dir=$(fixture fsprintf)
    if ! run_in "$dir" fsprintf build; then fail "$name" "dotnet build failed"; return; fi
    exe=$(exe "$dir/bin/Debug/$TFM/fsprintf")
    "$exe" > "$WORK/fsprintf.out" 2> "$WORK/fsprintf.err"
    if ! same_output "$WORK/fsprintf.out" "$dir/expected.txt" "$WORK/fsprintf.diff"; then
        fail "$name" "printed: $(head -c 300 "$WORK/fsprintf.diff")"; return
    fi
    if [ "$(tr -d '\r' < "$WORK/fsprintf.err")" != "to standard error 1" ]; then fail "$name" "standard error: $(cat "$WORK/fsprintf.err")"; return; fi
    pass "$name"
}

# Sequence expressions, comprehensions, and the Seq, List, Array, Map, Set and
# String modules, against .NET's output; optimized (state machines for seq { })
# and not (Seq.delay, Seq.append and the others the compiler calls instead).
test_fsharp_collections() {
    local name="F# collections" dir exe opt
    can_build "$name" || return
    dir=$(fixture fscollections)
    for opt in true false; do
        rm -rf "$dir/bin" "$dir/obj"
        if ! run_in "$dir" fscollections build -p:Optimize=$opt; then fail "$name" "dotnet build failed (Optimize=$opt)"; return; fi
        exe=$(exe "$dir/bin/Debug/$TFM/fscollections")
        "$exe" > "$WORK/fscollections.out" 2>&1
        if ! same_output "$WORK/fscollections.out" "$dir/expected.txt" "$WORK/fscollections.diff"; then
            fail "$name" "printed (Optimize=$opt): $(head -c 300 "$WORK/fscollections.diff")"; return
        fi
    done
    pass "$name"
}

# Computation expressions with the program's own builders (let!, and!,
# for, while, use, try/finally, custom operations) and lazy values, against
# .NET's output; both ways fsc optimizes.
test_fsharp_builders() {
    local name="F# builders and lazy" dir exe opt
    can_build "$name" || return
    dir=$(fixture fsbuilders)
    for opt in true false; do
        rm -rf "$dir/bin" "$dir/obj"
        if ! run_in "$dir" fsbuilders build -p:Optimize=$opt; then fail "$name" "dotnet build failed (Optimize=$opt)"; return; fi
        exe=$(exe "$dir/bin/Debug/$TFM/fsbuilders")
        "$exe" > "$WORK/fsbuilders.out" 2>&1
        if ! same_output "$WORK/fsbuilders.out" "$dir/expected.txt" "$WORK/fsbuilders.diff"; then
            fail "$name" "printed (Optimize=$opt): $(head -c 300 "$WORK/fsbuilders.diff")"; return
        fi
    done
    pass "$name"
}

# task { }, async { } and MailboxProcessor (fsharp/control/Control.fs),
# against .NET's output; both ways fsc optimizes.
test_fsharp_async() {
    local name="F# async, task and MailboxProcessor" dir exe opt
    can_build "$name" || return
    dir=$(fixture fsasync)
    for opt in true false; do
        rm -rf "$dir/bin" "$dir/obj"
        if ! run_in "$dir" fsasync build -p:Optimize=$opt; then fail "$name" "dotnet build failed (Optimize=$opt)"; return; fi
        exe=$(exe "$dir/bin/Debug/$TFM/fsasync")
        "$exe" > "$WORK/fsasync.out" 2>&1
        if ! same_output "$WORK/fsasync.out" "$dir/expected.txt" "$WORK/fsasync.diff"; then
            fail "$name" "printed (Optimize=$opt): $(head -c 300 "$WORK/fsasync.diff")"; return
        fi
    done
    pass "$name"
}

# Arena.DebugChecks with agents: a message made inside an arena scope and
# still queued when it ends stops the program; one taken before is fine.
test_fsharp_arena_post() {
    local name="F# message left queued from an arena scope" dir exe opt
    can_build "$name" || return
    dir=$(fixture fsarenapost)
    for opt in true false; do
        rm -rf "$dir/bin" "$dir/obj"
        if ! run_in "$dir" fsarenapost build -p:Optimize=$opt; then fail "$name" "dotnet build failed (Optimize=$opt)"; return; fi
        exe=$(exe "$dir/bin/Debug/$TFM/fsarenapost")
        if "$exe" > "$WORK/fsarenapost.out" 2>&1; then fail "$name" "the program didn't stop (Optimize=$opt)"; return; fi
        if ! grep -q 'taken inside: 1' "$WORK/fsarenapost.out" \
            || ! grep -q "still in an agent's queue as the scope ends" "$WORK/fsarenapost.out" \
            || grep -q 'not here' "$WORK/fsarenapost.out"; then
            fail "$name" "printed (Optimize=$opt): $(head -c 300 "$WORK/fsarenapost.out")"; return
        fi
    done
    pass "$name"
}

# Catch.result and Catch.Run: a raise comes back, a file opened with use is
# closed after it; optimized and not.
test_fsharp_catch() {
    local name="F# Catch" dir exe opt
    can_build "$name" || return
    dir=$(fixture fscatch)
    for opt in true false; do
        rm -rf "$dir/bin" "$dir/obj"
        if ! run_in "$dir" fscatch build -p:Optimize=$opt; then fail "$name" "dotnet build failed (Optimize=$opt)"; return; fi
        exe=$(exe "$dir/bin/Debug/$TFM/fscatch")
        (cd "$WORK" && "$exe") > "$WORK/fscatch.out" 2>&1
        if ! same_output "$WORK/fscatch.out" "$dir/expected.txt" "$WORK/fscatch.diff"; then
            fail "$name" "printed (Optimize=$opt): $(head -c 300 "$WORK/fscatch.diff")"; return
        fi
    done
    pass "$name"
}

# decimal (zerocore's, against .NET's results) and the math functions.
test_fsharp_decimal() {
    local name="F# decimal" dir exe
    can_build "$name" || return
    dir=$(fixture fsdecimal)
    if ! run_in "$dir" fsdecimal build; then fail "$name" "dotnet build failed"; return; fi
    exe=$(exe "$dir/bin/Debug/$TFM/fsdecimal")
    "$exe" > "$WORK/fsdecimal.out" 2>&1
    if ! same_output "$WORK/fsdecimal.out" "$dir/expected.txt" "$WORK/fsdecimal.diff"; then
        fail "$name" "printed: $(head -c 300 "$WORK/fsdecimal.diff")"; return
    fi
    pass "$name"
}

# Memory lifetimes from F#: an arena, F# lists and closures in an object
# scope (static constructors that first run inside it allocate outside it,
# or DebugChecks stops the program), Arena.Scratch. Both ways fsc optimizes.
test_fsharp_memory() {
    local name="F# memory" dir exe opt
    can_build "$name" || return
    dir=$(fixture fsmemory)
    for opt in true false; do
        rm -rf "$dir/bin" "$dir/obj"
        if ! run_in "$dir" fsmemory build -p:Optimize=$opt; then fail "$name" "dotnet build failed (Optimize=$opt)"; return; fi
        exe=$(exe "$dir/bin/Debug/$TFM/fsmemory")
        "$exe" > "$WORK/fsmemory.out" 2>&1
        if ! same_output "$WORK/fsmemory.out" "$dir/expected.txt" "$WORK/fsmemory.diff"; then
            fail "$name" "printed (Optimize=$opt): $(head -c 300 "$WORK/fsmemory.diff")"; return
        fi
    done
    pass "$name"
}

# sdk/test/fixtures/fs-must-not-compile/cases/*.fs: F# programs the build
# must refuse, each with the error it must give on its first line
# (// expect: FS0406). They check that nothing from an Arena.Scratch scope
# can outlive the callback: by fsc's rules for ref structs, and, for a ref
# struct as the by-ref state, where F# ignores C#'s `scoped`, by the SDK's
# own check (ZERO006).
test_fsharp_must_not_compile() {
    local name="F# must not compile" dir file case expected failures=""
    dir=$(fixture fs-must-not-compile)
    for file in "$dir"/cases/*.fs; do
        case=$(basename "$file" .fs)
        expected=$(sed -n '1s|^// expect: ||p' "$file")
        cp "$file" "$dir/Program.fs"
        if run_in "$dir" "fs-must-not-compile-$case" build; then
            failures+=" $case (it built)"
        elif [ -z "$expected" ] || ! grep -q "error $expected" "$LOG"; then
            failures+=" $case (not refused with ${expected:-an // expect: line})"
        fi
    done
    if [ -n "$failures" ]; then fail "$name" "$failures"; return; fi
    pass "$name"
}

# ZERO006 is for libraries too: the check runs after every F# compile.
test_fsharp_scratch_ref_struct_state() {
    local name="F# Scratch ref struct state in a library" dir
    dir=$(fixture fsmemory fsmemory-own-state)
    sed -i.bak 's|<OutputType>Exe</OutputType>|<OutputType>Library</OutputType>|' "$dir/fsmemory.fsproj" && rm -f "$dir/fsmemory.fsproj.bak"
    cat > "$dir/Program.fs" <<'EOF'
module Leak
// Not a record: fsc after 10.0.402 gives records a ToString that boxes them,
// which a ref struct can't be (FS0412).
[<Struct; System.Runtime.CompilerServices.IsByRefLike>]
type Holder =
    val mutable Kept: System.Span<int>
let leak () =
    let mutable h = Holder()
    Arena.Scratch(&h, ScratchAction<Holder>(fun scratch state -> state.Kept <- scratch.AllocSpan<int>(4)))
    h.Kept.Length
EOF
    if run_in "$dir" fsmemory-own-state build; then fail "$name" "a library with a ref struct state built"; return; fi
    if ! grep -q 'ZERO006.*Holder, as its state' "$LOG"; then fail "$name" "no ZERO006 for the library's Holder state"; return; fi
    pass "$name"
}

# ZERO007: F# calls to members C# marks `unsafe` are warned about unless the
# function is marked [<Safety "why">] or [<RequiresUnsafe>]. The fixture tags
# each line that must warn with "// warns"; exactly those are reported, with
# their file and line, whichever way fsc optimizes (a small marked function
# isn't copied into its callers), and the program builds and runs. A build
# with nothing changed warns again: warnings stay until they're dealt with.
test_fsharp_unsafe_calls() {
    local name="F# unsafe calls" dir exe opt want got
    can_build "$name" || return
    dir=$(fixture fsunsafe)
    want=$(grep -n '// warns' "$dir/Program.fs" | cut -d: -f1 | tr '\n' ' ')
    for opt in true false; do
        rm -rf "$dir/bin" "$dir/obj"
        if ! run_in "$dir" fsunsafe build -p:Optimize=$opt; then fail "$name" "dotnet build failed (Optimize=$opt)"; return; fi
        got=$(tr -d '\r' < "$LOG" | sed -n 's/.*Program\.fs(\([0-9]*\),[0-9]*): warning ZERO007.*/\1/p' | sort -un | tr '\n' ' ')
        if [ "$got" != "$want" ]; then fail "$name" "warned for lines $got, not $want (Optimize=$opt)"; return; fi
        if ! grep -q 'warning ZERO007: Program.callsUnsafeFunction calls Program.endFrames, which is unsafe' "$LOG"; then
            fail "$name" "the call to an F# [<RequiresUnsafe>] function isn't named (Optimize=$opt)"; return
        fi
        if ! grep -q 'warning ZERO007: Program.unmarkedLambda calls Arena.OpenScope (in a lambda' "$LOG"; then
            fail "$name" "a lambda's call isn't reported for its function (Optimize=$opt)"; return
        fi
        exe=$(exe "$dir/bin/Debug/$TFM/fsunsafe")
        "$exe" > "$WORK/fsunsafe.out" 2>&1
        if ! same_output "$WORK/fsunsafe.out" "$dir/expected.txt" "$WORK/fsunsafe.diff"; then
            fail "$name" "printed (Optimize=$opt): $(head -c 300 "$WORK/fsunsafe.diff")"; return
        fi
    done
    if ! run_in "$dir" fsunsafe-again build; then fail "$name" "the second build failed"; return; fi
    if ! grep -q 'warning ZERO007' "$LOG"; then fail "$name" "a build with nothing changed didn't warn again"; return; fi
    # As an error, it fails every build, not only the first.
    if run_in "$dir" fsunsafe-error build -p:WarningsAsErrors=ZERO007; then fail "$name" "built with WarningsAsErrors=ZERO007"; return; fi
    if run_in "$dir" fsunsafe-error-again build -p:WarningsAsErrors=ZERO007; then fail "$name" "the second build with WarningsAsErrors=ZERO007 passed"; return; fi
    pass "$name"
}

# ZERO007 across libraries: a C# zero library's own `unsafe` method, and an
# F# library's [<RequiresUnsafe>] function, are unsafe to call from F#; an F#
# library's function with [<Safety>] is not, and the library itself builds
# without warnings.
test_fsharp_unsafe_calls_libraries() {
    local name="F# unsafe calls into libraries" dir out
    can_build "$name" || return
    dir=$(fixture fs-with-libs fs-unsafe-libs)
    cat > "$dir/CsLib/Raw.cs" <<'EOF'
namespace CsLib;

public static class Raw
{
    /// <safety>index must be inside the array.</safety>
    public static unsafe int At(int[] items, int index)
    {
        unsafe { fixed (int* p = items) return p[index]; } // SAFETY: the caller's promise.
    }

    public static int First(int[] items) => items[0];
}
EOF
    cat >> "$dir/FsLib/Tree.fs" <<'EOF'

module Frames =
    let frame = Arena.Create(1024un)

    // Not small: fsc would copy a small one into the caller.
    [<RequiresUnsafe>]
    let reset (times: int) =
        for _ in 1 .. times do
            frame.Reset()
        for _ in 1 .. times do
            frame.Reset()

    [<Safety "the frame is this module's own and holds nothing">]
    let clear () = frame.Reset()
EOF
    cat > "$dir/App/Program.fs" <<'EOF'
module Program

[<Safety "index 0 of an array of one">]
let marked () = CsLib.Raw.At([| 7 |], 0)

[<EntryPoint>]
let main _ =
    let second = CsLib.Raw.At([| 1; 2 |], 1)
    FsLib.Frames.reset 1
    FsLib.Frames.clear ()
    Out.Line("libraries: ", int64 (second + marked () + CsLib.Raw.First([| 3 |])))
    0
EOF
    if ! run_in "$dir/App" fs-unsafe-libs run; then fail "$name" "dotnet run failed"; return; fi
    out=$(tr -d '\r' < "$LOG" | tail -1)
    if [ "$out" != "libraries: 12" ]; then fail "$name" "printed: $out"; return; fi
    if ! grep -q 'Program\.fs(8,.*warning ZERO007: Program.main calls Raw.At, which is unsafe' "$LOG"; then fail "$name" "no ZERO007 for the C# library's unsafe method"; return; fi
    if ! grep -q 'Program\.fs(9,.*warning ZERO007: Program.main calls Frames.reset, which is unsafe' "$LOG"; then fail "$name" "no ZERO007 for the F# library's [<RequiresUnsafe>] function"; return; fi
    if [ "$(grep -c 'warning ZERO007' "$LOG")" != "$(grep -c 'Program\.fs([89],.*warning ZERO007' "$LOG")" ]; then
        fail "$name" "other ZERO007 warnings: $(grep 'warning ZERO007' "$LOG" | grep -v 'Program\.fs([89],' | head -c 300)"; return
    fi
    pass "$name"
}

# An F# program referencing a C# zero library and an F# one.
test_fsharp_libraries() {
    local name="F# with libraries" dir out
    can_build "$name" || return
    dir=$(fixture fs-with-libs)
    if ! run_in "$dir/App" fs-with-libs run; then fail "$name" "dotnet run failed"; return; fi
    out=$(tr -d '\r' < "$LOG" | tail -3)
    if [ "$out" != $'C# library: 42\nF# library: 17\nfound 8' ]; then fail "$name" "printed: $out"; return; fi
    pass "$name"
}

# The other targets: bflat writes the program under another name (it names
# the C# entry program's assembly after it), the build copies it.
test_fsharp_cross_targets() {
    local name="F# cross targets" dir other other_arch
    can_build "$name" || return
    dir=$(fixture fsapp fs-cross)
    if ! run_in "$dir" fs-cross-win build -p:ZeroTarget=win-x64; then fail "$name" "win-x64 build failed"; return; fi
    if ! is_pe "$dir/bin/Debug/$TFM/win-x64/fsapp.exe"; then fail "$name" "fsapp.exe isn't a Windows program"; return; fi
    if [ "$HOST_ARCH_NAME" = x64 ]; then other=linux-arm64; other_arch=aarch64; else other=linux-x64; other_arch=x86-64; fi
    if ! run_in "$dir" fs-cross-linux build -p:ZeroTarget=$other; then fail "$name" "$other build failed"; return; fi
    if ! is_elf "$dir/bin/Debug/$TFM/$other/fsapp" "$other_arch"; then fail "$name" "not an ELF for $other_arch"; return; fi
    if ! run_in "$dir" fs-cross-uefi build -p:ZeroTarget=uefi-x64; then fail "$name" "uefi-x64 build failed"; return; fi
    if ! is_efi_app "$dir/bin/Debug/$TFM/uefi-x64/fsapp.efi" x64; then fail "$name" "fsapp.efi isn't a UEFI application"; return; fi
    pass "$name"
}

# The C64 game in F# (samples/Paddles.fs): it builds for the C64 and fits;
# where VICE and ./setup.sh --c64's tools are, it plays its test match, which
# must be the C# game's (tests/expected/Paddles.txt): the same score, hits
# and frames, and nothing allocated while playing.
test_fsharp_c64() {
    local name="F# on the C64" dir prg size
    can_build "$name" || return
    case "$HOST_RID" in
        linux-x64|win-x64) ;;
        osx-arm64)
            if ! docker info > /dev/null 2>&1; then skip "$name" "on a Mac the C64 needs Docker, which isn't running"; return; fi ;;
        *) skip "$name" "not a host for the C64"; return ;;
    esac
    dir=$(fixture fspaddles)
    if ! run_in "$dir" fspaddles build -p:ZeroTarget=c64; then fail "$name" "dotnet build failed"; return; fi
    prg=$dir/bin/Debug/$TFM/c64/fspaddles.prg
    if [ ! -s "$prg" ]; then fail "$name" "no $prg"; return; fi
    if [ "$(head -c 2 "$prg" | od -An -tx1 | tr -d ' \n')" != 0108 ]; then fail "$name" "fspaddles.prg doesn't load at \$0801"; return; fi
    # About 50 KB for the program, its heap and its stack.
    size=$(wc -c < "$prg" | tr -d ' ')
    if [ "$size" -gt 45000 ]; then fail "$name" "fspaddles.prg is $size bytes, more than 45000"; return; fi
    if ! bash -c 'source ./env.sh && find_c64_emulator > /dev/null && [ -x "$LLVM_MOS/bin/llvm-nm" ]' 2> /dev/null; then
        echo "     (no VICE or ./setup.sh --c64 tools: built, not run)"
        pass "$name"
        return
    fi
    mkdir -p bin/c64
    cp "$prg" bin/c64/FsPaddles.prg
    cp "$prg.elf" bin/c64/FsPaddles.prg.elf
    LOG=$WORK/fspaddles-run.log
    if ! c64/run.sh FsPaddles < tests/input/Paddles.txt > "$WORK/fspaddles.out" 2> "$LOG"; then
        fail "$name" "the test match failed: $(tr '\n' ' ' < "$WORK/fspaddles.out")"; return
    fi
    if ! same_output "$WORK/fspaddles.out" tests/expected/Paddles.txt "$WORK/fspaddles.diff"; then
        fail "$name" "not the C# game's match: $(cat "$WORK/fspaddles.diff")"; return
    fi
    pass "$name"
}

test_fsharp_incremental() {
    local name="F# incremental" dir
    can_build "$name" || return
    dir=$(fixture fsapp fs-incremental)
    if ! run_in "$dir" fs-incremental-1 build -v:n || ! ran_bflat; then fail "$name" "the first build didn't run bflat"; return; fi
    if ! run_in "$dir" fs-incremental-2 build -v:n || ran_bflat; then fail "$name" "an unchanged project ran bflat again"; return; fi
    sleep 1; touch "$dir/Shapes.fs"
    if ! run_in "$dir" fs-incremental-3 build -v:n || ! ran_bflat; then fail "$name" "a changed source didn't run bflat"; return; fi
    pass "$name"
}

test_fsharp_publish() {
    local name="F# publish" dir exe
    can_build "$name" || return
    dir=$(fixture fsapp fs-publish)
    if ! run_in "$dir" fs-publish publish -o "$(native_path "$dir/out")"; then fail "$name" "dotnet publish failed"; return; fi
    exe=$(exe "$dir/out/fsapp")
    if ! is_host_program "$exe"; then fail "$name" "no program in the publish folder"; return; fi
    if [ -f "$dir/out/fsapp.dll" ]; then fail "$name" "the IL was published"; return; fi
    "$exe" > /dev/null
    if [ $? != 7 ]; then fail "$name" "the published program didn't run"; return; fi
    pass "$name"
}

# An F# native library: fixtures/nativelib's exports written in F#
# (structs, pointers, static initialization), called by the same C program,
# loaded at run time and linked. bflat exports what it compiles from C#, so
# the SDK makes a C# export for each F# one.
test_fsharp_native_library() {
    local name="F# native library" dir lib cc caller out exported ldl=
    can_build "$name" || return
    dir=$(fixture fsnativelib)
    if ! run_in "$dir" fsnativelib build; then fail "$name" "dotnet build failed"; return; fi
    lib=$dir/bin/Debug/$TFM/native/fsnativelib$HOST_LIB_EXT
    if [ ! -f "$lib" ]; then fail "$name" "no $lib"; return; fi
    local expected
    expected=$(printf '%s\n' nl_add nl_cctor_value nl_mul_long nl_negate nl_offset nl_remember nl_scale nl_sum)
    case "$HOST_OS" in
        Linux)  exported=$(nm -D --defined-only "$lib" | awk '{print $3}' | sed 's/@.*//' | sort) ;;
        Darwin) exported=$(nm -gU "$lib" | awk '{print $3}' | sed 's/^_//' | sort) ;;
        *)      exported=$expected
                if [ ! -f "$dir/bin/Debug/$TFM/native/fsnativelib.lib" ]; then fail "$name" "no import library"; return; fi ;;
    esac
    if [ "$exported" != "$expected" ]; then fail "$name" "exports: $(echo $exported)"; return; fi
    if ! cc=$(c_compiler); then skip "$name" "no C compiler"; return; fi
    [ "$HOST_OS" = Linux ] && ldl=-ldl
    caller=$(exe "$WORK/fsnativelib-caller")
    LOG=$WORK/fsnativelib-cc.log
    if ! "$cc" "$(native_path "$ROOT/sdk/test/fixtures/nativelib-c/main.c")" -o "$(native_path "$caller")" $ldl > "$LOG" 2>&1; then
        fail "$name" "the C caller didn't compile"; return
    fi
    expected=$(head -8 "$ROOT/sdk/test/fixtures/nativelib-caller/expected.txt" | tr -d '\r')
    out=$("$caller" "$(native_path "$lib")" 2>&1 | tr -d '\r')
    if [ "$out" != "$expected" ]; then fail "$name" "printed: $(echo $out)"; return; fi
    if [ "$HOST_OS" != Windows ]; then
        caller=$WORK/fsnativelib-linked-caller
        if ! "$cc" -DLINKED "$ROOT/sdk/test/fixtures/nativelib-c/main.c" "$lib" -Wl,-rpath,"$(dirname "$lib")" -o "$caller" > "$LOG" 2>&1; then
            fail "$name" "the linked C caller didn't link"; return
        fi
        out=$("$caller" 2>&1)
        if [ "$out" != "$expected" ]; then fail "$name" "linked, printed: $(echo $out)"; return; fi
    fi
    pass "$name"
}

# The other targets' libraries, and Windows' import library, which the SDK
# makes again: bflat's names the DLL it was built as (zero-native.dll).
test_fsharp_native_library_cross() {
    local name="F# native library cross" dir other other_arch
    can_build "$name" || return
    dir=$(fixture fsnativelib fsnativelib-cross)
    if ! run_in "$dir" fsnativelib-cross-win build -p:ZeroTarget=win-x64; then fail "$name" "win-x64 build failed"; return; fi
    if ! is_pe_dll "$dir/bin/Debug/$TFM/win-x64/native/fsnativelib.dll"; then fail "$name" "no Windows DLL"; return; fi
    if ! LC_ALL=C grep -aq 'fsnativelib\.dll' "$dir/bin/Debug/$TFM/win-x64/native/fsnativelib.lib"; then
        fail "$name" "the import library doesn't name fsnativelib.dll"; return
    fi
    if [ "$HOST_ARCH_NAME" = x64 ]; then other=linux-arm64; other_arch=aarch64; else other=linux-x64; other_arch=x86-64; fi
    if ! run_in "$dir" fsnativelib-cross-linux build -p:ZeroTarget=$other; then fail "$name" "$other build failed"; return; fi
    if ! is_elf_shared "$dir/bin/Debug/$TFM/$other/native/fsnativelib.so"; then fail "$name" "no shared library for $other"; return; fi
    pass "$name"
}

# An F# library's bindings, made from its IL, called from C#.
test_fsharp_native_library_bindings() {
    local name="F# native library bindings" dir
    can_build "$name" || return
    dir=$(fixture fsnativelib-app fs-bindings/fsnativelib-app)
    fixture fsnativelib fs-bindings/fsnativelib > /dev/null
    if ! run_in "$dir" fs-bindings build; then fail "$name" "dotnet build failed"; return; fi
    dotnet "$dir/bin/Debug/net10.0/fsnativelib-app.dll" > "$WORK/fs-bindings.out" 2>&1
    if ! same_output "$WORK/fs-bindings.out" "$dir/expected.txt" "$WORK/fs-bindings.diff"; then
        fail "$name" "$(cat "$WORK/fs-bindings.diff")"; return
    fi
    pass "$name"
}

# An F# program calls a C# native library through the F# bindings
# (bin/MathLib.Interop.fs); the library is copied to the program's folder.
test_fsharp_program_calls_native_library() {
    local name="F# program calls native library" dir out
    can_build "$name" || return
    dir=$(fixture nativelib-fsapp)
    fixture nativelib nativelib-fsapp/MathLib > /dev/null
    mv "$dir/MathLib/nativelib.csproj" "$dir/MathLib/MathLib.csproj"
    if ! run_in "$dir/App" nativelib-fsapp build; then fail "$name" "dotnet build failed"; return; fi
    if [ ! -f "$dir/App/bin/Debug/net10.0/MathLib$HOST_LIB_EXT" ]; then fail "$name" "MathLib$HOST_LIB_EXT wasn't copied to the program"; return; fi
    out=$(dotnet "$dir/App/bin/Debug/net10.0/App.dll" 2>&1 | tr -d '\r')
    if [ "$out" != $'add 5\noffset 11,22' ]; then fail "$name" "printed: $out"; return; fi
    pass "$name"
}

# The F# bindings of every kind of type (fixtures/interoplib), called from F#:
# the same output as the C# program's.
test_fsharp_native_library_bindings_all_types() {
    local name="F# bindings of every type" dir
    can_build "$name" || return
    dir=$(fixture interoplib-fsapp fs-all-bindings/interoplib-fsapp)
    fixture interoplib fs-all-bindings/interoplib > /dev/null
    if ! run_in "$dir" fs-all-bindings build; then fail "$name" "dotnet build failed"; return; fi
    dotnet "$dir/bin/Debug/net10.0/interoplib-fsapp.dll" > "$WORK/fs-all-bindings.out" 2>&1
    if ! same_output "$WORK/fs-all-bindings.out" "$ROOT/sdk/test/fixtures/interoplib-app/expected.txt" "$WORK/fs-all-bindings.diff"; then
        fail "$name" "$(cat "$WORK/fs-all-bindings.diff")"; return
    fi
    pass "$name"
}

# An export C can't call (a bool) or reach (a private function): ZERO005.
test_fsharp_native_library_errors() {
    local name="F# native library errors" dir
    dir=$(fixture fsnativelib fsnativelib-errors)
    printf '\n    [<UnmanagedCallersOnly(EntryPoint = "nl_flag")>]\n    let flag (b: bool) = b\n' >> "$dir/Exports.fs"
    if run_in "$dir" fsnativelib-errors build; then fail "$name" "a bool export built"; return; fi
    if ! grep -q 'ZERO005.*nl_flag.*bool' "$LOG"; then fail "$name" "no ZERO005 for the bool"; return; fi
    dir=$(fixture fsnativelib fsnativelib-errors)
    printf '\n    [<UnmanagedCallersOnly(EntryPoint = "nl_hidden")>]\n    let private hidden (x: int) = x\n' >> "$dir/Exports.fs"
    if run_in "$dir" fsnativelib-errors-private build; then fail "$name" "a private export built"; return; fi
    if ! grep -q 'ZERO005.*nl_hidden.*public' "$LOG"; then fail "$name" "no ZERO005 for the private function"; return; fi
    pass "$name"
}

ALL_TESTS=(host_packages payload_complete hello_compiles console_is_an_error old_dotnet_sdk_refused unions_compile plain_packages_refused
    native_hello_runs cross_targets c64_target arm_target gpu_template gpu_errors cuda_build cuda_errors bad_target path_with_spaces compile_items incremental
    bflat_failure_fails_the_build size strip_symbols debug_unoptimized mac_debug_object_kept windows_debugger macos_error
    project_reference library_change_rebuilds pack_and_package_reference version_mismatch templates
    custom_packages_folder dotnet_run publish extra_dll_imports library_import inherited_settings clean_and_rebuild
    targets_side_by_side language_settings_forwarded target_framework_is_an_error read_only_packages_folder
    release_tag_check payload_folder_guard target_from_directory_targets upgrade_from_0_1_0
    long_command_line windows_builds_linux unusual_packages_folder crlf_input
    artifacts_path_with_spaces trim_windows_layout trim_stamp llama_template sdl_template pico_template
    framework_from_property newer_sdk_library
    mac_target mac_target_links_libm_and_threads mac_target_path_with_spaces mac_target_undefined_import
    ios_simulator_target ios_simulator_errors
    trim_macos_layout mac_host_builds_all_targets mac_target_common_libc mac_target_user_tbd mac_target_newer_dylib
    tutorial_steps fsharp_tutorial_steps simd_instruction_set static_constructor_threads failure_keeps_output
    native_library_dotnet native_library_exports native_library_two native_library_errors
    native_library_not_packable native_library_publish native_library_incremental native_library_clean
    native_library_path_with_spaces native_library_with_zero_reference native_library_dotted_name
    native_library_c native_library_c_linked native_library_with_zero_exe native_library_cross
    link_library link_library_passed_on link_library_newer_glibc sdl3 sdl3_windows
    native_library_name_with_spaces native_library_reference_kinds native_library_app_recipe native_library_bindings
    native_library_visual_studio
    fsharp_program fsharp_printf fsharp_collections fsharp_builders fsharp_async fsharp_arena_post fsharp_catch fsharp_memory fsharp_must_not_compile fsharp_scratch_ref_struct_state fsharp_unsafe_calls fsharp_unsafe_calls_libraries fsharp_decimal fsharp_top_level fsharp_libraries fsharp_cross_targets fsharp_incremental fsharp_publish fsharp_c64
    fsharp_native_library fsharp_native_library_cross fsharp_native_library_errors
    fsharp_native_library_bindings fsharp_program_calls_native_library fsharp_native_library_bindings_all_types)

# sdk/trim-windows-layout.sh with a stub objdump: a DLL that lld.exe imports
# is found whatever its capitals, and an objdump failure stops the trim
# instead of leaving the DLLs out.
test_trim_windows_layout() {
    local name="trim windows layout" d=$WORK/trimwin
    rm -rf "$d" && mkdir -p "$d/linux/bin" "$d/linux/lib" "$d/win/bin" "$d/win/lib" "$d/stub"
    touch "$d/linux/bflat" "$d/linux/bin/lld" "$d/linux/libfoo.so" "$d/linux/lib/zero.txt"
    touch "$d/win/bflat.exe" "$d/win/bin/lld.exe" "$d/win/foo.dll" "$d/win/lib/zero.txt" "$d/win/bin/Zlib1.dll"
    cat > "$d/stub/objdump" <<'STUB'
#!/usr/bin/env bash
[ -n "${STUB_FAIL:-}" ] && { echo "objdump: file format not recognized" >&2; exit 1; }
case "$2" in *lld.exe) printf '\tDLL Name: ZLIB1.DLL\r\n\tDLL Name: KERNEL32.dll\r\n' ;; esac
STUB
    chmod +x "$d/stub/objdump"
    LOG=$d/trim.log
    if ! PATH="$d/stub:$PATH" sdk/trim-windows-layout.sh "$d/win" "$d/linux" "$d/out" > "$LOG" 2>&1; then fail "$name" "the trim failed"; return; fi
    if [ -z "$(find "$d/out/bin" -name Zlib1.dll)" ]; then fail "$name" "bin/Zlib1.dll (imported as ZLIB1.DLL) is missing"; return; fi
    if [ ! -f "$d/out/foo.dll" ] || [ ! -f "$d/out/lib/zero.txt" ]; then fail "$name" "the mapped files are missing"; return; fi
    if STUB_FAIL=1 PATH="$d/stub:$PATH" sdk/trim-windows-layout.sh "$d/win" "$d/linux" "$d/out" > "$LOG" 2>&1; then fail "$name" "an objdump failure was ignored"; return; fi
    if ! grep -q "objdump" "$LOG"; then fail "$name" "the error doesn't name objdump"; return; fi
    pass "$name"
}

# sdk/trim-host.sh redoes a trimmed layout when any file it copied changes in
# the full layout, and names what's missing instead of failing on a cd.
test_trim_stamp() {
    local name="trim stamp" d=$WORK/trimstamp
    rm -rf "$d" && mkdir -p "$d/layouts/windows-x64/bin" "$d/layouts/windows-x64/lib" "$d/trimmed/linux-x64/bin" "$d/trimmed/linux-x64/lib" "$d/stub"
    touch "$d/trimmed/linux-x64/bflat" "$d/trimmed/linux-x64/bin/lld" "$d/trimmed/linux-x64/lib/zero.txt"
    touch "$d/layouts/windows-x64/bflat.exe" "$d/layouts/windows-x64/bin/lld.exe"
    echo one > "$d/layouts/windows-x64/lib/zero.txt"
    printf '#!/usr/bin/env bash\ntrue\n' > "$d/stub/objdump" && chmod +x "$d/stub/objdump"
    LOG=$d/trim.log
    if ! PATH="$d/stub:$PATH" sdk/trim-host.sh win-x64 "$d/layouts" "$d/trimmed" > "$LOG" 2>&1; then fail "$name" "the first trim failed"; return; fi
    echo two > "$d/layouts/windows-x64/lib/zero.txt"
    if ! PATH="$d/stub:$PATH" sdk/trim-host.sh win-x64 "$d/layouts" "$d/trimmed" > "$LOG" 2>&1; then fail "$name" "the second trim failed"; return; fi
    if [ "$(cat "$d/trimmed/win-x64/lib/zero.txt")" != two ]; then fail "$name" "a changed lib/ file didn't redo the trim"; return; fi
    rm -rf "$d/trimmed/linux-x64" "$d/trimmed/win-x64"
    if PATH="$d/stub:$PATH" sdk/trim-host.sh win-x64 "$d/layouts" "$d/trimmed" > "$LOG" 2>&1; then fail "$name" "win-x64 trimmed without a linux-x64 one"; return; fi
    if ! grep -q "linux-x64" "$LOG"; then fail "$name" "the error doesn't name linux-x64"; return; fi
    pass "$name"
}

# dotnet new zerollama: the Llama sample as a project, with the model from the
# ZeroCSharp.Stories260K package. It tells the repo sample's story, word for
# word, and builds for another target too.
test_llama_template() {
    local name="llama template" dir=$WORK/llamatemplate out other
    can_build "$name" || return
    rm -rf "$dir" && mkdir -p "$dir"
    fixture hello llamatemplate-config > /dev/null    # just for its nuget.config
    cp "$WORK/llamatemplate-config/nuget.config" "$dir/"
    dotnet new uninstall ZeroCSharp.Templates > /dev/null 2>&1 || true
    if ! run_in "$dir" llama-install new install "$FEED/ZeroCSharp.Templates.$VERSION.nupkg"; then fail "$name" "install failed"; return; fi
    if ! run_in "$dir" llama-new new zerollama -o Story; then fail "$name" "dotnet new zerollama failed"; return; fi
    if ! run_in "$dir/Story" llama-build build; then fail "$name" "the program didn't build"; return; fi
    out=$("$(exe "$dir/Story/bin/Debug/$TFM/Story")" | tr -d '\r')
    if [ "$out" != "$(tr -d '\r' < "$ROOT/tests/expected/Llama.txt")" ]; then fail "$name" "printed '$out'"; return; fi
    out=$(printf 'The dog ran.\n' | "$(exe "$dir/Story/bin/Debug/$TFM/Story")" -c -s 3 | tr -d '\r')
    if [[ "$out" != *"Tell a story together"* ]] || [ "$(wc -l <<< "$out")" -lt 3 ]; then fail "$name" "-c printed '$out'"; return; fi
    case "$HOST_RID" in win-x64) other=linux-x64 ;; *) other=win-x64 ;; esac
    if ! run_in "$dir/Story" llama-other build -p:ZeroTarget=$other; then fail "$name" "the $other build failed"; return; fi
    pass "$name"
}

# dotnet new zerosdl: the SDL 3 window, with ZeroCSharp.SDL3 from the feed.
# Every host builds it for Windows (SDL's DLL from the package next to it);
# where SDL 3 is installed, or on Windows, it runs a few frames headless.
test_sdl_template() {
    local name="sdl template" dir=$WORK/sdltemplate out run=
    can_build "$name" || return
    if [ ! -f "$FEED/ZeroCSharp.SDL3.$VERSION.nupkg" ]; then fail "$name" "the feed has no ZeroCSharp.SDL3"; return; fi
    rm -rf "$dir" "${NUGET_PACKAGES:-$HOME/.nuget/packages}/zerocsharp.sdl3" && mkdir -p "$dir"
    fixture hello sdltemplate-config > /dev/null    # just for its nuget.config
    cp "$WORK/sdltemplate-config/nuget.config" "$dir/"
    dotnet new uninstall ZeroCSharp.Templates > /dev/null 2>&1 || true
    if ! run_in "$dir" sdl-install new install "$FEED/ZeroCSharp.Templates.$VERSION.nupkg"; then fail "$name" "install failed"; return; fi
    if ! run_in "$dir" sdl-new new zerosdl -o Game; then fail "$name" "dotnet new zerosdl failed"; return; fi
    if ! grep -q "ZeroCSharp.Sdk/$VERSION" "$dir/Game/Game.csproj" || [ ! -f "$dir/Game/.vscode/launch.json" ]; then fail "$name" "the project isn't filled in"; return; fi
    if ! run_in "$dir/Game" sdl-win build -p:ZeroTarget=win-x64; then fail "$name" "the win-x64 build failed"; return; fi
    if ! ls "$dir/Game/bin/Debug/$TFM/win-x64" | grep -qx SDL3.dll; then fail "$name" "no SDL3.dll next to the Windows program"; return; fi
    case "$HOST_OS" in
        Windows) run=win ;;
        Linux)   compgen -G '/usr/lib/*/libSDL3.so' > /dev/null && run=linux ;;
        Darwin)  [ -f /opt/homebrew/lib/libSDL3.dylib ] && run=mac ;;
    esac
    if [ -n "$run" ]; then
        if ! run_in "$dir/Game" sdl-build build; then fail "$name" "the program didn't build"; return; fi
        case "$run" in
            win)   out=$(SDL_VIDEO_DRIVER=dummy SDL_RENDER_DRIVER=software "$(exe "$dir/Game/bin/Debug/$TFM/Game")" --frames 30 2>&1 | tr -d '\r') ;;
            linux) out=$(SDL_VIDEO_DRIVER=offscreen "$dir/Game/bin/Debug/$TFM/Game" --frames 30 2>&1) ;;
            mac)   out=$("$dir/Game/bin/Debug/$TFM/Game" --frames 30 2>&1) ;;
        esac
        if [ "$out" != "30 frames, square at 330,220" ]; then fail "$name" "printed '$out'"; return; fi
    fi
    pass "$name"
}

# dotnet new zeropico: a microcontroller program, for the Pico 2 by default and
# for the Uno R4 with --board r4. Both build; where qemu-system-arm is
# installed, dotnet run blinks in QEMU and prints its two lines.
test_pico_template() {
    local name="pico template" dir=$WORK/picotemplate board out
    can_build "$name" || return
    if [ "$HOST_RID" = osx-arm64 ] && ! docker info > /dev/null 2>&1; then
        skip "$name" "on a Mac the microcontrollers need Docker, which isn't running"; return
    fi
    rm -rf "$dir" && mkdir -p "$dir"
    fixture hello picotemplate-config > /dev/null    # just for its nuget.config
    cp "$WORK/picotemplate-config/nuget.config" "$dir/"
    dotnet new uninstall ZeroCSharp.Templates > /dev/null 2>&1 || true
    if ! run_in "$dir" pico-install new install "$FEED/ZeroCSharp.Templates.$VERSION.nupkg"; then fail "$name" "install failed"; return; fi
    for board in pico2 r4; do
        if ! run_in "$dir" pico-new-$board new zeropico -o Blinky-$board --board $board; then fail "$name" "dotnet new zeropico --board $board failed"; return; fi
        if ! grep -q "<ZeroTarget>$board</ZeroTarget>" "$dir/Blinky-$board/Blinky-$board.csproj"; then fail "$name" "the $board project doesn't target $board"; return; fi
        if ! run_in "$dir/Blinky-$board" pico-build-$board build; then fail "$name" "the $board program didn't build"; return; fi
        if command -v qemu-system-arm > /dev/null 2>&1; then
            if ! run_in "$dir/Blinky-$board" pico-run-$board run < /dev/null; then fail "$name" "dotnet run ($board) failed"; return; fi
            out=$(grep -E "Hello from C#|Blinked for" "$LOG" | tr -d '\r')
            if [ "$out" != $'Hello from C# on a microcontroller!\nBlinked for 5 seconds.' ]; then fail "$name" "dotnet run ($board) printed '$out'"; return; fi
        fi
    done
    if [ ! -s "$dir/Blinky-pico2/bin/Debug/$TFM/pico2/Blinky-pico2.uf2" ]; then fail "$name" "no .uf2 for the Pico 2"; return; fi
    pass "$name"
}

# The framework is ZeroFramework's (sdk/version.props), not made from the
# version: builds go to bin/<config>/<it>/, packed libraries to lib/<it>/
# with build/<id>.props naming the SDK version that built them.
test_framework_from_property() {
    local name="framework from property" framework dir list
    framework=$(sed -n 's|.*<ZeroFramework>\(.*\)</ZeroFramework>.*|\1|p' sdk/version.props)
    if [ -z "$framework" ]; then fail "$name" "sdk/version.props has no ZeroFramework"; return; fi
    if [ "$TFM" != "$framework" ]; then fail "$name" "TFM is $TFM, ZeroFramework $framework"; return; fi
    dir=$(app_with_lib frameworkprop)
    if ! run_in "$dir/Geometry" frameworkprop-pack pack -o "$WORK/frameworkfeed"; then fail "$name" "dotnet pack failed"; return; fi
    if [ ! -f "$dir/Geometry/bin/Release/$framework/Geometry.dll" ]; then fail "$name" "no bin/Release/$framework/Geometry.dll"; return; fi
    list=$(zip_list "$WORK/frameworkfeed/ZeroTest.Geometry.1.0.0.nupkg")
    if ! grep -qx "lib/$framework/Geometry.dll" <<< "$list"; then fail "$name" "the package has no lib/$framework/Geometry.dll"; return; fi
    if ! grep -qx "build/ZeroTest.Geometry.props" <<< "$list"; then fail "$name" "the package has no build/ZeroTest.Geometry.props"; return; fi
    pass "$name"
}

# A library built by a newer SDK may use what this one lacks: ZERO002, naming
# both versions. One from the same SDK, or an older (prerelease) one, builds.
test_newer_sdk_library() {
    local name="newer sdk library" dir src v app
    can_build "$name" || return
    dir=$(app_with_lib newersdk)
    rm -rf "$WORK/newersdk-feed" "$WORK/newersdk-src"
    if ! run_in "$dir/Geometry" newersdk-pack pack -o "$WORK/newersdk-feed"; then fail "$name" "dotnet pack failed"; return; fi
    for v in 99.0.0 "$VERSION" 0.3.0-preview1; do
        src=$WORK/newersdk-src/$v
        mkdir -p "$src" "$WORK/newersdk-feeds/$v"
        (cd "$src" && unzip -qo "$WORK/newersdk-feed/ZeroTest.Geometry.1.0.0.nupkg")
        if [ ! -f "$src/build/ZeroTest.Geometry.props" ]; then fail "$name" "the package has no build/ZeroTest.Geometry.props"; return; fi
        # NuGet imports buildTransitive/'s copy when there is one: change both.
        for f in "$src/build/ZeroTest.Geometry.props" "$src/buildTransitive/ZeroTest.Geometry.props"; do
            sed -i.bak "s|SdkVersion=\"[^\"]*\"|SdkVersion=\"$v\"|" "$f" && rm -f "$f.bak"
        done
        rm -f "$WORK/newersdk-feeds/$v/ZeroTest.Geometry.1.0.0.nupkg"
        zip_dir "$src" "$WORK/newersdk-feeds/$v/ZeroTest.Geometry.1.0.0.nupkg"
        rm -rf "${NUGET_PACKAGES:-$HOME/.nuget/packages}/zerotest.geometry"
        app=$(fixture app-with-pkg "newersdk-app-$v")/App
        add_source "$app/../nuget.config" libs "$WORK/newersdk-feeds/$v"
        if [ "$v" = 99.0.0 ]; then
            if run_in "$app" "newersdk-$v" build; then fail "$name" "a library from SDK 99.0.0 was accepted"; return; fi
            if ! grep -q ZERO002 "$LOG" || ! grep -q "99.0.0" "$LOG" || ! grep -q "$VERSION" "$LOG"; then fail "$name" "no ZERO002 naming 99.0.0 and $VERSION"; return; fi
        elif ! run_in "$app" "newersdk-$v" build; then
            fail "$name" "a library from SDK $v was refused"; return
        fi
    done
    pass "$name"
}

# osx-arm64 from any build machine: bflat compiles, ld64.lld links against the
# payload's libSystem.tbd. The programs are kept in $WORK/mac-built/<host>/
# for CI, which runs them on a Mac (sdk.yml, *-built-macos).
test_mac_target() {
    local name="mac target" dir
    can_build "$name" || return
    dir=$(fixture native mactarget)
    if ! run_in "$dir" mactarget build -p:ZeroTarget=osx-arm64; then fail "$name" "dotnet build failed"; return; fi
    if ! is_macho_arm64 "$dir/bin/Debug/$TFM/osx-arm64/native"; then fail "$name" "osx-arm64/native isn't an arm64 Mach-O program"; return; fi
    mkdir -p "$WORK/mac-built/$HOST_RID"
    cp "$dir/bin/Debug/$TFM/osx-arm64/native" "$WORK/mac-built/$HOST_RID/native"
    pass "$name"
}

# Threads and libm on macOS come from libSystem: the link must find them all.
test_mac_target_links_libm_and_threads() {
    local name="mac target links libm and threads" dir
    can_build "$name" || return
    dir=$(fixture threads macthreads)
    if ! run_in "$dir" macthreads build -p:ZeroTarget=osx-arm64; then fail "$name" "dotnet build failed"; return; fi
    if ! is_macho_arm64 "$dir/bin/Debug/$TFM/osx-arm64/threads"; then fail "$name" "osx-arm64/threads isn't an arm64 Mach-O program"; return; fi
    mkdir -p "$WORK/mac-built/$HOST_RID"
    cp "$dir/bin/Debug/$TFM/osx-arm64/threads" "$WORK/mac-built/$HOST_RID/threads"
    pass "$name"
}

# ZeroTarget=iossimulator-arm64: bflat's macOS object, marked as built for the
# iOS simulator (zero-bitcode macho-platform) and linked by the package's
# ld64.lld, so every host builds it. A path with spaces and a non-ASCII
# letter goes through the build and through dotnet run's launcher. On a Mac
# with a simulator, dotnet run starts it in the project's folder, with its
# arguments, and returns its exit code.
test_ios_simulator_target() {
    local name="ios simulator target" dir program code
    can_build "$name" || return
    dir=$(fixture iosrun "ios space/Zoë Ann")
    if ! run_in "$dir" iossim build -p:ZeroTarget=iossimulator-arm64; then fail "$name" "dotnet build failed"; return; fi
    program=$dir/bin/Debug/$TFM/iossimulator-arm64/iosrun
    if ! is_macho_arm64 "$program"; then fail "$name" "no arm64 Mach-O program"; return; fi
    if [ "$(macho_platform "$program")" != 7 ]; then fail "$name" "the program isn't built for the iOS simulator"; return; fi
    if [ "$HOST_RID" != osx-arm64 ]; then pass "$name (built; running needs a Mac)"; return; fi
    if ! xcrun simctl list devices available 2> /dev/null | grep -qE '^ +iPhone'; then
        skip "$name" "built; no iPhone simulator to run it in (Xcode's iOS platform)"; return
    fi
    code=0
    run_in "$dir" iossim-run run -p:ZeroTarget=iossimulator-arm64 -- "an argument" é || code=$?
    if [ "$code" != 3 ]; then fail "$name" "dotnet run exited with $code, not the program's 3"; return; fi
    if [ "$(tail -3 "$LOG")" != "$(printf 'in the project folder\nan argument\né')" ]; then
        fail "$name" "dotnet run printed something else"; return
    fi
    pass "$name"
}

# The iOS simulator takes programs only: no native libraries, no GPU kernels.
test_ios_simulator_errors() {
    local name="ios simulator errors" dir
    can_build "$name" || return
    dir=$(fixture nativelib iossimlib)
    if run_in "$dir" iossim-lib build -p:ZeroTarget=iossimulator-arm64; then fail "$name" "a native library built for the simulator"; return; fi
    if ! grep -q "ZeroTarget 'iossimulator-arm64' has no shared libraries" "$LOG"; then fail "$name" "no message about native libraries"; return; fi
    dir=$(fixture native iossimgpu)
    if run_in "$dir" iossim-gpu build -p:ZeroTarget=iossimulator-arm64 -p:ZeroGpu=Metal; then fail "$name" "ZeroGpu=Metal built for the simulator"; return; fi
    if ! grep -q "ZeroGpu=Metal builds for ZeroTarget osx-arm64" "$LOG"; then fail "$name" "no message about the GPU"; return; fi
    pass "$name"
}

test_mac_target_path_with_spaces() {
    local name="mac target path with spaces" dir
    can_build "$name" || return
    dir=$(fixture native "mac space/Zoë Ann")
    if ! run_in "$dir" macspaces build -p:ZeroTarget=osx-arm64; then fail "$name" "dotnet build failed"; return; fi
    if ! is_macho_arm64 "$dir/bin/Debug/$TFM/osx-arm64/native"; then fail "$name" "no arm64 Mach-O program"; return; fi
    pass "$name"
}

# A library built for a newer macOS than programs target (Homebrew's, built
# for the Mac it's on): lld's "has version 27.0, which is newer than target
# minimum of 12.0" isn't a warning, only a message in a -v:n log. The library
# is the nativelib fixture, linked again by the package's lld for macOS 27.
test_mac_target_newer_dylib() {
    local name="mac target newer dylib" lib app args lld newer out
    can_build "$name" || return
    lib=$(fixture nativelib macnewer-lib)
    if ! run_in "$lib" macnewer-lib build -p:ZeroTarget=osx-arm64; then fail "$name" "the library didn't build"; return; fi
    lld=${NUGET_PACKAGES:-$HOME/.nuget/packages}/zerocsharp.bflat.$HOST_RID/$(echo "$VERSION" | tr 'A-Z' 'a-z')/tools/bflat/bin/lld
    [ "$HOST_OS" = Windows ] && lld=$lld.exe
    newer=$(native_path "$WORK/macnewer/libnewer.dylib" | tr '\\' '/')
    mkdir -p "$WORK/macnewer"
    args=$(ls "$lib"/obj/Debug/"$TFM"/zero-link.osx-arm64.args)
    sed -e 's/-platform_version macos 12.0 12.0/-platform_version macos 27.0 27.0/' \
        -e 's|-install_name "@rpath/nativelib.dylib"|-install_name "@rpath/libnewer.dylib"|' \
        -e 's|-o "[^"]*"|-o "'"$newer"'"|' "$args" > "$WORK/macnewer/link.args"
    if ! "$lld" -flavor darwin @"$(native_path "$WORK/macnewer/link.args")" > "$WORK/macnewer-lld.log" 2>&1; then fail "$name" "relinking for macOS 27 failed"; return; fi
    app=$WORK/macnewer/App
    mkdir -p "$app"
    cp "$(fixture hello macnewer-config)/nuget.config" "$app/"
    printf '%s\n' "<Project Sdk=\"ZeroCSharp.Sdk/$VERSION\">" '  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>' \
        "  <ItemGroup><ZeroLinkLibrary Include=\"$newer\" DllImport=\"newer\" /></ItemGroup>" '</Project>' > "$app/App.csproj"
    cat > "$app/Program.cs" <<'CS'
using System.Runtime.InteropServices;

public static class Program
{
    [DllImport("newer", EntryPoint = "nl_add")] static safe extern int Add(int a, int b);

    public static int Main()
    {
        Out.Line($"add {Add(2, 3)}");
        Out.Flush();
        return 0;
    }
}
CS
    if ! run_in "$app" macnewer-app build -p:ZeroTarget=osx-arm64; then fail "$name" "the program didn't build"; return; fi
    if grep -q "newer than target minimum" "$LOG" || ! grep -q " 0 Warning(s)" "$LOG"; then fail "$name" "the version line is a warning"; return; fi
    if ! run_in "$app" macnewer-app-v build -p:ZeroTarget=osx-arm64 --no-incremental -v:n || ! grep -q "newer than target minimum" "$LOG"; then
        fail "$name" "the version line isn't in the -v:n log"; return
    fi
    if [ "$HOST_OS" = Darwin ]; then
        out=$(DYLD_LIBRARY_PATH=$WORK/macnewer "$app/bin/Debug/$TFM/osx-arm64/App")
        if [ "$out" != "add 5" ]; then fail "$name" "printed '$out'"; return; fi
    fi
    pass "$name"
}

# A C function libSystem.tbd doesn't list: the link fails, naming it.
test_mac_target_undefined_import() {
    local name="mac target undefined import" dir
    can_build "$name" || return
    dir=$(fixture native macundefined)
    cat > "$dir/Program.cs" <<'CS'
using System.Runtime.InteropServices;
public static class Program
{
    [DllImport("libc")] static safe extern int zero_no_such_function();
    public static int Main() => zero_no_such_function();
}
CS
    if run_in "$dir" macundefined build -p:ZeroTarget=osx-arm64; then fail "$name" "the build succeeded"; return; fi
    if ! grep -q "_zero_no_such_function" "$LOG"; then fail "$name" "the error doesn't name _zero_no_such_function"; return; fi
    pass "$name"
}

# sdk/trim-macos-layout.sh with a stub otool: the linux-arm64 list mapped to
# macOS names (.so -> .dylib), plus a dylib bflat loads, found whatever its
# capitals; an otool failure stops the trim instead of leaving dylibs out.
test_trim_macos_layout() {
    local name="trim macos layout" d=$WORK/trimmac
    rm -rf "$d" && mkdir -p "$d/linux/bin" "$d/linux/lib/linux" "$d/mac/bin" "$d/mac/lib/linux" "$d/stub"
    touch "$d/linux/bflat" "$d/linux/bin/lld" "$d/linux/libjitinterface_arm64.so" "$d/linux/lib/linux/zero.txt"
    touch "$d/mac/bflat" "$d/mac/bin/lld" "$d/mac/libjitinterface_arm64.dylib" "$d/mac/lib/linux/zero.txt" "$d/mac/libExtra.dylib" "$d/mac/unused.dylib"
    cat > "$d/stub/otool" <<'STUB'
#!/usr/bin/env bash
[ -n "${STUB_FAIL:-}" ] && { echo "otool: can't open file" >&2; exit 1; }
echo "$2:"
case "$2" in *bflat) printf '\t@rpath/LIBEXTRA.dylib (compatibility version 1.0.0)\n\t/usr/lib/libSystem.B.dylib (compatibility version 1.0.0)\n' ;; esac
STUB
    chmod +x "$d/stub/otool"
    LOG=$d/trim.log
    if ! PATH="$d/stub:$PATH" sdk/trim-macos-layout.sh "$d/mac" "$d/linux" "$d/out" > "$LOG" 2>&1; then fail "$name" "the trim failed"; return; fi
    if [ ! -f "$d/out/libjitinterface_arm64.dylib" ] || [ ! -f "$d/out/lib/linux/zero.txt" ] || [ ! -f "$d/out/bin/lld" ]; then fail "$name" "the mapped files are missing"; return; fi
    if [ -z "$(find "$d/out" -maxdepth 1 -name libExtra.dylib)" ]; then fail "$name" "libExtra.dylib (loaded as LIBEXTRA.dylib) is missing"; return; fi
    if [ -e "$d/out/unused.dylib" ]; then fail "$name" "unused.dylib was copied"; return; fi
    if STUB_FAIL=1 PATH="$d/stub:$PATH" sdk/trim-macos-layout.sh "$d/mac" "$d/linux" "$d/out" > "$LOG" 2>&1; then fail "$name" "an otool failure was ignored"; return; fi
    if ! grep -q "otool" "$LOG"; then fail "$name" "the error doesn't name otool"; return; fi
    pass "$name"
}

# On an Apple Silicon Mac: the Mac's own program, signed ad hoc and run, and
# the other three targets. They're kept in $WORK/macos-built/<rid>/ for CI,
# which runs them on their own systems (sdk.yml, macos-built-*).
test_mac_host_builds_all_targets() {
    local name="mac host builds all targets" dir out t f
    if [ "$HOST_RID" != osx-arm64 ]; then skip "$name" "Apple Silicon Macs only"; return; fi
    dir=$(fixture native machost)
    if ! run_in "$dir" machost build; then fail "$name" "dotnet build failed"; return; fi
    if ! codesign -v "$dir/bin/Debug/$TFM/native" 2> /dev/null; then fail "$name" "the program isn't signed"; return; fi
    out=$("$dir/bin/Debug/$TFM/native"); if [ "$out" != hi ]; then fail "$name" "printed '$out'"; return; fi
    for t in linux-x64 linux-arm64 win-x64; do
        if ! run_in "$dir" "machost-$t" build -p:ZeroTarget=$t; then fail "$name" "the $t build failed"; return; fi
        f=$dir/bin/Debug/$TFM/$t/native; [ "$t" = win-x64 ] && f=$f.exe
        case "$t" in
            linux-x64)   is_elf "$f" x86-64 ;;
            linux-arm64) is_elf "$f" aarch64 ;;
            win-x64)     is_pe "$f" ;;
        esac || { fail "$name" "$t/native isn't a $t program"; return; }
        mkdir -p "$WORK/macos-built/$t" && cp "$f" "$WORK/macos-built/$t/"
    done
    pass "$name"
}

# C functions programs commonly import link for macOS without extra steps:
# libSystem.tbd lists them. (Called only when there are 100 arguments, so the
# link needs them and running needs nothing.)
test_mac_target_common_libc() {
    local name="mac target common libc" dir
    can_build "$name" || return
    dir=$(fixture native maclibc)
    cat > "$dir/Program.cs" <<'CS'
using System.Runtime.InteropServices;
public static class Program
{
    [DllImport("libc")] static safe extern int snprintf(nint buffer, nint size, nint format);
    [DllImport("libc")] static safe extern nint fopen(nint path, nint mode);
    [DllImport("libc")] static safe extern nint fread(nint buffer, nint size, nint count, nint file);
    [DllImport("libc")] static safe extern int fclose(nint file);
    [DllImport("libc")] static safe extern void qsort(nint items, nint count, nint size, nint compare);
    [DllImport("libc")] static safe extern nint strerror(int error);
    [DllImport("libc")] static safe extern int getchar();
    [DllImport("libc")] static safe extern nint dlopen(nint path, int mode);
    [DllImport("libc")] static safe extern int atoi(nint text);
    [DllImport("libc")] static safe extern nint strtol(nint text, nint end, int radix);
    public static int Main(string[] args)
    {
        if (args.Length == 100)
        {
            snprintf(0, 0, 0); fclose(fopen(0, 0)); fread(0, 0, 0, 0); qsort(0, 0, 0, 0);
            strerror(0); getchar(); dlopen(0, 0); atoi(0); strtol(0, 0, 0);
        }
        Out.Line("hi");
        return 0;
    }
}
CS
    if ! run_in "$dir" maclibc build -p:ZeroTarget=osx-arm64; then fail "$name" "the link failed"; return; fi
    if ! is_macho_arm64 "$dir/bin/Debug/$TFM/osx-arm64/native"; then fail "$name" "no arm64 Mach-O program"; return; fi
    pass "$name"
}

# A C function libSystem.tbd doesn't list: the project's own .tbd, with
# libSystem's install name, in ZeroNativeInput (install-with-dotnet.md).
test_mac_target_user_tbd() {
    local name="mac target user tbd" dir
    can_build "$name" || return
    dir=$(fixture native macusertbd)
    cat > "$dir/Program.cs" <<'CS'
using System.Runtime.InteropServices;
public static class Program
{
    [DllImport("libc")] static safe extern int zero_only_in_my_tbd();
    public static int Main(string[] args) => args.Length == 100 ? zero_only_in_my_tbd() : 0;
}
CS
    printf '%s\n' '--- !tapi-tbd' 'tbd-version:     4' 'targets:         [ arm64-macos ]' \
        "install-name:    '/usr/lib/libSystem.B.dylib'" 'exports:' '  - targets:     [ arm64-macos ]' \
        '    symbols:     [ _zero_only_in_my_tbd ]' '...' > "$dir/more-libc.tbd"
    sed -i.bak 's|</Project>|  <ItemGroup><ZeroNativeInput Include="more-libc.tbd" /></ItemGroup>\n</Project>|' "$dir/native.csproj" && rm -f "$dir/native.csproj.bak"
    if ! run_in "$dir" macusertbd build -p:ZeroTarget=osx-arm64; then fail "$name" "the link failed"; return; fi
    if ! is_macho_arm64 "$dir/bin/Debug/$TFM/osx-arm64/native"; then fail "$name" "no arm64 Mach-O program"; return; fi
    pass "$name"
}

# The first program and the tutorial, as docs/ tells them: each step's samples/*.cs is the
# Program.cs of a zero project, built with dotnet build and run with its input, and prints
# what the repository's build does (tests/expected/).
test_tutorial_steps() {
    local name="tutorial steps" s dir input out
    can_build "$name" || return
    for s in Minimal QuizStep1 QuizStep2 QuizStep3 Quiz Refs; do
        dir=$(fixture native "tutorial-$s")
        rm -f "$dir/Program.cs"
        cp "$ROOT/samples/$s.cs" "$dir/Program.cs"
        input=/dev/null; [ -f "$ROOT/tests/input/$s.txt" ] && input=$ROOT/tests/input/$s.txt
        if ! run_in "$dir" "tutorial-$s" build; then fail "$name" "$s didn't build"; return; fi
        out=$("$(exe "$dir/bin/Debug/$TFM/native")" < "$input" | tr -d '\r')
        echo "size $s: $(wc -c < "$(exe "$dir/bin/Debug/$TFM/native")")" >> "$WORK/tutorial-sizes.txt"
        # Windows readers in PowerShell: cmd redirects the answers file unchanged.
        if [ "$HOST_OS" = Windows ] && [ "$input" != /dev/null ]; then
            out=$(cmd //c "$(native_path "$(exe "$dir/bin/Debug/$TFM/native")") < $(native_path "$input")" | tr -d '\r')
            if [ "$out" != "$(tr -d '\r' < "$ROOT/tests/expected/$s.txt")" ]; then fail "$name" "$s through cmd's < printed something else"; return; fi
        fi
        if [ "$out" != "$(tr -d '\r' < "$ROOT/tests/expected/$s.txt")" ]; then
            echo "$out" >> "$LOG"
            fail "$name" "$s printed something else"; return
        fi
    done
    pass "$name"
}

# The tutorial's F# tabs: each step's samples/*.fs is the Program.fs of an F# zero
# project, and prints what its C# twin prints (tests/expected/).
test_fsharp_tutorial_steps() {
    local name="F# tutorial steps" s dir input out
    can_build "$name" || return
    for s in QuizStep1 QuizStep2 QuizStep3 Quiz; do
        dir=$(fixture fstoplevel "fs-tutorial-$s")
        cp "$ROOT/samples/$s.fs" "$dir/Program.fs"
        input=/dev/null; [ -f "$ROOT/tests/input/$s.txt" ] && input=$ROOT/tests/input/$s.txt
        if ! run_in "$dir" "fs-tutorial-$s" build; then fail "$name" "$s didn't build"; return; fi
        out=$("$(exe "$dir/bin/Debug/$TFM/fstoplevel")" < "$input" | tr -d '\r')
        if [ "$out" != "$(tr -d '\r' < "$ROOT/tests/expected/$s.txt")" ]; then
            echo "$out" >> "$LOG"
            fail "$name" "$s printed something else than $s.cs"; return
        fi
    done
    pass "$name"
}

# docs/how-to/use-simd.md's ZeroInstructionSet, per target architecture: the
# Simd sample takes its SIMD path, and the result matches the scalar one.
test_simd_instruction_set() {
    local name="simd instruction set" dir out
    can_build "$name" || return
    dir=$(fixture native simd)
    rm -f "$dir/Program.cs" && cp "$ROOT/samples/Simd.cs" "$dir/Program.cs"
    sed -i.bak 's|</Project>|  <PropertyGroup>\n    <ZeroInstructionSet Condition="'"'"'$(ZeroTarget)'"'"' == '"'"'linux-x64'"'"' or '"'"'$(ZeroTarget)'"'"' == '"'"'win-x64'"'"'">x86-64-v3</ZeroInstructionSet>\n    <ZeroInstructionSet Condition="'"'"'$(ZeroTarget)'"'"' == '"'"'linux-arm64'"'"' or '"'"'$(ZeroTarget)'"'"' == '"'"'osx-arm64'"'"'">dotprod</ZeroInstructionSet>\n  </PropertyGroup>\n</Project>|' "$dir/native.csproj" && rm -f "$dir/native.csproj.bak"
    if ! run_in "$dir" simd build -v:n; then fail "$name" "dotnet build failed"; return; fi
    if ! grep -qE -- "-m (x86-64-v3|dotprod)" "$LOG"; then fail "$name" "bflat didn't get -m"; return; fi
    out=$("$(exe "$dir/bin/Debug/$TFM/native")" | tr -d '\r')
    if ! grep -qE "Path: (NEON|AVX)" <<< "$out"; then fail "$name" "not the SIMD path: $(grep Path: <<< "$out")"; return; fi
    if ! grep -q "Results match: True" <<< "$out"; then fail "$name" "the results don't match"; return; fi
    if ! run_in "$dir" simd-mac build -p:ZeroTarget=osx-arm64; then fail "$name" "the osx-arm64 build failed"; return; fi
    pass "$name"
}

# ---- Run ---------------------------------------------------------------------

if [ -z "${ZERO_NO_PACK:-}" ]; then
    if ! sdk/pack.sh > "$WORK.pack.log" 2>&1; then
        echo "sdk/pack.sh failed:"
        tail -30 "$WORK.pack.log"
        exit 1
    fi
fi
# The feed's packages keep their version between runs, so drop the cached
# copies: restore must take the ones just packed.
rm -rf "${NUGET_PACKAGES:-$HOME/.nuget/packages}"/zerocsharp.*
# MSBuild processes that stay alive between builds keep the SDK's files in
# memory; with the same version repacked, they'd build with the old ones.
export MSBUILDDISABLENODEREUSE=1
dotnet build-server shutdown > /dev/null 2>&1 || true
mkdir -p "$WORK"

tests=("$@")
[ ${#tests[@]} -eq 0 ] && tests=("${ALL_TESTS[@]}")
for name in "${tests[@]}"; do
    LOG=
    "test_${name//-/_}"
done
echo
echo "$passed passed, $failed failed, $skipped skipped"
exit "$failed"
