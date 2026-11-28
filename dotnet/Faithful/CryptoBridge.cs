// The crypto classes here are .NET's own (the platform's), not lib/'s; the
// streams are lib/'s. Where the two meet, .NET's hashing takes a
// System.IO.Stream of its own, which lib/'s isn't: these take lib/'s, and
// feed .NET's hash from it.
using System.IO;

namespace System.Security.Cryptography
{
    public static class ZeroSha256Stream { extension(SHA256) { public static byte[] HashData(Stream source) => ZeroStreamHashing.Hash(HashAlgorithmName.SHA256, source); } }
    public static class ZeroSha384Stream { extension(SHA384) { public static byte[] HashData(Stream source) => ZeroStreamHashing.Hash(HashAlgorithmName.SHA384, source); } }
    public static class ZeroSha512Stream { extension(SHA512) { public static byte[] HashData(Stream source) => ZeroStreamHashing.Hash(HashAlgorithmName.SHA512, source); } }

    static class ZeroStreamHashing
    {
        internal static byte[] Hash(HashAlgorithmName algorithm, Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            using IncrementalHash hash = IncrementalHash.CreateHash(algorithm);
            byte[] buffer = new byte[4096];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
            return hash.GetHashAndReset();
        }
    }
}
