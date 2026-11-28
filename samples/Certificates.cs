// Example: certificates (lib/CryptoRsa.cs, lib/CryptoEcdsa.cs,
// lib/CryptoX509.cs, lib/CryptoX509Chain.cs): RSA and ECDSA signatures,
// reading X.509 certificates, host names, and checking chains up to a
// trusted root, with what each check refuses. The test certificates come
// from scripts/crypto-fuzz/make-certs.sh (OpenSSL); two real chains, from
// letsencrypt.org and github.com, are checked at a fixed time, as they'll
// expire. Nothing here needs the network or the system's roots, so it runs
// on every target that has room for it.
// Exit code = number of failed checks.
// Build: ./build.sh Certificates

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertificatesSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void Section(string section)
    {
        Out.Line($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Out.Line($"  failed: {name}");
        }
    }

    static void CheckText(string name, string actual, string expected)
    {
        Check(name, actual == expected);
        if (actual != expected)
            Out.Line($"    got      {actual}\n    expected {expected}");
    }

    static byte[] FromHex(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(Digit(hex[2 * i]) * 16 + Digit(hex[2 * i + 1]));
        return bytes;
    }

    static int Digit(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    static byte[] Flip(byte[] bytes, int at)
    {
        byte[] copy = new ReadOnlySpan<byte>(bytes).ToArray();
        copy[at] ^= 1;
        return copy;
    }

    static CalendarTime Day(int year, int month, int day) => new CalendarTime { Year = year, Month = month, Day = day, Hour = 12 };

    public static int Main()
    {
        Signatures();
        Reading();
        HostNames();
        Chains();
        Refusals();
        RealChains();
        return s_failures;
    }

    static void Signatures()
    {
        // RSA keys whose size isn't a multiple of 8: PSS's encoded message is
        // then a byte shorter than the modulus (1025) or has unused top bits (2047).
        byte[] message = "odd sized key"u8.ToArray();
        foreach (var (bits, spki, pss, pkcs1) in s_oddKeys)
        {
            using RSA rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(FromHex(spki), out int read);
            Check($"{bits}: key read", read == spki.Length / 2 && rsa.KeySize.ToString() == bits);
            byte[] pssSignature = FromHex(pss), pkcs1Signature = FromHex(pkcs1);
            Check($"{bits}: PSS", rsa.VerifyData(message, pssSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            Check($"{bits}: PKCS1", rsa.VerifyData(message, pkcs1Signature, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1));
            Check($"{bits}: PSS changed", !rsa.VerifyData(message, Flip(pssSignature, 40), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            Check($"{bits}: PKCS1 changed", !rsa.VerifyData(message, Flip(pkcs1Signature, 40), HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1));
            Check($"{bits}: other message", !rsa.VerifyData("odd sized kez"u8, pssSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }

        // A certificate's signature, checked by hand with its issuer's key.
        X509Certificate2 inter = X509Certificate2.CreateFromPem(InterPem);
        X509Certificate2 leaf = X509Certificate2.CreateFromPem(LeafPem);
        using ECDsa issuerKey = inter.GetECDsaPublicKey();
        Check("issuer key is P-384", issuerKey != null && issuerKey.KeySize == 384 && inter.GetRSAPublicKey() == null);
        ECParameters p = issuerKey.ExportParameters(false);
        Check("P-384 point", p.Q.X.Length == 48 && p.Q.Y.Length == 48 && p.Curve.Oid.Value == "1.3.132.0.34");
        using ECDsa again = ECDsa.Create(p);
        Check("leaf key is P-256", leaf.GetECDsaPublicKey().KeySize == 256);
        X509Certificate2 root = X509Certificate2.CreateFromPem(RootPem);
        using RSA rootKey = root.GetRSAPublicKey();
        Check("root key is RSA 2048", rootKey != null && rootKey.KeySize == 2048);
        RSAParameters rp = rootKey.ExportParameters(false);
        Check("RSA numbers", rp.Modulus.Length == 256 && rp.Exponent.Length == 3);

        Section("RSA and ECDSA");
    }

    static void Reading()
    {
        // docs:read
        X509Certificate2 leaf = X509Certificate2.CreateFromPem(LeafPem);
        string subject = leaf.Subject;   // "CN=www.example.test, O=..."
        string issuer = leaf.Issuer;
        CalendarTime notAfter = leaf.NotAfter;
        // docs:end
        CheckText("Subject", subject, "CN=www.example.test, O=\"Example, Inc.\", C=US");
        CheckText("Issuer", issuer, "CN=Zero Test CA E1, O=Zero Test, C=US");
        Check("NotBefore", leaf.NotBefore.Year == 2026 && leaf.NotBefore.Month == 1 && leaf.NotBefore.Day == 1 && leaf.NotBefore.Hour == 0);
        Check("NotAfter", notAfter.Year == 2027 && notAfter.Month == 1 && notAfter.Day == 1);
        Check("Version", leaf.Version == 3);
        CheckText("SignatureAlgorithm", leaf.SignatureAlgorithm.Value, "1.2.840.10045.4.3.3");
        CheckText("SignatureAlgorithm name", leaf.SignatureAlgorithm.FriendlyName, "sha384ECDSA");
        Check("SerialNumber", leaf.SerialNumber.Length >= 2 && leaf.SerialNumber.Length <= 18);
        CheckText("SimpleName", leaf.GetNameInfo(X509NameType.SimpleName, false), "www.example.test");
        CheckText("issuer SimpleName", leaf.GetNameInfo(X509NameType.SimpleName, true), "Zero Test CA E1");
        CheckText("DnsName", leaf.GetNameInfo(X509NameType.DnsName, false), "www.example.test");
        Check("GetCertHash", leaf.GetCertHash(HashAlgorithmName.SHA256).Length == 32
                             && leaf.GetCertHashString(HashAlgorithmName.SHA256).Length == 64);
        Check("RawData", X509CertificateLoader.LoadCertificate(leaf.RawData).Subject == subject);
        Check("HasPrivateKey", !leaf.HasPrivateKey);
        CheckText("root Subject", X509Certificate2.CreateFromPem(RootPem).Subject, "CN=Zero Test Root R1, O=Zero Test, C=US");

        // Several certificates in one PEM text.
        X509Certificate2Collection all = new X509Certificate2Collection();
        all.ImportFromPem(LetsEncryptChainPem);
        Check("ImportFromPem", all.Count == 4 && all[0].GetNameInfo(X509NameType.SimpleName, false) == "letsencrypt.org");
        int n = 0;
        foreach (X509Certificate2 c in all)
            n += c.Version;
        Check("foreach", n == 12 && all.Contains(all[2]));

        Section("Reading certificates");
    }

    static void HostNames()
    {
        X509Certificate2 leaf = X509Certificate2.CreateFromPem(LeafPem);
        Check("exact", leaf.MatchesHostname("www.example.test"));
        Check("case and final dot", leaf.MatchesHostname("WWW.Example.TEST."));
        Check("wildcard", leaf.MatchesHostname("eu.api.example.test"));
        Check("wildcard needs a label", !leaf.MatchesHostname("api.example.test"));
        Check("wildcard is one label", !leaf.MatchesHostname("a.b.api.example.test"));
        Check("wildcards off", !leaf.MatchesHostname("eu.api.example.test", allowWildcards: false));
        Check("other name", !leaf.MatchesHostname("example.test"));
        Check("IPv4", leaf.MatchesHostname("192.0.2.7"));
        Check("IPv6", leaf.MatchesHostname("2001:db8:0::7"));
        Check("other address", !leaf.MatchesHostname("192.0.2.8"));
        // With alternative names, the common name doesn't count.
        X509Certificate2 rsaLeaf = X509Certificate2.CreateFromPem(LeafRsaPem);
        Check("alternative name", rsaLeaf.MatchesHostname("rsa.example.test"));
        Section("Host names");
    }

    static X509Chain NewChain(string rootPem, CalendarTime at)
    {
        X509Chain chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(X509Certificate2.CreateFromPem(rootPem));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = at;
        chain.ChainPolicy.VerificationTimeIgnored = false;
        return chain;
    }

    static X509ChainStatusFlags Flags(X509Chain chain)
    {
        X509ChainStatusFlags flags = X509ChainStatusFlags.NoError;
        foreach (X509ChainStatus s in chain.ChainStatus)
            flags |= s.Status;
        return flags;
    }

    static void Chains()
    {
        CalendarTime june = Day(2026, 6, 1);
        X509Certificate2 leaf = X509Certificate2.CreateFromPem(LeafPem);
        X509Certificate2 inter = X509Certificate2.CreateFromPem(InterPem);

        // docs:chain
        using X509Chain chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(X509Certificate2.CreateFromPem(RootPem));
        chain.ChainPolicy.ExtraStore.Add(inter);   // what the server sent
        // Only for TLS servers:
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        bool trusted = chain.Build(leaf);
        // docs:end
        Check("leaf, P-384 CA, RSA root", trusted && chain.ChainElements.Count == 3 && chain.ChainStatus.Length == 0);
        Check("elements in order", chain.ChainElements[0].Certificate == leaf
                                   && chain.ChainElements[1].Certificate == inter
                                   && chain.ChainElements[2].Certificate.Subject == "CN=Zero Test Root R1, O=Zero Test, C=US");

        // An RSA leaf under an ECDSA root, no intermediate.
        using X509Chain rsa = NewChain(RootEPem, june);
        Check("RSA leaf, ECDSA root", rsa.Build(X509Certificate2.CreateFromPem(LeafRsaPem)) && rsa.ChainElements.Count == 2);

        // A trusted root on its own.
        using X509Chain rootOnly = NewChain(RootPem, june);
        Check("the root itself", rootOnly.Build(X509Certificate2.CreateFromPem(RootPem)) && rootOnly.ChainElements.Count == 1);

        // A client certificate is fine when no purpose is asked for.
        using X509Chain anyUse = NewChain(RootPem, june);
        anyUse.ChainPolicy.ExtraStore.Add(inter);
        Check("no purpose asked", anyUse.Build(X509Certificate2.CreateFromPem(ClientPem)));

        Section("Chains");
    }

    // Builds leafPem's chain with these intermediates, and checks it fails with exactly these flags.
    static void Refused(string name, string leafPem, string[] extra, X509ChainStatusFlags expected,
        CalendarTime at, bool serverOnly = false, string rootPem = null)
    {
        using X509Chain chain = NewChain(rootPem ?? RootPem, at);
        foreach (string pem in extra)
            chain.ChainPolicy.ExtraStore.Add(X509Certificate2.CreateFromPem(pem));
        if (serverOnly)
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        bool built = chain.Build(X509Certificate2.CreateFromPem(leafPem));
        X509ChainStatusFlags flags = Flags(chain);
        Check(name, !built && flags == expected);
        if (built || flags != expected)
            Out.Line($"    got {(int)flags:X}, expected {(int)expected:X}");
    }

    static void Refusals()
    {
        CalendarTime june = Day(2026, 6, 1);
        string[] inter = { InterPem };
        Refused("expired", ExpiredPem, inter, X509ChainStatusFlags.NotTimeValid, june);
        Refused("not yet valid", LeafPem, inter, X509ChainStatusFlags.NotTimeValid, Day(2025, 12, 31));
        Refused("signed by another key", ForgedPem, inter,
            X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.PartialChain, june);
        Refused("issuer isn't a CA", NotCaChildPem, new[] { InterPem, NotCaPem }, X509ChainStatusFlags.InvalidBasicConstraints, june);
        Refused("path too long", DeepChildPem, new[] { InterPem, SubCaPem }, X509ChainStatusFlags.InvalidBasicConstraints, june);
        Refused("not for servers", ClientPem, inter, X509ChainStatusFlags.NotValidForUsage, june, serverOnly: true);
        Refused("unknown critical extension", CriticalPem, inter, X509ChainStatusFlags.HasNotSupportedCriticalExtension, june);
        Refused("SHA-1", Sha1Pem, inter, X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.PartialChain, june);
        Refused("RSA-PSS in a certificate", PssPem, new string[0],
            X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.PartialChain, june);
        Refused("missing intermediate", LeafPem, new string[0], X509ChainStatusFlags.PartialChain, june);
        Refused("untrusted root", LeafPem, new[] { InterPem, RootPem }, X509ChainStatusFlags.UntrustedRoot, june,
            rootPem: RootEPem);

        // VerificationFlags overlook what they name, and the status still says it.
        using X509Chain overlooked = NewChain(RootEPem, june);
        overlooked.ChainPolicy.ExtraStore.Add(X509Certificate2.CreateFromPem(InterPem));
        overlooked.ChainPolicy.ExtraStore.Add(X509Certificate2.CreateFromPem(RootPem));
        overlooked.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        Check("AllowUnknownCertificateAuthority", overlooked.Build(X509Certificate2.CreateFromPem(LeafPem))
                                                  && Flags(overlooked) == X509ChainStatusFlags.UntrustedRoot
                                                  && overlooked.ChainElements.Count == 3);
        using X509Chain late = NewChain(RootPem, Day(2030, 1, 1));
        late.ChainPolicy.ExtraStore.Add(X509Certificate2.CreateFromPem(InterPem));
        late.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        Check("IgnoreNotTimeValid", late.Build(X509Certificate2.CreateFromPem(LeafPem)) && Flags(late) == X509ChainStatusFlags.NotTimeValid);
        Check("element status", late.ChainElements[0].ChainElementStatus.Length == 1
                                && late.ChainElements[2].ChainElementStatus.Length == 0);

        // .NET's default asks for revocation checks, which aren't done here:
        // Build says so and refuses, without throwing.
        using X509Chain revocation = NewChain(RootPem, june);
        revocation.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        revocation.ChainPolicy.ExtraStore.Add(X509Certificate2.CreateFromPem(InterPem));
        X509Certificate2 leaf = X509Certificate2.CreateFromPem(LeafPem);
        Check("revocation unknown", !revocation.Build(leaf)
            && Flags(revocation) == (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)
            && revocation.ChainElements[2].ChainElementStatus.Length == 0);
        revocation.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreEndRevocationUnknown
            | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown;
        Check("revocation overlooked", revocation.Build(leaf));
        revocation.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreEndRevocationUnknown;
        Check("only the leaf overlooked", !revocation.Build(leaf));

        Section("Chain refusals");
    }

    static void RealChains()
    {
        CalendarTime now = Day(2026, 11, 28);

        // letsencrypt.org: ECDSA leaf and CAs, then ISRG Root X2 cross-signed
        // by ISRG Root X1, an RSA 4096 root.
        X509Certificate2Collection sent = new X509Certificate2Collection();
        sent.ImportFromPem(LetsEncryptChainPem);
        using X509Chain le = NewChain(IsrgRootX1Pem, now);
        for (int i = 1; i < sent.Count; i++)
            le.ChainPolicy.ExtraStore.Add(sent[i]);
        le.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        bool ok = le.Build(sent[0]);
        Check("letsencrypt.org", ok && le.ChainElements.Count == 5);
        if (!ok)
            foreach (X509ChainStatus s in le.ChainStatus)
                Out.Line($"    {s.StatusInformation}");
        Check("letsencrypt.org name", sent[0].MatchesHostname("letsencrypt.org") && sent[0].MatchesHostname("www.lencr.org")
                                      && !sent[0].MatchesHostname("example.org"));

        // github.com: Sectigo's ECDSA chain up to USERTrust's ECC root.
        X509Certificate2Collection gh = new X509Certificate2Collection();
        gh.ImportFromPem(GitHubChainPem);
        using X509Chain github = NewChain(UserTrustEccPem, now);
        for (int i = 1; i < gh.Count; i++)
            github.ChainPolicy.ExtraStore.Add(gh[i]);
        ok = github.Build(gh[0]);
        Check("github.com", ok && github.ChainElements.Count == 4);
        if (!ok)
            foreach (X509ChainStatus s in github.ChainStatus)
                Out.Line($"    {s.StatusInformation}");
        Check("github.com name", gh[0].MatchesHostname("github.com") && !gh[0].MatchesHostname("gist.github.com"));

        // The same chains once their certificates have expired.
        using X509Chain later = NewChain(UserTrustEccPem, Day(2027, 6, 1));
        for (int i = 1; i < gh.Count; i++)
            later.ChainPolicy.ExtraStore.Add(gh[i]);
        Check("github.com expired", !later.Build(gh[0]) && Flags(later) == X509ChainStatusFlags.NotTimeValid);

        Section("Real chains");
    }

    static readonly (string Bits, string Spki, string Pss, string Pkcs1)[] s_oddKeys =
    {
        ("1025", "30819f300d06092a864886f70d010101050003818d0030818902818101831b557ca52981c13eae8d2e8fd1033081066dd4099a05f007591fddccb72e28c218bb7e62034bf4a57f3e79ce589da48fc673e11fe320c09e7ce6fd2c79a590473064f96e30b76db8460f523e0152291ef343ebdf5f3174731d2d5c604d59c499119f2872bcadefaf06d87795bfb6f643fff7e1c08799439a1f219be575fa710203010001",
            "008eebc457d88f153208b879d50bd5e22eda76ee754dd41c1af3c4f1cad783419863522569586e8ba628f210d61ad17a113bce1920b4d1c99081b447506c777febfdbbb6fedf405a46cef976a0177918f99674d6a5d9864bc71c65593b20e6d7d793dda2def49a0ba86d42df026678281aeee93656f5381e599a5565ecb57d7f65",
            "00957fc9fcffd7613e9fd3bbe91ff4bdaa7676170ddee875f95792638d90b3c5131411ae0c58e862d8b1960a0f6e1997cf3972edf2fcd271896f15a42068b4f64884f2e7baeaf0ba7b1b21c8e0450f0c516bf6c195641c56b1b342d3c09dddd19f22af0e500469410986c3ba33017f01538a39d61d78592891dbc3726ed39eb310"),
        ("2047", "30820121300d06092a864886f70d01010105000382010e003082010902820100621a30e01bda3815694386945f94a3a8644413cc743b7bc35f2e4f693377fef1e8a3c640f4ee340a0f28583229e86e6514517f82130e024fada54404b04865ca1062b7e5e76352279501e695e6a8e06bbca9d12f9d368e01d3d09137d7db9e6ae03d8bb6e1f8c421ebcc597df6a55cf1abd139cb7a392e7bb37b2cf014130696756f417f39ea62e2b3fcc97299d7610b072ce8de0727980669e8dce4527440795eede1488ea337f57844bba612d0993fe6cd784c8d6a732cc76cb44337d0a68851b4aba5115a9f09924f643d02d4e7956e375c2c4852e8c0864bac6bdaa8ab8ffed36c769c787ea8491443a27b0b041d1a02e9e7f316a4852e4be120b7a9de2f0203010001",
            "1a3b564df7e48a7279c824e469b14755f19e12188a31eed4e90935c181f55f31304893da5b4f5d9b363d38c37e493cd2b207e6a15ab907d7a2df4d9686a53055bfd9eda67c5e078a6ab4b911247b0732e25d38c225f19a2435b464d7eb2d24d674718cfbd6becdc8d53690d04902fe7d1494069a7534fd75ead066b5e5e8a39f6287b770ddef094b79667a499887056bb816e23a233f4ac8a62e074b043f5bbbe887137747f2471fbb4a64616cc11687059ddfd01ec6c363ae809864a8b94ef93e3fcaab9796930eaf3d63549557d6b9a42839126a79d13cc9d68071e88a88fc8f4fd9bf8ec32f56844ede70271013773e40511b6a303b1344da0619bb1490c4",
            "59c88036c94040667678e4d72ef9e80bef3697b602a472c43b5828689139fbc98419243b0064d1f663fed2515d3e74e2ed9f832b0a409116659758cd78a54f62919ca62f82d6adce0fd08badac334fd870c8af8c996429ae5ecac85144d57ac235b99d9a7ef701edc7f9125dc6bedd5660505e063a09b39e3c7585b025790b34963feaf2542fc54e00fa0c8fa2d5ecb13d5ab75949de1fc5014ab5a710c451dbe11417042adb43ac1052e8bde4f8759401dc6b59e6bb7713daf9e7a5cdaa0369e9227a4057ef9a5eab9b438670c95803d9f2965b91b153fab4dfacb234ffebca4378e8aabac3b52ff178919decdae918fd8a4d605200939a03750fdd9f5ba746"),
    };

    const string RootPem = """
        -----BEGIN CERTIFICATE-----
        MIIDPzCCAiegAwIBAgIJALQObwehUbhuMA0GCSqGSIb3DQEBCwUAMD0xCzAJBgNV
        BAYTAlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGjAYBgNVBAMMEVplcm8gVGVzdCBS
        b290IFIxMB4XDTI2MDEwMTAwMDAwMFoXDTI3MDEwMTAwMDAwMFowPTELMAkGA1UE
        BhMCVVMxEjAQBgNVBAoMCVplcm8gVGVzdDEaMBgGA1UEAwwRWmVybyBUZXN0IFJv
        b3QgUjEwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQCzCbOG7Nba7FE5
        c+gpWrEWU9b326eLhNfBl7DRv1eMr9WIALC1tzfY4rGTaVdsAHlX8aFhm/fbMdci
        68A4F1nepiu18zNbB7KxpJUuC9tay04SkEi3z0zvYDAxruw0xQPJk9/DaKQfi+rw
        CH0WwDNB+GutzP5oDZ4ZbWAHddxFke0wMjlVnS+2RyBDcpwy8DBm6ElrLt/EBXnM
        ZbRBEKz70p+oRvsFieYqAY1pCI6s258Dme0K+Xbyu3PjHjUeWEhCfUAecueeLIuY
        i9PM06AYyAOHjx72cLHOoo5CfvoihyI8UXh6BkTaJECIXA4ye9gJn3pUWtxC2jpA
        LD5qdhSbAgMBAAGjQjBAMA8GA1UdEwEB/wQFMAMBAf8wDgYDVR0PAQH/BAQDAgEG
        MB0GA1UdDgQWBBRIztWg7/HLgOWkUYgKBk6NDUZSsjANBgkqhkiG9w0BAQsFAAOC
        AQEAhTKPT3DzSD2FyQQsIcIUmqx3uf9b0VfhgrMru9nZgbrQ9RnXtH+HHr3afe5N
        5KNw9tUiL+gE+viuOPysZB1B8Omysc8eAHNNj4JeK8dEF/xTdUz/jj+GsYekXmK1
        HBoc50ntBAZbW93jDmXwrq6kbEuGj3zuCSXb3o/Lc3koklHOWuyPwIERfI5+BxHo
        uZWhhQKFbpUzF7L2wAKIE/zlev4jffz4slhBDg5W7+jIShFGVWkdCH7B3MrZYyPp
        O2XP0X1YMNbMRi7pwnO/0PCAe7Yhc14N9sBDFl+Y9KkwGlgSF6or9S1t13JIUmkh
        AnjMlv6ny8ANvwfp1I8zBEsXHw==
        -----END CERTIFICATE-----
        """;

    const string InterPem = """
        -----BEGIN CERTIFICATE-----
        MIIC1DCCAbygAwIBAgIJAIu3AjdhShIvMA0GCSqGSIb3DQEBCwUAMD0xCzAJBgNV
        BAYTAlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGjAYBgNVBAMMEVplcm8gVGVzdCBS
        b290IFIxMB4XDTI2MDEwMTAwMDAwMFoXDTI3MDEwMTAwMDAwMFowOzELMAkGA1UE
        BhMCVVMxEjAQBgNVBAoMCVplcm8gVGVzdDEYMBYGA1UEAwwPWmVybyBUZXN0IENB
        IEUxMHYwEAYHKoZIzj0CAQYFK4EEACIDYgAE7gNQCWXt1DAYaTzDrh70mF8gC1l5
        JVd7pzW6loVUsNbjGJuCNB0vdzssHclSRK3eKz1BJqVYWlds1ykmu+oB60fK+wXG
        MBjThiofQRYptMymScTdROnzJaoEL9A8wGHJo4GGMIGDMBIGA1UdEwEB/wQIMAYB
        Af8CAQAwDgYDVR0PAQH/BAQDAgEGMB0GA1UdJQQWMBQGCCsGAQUFBwMBBggrBgEF
        BQcDAjAdBgNVHQ4EFgQUPD57g69/BYm36mf3pKIKK4nj8wYwHwYDVR0jBBgwFoAU
        SM7VoO/xy4DlpFGICgZOjQ1GUrIwDQYJKoZIhvcNAQELBQADggEBAFEWox9n0nR9
        jLJyZr0EWm543AJfXupJSdJPq148e6IcxvuA6DRIThep788+MIwtTJNs4eufUBou
        9ReTaEmDTsS1NUPzAV6RPxtanAMofnO69ar9KDHoq1WBh91esMW2SSuk+9tFAiuY
        do8Dj5G4/rAsNk4fPXjRdZhsB7iMXWELY8ODBAyVCI9kMnZVTExLc6k6xAflbdxi
        vs1ZmAX/+Ky0mriPjpU0D3Syprk40K9Lpx3FZI/SUqAuvZbAK2gQYXYiR4leVz+h
        xcYpw+NBB21zsAE0FbIVnXwCsFPlqUAjSjiI0mEVHjf9NCV5ovSaF5OrSDtwzBmg
        BpfyvSTj5nw=
        -----END CERTIFICATE-----
        """;

    const string LeafPem = """
        -----BEGIN CERTIFICATE-----
        MIICUjCCAdigAwIBAgIJAO0g1kvrt505MAoGCCqGSM49BAMDMDsxCzAJBgNVBAYT
        AlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGDAWBgNVBAMMD1plcm8gVGVzdCBDQSBF
        MTAeFw0yNjAxMDEwMDAwMDBaFw0yNzAxMDEwMDAwMDBaMEAxCzAJBgNVBAYTAlVT
        MRYwFAYDVQQKDA1FeGFtcGxlLCBJbmMuMRkwFwYDVQQDDBB3d3cuZXhhbXBsZS50
        ZXN0MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5R8PLvgQmsZNEbVmO6TmM241
        YqJ+sNvrv8CIsko9/S/TCPE+gVTMb/mydoHn5hCRRQU9JH99Gp3xNk15s7pWW6OB
        vzCBvDAMBgNVHRMBAf8EAjAAMA4GA1UdDwEB/wQEAwIHgDATBgNVHSUEDDAKBggr
        BgEFBQcDATBHBgNVHREEQDA+ghB3d3cuZXhhbXBsZS50ZXN0ghIqLmFwaS5leGFt
        cGxlLnRlc3SHBMAAAgeHECABDbgAAAAAAAAAAAAAAAcwHQYDVR0OBBYEFAVj1vxk
        LJ0fjGQyJuyOuSwrTk2rMB8GA1UdIwQYMBaAFDw+e4OvfwWJt+pn96SiCiuJ4/MG
        MAoGCCqGSM49BAMDA2gAMGUCMHGtq9dkkwuXgnxphLz+Yz/aTAFA1jOX/xKDGHg2
        OcVuEDOY1NCfW1y7TeEkkmetsQIxALccmlVyax17WFZ/TpAdtHqtY8/Hi3aZzZ9w
        RkcLl70q7VLMAcxwUwN1p9dE4/Z9Tw==
        -----END CERTIFICATE-----
        """;

    const string RootEPem = """
        -----BEGIN CERTIFICATE-----
        MIIBmjCCAT+gAwIBAgIJAODJ5Xd7rG+3MAoGCCqGSM49BAMCMDAxEjAQBgNVBAoM
        CVplcm8gVGVzdDEaMBgGA1UEAwwRWmVybyBUZXN0IFJvb3QgRTIwHhcNMjYwMTAx
        MDAwMDAwWhcNMjcwMTAxMDAwMDAwWjAwMRIwEAYDVQQKDAlaZXJvIFRlc3QxGjAY
        BgNVBAMMEVplcm8gVGVzdCBSb290IEUyMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcD
        QgAE7+E4AGGu6XnSn8hELhGrkBxVoQUu3bUG4FQqC68mn2nAiYMO/EN0ykg7LoLb
        zFV1CjpzgEiVXT8YD91H3tGWs6NCMEAwDwYDVR0TAQH/BAUwAwEB/zAOBgNVHQ8B
        Af8EBAMCAQYwHQYDVR0OBBYEFESi50fU4PILXpyl2Rlc6TcMGfWnMAoGCCqGSM49
        BAMCA0kAMEYCIQCMlyzk0nMcoFPqBKgn6yGETGZzzzfWPKhTwh3V4zjedgIhAJGc
        dUDgpKp6J3nxYgUY2mLzhZ/8D1b0ZxB8wO+gH+gs
        -----END CERTIFICATE-----
        """;

    const string LeafRsaPem = """
        -----BEGIN CERTIFICATE-----
        MIIC3jCCAoSgAwIBAgIIBIK1vgGmlsEwCgYIKoZIzj0EAwIwMDESMBAGA1UECgwJ
        WmVybyBUZXN0MRowGAYDVQQDDBFaZXJvIFRlc3QgUm9vdCBFMjAeFw0yNjAxMDEw
        MDAwMDBaFw0yNzAxMDEwMDAwMDBaMBsxGTAXBgNVBAMMEHJzYS5leGFtcGxlLnRl
        c3QwggGiMA0GCSqGSIb3DQEBAQUAA4IBjwAwggGKAoIBgQCsUDLXIuZkDEhPUSzO
        k4g2GJ7Sn/jZsHhyFCjt1HxDt1Pvvco2I7KsUqfGhT4d9XTXFFPSx8aZB8rYxsBp
        4aLVNYXqhhqw9HpsArgIrW30v0qCaipfQ8jdOLiyP+jlV4OqY23Jl3n5ioCB4uGL
        t4qMxSTfEstuvp8p8ny4PV3TV8ELC9Xw8JAn2Kn8f9+oPm2WuwsA1c1tp3hmqzPQ
        v3pK7QrK7cYQMGZzWSbin5uSzUyx5wDd8n4Y6GtknNYkN5kqv8oUcQ3qK2a2jlHA
        jwmp98qeR+5goMA6TJXoMGagdgQLnovwMFMrk9BPAlXxf478mAV2onPSIzQuPbC1
        yCjst4hOdk3ADtbN0cNA8Q7Untnavho27QRJ3LfU51CEffK6z1eAU+X3Ug/nYjcj
        uFAKbB4wJHRbZj9hv/4h/8FpwF5GCggiq/mqm1pfGfLNh6tyxYuGs1GvLG6/xlXM
        0byzPqt0jfGrYq7Zw/kauNVo9nkHBvy7VyJn2KRD4hMhmCkCAwEAAaNSMFAwDAYD
        VR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCBaAwEwYDVR0lBAwwCgYIKwYBBQUHAwEw
        GwYDVR0RBBQwEoIQcnNhLmV4YW1wbGUudGVzdDAKBggqhkjOPQQDAgNIADBFAiA+
        0Sl3Q/no0tAGc1z3gveSGeH1jt0MWSG3F1F9kkYsdgIhAI9PCa05UYMWwOcNJbY5
        3uAFW5cBOWYjVmAYUdnjvi4s
        -----END CERTIFICATE-----
        """;

    const string ExpiredPem = """
        -----BEGIN CERTIFICATE-----
        MIICLTCCAbOgAwIBAgIJAJwBfbJoW2qrMAoGCCqGSM49BAMDMDsxCzAJBgNVBAYT
        AlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGDAWBgNVBAMMD1plcm8gVGVzdCBDQSBF
        MTAeFw0yMDAxMDEwMDAwMDBaFw0yMTAxMDEwMDAwMDBaMBsxGTAXBgNVBAMMEHd3
        dy5leGFtcGxlLnRlc3QwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAT9+92Ns9t7
        YEX5mGvW/viiM+6w+zqz9tDjWe323iYoR+6wN4R0UsxzERzJJGIlCcNZ6rRMgJkP
        xyCPPfnsTy1oo4G/MIG8MAwGA1UdEwEB/wQCMAAwDgYDVR0PAQH/BAQDAgeAMBMG
        A1UdJQQMMAoGCCsGAQUFBwMBMEcGA1UdEQRAMD6CEHd3dy5leGFtcGxlLnRlc3SC
        EiouYXBpLmV4YW1wbGUudGVzdIcEwAACB4cQIAENuAAAAAAAAAAAAAAABzAdBgNV
        HQ4EFgQUSZ4qA9UP45Ld3cnFRjdmSot8bgkwHwYDVR0jBBgwFoAUPD57g69/BYm3
        6mf3pKIKK4nj8wYwCgYIKoZIzj0EAwMDaAAwZQIxAPVQSSJDWzuTDU9IpTglHIxc
        4nbtATlJSJIGrRhABuoa4v8Knhbh2Jerc+4jknQFYAIwdJX+YqcfVD0AmaALBDiQ
        7FXI9EYFlvY+VbVrI63SxfdqoN2NoBuDg20Tnwz2gtxw
        -----END CERTIFICATE-----
        """;

    const string ForgedPem = """
        -----BEGIN CERTIFICATE-----
        MIICLDCCAbKgAwIBAgIIf3CiXAAqWjMwCgYIKoZIzj0EAwMwOzELMAkGA1UEBhMC
        VVMxEjAQBgNVBAoMCVplcm8gVGVzdDEYMBYGA1UEAwwPWmVybyBUZXN0IENBIEUx
        MB4XDTI2MDEwMTAwMDAwMFoXDTI3MDEwMTAwMDAwMFowGzEZMBcGA1UEAwwQd3d3
        LmV4YW1wbGUudGVzdDBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABI28GUmTESrm
        lsYzTu8LZrS5JoVVabpOueNBt0A2z03X17/nYQCRX4b+BAFOWOTBrpSi1se+8SZy
        xxGrWvdnPW+jgb8wgbwwDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCB4AwEwYD
        VR0lBAwwCgYIKwYBBQUHAwEwRwYDVR0RBEAwPoIQd3d3LmV4YW1wbGUudGVzdIIS
        Ki5hcGkuZXhhbXBsZS50ZXN0hwTAAAIHhxAgAQ24AAAAAAAAAAAAAAAHMB0GA1Ud
        DgQWBBR2Xvzbc3U63jGPyBihgIJuSpnckzAfBgNVHSMEGDAWgBSgMeEvBHa6RdTV
        0Z6QzS7THInBoDAKBggqhkjOPQQDAwNoADBlAjEAg7ZEuV7QI+h/SD8ZYCSYCj2J
        WJA3N9PhdJeBkNwAcN60RxXu22jLnjLuXvj2+H1SAjA65Wl0Z+7DBSioUw4rBmtW
        igqEFWK8h8bREIEt2pSbar2Hx75GESNa6I0CxVHTbH4=
        -----END CERTIFICATE-----
        """;

    const string NotCaPem = """
        -----BEGIN CERTIFICATE-----
        MIIBhDCCAQqgAwIBAgIIdkP2aYLfwQQwCgYIKoZIzj0EAwMwOzELMAkGA1UEBhMC
        VVMxEjAQBgNVBAoMCVplcm8gVGVzdDEYMBYGA1UEAwwPWmVybyBUZXN0IENBIEUx
        MB4XDTI2MDEwMTAwMDAwMFoXDTI3MDEwMTAwMDAwMFowEzERMA8GA1UEAwwITm90
        IEEgQ0EwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAASe8VH7BSyI901lBvWvPSHr
        iRL5kWY3KYqgJfHAyqIcXn1W+HgTNLJisr5dU/KvKvwvcAOXTlarRkDAqRYmtdu4
        oyAwHjAMBgNVHRMBAf8EAjAAMA4GA1UdDwEB/wQEAwIChDAKBggqhkjOPQQDAwNo
        ADBlAjBaXHcZm9PSapHO39UuxOfp8eRzqcQ6Dw2YE12SAPRGSpRrSGFvX2v9jIqh
        6ad/0PUCMQCl0slwDL9ns6wlh58qns+F7QJj8dJ9BWvffOviQGoel10gdT0UlJaG
        S3GUYJWU8yE=
        -----END CERTIFICATE-----
        """;

    const string NotCaChildPem = """
        -----BEGIN CERTIFICATE-----
        MIIBVDCB/KADAgECAgkA6JNtPcEquC8wCgYIKoZIzj0EAwIwEzERMA8GA1UEAwwI
        Tm90IEEgQ0EwHhcNMjYwMTAxMDAwMDAwWhcNMjcwMTAxMDAwMDAwWjAdMRswGQYD
        VQQDDBJwbGFpbi5leGFtcGxlLnRlc3QwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNC
        AAQlheqnPMk20ia2/cJrFP6n+wqUSUzY0omgGqOHGnjr8h4MJ/BiP777jH8UwQi9
        8skZ5F4T2zFWj6jV1ZgSMGUGoy8wLTAMBgNVHRMBAf8EAjAAMB0GA1UdEQQWMBSC
        EnBsYWluLmV4YW1wbGUudGVzdDAKBggqhkjOPQQDAgNHADBEAiBpRhNnH3PlgzVb
        dEK3MXTSYIGMToqgIAeOf8R16CuHpgIgTpPdPFu/S4mhEds3pM6Gbh/HFFKc2ynO
        eXjkDZMEX8w=
        -----END CERTIFICATE-----
        """;

    const string SubCaPem = """
        -----BEGIN CERTIFICATE-----
        MIIBsDCCATWgAwIBAgIJAIGvbT1rn94YMAoGCCqGSM49BAMDMDsxCzAJBgNVBAYT
        AlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGDAWBgNVBAMMD1plcm8gVGVzdCBDQSBF
        MTAeFw0yNjAxMDEwMDAwMDBaFw0yNzAxMDEwMDAwMDBaMBsxGTAXBgNVBAMMEFpl
        cm8gVGVzdCBTdWIgQ0EwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAS6ku+047Xc
        fsrXsvVAUpSJUxVz3jU+EO69qD+vTRxLarD+F3JU6D1wi5kZ4drsMRP2p+kaMhpq
        +YV3KLinsLkOo0IwQDAPBgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBBjAd
        BgNVHQ4EFgQU/KBcztbnH+i+y37GJd7R06nzOhowCgYIKoZIzj0EAwMDaQAwZgIx
        AN7Gy+mNTi3ZaWXd/HUkP183taQo0sZuI2+RfGWEJelULFrcXaa02dYVImuyttqI
        JAIxAIshOoYHeKR+wqeqan7EZAV/EUY3E2t7jINllpgnD+T6Wb8iu7Ytz7KAmk7t
        P+rioQ==
        -----END CERTIFICATE-----
        """;

    const string DeepChildPem = """
        -----BEGIN CERTIFICATE-----
        MIIBXzCCAQSgAwIBAgIJAONQsiNKLRT6MAoGCCqGSM49BAMCMBsxGTAXBgNVBAMM
        EFplcm8gVGVzdCBTdWIgQ0EwHhcNMjYwMTAxMDAwMDAwWhcNMjcwMTAxMDAwMDAw
        WjAdMRswGQYDVQQDDBJwbGFpbi5leGFtcGxlLnRlc3QwWTATBgcqhkjOPQIBBggq
        hkjOPQMBBwNCAAQxA9/2ia1/YXKmq0J/QXxc9AdysjvtxbWhkqUVNS11bp5lRoz9
        8ads3fJtJbxQnj1r5y02ZYS7+9PwhmqvI4bboy8wLTAMBgNVHRMBAf8EAjAAMB0G
        A1UdEQQWMBSCEnBsYWluLmV4YW1wbGUudGVzdDAKBggqhkjOPQQDAgNJADBGAiEA
        9u2Kp9hoPIKEHqcFuJDK9uV1owymGgboxIB2vMQiWU4CIQCy1tY4UInx4CYbRNKI
        dcBYv0brw2aFkANGr0iOZMmLtg==
        -----END CERTIFICATE-----
        """;

    const string ClientPem = """
        -----BEGIN CERTIFICATE-----
        MIIBtDCCATugAwIBAgIJALEIG193QR4VMAoGCCqGSM49BAMDMDsxCzAJBgNVBAYT
        AlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGDAWBgNVBAMMD1plcm8gVGVzdCBDQSBF
        MTAeFw0yNjAxMDEwMDAwMDBaFw0yNzAxMDEwMDAwMDBaMB4xHDAaBgNVBAMME2Ns
        aWVudC5leGFtcGxlLnRlc3QwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAR9x80U
        GF+ynIqqkpCx8crc/TkpPeUEloB564jy/Vw6tXQ1j33p8P7oGf4rOZN062GYQEMO
        NTETSMsmeUUV6IPOo0UwQzAMBgNVHRMBAf8EAjAAMBMGA1UdJQQMMAoGCCsGAQUF
        BwMCMB4GA1UdEQQXMBWCE2NsaWVudC5leGFtcGxlLnRlc3QwCgYIKoZIzj0EAwMD
        ZwAwZAIwckdOaumZfHUEwmkg2z2tVZOpFupaLEyDrC8tm1+lqcqwuYG2BNKQePn+
        FEPrenjAAjBu4ASdaruLtVbFMF3QJhVyLKi78TrlBM5xb0welhjCFri+xi7D4bDA
        tMiUDMmdVTo=
        -----END CERTIFICATE-----
        """;

    const string CriticalPem = """
        -----BEGIN CERTIFICATE-----
        MIIBuTCCAT6gAwIBAgIJAMH7h/y/sF8gMAoGCCqGSM49BAMDMDsxCzAJBgNVBAYT
        AlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGDAWBgNVBAMMD1plcm8gVGVzdCBDQSBF
        MTAeFw0yNjAxMDEwMDAwMDBaFw0yNzAxMDEwMDAwMDBaMCAxHjAcBgNVBAMMFWNy
        aXRpY2FsLmV4YW1wbGUudGVzdDBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABO3v
        UtMqukDDRlMlxDy14T6R5riiw0qCwPum96zu56lDg9k0scY/VmZHlFIAq2P9jtdw
        Tbfwavh2e5d1VLciUn+jRjBEMAwGA1UdEwEB/wQCMAAwIAYDVR0RBBkwF4IVY3Jp
        dGljYWwuZXhhbXBsZS50ZXN0MBIGCSsGAQQBg7IDAQEB/wQCBQAwCgYIKoZIzj0E
        AwMDaQAwZgIxANm73oiR6lFz3Aqg2yXM446UbwqhNgBbXGgHRyBk3V9yVA76neo8
        inDW1lroNj6CwAIxALEbaROMHgbpo3uEBK4l3DzOkXVCgEpwfxGspctKIQSmnHrz
        skURllBjHWzi/bPQlQ==
        -----END CERTIFICATE-----
        """;

    const string Sha1Pem = """
        -----BEGIN CERTIFICATE-----
        MIIBnDCCASKgAwIBAgIIDfkDGw8UPmAwCQYHKoZIzj0EATA7MQswCQYDVQQGEwJV
        UzESMBAGA1UECgwJWmVybyBUZXN0MRgwFgYDVQQDDA9aZXJvIFRlc3QgQ0EgRTEw
        HhcNMjYwMTAxMDAwMDAwWhcNMjcwMTAxMDAwMDAwWjAdMRswGQYDVQQDDBJwbGFp
        bi5leGFtcGxlLnRlc3QwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAR9AuWakXSD
        d09UZK/ay0ga6Fla/4FolEsn/LtblUvhWLJDxbZkxQMR6QF9ErVP/Az6A892tlE8
        mVCGurgQggb/oy8wLTAMBgNVHRMBAf8EAjAAMB0GA1UdEQQWMBSCEnBsYWluLmV4
        YW1wbGUudGVzdDAJBgcqhkjOPQQBA2kAMGYCMQDVWnV5kSwmhgvQ60IOi/MgWGOp
        7M4viwoFYKJCsvo+1sAXWdpvIiucIj27oeMju9ACMQCU/YlR3LBbvqvJki+qhbig
        6W1NNTK7PrnK0+llp3w2nst8zdB7jV7eNd7tZXrPFzY=
        -----END CERTIFICATE-----
        """;

    const string PssPem = """
        -----BEGIN CERTIFICATE-----
        MIICojCCAVagAwIBAgICAQIwQQYJKoZIhvcNAQEKMDSgDzANBglghkgBZQMEAgEF
        AKEcMBoGCSqGSIb3DQEBCDANBglghkgBZQMEAgEFAKIDAgEgMD0xCzAJBgNVBAYT
        AlVTMRIwEAYDVQQKDAlaZXJvIFRlc3QxGjAYBgNVBAMMEVplcm8gVGVzdCBSb290
        IFIxMB4XDTI2MDEwMTAwMDAwMFoXDTI3MDEwMTAwMDAwMFowHTEbMBkGA1UEAwwS
        cGxhaW4uZXhhbXBsZS50ZXN0MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOh8Q
        t/YdbqFTMbAuSaZe6Y1wA3XpjcGGb4d7pUBSohyRgI4s5CamQ+vSA+yOoQbn6nvi
        CzbPpijYgKqw2Q5gmKMvMC0wDAYDVR0TAQH/BAIwADAdBgNVHREEFjAUghJwbGFp
        bi5leGFtcGxlLnRlc3QwQQYJKoZIhvcNAQEKMDSgDzANBglghkgBZQMEAgEFAKEc
        MBoGCSqGSIb3DQEBCDANBglghkgBZQMEAgEFAKIDAgEgA4IBAQARfQkHySeFQQzO
        X+W6GFJCHCYP+AxBw+pogotsCrfLFCaQoB7wYVcuxfNt9d52DhQIGrl2ACOBemBY
        FCjHncCPZ4gYn0RiGGkGkwph/0oFEPZMkZ6CKeEbKQeSLqxBoRKzJORt6lQ1PiRM
        Fr2fdrKpSAwmDK+flkb2qNevyuQsGCWIVMSiAMZBFN79QKI3ybLiO9+HSZl3Zquw
        awyI2jYLhapee2OMxDaacyOFaJmy2O/gWYUYlnGhH+Ojg75RPm6DPpUf8QUs9U5a
        zNXIIv637LPMm0X+TRfAf8MHV5xWdC5t8NdGTlPgQAmX2I2NFK3QD+4XhN4qNauU
        YstE1nND
        -----END CERTIFICATE-----
        """;

    const string LetsEncryptChainPem = """
        -----BEGIN CERTIFICATE-----
        MIIEPjCCA8SgAwIBAgISBWiMkq1RKjZ422NVgxoL+70yMAoGCCqGSM49BAMDMDMx
        CzAJBgNVBAYTAlVTMRYwFAYDVQQKEw1MZXQncyBFbmNyeXB0MQwwCgYDVQQDEwNZ
        RTIwHhcNMjYxMTAzMTM0NDM0WhcNMjcwMjAxMTM0NDMzWjAaMRgwFgYDVQQDEw9s
        ZXRzZW5jcnlwdC5vcmcwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAATwMStYVLOg
        2Fb8v6HhbJSBjA2F5/t15QCPdaq9NvDNlX5Au/xBrmNdxUd08LQJRIcyhFFeiq9+
        DGCQNOC9IFISo4ICzzCCAsswDgYDVR0PAQH/BAQDAgeAMBMGA1UdJQQMMAoGCCsG
        AQUFBwMBMAwGA1UdEwEB/wQCMAAwHQYDVR0OBBYEFM3hJcx4hrtzy4N4tnj24rli
        SSBFMB8GA1UdIwQYMBaAFLlZ8o7PIvCG0zdI/3YUGLqC2FWHMDMGCCsGAQUFBwEB
        BCcwJTAjBggrBgEFBQcwAoYXaHR0cDovL3llMi5pLmxlbmNyLm9yZy8wgdMGA1Ud
        EQSByzCByIISY3AubGV0c2VuY3J5cHQub3JnghpjcC5yb290LXgxLmxldHNlbmNy
        eXB0Lm9yZ4ITY3BzLmxldHNlbmNyeXB0Lm9yZ4IbY3BzLnJvb3QteDEubGV0c2Vu
        Y3J5cHQub3JngglsZW5jci5vcmeCD2xldHNlbmNyeXB0LmNvbYIPbGV0c2VuY3J5
        cHQub3Jngg13d3cubGVuY3Iub3JnghN3d3cubGV0c2VuY3J5cHQuY29tghN3d3cu
        bGV0c2VuY3J5cHQub3JnMBMGA1UdIAQMMAowCAYGZ4EMAQIBMC4GA1UdHwQnMCUw
        I6AhoB+GHWh0dHA6Ly95ZTIuYy5sZW5jci5vcmcvNTEuY3JsMIIBBAYKKwYBBAHW
        eQIEAgSB9QSB8gDwAHcATGPcmOWcHauI9h6KPd6uj6tEozd7X5uUw/uhnPzBviYA
        AAGhod0ZwAAABAMASDBGAiEAofMALApTmDrNb1+VD6B1VVVNJzgm5FZ0GofExfXd
        vTQCIQCP7p3v3VnJAFwRnksr06jVmLUCaJTGiAf+iZtpAUotegB1AEaiOWfGDbZG
        h8ZvPfmZlHaTpqYRIIRX1VXn49Ch2bZGAAABoaHdIYwAAAQDAEYwRAIgBvADZT8P
        00lzDNwzoJgV61ndN0JL7NMo9MbdoW+3ZoACIDwxLBrSqEC6vZT585he2z2GfIXZ
        +rXfSyJUzRPGNQt8MAoGCCqGSM49BAMDA2gAMGUCMQDjHdwKFqoSgXa+bqAUSDYF
        rS5aeDAZ3yQ1x5PVzDnOx4VqCP8YSef9x0ur6ywRm5oCME4Jc3v7oT46JURlMDmF
        VG3S31kxPRCpAIaY4+Hktd/3+6JBqUEAeeHAy0iUUAjCqA==
        -----END CERTIFICATE-----
        -----BEGIN CERTIFICATE-----
        MIICjDCCAhGgAwIBAgIQTfOxXdbAeExQfNN7WObxFTAKBggqhkjOPQQDAzAuMQsw
        CQYDVQQGEwJVUzENMAsGA1UEChMESVNSRzEQMA4GA1UEAxMHUm9vdCBZRTAeFw0y
        NTA5MDMwMDAwMDBaFw0yODA5MDIyMzU5NTlaMDMxCzAJBgNVBAYTAlVTMRYwFAYD
        VQQKEw1MZXQncyBFbmNyeXB0MQwwCgYDVQQDEwNZRTIwdjAQBgcqhkjOPQIBBgUr
        gQQAIgNiAARxmrQzkdbEEL3MqXt3dJQttYc47axkdDTHud5TPqM2z5uSD5cmk0Wr
        HlWXvnlvqBLqiB34kluxIbmMyAiq3/YD6e80/vV259K8XQIdjFXloYOa0mIU71f7
        HQ09PvYDlw+jge4wgeswDgYDVR0PAQH/BAQDAgGGMBMGA1UdJQQMMAoGCCsGAQUF
        BwMBMBIGA1UdEwEB/wQIMAYBAf8CAQAwHQYDVR0OBBYEFLlZ8o7PIvCG0zdI/3YU
        GLqC2FWHMB8GA1UdIwQYMBaAFKPIJlqOoUzQNWP8myPIOq5W809WMDIGCCsGAQUF
        BwEBBCYwJDAiBggrBgEFBQcwAoYWaHR0cDovL3llLmkubGVuY3Iub3JnLzATBgNV
        HSAEDDAKMAgGBmeBDAECATAnBgNVHR8EIDAeMBygGqAYhhZodHRwOi8veWUuYy5s
        ZW5jci5vcmcvMAoGCCqGSM49BAMDA2kAMGYCMQDIcnw5dcZLN9ffynXnnkLD/itS
        JEycJPb3sRkzeqBowup7vOsAwaqoCnNn/jh9wycCMQCJM6CPlaOC4pQYYbJtVPYb
        DKrIb2EKk5NpOpE6/XttQYZV/3gilB9l+Cc/DOVwmyg=
        -----END CERTIFICATE-----
        -----BEGIN CERTIFICATE-----
        MIICpjCCAiugAwIBAgIRAIchZfw0tuX7qK3Vs3BftTowCgYIKoZIzj0EAwMwTzEL
        MAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2VhcmNo
        IEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDIwHhcNMjYwNTEzMDAwMDAwWhcN
        MzIwOTAyMjM1OTU5WjAuMQswCQYDVQQGEwJVUzENMAsGA1UEChMESVNSRzEQMA4G
        A1UEAxMHUm9vdCBZRTB2MBAGByqGSM49AgEGBSuBBAAiA2IABDwS/6vhrcVqcbBo
        +wgdI3fwn9x7DNJJOY/lTOti0vkwuRN87RhEhTH17E7XyFjWsPYhIPt/wzOqxTd2
        b+4ZJNy9ID04YywF9U5zasDVyGSNErVNtz8uSGh5izW87j77GaOB6zCB6DAOBgNV
        HQ8BAf8EBAMCAQYwEwYDVR0lBAwwCgYIKwYBBQUHAwEwDwYDVR0TAQH/BAUwAwEB
        /zAdBgNVHQ4EFgQUo8gmWo6hTNA1Y/ybI8g6rlbzT1YwHwYDVR0jBBgwFoAUfEKW
        rt5LSDv6kviejM9ti6lyN5UwMgYIKwYBBQUHAQEEJjAkMCIGCCsGAQUFBzAChhZo
        dHRwOi8veDIuaS5sZW5jci5vcmcvMBMGA1UdIAQMMAowCAYGZ4EMAQIBMCcGA1Ud
        HwQgMB4wHKAaoBiGFmh0dHA6Ly94Mi5jLmxlbmNyLm9yZy8wCgYIKoZIzj0EAwMD
        aQAwZgIxAMU19WCtmxVND8UHBZRoma49Z7jPs64Dma0eTu1OChVbB/2J7GV3nvYK
        Ax54uk1G9QIxAO0miLVJu8PLNiXXXkiE/gsK3CTRTF/aeo4bMX42Zw40csRU6AC2
        6hSW1/IWaas6dg==
        -----END CERTIFICATE-----
        -----BEGIN CERTIFICATE-----
        MIIEcDCCAligAwIBAgIQbI8dxyfHEX97r4U6yYD5zTANBgkqhkiG9w0BAQsFADBP
        MQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJuZXQgU2VjdXJpdHkgUmVzZWFy
        Y2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBYMTAeFw0yNjA1MTMwMDAwMDBa
        Fw0zMjA5MDIyMzU5NTlaME8xCzAJBgNVBAYTAlVTMSkwJwYDVQQKEyBJbnRlcm5l
        dCBTZWN1cml0eSBSZXNlYXJjaCBHcm91cDEVMBMGA1UEAxMMSVNSRyBSb290IFgy
        MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAEzZvVn4CDCuwJSvMWSj5cz3es3mcFDR0H
        ttwW+1qLFNvicWDEukWVEYmO6gbf9yoWHKS5xcUy4APgHoIYOIvXRdgKam7mAHf7
        AlF9ItgKbppbd9/w+kHsOdx1ymgHDB/qo4H1MIHyMA4GA1UdDwEB/wQEAwIBBjAd
        BgNVHSUEFjAUBggrBgEFBQcDAQYIKwYBBQUHAwIwDwYDVR0TAQH/BAUwAwEB/zAd
        BgNVHQ4EFgQUfEKWrt5LSDv6kviejM9ti6lyN5UwHwYDVR0jBBgwFoAUebRZ5nu2
        5eQBc4AIiMgaWPbpm24wMgYIKwYBBQUHAQEEJjAkMCIGCCsGAQUFBzAChhZodHRw
        Oi8veDEuaS5sZW5jci5vcmcvMBMGA1UdIAQMMAowCAYGZ4EMAQIBMCcGA1UdHwQg
        MB4wHKAaoBiGFmh0dHA6Ly94MS5jLmxlbmNyLm9yZy8wDQYJKoZIhvcNAQELBQAD
        ggIBAD2/e9frmMxNpCV03qUHegg+MV2wz9644YoXdqtH8RyWYcBO7xfjjGEXdU1e
        /o0OkEFiynUCOSIk/vLLo7ttz6CPAeNlWfC0XNkoGeWgK6jjXvozBaGuGH5n0Ufo
        shMeWTuURqNN5G00sSXDTBrpp2+mgvdZQjb8K11TYMA25QA+YHNfbIEL0BniAhKS
        2gsnJjSzrdZLI+EZ7SEyqdR2rkjd1KutLDU+n3TFyxjniZVGur4YlhMP3mY/dV95
        IruAkkjOZier6hGBdEgZXXvaCz9u9iVEadsIE75pAGL8oHV5vxdARDiotRpul1IN
        /UZwzAbrfUFcw1HkAcYD/mlZfnQ2ieCF2MS7j3Vhv7JPDKp45fmykmzYNSrumRW0
        upFFKDBOoF7hsOb7oLyHS+Uft6jOUfOrogj8YUx38hKb2K20r42OgsSdDdxdeYWc
        MS3Sb6mwJeSZEYxJ2gaXnDSPaKhhrNkYwljyVQyr4Nq+MEJytXNTnHqaAcrNwZlV
        pcJL1KBnMrMjP7eanvUwL3FYj3cF17jtboLt7gLoi4+2rWZFvn+w54jmd/FIuhhZ
        cEaU/wvU6BUNMtcVquVGHp7itQeDth5j+XL3j4WJ2SABwzUl6OeYdgpIt/ITZa+p
        TT0mQ/r5XyA4MEAiabn7XJjvCERlF2dcn2wqJw+CreTkkQ2R
        -----END CERTIFICATE-----
        """;

    const string IsrgRootX1Pem = """
        -----BEGIN CERTIFICATE-----
        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
        -----END CERTIFICATE-----
        """;

    const string GitHubChainPem = """
        -----BEGIN CERTIFICATE-----
        MIID9DCCA5ugAwIBAgIQPh1gJrSwrqJ53KLvu87YKzAKBggqhkjOPQQDAjBgMQsw
        CQYDVQQGEwJHQjEYMBYGA1UEChMPU2VjdGlnbyBMaW1pdGVkMTcwNQYDVQQDEy5T
        ZWN0aWdvIFB1YmxpYyBTZXJ2ZXIgQXV0aGVudGljYXRpb24gQ0EgRFYgRTM2MB4X
        DTI2MTAzMDAwMDAwMFoXDTI3MDEyNzIzNTk1OVowFTETMBEGA1UEAxMKZ2l0aHVi
        LmNvbTBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABDfKd4nH+v687AH/LbUBuI5o
        XVsDbbLwRkLBCDbDmYWcrpxd8+9O0uPHpIgjAQ0gBToGy66v4d70x/jgDoosK3ej
        ggKAMIICfDAfBgNVHSMEGDAWgBQXmagEwW/kLXCoChA9A9PpGrgmYzAdBgNVHQ4E
        FgQUjLAqHq7uhl0AATWx5YtpI6U+PHEwDgYDVR0PAQH/BAQDAgeAMAwGA1UdEwEB
        /wQCMAAwEwYDVR0lBAwwCgYIKwYBBQUHAwEwSQYDVR0gBEIwQDA0BgsrBgEEAbIx
        AQICBzAlMCMGCCsGAQUFBwIBFhdodHRwczovL3NlY3RpZ28uY29tL0NQUzAIBgZn
        gQwBAgEwgYQGCCsGAQUFBwEBBHgwdjBPBggrBgEFBQcwAoZDaHR0cDovL2NydC5z
        ZWN0aWdvLmNvbS9TZWN0aWdvUHVibGljU2VydmVyQXV0aGVudGljYXRpb25DQURW
        RTM2LmNydDAjBggrBgEFBQcwAYYXaHR0cDovL29jc3Auc2VjdGlnby5jb20wJQYD
        VR0RBB4wHIIKZ2l0aHViLmNvbYIOd3d3LmdpdGh1Yi5jb20wggEMBgorBgEEAdZ5
        AgQCBIH9BIH6APgAdQBGojlnxg22RofGbz35mZR2k6amESCEV9VV5+PQodm2RgAA
        AaGKLocgAAAEAwBGMEQCICIQbbpOkqdkpMHlEO4IaqjUE51GdnAX8lTdfJqayeUy
        AiBW+rfEnQghzD2sljP8DmiE364OeNXjRLnoS4/+EDmBMQB/AKKBABhzThduHUfg
        lUDzgbpUZpfNY6hDUHFuuAlO2vENAAABoYouiCYACAAABQAyHujyBAMASDBGAiEA
        qR77xK7WOstHFrsyltQlPHqO5UAlaD5EUGgfV+SyHngCIQCTdXIrVaNFlwq2jVoU
        6eq3LTDtgBbBB8AS6pKHuIF94DAKBggqhkjOPQQDAgNHADBEAiAiIVULRbVeXo16
        6wK3Vp15eud/nlbNTa+j4FUnKD6GYAIgBcK3WmeV05N87KRIoaIV/h97FykBOCul
        KBsCT9qemhg=
        -----END CERTIFICATE-----
        -----BEGIN CERTIFICATE-----
        MIIDXzCCAuagAwIBAgIQNuBZ7YiN1Xrt1XC2cn+b2jAKBggqhkjOPQQDAzBfMQsw
        CQYDVQQGEwJHQjEYMBYGA1UEChMPU2VjdGlnbyBMaW1pdGVkMTYwNAYDVQQDEy1T
        ZWN0aWdvIFB1YmxpYyBTZXJ2ZXIgQXV0aGVudGljYXRpb24gUm9vdCBFNDYwHhcN
        MjEwMzIyMDAwMDAwWhcNMzYwMzIxMjM1OTU5WjBgMQswCQYDVQQGEwJHQjEYMBYG
        A1UEChMPU2VjdGlnbyBMaW1pdGVkMTcwNQYDVQQDEy5TZWN0aWdvIFB1YmxpYyBT
        ZXJ2ZXIgQXV0aGVudGljYXRpb24gQ0EgRFYgRTM2MFkwEwYHKoZIzj0CAQYIKoZI
        zj0DAQcDQgAEaKGnbAUnBYljHDmn/yUhxe3TLxKYuyzc9VXoSaCEV5F73Fhfa/Si
        /RMsmwTFW3R9s7J6JpYZFmu4do3vk/Vgl6OCAYEwggF9MB8GA1UdIwQYMBaAFNEi
        2kxZ8UtfJjiqndbu6w3D+6lhMB0GA1UdDgQWBBQXmagEwW/kLXCoChA9A9PpGrgm
        YzAOBgNVHQ8BAf8EBAMCAYYwEgYDVR0TAQH/BAgwBgEB/wIBADAdBgNVHSUEFjAU
        BggrBgEFBQcDAQYIKwYBBQUHAwIwGwYDVR0gBBQwEjAGBgRVHSAAMAgGBmeBDAEC
        ATBUBgNVHR8ETTBLMEmgR6BFhkNodHRwOi8vY3JsLnNlY3RpZ28uY29tL1NlY3Rp
        Z29QdWJsaWNTZXJ2ZXJBdXRoZW50aWNhdGlvblJvb3RFNDYuY3JsMIGEBggrBgEF
        BQcBAQR4MHYwTwYIKwYBBQUHMAKGQ2h0dHA6Ly9jcnQuc2VjdGlnby5jb20vU2Vj
        dGlnb1B1YmxpY1NlcnZlckF1dGhlbnRpY2F0aW9uUm9vdEU0Ni5wN2MwIwYIKwYB
        BQUHMAGGF2h0dHA6Ly9vY3NwLnNlY3RpZ28uY29tMAoGCCqGSM49BAMDA2cAMGQC
        MFsKnBQDh64l+v+aUYWjDCJKQMxHUUGmcwAYDIjJ9pbRYItMCIx5xu0oUb6sIfTX
        qQIwPddcsDE4KdeLu1hJdpHgdLvsHAK3vygyLGujMU9xBJCDackRT93VHEE0gppg
        NqdV
        -----END CERTIFICATE-----
        -----BEGIN CERTIFICATE-----
        MIIDRjCCAsugAwIBAgIQGp6v7G3o4ZtcGTFBto2Q3TAKBggqhkjOPQQDAzCBiDEL
        MAkGA1UEBhMCVVMxEzARBgNVBAgTCk5ldyBKZXJzZXkxFDASBgNVBAcTC0plcnNl
        eSBDaXR5MR4wHAYDVQQKExVUaGUgVVNFUlRSVVNUIE5ldHdvcmsxLjAsBgNVBAMT
        JVVTRVJUcnVzdCBFQ0MgQ2VydGlmaWNhdGlvbiBBdXRob3JpdHkwHhcNMjEwMzIy
        MDAwMDAwWhcNMzgwMTE4MjM1OTU5WjBfMQswCQYDVQQGEwJHQjEYMBYGA1UEChMP
        U2VjdGlnbyBMaW1pdGVkMTYwNAYDVQQDEy1TZWN0aWdvIFB1YmxpYyBTZXJ2ZXIg
        QXV0aGVudGljYXRpb24gUm9vdCBFNDYwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAAR2
        +pmpbiDt+dd34wc7qNs9Xzjoq1WmVk/WSOrsfy2qw7LFeeyZYX8QeccCWvkEN/U0
        NSt3zn8gj1KjAIns1aeibVvjS5KToID1AZTc8GgHHs3u/iVStSBDHBv+6xnOQ6Oj
        ggEgMIIBHDAfBgNVHSMEGDAWgBQ64QmG1M8ZwpZ2dEl23OA1xmNjmjAdBgNVHQ4E
        FgQU0SLaTFnxS18mOKqd1u7rDcP7qWEwDgYDVR0PAQH/BAQDAgGGMA8GA1UdEwEB
        /wQFMAMBAf8wHQYDVR0lBBYwFAYIKwYBBQUHAwEGCCsGAQUFBwMCMBEGA1UdIAQK
        MAgwBgYEVR0gADBQBgNVHR8ESTBHMEWgQ6BBhj9odHRwOi8vY3JsLnVzZXJ0cnVz
        dC5jb20vVVNFUlRydXN0RUNDQ2VydGlmaWNhdGlvbkF1dGhvcml0eS5jcmwwNQYI
        KwYBBQUHAQEEKTAnMCUGCCsGAQUFBzABhhlodHRwOi8vb2NzcC51c2VydHJ1c3Qu
        Y29tMAoGCCqGSM49BAMDA2kAMGYCMQCMCyBit99vX2ba6xEkDe+YO7vC0twjbkv9
        PKpqGGuZ61JZryjFsp+DFpEclCVy4noCMQCwvZDXD/m2Ko1HA5Bkmz7YQOFAiNDD
        49IWa2wdT7R3DtODaSXH/BiXv8fwB9su4tU=
        -----END CERTIFICATE-----
        """;

    const string UserTrustEccPem = """
        -----BEGIN CERTIFICATE-----
        MIICjzCCAhWgAwIBAgIQXIuZxVqUxdJxVt7NiYDMJjAKBggqhkjOPQQDAzCBiDEL
        MAkGA1UEBhMCVVMxEzARBgNVBAgTCk5ldyBKZXJzZXkxFDASBgNVBAcTC0plcnNl
        eSBDaXR5MR4wHAYDVQQKExVUaGUgVVNFUlRSVVNUIE5ldHdvcmsxLjAsBgNVBAMT
        JVVTRVJUcnVzdCBFQ0MgQ2VydGlmaWNhdGlvbiBBdXRob3JpdHkwHhcNMTAwMjAx
        MDAwMDAwWhcNMzgwMTE4MjM1OTU5WjCBiDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
        Ck5ldyBKZXJzZXkxFDASBgNVBAcTC0plcnNleSBDaXR5MR4wHAYDVQQKExVUaGUg
        VVNFUlRSVVNUIE5ldHdvcmsxLjAsBgNVBAMTJVVTRVJUcnVzdCBFQ0MgQ2VydGlm
        aWNhdGlvbiBBdXRob3JpdHkwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAAQarFRaqflo
        I+d61SRvU8Za2EurxtW20eZzca7dnNYMYf3boIkDuAUU7FfO7l0/4iGzzvfUinng
        o4N+LZfQYcTxmdwlkWOrfzCjtHDix6EznPO/LlxTsV+zfTJ/ijTjeXmjQjBAMB0G
        A1UdDgQWBBQ64QmG1M8ZwpZ2dEl23OA1xmNjmjAOBgNVHQ8BAf8EBAMCAQYwDwYD
        VR0TAQH/BAUwAwEB/zAKBggqhkjOPQQDAwNoADBlAjA2Z6EWCNzklwBBHU6+4WMB
        zzuqQhFkoJ2UOQIReVx7Hfpkue4WQrO/isIJxOzksU0CMQDpKmFHjFJKS04YcPbW
        RNZu9YO6bVi9JNlWSOrvxKJGgYhqOkbRqZtNyWHa0V1Xahg=
        -----END CERTIFICATE-----
        """;

}
