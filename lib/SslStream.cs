// SslStream: TLS 1.3 over another stream, as a client, with .NET's API.
//
//   using TcpClient tcp = new TcpClient("example.com", 443);
//   using SslStream tls = new SslStream(tcp.GetStream());
//   tls.AuthenticateAsClient("example.com");
//   tls.Write("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"u8);
//   int n = tls.Read(buffer);
//
// AuthenticateAsClient runs the TLS 1.3 handshake (RFC 8446): it offers
// AES-128-GCM, AES-256-GCM and ChaCha20-Poly1305 with an X25519 key share
// (and a P-256 one if the server asks for it with a HelloRetryRequest),
// checks the server's certificate chain (X509Chain, with the system's roots
// unless the options give a chain policy), that the certificate is for the
// host, and that the server's key signed the handshake. Then Read and Write
// carry data in encrypted records.
//
// Only TLS 1.3, and only as a client. A server that speaks only TLS 1.2, or
// wants a key share other than X25519 or P-256, is refused.
// No session resumption, no 0-RTT, no client certificates (a server that
// asks gets an empty Certificate message, as RFC 8446 allows).
//
// What differs from .NET:
// - A throw ends the program unless a Catch.Run is waiting (lib/Catch.cs).
//   TryAuthenticateAsClient returns false with the reason instead, whatever
//   the server sends and however it hangs up, as long as the inner stream
//   doesn't throw: lib's NetworkStream doesn't (a broken connection reads as
//   0 and drops writes, and its Error says why), so over a TcpClient it never
//   throws (scripts/tls-interop.sh checks hang-ups). A stream of your own that
//   throws still ends the program. After the handshake, Read and Write throw
//   IOException for a record that fails its check or an alert, as .NET's do;
//   a connection that just ends reads as 0.
// - ApplicationProtocols is an array, not a List.
// - Dispose doesn't send close_notify, as in .NET; ShutdownAsync does.
// - EnabledSslProtocols may only be None (the default) or include Tls13.

using System;
using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace System.Security.Authentication
{
    /// <summary>A TLS handshake that failed: the server, its certificate or the connection.</summary>
    public class AuthenticationException : Exception
    {
        public AuthenticationException() : base("Authentication failed.") { }
        public AuthenticationException(string message) : base(message) { }
    }

    [Flags]
    public enum SslProtocols
    {
        None = 0,
        Tls12 = 0xC00,
        Tls13 = 0x3000,
    }
}

namespace System.Net.Security
{
    /// <summary>What is wrong with the server's certificate.</summary>
    [Flags]
    public enum SslPolicyErrors
    {
        None = 0,
        RemoteCertificateNotAvailable = 1,
        RemoteCertificateNameMismatch = 2,
        RemoteCertificateChainErrors = 4,
    }

    /// <summary>Decides whether to accept the server's certificate; errors says what the checks found.</summary>
    public delegate bool RemoteCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors);

    public enum TlsCipherSuite : ushort
    {
        TLS_AES_128_GCM_SHA256 = 0x1301,
        TLS_AES_256_GCM_SHA384 = 0x1302,
        TLS_CHACHA20_POLY1305_SHA256 = 0x1303,
    }

    /// <summary>An application protocol name for ALPN, such as "http/1.1".</summary>
    public readonly struct SslApplicationProtocol
    {
        readonly byte[] _protocol;

        public SslApplicationProtocol(byte[] protocol)
        {
            if (protocol == null || protocol.Length == 0 || protocol.Length > 255)
                throw new ArgumentException("An application protocol is 1 to 255 bytes.", nameof(protocol));
            _protocol = protocol;
        }

        public SslApplicationProtocol(string protocol) : this(Ascii(protocol)) { }

        static byte[] Ascii(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));
            byte[] bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
                bytes[i] = (byte)text[i];
            return bytes;
        }

        /// <summary>The name's bytes.</summary>
        public ReadOnlyMemory<byte> Protocol => _protocol;

        public static SslApplicationProtocol Http11 => new SslApplicationProtocol("http/1.1");
        public static SslApplicationProtocol Http2 => new SslApplicationProtocol("h2");
        public static SslApplicationProtocol Http3 => new SslApplicationProtocol("h3");

        internal ReadOnlySpan<byte> Bytes => _protocol;

        public override string ToString()
        {
            if (_protocol == null)
                return "";
            System.Text.StringBuilder text = new System.Text.StringBuilder(_protocol.Length);
            foreach (byte b in _protocol)
                text.Append((char)b);
            return text.ToString();
        }
    }

    /// <summary>How AuthenticateAsClient connects: the host, and how to check its certificate.</summary>
    public class SslClientAuthenticationOptions
    {
        /// <summary>The server's name: sent to it (SNI) and checked against its certificate.</summary>
        public string TargetHost { get; set; }

        /// <summary>Decides whether to accept the certificate, instead of the default (no errors at all).</summary>
        public RemoteCertificateValidationCallback RemoteCertificateValidationCallback { get; set; }

        /// <summary>Protocols to offer with ALPN, most wanted first (an array here, a List in .NET).</summary>
        public SslApplicationProtocol[] ApplicationProtocols { get; set; }

        /// <summary>None (the system's choice) or a set including Tls13: only TLS 1.3 is spoken here.</summary>
        public SslProtocols EnabledSslProtocols { get; set; }

        /// <summary>NoCheck by default, as in .NET; revocation isn't checked here (see X509Chain).</summary>
        public X509RevocationMode CertificateRevocationCheckMode { get; set; } = X509RevocationMode.NoCheck;

        /// <summary>The policy to check the server's chain with, such as a CustomTrustStore; the system's roots if null.</summary>
        public X509ChainPolicy CertificateChainPolicy { get; set; }
    }

    /// <summary>A TLS 1.3 client connection over another stream.</summary>
    public class SslStream : Stream
    {
        readonly Stream _inner;
        readonly bool _leaveOpen;
        readonly RemoteCertificateValidationCallback _callback;
        TlsRecordLayer _records;
        TlsSuite _suite;
        byte[] _readSecret, _writeSecret;   // application traffic secrets, for KeyUpdate
        X509Certificate2 _remote;
        SslApplicationProtocol _alpn;
        string _targetHost;
        bool _authenticated;
        bool _receivedClose, _sentClose, _disposed;
        byte[] _pending;                    // decrypted application data not yet read
        int _pendingStart, _pendingEnd;
        byte[] _handshake;                  // handshake bytes received but not yet a whole message
        int _handshakeLength;

        // For tests (scripts/crypto-fuzz replays RFC 8448): a fixed ClientHello and X25519 key.
        internal byte[] TestClientHello;
        internal byte[] TestPrivateKey;
        internal bool TestNoChangeCipherSpec;
        internal byte[] TestClientHello2;     // after a HelloRetryRequest
        internal byte[] TestP256PrivateKey;

        public SslStream(Stream innerStream) : this(innerStream, false, null) { }

        public SslStream(Stream innerStream, bool leaveInnerStreamOpen) : this(innerStream, leaveInnerStreamOpen, null) { }

        public SslStream(Stream innerStream, bool leaveInnerStreamOpen, RemoteCertificateValidationCallback userCertificateValidationCallback)
        {
            _inner = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _leaveOpen = leaveInnerStreamOpen;
            _callback = userCertificateValidationCallback;
        }

        public Stream InnerStream => _inner;
        public bool IsAuthenticated => _authenticated;
        public bool IsEncrypted => _authenticated;
        public bool IsSigned => _authenticated;
        public bool IsServer => false;
        public bool IsMutuallyAuthenticated => false;
        public SslProtocols SslProtocol => _authenticated ? SslProtocols.Tls13 : SslProtocols.None;
        public TlsCipherSuite NegotiatedCipherSuite => (TlsCipherSuite)_suite.Code;
        public SslApplicationProtocol NegotiatedApplicationProtocol => _alpn;
        public string TargetHostName => _targetHost ?? "";

        /// <summary>The server's certificate, once authenticated.</summary>
        public X509Certificate RemoteCertificate => _remote;

        public X509Certificate LocalCertificate => null;

        /// <summary>Runs the handshake with this server name. Throws AuthenticationException if it fails.</summary>
        public void AuthenticateAsClient(string targetHost)
            => AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = targetHost });

        /// <summary>Runs the handshake. Throws AuthenticationException if it fails.</summary>
        public void AuthenticateAsClient(SslClientAuthenticationOptions sslClientAuthenticationOptions)
        {
            if (!TryAuthenticateAsClient(sslClientAuthenticationOptions, out string error))
                throw new AuthenticationException(error);
        }

        public Task AuthenticateAsClientAsync(string targetHost)
        {
            AuthenticateAsClient(targetHost);
            return Task.CompletedTask;
        }

        public Task AuthenticateAsClientAsync(SslClientAuthenticationOptions sslClientAuthenticationOptions, CancellationToken cancellationToken = default)
        {
            AuthenticateAsClient(sslClientAuthenticationOptions);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs the handshake; false, with the reason, if the server can't be trusted or
        /// doesn't speak TLS 1.3 as this does. Doesn't throw for anything the server sends or does, over lib's NetworkStream
        /// (a stream of your own that throws still ends the program).
        /// After a failure the stream is of no further use.
        /// </summary>
        public bool TryAuthenticateAsClient(SslClientAuthenticationOptions options, out string error)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (_disposed)
                throw new ObjectDisposedException(nameof(SslStream));
            if (_authenticated || _records != null)
                throw new InvalidOperationException("SslStream: already authenticated, or a handshake was tried.");
            if (options.EnabledSslProtocols != SslProtocols.None && (options.EnabledSslProtocols & SslProtocols.Tls13) == 0)
            {
                error = "SslStream: only TLS 1.3 is supported here.";
                return false;
            }
            _targetHost = options.TargetHost ?? "";
            _records = new TlsRecordLayer(_inner);
            TlsClientHandshake handshake = new TlsClientHandshake(this, options);
            error = handshake.Run();
            handshake.Clear();
            if (error != null)
            {
                error = string.Concat("SslStream: ", error);
                _records.Dispose();
                return false;
            }
            _authenticated = true;
            return true;
        }

        // ---- what the handshake uses ----

        internal TlsRecordLayer Records => _records;
        internal RemoteCertificateValidationCallback Callback => _callback;

        internal void Established(TlsSuite suite, byte[] readSecret, byte[] writeSecret, X509Certificate2 remote, SslApplicationProtocol alpn)
        {
            _suite = suite;
            _readSecret = readSecret;
            _writeSecret = writeSecret;
            _remote = remote;
            _alpn = alpn;
        }

        /// <summary>
        /// The next handshake message (type, then 3-byte length, then body), reading records as
        /// needed. Null with error set if something else came or the connection ended.
        /// </summary>
        internal byte[] NextHandshakeMessage(out string error, out byte alert)
        {
            error = null;
            alert = 0;
            while (true)
            {
                if (_handshakeLength >= 4)
                {
                    int length = _handshake[1] << 16 | _handshake[2] << 8 | _handshake[3];
                    if (length > 1 << 18)
                    {
                        error = "a handshake message was too long";
                        alert = TlsAlert.DecodeError;
                        return null;
                    }
                    if (_handshakeLength >= 4 + length)
                    {
                        byte[] message = _handshake.AsSpan(0, 4 + length).ToArray();
                        _handshake.AsSpan(4 + length, _handshakeLength - 4 - length).CopyTo(_handshake);
                        _handshakeLength -= 4 + length;
                        return message;
                    }
                }
                if (!_records.Read(out byte type, out Span<byte> fragment))
                {
                    error = _records.Error ?? "the server closed the connection during the handshake";
                    alert = _records.Error != null ? _records.ErrorAlert : (byte)0;
                    return null;
                }
                if (type == TlsRecordLayer.ChangeCipherSpec)
                {
                    if (fragment.Length != 1 || fragment[0] != 1)
                    {
                        error = "a malformed change_cipher_spec";
                        alert = TlsAlert.UnexpectedMessage;
                        return null;
                    }
                    continue; // sent for middleboxes; means nothing in TLS 1.3
                }
                if (type == TlsRecordLayer.Alert)
                {
                    error = fragment.Length != 2 ? "a malformed alert"
                        : fragment[1] == TlsAlert.ProtocolVersion
                            ? "the server refused: protocol_version (it doesn't speak TLS 1.3, the only version here)"
                            : string.Concat("the server refused: ", TlsAlert.Name(fragment[1]));
                    return null;
                }
                if (type != TlsRecordLayer.Handshake || fragment.Length == 0)
                {
                    error = "something other than the handshake came during it";
                    alert = TlsAlert.UnexpectedMessage;
                    return null;
                }
                _handshake ??= new byte[4096];
                if (_handshakeLength + fragment.Length > _handshake.Length)
                {
                    byte[] bigger = new byte[Math.Max(_handshake.Length * 2, _handshakeLength + fragment.Length)];
                    _handshake.AsSpan(0, _handshakeLength).CopyTo(bigger);
                    _handshake = bigger;
                }
                fragment.CopyTo(_handshake.AsSpan(_handshakeLength));
                _handshakeLength += fragment.Length;
            }
        }

        /// <summary>True if no part of a handshake message is waiting (required where keys change).</summary>
        internal bool HandshakeBufferEmpty => _handshakeLength == 0;

        // ---- Stream ----

        public override bool CanRead => _authenticated && !_disposed;
        public override bool CanWrite => _authenticated && !_disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush()
        {
            CheckAuthenticated();
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(new Span<byte>(buffer, offset, count));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(new ReadOnlySpan<byte>(buffer, offset, count));
        }

        /// <summary>
        /// Reads decrypted data, waiting for a record if none is left; 0 once the server has
        /// closed (with close_notify, or by ending the connection, as in .NET). Throws
        /// IOException for a record that fails its check or an alert.
        /// </summary>
        public override int Read(Span<byte> buffer)
        {
            CheckAuthenticated();
            if (buffer.IsEmpty)
                return 0;
            while (_pendingStart == _pendingEnd)
            {
                if (_receivedClose || !ReceiveRecord())
                    return 0;
            }
            int n = Math.Min(buffer.Length, _pendingEnd - _pendingStart);
            _pending.AsSpan(_pendingStart, n).CopyTo(buffer);
            _pendingStart += n;
            return n;
        }

        // Reads one record after the handshake. False at the end of the connection.
        bool ReceiveRecord()
        {
            if (!_records.Read(out byte type, out Span<byte> fragment))
            {
                if (_records.Error == null)
                {
                    _receivedClose = true;
                    return false;
                }
                Fatal(_records.ErrorAlert, _records.Error);
            }
            switch (type)
            {
                case TlsRecordLayer.ApplicationData:
                    if (_handshakeLength != 0)
                        Fatal(TlsAlert.UnexpectedMessage, "application data inside a handshake message");
                    _pending ??= new byte[TlsRecordLayer.MaxPlaintext];
                    fragment.CopyTo(_pending);
                    _pendingStart = 0;
                    _pendingEnd = fragment.Length;
                    return true;
                case TlsRecordLayer.Alert:
                    if (fragment.Length != 2)
                        Fatal(TlsAlert.DecodeError, "a malformed alert");
                    if (fragment[1] == TlsAlert.CloseNotify)
                    {
                        _receivedClose = true;
                        return false;
                    }
                    if (fragment[1] == TlsAlert.UserCanceled)
                        return true;
                    throw new IOException(string.Concat("SslStream: the server reported an error: ", TlsAlert.Name(fragment[1])));
                case TlsRecordLayer.Handshake:
                    PostHandshake(fragment);
                    return true;
                default:
                    Fatal(TlsAlert.UnexpectedMessage, "an unexpected record after the handshake");
                    return false;
            }
        }

        // NewSessionTicket (ignored: no resumption here) and KeyUpdate.
        void PostHandshake(ReadOnlySpan<byte> fragment)
        {
            _handshake ??= new byte[4096];
            if (_handshakeLength + fragment.Length > _handshake.Length)
            {
                if (_handshakeLength + fragment.Length > 1 << 18)
                    Fatal(TlsAlert.DecodeError, "a handshake message was too long");
                byte[] bigger = new byte[Math.Max(_handshake.Length * 2, _handshakeLength + fragment.Length)];
                _handshake.AsSpan(0, _handshakeLength).CopyTo(bigger);
                _handshake = bigger;
            }
            fragment.CopyTo(_handshake.AsSpan(_handshakeLength));
            _handshakeLength += fragment.Length;
            while (_handshakeLength >= 4)
            {
                int length = _handshake[1] << 16 | _handshake[2] << 8 | _handshake[3];
                if (_handshakeLength < 4 + length)
                    return;
                byte type = _handshake[0];
                if (type == 24) // KeyUpdate
                {
                    if (length != 1 || _handshake[4] > 1 || _handshakeLength != 5)
                        Fatal(TlsAlert.DecodeError, "a malformed KeyUpdate");
                    bool requested = _handshake[4] == 1;
                    _handshakeLength = 0;
                    NextSecret(_readSecret);
                    _records.SetReadKey(_suite, _readSecret);
                    if (requested && !_sentClose)
                    {
                        Span<byte> update = stackalloc byte[5];
                        update[0] = 24;
                        update[1] = 0;
                        update[2] = 0;
                        update[3] = 1;
                        update[4] = 0; // update_not_requested
                        _records.Write(TlsRecordLayer.Handshake, update);
                        NextSecret(_writeSecret);
                        _records.SetWriteKey(_suite, _writeSecret);
                    }
                    return;
                }
                if (type != 4) // NewSessionTicket
                    Fatal(TlsAlert.UnexpectedMessage, "an unexpected handshake message after the handshake");
                _handshake.AsSpan(4 + length, _handshakeLength - 4 - length).CopyTo(_handshake);
                _handshakeLength -= 4 + length;
            }
        }

        void NextSecret(byte[] secret)
        {
            Span<byte> next = stackalloc byte[_suite.HashSize];
            TlsKeys.ExpandLabel(_suite, secret, "traffic upd"u8, ReadOnlySpan<byte>.Empty, next);
            next.CopyTo(secret);
            CryptographicOperations.ZeroMemory(next);
        }

        void Fatal(byte alert, string message)
        {
            if (!_sentClose && alert != 0)
            {
                _sentClose = true;
                _records.SendAlert(alert);
            }
            throw new IOException(string.Concat("SslStream: ", message));
        }

        /// <summary>Encrypts data and sends it, in records of up to 16 KB.</summary>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckAuthenticated();
            if (_sentClose)
                throw new IOException("SslStream: written after closing.");
            if (!buffer.IsEmpty)
                _records.Write(TlsRecordLayer.ApplicationData, buffer);
        }

        /// <summary>Sends close_notify: the server's reads end, and writing after it throws.</summary>
        public virtual Task ShutdownAsync()
        {
            CheckAuthenticated();
            if (!_sentClose)
            {
                _sentClose = true;
                _records.SendAlert(TlsAlert.CloseNotify);
            }
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (disposing)
            {
                _records?.Dispose();
                if (_readSecret != null)
                    CryptographicOperations.ZeroMemory(_readSecret);
                if (_writeSecret != null)
                    CryptographicOperations.ZeroMemory(_writeSecret);
                if (_pending != null)
                    CryptographicOperations.ZeroMemory(_pending);
                if (!_leaveOpen)
                    _inner.Dispose();
            }
        }

        void CheckAuthenticated()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SslStream));
            if (!_authenticated)
                throw new InvalidOperationException("SslStream: not authenticated: call AuthenticateAsClient first.");
        }
    }
}
