#!/usr/bin/env bash
# Builds what F# programs compile against, from zerocore's IL:
#
#   <out>/System.Runtime.dll            zerocore, under the name fsc looks for
#   <out>/System.Runtime.Extensions.dll shim/: the BCL types fsc needs that
#                                       zerocore doesn't have
#   <out>/FSharp.Core.dll               core/: a small FSharp.Core
#   <lib's dir>/FSharp.Printf.dll       printf/: printf, sprintf, failwithf
#   <lib's dir>/FSharp.Control.dll      control/: async, task, MailboxProcessor
#
# Usage:  fsharp/build-core.sh <System.Private.CoreLib.dll> <BflatLib.dll> <out dir>
#         (printf writes through lib/'s Out)
# Env:    ZERO_FSHARP_SDK the .NET SDK whose compilers to use (default: the
#                         newest one `dotnet --list-sdks` shows; 10 or newer)
#
# Only the .NET SDK is needed (its C# and F# compilers), not bflat.
#
# Why each piece is the way it is:
# - fsc takes framework types (Tuple, IComparable, ...) only from assemblies
#   whose names are on its list of system assemblies, so the shim is named
#   System.Runtime.Extensions.
# - With --targetprofile:netcore fsc finds its primary assembly by the file
#   name System.Runtime.dll, and the IL it writes refers to that assembly's
#   real name, System.Private.CoreLib: bflat then finds zerocore.
# - zerocore's metadata version is empty; fsc would copy it and write ECMA
#   v1.x metadata tables, which bflat misreads. --metadataversion sets it.
# - --reflectionfree: records and unions get no sprintf-based ToString.
# - F# compilers after SDK 10.0.402 generate a ToString for each record and
#   union under --reflectionfree, from Operators; core.fs comes before ops.fs,
#   so its records and unions write their own (see core.fs).
set -euo pipefail
usage="usage: build-core.sh <System.Private.CoreLib.dll> <BflatLib.dll> <out dir>"
core=${1:?$usage}
lib=${2:?$usage}
out=${3:?$usage}
here=$(cd "$(dirname "$0")" && pwd)

if [ -z "${ZERO_FSHARP_SDK:-}" ]; then
    ZERO_FSHARP_SDK=$(dotnet --list-sdks | awk '{v = $1; d = $2} END {if (v != "") {gsub(/[][]/, "", d); print d "/" v}}')
fi
if [ -z "$ZERO_FSHARP_SDK" ] || [ ! -f "$ZERO_FSHARP_SDK/FSharp/fsc.dll" ]; then
    echo "Error: no .NET SDK found (for its C# and F# compilers); install one or set ZERO_FSHARP_SDK" >&2
    exit 1
fi

mkdir -p "$out"
cp "$core" "$out/System.Runtime.dll"

echo "[csc]   fsharp/shim/ -> $out/System.Runtime.Extensions.dll"
dotnet "$ZERO_FSHARP_SDK/Roslyn/bincore/csc.dll" -nologo -noconfig -nostdlib -deterministic -optimize+ \
    -t:library -r:"$out/System.Runtime.dll" -out:"$out/System.Runtime.Extensions.dll" "$here"/shim/*.cs

echo "[fsc]   fsharp/core/ -> $out/FSharp.Core.dll"
dotnet "$ZERO_FSHARP_SDK/FSharp/fsc.dll" --nologo --noframework --targetprofile:netcore --compiling-fslib \
    --reflectionfree --optimize+ --deterministic+ --metadataversion:v4.0.30319 --nowarn:75,3370 \
    --target:library -r:"$out/System.Runtime.dll" -r:"$out/System.Runtime.Extensions.dll" \
    --out:"$out/FSharp.Core.dll" \
    "$here/core/prelude.fs" "$here/core/attrs.fs" "$here/core/core.fs" "$here/core/ops.fs" "$here/core/local.fs" \
    "$here/core/seqcore.fs" "$here/core/seq.fs" "$here/core/collections.fs" "$here/core/map.fs" "$here/core/set.fs" "$here/core/string.fs" "$here/core/option.fs" "$here/core/lazy.fs" \
    "$here/core/nativeptr.fs" "$here/core/printf.fs"

# printf and the others: a library of their own (see printf/Printf.fs), as
# any F# library, against FSharp.Core and lib/ (Out, and the scratch arena it
# formats in). Next to lib, as FSharp.Control below is: its signatures name
# lib's Arena (Arena.Sprintf).
printf=$(dirname "$lib")/FSharp.Printf.dll
echo "[fsc]   fsharp/printf/ -> $printf"
dotnet "$ZERO_FSHARP_SDK/FSharp/fsc.dll" --nologo --noframework --targetprofile:netcore \
    --reflectionfree --optimize+ --deterministic+ --metadataversion:v4.0.30319 --nowarn:75,3370 \
    --target:library -r:"$out/System.Runtime.dll" -r:"$out/System.Runtime.Extensions.dll" -r:"$out/FSharp.Core.dll" -r:"$lib" \
    --out:"$printf" "$here/printf/Printf.fs"

# async, task and MailboxProcessor: on lib/'s tasks and fibers. Next to lib,
# not in <out>: fsc takes <out> (System.Runtime.dll's directory) as the
# framework's, and looks for what its assemblies refer to only there, so
# lib's types in this library's signatures would be "not referenced" (FS0074).
control=$(dirname "$lib")/FSharp.Control.dll
echo "[fsc]   fsharp/control/ -> $control"
dotnet "$ZERO_FSHARP_SDK/FSharp/fsc.dll" --nologo --noframework --targetprofile:netcore \
    --reflectionfree --optimize+ --deterministic+ --metadataversion:v4.0.30319 --nowarn:75,3370 \
    --target:library -r:"$out/System.Runtime.dll" -r:"$out/System.Runtime.Extensions.dll" -r:"$out/FSharp.Core.dll" -r:"$lib" \
    --out:"$control" "$here/control/Control.fs" "$here/control/Arena.fs"
