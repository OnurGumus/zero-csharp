// SslStream's HelloRetryRequest path and P-256 key share, against RFC 8448
// section 5: the client offers X25519, the server asks for P-256 (with a
// cookie), and the handshake goes on with the client's second ClientHello.
// Given the RFC's two ClientHellos and keys, everything the client writes
// must be the RFC's bytes: the second ClientHello record, its Finished and
// its close_notify.

using System;
using ZN = ZeroTest.Net.Security;
using ZX = ZeroTest.X509Certificates;

static partial class Program
{
    const string RetryClientHello1 =
        "16030100b4010000b00303b0b1c5a5aa37c5919f2ed1d5c6fff7fcb7849716945a2b8cee9258a346677b6f00000613011303" +
        "1302010000810000000b0009000006736572766572ff01000100000a00080006001d00170018003300260024001d0020e8e8" +
        "e3f3b93a25ed97a14a7dcacb8a272c6288e585c6484d05262fcad062ad1f002b0003020304000d0020001e04030503060302" +
        "0308040805080604010501060102010402050206020202002d00020101001c00024001";

    const string RetryHelloRetryRequest =
        "16030300b0020000ac0303cf21ad74e59a6111be1d8c021e65b891c2a211167abb8c5e079e09e2c8a8339c00130100008400" +
        "3300020017002c0074007271dcd04bb88bc3189119398a00000000eefafc76c146b823b096f8aacad365dd0030953f4edf62" +
        "5636e5f21bb2e23fcc654b1b5b40318d10d137abcbb87574e36e8a1f025f7dfa5d6e50781b5eda4aa15b0c8be778257d16aa" +
        "3030e9e7841dd9e4c0342267e8ca0caf571fb2b7cff0f934b0002b00020304";

    const string RetryClientHello2 =
        "1603030200010001fc0303b0b1c5a5aa37c5919f2ed1d5c6fff7fcb7849716945a2b8cee9258a346677b6f00000613011303" +
        "1302010001cd0000000b0009000006736572766572ff01000100000a00080006001d001700180033004700450017004104a6" +
        "da7392ec591e17abfd535964b99894d13befb221b3def2ebe3830eac8f0151812677c4d6d2237e85cf01d6910cfb83954e76" +
        "ba7352830534159897e8065780002b0003020304000d0020001e040305030603020308040805080604010501060102010402" +
        "050206020202002c0074007271dcd04bb88bc3189119398a00000000eefafc76c146b823b096f8aacad365dd0030953f4edf" +
        "625636e5f21bb2e23fcc654b1b5b40318d10d137abcbb87574e36e8a1f025f7dfa5d6e50781b5eda4aa15b0c8be778257d16" +
        "aa3030e9e7841dd9e4c0342267e8ca0caf571fb2b7cff0f934b0002d00020101001c00024001001500af0000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000";

    const string RetryServerHello =
        "160303007b020000770303bb341d847fd789c47c387172dc0c9bf147fccacb5043d86ca4c598d3ff571b9800130100004f00" +
        "3300450017004104583e054b7a66672ae020ad9d2686fcc85b5ad41a134a0f03ee72b893052bd85b4c8de6776f5b04ac07d8" +
        "3540eab3e3d9c547bc6528c4317d294686093a6cad7d002b00020304";

    const string RetryServerFlight =
        "170303029699bee20baf5b7fc727bfab6223928a381e6d0cf9c4da653f9d2a7b23f7de11cce842d5cf75631763450ffb8b0c" +
        "c1d238e658af7a12adc86243114ab14a1da2fae42621ce483fb6242eabfaad52566b02b31d2eddedefeb80e66a9900d5f973" +
        "b40c4fdf74719ecf1b68d7f9c3b6ceb903ca13dd1bb8f8187ae33417e1d152522c5822a1a03ad52c838c55953d610222874c" +
        "ce8e1790b229a2aa0b53c8d377ee720182951dc6181dc5d90bd1f0105ed1e84aa5f75957c6661897079e5ea5007449e3197b" +
        "dc7c9beeedddeafdd844afa5c315ecfe65e576afe909812880620ec7048b42d7f5c78d76f299d6d82534bdd8f512febc0ed3" +
        "814aca470cd8000d3e1cb9962b052fbb950df683a52c2ba77ed3713b122937a6e5170964e2ab7969dcd980b3db9b458da760" +
        "3124d6dc005e4d6e04b4d0c4baf3275db827dbba0a6db09672171fc057b3851d7e026841e2978fbd2346bbefdd0376bb1108" +
        "fe9acc92189f5650aa5e85d8e8c7b67ac510dba003d3d7e16350bb66d45013efd44c9b607c0d318c4c7d1a1f5cbc57e20611" +
        "804e3787d7b4a4b5f08ed8fd70bdaeade02260b12ab842ef690b4a3ee7911e841b374ecd5ebbbc2a54d047b600336dd7d0c8" +
        "8b4bc10e58ee6cb656de7247fa20d8e91deb84628608cf80615b62e96c1491c7ac3755eb6901405d3474fe1ac79d106a0cee" +
        "56c2577fc88480f96cb6b8c681b7b68b53c146093908f350888175bdfb0b1e31ad61e30ba0adfe6d223aa03c0783b5001a57" +
        "587c328a9afcfcfb978d1cd4328f7d9d60530e630befd96c0c816ee20b0100768ae2a6df51fc68f172740a79af11398ee3be" +
        "1252491fa9c693479e877f94ab7c5f8cad480203e6ab7b87dd71e8a0729113df17f5eee86ce108d1d72007ec1cd13c85a6c1" +
        "49621e77b7d78d805a30f0be030c315e54";

    const string RetryClientFinished =
        "1703030035d74f1923c662fd34137c6f502f3dd2b93d951d1b3bc97e42afe23c31abea92fe91b474999e85e3b791ce252fe8" +
        "c3e9f939a4120cb2";

    const string RetryClientClose =
        "17030300132ea6cdf749196023e2b3a49491695536426047";

    const string RetryServerClose =
        "1703030013519fc5075cb0884349759ff9ef6f011bb4c6f2";

    const string RetryX25519Key =
        "0ed02f8e8117efc75ca7ac32aa7e34eda64cdc0ddad154a5e85289f959f63204";

    const string RetryP256Key =
        "ab5473467e19346ceb0a0414e41da21d4d2445bc3025afe97c4e8dc8d513da39";

    static void TlsRetryReplay()
    {
        int failures = 0;
        string first = "";
        void Fail(string what) { if (failures++ == 0) first = what; }
        byte[] server = H(RetryHelloRetryRequest, RetryServerHello, RetryServerFlight, RetryServerClose);
        var wire = new ReplayStream(server);
        var ssl = new ZN.SslStream(wire, true)
        {
            TestClientHello = H(RetryClientHello1).AsSpan(5).ToArray(),
            TestPrivateKey = H(RetryX25519Key),
            TestClientHello2 = H(RetryClientHello2).AsSpan(5).ToArray(),
            TestP256PrivateKey = H(RetryP256Key),
            TestNoChangeCipherSpec = true,
        };
        var policy = new ZX.X509ChainPolicy();
        policy.TrustMode = ZX.X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.Add(new ZX.X509Certificate2(H(ServerCertificate)));
        policy.RevocationMode = ZX.X509RevocationMode.NoCheck;
        policy.VerificationTime = new CalendarTime { Year = 2020, Month = 1, Day = 1 };
        policy.VerificationTimeIgnored = false;
        policy.ApplicationPolicy.Add(new ZeroTest.Oid("2.5.29.37.0"));
        bool ok = ssl.TryAuthenticateAsClient(new ZN.SslClientAuthenticationOptions
        {
            TargetHost = "rsa",
            CertificateChainPolicy = policy,
        }, out string error);
        if (!ok)
            Fail("RFC 8448 HelloRetryRequest handshake: " + error);
        else
        {
            if (Hex(wire.Written.ToArray()) != Hex(H(RetryClientHello1, RetryClientHello2, RetryClientFinished)))
                Fail("the client's records differ from RFC 8448 section 5's");
            byte[] buffer = new byte[16];
            if (ssl.Read(buffer, 0, 16) != 0)
                Fail("close_notify wasn't the end");
            ssl.ShutdownAsync();
            if (Hex(wire.Written.ToArray()) != Hex(H(RetryClientHello1, RetryClientHello2, RetryClientFinished, RetryClientClose)))
                Fail("the client's close_notify differs from RFC 8448 section 5's");
        }
        Report("TLS 1.3 RFC 8448 retry", 1, failures, first);
    }
}
