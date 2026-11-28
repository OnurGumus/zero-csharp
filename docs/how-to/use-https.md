---
title: Connect with TLS (HTTPS)
index: 31
---

# Connect with TLS (HTTPS)

A plain TCP connection hides nothing: anyone on the way can read it, change it, or pretend to be
the server. HTTPS and most other protocols on the internet put TLS between the program and the
connection. TLS encrypts what goes through, and the server proves who it is with a certificate.

`SslStream` is that layer, with .NET's API. Wrap a connection's stream in it, authenticate, then
read and write as usual:

```
  your program ── SslStream ── NetworkStream ── TCP ── the server
                  encrypts, checks the
                  server's certificate
```

<!-- sample: samples/Https.cs https -->
```csharp
using TcpClient tcp = new TcpClient();
tcp.Connect(host, port);
using SslStream tls = new SslStream(tcp.GetStream());
var options = new SslClientAuthenticationOptions
{
    TargetHost = host,
    ApplicationProtocols = new[] { SslApplicationProtocol.Http11 },
};
if (!tls.TryAuthenticateAsClient(options, out string error))
{
    Console.WriteLine(error);
    return 1;
}
```

After that, `tls.Write` sends encrypted and `tls.Read` returns what the server sent, until it
closes. `samples/Https.cs` fetches a page this way: `./run.sh Https github.com /`.

## What authenticating checks

The handshake (TLS 1.3, RFC 8446) agrees on fresh keys with X25519, so recording the connection
and later learning the server's key doesn't open it. Then it checks the server in three ways, and
fails if any check fails:

- **The certificate chain** reaches a root the system trusts, with `X509Chain`, as
  [Use cryptography](use-cryptography.html#check-a-certificate) describes. Each certificate must be
  within its dates and meant for TLS servers.
- **The certificate is for this host**: `TargetHost` must be among its names (`MatchesHostname`).
- **The server holds the certificate's key**: it signs the handshake with it, and the signature
  must verify.

`AuthenticateAsClient(host)` does the same and throws `AuthenticationException` when it fails. A
throw ends the program unless a [`Catch.Run`](recover-from-errors.html) is waiting for it, so
`TryAuthenticateAsClient` is the one to use when the other end might not be trustworthy. It
returns false with the reason, for example:

```
SslStream: the server's certificate isn't trusted. The certificate isn't for 127.0.0.1.
```

## Choosing what to trust

The roots come from the system's PEM bundle on Linux, macOS and Android, as `X509Chain` reads them.
Windows' certificate store isn't read yet. On Windows, and anywhere without a bundle, set
`SSL_CERT_FILE` to a PEM file of roots, or give the roots in code:

```csharp no-sample
var policy = new X509ChainPolicy();
policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
policy.CustomTrustStore.Add(X509Certificate2.CreateFromPem(myRootPem));
options.CertificateChainPolicy = policy;
```

To decide yourself, give a `RemoteCertificateValidationCallback`. It gets the certificate, the
built chain and the `SslPolicyErrors` the checks found, and returns whether to go on. Returning
`true` whatever the errors turns off the protection, so do that only for tests.

## Free each connection's memory

Zero mode has no garbage collector. A connection allocates its handshake messages, the server's
certificates, their keys and the chain's checks: about 40 KB that stays taken after the connection
closes. A program that connects once doesn't care, but one that keeps connecting would slowly run
out. Open an [object scope](../concepts/memory.html#ordinary-objects-in-an-arena) around each connection, and everything it
allocated is freed when the block ends:

<!-- sample: samples/Https.cs https-scope -->
```csharp
Arena.ObjectScope objects;
// SAFETY: nothing made inside is kept after the block
unsafe { objects = connection.OpenObjectScope(); }
using (objects)
    result = Fetch(host, port, path);
```

Nothing from inside may be kept after the block: not the stream, not `RemoteCertificate`, not a
string read from the connection. Copy out what you need first. What lib/ keeps for the whole run
(the system's roots, read once, and the curves' tables) is made outside every scope, so a first
connection inside one is fine.

## What's supported

| | |
|---|---|
| Versions | TLS 1.3 only. A server that speaks only TLS 1.2 is refused, with a message saying so |
| Cipher suites | AES-128-GCM, AES-256-GCM, ChaCha20-Poly1305 |
| Key exchange | X25519. A server that only does P-256 is refused (most do both) |
| Server certificates | RSA (2048 bits and up, signing with PSS) and ECDSA P-256, P-384 |
| ALPN | `ApplicationProtocols`, an array here (a `List` in .NET) |
| Client certificates | none: a server that asks gets an empty list, and may still refuse |
| Resumption, 0-RTT | no: every connection does a whole handshake |

Only the client side is here: a program can connect to a TLS server but can't be one.

`SslStream` was checked against RFC 8448's recorded handshake byte for byte, against OpenSSL's
server with each cipher suite and key type (`scripts/tls-interop.sh`), and against real sites.
