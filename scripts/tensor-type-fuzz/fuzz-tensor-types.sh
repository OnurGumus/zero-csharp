#!/usr/bin/env bash
# Checks zerocore's Tensor<T>, TensorSpan<T> and Tensor class against .NET's
# (the System.Numerics.Tensors package): builds zerocore's tensor sources
# (zerocore/System/Numerics/Tensors/ and NIndex/NRange), moved into namespace
# ZeroTest, into an ordinary .NET program with Program.cs, which runs both on
# random tensors (dense, sliced, permuted; broadcasting) and compares every
# result and every argument error.
#
# Usage:  scripts/tensor-type-fuzz/fuzz-tensor-types.sh [iterations] [seed]
#         (default 2000 iterations; seed 12345)
# Needs:  the .NET 10 SDK (or newer) and nuget.org for the package.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here/Program.cs" "$work/"
mkdir "$work/zero"
# Everything moves into ZeroTest; the usings a file got for free from being
# inside System.* come with it. zerocore's string.FromChars is .NET's
# new string(span).
for f in "$root"/zerocore/System/Numerics/Tensors/*.cs "$root"/zerocore/System/Buffers/NIndex.cs "$root"/zerocore/System/Buffers/NRange.cs; do
    perl -pe '
        if ($. == 1) { print "using System;\nusing System.Collections;\nusing System.Collections.Generic;\nusing System.Diagnostics;\nusing System.Numerics;\nusing System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\nusing System.Runtime.Intrinsics;\nusing System.Runtime.Intrinsics.Arm;\nusing System.Runtime.Intrinsics.X86;\nusing System.Text;\n"; }
        s/^using System\.Buffers;$//;
        s/^using System\.Numerics\.Tensors;$//;
        s/^namespace System(\.Numerics\.Tensors|\.Buffers|\.Runtime\.InteropServices)?$/namespace ZeroTest/;
        s/\bstring\.FromChars\(/new string(/g;
    ' "$f" > "$work/zero/$(basename "$f")"
done
cat > "$work/fuzz.csproj" <<'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ImplicitUsings>disable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>CS1718;CS0436;CS8500;SYSLIB5001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Numerics.Tensors" Version="11.0.0" />
  </ItemGroup>
</Project>
PROJ
"${DOTNET:-dotnet}" run --project "$work/fuzz.csproj" -c Release -- "${1:-2000}" "${2:-12345}"
