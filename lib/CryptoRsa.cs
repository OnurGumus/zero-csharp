// RSA signature verification (RFC 8017), with .NET's RSA API:
//
//   using RSA rsa = RSA.Create();
//   rsa.ImportSubjectPublicKeyInfo(publicKey, out _);   // or certificate.GetRSAPublicKey()
//   bool ok = rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
//
// Both paddings are here: PKCS #1 v1.5, which most certificates are signed
// with, and PSS, which TLS 1.3 servers sign the handshake with. With PSS the
// salt is as long as the hash, as TLS 1.3 requires and .NET does.
//
// Only public keys: this checks signatures, it doesn't make them, and it
// doesn't encrypt or decrypt. A public key's math has no secrets, so it
// doesn't need to run in constant time (lib/CryptoBigNumbers.cs). Keys of
// 512 to 8192 bits are accepted; certificates today use 2048 to 4096.

using System;
using System.Buffers.Binary;

namespace System.Security.Cryptography;

/// <summary>The base of RSA and ECDsa.</summary>
public abstract class AsymmetricAlgorithm : IDisposable
{
    /// <summary>The key's size in bits: the modulus for RSA, the curve's field for ECDSA.</summary>
    public virtual int KeySize { get; set; }

    public void Dispose()
    {
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing) { }
}

public enum RSASignaturePaddingMode
{
    Pkcs1 = 0,
    Pss = 1,
}

/// <summary>How an RSA signature is padded: PKCS #1 v1.5 or PSS.</summary>
public sealed class RSASignaturePadding
{
    RSASignaturePadding(RSASignaturePaddingMode mode) { Mode = mode; }

    public RSASignaturePaddingMode Mode { get; }

    /// <summary>PKCS #1 v1.5: what most certificates are signed with.</summary>
    public static RSASignaturePadding Pkcs1 { get; } = new RSASignaturePadding(RSASignaturePaddingMode.Pkcs1);

    /// <summary>PSS with MGF1 and a salt as long as the hash: what TLS 1.3 signs handshakes with.</summary>
    public static RSASignaturePadding Pss { get; } = new RSASignaturePadding(RSASignaturePaddingMode.Pss);

    public override string ToString() => Mode == RSASignaturePaddingMode.Pss ? "Pss" : "Pkcs1";
}

/// <summary>An RSA public key's numbers, big-endian.</summary>
public struct RSAParameters
{
    public byte[] Modulus;
    public byte[] Exponent;
}

/// <summary>RSA signature checking with a public key.</summary>
public sealed class RSA : AsymmetricAlgorithm
{
    byte[] _modulus;  // big-endian, no leading zeros
    byte[] _exponent;
    Modulus _n;

    RSA() { }

    /// <summary>An RSA object with no key yet: import one before verifying.</summary>
    public static RSA Create() => new RSA();

    /// <summary>An RSA object with this public key.</summary>
    public static RSA Create(RSAParameters parameters)
    {
        RSA rsa = new RSA();
        rsa.ImportParameters(parameters);
        return rsa;
    }

    public override int KeySize
    {
        get => _n == null ? 0 : _n.Bits;
        set => throw new CryptographicException("RSA: import a key to set its size; this RSA doesn't make keys.");
    }

    public void ImportParameters(RSAParameters parameters)
    {
        if (!TrySetKey(parameters.Modulus, parameters.Exponent))
            throw new CryptographicException("RSA: not a usable public key (512 to 8192 bits, odd modulus).");
    }

    /// <summary>The public key's numbers.</summary>
    public RSAParameters ExportParameters(bool includePrivateParameters)
    {
        if (includePrivateParameters)
            throw new CryptographicException("RSA: this object has only a public key.");
        if (_n == null)
            throw new CryptographicException("RSA: no key has been imported.");
        return new RSAParameters { Modulus = _modulus.AsSpan().ToArray(), Exponent = _exponent.AsSpan().ToArray() };
    }

    /// <summary>Imports a SubjectPublicKeyInfo (a certificate's key, or "BEGIN PUBLIC KEY" PEM's bytes).</summary>
    public void ImportSubjectPublicKeyInfo(ReadOnlySpan<byte> source, out int bytesRead)
    {
        if (!TryImportSubjectPublicKeyInfo(source, out bytesRead))
            throw new CryptographicException("ASN1 corrupted data.");
    }

    /// <summary>Imports PKCS #1's RSAPublicKey (a "BEGIN RSA PUBLIC KEY" PEM's bytes).</summary>
    public void ImportRSAPublicKey(ReadOnlySpan<byte> source, out int bytesRead)
    {
        if (!TryImportRsaPublicKey(source, out bytesRead))
            throw new CryptographicException("ASN1 corrupted data.");
    }

    internal bool TryImportSubjectPublicKeyInfo(ReadOnlySpan<byte> source, out int bytesRead)
    {
        bytesRead = 0;
        DerReader outer = new DerReader(source);
        outer.Read(DerReader.Sequence, out ReadOnlySpan<byte> encoded);
        if (outer.Failed)
            return false;
        DerReader spki = new DerReader(encoded);
        spki = spki.ReadSequence();
        DerReader algorithm = spki.ReadSequence();
        ReadOnlySpan<byte> oid = algorithm.ReadOid();
        // The parameters must be NULL, though some encoders leave them out.
        if (algorithm.HasData)
            algorithm.Read(DerReader.Null);
        algorithm.End();
        ReadOnlySpan<byte> key = spki.ReadBitStringBytes();
        spki.End();
        if (spki.Failed || algorithm.Failed || !DerReader.Same(oid, Oids.RsaEncryption))
            return false;
        if (!TryImportRsaPublicKey(key, out int keyRead) || keyRead != key.Length)
            return false;
        bytesRead = encoded.Length;
        return true;
    }

    internal bool TryImportRsaPublicKey(ReadOnlySpan<byte> source, out int bytesRead)
    {
        bytesRead = 0;
        DerReader outer = new DerReader(source);
        outer.Read(DerReader.Sequence, out ReadOnlySpan<byte> encoded);
        DerReader key = new DerReader(encoded).ReadSequence();
        ReadOnlySpan<byte> modulus = key.ReadUnsignedInteger();
        ReadOnlySpan<byte> exponent = key.ReadUnsignedInteger();
        key.End();
        if (outer.Failed || key.Failed || !TrySetKey(modulus, exponent))
            return false;
        bytesRead = encoded.Length;
        return true;
    }

    bool TrySetKey(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> exponent)
    {
        modulus = Modulus.TrimZeros(modulus);
        exponent = Modulus.TrimZeros(exponent);
        if (modulus.Length < 64 || modulus.Length > 1024 || exponent.IsEmpty || exponent.Length > modulus.Length)
            return false;
        Modulus n = Modulus.Create(modulus);
        if (n == null)
            return false;
        _n = n;
        _modulus = modulus.ToArray();
        _exponent = exponent.ToArray();
        return true;
    }

    public bool VerifyData(byte[] data, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (signature == null)
            throw new ArgumentNullException(nameof(signature));
        return VerifyData(new ReadOnlySpan<byte>(data), new ReadOnlySpan<byte>(signature), hashAlgorithm, padding);
    }

    /// <summary>True if <paramref name="signature"/> is this key's signature of <paramref name="data"/>.</summary>
    public bool VerifyData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        HashKind kind = hashAlgorithm.Kind;
        Span<byte> hash = stackalloc byte[64];
        hash = hash.Slice(0, HashState.SizeOf(kind));
        HashState.Hash(kind, data, hash);
        return VerifyHash(hash, signature, hashAlgorithm, padding);
    }

    public bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (hash == null)
            throw new ArgumentNullException(nameof(hash));
        if (signature == null)
            throw new ArgumentNullException(nameof(signature));
        return VerifyHash(new ReadOnlySpan<byte>(hash), new ReadOnlySpan<byte>(signature), hashAlgorithm, padding);
    }

    /// <summary>True if <paramref name="signature"/> is this key's signature of a message with this hash.</summary>
    public bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (padding == null)
            throw new ArgumentNullException(nameof(padding));
        if (_n == null)
            throw new CryptographicException("RSA: no key has been imported.");
        HashKind kind = hashAlgorithm.Kind;
        if (hash.Length != HashState.SizeOf(kind))
            return false;
        return padding.Mode == RSASignaturePaddingMode.Pss
            ? VerifyPss(hash, signature, kind)
            : VerifyPkcs1(hash, signature, kind);
    }

    // m = s^e mod n, written big-endian into em (the modulus's length). False if
    // the signature isn't exactly that long or isn't below n.
    bool Open(ReadOnlySpan<byte> signature, Span<byte> em)
    {
        if (signature.Length != _modulus.Length)
            return false;
        int length = _n.Length;
        Span<uint> s = stackalloc uint[length];
        if (!Modulus.FromBytes(signature, s) || !_n.IsReduced(s))
            return false;
        _n.ToMontgomery(s, s);
        _n.Power(s, s, _exponent);
        _n.FromMontgomery(s, s);
        Modulus.ToBytes(s, em);
        return true;
    }

    bool VerifyPkcs1(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, HashKind kind)
    {
        int k = _modulus.Length;
        ReadOnlySpan<byte> prefix = kind == HashKind.Sha256 ? DigestInfoSha256
            : kind == HashKind.Sha384 ? DigestInfoSha384 : DigestInfoSha512;
        int t = prefix.Length + hash.Length;
        if (k < t + 11)
            return false;
        Span<byte> em = stackalloc byte[k];
        if (!Open(signature, em))
            return false;

        // EM = 00 01 FF...FF 00 DigestInfo hash: build it and compare all of it.
        Span<byte> expected = stackalloc byte[k];
        expected[0] = 0;
        expected[1] = 1;
        expected.Slice(2, k - t - 3).Fill(0xFF);
        expected[k - t - 1] = 0;
        prefix.CopyTo(expected.Slice(k - t));
        hash.CopyTo(expected.Slice(k - hash.Length));
        return CryptographicOperations.FixedTimeEquals(em, expected);
    }

    bool VerifyPss(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, HashKind kind)
    {
        int k = _modulus.Length;
        int hLen = hash.Length;
        int sLen = hLen;
        int emBits = _n.Bits - 1;
        int emLen = (emBits + 7) / 8;
        if (emLen < hLen + sLen + 2)
            return false;
        Span<byte> full = stackalloc byte[k];
        if (!Open(signature, full))
            return false;
        // When the modulus's bit length is 1 more than a multiple of 8, EM is a
        // byte shorter than the modulus, and that byte must be 0.
        if (k > emLen && full[0] != 0)
            return false;
        Span<byte> em = full.Slice(k - emLen);
        if (em[emLen - 1] != 0xBC)
            return false;
        int dbLength = emLen - hLen - 1;
        Span<byte> db = em.Slice(0, dbLength);
        ReadOnlySpan<byte> h = em.Slice(dbLength, hLen);
        int topBits = 8 * emLen - emBits;
        byte topMask = (byte)(0xFF >> topBits);
        if ((db[0] & ~topMask) != 0)
            return false;

        // DB = maskedDB XOR MGF1(H), then 00...00 01 salt.
        Mgf1Xor(kind, h, db);
        db[0] &= topMask;
        int padding = emLen - hLen - sLen - 2;
        for (int i = 0; i < padding; i++)
            if (db[i] != 0)
                return false;
        if (db[padding] != 1)
            return false;
        ReadOnlySpan<byte> salt = db.Slice(dbLength - sLen);

        // H' = Hash(8 zero bytes, mHash, salt)
        HashState state = default;
        state.Initialize(kind);
        Span<byte> zeros = stackalloc byte[8];
        zeros.Clear();
        state.Append(zeros);
        state.Append(hash);
        state.Append(salt);
        Span<byte> expected = stackalloc byte[64];
        expected = expected.Slice(0, hLen);
        state.Finish(expected);
        return CryptographicOperations.FixedTimeEquals(h, expected);
    }

    // data ^= MGF1(seed), RFC 8017 B.2.1.
    static void Mgf1Xor(HashKind kind, ReadOnlySpan<byte> seed, Span<byte> data)
    {
        int hLen = HashState.SizeOf(kind);
        Span<byte> block = stackalloc byte[64];
        block = block.Slice(0, hLen);
        Span<byte> counter = stackalloc byte[4];
        for (int at = 0, n = 0; at < data.Length; at += hLen, n++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(counter, (uint)n);
            HashState state = default;
            state.Initialize(kind);
            state.Append(seed);
            state.Append(counter);
            state.Finish(block);
            int take = Math.Min(hLen, data.Length - at);
            for (int i = 0; i < take; i++)
                data[at + i] ^= block[i];
        }
    }

    static ReadOnlySpan<byte> DigestInfoSha256 => [0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20];
    static ReadOnlySpan<byte> DigestInfoSha384 => [0x30, 0x41, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x02, 0x05, 0x00, 0x04, 0x30];
    static ReadOnlySpan<byte> DigestInfoSha512 => [0x30, 0x51, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x03, 0x05, 0x00, 0x04, 0x40];
}

/// <summary>The encoded object identifiers certificates and keys use.</summary>
internal static class Oids
{
    // 1.2.840.113549.1.1.1
    internal static ReadOnlySpan<byte> RsaEncryption => [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01];
    // 1.2.840.113549.1.1.10
    internal static ReadOnlySpan<byte> RsaPss => [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x0a];
    // 1.2.840.113549.1.1.11 / 12 / 13
    internal static ReadOnlySpan<byte> Sha256WithRsa => [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x0b];
    internal static ReadOnlySpan<byte> Sha384WithRsa => [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x0c];
    internal static ReadOnlySpan<byte> Sha512WithRsa => [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x0d];
    // 1.2.840.10045.2.1
    internal static ReadOnlySpan<byte> EcPublicKey => [0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01];
    // 1.2.840.10045.3.1.7, 1.3.132.0.34
    internal static ReadOnlySpan<byte> P256 => [0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07];
    internal static ReadOnlySpan<byte> P384 => [0x2b, 0x81, 0x04, 0x00, 0x22];
    // 1.2.840.10045.4.3.2 / 3 / 4
    internal static ReadOnlySpan<byte> EcdsaSha256 => [0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02];
    internal static ReadOnlySpan<byte> EcdsaSha384 => [0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x03];
    internal static ReadOnlySpan<byte> EcdsaSha512 => [0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x04];
}
