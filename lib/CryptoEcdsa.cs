// ECDSA signature verification (FIPS 186-5) on the NIST curves P-256 and
// P-384, with .NET's ECDsa API:
//
//   using ECDsa ecdsa = ECDsa.Create();
//   ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);  // or certificate.GetECDsaPublicKey()
//   bool ok = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
//
// Signatures come in two formats: r and s side by side, each as long as the
// curve's numbers (IEEE P1363, .NET's default), or as a DER SEQUENCE of two
// INTEGERs (RFC 3279), which certificates and TLS use:
//
//   ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256,
//                    DSASignatureFormat.Rfc3279DerSequence);
//
// These two curves sign nearly every certificate and TLS handshake that
// isn't RSA. P-521 and the Brainpool curves aren't here.
//
// Only public keys: this checks signatures and doesn't make them, so all
// the numbers are public and the arithmetic (lib/CryptoBigNumbers.cs)
// needn't run in constant time. Points are in Jacobian coordinates
// (X/Z², Y/Z³), so adding them needs no division, and u1·G + u2·Q is one
// pass over the bits of both (Shamir's trick).

using System;

namespace System.Security.Cryptography;

public enum DSASignatureFormat
{
    /// <summary>r then s, each as long as the curve's numbers: .NET's default.</summary>
    IeeeP1363FixedFieldConcatenation = 0,
    /// <summary>A DER SEQUENCE of the INTEGERs r and s: what certificates and TLS use.</summary>
    Rfc3279DerSequence = 1,
}

/// <summary>A point on a curve: big-endian coordinates, each as long as the curve's numbers.</summary>
public struct ECPoint
{
    public byte[] X;
    public byte[] Y;
}

/// <summary>An elliptic curve, named by its Oid. Only P-256 and P-384 work here.</summary>
public struct ECCurve
{
    /// <summary>The curve's object identifier.</summary>
    public Oid Oid;

    /// <summary>The curves ECDsa here can use.</summary>
    public static class NamedCurves
    {
        /// <summary>NIST P-256 (secp256r1, prime256v1).</summary>
        public static ECCurve nistP256 => new ECCurve { Oid = new Oid("1.2.840.10045.3.1.7", "nistP256") };

        /// <summary>NIST P-384 (secp384r1).</summary>
        public static ECCurve nistP384 => new ECCurve { Oid = new Oid("1.3.132.0.34", "nistP384") };
    }

    public static ECCurve CreateFromValue(string oidValue) => new ECCurve { Oid = new Oid(oidValue) };

    public static ECCurve CreateFromFriendlyName(string oidFriendlyName)
    {
        if (oidFriendlyName == "nistP256" || oidFriendlyName == "ECDSA_P256" || oidFriendlyName == "secp256r1" || oidFriendlyName == "prime256v1")
            return NamedCurves.nistP256;
        if (oidFriendlyName == "nistP384" || oidFriendlyName == "ECDSA_P384" || oidFriendlyName == "secp384r1")
            return NamedCurves.nistP384;
        return new ECCurve { Oid = new Oid(null, oidFriendlyName) };
    }

    internal Curve Find()
    {
        if (Oid == null)
            return null;
        if (Oid.Value == "1.2.840.10045.3.1.7" || (Oid.Value == null && CreateFromFriendlyName(Oid.FriendlyName).Oid.Value == "1.2.840.10045.3.1.7"))
            return Curve.P256;
        if (Oid.Value == "1.3.132.0.34" || (Oid.Value == null && CreateFromFriendlyName(Oid.FriendlyName).Oid.Value == "1.3.132.0.34"))
            return Curve.P384;
        return null;
    }
}

/// <summary>An elliptic-curve key's numbers: the curve and the public point.</summary>
public struct ECParameters
{
    public ECCurve Curve;
    public ECPoint Q;
}

/// <summary>ECDSA signature checking with a public key, on P-256 or P-384.</summary>
public sealed class ECDsa : AsymmetricAlgorithm
{
    Curve _curve;
    uint[] _q; // the public point, affine, Montgomery form: x then y

    ECDsa() { }

    /// <summary>An ECDsa object with no key yet: import one before verifying.</summary>
    public static ECDsa Create() => new ECDsa();

    /// <summary>An ECDsa object with this public key.</summary>
    public static ECDsa Create(ECParameters parameters)
    {
        ECDsa ecdsa = new ECDsa();
        ecdsa.ImportParameters(parameters);
        return ecdsa;
    }

    public override int KeySize
    {
        get => _curve == null ? 0 : _curve.P.Bits;
        set => throw new CryptographicException("ECDsa: import a key to choose the curve; this ECDsa doesn't make keys.");
    }

    public void ImportParameters(ECParameters parameters)
    {
        Curve curve = parameters.Curve.Find();
        if (curve == null)
            throw new CryptographicException("ECDsa: only the curves nistP256 and nistP384 are supported.");
        if (parameters.Q.X == null || parameters.Q.Y == null
            || parameters.Q.X.Length != curve.Bytes || parameters.Q.Y.Length != curve.Bytes
            || !TrySetKey(curve, parameters.Q.X, parameters.Q.Y))
            throw new CryptographicException("ECDsa: the public key isn't a point on the curve.");
    }

    /// <summary>The curve and public point.</summary>
    public ECParameters ExportParameters(bool includePrivateParameters)
    {
        if (includePrivateParameters)
            throw new CryptographicException("ECDsa: this object has only a public key.");
        if (_curve == null)
            throw new CryptographicException("ECDsa: no key has been imported.");
        int length = _curve.P.Length;
        byte[] x = new byte[_curve.Bytes], y = new byte[_curve.Bytes];
        Span<uint> t = stackalloc uint[length];
        _curve.P.FromMontgomery(t, _q.AsSpan(0, length));
        Modulus.ToBytes(t, x);
        _curve.P.FromMontgomery(t, _q.AsSpan(length, length));
        Modulus.ToBytes(t, y);
        return new ECParameters
        {
            Curve = _curve.Bits == 256 ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384,
            Q = new ECPoint { X = x, Y = y },
        };
    }

    /// <summary>Imports a SubjectPublicKeyInfo (a certificate's key, or "BEGIN PUBLIC KEY" PEM's bytes).</summary>
    public void ImportSubjectPublicKeyInfo(ReadOnlySpan<byte> source, out int bytesRead)
    {
        if (!TryImportSubjectPublicKeyInfo(source, out bytesRead))
            throw new CryptographicException("ECDsa: not a P-256 or P-384 public key.");
    }

    internal bool TryImportSubjectPublicKeyInfo(ReadOnlySpan<byte> source, out int bytesRead)
    {
        bytesRead = 0;
        DerReader outer = new DerReader(source);
        outer.Read(DerReader.Sequence, out ReadOnlySpan<byte> encoded);
        DerReader spki = new DerReader(encoded).ReadSequence();
        DerReader algorithm = spki.ReadSequence();
        ReadOnlySpan<byte> oid = algorithm.ReadOid();
        ReadOnlySpan<byte> curveOid = algorithm.ReadOid();
        algorithm.End();
        ReadOnlySpan<byte> point = spki.ReadBitStringBytes();
        spki.End();
        if (outer.Failed || spki.Failed || algorithm.Failed || !DerReader.Same(oid, Oids.EcPublicKey))
            return false;
        Curve curve = DerReader.Same(curveOid, Oids.P256) ? Curve.P256
            : DerReader.Same(curveOid, Oids.P384) ? Curve.P384 : null;
        if (curve == null || !TryImportPoint(curve, point))
            return false;
        bytesRead = encoded.Length;
        return true;
    }

    // An uncompressed point: 04, x, y. (Compressed points, 02 or 03, aren't
    // used by certificates or TLS 1.3.)
    internal bool TryImportPoint(Curve curve, ReadOnlySpan<byte> point)
    {
        int n = curve.Bytes;
        if (point.Length != 1 + 2 * n || point[0] != 4)
            return false;
        return TrySetKey(curve, point.Slice(1, n), point.Slice(1 + n, n));
    }

    bool TrySetKey(Curve curve, ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        uint[] q = new uint[2 * curve.P.Length];
        if (!curve.TryLoadPoint(x, y, q))
            return false;
        _curve = curve;
        _q = q;
        return true;
    }

    public bool VerifyData(byte[] data, byte[] signature, HashAlgorithmName hashAlgorithm)
        => VerifyData(data, signature, hashAlgorithm, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public bool VerifyData(byte[] data, byte[] signature, HashAlgorithmName hashAlgorithm, DSASignatureFormat signatureFormat)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (signature == null)
            throw new ArgumentNullException(nameof(signature));
        return VerifyData(new ReadOnlySpan<byte>(data), new ReadOnlySpan<byte>(signature), hashAlgorithm, signatureFormat);
    }

    public bool VerifyData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, HashAlgorithmName hashAlgorithm)
        => VerifyData(data, signature, hashAlgorithm, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>True if <paramref name="signature"/> is this key's signature of <paramref name="data"/>.</summary>
    public bool VerifyData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, HashAlgorithmName hashAlgorithm, DSASignatureFormat signatureFormat)
    {
        HashKind kind = hashAlgorithm.Kind;
        Span<byte> hash = stackalloc byte[64];
        hash = hash.Slice(0, HashState.SizeOf(kind));
        HashState.Hash(kind, data, hash);
        return VerifyHash(hash, signature, signatureFormat);
    }

    public bool VerifyHash(byte[] hash, byte[] signature)
        => VerifyHash(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public bool VerifyHash(byte[] hash, byte[] signature, DSASignatureFormat signatureFormat)
    {
        if (hash == null)
            throw new ArgumentNullException(nameof(hash));
        if (signature == null)
            throw new ArgumentNullException(nameof(signature));
        return VerifyHash(new ReadOnlySpan<byte>(hash), new ReadOnlySpan<byte>(signature), signatureFormat);
    }

    public bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature)
        => VerifyHash(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>True if <paramref name="signature"/> is this key's signature of a message with this hash.</summary>
    public bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, DSASignatureFormat signatureFormat)
    {
        if (_curve == null)
            throw new CryptographicException("ECDsa: no key has been imported.");
        int n = _curve.Bytes;
        Span<byte> rs = stackalloc byte[2 * n];
        if (signatureFormat == DSASignatureFormat.Rfc3279DerSequence)
        {
            if (!TryReadDerSignature(signature, rs, n))
                return false;
        }
        else
        {
            if (signature.Length != 2 * n)
                return false;
            signature.CopyTo(rs);
        }
        return _curve.Verify(_q, hash, rs.Slice(0, n), rs.Slice(n, n));
    }

    // SEQUENCE { INTEGER r, INTEGER s } into r and s, each n bytes.
    static bool TryReadDerSignature(ReadOnlySpan<byte> signature, Span<byte> rs, int n)
    {
        DerReader outer = new DerReader(signature);
        DerReader sequence = outer.ReadSequence();
        outer.End();
        ReadOnlySpan<byte> r = sequence.ReadUnsignedInteger();
        ReadOnlySpan<byte> s = sequence.ReadUnsignedInteger();
        sequence.End();
        if (outer.Failed || sequence.Failed || r.Length > n || s.Length > n)
            return false;
        rs.Clear();
        r.CopyTo(rs.Slice(n - r.Length));
        s.CopyTo(rs.Slice(2 * n - s.Length));
        return true;
    }
}

/// <summary>A short Weierstrass curve y² = x³ - 3x + b over a prime field, and ECDSA on it.</summary>
internal sealed class Curve
{
    internal readonly Modulus P;  // the field
    internal readonly Modulus N;  // the group order (prime; the cofactor is 1)
    internal readonly int Bytes;
    internal readonly int Bits;
    readonly uint[] _b;           // Montgomery form
    readonly uint[] _g;           // the base point, affine, Montgomery form

    static Curve s_p256, s_p384;

    internal static Curve P256 => s_p256 ??= new Curve(
        "ffffffff00000001000000000000000000000000ffffffffffffffffffffffff",
        "ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551",
        "5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b",
        "6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296",
        "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");

    internal static Curve P384 => s_p384 ??= new Curve(
        "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffeffffffff0000000000000000ffffffff",
        "ffffffffffffffffffffffffffffffffffffffffffffffffc7634d81f4372ddf581a0db248b0a77aecec196accc52973",
        "b3312fa7e23ee7e4988e056be3f82d19181d9c6efe8141120314088f5013875ac656398d8a2ed19d2a85c8edd3ec2aef",
        "aa87ca22be8b05378eb1c71ef320ad746e1d3b628ba79b9859f741e082542a385502f25dbf55296c3a545e3872760ab7",
        "3617de4a96262c6f5d9e98bf9292dc29f8f41dbd289a147ce9da3113b5f0b8c00a60b1ce1d7e819d7a431d7c90ea0e5f");

    Curve(string p, string n, string b, string gx, string gy)
    {
        P = Modulus.Create(Hex(p));
        N = Modulus.Create(Hex(n));
        Bytes = p.Length / 2;
        Bits = 8 * Bytes;
        int length = P.Length;
        _b = new uint[length];
        Modulus.FromBytes(Hex(b), _b);
        P.ToMontgomery(_b, _b);
        _g = new uint[2 * length];
        TryLoadPoint(Hex(gx), Hex(gy), _g);
    }

    static byte[] Hex(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(Digit(hex[2 * i]) * 16 + Digit(hex[2 * i + 1]));
        return bytes;
    }

    static int Digit(char c) => c <= '9' ? c - '0' : c - 'a' + 10;

    /// <summary>
    /// Loads an affine point into <paramref name="q"/> (x then y, Montgomery form);
    /// false unless both coordinates are below p and the point is on the curve.
    /// </summary>
    internal bool TryLoadPoint(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<uint> q)
    {
        int length = P.Length;
        Span<uint> qx = q.Slice(0, length), qy = q.Slice(length, length);
        if (!Modulus.FromBytes(x, qx) || !Modulus.FromBytes(y, qy) || !P.IsReduced(qx) || !P.IsReduced(qy))
            return false;
        P.ToMontgomery(qx, qx);
        P.ToMontgomery(qy, qy);

        // y² = x³ - 3x + b
        Span<uint> left = stackalloc uint[length], right = stackalloc uint[length], t = stackalloc uint[length];
        P.Multiply(left, qy, qy);
        P.Multiply(right, qx, qx);
        P.Multiply(right, right, qx);
        P.Add(t, qx, qx);
        P.Add(t, t, qx);
        P.Subtract(right, right, t);
        P.Add(right, right, _b);
        // (0, y) and points with x = 0 are fine; the point at infinity has no
        // affine form, so it can't get here.
        return Modulus.Compare(left, right) == 0;
    }

    /// <summary>ECDSA verification of a hash with the public point q; r and s big-endian, Bytes long.</summary>
    internal bool Verify(ReadOnlySpan<uint> q, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes)
    {
        int length = N.Length;
        Span<uint> r = stackalloc uint[length], s = stackalloc uint[length];
        if (!Modulus.FromBytes(rBytes, r) || !Modulus.FromBytes(sBytes, s))
            return false;
        if (Modulus.IsZero(r) || Modulus.IsZero(s) || !N.IsReduced(r) || !N.IsReduced(s))
            return false;

        // e: the hash's leftmost Bits bits (both curves' orders are a whole
        // number of bytes long), reduced mod n once (e < 2^Bits < 2n).
        Span<uint> e = stackalloc uint[length];
        Modulus.FromBytes(hash.Length > Bytes ? hash.Slice(0, Bytes) : hash, e);
        N.ReduceOnce(e);

        // w = s⁻¹, u1 = e·w, u2 = r·w (mod n)
        Span<uint> w = stackalloc uint[length], u1 = stackalloc uint[length], u2 = stackalloc uint[length];
        N.ToMontgomery(s, s);
        N.InvertPrime(w, s);
        N.ToMontgomery(e, e);
        N.Multiply(u1, e, w);
        N.FromMontgomery(u1, u1);
        N.ToMontgomery(r, r);
        N.Multiply(u2, r, w);
        N.FromMontgomery(u2, u2);
        N.FromMontgomery(r, r);

        // u1·G + u2·Q, one bit of each at a time from the top, with G, Q and
        // G + Q ready in Jacobian coordinates.
        int point = 3 * length;
        Span<uint> g = stackalloc uint[point], qj = stackalloc uint[point], gq = stackalloc uint[point];
        Span<uint> acc = stackalloc uint[point];
        ToJacobian(_g, g);
        ToJacobian(q, qj);
        Add(gq, g, qj);
        acc.Clear(); // Z = 0: the point at infinity
        for (int bit = Bits - 1; bit >= 0; bit--)
        {
            Double(acc, acc);
            int b1 = (int)(u1[bit / 32] >> (bit % 32)) & 1;
            int b2 = (int)(u2[bit / 32] >> (bit % 32)) & 1;
            if (b1 != 0 && b2 != 0)
                Add(acc, acc, gq);
            else if (b1 != 0)
                Add(acc, acc, g);
            else if (b2 != 0)
                Add(acc, acc, qj);
        }
        Span<uint> z = acc.Slice(2 * length, length);
        if (Modulus.IsZero(z))
            return false;

        // x = X/Z², out of Montgomery form, then mod n (x < p < 2n).
        Span<uint> zInverse = stackalloc uint[length], x = stackalloc uint[length];
        P.InvertPrime(zInverse, z);
        P.Multiply(zInverse, zInverse, zInverse);
        P.Multiply(x, acc.Slice(0, length), zInverse);
        P.FromMontgomery(x, x);
        N.ReduceOnce(x);
        return Modulus.Compare(x, r) == 0;
    }

    void ToJacobian(ReadOnlySpan<uint> affine, Span<uint> jacobian)
    {
        int length = P.Length;
        affine.Slice(0, 2 * length).CopyTo(jacobian);
        P.One.CopyTo(jacobian.Slice(2 * length, length));
    }

    // r = 2·a ("dbl-2001-b", for a = -3). r may be a.
    void Double(Span<uint> r, ReadOnlySpan<uint> a)
    {
        int length = P.Length;
        ReadOnlySpan<uint> x = a.Slice(0, length), y = a.Slice(length, length), z = a.Slice(2 * length, length);
        Span<uint> delta = stackalloc uint[length], gamma = stackalloc uint[length], beta = stackalloc uint[length];
        Span<uint> alpha = stackalloc uint[length], t1 = stackalloc uint[length], t2 = stackalloc uint[length];
        Span<uint> x3 = stackalloc uint[length], y3 = stackalloc uint[length], z3 = stackalloc uint[length];

        P.Multiply(delta, z, z);
        P.Multiply(gamma, y, y);
        P.Multiply(beta, x, gamma);
        // alpha = 3·(x - delta)·(x + delta)
        P.Subtract(t1, x, delta);
        P.Add(t2, x, delta);
        P.Multiply(alpha, t1, t2);
        P.Add(t1, alpha, alpha);
        P.Add(alpha, t1, alpha);
        // z3 = (y + z)² - gamma - delta
        P.Add(t1, y, z);
        P.Multiply(z3, t1, t1);
        P.Subtract(z3, z3, gamma);
        P.Subtract(z3, z3, delta);
        // x3 = alpha² - 8·beta
        P.Add(beta, beta, beta);
        P.Add(beta, beta, beta);   // 4·beta
        P.Multiply(x3, alpha, alpha);
        P.Add(t1, beta, beta);     // 8·beta
        P.Subtract(x3, x3, t1);
        // y3 = alpha·(4·beta - x3) - 8·gamma²
        P.Subtract(t1, beta, x3);
        P.Multiply(y3, alpha, t1);
        P.Multiply(gamma, gamma, gamma);
        P.Add(gamma, gamma, gamma);
        P.Add(gamma, gamma, gamma);
        P.Add(gamma, gamma, gamma);
        P.Subtract(y3, y3, gamma);

        x3.CopyTo(r);
        y3.CopyTo(r.Slice(length));
        z3.CopyTo(r.Slice(2 * length));
    }

    // r = a + b ("add-2007-bl"), with the cases it doesn't cover: either point
    // at infinity, a = b (double) and a = -b (infinity). r may be a or b.
    void Add(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        int length = P.Length;
        ReadOnlySpan<uint> x1 = a.Slice(0, length), y1 = a.Slice(length, length), z1 = a.Slice(2 * length, length);
        ReadOnlySpan<uint> x2 = b.Slice(0, length), y2 = b.Slice(length, length), z2 = b.Slice(2 * length, length);
        if (Modulus.IsZero(z1))
        {
            b.Slice(0, 3 * length).CopyTo(r);
            return;
        }
        if (Modulus.IsZero(z2))
        {
            a.Slice(0, 3 * length).CopyTo(r);
            return;
        }
        Span<uint> z1z1 = stackalloc uint[length], z2z2 = stackalloc uint[length];
        Span<uint> u1 = stackalloc uint[length], u2 = stackalloc uint[length];
        Span<uint> s1 = stackalloc uint[length], s2 = stackalloc uint[length];
        Span<uint> h = stackalloc uint[length], rr = stackalloc uint[length];
        Span<uint> i = stackalloc uint[length], j = stackalloc uint[length], v = stackalloc uint[length];
        Span<uint> x3 = stackalloc uint[length], y3 = stackalloc uint[length], z3 = stackalloc uint[length];

        P.Multiply(z1z1, z1, z1);
        P.Multiply(z2z2, z2, z2);
        P.Multiply(u1, x1, z2z2);
        P.Multiply(u2, x2, z1z1);
        P.Multiply(s1, y1, z2);
        P.Multiply(s1, s1, z2z2);
        P.Multiply(s2, y2, z1);
        P.Multiply(s2, s2, z1z1);
        P.Subtract(h, u2, u1);
        P.Subtract(rr, s2, s1);
        if (Modulus.IsZero(h))
        {
            if (Modulus.IsZero(rr))
                Double(r, a);
            else
                r.Slice(0, 3 * length).Clear();
            return;
        }
        // i = (2h)², j = h·i, rr = 2·(s2 - s1), v = u1·i
        P.Add(i, h, h);
        P.Multiply(i, i, i);
        P.Multiply(j, h, i);
        P.Add(rr, rr, rr);
        P.Multiply(v, u1, i);
        // x3 = rr² - j - 2v
        P.Multiply(x3, rr, rr);
        P.Subtract(x3, x3, j);
        P.Subtract(x3, x3, v);
        P.Subtract(x3, x3, v);
        // y3 = rr·(v - x3) - 2·s1·j
        P.Subtract(y3, v, x3);
        P.Multiply(y3, rr, y3);
        P.Multiply(s1, s1, j);
        P.Add(s1, s1, s1);
        P.Subtract(y3, y3, s1);
        // z3 = ((z1 + z2)² - z1z1 - z2z2)·h
        P.Add(z3, z1, z2);
        P.Multiply(z3, z3, z3);
        P.Subtract(z3, z3, z1z1);
        P.Subtract(z3, z3, z2z2);
        P.Multiply(z3, z3, h);

        x3.CopyTo(r);
        y3.CopyTo(r.Slice(length));
        z3.CopyTo(r.Slice(2 * length));
    }
}
