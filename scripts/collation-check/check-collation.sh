#!/usr/bin/env bash
# Checks zerocore's collation (how named cultures sort and search text)
# against .NET's with ICU: builds Collator and its tables (CollationData, and
# the culture names from CultureData) into an ordinary .NET program with
# Program.cs (with InvariantGlobalization off, so .NET uses ICU), which, in
# every culture in the table and with each set of options, compares random
# texts drawn from every assigned character (and each culture's alphabet),
# checks that texts comparing equal hash the same, and searches them
# (IsPrefix, IsSuffix, IndexOf, LastIndexOf with the match length).
#
# Usage:  scripts/collation-check/check-collation.sh [iterations] [seed]
#         (default 20000 per culture and option set; seed 12345)
#         SEARCH=0 ...      compare only
#         SHOW=tr-TR ...    show that culture's failures
#         PAIR="tr-TR|ılık|ilaç" ...   compare just these two, with each set of options
# Needs:  the .NET 10 SDK (or newer), with ICU (macOS and Linux have it).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here/Program.cs" "$root/scripts/zerocore-shims.cs" "$work/"
for f in Collator CollationData CultureData; do
    perl -pe 'print "using System;\nusing System.Globalization;\n" if $. == 1;
              s/^namespace System\.Globalization$/namespace ZeroTest/' \
        "$root/zerocore/System/Globalization/$f.cs" > "$work/$f.cs"
done
cat > "$work/check.csproj" <<'EOT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ImplicitUsings>disable</ImplicitUsings>
    <InvariantGlobalization>false</InvariantGlobalization>
    <NoWarn>CS0660;CS0661;CS8981;CS0169;CS0414;CS0108;CS0436;CS0105;CS0162</NoWarn>
  </PropertyGroup>
</Project>
EOT
dotnet build -c Release -v q "$work/check.csproj" -o "$work/out" > "$work/build.log" || { grep -E "error" "$work/build.log" | head -20; exit 1; }
"$work/out/check" "${1:-20000}" "${2:-12345}"
