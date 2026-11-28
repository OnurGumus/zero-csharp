// Checking a certificate chain (RFC 5280 path validation), with .NET's
// X509Chain API:
//
//   using X509Chain chain = new X509Chain();
//   chain.ChainPolicy.ExtraStore.AddRange(intermediatesTheServerSent);
//   chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server
//   if (!chain.Build(serverCertificate))
//       foreach (X509ChainStatus s in chain.ChainStatus)
//           Console.WriteLine(s.StatusInformation);
//
// Build follows issuers from the certificate up: each next certificate is
// one whose subject is the issuer's name and whose key verifies the
// signature, from the trusted roots first, then ExtraStore. It stops at a
// trusted root (a certificate with the same name and key as the one in the
// trust store), and then checks each certificate below the root:
//
// - the verification time is between NotBefore and NotAfter;
// - each issuer is a CA (basic constraints) allowed to sign certificates
//   (key usage), and not more CAs below it than its path length allows;
// - with ApplicationPolicy set, each certificate's extended key usage, if
//   it has one, includes those purposes;
// - no extension marked critical that this doesn't understand.
//
// A root is trusted as it is: its own dates and extensions aren't checked,
// as RFC 5280 says (.NET on Windows would also report an expired root).
//
// Trust comes from ChainPolicy.CustomTrustStore when TrustMode is
// CustomRootTrust, else from the system's roots: the PEM bundle at
// SSL_CERT_FILE, or the distribution's (/etc/ssl/certs/ca-certificates.crt
// and the like), macOS's /etc/ssl/cert.pem, Android's
// /system/etc/security/cacerts. Windows, WebAssembly, DOS, UEFI and the
// microcontrollers have no such file here: use CustomRootTrust there.
//
// Not done: revocation (CRLs, OCSP). With RevocationMode left at Online, as
// .NET has it, Build reports RevocationStatusUnknown and refuses the chain
// (it doesn't throw); set NoCheck to go without. Also not done: name
// constraints (a CA certificate with them is refused, as
// HasNotSupportedNameConstraint), policy constraints, and signatures other
// than RSA PKCS #1 v1.5 and ECDSA P-256/P-384 with SHA-2 (SHA-1 isn't
// accepted, as browsers don't).

using System;
using System.Security.Cryptography;

namespace System.Security.Cryptography.X509Certificates;

public enum X509ChainTrustMode
{
    /// <summary>Trust the system's root certificates.</summary>
    System = 0,
    /// <summary>Trust only ChainPolicy.CustomTrustStore.</summary>
    CustomRootTrust = 1,
}

public enum X509RevocationMode
{
    NoCheck = 0,
    Online = 1,
    Offline = 2,
}

public enum X509RevocationFlag
{
    EndCertificateOnly = 0,
    EntireChain = 1,
    ExcludeRoot = 2,
}

/// <summary>Problems Build may overlook.</summary>
[Flags]
public enum X509VerificationFlags
{
    NoFlag = 0,
    IgnoreNotTimeValid = 0x1,
    IgnoreCtlNotTimeValid = 0x2,
    IgnoreNotTimeNested = 0x4,
    IgnoreInvalidBasicConstraints = 0x8,
    /// <summary>Accept a chain that ends at a root that isn't trusted, or doesn't reach a root.</summary>
    AllowUnknownCertificateAuthority = 0x10,
    IgnoreWrongUsage = 0x20,
    IgnoreInvalidName = 0x40,
    IgnoreInvalidPolicy = 0x80,
    IgnoreEndRevocationUnknown = 0x100,
    IgnoreCtlSignerRevocationUnknown = 0x200,
    IgnoreCertificateAuthorityRevocationUnknown = 0x400,
    IgnoreRootRevocationUnknown = 0x800,
    AllFlags = 0xFFF,
}

/// <summary>What is wrong with a certificate or chain.</summary>
[Flags]
public enum X509ChainStatusFlags
{
    NoError = 0,
    NotTimeValid = 0x1,
    NotTimeNested = 0x2,
    Revoked = 0x4,
    NotSignatureValid = 0x8,
    NotValidForUsage = 0x10,
    UntrustedRoot = 0x20,
    RevocationStatusUnknown = 0x40,
    Cyclic = 0x80,
    InvalidExtension = 0x100,
    InvalidPolicyConstraints = 0x200,
    InvalidBasicConstraints = 0x400,
    InvalidNameConstraints = 0x800,
    HasNotSupportedNameConstraint = 0x1000,
    HasNotDefinedNameConstraint = 0x2000,
    HasNotPermittedNameConstraint = 0x4000,
    HasExcludedNameConstraint = 0x8000,
    PartialChain = 0x10000,
    CtlNotTimeValid = 0x20000,
    CtlNotSignatureValid = 0x40000,
    CtlNotValidForUsage = 0x80000,
    HasWeakSignature = 0x100000,
    OfflineRevocation = 0x1000000,
    NoIssuanceChainPolicy = 0x2000000,
    ExplicitDistrust = 0x4000000,
    HasNotSupportedCriticalExtension = 0x8000000,
}

public struct X509ChainStatus
{
    public X509ChainStatusFlags Status { get; set; }
    public string StatusInformation { get; set; }
}

/// <summary>What X509Chain.Build checks against.</summary>
public sealed class X509ChainPolicy
{
    CalendarTime _verificationTime;
    bool _timeSet;

    /// <summary>Certificates Build may use on the way up: the intermediates a server sent.</summary>
    public X509Certificate2Collection ExtraStore { get; private set; } = new X509Certificate2Collection();

    /// <summary>The roots trusted when TrustMode is CustomRootTrust.</summary>
    public X509Certificate2Collection CustomTrustStore { get; private set; } = new X509Certificate2Collection();

    public X509ChainTrustMode TrustMode { get; set; }

    /// <summary>Purposes each certificate must allow, such as 1.3.6.1.5.5.7.3.1 for a TLS server.</summary>
    public OidCollection ApplicationPolicy { get; private set; } = new OidCollection();

    /// <summary>
    /// Revocation isn't checked here. Online (the default, as in .NET) and Offline make
    /// Build report RevocationStatusUnknown and refuse the chain; set NoCheck, or the
    /// Ignore...RevocationUnknown verification flags, to go without.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.Online;

    public X509RevocationFlag RevocationFlag { get; set; } = X509RevocationFlag.ExcludeRoot;

    public X509VerificationFlags VerificationFlags { get; set; }

    /// <summary>The time to check the dates against, in UTC; the current time unless set.</summary>
    public CalendarTime VerificationTime
    {
        get => _timeSet ? _verificationTime : Clock.UtcNow;
        set
        {
            _verificationTime = value;
            _timeSet = true;
        }
    }

    /// <summary>When true (the default), Build uses the time it runs at, not VerificationTime.</summary>
    public bool VerificationTimeIgnored { get; set; } = true;

    internal long VerificationSeconds
        => (_timeSet && !VerificationTimeIgnored ? _verificationTime.ToUnixMilliseconds() : Clock.UnixMilliseconds) / 1000;

    public void Reset()
    {
        ExtraStore = new X509Certificate2Collection();
        CustomTrustStore = new X509Certificate2Collection();
        ApplicationPolicy = new OidCollection();
        TrustMode = X509ChainTrustMode.System;
        RevocationMode = X509RevocationMode.Online;
        RevocationFlag = X509RevocationFlag.ExcludeRoot;
        VerificationFlags = X509VerificationFlags.NoFlag;
        VerificationTimeIgnored = true;
        _timeSet = false;
    }

    public X509ChainPolicy Clone()
    {
        X509ChainPolicy copy = new X509ChainPolicy();
        copy.ExtraStore.AddRange(ExtraStore);
        copy.CustomTrustStore.AddRange(CustomTrustStore);
        for (int i = 0; i < ApplicationPolicy.Count; i++)
            copy.ApplicationPolicy.Add(ApplicationPolicy[i]);
        copy.TrustMode = TrustMode;
        copy.RevocationMode = RevocationMode;
        copy.RevocationFlag = RevocationFlag;
        copy.VerificationFlags = VerificationFlags;
        copy.VerificationTimeIgnored = VerificationTimeIgnored;
        copy._verificationTime = _verificationTime;
        copy._timeSet = _timeSet;
        return copy;
    }
}

/// <summary>A certificate in a built chain, and what is wrong with it.</summary>
public sealed class X509ChainElement
{
    internal X509ChainElement(X509Certificate2 certificate, X509ChainStatus[] status)
    {
        Certificate = certificate;
        ChainElementStatus = status;
    }

    public X509Certificate2 Certificate { get; }
    public X509ChainStatus[] ChainElementStatus { get; }
    public string Information => "";
}

/// <summary>The certificates of a built chain, the one Build was given first and the root last.</summary>
public sealed class X509ChainElementCollection
{
    readonly X509ChainElement[] _elements;

    internal X509ChainElementCollection(X509ChainElement[] elements) { _elements = elements; }

    public int Count => _elements.Length;

    public X509ChainElement this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_elements.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _elements[index];
        }
    }

    public Enumerator GetEnumerator() => new Enumerator(_elements);

    public struct Enumerator
    {
        readonly X509ChainElement[] _elements;
        int _index;

        internal Enumerator(X509ChainElement[] elements)
        {
            _elements = elements;
            _index = -1;
        }

        public X509ChainElement Current => _elements[_index];

        public bool MoveNext() => ++_index < _elements.Length;
    }
}

/// <summary>Builds and checks a certificate's chain up to a trusted root.</summary>
public class X509Chain : IDisposable
{
    const int MaxDepth = 10;

    X509ChainPolicy _policy = new X509ChainPolicy();
    X509ChainElementCollection _elements = new X509ChainElementCollection(new X509ChainElement[0]);
    X509ChainStatus[] _status = new X509ChainStatus[0];

    public X509ChainPolicy ChainPolicy
    {
        get => _policy;
        set => _policy = value ?? throw new ArgumentNullException(nameof(value));
    }

    public X509ChainElementCollection ChainElements => _elements;

    /// <summary>Every problem Build found, in any certificate of the chain.</summary>
    public X509ChainStatus[] ChainStatus => _status;

    public static X509Chain Create() => new X509Chain();

    /// <summary>
    /// Builds the chain from <paramref name="certificate"/> to a trusted root and checks it.
    /// True if nothing is wrong, apart from what VerificationFlags overlooks; ChainElements
    /// and ChainStatus say what was found either way.
    /// </summary>
    public bool Build(X509Certificate2 certificate)
    {
        if (certificate == null)
            throw new ArgumentNullException(nameof(certificate));
        X509Certificate2Collection anchors = _policy.TrustMode == X509ChainTrustMode.CustomRootTrust
            ? _policy.CustomTrustStore
            : SystemRoots.Get();
        return Run(certificate, anchors, _policy.ExtraStore);
    }

    /// <summary>Forgets the last Build's results; the policy stays.</summary>
    public void Reset()
    {
        _elements = new X509ChainElementCollection(new X509ChainElement[0]);
        _status = new X509ChainStatus[0];
    }

    public void Dispose() { }

    bool Run(X509Certificate2 leaf, X509Certificate2Collection anchors, X509Certificate2Collection extra)
    {
        X509Certificate2[] path = new X509Certificate2[MaxDepth + 1];
        X509ChainStatusFlags[] flags = new X509ChainStatusFlags[MaxDepth + 1];
        int count = 0;
        path[count++] = leaf;
        bool trusted = false;
        X509Certificate2 current = leaf;
        while (true)
        {
            if (FindSame(anchors, current) != null)
            {
                trusted = true; // the certificate is itself a trusted root
                break;
            }
            X509Certificate2 issuer = FindIssuer(anchors, current, path, count, out bool nameMatched);
            if (issuer != null)
            {
                path[count++] = issuer;
                trusted = true;
                break;
            }
            // A self-signed certificate that isn't trusted ends the path.
            if (current.IsSelfIssued && current.Signed(current))
            {
                flags[count - 1] |= X509ChainStatusFlags.UntrustedRoot;
                break;
            }
            issuer = FindIssuer(extra, current, path, count, out bool extraNameMatched);
            nameMatched |= extraNameMatched;
            if (issuer != null && count < MaxDepth)
            {
                if (Contains(path, count, issuer))
                {
                    flags[count - 1] |= X509ChainStatusFlags.Cyclic;
                    break;
                }
                path[count++] = issuer;
                current = issuer;
                continue;
            }
            // Nowhere to go: a gap.
            if (nameMatched)
                flags[count - 1] |= X509ChainStatusFlags.NotSignatureValid;
            flags[count - 1] |= X509ChainStatusFlags.PartialChain;
            break;
        }

        // Check everything below the trusted root (all of it, if it isn't trusted).
        int checkedCount = trusted ? Math.Max(1, count - 1) : count;
        if (trusted && count == 1)
            checkedCount = 0; // the certificate is a trusted root itself
        long now = _policy.VerificationSeconds;
        int casBelow = 0; // CA certificates between this one and the leaf, not counting self-issued ones
        for (int i = 0; i < checkedCount; i++)
        {
            X509Certificate2 c = path[i];
            if (now < c.NotBeforeSeconds || now > c.NotAfterSeconds)
                flags[i] |= X509ChainStatusFlags.NotTimeValid;
            if (c.HasUnsupportedCriticalExtension)
                flags[i] |= X509ChainStatusFlags.HasNotSupportedCriticalExtension;
            if (c.HasNameConstraints)
                flags[i] |= X509ChainStatusFlags.HasNotSupportedNameConstraint;
            if (!AllowsPurposes(c))
                flags[i] |= X509ChainStatusFlags.NotValidForUsage;
        }
        // Every issuer, the trusted root included, must be a CA that may sign certificates.
        for (int i = 1; i < count; i++)
        {
            X509Certificate2 c = path[i];
            bool isRoot = trusted && i == count - 1;
            if (!isRoot)
            {
                if (!c.HasBasicConstraints || !c.IsCertificateAuthority)
                    flags[i] |= X509ChainStatusFlags.InvalidBasicConstraints;
                else if (c.PathLengthConstraint >= 0 && casBelow > c.PathLengthConstraint)
                    flags[i] |= X509ChainStatusFlags.InvalidBasicConstraints;
                if (c.KeyUsageFlags >= 0 && (c.KeyUsageFlags & KeyCertSign) == 0)
                    flags[i] |= X509ChainStatusFlags.NotValidForUsage;
            }
            else
            {
                // A root's own constraints still bound the CAs under it.
                if (c.HasBasicConstraints && c.PathLengthConstraint >= 0 && casBelow > c.PathLengthConstraint)
                    flags[i] |= X509ChainStatusFlags.InvalidBasicConstraints;
            }
            if (!c.IsSelfIssued)
                casBelow++;
        }

        // Revocation isn't checked here (no CRLs, no OCSP). Asked for, it is
        // reported as unknown, as .NET reports it when offline, so the chain is
        // refused rather than passed unchecked.
        if (_policy.RevocationMode != X509RevocationMode.NoCheck)
        {
            for (int i = 0; i < count; i++)
            {
                bool isRoot = i == count - 1 && i > 0 && (trusted || path[i].IsSelfIssued);
                if (_policy.RevocationFlag == X509RevocationFlag.EndCertificateOnly && i > 0)
                    break;
                if (_policy.RevocationFlag == X509RevocationFlag.ExcludeRoot && isRoot)
                    break;
                flags[i] |= X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
            }
        }

        X509ChainElement[] elements = new X509ChainElement[count];
        X509ChainStatusFlags all = X509ChainStatusFlags.NoError;
        bool ok = true;
        X509VerificationFlags verification = _policy.VerificationFlags;
        for (int i = 0; i < count; i++)
        {
            elements[i] = new X509ChainElement(path[i], ToStatus(flags[i]));
            all |= flags[i];
            X509ChainStatusFlags overlooked = Overlooked(verification);
            bool isRoot = i == count - 1 && i > 0 && (trusted || path[i].IsSelfIssued);
            X509VerificationFlags ignoreRevocation = i == 0 ? X509VerificationFlags.IgnoreEndRevocationUnknown
                : isRoot ? X509VerificationFlags.IgnoreRootRevocationUnknown
                : X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown;
            if ((verification & ignoreRevocation) != 0)
                overlooked |= X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
            if ((flags[i] & ~overlooked) != 0)
                ok = false;
        }
        _elements = new X509ChainElementCollection(elements);
        _status = ToStatus(all);
        return ok;
    }

    const int KeyCertSign = 1 << 5;

    bool AllowsPurposes(X509Certificate2 c)
    {
        OidCollection wanted = _policy.ApplicationPolicy;
        if (wanted.Count == 0 || !c.HasExtendedKeyUsage || c.ExtendedKeyUsageAny)
            return true;
        for (int i = 0; i < wanted.Count; i++)
        {
            string value = wanted[i].Value;
            bool has = value == "1.3.6.1.5.5.7.3.1" ? c.ExtendedKeyUsageServer
                : value == "1.3.6.1.5.5.7.3.2" ? c.ExtendedKeyUsageClient
                : false;
            if (!has)
                return false;
        }
        return true;
    }

    static X509ChainStatusFlags Overlooked(X509VerificationFlags flags)
    {
        X509ChainStatusFlags overlooked = X509ChainStatusFlags.NoError;
        if ((flags & X509VerificationFlags.IgnoreNotTimeValid) != 0)
            overlooked |= X509ChainStatusFlags.NotTimeValid;
        if ((flags & X509VerificationFlags.IgnoreInvalidBasicConstraints) != 0)
            overlooked |= X509ChainStatusFlags.InvalidBasicConstraints;
        if ((flags & X509VerificationFlags.AllowUnknownCertificateAuthority) != 0)
            overlooked |= X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
        if ((flags & X509VerificationFlags.IgnoreWrongUsage) != 0)
            overlooked |= X509ChainStatusFlags.NotValidForUsage;
        return overlooked;
    }

    // The trust store's copy of this certificate: same subject and same key.
    static X509Certificate2 FindSame(X509Certificate2Collection store, X509Certificate2 c)
    {
        for (int i = 0; i < store.Count; i++)
        {
            X509Certificate2 s = store[i];
            if (DerReader.Same(s.SubjectEncoded, c.SubjectEncoded) && DerReader.Same(s.SubjectPublicKeyInfo, c.SubjectPublicKeyInfo))
                return s;
        }
        return null;
    }

    // A certificate in the store named as c's issuer whose key verifies c's signature.
    static X509Certificate2 FindIssuer(X509Certificate2Collection store, X509Certificate2 c,
        X509Certificate2[] path, int count, out bool nameMatched)
    {
        nameMatched = false;
        X509Certificate2 cyclic = null;
        for (int i = 0; i < store.Count; i++)
        {
            X509Certificate2 candidate = store[i];
            if (!DerReader.Same(candidate.SubjectEncoded, c.IssuerEncoded))
                continue;
            nameMatched = true;
            if (!candidate.Signed(c))
                continue;
            // Prefer one that isn't already in the path.
            if (Contains(path, count, candidate))
            {
                cyclic ??= candidate;
                continue;
            }
            return candidate;
        }
        return cyclic;
    }

    static bool Contains(X509Certificate2[] path, int count, X509Certificate2 c)
    {
        for (int i = 0; i < count; i++)
            if (path[i] == c || DerReader.Same(path[i].Tbs, c.Tbs))
                return true;
        return false;
    }

    static X509ChainStatus[] ToStatus(X509ChainStatusFlags flags)
    {
        int n = 0;
        for (int bit = 0; bit < 32; bit++)
            if (((int)flags & (1 << bit)) != 0)
                n++;
        X509ChainStatus[] status = new X509ChainStatus[n];
        int at = 0;
        for (int bit = 0; bit < 32; bit++)
        {
            X509ChainStatusFlags flag = (X509ChainStatusFlags)(1 << bit);
            if ((flags & flag) != 0)
                status[at++] = new X509ChainStatus { Status = flag, StatusInformation = Describe(flag) };
        }
        return status;
    }

    static string Describe(X509ChainStatusFlags flag) => flag switch
    {
        X509ChainStatusFlags.NotTimeValid => "A certificate has expired or isn't valid yet.",
        X509ChainStatusFlags.NotSignatureValid => "A certificate's signature doesn't verify, or uses an algorithm this doesn't support.",
        X509ChainStatusFlags.NotValidForUsage => "A certificate isn't allowed for this purpose.",
        X509ChainStatusFlags.UntrustedRoot => "The chain ends at a root certificate that isn't trusted.",
        X509ChainStatusFlags.Cyclic => "The chain goes in a circle.",
        X509ChainStatusFlags.InvalidBasicConstraints => "An issuer isn't a certificate authority, or is too far from the certificate.",
        X509ChainStatusFlags.HasNotSupportedNameConstraint => "An issuer has name constraints, which aren't checked here.",
        X509ChainStatusFlags.PartialChain => "The chain doesn't reach a root certificate.",
        X509ChainStatusFlags.RevocationStatusUnknown => "Whether a certificate was revoked wasn't checked: there is no revocation checking here (set RevocationMode to NoCheck).",
        X509ChainStatusFlags.OfflineRevocation => "Revocation wasn't checked: no revocation server was asked.",
        X509ChainStatusFlags.HasNotSupportedCriticalExtension => "A certificate has a critical extension this doesn't understand.",
        _ => flag.ToString(),
    };
}

/// <summary>The system's trusted root certificates, read once from its PEM bundle.</summary>
internal static class SystemRoots
{
    static X509Certificate2Collection s_roots;

    static readonly string[] s_files =
    {
        "/etc/ssl/certs/ca-certificates.crt",                // Debian, Ubuntu, Alpine, Arch
        "/etc/pki/tls/certs/ca-bundle.crt",                  // Fedora, RHEL
        "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", // RHEL
        "/etc/ssl/ca-bundle.pem",                            // openSUSE
        "/etc/ssl/cert.pem",                                 // macOS, Alpine, BSDs
    };

    internal static X509Certificate2Collection Get()
    {
        if (s_roots != null)
            return s_roots;
        X509Certificate2Collection roots = new X509Certificate2Collection();
        string fromEnvironment = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        if (!string.IsNullOrEmpty(fromEnvironment))
            AddFile(roots, fromEnvironment);
        for (int i = 0; i < s_files.Length && roots.Count == 0; i++)
            AddFile(roots, s_files[i]);
        if (roots.Count == 0)
            AddDirectory(roots, "/system/etc/security/cacerts"); // Android: one PEM file per root
        s_roots = roots;
        return roots;
    }

    static void AddFile(X509Certificate2Collection roots, string path)
    {
        byte[] data = Pem.ReadFile(path);
        if (data == null)
            return;
        int at = 0;
        while (true)
        {
            byte[] der = Pem.Next(data.AsSpan(at), out int consumed);
            if (der == null)
                return;
            at += consumed;
            // A root this can't read is left out rather than ending the program.
            if (X509Certificate2.TryLoad(der, out X509Certificate2 root))
                roots.Add(root);
        }
    }

    static void AddDirectory(X509Certificate2Collection roots, string path)
    {
        if (!global::Directory.Exists(path))
            return;
        foreach (DirectoryEntry entry in global::Directory.Enumerate(path))
            if (!entry.IsDirectory)
                AddFile(roots, string.Concat(path, "/", DerReader.Chars(entry.Name)));
    }
}
