#!/usr/bin/env bash
# Checks zerocore's built-in cultures against .NET's with ICU: builds
# CultureInfo, the culture table (CultureData), the number and date
# formatting and parsing, and casing into an ordinary .NET program with
# Program.cs (with InvariantGlobalization off, so .NET uses ICU), which
# compares every culture in the table: its properties, NumberFormatInfo,
# DateTimeFormatInfo and TextInfo, then numbers and dates written and read
# with it, and text cased with it.
#
# Usage:  scripts/cultures-check/check-cultures.sh [iterations] [seed]
#         (default 2000 iterations per culture; seed 12345)
# Needs:  the .NET 10 SDK (or newer), with ICU (macOS and Linux have it). The
#         table is .NET's ICU data from when scripts/gen-cultures last ran, so
#         a different ICU version can differ in a few strings.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here/Program.cs" "$work/"
# In a namespace of its own, so it can't reach .NET's internals by name.
for f in Globalization/CultureInfo Globalization/CultureData Globalization/CultureData.Lookup Globalization/NumberFormatInfo \
         Globalization/DateTimeFormatInfo Globalization/TextInfo Globalization/CompareInfo Globalization/TextCasing \
         Globalization/CharUnicodeInfo Globalization/UnicodeData Text/Rune Number.Formatting Number.Parsing Number.Culture \
         DateTimeFormat DateTime DateTimeKind DateTimeOffset DateOnly TimeSpan TimeZoneInfo; do
    perl -pe 'print "using System;\nusing System.Globalization;\nusing System.Runtime.InteropServices;\n" if $. == 1;
              s/^namespace System(\.Globalization|\.Text)?$/namespace ZeroTest/;
              s/DllImport\("\*"\)/DllImport("libc")/' \
        "$root/zerocore/System/$f.cs" > "$work/$(basename "$f").cs"
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
"$work/out/check" "${1:-2000}" "${2:-12345}"
