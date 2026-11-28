// SslStream (lib/SslStream.cs, TlsHandshake.cs, TlsRecords.cs) against RFC
// 8448 section 3: a whole TLS 1.3 handshake written out byte by byte. The
// client is given the RFC's ClientHello and X25519 key, then reads the
// server's records as the RFC has them: everything the client writes must
// be the RFC's bytes exactly (its Finished, its data, its close_notify), and
// the server's data must come out. Then the server's records are damaged at
// random: the handshake must fail cleanly, never with an exception.

using System;
using System.IO;
using ZN = ZeroTest.Net.Security;
using ZX = ZeroTest.X509Certificates;

static partial class Program
{
    // Reads from the server's recorded bytes; keeps what the client writes.
    sealed class ReplayStream : Stream
    {
        readonly MemoryStream _from;
        internal readonly MemoryStream Written = new MemoryStream();
        internal ReplayStream(byte[] server) { _from = new MemoryStream(server); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _from.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    const string ClientHelloRecord =
        "16030100c4010000c00303cb34ecb1e78163ba1c38c6dacb196a6dffa21a8d9912ec18a2ef6283024dece700000613011303" +
        "1302010000910000000b0009000006736572766572ff01000100000a00140012001d00170018001901000101010201030104" +
        "00230000003300260024001d002099381de560e4bd43d23d8e435a7dbafeb3c06e51c13cae4d5413691e529aaf2c002b0003" +
        "020304000d0020001e040305030603020308040805080604010501060102010402050206020202002d00020101001c000240" +
        "01";

    const string ServerHelloRecord =
        "160303005a020000560303a6af06a4121860dc5e6e60249cd34c95930c8ac5cb1434dac155772ed3e2692800130100002e00" +
        "330024001d0020c9828876112095fe66762bdbf7c672e156d6cc253b833df1dd69b1b04e751f0f002b00020304";

    const string ServerFlightRecord =
        "17030302a2d1ff334a56f5bff6594a07cc87b580233f500f45e489e7f33af35edf7869fcf40aa40aa2b8ea73f848a7ca0761" +
        "2ef9f945cb960b4068905123ea78b111b429ba9191cd05d2a389280f526134aadc7fc78c4b729df828b5ecf7b13bd9aefb0e" +
        "57f271585b8ea9bb355c7c79020716cfb9b1183ef3ab20e37d57a6b9d7477609aee6e122a4cf51427325250c7d0e50928944" +
        "4c9b3a648f1d71035d2ed65b0e3cdd0cbae8bf2d0b227812cbb360987255cc744110c453baa4fcd610928d809810e4b7ed1a" +
        "8fd991f06aa6248204797e36a6a73b70a2559c09ead686945ba246ab66e5edd8044b4c6de3fcf2a89441ac66272fd8fb330e" +
        "f8190579b3684596c960bd596eea520a56a8d650f563aad27409960dca63d3e688611ea5e22f4415cf9538d51a200c270342" +
        "72968a264ed6540c84838d89f72c24461aad6d26f59ecaba9acbbb317b66d902f4f292a36ac1b639c637ce343117b6596222" +
        "45317b49eeda0c6258f100d7d961ffb138647e92ea330faeea6dfa31c7a84dc3bd7e1b7a6c7178af36879018e3f252107f24" +
        "3d243dc7339d5684c8b0378bf30244da8c87c843f5e56eb4c5e8280a2b48052cf93b16499a66db7cca71e4599426f7d461e6" +
        "6f99882bd89fc50800becca62d6c74116dbd2972fda1fa80f85df881edbe5a37668936b335583b599186dc5c6918a396fa48" +
        "a181d6b6fa4f9d62d513afbb992f2b992f67f8afe67f76913fa388cb5630c8ca01e0c65d11c66a1e2ac4c85977b7c7a6999b" +
        "bf10dc35ae69f5515614636c0b9b68c19ed2e31c0b3b66763038ebba42f3b38edc0399f3a9f23faa63978c317fc9fa66a73f" +
        "60f0504de93b5b845e275592c12335ee340bbc4fddd502784016e4b3be7ef04dda49f4b440a30cb5d2af939828fd4ae3794e" +
        "44f94df5a631ede42c1719bfdabf0253fe5175be898e750edc53370d2b";

    const string ClientFinishedRecord =
        "170303003575ec4dc238cce60b298044a71e219c56cc77b0517fe9b93c7a4bfc44d87f38f80338ac98fc46deb384bd1caeac" +
        "ab6867d726c40546";

    const string NewSessionTicketRecord =
        "17030300de3a6b8f90414a97d6959c3487680de5134a2b240e6cffac116e95d41d6af8f6b580dcf3d11d63c758db289a0159" +
        "40252f55713e061dc13e078891a38efbcf5753ad8ef170ad3c7353d16d9da773b9ca7f2b9fa1b6c0d4a3d03f75e09c30ba1e" +
        "62972ac46f75f7b981be63439b2999ce13064615139891d5e4c5b406f16e3fc181a77ca475840025db2f0a77f81b5ab05b94" +
        "c01346755f69232c86519d86cbeeac87aac347d143f9605d64f650db4d023e70e952ca49fe5137121c74bc2697687e248746" +
        "d6df353005f3bce18696129c8153556b3b6c6779b37bf15985684f";

    const string ClientDataRecord =
        "1703030043a23f7054b62c94d0affafe8228ba55cbefacea42f914aa66bcab3f2b9819a8a5b46b395bd54a9a20441e2b6297" +
        "4e1f5a6292a2977014bd1e3deae63aeebb21694915e4";

    const string ServerDataRecord =
        "17030300432e937e11ef4ac740e538ad36005fc4a46932fc3225d05f82aa1b36e30efaf97d90e6dffc602dcb501a59a8fcc4" +
        "9c4bf2e5f0a21c0047c2abf332540dd032e167c2955d";

    const string ClientCloseRecord =
        "1703030013c9872760655666b74d7ff1153efd6db6d0b0e3";

    const string ServerCloseRecord =
        "1703030013b58fd67166ebf599d24720cfbe7efa7a8864a9";

    const string ServerCertificate =
        "308201ac30820115a003020102020102300d06092a864886f70d01010b0500300e310c300a06035504031303727361301e17" +
        "0d3136303733303031323335395a170d3236303733303031323335395a300e310c300a0603550403130372736130819f300d" +
        "06092a864886f70d010101050003818d0030818902818100b4bb498f8279303d980836399b36c6988c0c68de55e1bdb826d3" +
        "901a2461eafd2de49a91d015abbc9a95137ace6c1af19eaa6af98c7ced43120998e187a80ee0ccb0524b1b018c3e0b63264d" +
        "449a6d38e22a5fda430846748030530ef0461c8ca9d9efbfae8ea6d1d03e2bd193eff0ab9a8002c47428a6d35a8d88d79f7f" +
        "1e3f0203010001a31a301830090603551d1304023000300b0603551d0f0404030205a0300d06092a864886f70d01010b0500" +
        "0381810085aad2a0e5b9276b908c65f73a7267170618a54c5f8a7b337d2df7a594365417f2eae8f8a58c8f8172f9319cf36b" +
        "7fd6c55b80f21a03015156726096fd335e5e67f2dbf102702e608ccae6bec1fc63a42a99be5c3eb7107c3c54e9b9eb2bd520" +
        "3b1c3b84e0a8b2f759409ba3eac9d91d402dcc0cc8f8961229ac9187b42b4de1";

    const string ClientPrivateKey = "49af42ba7f7994852d713ef2784bcbcaa7911de26adc5642cb634540e7ea5005";

    static byte[] H(params string[] hex) => Convert.FromHexString(string.Concat(hex));

    static ZN.SslStream ReplayClient(byte[] server, out ReplayStream wire, out bool ok, out string error)
    {
        wire = new ReplayStream(server);
        var ssl = new ZN.SslStream(wire, true)
        {
            TestClientHello = H(ClientHelloRecord).AsSpan(5).ToArray(),
            TestPrivateKey = H(ClientPrivateKey),
            TestNoChangeCipherSpec = true,
        };
        var policy = new ZX.X509ChainPolicy();
        policy.TrustMode = ZX.X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.Add(new ZX.X509Certificate2(H(ServerCertificate)));
        policy.RevocationMode = ZX.X509RevocationMode.NoCheck;
        policy.VerificationTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        policy.VerificationTimeIgnored = false;
        // The RFC's certificate is self-signed, for "rsa", with no extensions.
        policy.ApplicationPolicy.Add(new ZeroTest.Oid("2.5.29.37.0"));
        ok = ssl.TryAuthenticateAsClient(new ZN.SslClientAuthenticationOptions
        {
            TargetHost = "rsa",
            CertificateChainPolicy = policy,
        }, out error);
        return ssl;
    }

    // A TLS 1.3 record: header, then the data and its content type, sealed.
    static byte[] SealRecord(ZN.TlsCipher cipher, byte type, byte[] data)
    {
        byte[] record = new byte[5 + data.Length + 1 + 16];
        record[0] = 23; record[1] = 3; record[2] = 3;
        record[3] = (byte)((data.Length + 17) >> 8); record[4] = (byte)(data.Length + 17);
        data.CopyTo(record, 5);
        record[5 + data.Length] = type;
        cipher.Seal(record.AsSpan(5, data.Length + 1), record.AsSpan(6 + data.Length, 16), record.AsSpan(0, 5));
        return record;
    }

    // Opens the record at offset: its data and content type, or null.
    static byte[] OpenRecord(ZN.TlsCipher cipher, byte[] wire, int offset, out int next)
    {
        next = offset;
        if (wire.Length < offset + 5)
            return null;
        int length = wire[offset + 3] << 8 | wire[offset + 4];
        next = offset + 5 + length;
        if (wire.Length < next)
            return null;
        byte[] text = wire.AsSpan(offset + 5, length - 16).ToArray();
        return cipher.Open(text, wire.AsSpan(next - 16, 16), wire.AsSpan(offset, 5)) ? text : null;
    }

    static void TlsReplay(int iterations)
    {
        int failures = 0, runs = 0;
        string first = "";
        void Fail(string what) { if (failures++ == 0) first = what; }

        byte[] server = H(ServerHelloRecord, ServerFlightRecord, NewSessionTicketRecord, ServerDataRecord, ServerCloseRecord);
        var ssl = ReplayClient(server, out ReplayStream wire, out bool ok, out string error);
        runs++;
        if (!ok)
            Fail("RFC 8448 handshake: " + error);
        else
        {
            if (Hex(wire.Written.ToArray()) != Hex(H(ClientHelloRecord, ClientFinishedRecord)))
                Fail("the ClientHello or client Finished records differ from RFC 8448's");
            if (ssl.NegotiatedCipherSuite != ZN.TlsCipherSuite.TLS_AES_128_GCM_SHA256)
                Fail("cipher suite");
            if (ssl.RemoteCertificate.Subject != "CN=rsa")
                Fail("RemoteCertificate");
            byte[] data = new byte[50];
            for (int i = 0; i < 50; i++)
                data[i] = (byte)i;
            ssl.Write(data);
            byte[] back = new byte[100];
            int n = ssl.Read(back, 0, back.Length);
            if (n != 50 || Hex(back.AsSpan(0, 50)) != Hex(data))
                Fail("the server's application data didn't come out");
            if (ssl.Read(back, 0, back.Length) != 0)
                Fail("Read after close_notify wasn't 0");
            ssl.ShutdownAsync();
            if (Hex(wire.Written.ToArray()) != Hex(H(ClientHelloRecord, ClientFinishedRecord, ClientDataRecord, ClientCloseRecord)))
                Fail("the client's data or close_notify records differ from RFC 8448's");
        }

        // KeyUpdate: after the RFC's handshake and ticket, the server updates its key
        // and asks the client to update too; the client must answer under its old key,
        // then write under the new one, and read the server's data under its new key.
        {
            var suite = new ZN.TlsSuite(0x1301);
            byte[] serverSecret = H("a11af9f05531f856ad47116b45a950328204b4f44bfb6b3a4b4f1f3fcb631643");
            byte[] clientSecret = H("9e40646ce79a7f9dc05af8889bce6552875afa0b06df0087f792ebb7c17504a5");
            var serverOld = new ZN.TlsCipher(suite, serverSecret);
            SealRecord(serverOld, 22, new byte[5]); // the NewSessionTicket took sequence number 0
            byte[] update = SealRecord(serverOld, 22, new byte[] { 24, 0, 0, 1, 1 });
            byte[] serverNextSecret = new byte[32];
            ZN.TlsKeys.ExpandLabel(suite, serverSecret, "traffic upd"u8, ReadOnlySpan<byte>.Empty, serverNextSecret);
            var serverNew = new ZN.TlsCipher(suite, serverNextSecret);
            byte[] after = SealRecord(serverNew, 23, "after the update"u8.ToArray());
            byte[] close = SealRecord(serverNew, 21, new byte[] { 1, 0 });
            byte[] flight = H(ServerHelloRecord, ServerFlightRecord, NewSessionTicketRecord);
            byte[] all = new byte[flight.Length + update.Length + after.Length + close.Length];
            flight.CopyTo(all, 0);
            update.CopyTo(all, flight.Length);
            after.CopyTo(all, flight.Length + update.Length);
            close.CopyTo(all, flight.Length + update.Length + after.Length);

            var updating = ReplayClient(all, out ReplayStream w, out bool updated, out string updateError);
            runs++;
            if (!updated)
                Fail("KeyUpdate handshake: " + updateError);
            else
            {
                int handshakeBytes = (int)w.Written.Length;
                byte[] got = new byte[100];
                int n = updating.Read(got, 0, got.Length);
                if (n != 16 || System.Text.Encoding.ASCII.GetString(got, 0, n) != "after the update")
                    Fail("data after the server's KeyUpdate didn't come out");
                updating.Write("reply"u8);
                byte[] written = w.Written.ToArray().AsSpan(handshakeBytes).ToArray();
                var clientOld = new ZN.TlsCipher(suite, clientSecret);
                byte[] clientNextSecret = new byte[32];
                ZN.TlsKeys.ExpandLabel(suite, clientSecret, "traffic upd"u8, ReadOnlySpan<byte>.Empty, clientNextSecret);
                var clientNew = new ZN.TlsCipher(suite, clientNextSecret);
                byte[] answer = OpenRecord(clientOld, written, 0, out int next);
                byte[] second = answer == null ? null : OpenRecord(clientNew, written, next, out _);
                if (answer == null || Hex(answer) != "180000010016")
                    Fail("the client's answering KeyUpdate is wrong");
                else if (second == null || System.Text.Encoding.ASCII.GetString(second) != "reply\u0017")
                    Fail("the client didn't write under its updated key");
                if (updating.Read(got, 0, got.Length) != 0)
                    Fail("close_notify under the updated key wasn't the end");
            }
        }

        // Any damage to the server's handshake records: refused, without an exception.
        byte[] handshake = H(ServerHelloRecord, ServerFlightRecord);
        for (int i = 0; i < iterations; i++, runs++)
        {
            byte[] damaged = Damage(handshake);
            try
            {
                ReplayClient(damaged, out _, out bool accepted, out _);
                // Only damage outside anything checked could pass: there is none,
                // except the outer record version byte, which TLS 1.3 ignores.
                if (accepted && Hex(damaged) != Hex(handshake))
                {
                    bool versionOnly = damaged.Length == handshake.Length;
                    for (int b = 0; b < damaged.Length && versionOnly; b++)
                        if (damaged[b] != handshake[b] && b != 2 && b != 97)
                            versionOnly = false;
                    if (!versionOnly)
                        Fail("a damaged handshake was accepted: " + Hex(damaged));
                }
            }
            catch (Exception e)
            {
                Fail($"{e.GetType().Name}: {e.Message}");
            }
        }
        Report("TLS 1.3 RFC 8448 replay", runs, failures, first);
    }
}
