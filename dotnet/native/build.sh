#!/usr/bin/env bash
# libzerocompat: the native half of dotnet/ZeroCompat on macOS (see compat-mac.c).
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p ../out
case "$(uname)" in
    Darwin) clang -O2 -dynamiclib -arch arm64 -arch x86_64 compat-mac.c ../../mac/runtime.c -o ../out/libzerocompat.dylib ;;
    Linux) ;; # nothing to build: Linux has glibc itself (see NativeResolver in ../ZeroCompat/Shims.cs)
    *) echo "dotnet/native: macOS and Linux only for now" >&2; exit 1 ;;
esac
