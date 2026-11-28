// TLS on .NET: .NET's own SslStream (the platform's TLS), over lib/'s streams.
//
// lib/'s TLS client (lib/SslStream.cs, with its own ciphers, signatures and
// certificate checks) stays out of the .NET build: a program on .NET gets the
// platform's. But .NET's SslStream reads and writes through a .NET Stream,
// and what lib/'s sockets give (TcpClient.GetStream()) is lib/'s Stream,
// another type of the same name. So this file has:
//
// - SslStream, with lib/'s members, which is what a program names: a lib/
//   Stream on the outside, .NET's SslStream inside;
// - LibStreamForBcl, a .NET Stream over lib/'s, for .NET's SslStream to read
//   and write through;
// - SslClientAuthenticationOptions as lib/ has it (ApplicationProtocols is an
//   array there, a List in .NET).
//
// .NET's types that lib/'s take the place of (Stream, SslStream,
// IOException, SeekOrigin) are reached through the alias `bcl`, which the
// project gives .NET's assemblies (Faithful.csproj, Sdk/ZeroDotnet.targets).
//
// Only .NET's synchronous calls are used: they read and write the inner
// stream on the calling thread, so a fiber waiting for the network lets the
// others run, as with any lib/ socket. The async members run those on a
// fiber (lib/'s Task.Run).
//
// What differs from lib/'s SslStream:
// - The platform's TLS does more: TLS 1.2 as well as 1.3, other key shares
//   and suites. A server lib/'s client refuses for those connects here.
// - The system's own roots are trusted (the keychain on macOS), unless the
//   options give a chain policy or SSL_CERT_FILE names a file of roots,
//   which then are the only ones, as for lib/'s.
// - NegotiatedCipherSuite and SslProtocol say what the platform chose.
extern alias bcl;

using System;
using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using BclIOException = bcl::System.IO.IOException;
using BclOptions = bcl::System.Net.Security.SslClientAuthenticationOptions;
using BclSeekOrigin = bcl::System.IO.SeekOrigin;
using BclSslStream = bcl::System.Net.Security.SslStream;
using BclStream = bcl::System.IO.Stream;
using BclTask = bcl::System.Threading.Tasks.Task;
using BclToken = bcl::System.Threading.CancellationToken;

namespace System.Net.Security
{
    /// <summary>What AuthenticateAsClient needs to know (lib/SslStream.cs's).</summary>
    public class SslClientAuthenticationOptions
    {
        /// <summary>The server's name, checked against its certificate.</summary>
        public string TargetHost { get; set; }

        /// <summary>Decides whether to accept the server's certificate, instead of the default check.</summary>
        public RemoteCertificateValidationCallback RemoteCertificateValidationCallback { get; set; }

        /// <summary>The protocols to offer the server (ALPN), in order of preference.</summary>
        public SslApplicationProtocol[] ApplicationProtocols { get; set; }

        /// <summary>None for the platform's choice.</summary>
        public SslProtocols EnabledSslProtocols { get; set; }

        public X509RevocationMode CertificateRevocationCheckMode { get; set; } = X509RevocationMode.NoCheck;

        /// <summary>How to check the server's certificate chain, instead of against the system's roots.</summary>
        public X509ChainPolicy CertificateChainPolicy { get; set; }
    }

    /// <summary>TLS over another stream, as a client: .NET's SslStream, over a lib/ stream.</summary>
    public class SslStream : Stream
    {
        readonly Stream _inner;
        readonly BclSslStream _tls;
        bool _disposed;

        public SslStream(Stream innerStream) : this(innerStream, false, null) { }

        public SslStream(Stream innerStream, bool leaveInnerStreamOpen) : this(innerStream, leaveInnerStreamOpen, null) { }

        public SslStream(Stream innerStream, bool leaveInnerStreamOpen, RemoteCertificateValidationCallback userCertificateValidationCallback)
        {
            ArgumentNullException.ThrowIfNull(innerStream);
            _inner = innerStream;
            _tls = new BclSslStream(new LibStreamForBcl(innerStream, leaveInnerStreamOpen), false, userCertificateValidationCallback);
        }

        public Stream InnerStream => _inner;
        public bool IsAuthenticated => _tls.IsAuthenticated;
        public bool IsEncrypted => _tls.IsEncrypted;
        public bool IsSigned => _tls.IsSigned;
        public bool IsServer => false;
        public bool IsMutuallyAuthenticated => _tls.IsMutuallyAuthenticated;
        public SslProtocols SslProtocol => _tls.SslProtocol;
        public TlsCipherSuite NegotiatedCipherSuite => _tls.NegotiatedCipherSuite;
        public SslApplicationProtocol NegotiatedApplicationProtocol => _tls.NegotiatedApplicationProtocol;
        public string TargetHostName => _tls.TargetHostName;
        public X509Certificate RemoteCertificate => _tls.RemoteCertificate;
        public X509Certificate LocalCertificate => _tls.LocalCertificate;

        public void AuthenticateAsClient(string targetHost)
            => AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = targetHost });

        public void AuthenticateAsClient(SslClientAuthenticationOptions sslClientAuthenticationOptions)
        {
            ArgumentNullException.ThrowIfNull(sslClientAuthenticationOptions);
            try
            {
                _tls.AuthenticateAsClient(ForBcl(sslClientAuthenticationOptions));
            }
            catch (BclIOException e)
            {
                throw new IOException(e.Message); // lib/'s IOException: what a program here catches
            }
        }

        /// <summary>
        /// AuthenticateAsClient that doesn't throw for anything the server
        /// does: false, with the reason, if the handshake fails.
        /// </summary>
        public bool TryAuthenticateAsClient(SslClientAuthenticationOptions options, out string error)
        {
            ArgumentNullException.ThrowIfNull(options);
            try
            {
                _tls.AuthenticateAsClient(ForBcl(options));
                error = null;
                return true;
            }
            catch (Exception e) when (e is AuthenticationException or BclIOException or IOException
                or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
            {
                // As lib's reasons are: "SslStream: " and why (here, .NET's words).
                error = "SslStream: " + (e.InnerException != null ? e.Message + " " + e.InnerException.Message : e.Message);
                return false;
            }
        }

        public Task AuthenticateAsClientAsync(string targetHost)
            => Task.Run(() => AuthenticateAsClient(targetHost));

        public Task AuthenticateAsClientAsync(SslClientAuthenticationOptions sslClientAuthenticationOptions, CancellationToken cancellationToken = default)
            => Task.Run(() => AuthenticateAsClient(sslClientAuthenticationOptions), cancellationToken);

        static BclOptions ForBcl(SslClientAuthenticationOptions options)
        {
            var bcl = new BclOptions
            {
                TargetHost = options.TargetHost,
                RemoteCertificateValidationCallback = options.RemoteCertificateValidationCallback,
                EnabledSslProtocols = options.EnabledSslProtocols,
                CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
                CertificateChainPolicy = options.CertificateChainPolicy ?? RootsFromEnvironment(),
            };
            if (options.ApplicationProtocols != null)
                bcl.ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol>(options.ApplicationProtocols);
            return bcl;
        }

        // SSL_CERT_FILE, as lib/'s X509Chain and OpenSSL read it: the roots to
        // trust, in place of the system's. (.NET reads it itself on Linux,
        // where its TLS is OpenSSL's, and not on macOS.)
        static X509ChainPolicy RootsFromEnvironment()
        {
            string file = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
            if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file))
                return null;
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            policy.CustomTrustStore.ImportFromPemFile(file);
            return policy;
        }

        public override bool CanRead => !_disposed && _tls.CanRead;
        public override bool CanWrite => !_disposed && _tls.CanWrite;
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
            try { _tls.Flush(); }
            catch (BclIOException e) { throw new IOException(e.Message); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return _tls.Read(buffer, offset, count); }
            catch (BclIOException e) { throw new IOException(e.Message); }
        }

        public override int Read(Span<byte> buffer)
        {
            try { return _tls.Read(buffer); }
            catch (BclIOException e) { throw new IOException(e.Message); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            try { _tls.Write(buffer, offset, count); }
            catch (BclIOException e) { throw new IOException(e.Message); }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try { _tls.Write(buffer); }
            catch (BclIOException e) { throw new IOException(e.Message); }
        }

        /// <summary>Tells the server no more data comes (close_notify).</summary>
        public virtual Task ShutdownAsync()
            => Task.Run(() => _tls.ShutdownAsync().GetAwaiter().GetResult());

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (disposing)
                _tls.Dispose(); // and, through LibStreamForBcl, the inner stream unless it was to stay open
            base.Dispose(disposing);
        }
    }

    // A .NET Stream over a lib/ Stream: what .NET's SslStream reads the
    // server's records from and writes its own to. Synchronous calls only
    // (see the top of the file).
    sealed class LibStreamForBcl(Stream inner, bool leaveOpen) : BclStream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, BclSeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();

        // lib/'s IOException isn't .NET's: .NET's SslStream is given its own
        // kind, so that it treats a broken connection as one.
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return inner.Read(buffer, offset, count); }
            catch (IOException e) { throw new BclIOException(e.Message); }
        }

        public override int Read(Span<byte> buffer)
        {
            try { return inner.Read(buffer); }
            catch (IOException e) { throw new BclIOException(e.Message); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            try { inner.Write(buffer, offset, count); }
            catch (IOException e) { throw new BclIOException(e.Message); }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try { inner.Write(buffer); }
            catch (IOException e) { throw new BclIOException(e.Message); }
        }

        // .NET's SslStream writes its last record (ShutdownAsync) through
        // these. A Stream's own would run Write on a thread-pool thread,
        // beside the program; these run it here, and are done on return.
        public override BclTask WriteAsync(byte[] buffer, int offset, int count, BclToken cancellationToken)
        {
            Write(buffer, offset, count);
            return BclTask.CompletedTask;
        }

        public override bcl::System.Threading.Tasks.ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, BclToken cancellationToken = default)
        {
            Write(buffer.Span);
            return default;
        }

        public override bcl::System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, BclToken cancellationToken)
            => BclTask.FromResult(Read(buffer, offset, count));

        public override bcl::System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, BclToken cancellationToken = default)
            => new bcl::System.Threading.Tasks.ValueTask<int>(Read(buffer.Span));

        public override BclTask FlushAsync(BclToken cancellationToken)
        {
            Flush();
            return BclTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
