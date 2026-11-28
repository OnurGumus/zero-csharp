// Example: HTTPS (lib/SslStream.cs): connects to a web server, runs the TLS
// 1.3 handshake, checks the server's certificate against the system's
// roots, and fetches a page with HTTP/1.1. Needs the network, so the tests
// don't run it.
//
//   ./run.sh Https                       https://example.com/
//   ./run.sh Https github.com /          another host and path
//   ./run.sh Https localhost:4433        another port (scripts/tls-interop.sh)
//   SSL_CERT_FILE=roots.pem ./run.sh Https example.com
//
// Exit code: 0 if the page came, 1 if the connection or handshake failed.
// Build: ./build.sh Https

using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace HttpsSample;

public static class Program
{
    public static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "example.com";
        string path = args.Length > 1 ? args[1] : "/";
        int port = 443;
        int colon = host.LastIndexOf(':');
        if (colon > 0)
        {
            port = 0;
            foreach (char c in host.Substring(colon + 1))
                port = port * 10 + (c - '0');
            host = host.Substring(0, colon);
        }

        // There's no garbage collector: a connection's objects (the handshake's,
        // the certificates') are freed with an object scope around it. A program
        // that keeps connecting would otherwise lose about 40 KB each time.
        Arena.DebugChecks = true;
        Arena connection = Arena.Create(64 * 1024, "connection");
        int result;
        // docs:https-scope
        Arena.ObjectScope objects;
        // SAFETY: nothing made inside is kept after the block
        unsafe { objects = connection.OpenObjectScope(); }
        using (objects)
            result = Fetch(host, port, path);
        // docs:end
        return result;
    }

    static int Fetch(string host, int port, string path)
    {
        // docs:https
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
        // docs:end
        string suite = tls.NegotiatedCipherSuite switch
        {
            TlsCipherSuite.TLS_AES_128_GCM_SHA256 => "AES-128-GCM",
            TlsCipherSuite.TLS_AES_256_GCM_SHA384 => "AES-256-GCM",
            _ => "ChaCha20-Poly1305",
        };
        Console.WriteLine($"TLS 1.3, {suite}, ALPN '{tls.NegotiatedApplicationProtocol.ToString()}'");
        Console.WriteLine($"certificate: {tls.RemoteCertificate.Subject}");
        Console.WriteLine($"issued by:   {tls.RemoteCertificate.Issuer}");

        tls.Write(System.Text.Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: zero-csharp\r\nConnection: close\r\n\r\n"));
        byte[] buffer = new byte[16384];
        long total = 0;
        string firstLine = null;
        int n;
        while ((n = tls.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (firstLine == null)
            {
                int end = 0;
                while (end < n && buffer[end] != (byte)'\r')
                    end++;
                firstLine = System.Text.Encoding.ASCII.GetString(buffer, 0, end);
            }
            total += n;
        }
        Console.WriteLine($"{firstLine}: {total} bytes");
        return 0;
    }
}
