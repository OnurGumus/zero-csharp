// lib/CryptoX509.cs and CryptoX509Chain.cs: damaged certificates must be
// refused or read without an exception (what a server sends mustn't end the
// program), and chains must be accepted or refused as .NET's X509Chain does.
// The certificates are the ones in samples/Certificates.cs.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using S = System.Security.Cryptography;
using SX = System.Security.Cryptography.X509Certificates;
using Z = ZeroTest;
using ZX = ZeroTest.X509Certificates;

static partial class Program
{
    static Dictionary<string, string> s_pems;

    static Dictionary<string, string> Pems()
    {
        if (s_pems != null)
            return s_pems;
        s_pems = new Dictionary<string, string>();
        string path = Environment.GetEnvironmentVariable("CERTIFICATES_SAMPLE");
        string text = System.IO.File.ReadAllText(path);
        foreach (Match m in Regex.Matches(text, "const string (\\w+) = \"\"\"\\n(.*?)\\n\\s*\"\"\";", RegexOptions.Singleline))
        {
            var lines = new List<string>();
            foreach (string line in m.Groups[2].Value.Split('\n'))
                lines.Add(line.Trim());
            s_pems[m.Groups[1].Value] = string.Join("\n", lines);
        }
        return s_pems;
    }

    static List<byte[]> AllDer()
    {
        var all = new List<byte[]>();
        foreach (string pem in Pems().Values)
            foreach (Match m in Regex.Matches(pem, "-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----", RegexOptions.Singleline))
                all.Add(Convert.FromBase64String(m.Groups[1].Value.Replace("\n", "")));
        return all;
    }

    static byte[] Damage(byte[] der)
    {
        var bytes = new List<byte>(der);
        int changes = 1 + s_random.Next(4);
        for (int c = 0; c < changes && bytes.Count > 0; c++)
        {
            int at = s_random.Next(bytes.Count);
            switch (s_random.Next(6))
            {
                case 0: bytes[at] ^= (byte)(1 << s_random.Next(8)); break;
                case 1: bytes[at] = (byte)s_random.Next(256); break;
                case 2: bytes.RemoveRange(at, Math.Min(bytes.Count - at, 1 + s_random.Next(8))); break;
                case 3: bytes.Insert(at, (byte)s_random.Next(256)); break;
                case 4: bytes.RemoveRange(at, bytes.Count - at); break; // truncate
                // Lengths and tags matter most: hit the bytes after a 0x30 or 0x82.
                default:
                    int i = bytes.FindIndex(at, b => b == 0x30 || b == 0x82 || b == 0x81);
                    if (i >= 0 && i + 1 < bytes.Count)
                        bytes[i + 1] = (byte)s_random.Next(256);
                    break;
            }
        }
        return bytes.ToArray();
    }

    static void X509Damaged(int iterations)
    {
        List<byte[]> all = AllDer();
        ZX.X509Certificate2 anchor = ZX.X509Certificate2.CreateFromPem(Pems()["RootPem"]);
        ZX.X509Certificate2 inter = ZX.X509Certificate2.CreateFromPem(Pems()["InterPem"]);
        int failures = 0, loaded = 0;
        string first = "";
        for (int i = 0; i < iterations; i++)
        {
            byte[] damaged = Damage(all[s_random.Next(all.Count)]);
            try
            {
                if (!ZX.X509Certificate2.TryLoad(damaged, out ZX.X509Certificate2 c))
                    continue;
                loaded++;
                // Everything a program might read, and a chain build.
                _ = c.Subject + c.Issuer + c.SerialNumber + c.SignatureAlgorithm.Value + c.NotBefore.Year + c.NotAfter.Year;
                _ = c.GetNameInfo(ZX.X509NameType.DnsName, false) + c.GetNameInfo(ZX.X509NameType.SimpleName, true)
                    + c.GetNameInfo(ZX.X509NameType.EmailName, false) + c.GetNameInfo(ZX.X509NameType.UrlName, false);
                c.MatchesHostname("www.example.test");
                c.MatchesHostname("192.0.2.7");
                c.GetRSAPublicKey();
                c.GetECDsaPublicKey();
                var chain = new ZX.X509Chain();
                chain.ChainPolicy.TrustMode = ZX.X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(anchor);
                chain.ChainPolicy.ExtraStore.Add(inter);
                chain.ChainPolicy.ExtraStore.Add(c);
                chain.ChainPolicy.RevocationMode = ZX.X509RevocationMode.NoCheck;
                chain.Build(c);
                // A damaged certificate can still be good only if the damage was
                // outside what is signed (the outer lengths can't change, so that
                // leaves nothing but bytes DER doesn't check).
            }
            catch (Exception e)
            {
                if (failures++ == 0)
                    first = $"{e.GetType().Name}: {e.Message} on {Convert.ToHexString(damaged)}";
            }
        }
        Report($"X.509 damaged ({loaded} read)", iterations, failures, first);
    }

    // The same chains through .NET's X509Chain and ours: both accept, or both refuse.
    static void X509Chains()
    {
        var pems = Pems();
        var scenarios = new (string Leaf, string[] Extra, string Root, DateTime At, bool Server)[]
        {
            ("LeafPem", new[] { "InterPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), true),
            ("LeafRsaPem", new string[0], "RootEPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), true),
            ("ClientPem", new[] { "InterPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("ExpiredPem", new[] { "InterPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("LeafPem", new[] { "InterPem" }, "RootPem", new DateTime(2025, 12, 31, 12, 0, 0, DateTimeKind.Utc), false),
            ("ForgedPem", new[] { "InterPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("NotCaChildPem", new[] { "InterPem", "NotCaPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("DeepChildPem", new[] { "InterPem", "SubCaPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("ClientPem", new[] { "InterPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), true),
            ("CriticalPem", new[] { "InterPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("Sha1Pem", new[] { "InterPem" }, "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("LeafPem", new string[0], "RootPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("LeafPem", new[] { "InterPem", "RootPem" }, "RootEPem", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), false),
            ("LetsEncryptChainPem", null, "IsrgRootX1Pem", new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc), true),
            ("GitHubChainPem", null, "UserTrustEccPem", new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc), true),
            ("GitHubChainPem", null, "UserTrustEccPem", new DateTime(2027, 6, 1, 12, 0, 0, DateTimeKind.Utc), true),
        };
        int failures = 0;
        string first = "";
        foreach (var (leafName, extra, rootName, at, server) in scenarios)
        {
            // .NET's
            var leafs = new SX.X509Certificate2Collection();
            leafs.ImportFromPem(pems[leafName]);
            using var dotnet = new SX.X509Chain();
            dotnet.ChainPolicy.TrustMode = SX.X509ChainTrustMode.CustomRootTrust;
            dotnet.ChainPolicy.CustomTrustStore.Add(SX.X509Certificate2.CreateFromPem(pems[rootName]));
            dotnet.ChainPolicy.RevocationMode = SX.X509RevocationMode.NoCheck;
            dotnet.ChainPolicy.VerificationTime = at.ToLocalTime();
            dotnet.ChainPolicy.VerificationTimeIgnored = false;
            dotnet.ChainPolicy.DisableCertificateDownloads = true;
            if (server)
                dotnet.ChainPolicy.ApplicationPolicy.Add(new S.Oid("1.3.6.1.5.5.7.3.1"));
            for (int i = 1; i < leafs.Count; i++)
                dotnet.ChainPolicy.ExtraStore.Add(leafs[i]);
            foreach (string e in extra ?? new string[0])
                dotnet.ChainPolicy.ExtraStore.Add(SX.X509Certificate2.CreateFromPem(pems[e]));
            bool theirs = dotnet.Build(leafs[0]);

            // ours
            var zleafs = new ZX.X509Certificate2Collection();
            zleafs.ImportFromPem(pems[leafName]);
            var chain = new ZX.X509Chain();
            chain.ChainPolicy.TrustMode = ZX.X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ZX.X509Certificate2.CreateFromPem(pems[rootName]));
            chain.ChainPolicy.RevocationMode = ZX.X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = new CalendarTime { Year = at.Year, Month = at.Month, Day = at.Day, Hour = at.Hour };
            chain.ChainPolicy.VerificationTimeIgnored = false;
            if (server)
                chain.ChainPolicy.ApplicationPolicy.Add(new Z.Oid("1.3.6.1.5.5.7.3.1"));
            for (int i = 1; i < zleafs.Count; i++)
                chain.ChainPolicy.ExtraStore.Add(zleafs[i]);
            foreach (string e in extra ?? new string[0])
                chain.ChainPolicy.ExtraStore.Add(ZX.X509Certificate2.CreateFromPem(pems[e]));
            bool ours = chain.Build(zleafs[0]);

            // SHA-1 signatures are refused here, as browsers do; .NET still takes them.
            if (leafName == "Sha1Pem")
                theirs = false;
            if (ours != theirs && failures++ == 0)
            {
                string why = "";
                foreach (var s in dotnet.ChainStatus)
                    why += s.Status + " ";
                first = $"{leafName} under {rootName} at {at:yyyy-MM-dd}: ours {ours}, .NET {theirs} ({why.Trim()})";
            }
        }
        Report("X.509 chains vs .NET", scenarios.Length, failures, first);
    }

    // The system's roots (SSL_CERT_FILE, or the bundle's usual places): the real chains
    // must reach one. Skipped where there is no bundle.
    static void X509SystemRoots()
    {
        if (ZX.SystemRoots.Get().Count == 0)
        {
            Console.WriteLine("skip X.509 system roots      (no PEM bundle here)");
            return;
        }
        int failures = 0;
        string first = "";
        foreach (string name in new[] { "LetsEncryptChainPem", "GitHubChainPem" })
        {
            var sent = new ZX.X509Certificate2Collection();
            sent.ImportFromPem(Pems()[name]);
            var chain = new ZX.X509Chain();
            chain.ChainPolicy.RevocationMode = ZX.X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = new CalendarTime { Year = 2026, Month = 11, Day = 28, Hour = 12 };
            chain.ChainPolicy.VerificationTimeIgnored = false;
            for (int i = 1; i < sent.Count; i++)
                chain.ChainPolicy.ExtraStore.Add(sent[i]);
            if (!chain.Build(sent[0]) && failures++ == 0)
                first = $"{name}: {chain.ChainStatus[0].StatusInformation}";
        }
        Report($"X.509 system roots ({ZX.SystemRoots.Get().Count})", 2, failures, first);
    }

    // Damaged public keys: refused or read, never an exception.
    static void KeysDamaged(int iterations)
    {
        using var rsa = S.RSA.Create(2048);
        using var ec = S.ECDsa.Create(S.ECCurve.NamedCurves.nistP256);
        byte[][] keys = { rsa.ExportSubjectPublicKeyInfo(), ec.ExportSubjectPublicKeyInfo(), rsa.ExportRSAPublicKey() };
        int failures = 0;
        string first = "";
        for (int i = 0; i < iterations; i++)
        {
            byte[] damaged = Damage(keys[i % 3]);
            try
            {
                Z.RSA.Create().TryImportSubjectPublicKeyInfo(damaged, out _);
                Z.RSA.Create().TryImportRsaPublicKey(damaged, out _);
                Z.ECDsa.Create().TryImportSubjectPublicKeyInfo(damaged, out _);
            }
            catch (Exception e)
            {
                if (failures++ == 0)
                    first = $"{e.GetType().Name}: {e.Message}";
            }
        }
        Report("keys damaged", iterations, failures, first);
    }
}
