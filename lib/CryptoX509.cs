// X.509 certificates (RFC 5280), with .NET's X509Certificate2 API:
//
//   X509Certificate2 cert = X509Certificate2.CreateFromPem(pemText);
//   Console.WriteLine(cert.Subject);                // "CN=example.com, O=Example, C=US"
//   bool forHost = cert.MatchesHostname("www.example.com");
//
// A certificate is a public key, the name of whoever holds it (the subject),
// the dates it is good between, and a signature by an issuer vouching for
// all of that. X509Chain (lib/CryptoX509Chain.cs) follows the signatures up
// to a root you trust.
//
// What a certificate says is read when it is loaded: names, dates, the key
// and the extensions a chain check needs (basic constraints, key usage,
// extended key usage, subject alternative names). A malformed certificate
// throws CryptographicException from the public API; X509Chain and TLS use
// internal Try methods that don't throw, since what a server sends mustn't
// end the program.
//
// Differences from .NET: NotBefore and NotAfter are CalendarTime in UTC,
// not local DateTime; there is no Thumbprint (it is SHA-1, which lib/ doesn't
// have) but GetCertHash(HashAlgorithmName.SHA256) is; no private keys; no
// Extensions collection.

using System;
using System.Net;
using System.Security.Cryptography;

namespace System.Security.Cryptography.X509Certificates;

public enum X509NameType
{
    /// <summary>The common name (CN), or the most specific name there is.</summary>
    SimpleName = 0,
    /// <summary>The e-mail address.</summary>
    EmailName = 1,
    UpnName = 2,
    /// <summary>The first DNS name of the subject alternative names, else the common name.</summary>
    DnsName = 3,
    DnsFromAlternativeName = 4,
    UrlName = 5,
}

/// <summary>An X.509 certificate.</summary>
public sealed class X509Certificate2 : IDisposable
{
    readonly byte[] _raw;

    // Where things are in _raw.
    int _tbsStart, _tbsLength;
    int _signatureStart, _signatureLength;
    int _serialStart, _serialLength;
    int _issuerStart, _issuerLength;
    int _subjectStart, _subjectLength;
    int _spkiStart, _spkiLength;
    int _sanStart, _sanLength; // the GeneralNames SEQUENCE's contents, if present

    internal int VersionNumber;
    internal SignatureKind SignatureAlgorithmKind;
    internal long NotBeforeSeconds, NotAfterSeconds; // Unix time
    internal bool HasBasicConstraints, IsCertificateAuthority;
    internal int PathLengthConstraint = -1;           // -1: none
    internal int KeyUsageFlags = -1;                  // -1: no key usage extension
    internal bool HasExtendedKeyUsage, ExtendedKeyUsageAny, ExtendedKeyUsageServer, ExtendedKeyUsageClient;
    internal bool HasUnsupportedCriticalExtension;
    internal bool HasNameConstraints;

    RSA _rsa;
    ECDsa _ecdsa;
    bool _keyLoaded;
    string _subject, _issuer;

    X509Certificate2(byte[] raw, bool parsed) { _raw = raw; }

    /// <summary>Loads a DER-encoded certificate. Throws CryptographicException if it isn't one.</summary>
    public X509Certificate2(byte[] rawData)
    {
        if (rawData == null)
            throw new ArgumentNullException(nameof(rawData));
        _raw = Outer(rawData);
        if (_raw == null || !ParseRaw())
            throw new CryptographicException("X509Certificate2: not a DER-encoded X.509 certificate this can read.");
    }

    // The certificate's SEQUENCE, copied, if that is all there is.
    static byte[] Outer(ReadOnlySpan<byte> der)
    {
        DerReader outer = new DerReader(der);
        outer.Read(DerReader.Sequence, out ReadOnlySpan<byte> whole);
        outer.End();
        return outer.Failed ? null : whole.ToArray();
    }

    /// <summary>Loads the first certificate in PEM text ("-----BEGIN CERTIFICATE-----").</summary>
    public static X509Certificate2 CreateFromPem(ReadOnlySpan<char> certPem)
    {
        int at = 0;
        byte[] der = Pem.Next(certPem, ref at);
        if (der == null)
            throw new CryptographicException("X509Certificate2.CreateFromPem: no CERTIFICATE in the text.");
        if (!TryLoad(der, out X509Certificate2 certificate))
            throw new CryptographicException("X509Certificate2.CreateFromPem: the CERTIFICATE isn't one this can read.");
        return certificate;
    }

    /// <summary>
    /// Parses a DER certificate (the bytes are copied); false if it is malformed or uses
    /// something this doesn't read. Never throws: it reads what a server sends.
    /// </summary>
    internal static bool TryLoad(ReadOnlySpan<byte> der, out X509Certificate2 certificate)
    {
        certificate = null;
        byte[] raw = Outer(der);
        if (raw == null)
            return false;
        X509Certificate2 c = new X509Certificate2(raw, true);
        if (!c.ParseRaw())
            return false;
        certificate = c;
        return true;
    }

    bool ParseRaw()
    {
        ReadOnlySpan<byte> raw = _raw;
        DerReader outer = new DerReader(raw);
        DerReader cert = outer.ReadSequence();
        ReadOnlySpan<byte> tbsContents = cert.Read(DerReader.Sequence, out ReadOnlySpan<byte> tbs);
        cert.Read(DerReader.Sequence, out ReadOnlySpan<byte> signatureAlgorithm);
        ReadOnlySpan<byte> signature = cert.ReadBitStringBytes();
        cert.End();
        if (cert.Failed)
            return false;
        Locate(tbs, out _tbsStart, out _tbsLength);
        Locate(signature, out _signatureStart, out _signatureLength);

        DerReader t = new DerReader(tbsContents);
        VersionNumber = 1;
        if (t.TryRead(DerReader.Context(0), out ReadOnlySpan<byte> versionField))
        {
            DerReader v = new DerReader(versionField);
            VersionNumber = v.ReadSmallInteger() + 1;
            v.End();
            if (v.Failed || VersionNumber > 3)
                return false;
        }
        ReadOnlySpan<byte> serial = t.Read(DerReader.Integer);
        t.Read(DerReader.Sequence, out ReadOnlySpan<byte> innerAlgorithm);
        t.Read(DerReader.Sequence, out ReadOnlySpan<byte> issuer);
        DerReader validity = t.ReadSequence();
        bool timesOk = ReadTime(ref validity, out NotBeforeSeconds) & ReadTime(ref validity, out NotAfterSeconds);
        validity.End();
        t.Read(DerReader.Sequence, out ReadOnlySpan<byte> subject);
        t.Read(DerReader.Sequence, out ReadOnlySpan<byte> spki);
        t.TryRead(DerReader.ContextPrimitive(1), out _); // issuerUniqueID
        t.TryRead(DerReader.ContextPrimitive(2), out _); // subjectUniqueID
        bool extensionsOk = true;
        if (t.TryRead(DerReader.Context(3), out ReadOnlySpan<byte> extensions))
            extensionsOk = VersionNumber == 3 && ParseExtensions(extensions);
        t.End();
        if (t.Failed || validity.Failed || !timesOk || !extensionsOk || serial.IsEmpty || serial.Length > 32)
            return false;
        // The algorithm outside the signed part must be the one inside it.
        if (!DerReader.Same(innerAlgorithm, signatureAlgorithm))
            return false;
        SignatureAlgorithmKind = ReadSignatureAlgorithm(signatureAlgorithm);

        Locate(serial, out _serialStart, out _serialLength);
        Locate(issuer, out _issuerStart, out _issuerLength);
        Locate(subject, out _subjectStart, out _subjectLength);
        Locate(spki, out _spkiStart, out _spkiLength);
        return true;
    }

    // Where a span that points into _raw starts.
    void Locate(ReadOnlySpan<byte> part, out int start, out int length)
    {
        unsafe
        {
            // SAFETY: part was sliced from _raw by DerReader, so both pointers are into
            // the same array and the difference is its offset there.
            fixed (byte* whole = _raw)
            fixed (byte* p = part)
                start = part.IsEmpty ? 0 : (int)(p - whole);
        }
        length = part.Length;
    }

    static SignatureKind ReadSignatureAlgorithm(ReadOnlySpan<byte> encoded)
    {
        DerReader outer = new DerReader(encoded);
        DerReader algorithm = outer.ReadSequence();
        ReadOnlySpan<byte> oid = algorithm.ReadOid();
        bool rsa = false;
        // RSA's parameters are NULL (or, by mistake, left out); ECDSA's are absent.
        if (algorithm.PeekTag() == DerReader.Null)
        {
            algorithm.Read(DerReader.Null);
            rsa = true;
        }
        algorithm.End();
        if (algorithm.Failed)
            return SignatureKind.Unknown;
        if (DerReader.Same(oid, Oids.Sha256WithRsa)) return SignatureKind.RsaSha256;
        if (DerReader.Same(oid, Oids.Sha384WithRsa)) return SignatureKind.RsaSha384;
        if (DerReader.Same(oid, Oids.Sha512WithRsa)) return SignatureKind.RsaSha512;
        if (rsa)
            return SignatureKind.Unknown;
        if (DerReader.Same(oid, Oids.EcdsaSha256)) return SignatureKind.EcdsaSha256;
        if (DerReader.Same(oid, Oids.EcdsaSha384)) return SignatureKind.EcdsaSha384;
        if (DerReader.Same(oid, Oids.EcdsaSha512)) return SignatureKind.EcdsaSha512;
        return SignatureKind.Unknown;
    }

    // Extensions ::= SEQUENCE OF SEQUENCE { extnID, critical BOOLEAN DEFAULT FALSE, extnValue OCTET STRING }
    bool ParseExtensions(ReadOnlySpan<byte> field)
    {
        DerReader outer = new DerReader(field);
        DerReader list = outer.ReadSequence();
        outer.End();
        int seen = 0; // each known extension may appear once
        bool any = false;
        while (list.HasData)
        {
            any = true;
            DerReader extension = list.ReadSequence();
            ReadOnlySpan<byte> oid = extension.ReadOid();
            bool critical = false;
            if (extension.PeekTag() == DerReader.Boolean)
                critical = extension.ReadBoolean();
            ReadOnlySpan<byte> value = extension.Read(DerReader.OctetString);
            extension.End();
            if (extension.Failed)
                return false;
            if (oid.Length == 3 && oid[0] == 0x55 && oid[1] == 0x1D) // 2.5.29.x
            {
                int which = oid[2];
                if (which < 32 && which != 14 && which != 35) // key identifiers may not be unique-checked
                {
                    if ((seen & (1 << which)) != 0)
                        return false;
                    seen |= 1 << which;
                }
                switch (which)
                {
                    case 19: if (!ParseBasicConstraints(value)) return false; continue;
                    case 15: if (!ParseKeyUsage(value)) return false; continue;
                    case 37: if (!ParseExtendedKeyUsage(value)) return false; continue;
                    case 17: if (!ParseSubjectAlternativeName(value)) return false; continue;
                    case 30: HasNameConstraints = true; break; // not checked: see X509Chain
                }
            }
            if (critical)
                HasUnsupportedCriticalExtension = true;
        }
        return any && !list.Failed && !outer.Failed;
    }

    bool ParseBasicConstraints(ReadOnlySpan<byte> value)
    {
        DerReader outer = new DerReader(value);
        DerReader sequence = outer.ReadSequence();
        outer.End();
        HasBasicConstraints = true;
        if (sequence.PeekTag() == DerReader.Boolean)
            IsCertificateAuthority = sequence.ReadBoolean();
        if (sequence.PeekTag() == DerReader.Integer)
            PathLengthConstraint = sequence.ReadSmallInteger();
        sequence.End();
        return !sequence.Failed && !outer.Failed;
    }

    bool ParseKeyUsage(ReadOnlySpan<byte> value)
    {
        DerReader reader = new DerReader(value);
        KeyUsageFlags = reader.ReadFlags();
        reader.End();
        return !reader.Failed;
    }

    bool ParseExtendedKeyUsage(ReadOnlySpan<byte> value)
    {
        DerReader outer = new DerReader(value);
        DerReader list = outer.ReadSequence();
        outer.End();
        HasExtendedKeyUsage = true;
        while (list.HasData)
        {
            ReadOnlySpan<byte> oid = list.ReadOid();
            if (DerReader.Same(oid, ServerAuthOid)) ExtendedKeyUsageServer = true;
            else if (DerReader.Same(oid, ClientAuthOid)) ExtendedKeyUsageClient = true;
            else if (DerReader.Same(oid, AnyExtendedKeyUsageOid)) ExtendedKeyUsageAny = true;
        }
        return !list.Failed && !outer.Failed;
    }

    bool ParseSubjectAlternativeName(ReadOnlySpan<byte> value)
    {
        DerReader outer = new DerReader(value);
        ReadOnlySpan<byte> names = outer.Read(DerReader.Sequence);
        outer.End();
        if (outer.Failed || names.IsEmpty)
            return false;
        // Check that every GeneralName is well formed now, so reading them later can't fail.
        DerReader list = new DerReader(names);
        while (list.HasData)
            list.ReadAny(out _, out _);
        if (list.Failed)
            return false;
        Locate(names, out _sanStart, out _sanLength);
        return true;
    }

    // 1.3.6.1.5.5.7.3.1 / .2, 2.5.29.37.0
    internal static ReadOnlySpan<byte> ServerAuthOid => [0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01];
    internal static ReadOnlySpan<byte> ClientAuthOid => [0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x02];
    static ReadOnlySpan<byte> AnyExtendedKeyUsageOid => [0x55, 0x1d, 0x25, 0x00];

    // UTCTime YYMMDDHHMMSSZ (years 1950 to 2049) or GeneralizedTime YYYYMMDDHHMMSSZ.
    static bool ReadTime(ref DerReader reader, out long unixSeconds)
    {
        unixSeconds = 0;
        int tag = reader.PeekTag();
        if (tag != DerReader.UtcTime && tag != DerReader.GeneralizedTime)
        {
            reader.Fail();
            return false;
        }
        ReadOnlySpan<byte> text = reader.Read(tag);
        int yearDigits = tag == DerReader.UtcTime ? 2 : 4;
        if (text.Length != yearDigits + 11 || text[text.Length - 1] != (byte)'Z')
            return false;
        for (int i = 0; i < text.Length - 1; i++)
            if (text[i] < '0' || text[i] > '9')
                return false;
        int at = 0;
        int year = Digits(text, ref at, yearDigits);
        if (yearDigits == 2)
            year += year < 50 ? 2000 : 1900;
        int month = Digits(text, ref at, 2), day = Digits(text, ref at, 2);
        int hour = Digits(text, ref at, 2), minute = Digits(text, ref at, 2), second = Digits(text, ref at, 2);
        if (month < 1 || month > 12 || day < 1 || day > DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return false;
        unixSeconds = DaysFromCivil(year, month, day) * 86400L + hour * 3600 + minute * 60 + second;
        return true;
    }

    static int Digits(ReadOnlySpan<byte> text, ref int at, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
            value = value * 10 + (text[at++] - '0');
        return value;
    }

    static int DaysInMonth(int year, int month)
    {
        if (month == 2)
            return (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)) ? 29 : 28;
        return month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;
    }

    // Days since 1970-01-01 (Howard Hinnant's days_from_civil).
    internal static long DaysFromCivil(int year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        long era = (year >= 0 ? year : year - 399) / 400;
        long yearOfEra = year - era * 400;
        long dayOfYear = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        long dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146097 + dayOfEra - 719468;
    }

    internal ReadOnlySpan<byte> Tbs => _raw.AsSpan(_tbsStart, _tbsLength);
    internal ReadOnlySpan<byte> SignatureBytes => _raw.AsSpan(_signatureStart, _signatureLength);
    internal ReadOnlySpan<byte> IssuerEncoded => _raw.AsSpan(_issuerStart, _issuerLength);
    internal ReadOnlySpan<byte> SubjectEncoded => _raw.AsSpan(_subjectStart, _subjectLength);
    internal ReadOnlySpan<byte> SubjectPublicKeyInfo => _raw.AsSpan(_spkiStart, _spkiLength);
    internal bool IsSelfIssued => DerReader.Same(IssuerEncoded, SubjectEncoded);

    /// <summary>The certificate's DER encoding.</summary>
    public byte[] RawData => _raw.AsSpan().ToArray();

    /// <summary>The X.509 version: 1, 2 or 3 (almost always 3).</summary>
    public int Version => VersionNumber;

    /// <summary>Who the certificate belongs to, such as "CN=example.com, O=Example, C=US".</summary>
    public string Subject => _subject ??= DistinguishedName.Format(SubjectEncoded);

    /// <summary>Who signed the certificate, in the same form as Subject.</summary>
    public string Issuer => _issuer ??= DistinguishedName.Format(IssuerEncoded);

    /// <summary>When the certificate starts being good, in UTC (.NET gives local time).</summary>
    public CalendarTime NotBefore => CalendarTime.FromUnixMilliseconds(NotBeforeSeconds * 1000);

    /// <summary>When the certificate stops being good, in UTC (.NET gives local time).</summary>
    public CalendarTime NotAfter => CalendarTime.FromUnixMilliseconds(NotAfterSeconds * 1000);

    /// <summary>The serial number in hexadecimal, big-endian, as .NET shows it.</summary>
    public string SerialNumber => Convert.ToHexString(_raw.AsSpan(_serialStart, _serialLength));

    /// <summary>The algorithm the issuer signed with, such as 1.2.840.10045.4.3.2 (ECDSA with SHA-256).</summary>
    public Oid SignatureAlgorithm
    {
        get
        {
            DerReader outer = new DerReader(_raw.AsSpan(_tbsStart + _tbsLength));
            DerReader algorithm = outer.ReadSequence();
            string value = DerReader.OidToString(algorithm.ReadOid());
            string name = SignatureAlgorithmKind switch
            {
                SignatureKind.RsaSha256 => "sha256RSA",
                SignatureKind.RsaSha384 => "sha384RSA",
                SignatureKind.RsaSha512 => "sha512RSA",
                SignatureKind.EcdsaSha256 => "sha256ECDSA",
                SignatureKind.EcdsaSha384 => "sha384ECDSA",
                SignatureKind.EcdsaSha512 => "sha512ECDSA",
                _ => null,
            };
            return new Oid(value, name);
        }
    }

    /// <summary>Always false: this X509Certificate2 holds no private keys.</summary>
    public bool HasPrivateKey => false;

    /// <summary>The certificate's hash, such as GetCertHash(HashAlgorithmName.SHA256): a fingerprint.</summary>
    public byte[] GetCertHash(HashAlgorithmName hashAlgorithm)
    {
        HashKind kind = hashAlgorithm.Kind;
        byte[] hash = new byte[HashState.SizeOf(kind)];
        HashState.Hash(kind, _raw, hash);
        return hash;
    }

    /// <summary>GetCertHash as uppercase hexadecimal.</summary>
    public string GetCertHashString(HashAlgorithmName hashAlgorithm) => Convert.ToHexString(GetCertHash(hashAlgorithm));

    /// <summary>The RSA public key, or null if the key isn't RSA.</summary>
    public RSA GetRSAPublicKey()
    {
        LoadKey();
        return _rsa;
    }

    /// <summary>The ECDSA public key, or null if the key isn't a P-256 or P-384 one.</summary>
    public ECDsa GetECDsaPublicKey()
    {
        LoadKey();
        return _ecdsa;
    }

    void LoadKey()
    {
        if (_keyLoaded)
            return;
        _keyLoaded = true;
        RSA rsa = RSA.Create();
        if (rsa.TryImportSubjectPublicKeyInfo(SubjectPublicKeyInfo, out _))
        {
            _rsa = rsa;
            return;
        }
        ECDsa ecdsa = ECDsa.Create();
        if (ecdsa.TryImportSubjectPublicKeyInfo(SubjectPublicKeyInfo, out _))
            _ecdsa = ecdsa;
    }

    /// <summary>True if this certificate's key signed <paramref name="child"/>.</summary>
    internal bool Signed(X509Certificate2 child)
    {
        LoadKey();
        ReadOnlySpan<byte> tbs = child.Tbs, signature = child.SignatureBytes;
        switch (child.SignatureAlgorithmKind)
        {
            case SignatureKind.RsaSha256: return _rsa != null && _rsa.VerifyData(tbs, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            case SignatureKind.RsaSha384: return _rsa != null && _rsa.VerifyData(tbs, signature, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
            case SignatureKind.RsaSha512: return _rsa != null && _rsa.VerifyData(tbs, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
            case SignatureKind.EcdsaSha256: return _ecdsa != null && _ecdsa.VerifyData(tbs, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            case SignatureKind.EcdsaSha384: return _ecdsa != null && _ecdsa.VerifyData(tbs, signature, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence);
            case SignatureKind.EcdsaSha512: return _ecdsa != null && _ecdsa.VerifyData(tbs, signature, HashAlgorithmName.SHA512, DSASignatureFormat.Rfc3279DerSequence);
            default: return false;
        }
    }

    /// <summary>
    /// A name from the certificate: SimpleName is the common name (or the most specific
    /// name there is), DnsName the first DNS name among the subject alternative names
    /// (or the common name), EmailName the e-mail address. Null or "" if there is none.
    /// </summary>
    public string GetNameInfo(X509NameType nameType, bool forIssuer)
    {
        ReadOnlySpan<byte> name = forIssuer ? IssuerEncoded : SubjectEncoded;
        switch (nameType)
        {
            case X509NameType.SimpleName:
                return DistinguishedName.Find(name, DistinguishedName.CommonName)
                    ?? DistinguishedName.Find(name, DistinguishedName.OrganizationalUnit)
                    ?? DistinguishedName.Find(name, DistinguishedName.Organization)
                    ?? DistinguishedName.Find(name, DistinguishedName.Email)
                    ?? "";
            case X509NameType.EmailName:
                return DistinguishedName.Find(name, DistinguishedName.Email) ?? (forIssuer ? "" : FirstAlternativeName(1)) ?? "";
            case X509NameType.DnsName:
            case X509NameType.DnsFromAlternativeName:
                string dns = forIssuer ? null : FirstAlternativeName(2);
                if (dns != null || nameType == X509NameType.DnsFromAlternativeName)
                    return dns ?? "";
                return DistinguishedName.Find(name, DistinguishedName.CommonName) ?? "";
            case X509NameType.UrlName:
                return (forIssuer ? null : FirstAlternativeName(6)) ?? "";
            default:
                return "";
        }
    }

    // The first subject alternative name of this kind: 1 e-mail, 2 DNS, 6 URI.
    string FirstAlternativeName(int kind)
    {
        DerReader list = new DerReader(_raw.AsSpan(_sanStart, _sanLength));
        while (list.HasData)
        {
            ReadOnlySpan<byte> value = list.ReadAny(out int tag, out _);
            if (tag == DerReader.ContextPrimitive(kind))
                return Latin1(value);
        }
        return null;
    }

    static string Latin1(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = bytes.Length <= 256 ? stackalloc char[bytes.Length] : new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = (char)bytes[i];
        return DerReader.Chars(chars);
    }

    /// <summary>
    /// True if the certificate is for this host name or IP address. It is checked against
    /// the subject alternative names, and against the common name only if there are none
    /// (and <paramref name="allowCommonName"/>). A wildcard "*.example.com" stands for one
    /// whole label: it matches "www.example.com", not "example.com" or "a.b.example.com".
    /// Host names compare without regard to ASCII case, and a final dot is ignored.
    /// </summary>
    public bool MatchesHostname(string hostname, bool allowWildcards = true, bool allowCommonName = true)
    {
        if (hostname == null)
            throw new ArgumentNullException(nameof(hostname));
        ReadOnlySpan<char> host = hostname;
        if (host.Length > 0 && host[host.Length - 1] == '.')
            host = host.Slice(0, host.Length - 1);
        if (host.IsEmpty)
            return false;

        // An IP address matches only an iPAddress entry, byte for byte.
        bool isAddress = IPAddress.TryParse(host, out IPAddress address);
        byte[] addressBytes = isAddress ? address.GetAddressBytes() : null;

        bool anyNames = false;
        DerReader list = new DerReader(_raw.AsSpan(_sanStart, _sanLength));
        while (list.HasData)
        {
            ReadOnlySpan<byte> value = list.ReadAny(out int tag, out _);
            if (tag == DerReader.ContextPrimitive(2))
            {
                anyNames = true;
                if (!isAddress && HostMatches(value, host, allowWildcards))
                    return true;
            }
            else if (tag == DerReader.ContextPrimitive(7))
            {
                anyNames = true;
                if (isAddress && DerReader.Same(value, addressBytes))
                    return true;
            }
        }
        if (anyNames || !allowCommonName || isAddress)
            return false;
        string commonName = DistinguishedName.Find(SubjectEncoded, DistinguishedName.CommonName);
        if (commonName == null)
            return false;
        Span<byte> name = commonName.Length <= 256 ? stackalloc byte[commonName.Length] : new byte[commonName.Length];
        for (int i = 0; i < commonName.Length; i++)
        {
            if (commonName[i] > 0x7F)
                return false;
            name[i] = (byte)commonName[i];
        }
        return HostMatches(name, host, allowWildcards);
    }

    // A DNS name from the certificate (ASCII) against the host name.
    static bool HostMatches(ReadOnlySpan<byte> pattern, ReadOnlySpan<char> host, bool allowWildcards)
    {
        if (pattern.Length > 0 && pattern[pattern.Length - 1] == '.')
            pattern = pattern.Slice(0, pattern.Length - 1);
        if (pattern.IsEmpty)
            return false;
        if (pattern.Length >= 2 && pattern[0] == '*' && pattern[1] == '.')
        {
            if (!allowWildcards)
                return false;
            // "*." then at least two labels: no "*.com".
            ReadOnlySpan<byte> rest = pattern.Slice(2);
            if (IndexOf(rest, '.') < 0 || IndexOf(rest, '*') >= 0)
                return false;
            int dot = host.IndexOf('.');
            if (dot <= 0)
                return false;
            return SameIgnoringCase(rest, host.Slice(dot + 1));
        }
        if (IndexOf(pattern, '*') >= 0)
            return false; // partial wildcards ("w*.example.com") aren't honoured
        return SameIgnoringCase(pattern, host);
    }

    static int IndexOf(ReadOnlySpan<byte> text, char c)
    {
        for (int i = 0; i < text.Length; i++)
            if (text[i] == c)
                return i;
        return -1;
    }

    static bool SameIgnoringCase(ReadOnlySpan<byte> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            int x = a[i], y = b[i];
            if (x >= 'A' && x <= 'Z') x += 32;
            if (y >= 'A' && y <= 'Z') y += 32;
            if (x != y)
                return false;
        }
        return true;
    }

    public void Dispose() { }

    public override string ToString() => Subject;
}

internal enum SignatureKind
{
    Unknown,
    RsaSha256,
    RsaSha384,
    RsaSha512,
    EcdsaSha256,
    EcdsaSha384,
    EcdsaSha512,
}

/// <summary>.NET 9's way to load a certificate: X509CertificateLoader.LoadCertificate(bytes).</summary>
public static class X509CertificateLoader
{
    /// <summary>Loads a certificate in DER, or the first one in PEM text.</summary>
    public static X509Certificate2 LoadCertificate(byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        return LoadCertificate(new ReadOnlySpan<byte>(data));
    }

    /// <summary>Loads a certificate in DER, or the first one in PEM text.</summary>
    public static X509Certificate2 LoadCertificate(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty && data[0] == (byte)'-')
        {
            byte[] der = Pem.Next(data, out _);
            if (der != null && X509Certificate2.TryLoad(der, out X509Certificate2 fromPem))
                return fromPem;
        }
        else if (X509Certificate2.TryLoad(data, out X509Certificate2 certificate))
            return certificate;
        throw new CryptographicException("X509CertificateLoader: not an X.509 certificate this can read.");
    }

    /// <summary>Loads a certificate from a file, in DER or PEM.</summary>
    public static X509Certificate2 LoadCertificateFromFile(string path)
    {
        byte[] data = Pem.ReadFile(path);
        if (data == null)
            throw new CryptographicException(string.Concat("X509CertificateLoader: can't read ", path));
        return LoadCertificate(data);
    }
}

/// <summary>A list of certificates, such as the ones a server sent or the roots you trust.</summary>
public class X509Certificate2Collection
{
    X509Certificate2[] _items = new X509Certificate2[4];
    int _count;

    public X509Certificate2Collection() { }

    public X509Certificate2Collection(X509Certificate2 certificate) { Add(certificate); }

    public int Count => _count;

    public X509Certificate2 this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[index];
        }
    }

    public int Add(X509Certificate2 certificate)
    {
        if (certificate == null)
            throw new ArgumentNullException(nameof(certificate));
        if (_count == _items.Length)
        {
            X509Certificate2[] bigger = new X509Certificate2[_items.Length * 2];
            _items.AsSpan().CopyTo(bigger);
            _items = bigger;
        }
        _items[_count] = certificate;
        return _count++;
    }

    public void AddRange(X509Certificate2Collection certificates)
    {
        for (int i = 0; i < certificates.Count; i++)
            Add(certificates[i]);
    }

    /// <summary>True if a certificate with the same bytes is in the list.</summary>
    public bool Contains(X509Certificate2 certificate)
    {
        for (int i = 0; i < _count; i++)
            if (_items[i] == certificate || DerReader.Same(_items[i].Tbs, certificate.Tbs))
                return true;
        return false;
    }

    public void Clear()
    {
        _items.AsSpan(0, _count).Clear();
        _count = 0;
    }

    /// <summary>Adds every CERTIFICATE in the PEM text. Throws if one is malformed.</summary>
    public void ImportFromPem(ReadOnlySpan<char> certPem)
    {
        if (!TryImportFromPem(certPem))
            throw new CryptographicException("X509Certificate2Collection.ImportFromPem: a CERTIFICATE isn't one this can read.");
    }

    /// <summary>Adds every CERTIFICATE in the PEM file.</summary>
    public void ImportFromPemFile(string certPemFilePath)
    {
        byte[] data = Pem.ReadFile(certPemFilePath);
        if (data == null)
            throw new CryptographicException(string.Concat("X509Certificate2Collection.ImportFromPemFile: can't read ", certPemFilePath));
        int at = 0;
        while (true)
        {
            byte[] der = Pem.Next(data.AsSpan(at), out int consumed);
            if (der == null)
                break;
            at += consumed;
            if (!X509Certificate2.TryLoad(der, out X509Certificate2 certificate))
                throw new CryptographicException("X509Certificate2Collection.ImportFromPemFile: a CERTIFICATE isn't one this can read.");
            Add(certificate);
        }
    }

    /// <summary>Adds a DER certificate.</summary>
    public void Import(byte[] rawData) => Add(new X509Certificate2(rawData));

    internal bool TryImportFromPem(ReadOnlySpan<char> certPem)
    {
        int at = 0;
        bool ok = true;
        while (true)
        {
            byte[] der = Pem.Next(certPem, ref at);
            if (der == null)
                return ok;
            if (X509Certificate2.TryLoad(der, out X509Certificate2 certificate))
                Add(certificate);
            else
                ok = false;
        }
    }

    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        readonly X509Certificate2Collection _collection;
        int _index;

        internal Enumerator(X509Certificate2Collection collection)
        {
            _collection = collection;
            _index = -1;
        }

        public X509Certificate2 Current => _collection._items[_index];

        public bool MoveNext() => ++_index < _collection._count;
    }
}

/// <summary>PEM: base64 between "-----BEGIN CERTIFICATE-----" and "-----END CERTIFICATE-----".</summary>
internal static class Pem
{
    const string Begin = "-----BEGIN CERTIFICATE-----";
    const string End = "-----END CERTIFICATE-----";

    /// <summary>The next CERTIFICATE's bytes from <paramref name="at"/> on, or null if there are no more.</summary>
    internal static byte[] Next(ReadOnlySpan<char> text, ref int at)
    {
        while (true)
        {
            int begin = text.Slice(at).IndexOf(Begin.AsSpan());
            if (begin < 0)
            {
                at = text.Length;
                return null;
            }
            int start = at + begin + Begin.Length;
            int end = text.Slice(start).IndexOf(End.AsSpan());
            if (end < 0)
            {
                at = text.Length;
                return null;
            }
            at = start + end + End.Length;
            byte[] der = Decode(text.Slice(start, end));
            if (der != null)
                return der;
        }
    }

    /// <summary>The same, for PEM text as bytes (a file): <paramref name="consumed"/> is how far it read.</summary>
    internal static byte[] Next(ReadOnlySpan<byte> text, out int consumed)
    {
        consumed = 0;
        while (true)
        {
            int begin = IndexOf(text.Slice(consumed), Begin);
            if (begin < 0)
            {
                consumed = text.Length;
                return null;
            }
            int start = consumed + begin + Begin.Length;
            int end = IndexOf(text.Slice(start), End);
            if (end < 0)
            {
                consumed = text.Length;
                return null;
            }
            consumed = start + end + End.Length;
            ReadOnlySpan<byte> body = text.Slice(start, end);
            char[] chars = new char[body.Length];
            for (int i = 0; i < body.Length; i++)
                chars[i] = (char)body[i];
            byte[] der = Decode(chars);
            if (der != null)
                return der;
        }
    }

    static int IndexOf(ReadOnlySpan<byte> text, string marker)
    {
        for (int i = 0; i + marker.Length <= text.Length; i++)
        {
            int j = 0;
            while (j < marker.Length && text[i + j] == marker[j])
                j++;
            if (j == marker.Length)
                return i;
        }
        return -1;
    }

    // Base64 with line breaks and spaces; null if it isn't base64.
    static byte[] Decode(ReadOnlySpan<char> body)
    {
        char[] compact = new char[body.Length];
        int n = 0;
        foreach (char c in body)
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                compact[n++] = c;
        byte[] bytes = new byte[n / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(compact.AsSpan(0, n), bytes, out int written) || written == 0)
            return null;
        return bytes.AsSpan(0, written).ToArray();
    }

    /// <summary>A whole file's bytes, or null if it can't be read.</summary>
    internal static byte[] ReadFile(string path)
    {
        if (path == null || !global::File.Exists(path))
            return null;
        using System.IO.FileStream stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read);
        long length = stream.Length;
        if (length > int.MaxValue)
            return null;
        byte[] data = new byte[(int)length];
        int at = 0;
        while (at < data.Length)
        {
            int n = stream.Read(data, at, data.Length - at);
            if (n <= 0)
                break;
            at += n;
        }
        return at == data.Length ? data : data.AsSpan(0, at).ToArray();
    }
}

/// <summary>Distinguished names (subject and issuer): formatting and finding attributes.</summary>
internal static class DistinguishedName
{
    // 2.5.4.3, 2.5.4.10, 2.5.4.11, 1.2.840.113549.1.9.1
    internal static ReadOnlySpan<byte> CommonName => [0x55, 0x04, 0x03];
    internal static ReadOnlySpan<byte> Organization => [0x55, 0x04, 0x0a];
    internal static ReadOnlySpan<byte> OrganizationalUnit => [0x55, 0x04, 0x0b];
    internal static ReadOnlySpan<byte> Email => [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x09, 0x01];
    static ReadOnlySpan<byte> DomainComponent => [0x09, 0x92, 0x26, 0x89, 0x93, 0xf2, 0x2c, 0x64, 0x01, 0x19];

    /// <summary>The value of the last attribute of this type (the most specific), or null.</summary>
    internal static string Find(ReadOnlySpan<byte> name, ReadOnlySpan<byte> type)
    {
        string found = null;
        DerReader outer = new DerReader(name);
        DerReader rdns = outer.ReadSequence();
        while (rdns.HasData)
        {
            DerReader set = new DerReader(rdns.Read(DerReader.Set));
            while (set.HasData)
            {
                DerReader attribute = set.ReadSequence();
                ReadOnlySpan<byte> oid = attribute.ReadOid();
                ReadOnlySpan<byte> value = attribute.ReadAny(out int tag, out _);
                if (!attribute.Failed && DerReader.Same(oid, type))
                    found = DecodeString(tag, value);
            }
        }
        return found;
    }

    /// <summary>.NET's format: the most specific part first, "CN=a, O=b, C=US".</summary>
    internal static string Format(ReadOnlySpan<byte> name)
    {
        // Collect the RDNs, then write them last to first.
        DerReader outer = new DerReader(name);
        DerReader rdns = outer.ReadSequence();
        int count = 0;
        DerReader counter = rdns;
        while (counter.HasData)
        {
            counter.Read(DerReader.Set);
            count++;
        }
        string[] parts = new string[count];
        for (int i = 0; i < count; i++)
        {
            DerReader set = new DerReader(rdns.Read(DerReader.Set));
            string part = null;
            while (set.HasData)
            {
                DerReader attribute = set.ReadSequence();
                ReadOnlySpan<byte> oid = attribute.ReadOid();
                ReadOnlySpan<byte> value = attribute.ReadAny(out int tag, out _);
                string text = string.Concat(Label(oid), "=", Quote(DecodeString(tag, value)));
                part = part == null ? text : string.Concat(part, " + ", text);
            }
            parts[i] = part ?? "";
        }
        string result = "";
        for (int i = count - 1; i >= 0; i--)
            result = i == count - 1 ? parts[i] : string.Concat(result, ", ", parts[i]);
        return result;
    }

    static string Label(ReadOnlySpan<byte> oid)
    {
        if (oid.Length == 3 && oid[0] == 0x55 && oid[1] == 0x04)
        {
            switch (oid[2])
            {
                case 3: return "CN";
                case 4: return "SN";
                case 5: return "SERIALNUMBER";
                case 6: return "C";
                case 7: return "L";
                case 8: return "S";
                case 9: return "STREET";
                case 10: return "O";
                case 11: return "OU";
                case 12: return "T";
                case 42: return "G";
                case 43: return "I";
            }
        }
        if (DerReader.Same(oid, Email))
            return "E";
        if (DerReader.Same(oid, DomainComponent))
            return "DC";
        return string.Concat("OID.", DerReader.OidToString(oid) ?? "?");
    }

    // Quotes a value that has characters with a meaning in the format.
    static string Quote(string value)
    {
        bool needs = value.Length > 0 && (value[0] == ' ' || value[value.Length - 1] == ' ');
        foreach (char c in value)
            if (c == ',' || c == '+' || c == '=' || c == '"' || c == '\n' || c == '<' || c == '>' || c == '#' || c == ';')
                needs = true;
        if (!needs)
            return value;
        System.Text.StringBuilder quoted = new System.Text.StringBuilder(value.Length + 4);
        quoted.Append('"');
        foreach (char c in value)
        {
            if (c == '"')
                quoted.Append('"');
            quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>A directory string's text: UTF-8, BMP (UTF-16), Universal (UTF-32) or one byte per character.</summary>
    internal static string DecodeString(int tag, ReadOnlySpan<byte> value)
    {
        switch (tag)
        {
            case DerReader.Utf8String:
                return System.Text.Encoding.UTF8.GetString(value);
            case DerReader.BmpString:
            {
                char[] chars = new char[value.Length / 2];
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = (char)(value[2 * i] << 8 | value[2 * i + 1]);
                return DerReader.Chars(chars);
            }
            case DerReader.UniversalString:
            {
                char[] chars = new char[value.Length / 2];
                int n = 0;
                for (int i = 0; i + 4 <= value.Length; i += 4)
                {
                    int c = value[i] << 24 | value[i + 1] << 16 | value[i + 2] << 8 | value[i + 3];
                    if (c >= 0x10000 && c <= 0x10FFFF)
                    {
                        c -= 0x10000;
                        chars[n++] = (char)(0xD800 + (c >> 10));
                        chars[n++] = (char)(0xDC00 + (c & 0x3FF));
                    }
                    else
                        chars[n++] = c <= 0xFFFF ? (char)c : '�';
                }
                return DerReader.Chars(chars.AsSpan(0, n));
            }
            default:
            {
                // PrintableString, IA5String, TeletexString (taken as Latin-1) and the like.
                char[] chars = new char[value.Length];
                for (int i = 0; i < value.Length; i++)
                    chars[i] = (char)value[i];
                return DerReader.Chars(chars);
            }
        }
    }
}
