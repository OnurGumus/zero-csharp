#!/usr/bin/env bash
# Reports static fields that zerocore and lib/ fill on first use, outside a
# static constructor and without ObjectAllocator.Lasting(): caches that,
# filled for the first time inside an object scope, would point into memory
# the scope frees. See Program.cs for the rules, allowed.txt for the
# exceptions and why each is safe.
#
# Usage:  scripts/static-stores/check.sh [bin]   (default: bin, after ./build.sh)
# Needs:  the .NET 10 SDK (or newer).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
bin=${1:-$root/bin}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Built outside the repository: its Directory.Build.props makes every
# project a zero-mode one.
cp "$here/Program.cs" "$work/"
cat > "$work/StaticStores.csproj" <<'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <RollForward>Major</RollForward>
  </PropertyGroup>
</Project>
PROJ
dotnet build "$work" -c Release -o "$work/out" -v quiet -nologo > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 2; }
dotnet "$work/out/StaticStores.dll" "$here/allowed.txt" \
    "$bin/System.Private.CoreLib.dll" "$bin/BflatLib.dll"
