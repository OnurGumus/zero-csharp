#!/usr/bin/env bash
# Checks zerocore's numbers written and read with a NumberFormatInfo of your
# own against .NET's: builds the number formatting and parsing (with
# Number.Culture.cs and NumberFormatInfo) into an ordinary .NET program with
# Program.cs, which makes random NumberFormatInfos (separators, signs, group
# sizes, currency and percent patterns, symbols) the same way on both sides,
# then formats and parses with them and compares the text, the value, or the
# exception and its message.
#
# Usage:  scripts/culture-check/check-culture.sh [iterations] [seed]
#         (default 100000 iterations; seed 12345)
# Needs:  the .NET 10 SDK (or newer).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here/Program.cs" "$root/scripts/zerocore-shims.cs" "$work/"
# In a namespace of its own, so it can't reach .NET's internals by name.
for f in Number.Formatting Number.Parsing Number.Culture Globalization/NumberFormatInfo Globalization/TextCasing Globalization/CharUnicodeInfo Globalization/UnicodeData; do
    perl -pe 'print "using System;\nusing System.Globalization;\n" if $. == 1;
              s/^namespace System(\.Globalization)?$/namespace ZeroTest/' \
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
    <NoWarn>CS0660;CS0661;CS8981;CS0169;CS0414;CS0108;CS0436;CS0105</NoWarn>
  </PropertyGroup>
</Project>
EOT
dotnet run --project "$work/check.csproj" -c Release -- "${1:-100000}" "${2:-12345}"
