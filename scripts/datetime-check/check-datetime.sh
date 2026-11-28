#!/usr/bin/env bash
# Checks zerocore's dates and times against .NET's: builds zerocore's
# DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, TimeZoneInfo,
# ISOWeek and their formatting into an ordinary .NET program with
# Program.cs, which compares every member on random and edge values (the
# result, or the exception and its message), once for each time zone below.
#
# Usage:  scripts/datetime-check/check-datetime.sh [iterations] [seed]
#         (default 100000 iterations per zone; seed 12345)
# Needs:  the .NET 10 SDK (or newer), and the system's tz database.
# ZONES="A B" checks only those time zones.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here/Program.cs" "$root/scripts/zerocore-shims.cs" "$work/"
# In a namespace of its own, so it can't reach .NET's internals by name, and
# with the C library found by name instead of at link time.
for f in TimeSpan DateTime DateTimeKind DateTimeOffset DateOnly DateTimeFormat TimeZoneInfo Globalization/ISOWeek \
         Globalization/DateTimeFormatInfo Globalization/TextCasing Globalization/CharUnicodeInfo Globalization/UnicodeData; do
    perl -pe 'print "using System;\nusing System.Globalization;\nusing System.Runtime.InteropServices;\n" if $. == 1;
              s/^namespace System(\.Globalization)?$/namespace ZeroTest/;
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
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>CS0660;CS0661;CS8981;CS0169;CS0414</NoWarn>
  </PropertyGroup>
</Project>
EOT
dotnet build -c Release -v q "$work/check.csproj" -o "$work/out" > "$work/build.log" || { cat "$work/build.log"; exit 1; }
status=0
for zone in ${ZONES:-UTC America/New_York Europe/Istanbul Asia/Kathmandu Australia/Lord_Howe Europe/Dublin}; do
    echo "== TZ=$zone"
    TZ=$zone "$work/out/check" "${1:-100000}" "${2:-12345}" || status=1
done
exit $status
