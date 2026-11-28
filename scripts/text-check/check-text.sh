#!/usr/bin/env bash
# Checks zerocore's string casing and comparing against .NET's in invariant
# mode: builds TextCasing, CompareInfo, TextInfo, Rune and StringOps (which
# string's ToUpper, Compare, IndexOf, ... forward to) with the Unicode tables
# into an ordinary .NET program with Program.cs, which compares every code
# point's casing, then random strings: casing, ToTitleCase, every comparison
# and search overload, and Rune (the result, or the exception and its message).
#
# Usage:  scripts/text-check/check-text.sh [iterations] [seed]
#         (default 200000 iterations; seed 12345)
# Needs:  the .NET 10 SDK (or newer).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here/Program.cs" "$root/scripts/zerocore-shims.cs" "$work/"
# In a namespace of its own, so it can't reach .NET's internals by name.
for f in Globalization/TextCasing Globalization/CompareInfo Globalization/TextInfo Text/Rune StringOps \
         Globalization/CharUnicodeInfo Globalization/UnicodeData Globalization/Collator Globalization/CollationData; do
    perl -pe 'print "using System;\nusing System.Globalization;\n" if $. == 1;
              s/^namespace System(\.Globalization|\.Text)?$/namespace ZeroTest/' \
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
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>CS0660;CS0661;CS8981;CS0169;CS0414;CS0108;CS0436</NoWarn>
  </PropertyGroup>
</Project>
EOT
dotnet run --project "$work/check.csproj" -c Release -- "${1:-200000}" "${2:-12345}"
