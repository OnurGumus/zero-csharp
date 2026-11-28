// The client side of TLS 1.3's handshake (RFC 8446 section 4), for SslStream:
//
//   ClientHello              ──▶                             (an X25519 key share)
//                           [◀──  HelloRetryRequest          (the server wants P-256)
//   ClientHello              ──▶]                            (a P-256 key share)
//                            ◀──  ServerHello                (in the clear)
//                                 EncryptedExtensions        (handshake keys from here)
//                                 [CertificateRequest]
//                                 Certificate
//                                 CertificateVerify          (the server's key signs the transcript)
//                                 Finished                   (an HMAC of the transcript)
//   [change_cipher_spec]
//   [Certificate (empty)]
//   Finished                 ──▶
//   application data         ◀─▶  application data           (application keys)
//
// Every check is here and none throws: Run returns null, or the reason the
// handshake failed after sending the server the matching alert.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace System.Net.Security;

/// <summary>Builds handshake messages: big-endian numbers and length-prefixed vectors.</summary>
internal sealed class TlsWriter
{
    byte[] _data = new byte[512];
    int _length;

    internal int Length => _length;
    internal ReadOnlySpan<byte> Written => _data.AsSpan(0, _length);

    void Room(int n)
    {
        if (_length + n <= _data.Length)
            return;
        byte[] bigger = new byte[Math.Max(_data.Length * 2, _length + n)];
        _data.AsSpan(0, _length).CopyTo(bigger);
        _data = bigger;
    }

    internal void U8(int value)
    {
        Room(1);
        _data[_length++] = (byte)value;
    }

    internal void U16(int value)
    {
        Room(2);
        _data[_length++] = (byte)(value >> 8);
        _data[_length++] = (byte)value;
    }

    internal void Bytes(ReadOnlySpan<byte> bytes)
    {
        Room(bytes.Length);
        bytes.CopyTo(_data.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Starts a vector with a length of this many bytes (1, 2 or 3); End fills it in.</summary>
    internal int Begin(int lengthBytes)
    {
        Room(lengthBytes);
        int at = _length;
        _length += lengthBytes;
        _data[at] = (byte)lengthBytes; // remembered until End
        return at;
    }

    internal void End(int at)
    {
        int lengthBytes = _data[at];
        int length = _length - at - lengthBytes;
        for (int i = lengthBytes - 1; i >= 0; i--)
        {
            _data[at + i] = (byte)length;
            length >>= 8;
        }
    }

    internal byte[] ToArray() => _data.AsSpan(0, _length).ToArray();
}

/// <summary>Reads handshake messages; a malformed one sets Failed, which sticks.</summary>
internal ref struct TlsReader
{
    ReadOnlySpan<byte> _data;
    bool _failed;

    internal TlsReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _failed = false;
    }

    internal bool Failed => _failed;
    internal bool HasData => !_failed && !_data.IsEmpty;

    void Fail()
    {
        _failed = true;
        _data = default;
    }

    internal int U8()
    {
        if (_data.Length < 1) { Fail(); return 0; }
        int v = _data[0];
        _data = _data.Slice(1);
        return v;
    }

    internal int U16()
    {
        if (_data.Length < 2) { Fail(); return 0; }
        int v = _data[0] << 8 | _data[1];
        _data = _data.Slice(2);
        return v;
    }

    internal ReadOnlySpan<byte> Bytes(int n)
    {
        if (n < 0 || _data.Length < n) { Fail(); return default; }
        ReadOnlySpan<byte> v = _data.Slice(0, n);
        _data = _data.Slice(n);
        return v;
    }

    /// <summary>A vector with a 1-, 2- or 3-byte length.</summary>
    internal ReadOnlySpan<byte> Vector(int lengthBytes)
    {
        int length = 0;
        for (int i = 0; i < lengthBytes; i++)
            length = length << 8 | U8();
        return Bytes(length);
    }

    internal void End()
    {
        if (!_data.IsEmpty)
            Fail();
    }
}

/// <summary>One run of the client handshake for an SslStream.</summary>
internal sealed class TlsClientHandshake
{
    const byte ClientHelloType = 1, ServerHelloType = 2, EncryptedExtensionsType = 8, CertificateType = 11;
    const byte CertificateRequestType = 13, CertificateVerifyType = 15, FinishedType = 20;
    const int X25519Group = 0x001d;
    const int P256Group = 0x0017;

    readonly SslStream _stream;
    readonly SslClientAuthenticationOptions _options;
    readonly TlsRecordLayer _records;

    Bytes32 _privateKey;          // X25519
    Bytes32 _p256PrivateKey;      // P-256, after a HelloRetryRequest
    int _group = X25519Group;     // the key share's group
    byte[] _random;
    byte[] _sessionId;
    byte[] _clientHello;
    TlsSuite _suite;
    IncrementalHash _transcript;
    byte[] _handshakeSecret, _clientSecret, _serverSecret;
    SslApplicationProtocol _alpn;
    bool _certificateRequested;
    int[] _offeredExtensions;
    byte[] _requestContext;
    X509Certificate2[] _certificates;

    // The RFC 8446 4.1.3 marker of a HelloRetryRequest: SHA-256("HelloRetryRequest").
    static ReadOnlySpan<byte> HelloRetryRandom =>
    [
        0xCF, 0x21, 0xAD, 0x74, 0xE5, 0x9A, 0x61, 0x11, 0xBE, 0x1D, 0x8C, 0x02, 0x1E, 0x65, 0xB8, 0x91,
        0xC2, 0xA2, 0x11, 0x16, 0x7A, 0xBB, 0x8C, 0x5E, 0x07, 0x9E, 0x09, 0xE2, 0xC8, 0xA8, 0x33, 0x9C,
    ];

    // Signature schemes offered (RFC 8446 4.2.3): ECDSA and RSA-PSS for the handshake,
    // RSA PKCS #1 too because certificates are signed with it.
    static ReadOnlySpan<byte> SignatureSchemes =>
    [
        0x04, 0x03, 0x05, 0x03, 0x08, 0x04, 0x08, 0x05, 0x08, 0x06, 0x04, 0x01, 0x05, 0x01, 0x06, 0x01,
    ];

    internal TlsClientHandshake(SslStream stream, SslClientAuthenticationOptions options)
    {
        _stream = stream;
        _options = options;
        _records = stream.Records;
    }

    string Fail(byte alert, string message)
    {
        if (alert != 0)
            _records.SendAlert(alert);
        return message;
    }

    byte[] Next(out string error)
    {
        byte[] message = _stream.NextHandshakeMessage(out error, out byte alert);
        if (message == null && alert != 0)
            _records.SendAlert(alert);
        return message;
    }

    /// <summary>The handshake: null when the connection is ready, else why it failed.</summary>
    internal string Run()
    {
        // ClientHello, with an X25519 key share (and P-256 among the groups offered).
        Span<byte> publicKey = stackalloc byte[65];
        if (_stream.TestClientHello != null)
        {
            _clientHello = _stream.TestClientHello;
            _stream.TestPrivateKey.AsSpan().CopyTo(_privateKey);
            _sessionId = _clientHello.AsSpan(39, _clientHello[38]).ToArray();
        }
        else
        {
            _random = new byte[32];
            RandomNumberGenerator.Fill(_random);
            // A session ID of 32 random bytes: "middlebox compatibility mode" (RFC 8446 D.4),
            // which some networks need to let TLS 1.3 through.
            _sessionId = new byte[32];
            RandomNumberGenerator.Fill(_sessionId);
            X25519.GeneratePrivateKey(_privateKey);
            X25519.GetPublicKey(_privateKey, publicKey.Slice(0, 32));
            _clientHello = BuildClientHello(X25519Group, publicKey.Slice(0, 32), ReadOnlySpan<byte>.Empty);
        }
        _offeredExtensions = ExtensionTypes(_clientHello);
        _records.Write(TlsRecordLayer.Handshake, _clientHello);
        _records.Flush();

        // ServerHello, or a HelloRetryRequest
        byte[] serverHello = Next(out string error);
        if (serverHello == null)
            return error;
        if (serverHello[0] != ServerHelloType)
            return Fail(TlsAlert.UnexpectedMessage, "the server's first message wasn't a ServerHello");
        string problem = ReadServerHello(serverHello, out bool retry, out int group,
            out ReadOnlySpan<byte> serverShare, out ReadOnlySpan<byte> cookie);
        if (problem != null)
            return problem;
        if (retry)
        {
            // The server wants a P-256 key share instead (RFC 8446 4.1.4). The
            // transcript starts with the first ClientHello's hash in its place.
            if (group != P256Group)
                return Fail(TlsAlert.IllegalParameter, "the server asked for a key share for a group that wasn't offered");
            ushort retrySuite = _suite.Code;
            _transcript = IncrementalHash.CreateHash(_suite.HashName);
            Span<byte> messageHash = stackalloc byte[4 + _suite.HashSize];
            messageHash[0] = 254; // message_hash
            messageHash[1] = 0;
            messageHash[2] = 0;
            messageHash[3] = (byte)_suite.HashSize;
            HashState.Hash(_suite.Hash, _clientHello, messageHash.Slice(4));
            _transcript.AppendData(messageHash);
            _transcript.AppendData(serverHello);
            CryptographicOperations.ZeroMemory(_privateKey);

            _group = P256Group;
            if (_stream.TestClientHello2 != null)
            {
                _clientHello = _stream.TestClientHello2;
                _stream.TestP256PrivateKey.AsSpan().CopyTo(_p256PrivateKey);
            }
            else
            {
                P256Ecdh.GenerateKey(_p256PrivateKey, publicKey);
                _clientHello = BuildClientHello(P256Group, publicKey, cookie);
            }
            _offeredExtensions = ExtensionTypes(_clientHello);
            _records.Write(TlsRecordLayer.Handshake, _clientHello);
            _records.Flush();

            serverHello = Next(out error);
            if (serverHello == null)
                return error;
            if (serverHello[0] != ServerHelloType)
                return Fail(TlsAlert.UnexpectedMessage, "a ServerHello didn't come after the second ClientHello");
            problem = ReadServerHello(serverHello, out retry, out group, out serverShare, out _);
            if (problem != null)
                return problem;
            if (retry)
                return Fail(TlsAlert.UnexpectedMessage, "the server sent a second HelloRetryRequest");
            if (_suite.Code != retrySuite)
                return Fail(TlsAlert.IllegalParameter, "the ServerHello's cipher suite isn't the HelloRetryRequest's");
        }
        else
            _transcript = IncrementalHash.CreateHash(_suite.HashName);
        _transcript.AppendData(_clientHello);
        _transcript.AppendData(serverHello);
        if (group != _group)
            return Fail(TlsAlert.IllegalParameter, "the server answered with a key share for another group");

        // The shared secret, and the handshake keys from it.
        int h = _suite.HashSize;
        Span<byte> shared = stackalloc byte[32];
        bool agreed = _group == X25519Group
            ? serverShare.Length == 32 && X25519.TryGetSharedSecret(_privateKey, serverShare, shared)
            : P256Ecdh.TryGetSharedSecret(_p256PrivateKey, serverShare, shared);
        if (!agreed)
            return Fail(TlsAlert.IllegalParameter, "the server's key share isn't a usable key");
        Span<byte> early = stackalloc byte[h];
        Span<byte> zeros = stackalloc byte[h];
        zeros.Clear();
        HKDF.Extract(_suite.HashName, zeros, ReadOnlySpan<byte>.Empty, early);
        _handshakeSecret = new byte[h];
        TlsKeys.Next(_suite, early, shared, _handshakeSecret);
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(_privateKey);
        CryptographicOperations.ZeroMemory(_p256PrivateKey);
        Span<byte> hash = stackalloc byte[h];
        _transcript.GetCurrentHash(hash);
        _clientSecret = new byte[h];
        _serverSecret = new byte[h];
        TlsKeys.DeriveSecret(_suite, _handshakeSecret, "c hs traffic"u8, hash, _clientSecret);
        TlsKeys.DeriveSecret(_suite, _handshakeSecret, "s hs traffic"u8, hash, _serverSecret);
        if (!_stream.HandshakeBufferEmpty)
            return Fail(TlsAlert.UnexpectedMessage, "the ServerHello's record carried more than it");
        _records.SetReadKey(_suite, _serverSecret);

        // EncryptedExtensions
        byte[] message = Next(out error);
        if (message == null)
            return error;
        if (message[0] != EncryptedExtensionsType)
            return Fail(TlsAlert.UnexpectedMessage, "EncryptedExtensions didn't come after the ServerHello");
        if ((problem = ReadEncryptedExtensions(message)) != null)
            return problem;
        _transcript.AppendData(message);

        // [CertificateRequest], Certificate
        message = Next(out error);
        if (message == null)
            return error;
        if (message[0] == CertificateRequestType)
        {
            TlsReader request = new TlsReader(message.AsSpan(4));
            _requestContext = request.Vector(1).ToArray();
            request.Vector(2);
            request.End();
            if (request.Failed)
                return Fail(TlsAlert.DecodeError, "a malformed CertificateRequest");
            _certificateRequested = true;
            _transcript.AppendData(message);
            message = Next(out error);
            if (message == null)
                return error;
        }
        if (message[0] != CertificateType)
            return Fail(TlsAlert.UnexpectedMessage, "the server didn't send its certificate");
        if ((problem = ReadCertificate(message)) != null)
            return problem;
        _transcript.AppendData(message);

        // CertificateVerify: the server's key signed the transcript so far.
        _transcript.GetCurrentHash(hash);
        message = Next(out error);
        if (message == null)
            return error;
        if (message[0] != CertificateVerifyType)
            return Fail(TlsAlert.UnexpectedMessage, "CertificateVerify didn't come after the certificate");
        if ((problem = CheckCertificateVerify(message, hash)) != null)
            return problem;
        _transcript.AppendData(message);

        // Finished: an HMAC of the transcript under the server's handshake secret.
        _transcript.GetCurrentHash(hash);
        message = Next(out error);
        if (message == null)
            return error;
        if (message[0] != FinishedType)
            return Fail(TlsAlert.UnexpectedMessage, "Finished didn't come after CertificateVerify");
        Span<byte> expected = stackalloc byte[h];
        TlsKeys.Finished(_suite, _serverSecret, hash, expected);
        if (message.Length != 4 + h || !CryptographicOperations.FixedTimeEquals(message.AsSpan(4), expected))
            return Fail(TlsAlert.DecryptError, "the server's Finished doesn't match the handshake");
        _transcript.AppendData(message);
        if (!_stream.HandshakeBufferEmpty)
            return Fail(TlsAlert.UnexpectedMessage, "the server's Finished record carried more than it");

        // The application secrets, from the transcript up to the server's Finished.
        _transcript.GetCurrentHash(hash);
        Span<byte> master = stackalloc byte[h];
        TlsKeys.Next(_suite, _handshakeSecret, zeros, master);
        byte[] clientApplication = new byte[h], serverApplication = new byte[h];
        TlsKeys.DeriveSecret(_suite, master, "c ap traffic"u8, hash, clientApplication);
        TlsKeys.DeriveSecret(_suite, master, "s ap traffic"u8, hash, serverApplication);
        CryptographicOperations.ZeroMemory(master);

        // Is the certificate one to trust, for this host?
        if ((problem = CheckTrust()) != null)
            return problem;

        // The client's second flight.
        if (!_stream.TestNoChangeCipherSpec)
        {
            Span<byte> changeCipherSpec = stackalloc byte[1];
            changeCipherSpec[0] = 1;
            _records.Write(TlsRecordLayer.ChangeCipherSpec, changeCipherSpec);
        }
        _records.SetWriteKey(_suite, _clientSecret);
        if (_certificateRequested)
        {
            // No client certificate: an empty list, which the server may accept or refuse.
            TlsWriter empty = new TlsWriter();
            empty.U8(CertificateType);
            int length = empty.Begin(3);
            int context = empty.Begin(1);
            empty.Bytes(_requestContext);
            empty.End(context);
            empty.End(empty.Begin(3));
            empty.End(length);
            _records.Write(TlsRecordLayer.Handshake, empty.Written);
            _transcript.AppendData(empty.Written);
        }
        _transcript.GetCurrentHash(hash);
        Span<byte> finished = stackalloc byte[4 + h];
        finished[0] = FinishedType;
        finished[1] = 0;
        finished[2] = 0;
        finished[3] = (byte)h;
        TlsKeys.Finished(_suite, _clientSecret, hash, finished.Slice(4));
        _records.Write(TlsRecordLayer.Handshake, finished);
        _records.Flush();

        _records.SetWriteKey(_suite, clientApplication);
        _records.SetReadKey(_suite, serverApplication);
        _stream.Established(_suite, serverApplication, clientApplication, _certificates[0], _alpn);
        return null;
    }

    // A ClientHello with one key share; a second one (after a HelloRetryRequest)
    // has the same random and session ID, the new share and the server's cookie.
    byte[] BuildClientHello(int group, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> cookie)
    {
        TlsWriter w = new TlsWriter();
        w.U8(ClientHelloType);
        int body = w.Begin(3);
        w.U16(0x0303);
        w.Bytes(_random);
        int sid = w.Begin(1);
        w.Bytes(_sessionId);
        w.End(sid);
        int suites = w.Begin(2);
        w.U16(TlsSuite.Aes128GcmSha256);
        w.U16(TlsSuite.Aes256GcmSha384);
        w.U16(TlsSuite.ChaCha20Poly1305Sha256);
        w.End(suites);
        w.U8(1); // compression methods: null only
        w.U8(0);
        int extensions = w.Begin(2);

        // server_name, unless the host is an IP address (RFC 6066 3).
        string host = HostForSni(_options.TargetHost);
        if (host != null)
        {
            w.U16(0);
            int ext = w.Begin(2);
            int list = w.Begin(2);
            w.U8(0); // host_name
            int name = w.Begin(2);
            foreach (char c in host)
                w.U8(c);
            w.End(name);
            w.End(list);
            w.End(ext);
        }
        // supported_groups
        w.U16(10);
        int groups = w.Begin(2);
        int groupList = w.Begin(2);
        w.U16(X25519Group);
        w.U16(P256Group);
        w.End(groupList);
        w.End(groups);
        // signature_algorithms
        w.U16(13);
        int signatures = w.Begin(2);
        int schemeList = w.Begin(2);
        w.Bytes(SignatureSchemes);
        w.End(schemeList);
        w.End(signatures);
        // application_layer_protocol_negotiation
        SslApplicationProtocol[] protocols = _options.ApplicationProtocols;
        if (protocols != null && protocols.Length > 0)
        {
            w.U16(16);
            int alpn = w.Begin(2);
            int names = w.Begin(2);
            foreach (SslApplicationProtocol p in protocols)
            {
                int one = w.Begin(1);
                w.Bytes(p.Bytes);
                w.End(one);
            }
            w.End(names);
            w.End(alpn);
        }
        // supported_versions: TLS 1.3 only
        w.U16(43);
        int versions = w.Begin(2);
        int versionList = w.Begin(1);
        w.U16(0x0304);
        w.End(versionList);
        w.End(versions);
        // cookie, echoed from a HelloRetryRequest
        if (!cookie.IsEmpty)
        {
            w.U16(44);
            int ext = w.Begin(2);
            int value = w.Begin(2);
            w.Bytes(cookie);
            w.End(value);
            w.End(ext);
        }
        // key_share: one key, X25519 (or P-256 when the server asked for it)
        w.U16(51);
        int share = w.Begin(2);
        int shares = w.Begin(2);
        w.U16(group);
        int key = w.Begin(2);
        w.Bytes(publicKey);
        w.End(key);
        w.End(shares);
        w.End(share);

        w.End(extensions);
        w.End(body);
        return w.ToArray();
    }

    // The host name to send, or null for an IP address or no name. A final dot goes.
    static string HostForSni(string host)
    {
        if (string.IsNullOrEmpty(host) || IPAddress.TryParse(host, out _))
            return null;
        if (host[host.Length - 1] == '.')
            host = host.Substring(0, host.Length - 1);
        if (host.Length == 0 || host.Length > 253)
            return null;
        foreach (char c in host)
            if (c <= ' ' || c > '~')
                return null; // not ASCII: international names must be given as punycode
        return host;
    }

    // The extension types a ClientHello offers.
    static int[] ExtensionTypes(byte[] clientHello)
    {
        TlsReader r = new TlsReader(clientHello.AsSpan(4));
        r.Bytes(2 + 32);
        r.Vector(1);
        r.Vector(2);
        r.Vector(1);
        TlsReader e = new TlsReader(r.Vector(2));
        int[] types = new int[32];
        int count = 0;
        while (e.HasData && count < types.Length)
        {
            types[count++] = e.U16();
            e.Vector(2);
        }
        return types.AsSpan(0, count).ToArray();
    }

    bool Offered(int type)
    {
        foreach (int t in _offeredExtensions)
            if (t == type)
                return true;
        return false;
    }

    // A ServerHello, or a HelloRetryRequest (retry): the group, and the share or cookie.
    string ReadServerHello(byte[] message, out bool retry, out int group,
        out ReadOnlySpan<byte> serverShare, out ReadOnlySpan<byte> cookie)
    {
        retry = false;
        group = 0;
        serverShare = default;
        cookie = default;
        TlsReader r = new TlsReader(message.AsSpan(4));
        int legacyVersion = r.U16();
        ReadOnlySpan<byte> random = r.Bytes(32);
        ReadOnlySpan<byte> sessionId = r.Vector(1);
        int suite = r.U16();
        int compression = r.U8();
        ReadOnlySpan<byte> extensions = r.Vector(2);
        r.End();
        if (r.Failed || legacyVersion != 0x0303 || compression != 0)
        {
            // An old server answers with its own version here, and no extensions.
            if (!r.Failed && legacyVersion < 0x0303)
                return Fail(TlsAlert.ProtocolVersion, "the server speaks only TLS 1.1 or older");
            return Fail(TlsAlert.DecodeError, "a malformed ServerHello");
        }
        retry = DerReader.Same(random, HelloRetryRandom);
        if (!DerReader.Same(sessionId, _sessionId))
            return Fail(TlsAlert.IllegalParameter, "the ServerHello doesn't echo the session ID");
        if (!TlsSuite.IsKnown((ushort)suite))
            return Fail(TlsAlert.IllegalParameter, "the server chose a cipher suite that wasn't offered");
        _suite = new TlsSuite((ushort)suite);

        bool version13 = false, haveShare = false;
        TlsReader e = new TlsReader(extensions);
        int seen = 0;
        while (e.HasData)
        {
            int type = e.U16();
            TlsReader data = new TlsReader(e.Vector(2));
            if (type == 43)
            {
                if ((seen & 1) != 0) return Fail(TlsAlert.IllegalParameter, "a ServerHello extension came twice");
                seen |= 1;
                version13 = data.U16() == 0x0304;
            }
            else if (type == 51)
            {
                if ((seen & 2) != 0) return Fail(TlsAlert.IllegalParameter, "a ServerHello extension came twice");
                seen |= 2;
                group = data.U16();
                if (!retry)
                    serverShare = data.Vector(2); // a HelloRetryRequest names only the group
                haveShare = true;
            }
            else if (type == 44 && retry)
            {
                if ((seen & 4) != 0) return Fail(TlsAlert.IllegalParameter, "a ServerHello extension came twice");
                seen |= 4;
                cookie = data.Vector(2);
                if (cookie.IsEmpty)
                    return Fail(TlsAlert.DecodeError, "an empty cookie");
            }
            else
                return Fail(TlsAlert.UnsupportedExtension, "the ServerHello has an extension that wasn't offered");
            data.End();
            if (data.Failed)
                return Fail(TlsAlert.DecodeError, "a malformed ServerHello extension");
        }
        if (e.Failed)
            return Fail(TlsAlert.DecodeError, "malformed ServerHello extensions");
        if (!version13)
        {
            // A TLS 1.2 server marks its random this way when it saw TLS 1.3 offered (4.1.3).
            return Fail(TlsAlert.ProtocolVersion, "the server doesn't speak TLS 1.3 (only TLS 1.2 or older)");
        }
        if (retry)
        {
            // It must ask for something: a key share for a group not sent yet.
            if (!haveShare)
                return Fail(TlsAlert.IllegalParameter, "a HelloRetryRequest that asks for no new key share");
            if (group == _group)
                return Fail(TlsAlert.IllegalParameter, "a HelloRetryRequest for the key share already sent");
            return null;
        }
        if (!haveShare)
            return Fail(TlsAlert.MissingExtension, "the ServerHello has no key share");
        return null;
    }

    string ReadEncryptedExtensions(byte[] message)
    {
        TlsReader r = new TlsReader(message.AsSpan(4));
        TlsReader e = new TlsReader(r.Vector(2));
        r.End();
        int seen = 0;
        while (e.HasData)
        {
            int type = e.U16();
            ReadOnlySpan<byte> data = e.Vector(2);
            if (!Offered(type) || type == 43 || type == 51 || type == 13)
                return Fail(TlsAlert.UnsupportedExtension, "EncryptedExtensions has an extension that wasn't offered");
            int bit = 1 << (type & 31);
            if ((seen & bit) != 0 && (type == 0 || type == 10 || type == 16))
                return Fail(TlsAlert.IllegalParameter, "an extension came twice");
            seen |= bit;
            if (type == 16)
            {
                // ALPN: exactly one of the names offered.
                TlsReader list = new TlsReader(data);
                TlsReader names = new TlsReader(list.Vector(2));
                list.End();
                ReadOnlySpan<byte> name = names.Vector(1);
                names.End();
                if (list.Failed || names.Failed || name.IsEmpty)
                    return Fail(TlsAlert.DecodeError, "a malformed ALPN answer");
                bool offered = false;
                SslApplicationProtocol[] protocols = _options.ApplicationProtocols;
                if (protocols != null)
                    foreach (SslApplicationProtocol p in protocols)
                        if (DerReader.Same(p.Bytes, name))
                        {
                            _alpn = p;
                            offered = true;
                        }
                if (!offered)
                    return Fail(TlsAlert.IllegalParameter, "the server chose an application protocol that wasn't offered");
            }
            else if (type == 0 && !data.IsEmpty)
                return Fail(TlsAlert.DecodeError, "a malformed server_name answer");
        }
        if (r.Failed || e.Failed)
            return Fail(TlsAlert.DecodeError, "malformed EncryptedExtensions");
        return null;
    }

    string ReadCertificate(byte[] message)
    {
        TlsReader r = new TlsReader(message.AsSpan(4));
        ReadOnlySpan<byte> context = r.Vector(1);
        TlsReader list = new TlsReader(r.Vector(3));
        r.End();
        if (r.Failed || !context.IsEmpty)
            return Fail(TlsAlert.DecodeError, "a malformed Certificate message");
        X509Certificate2[] certificates = new X509Certificate2[4];
        int count = 0;
        while (list.HasData)
        {
            ReadOnlySpan<byte> der = list.Vector(3);
            list.Vector(2); // per-certificate extensions (OCSP, SCTs): not used
            if (list.Failed)
                return Fail(TlsAlert.DecodeError, "a malformed Certificate message");
            if (!X509Certificate2.TryLoad(der, out X509Certificate2 certificate))
                return Fail(TlsAlert.BadCertificate, "the server sent a certificate this can't read");
            if (count == certificates.Length)
            {
                if (count == 32)
                    return Fail(TlsAlert.BadCertificate, "the server sent more than 32 certificates");
                X509Certificate2[] bigger = new X509Certificate2[count * 2];
                certificates.AsSpan().CopyTo(bigger);
                certificates = bigger;
            }
            certificates[count++] = certificate;
        }
        if (list.Failed)
            return Fail(TlsAlert.DecodeError, "a malformed Certificate message");
        if (count == 0)
            return Fail(TlsAlert.DecodeError, "the server sent no certificate");
        _certificates = certificates.AsSpan(0, count).ToArray();
        return null;
    }

    string CheckCertificateVerify(byte[] message, ReadOnlySpan<byte> transcriptHash)
    {
        TlsReader r = new TlsReader(message.AsSpan(4));
        int scheme = r.U16();
        ReadOnlySpan<byte> signature = r.Vector(2);
        r.End();
        if (r.Failed)
            return Fail(TlsAlert.DecodeError, "a malformed CertificateVerify");

        // What was signed: 64 spaces, a context string, a zero byte, the transcript hash.
        ReadOnlySpan<byte> label = "TLS 1.3, server CertificateVerify"u8;
        Span<byte> content = stackalloc byte[64 + label.Length + 1 + transcriptHash.Length];
        content.Slice(0, 64).Fill(0x20);
        label.CopyTo(content.Slice(64));
        content[64 + label.Length] = 0;
        transcriptHash.CopyTo(content.Slice(65 + label.Length));

        X509Certificate2 leaf = _certificates[0];
        bool ok;
        switch (scheme)
        {
            case 0x0403:
            case 0x0503:
            {
                ECDsa key = leaf.GetECDsaPublicKey();
                int bits = scheme == 0x0403 ? 256 : 384;
                if (key == null || key.KeySize != bits)
                    return Fail(TlsAlert.IllegalParameter, "the server signed with a scheme that doesn't fit its key");
                ok = key.VerifyData(content, signature, scheme == 0x0403 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA384,
                    DSASignatureFormat.Rfc3279DerSequence);
                break;
            }
            case 0x0804:
            case 0x0805:
            case 0x0806:
            {
                RSA key = leaf.GetRSAPublicKey();
                if (key == null)
                    return Fail(TlsAlert.IllegalParameter, "the server signed with a scheme that doesn't fit its key");
                HashAlgorithmName name = scheme == 0x0804 ? HashAlgorithmName.SHA256
                    : scheme == 0x0805 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA512;
                ok = key.VerifyData(content, signature, name, RSASignaturePadding.Pss);
                break;
            }
            default:
                return Fail(TlsAlert.IllegalParameter, "the server signed with a scheme that wasn't offered");
        }
        if (!ok)
            return Fail(TlsAlert.DecryptError, "the server's signature over the handshake doesn't verify (CertificateVerify)");
        return null;
    }

    // X509Chain, the host name, then the callback if there is one (as .NET's SslStream does).
    string CheckTrust()
    {
        X509Certificate2 leaf = _certificates[0];
        X509Chain chain = new X509Chain();
        if (_options.CertificateChainPolicy != null)
            chain.ChainPolicy = _options.CertificateChainPolicy.Clone();
        else
            chain.ChainPolicy.RevocationMode = _options.CertificateRevocationCheckMode;
        if (chain.ChainPolicy.ApplicationPolicy.Count == 0)
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        for (int i = 1; i < _certificates.Length; i++)
            chain.ChainPolicy.ExtraStore.Add(_certificates[i]);

        SslPolicyErrors errors = SslPolicyErrors.None;
        if (!chain.Build(leaf))
            errors |= SslPolicyErrors.RemoteCertificateChainErrors;
        string host = _options.TargetHost;
        if (!string.IsNullOrEmpty(host) && !leaf.MatchesHostname(host))
            errors |= SslPolicyErrors.RemoteCertificateNameMismatch;

        RemoteCertificateValidationCallback callback = _options.RemoteCertificateValidationCallback ?? _stream.Callback;
        bool accepted = callback != null ? callback(_stream, leaf, chain, errors) : errors == SslPolicyErrors.None;
        if (accepted)
            return null;

        string why = "";
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            why = string.Concat(" The certificate isn't for ", host, ".");
        foreach (X509ChainStatus status in chain.ChainStatus)
            why = string.Concat(why, " ", status.StatusInformation);
        if (why.Length == 0)
            why = " The validation callback refused it.";
        if (chain.ChainPolicy.TrustMode == X509ChainTrustMode.System && SystemRoots.Get().Count == 0)
            why = string.Concat(why, " No system root certificates were found here: install the CA bundle,"
                + " point SSL_CERT_FILE at one, or give a CertificateChainPolicy with a CustomTrustStore.");
        bool unknownRoot = false, expired = false;
        foreach (X509ChainStatus status in chain.ChainStatus)
        {
            unknownRoot |= status.Status == X509ChainStatusFlags.UntrustedRoot || status.Status == X509ChainStatusFlags.PartialChain;
            expired |= status.Status == X509ChainStatusFlags.NotTimeValid;
        }
        byte alert = unknownRoot ? TlsAlert.UnknownCa : expired ? TlsAlert.CertificateExpired : TlsAlert.BadCertificate;
        return Fail(alert, string.Concat("the server's certificate isn't trusted.", why));
    }

    /// <summary>Erases what's left of the secrets.</summary>
    internal void Clear()
    {
        CryptographicOperations.ZeroMemory(_privateKey);
        CryptographicOperations.ZeroMemory(_p256PrivateKey);
        if (_handshakeSecret != null) CryptographicOperations.ZeroMemory(_handshakeSecret);
        if (_clientSecret != null) CryptographicOperations.ZeroMemory(_clientSecret);
        if (_serverSecret != null) CryptographicOperations.ZeroMemory(_serverSecret);
        _transcript?.Dispose();
    }
}
