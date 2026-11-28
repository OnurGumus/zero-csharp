#!/usr/bin/env bash
# Checks lib/'s cryptography against .NET's and against plain big-integer
# versions of the math: builds lib/CryptoHashes.cs, CryptoChaCha.cs,
# CryptoX25519.cs, CryptoAes.cs and EncryptedStream.cs, moved into namespace ZeroTest, into
# an ordinary .NET program with Program.cs, which runs both on random input:
#
# - SHA-256/384/512, HMAC and HKDF against System.Security.Cryptography,
#   with data appended in random pieces;
# - ChaCha20-Poly1305 and AES-GCM (all key and tag sizes) against .NET's,
#   and a changed bit always failing;
# - Poly1305 against a BigInteger version of RFC 8439's definition, with
#   the RFC's edge-case vectors and limbs of all ones;
# - X25519 against a BigInteger version of RFC 7748's ladder, with u values
#   at and above p, and RFC 7748's 1000-iteration vector.
#
# ./test.sh runs samples/Crypto.cs, the standards' own vectors, on each target.
#
# Usage:  scripts/crypto-fuzz/fuzz-crypto.sh [iterations] [seed]
#         (default 20000 iterations; seed 12345)
# Needs:  the .NET 10 SDK (or newer).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here"/*.cs "$work/"
for source in "$root"/lib/{CryptoHashes,CryptoChaCha,CryptoX25519,CryptoAes,EncryptedStream,CryptoBigNumbers,CryptoAsn1,CryptoRsa,CryptoEcdsa,CryptoP256,CryptoX509,CryptoX509Chain,TlsRecords,TlsHandshake,SslStream}.cs; do
    # The namespace keeps the types apart from .NET's own.
    perl -pe 's/^namespace System\.Security\.Cryptography(\.X509Certificates)?;$/namespace ZeroTest$1;/;
              s/^namespace System\.Net\.Security/namespace ZeroTest.Net.Security/;
              s/^namespace System\.Security\.Authentication/namespace ZeroTest.Authentication/;
              s/^using System\.Security\.Cryptography\.X509Certificates;/using ZeroTest.X509Certificates;/;
              s/^using System\.Security\.Cryptography;/using ZeroTest;/;
              s/^using System\.Security\.Authentication;/using ZeroTest.Authentication;/' \
        "$source" > "$work/$(basename "$source")"
done
cat > "$work/Shims.cs" <<'SHIM'
namespace ZeroTest
{
    // lib/CryptoRandom.cs calls the C library; .NET has its own.
    internal static class RandomNumberGenerator
    {
        internal static void Fill(System.Span<byte> data)
            => System.Security.Cryptography.RandomNumberGenerator.Fill(data);
    }
}
SHIM
# lib/'s own File, Directory and Clock call the C library: .NET's stand in.
awk '/^public struct CalendarTime/,0' "$root/lib/Clock.cs" > "$work/CalendarTime.cs"
cat > "$work/LibShims.cs" <<'SHIM'
using System;
internal static class Clock
{
    internal static long UnixMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static CalendarTime UtcNow => CalendarTime.FromUnixMilliseconds(UnixMilliseconds);
    internal static long FloorDivide(long value, long divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }
}
internal static class File
{
    internal static bool Exists(string path) => System.IO.File.Exists(path);
}
internal struct DirectoryEntry
{
    public string Name;
    public bool IsDirectory;
}
internal static class Directory
{
    internal static bool Exists(string path) => System.IO.Directory.Exists(path);
    internal static DirectoryEntry[] Enumerate(string path)
    {
        var list = new System.Collections.Generic.List<DirectoryEntry>();
        foreach (string f in System.IO.Directory.GetFileSystemEntries(path))
            list.Add(new DirectoryEntry { Name = System.IO.Path.GetFileName(f), IsDirectory = System.IO.Directory.Exists(f) });
        return list.ToArray();
    }
}
SHIM
cat > "$work/fuzz.csproj" <<'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>
</Project>
PROJ
CERTIFICATES_SAMPLE="$root/samples/Certificates.cs" dotnet run --project "$work/fuzz.csproj" -c Release -- "${1:-20000}" "${2:-12345}"
