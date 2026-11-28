#!/usr/bin/env bash
# Checks zerocore's number parsing against .NET's: builds
# zerocore/System/Number.Parsing.cs into an ordinary .NET program with
# Program.cs, which parses random, edge and halfway-point strings as every
# numeric type with every kind of NumberStyles both ways and compares the
# value, or the exception and its message.
#
# Usage:  scripts/parse-check/check-parsing.sh [iterations] [seed]
#         (default 200000 iterations, a few million checks; seed 12345)
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
  </PropertyGroup>
</Project>
EOT
dotnet run --project "$work/check.csproj" -c Release -- "${1:-200000}" "${2:-12345}"
