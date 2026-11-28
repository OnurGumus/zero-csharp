#!/usr/bin/env bash
# Packs the Zero C# packages into sdk/feed/:
#   ZeroCSharp.Sdk               the MSBuild SDK and its payload (./build.sh --sdk-payload)
#   ZeroCSharp.Bflat.<rid>       bflat for each host, trimmed (sdk/trim-layout.sh, and for
#                                Windows sdk/trim-windows-layout.sh from the linux-x64 one)
#   ZeroCSharp.Stories260K       the Llama sample's model, as a zero library
#   ZeroCSharp.SDL3              SDL 3 bindings, a zero library, with SDL for Windows
#   ZeroCSharp.Templates         dotnet new zero, zerolib, zerollama, zerosdl, zeropico and zerogpu
#
# Usage:  sdk/pack.sh
# Env:    ZERO_HOSTS     the hosts to pack bflat for (default "linux-x64 linux-arm64 win-x64
#                        osx-arm64"; win-x64 needs sdk/trimmed/linux-x64, osx-arm64 linux-arm64)
#         ZERO_LAYOUTS   where the full layouts are (default ../bflat/layouts):
#                        linux-glibc-x64, linux-glibc-arm64, windows-x64, osx-arm64
#         BFLAT_FORK, BFLAT_COMMIT   where the fork is checked out, or its commit
#                        (the bflat packages name it; see build.sh --sdk-payload)
#         ZERO_PAYLOAD_PREBUILT=1   use sdk/ZeroCSharp.Sdk/payload as it is (CI builds it
#                        in another job, like sdk/trimmed/)
# Trimmed layouts are kept in sdk/trimmed/<rid> and redone only when a file
# they copied, or the trimming script, changed (sdk/trim-host.sh).
set -euo pipefail
cd "$(dirname "$0")/.."
hosts=${ZERO_HOSTS:-linux-x64 linux-arm64 win-x64 osx-arm64}
layouts=${ZERO_LAYOUTS:-../bflat/layouts}
payload=sdk/ZeroCSharp.Sdk/payload
version=$(sed -n 's|.*<ZeroSdkVersion>\(.*\)</ZeroSdkVersion>.*|\1|p' sdk/version.props)

mkdir -p sdk/feed
rm -f sdk/feed/ZeroCSharp.*.nupkg
[ -n "${ZERO_PAYLOAD_PREBUILT:-}" ] || ./build.sh --sdk-payload "$payload"
bflat_commit=$(sed -n 's/^bflat //p' "$payload/VERSIONS.txt")
# The bflat packages are AGPL: they must name the exact source they were built from.
if ! [[ "$bflat_commit" =~ ^[0-9a-f]{40}$ ]]; then
    echo "Error: the bflat fork's commit is '$bflat_commit'. Set BFLAT_FORK to its checkout, or BFLAT_COMMIT." >&2
    exit 1
fi

for rid in $hosts; do
    trimmed=sdk/trimmed/$rid
    sdk/trim-host.sh "$rid" "$layouts" sdk/trimmed
    dotnet pack sdk/ZeroCSharp.Bflat/ZeroCSharp.Bflat.csproj -o sdk/feed -v:q -nologo \
        -p:HostRid="$rid" -p:LayoutDir="$PWD/$trimmed" -p:BflatCommit="$bflat_commit"
done

dotnet pack sdk/ZeroCSharp.Sdk/ZeroCSharp.Sdk.csproj -o sdk/feed -v:q -nologo

# ZeroCSharp.Stories260K is a zero library, so it's built with the SDK just
# packed: staged with the version filled in, the model generated next to it,
# restored from sdk/feed (and nuget.org, for the SDK's compiler package) into
# its own packages folder: a cached SDK of the same version could be an older build.
# That folder is beside the project, not in it, or the project's **/*.cs would
# take in the C# sources the SDK carries (payload/dotnet).
stage=sdk/ZeroCSharp.Stories260K/obj/stage
rm -rf "$stage" && mkdir -p "$stage"
sed "s/@ZERO_SDK_VERSION@/$version/g" sdk/ZeroCSharp.Stories260K/ZeroCSharp.Stories260K.csproj > "$stage/ZeroCSharp.Stories260K.csproj"
scripts/llama-model-cs.sh "$stage/Stories260K.cs"
cat > "$stage/nuget.config" <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="feed" value="$PWD/sdk/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
rm -rf "$stage-packages"
NUGET_PACKAGES=$PWD/$stage-packages dotnet pack "$stage/ZeroCSharp.Stories260K.csproj" -o sdk/feed -v:q -nologo
# ZeroCSharp.SDL3, the SDL 3 bindings, the same way, with SDL's own Windows
# files (fetch-windows-sdl.sh: a pinned release, checked by its SHA-256).
sdk/ZeroCSharp.SDL3/fetch-windows-sdl.sh
sdl_stage=sdk/ZeroCSharp.SDL3/obj/stage
rm -rf "$sdl_stage" && mkdir -p "$sdl_stage"
sed "s/@ZERO_SDK_VERSION@/$version/g" sdk/ZeroCSharp.SDL3/ZeroCSharp.SDL3.csproj > "$sdl_stage/ZeroCSharp.SDL3.csproj"
cp sdk/ZeroCSharp.SDL3/SDL.cs "$sdl_stage/"
cp -R sdk/ZeroCSharp.SDL3/native "$sdl_stage/"
cp "$stage/nuget.config" "$sdl_stage/"
rm -rf "$sdl_stage-packages"
NUGET_PACKAGES=$PWD/$sdl_stage-packages dotnet pack "$sdl_stage/ZeroCSharp.SDL3.csproj" -o sdk/feed -v:q -nologo
# dotnet new zerollama's Program.cs is the Llama sample, with a header for a
# project instead of the repo's build.sh.
mkdir -p sdk/ZeroCSharp.Templates/obj/zerollama
{ cat sdk/ZeroCSharp.Templates/zerollama-header.txt; sed -n '/^using System;/,$p' samples/Llama.cs; } \
    > sdk/ZeroCSharp.Templates/obj/zerollama/Program.cs
if [ -f sdk/ZeroCSharp.Templates/ZeroCSharp.Templates.csproj ]; then
    dotnet pack sdk/ZeroCSharp.Templates/ZeroCSharp.Templates.csproj -o sdk/feed -v:q -nologo
fi
ls -la sdk/feed
