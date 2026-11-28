#!/usr/bin/env bash
# libzerocompat: the native half of dotnet/ZeroCompat (see compat-mac.c).
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p ../out
case "$(uname)" in
    Darwin) clang -O2 -dynamiclib -arch arm64 -arch x86_64 compat-mac.c ../../mac/runtime.c -o ../out/libzerocompat.dylib ;;
    *) echo "dotnet/native: only macOS for now" >&2; exit 1 ;;
esac
