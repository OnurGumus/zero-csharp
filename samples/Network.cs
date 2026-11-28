// Example: networking (lib/Net.cs, lib/Sockets.cs): addresses, and TCP and
// UDP sockets that work with fibers: a server fiber accepts connections and
// gives each a fiber of its own, all on one thread. Everything goes over
// loopback (127.0.0.1), so no network is needed.
// Exit code = number of failed checks.
// Build: ./build.sh Network

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkSample;

public sealed class Echo
{
    public TcpListener Listener;
    public int Port;
    public int Clients;     // how many connections the server takes
    public int Served;
    public int Correct;     // clients whose echo came back right
}

public sealed class Secure
{
    public TcpListener Listener;
    public int Port;
    public int Received;
    public bool Correct;
}

public sealed class Bulk
{
    public NetworkStream Stream;
    public int Size;
    public long Received;
    public bool Ordered = true;
}

public sealed class Failures
{
    public TcpListener Listener;
    public int Port;
    public SocketError Reset, Closed;
    public TcpClient Victim;    // the server's end of the connection closed from another fiber
    public bool Waiting;        // its reader is about to wait
    public bool EchoedAfter;
}

public sealed class Guarded
{
    public TcpListener Listener;
    public int Port;
    public int Served;
    public int Failed;      // connections whose serving threw
    public int Echoed;      // clients that got their echo
    public bool Dropped;    // the client whose connection failed saw it close
}

public sealed class Datagrams
{
    public UdpClient Server, Client;
    public int Port;
    public bool Echoed;
}

public sealed class Lookup
{
    public bool Found;
    public int OthersRan;
}

// Two async methods on one connection (see Main's "Async" section).
public sealed class AsyncPair
{
    public NetworkStream Reader, Writer;
    public byte[] Buffer = new byte[16];
    public int Read;
    public bool WriterRan;
}

// The timeouts section: a fiber that times out while another runs.
public sealed class Patience
{
    public Socket Socket;
    public SocketError Error;
    public int OthersRan;
}

// An async echo server and its clients (the "Async server" section).
public sealed class AsyncEcho
{
    public TcpListener Listener;
    public int Port;
    public int Clients;
    public int Correct;
}

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

    static void Same(string name, string actual, string expected)
    {
        Check(name, actual == expected);
        if (actual != expected)
            Out.Line($"    got \"{actual}\", expected \"{expected}\"");
    }

    static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    // Reads until the stream ends, and says why it did: Success if the
    // other end finished, or how the connection broke.
    static SocketError DrainUntilBroken(TcpClient client, Failures waiting)
    {
        NetworkStream stream = client.GetStream();
        Span<byte> buffer = stackalloc byte[64];
        if (waiting != null)
            waiting.Waiting = true;
        while (stream.Read(buffer) > 0) { }
        return stream.Error;
    }

    // docs:serve
    // Each connection gets a fiber: it reads what the client sends and
    // writes it back, until the client shuts its sending down.
    static void Serve(TcpClient client)
    {
        using NetworkStream stream = client.GetStream();
        Span<byte> buffer = stackalloc byte[512];
        int n;
        while ((n = stream.Read(buffer)) > 0)
            stream.Write(buffer.Slice(0, n));
    }
    // docs:end

    // docs:encrypted-handshake
    // Both ends send a fresh X25519 public key and agree on a secret; the
    // stream then makes keys from it and random bytes each end adds.
    // Nothing here proves who is at the other end: that's what TLS's
    // certificates add.
    static EncryptedStream Encrypt(NetworkStream stream, bool isInitiator)
    {
        Span<byte> mine = stackalloc byte[32], myPublic = stackalloc byte[32];
        Span<byte> theirPublic = stackalloc byte[32], shared = stackalloc byte[32];
        X25519.GeneratePrivateKey(mine);
        X25519.GetPublicKey(mine, myPublic);
        stream.Write(myPublic);
        stream.ReadExactly(theirPublic);
        if (!X25519.TryGetSharedSecret(mine, theirPublic, shared))
            throw new IOException("the other end sent a bad key");
        EncryptedStream secure =
            EncryptedStream.FromSharedSecret(stream, shared, isInitiator);
        CryptographicOperations.ZeroMemory(mine);
        CryptographicOperations.ZeroMemory(shared);
        return secure;
    }
    // docs:end

    // docs:serve-catch
    // The same with a Catch.Run of its own, which every fiber that should
    // survive a throw needs: a throw while serving (here, for a message
    // that starts with '!') ends this connection, not the program.
    static bool ServeGuarded(TcpClient client)
    {
        Exception error = Catch.Run(ref client, static (ref TcpClient c) =>
        {
            using NetworkStream stream = c.GetStream();
            Span<byte> buffer = stackalloc byte[512];
            int n;
            while ((n = stream.Read(buffer)) > 0)
            {
                if (buffer[0] == (byte)'!')
                    throw new IOException("a bad request");
                stream.Write(buffer.Slice(0, n));
            }
        });
        client.Dispose(); // runs either way: Catch.Run always returns
        return error == null;
    }
    // docs:end

    static async Task ReadAsync(AsyncPair p)
    {
        await Task.Yield(); // from here on, in the async methods' fiber
        p.Read = p.Reader.Read(p.Buffer);
    }

    static async Task WriteAsync(AsyncPair p)
    {
        await Task.Yield();
        p.WriterRan = true;
        p.Writer.Write("hello"u8);
    }

    // docs:async-serve
    // The async way: each connection is an async method rather than a
    // fiber. A wait costs no fiber, so thousands of connections can wait
    // at once.
    static async Task ServeAsync(TcpClient client)
    {
        using NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[512];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
            await stream.WriteAsync(buffer.AsMemory(0, n));
    }
    // docs:end

    static async Task AcceptAsync(AsyncEcho e)
    {
        for (int i = 0; i < e.Clients; i++)
        {
            TcpClient client = await e.Listener.AcceptTcpClientAsync();
            _ = ServeAsync(client);
        }
    }

    static async Task ClientAsync(AsyncEcho e, int id)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, e.Port);
        NetworkStream stream = client.GetStream();
        byte[] message = new byte[100];
        message.AsSpan().Fill((byte)id);
        await stream.WriteAsync(message);
        client.Client.Shutdown(SocketShutdown.Send);
        byte[] back = new byte[100];
        await stream.ReadExactlyAsync(back);
        if (Same(back, message) && await stream.ReadAsync(back) == 0)
            e.Correct++;
    }

    // Round trips of one byte over a connection: what the loop allocates
    // once it's going.
    static async Task<long> PingPongAsync(NetworkStream a, NetworkStream b, int rounds)
    {
        byte[] ping = [1], pong = new byte[1];
        long before = 0;
        for (int i = 0; i < rounds + 10; i++)
        {
            if (i == 10)
                before = GC.GetTotalAllocatedBytes(); // after warming up
            ValueTask<int> reading = b.ReadAsync(pong); // waits
            await a.WriteAsync(ping);
            if (await reading != 1)
                return -1;
        }
        return GC.GetTotalAllocatedBytes() - before;
    }

    static async Task AsyncChecksAsync()
    {
        // An echo server and clients, all async methods.
        var echo = new AsyncEcho { Listener = new TcpListener(IPAddress.Loopback, 0), Clients = 50 };
        echo.Listener.Start();
        echo.Port = ((IPEndPoint)echo.Listener.LocalEndpoint).Port;
        Task server = AcceptAsync(echo);
        var clients = new Task[echo.Clients];
        for (int id = 0; id < clients.Length; id++)
            clients[id] = ClientAsync(echo, id);
        await Task.WhenAll(clients);
        await server;
        Check("every echo came back", echo.Correct == echo.Clients);

        var pair = new TcpListener(IPAddress.Loopback, 0);
        pair.Start();
        using var near = new TcpClient();
        Task connecting = near.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pair.LocalEndpoint).Port);
        using TcpClient far = await pair.AcceptTcpClientAsync();
        await connecting;
        NetworkStream a = near.GetStream(), b = far.GetStream();
        long allocated = await PingPongAsync(a, b, 100);
        Check($"a round trip allocates nothing ({allocated} bytes)", allocated == 0);

        IPAddress[] local = await Dns.GetHostAddressesAsync("localhost");
        Check("name looked up", local.Length > 0);
        using (var named = new TcpClient())
        {
            await named.ConnectAsync("localhost", ((IPEndPoint)pair.LocalEndpoint).Port);
            Check("connected by name", named.Connected);
            using TcpClient answer = await pair.AcceptTcpClientAsync();
        }
        Section("Async server");

        // docs:cancel
        // Canceled or timed out, NetworkStream's ReadAsync returns 0, and
        // Error says why; the stream stays usable. So a timeout doesn't
        // need an await of a canceled task, which would end the program.
        byte[] buffer = new byte[16];
        using (var cts = new CancellationTokenSource(30))
        {
            int n = await a.ReadAsync(buffer, cts.Token);
            // n is 0, and a.Error is SocketError.OperationAborted
            Check("canceled read", n == 0 && a.Error == SocketError.OperationAborted);
        }
        a.ReadTimeout = 30; // milliseconds, for ReadAsync too
        int timedOut = await a.ReadAsync(buffer);
        // timedOut is 0, and a.Error is SocketError.TimedOut
        // docs:end
        Check("timed out read", timedOut == 0 && a.Error == SocketError.TimedOut);
        a.ReadTimeout = Timeout.Infinite;
        await b.WriteAsync("z"u8.ToArray());
        Check("read after", await a.ReadAsync(buffer) == 1 && buffer[0] == 'z' && a.Error == SocketError.Success);

        // Socket's own methods cancel their task, as in .NET.
        var source = new CancellationTokenSource();
        Task<int> receiving = near.Client.ReceiveAsync(buffer, source.Token).AsTask();
        Task<Socket> accepting = pair.AcceptSocketAsync(source.Token).AsTask();
        source.Cancel();
        Check("receive canceled", receiving.IsCanceled);
        Check("accept canceled", accepting.IsCanceled);
        await b.WriteAsync("w"u8.ToArray());
        Check("socket receive after", await near.Client.ReceiveAsync(buffer) == 1 && buffer[0] == 'w');

        // Closing the socket wakes a read waiting on it.
        ValueTask<int> orphan = b.ReadAsync(buffer);
        far.Client.Close();
        Check("closed under a read", await orphan == 0 && b.Error == SocketError.OperationAborted);
        Check("stays closed", await b.ReadAsync(buffer) == 0 && b.Error == SocketError.OperationAborted);

        // UDP.
        using var udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpClient = new UdpClient();
        var to = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)udpServer.Client.LocalEndPoint).Port);
        Task<UdpReceiveResult> datagram = udpServer.ReceiveAsync();
        await udpClient.SendAsync("ping"u8.ToArray(), to);
        UdpReceiveResult got = await datagram;
        Check("datagram", Same(got.Buffer, "ping"u8) && got.RemoteEndPoint.Port == ((IPEndPoint)udpClient.Client.LocalEndPoint).Port);
        echo.Listener.Stop();

        // docs:stop
        // Stopping the listener cancels the accept that waits on it. Awaited
        // as a plain Task with SuppressThrowing, a canceled task doesn't end
        // the program: the loop looks at IsCanceled instead.
        Task<TcpClient> next = pair.AcceptTcpClientAsync();
        pair.Stop();
        await ((Task)next).ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        bool stopped = next.IsCanceled; // true
        // docs:end
        Check("accept loop stopped", stopped);
        Section("Async errors");
    }

    public static int Main()
    {
        bool small = Fiber.DefaultStackSize < 64 * 1024;

        // docs:addresses
        // Addresses parse and print as in .NET; IPv6 prints in its
        // shortest form.
        IPAddress v4 = IPAddress.Parse("192.168.1.10");
        IPAddress v6 = IPAddress.Parse("2001:0db8:0000:0000:0000:0000:0000:0001");
        var endpoint = new IPEndPoint(IPAddress.IPv6Loopback, 8080);
        // v6 prints as 2001:db8::1, endpoint as [::1]:8080
        // docs:end
        Same("v4", v4.ToString(), "192.168.1.10");
        Same("v6", v6.ToString(), "2001:db8::1");
        Same("endpoint", endpoint.ToString(), "[::1]:8080");
        Same("v4 endpoint", new IPEndPoint(IPAddress.Loopback, 80).ToString(), "127.0.0.1:80");
        Same("::", IPAddress.IPv6Any.ToString(), "::");
        Same("mapped", IPAddress.Loopback.MapToIPv6().ToString(), "::ffff:127.0.0.1");
        Same("embedded v4", IPAddress.Parse("::ffff:10.0.0.1").MapToIPv4().ToString(), "10.0.0.1");
        Same("longest zeros", IPAddress.Parse("1:0:0:2:0:0:0:3").ToString(), "1:0:0:2::3");
        Same("one zero isn't ::", IPAddress.Parse("1:2:3:4:5:6:0:8").ToString(), "1:2:3:4:5:6:0:8");
        Same("scope", IPAddress.Parse("fe80::1%3").ToString(), "fe80::1%3");
        Same("upper case", IPAddress.Parse("FE80::ABCD").ToString(), "fe80::abcd");
        Check("bytes", IPAddress.Parse("1.2.3.4").GetAddressBytes()[3] == 4);
        Check("equal", IPAddress.Parse("::1").Equals(IPAddress.IPv6Loopback));
        Check("loopback", IPAddress.IsLoopback(IPAddress.Parse("127.5.5.5")) && IPAddress.IsLoopback(IPAddress.IPv6Loopback));
        string[] wrong = ["", "1.2.3", "1.2.3.4.5", "256.1.1.1", "1.2.3.x", ":::", "1::2::3", "12345::", "1:2:3:4:5:6:7:8:9", "1:", "fe80::1%x"];
        int refused = 0;
        foreach (string text in wrong)
            if (!IPAddress.TryParse(text, out _))
                refused++;
        Check("bad addresses refused", refused == wrong.Length);
        Check("endpoint parse", IPEndPoint.TryParse("10.0.0.1:443", out IPEndPoint parsed)
            && parsed.Port == 443 && parsed.Address.ToString() == "10.0.0.1");
        Check("v6 endpoint parse", IPEndPoint.TryParse("[::1]:80", out parsed) && parsed.Port == 80);
        Section("Addresses");

        // docs:echo
        // A server fiber accepts connections and gives each a fiber; client
        // fibers connect, send, and read the echo. Every call that waits
        // lets the other fibers run.
        var echo = new Echo
        {
            Listener = new TcpListener(IPAddress.Loopback, 0), // any free port
            Clients = small ? 3 : 20,
        };
        echo.Listener.Start();
        echo.Port = ((IPEndPoint)echo.Listener.LocalEndpoint).Port;
        Fiber.Spawn(echo, static e =>
        {
            while (e.Served < e.Clients)
            {
                // A failure (too many sockets open, say) waits and tries
                // again, rather than ending the program.
                if (!e.Listener.TryAcceptTcpClient(out TcpClient client, out _))
                {
                    Fiber.Sleep(10);
                    continue;
                }
                e.Served++;
                Fiber.Spawn(client, static c => Serve(c));
            }
        });
        for (int id = 0; id < echo.Clients; id++)
            Fiber.Spawn((echo, id), static s =>
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, s.echo.Port);
                NetworkStream stream = client.GetStream();
                Span<byte> message = stackalloc byte[100];
                message.Fill((byte)s.id);
                stream.Write(message);
                client.Client.Shutdown(SocketShutdown.Send);
                Span<byte> back = stackalloc byte[100];
                stream.ReadExactly(back);
                if (Same(back, message) && stream.Read(back) == 0)
                    s.echo.Correct++;
            });
        Fiber.Run();
        echo.Listener.Stop();
        // docs:end
        Check("all served", echo.Served == echo.Clients);
        Check("all echoed", echo.Correct == echo.Clients);
        Section("Echo");

        // Two fibers on one connection: one sends 2 MB (256 KB on a small
        // machine) and must wait whenever the system's buffers are full,
        // while the other reads the echo back.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Fiber.Spawn(listener, static l => Serve(l.AcceptTcpClient()));
        var bulk = new Bulk { Size = small ? 256 * 1024 : 2 * 1024 * 1024 };
        var connection = new TcpClient();
        connection.Connect(IPAddress.Loopback, port);
        bulk.Stream = connection.GetStream();
        Fiber.Spawn(bulk, static b =>
        {
            Span<byte> chunk = stackalloc byte[256];
            for (int sent = 0; sent < b.Size; sent += chunk.Length)
            {
                for (int i = 0; i < chunk.Length; i++)
                    chunk[i] = (byte)((sent + i) % 251);
                b.Stream.Write(chunk);
            }
            b.Stream.Socket.Shutdown(SocketShutdown.Send);
        });
        Fiber.Spawn(bulk, static b =>
        {
            Span<byte> chunk = stackalloc byte[256];
            int n;
            while ((n = b.Stream.Read(chunk)) > 0)
            {
                for (int i = 0; i < n; i++)
                    if (chunk[i] != (byte)((b.Received + i) % 251))
                        b.Ordered = false;
                b.Received += n;
            }
        });
        Fiber.Run();
        connection.Dispose();
        listener.Stop();
        Check("every byte back", bulk.Received == bulk.Size);
        Check("in order", bulk.Ordered);
        Section("Backpressure");

        // An echo server again, through EncryptedStream: 20000 bytes go out
        // in two records and come back in records the server writes.
        var secure = new Secure { Listener = new TcpListener(IPAddress.Loopback, 0) };
        secure.Listener.Start();
        secure.Port = ((IPEndPoint)secure.Listener.LocalEndpoint).Port;
        Fiber.Spawn(secure, static s =>
        {
            using TcpClient client = s.Listener.AcceptTcpClient();
            using EncryptedStream stream = Encrypt(client.GetStream(), isInitiator: false);
            Span<byte> buffer = stackalloc byte[512];
            int n;
            while ((n = stream.Read(buffer)) > 0)   // 0 once the client closes
                stream.Write(buffer.Slice(0, n));
        });                                         // Dispose: the server closes too
        Fiber.Spawn(secure, static s =>
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, s.Port);
            using EncryptedStream stream = Encrypt(client.GetStream(), isInitiator: true);
            byte[] message = new byte[20000];
            for (int i = 0; i < message.Length; i++)
                message[i] = (byte)(i % 253);
            stream.Write(message);
            stream.Shutdown();
            Span<byte> chunk = stackalloc byte[512];
            s.Correct = true;
            int n;
            while ((n = stream.Read(chunk)) > 0)
            {
                for (int i = 0; i < n; i++)
                    if (chunk[i] != (byte)((s.Received + i) % 253))
                        s.Correct = false;
                s.Received += n;
            }
        });
        Fiber.Run();
        secure.Listener.Stop();
        Check("encrypted echo", secure.Received == 20000 && secure.Correct);
        Section("Encrypted");

        // A client that resets its connection, and a connection closed from
        // another fiber while its reader waits, end only their own streams:
        // the server goes on serving.
        var failures = new Failures { Listener = new TcpListener(IPAddress.Loopback, 0) };
        failures.Listener.Start();
        failures.Port = ((IPEndPoint)failures.Listener.LocalEndpoint).Port;
        Fiber.Spawn(failures, static f =>
        {
            for (int i = 0; i < 3; i++)
            {
                if (!f.Listener.TryAcceptTcpClient(out TcpClient client, out _))
                    return;
                if (i == 0)
                    Fiber.Spawn((f, client), static s =>
                    {
                        s.client.GetStream().Write("hello"u8);
                        s.f.Reset = DrainUntilBroken(s.client, null);
                    });
                else if (i == 1)
                {
                    f.Victim = client;
                    Fiber.Spawn((f, client), static s => s.f.Closed = DrainUntilBroken(s.client, s.f));
                }
                else
                    Fiber.Spawn(client, static c => Serve(c));
            }
        });
        Fiber.Spawn(failures, static f =>
        {
            // docs:reset
            // Closing with LingerOption(true, 0), or with data still unread,
            // resets the connection: the other end's Read returns 0, with
            // Error ConnectionReset.
            var rude = new TcpClient();
            rude.Connect(IPAddress.Loopback, f.Port);
            rude.GetStream().Write("x"u8);
            Span<byte> peek = stackalloc byte[1];
            rude.Client.Receive(peek, SocketFlags.Peek); // the greeting: left unread
            rude.Client.LingerState = new LingerOption(true, 0);
            rude.Dispose();
            // docs:end

            var quiet = new TcpClient();
            quiet.Connect(IPAddress.Loopback, f.Port);
            while (!f.Waiting)
                Fiber.Yield();
            f.Victim.Dispose(); // its reader, waiting in Read, wakes
            quiet.Dispose();

            using var polite = new TcpClient();
            polite.Connect(IPAddress.Loopback, f.Port);
            NetworkStream stream = polite.GetStream();
            stream.Write("after"u8);
            polite.Client.Shutdown(SocketShutdown.Send);
            Span<byte> back = stackalloc byte[5];
            stream.ReadExactly(back);
            f.EchoedAfter = Same(back, "after"u8);
        });
        Fiber.Run();
        failures.Listener.Stop();
        // Wine, which runs this sample's Windows build in CI, closes in order
        // even then (real Windows resets): there the stream may simply end.
        bool wine = Path.Separator == '\\' && failures.Reset == SocketError.Success;
        Check("reset: ConnectionReset", failures.Reset == SocketError.ConnectionReset || wine);
        Check("closed under a reader: OperationAborted", failures.Closed == SocketError.OperationAborted);
        Check("served after both", failures.EchoedAfter);

        // docs:refused
        // Outside a fiber a call simply waits. TryConnect says why it
        // couldn't connect, rather than ending the program.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nothing listens there now
        using var socket = new Socket(AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        bool connected = socket.TryConnect(
            new IPEndPoint(IPAddress.Loopback, closedPort),
            out SocketError error);
        // connected is false, and error is SocketError.ConnectionRefused
        // docs:end
        Check("refused", !connected && error == SocketError.ConnectionRefused);
        Check("not connected", !socket.Connected);
        Section("Errors");

        // docs:catch
        // Connect throws a SocketException where TryConnect returns false.
        // Catch.Run gets it back, and closes the socket the callback opened
        // and left open.
        Socket attempt = null;
        Exception thrown = Catch.Run(() =>
        {
            attempt = new Socket(AddressFamily.InterNetwork,
                SocketType.Stream, ProtocolType.Tcp);
            attempt.Connect(new IPEndPoint(IPAddress.Loopback, closedPort));
        });
        // thrown is a SocketException, and attempt.Handle is -1
        // docs:end
        Check("Connect's throw comes back", thrown is SocketException refusal
            && refusal.SocketErrorCode == SocketError.ConnectionRefused);
        Check("and the socket it left open is closed", attempt.Handle == -1);

        // Kept, a socket opened inside stays open: for a pool, say.
        Socket pooled = null;
        thrown = Catch.Run(() =>
        {
            pooled = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Catch.Keep(pooled);
            throw new IOException("something else failed");
        });
        Check("a kept socket stays open after a throw", thrown is IOException && pooled.Handle != -1);
        pooled.Dispose();

        // A server whose connections each have a Catch.Run: one client sends
        // a request that makes its handler throw, and the others are served.
        var guarded = new Guarded { Listener = new TcpListener(IPAddress.Loopback, 0) };
        guarded.Listener.Start();
        guarded.Port = ((IPEndPoint)guarded.Listener.LocalEndpoint).Port;
        Fiber.Spawn(guarded, static g =>
        {
            while (g.Served < 3)
            {
                TcpClient client = g.Listener.AcceptTcpClient();
                g.Served++;
                Fiber.Spawn((g, client), static s =>
                {
                    if (!ServeGuarded(s.client))
                        s.g.Failed++;
                });
            }
        });
        for (int id = 0; id < 3; id++)
            Fiber.Spawn((guarded, id), static s =>
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, s.guarded.Port);
                NetworkStream stream = client.GetStream();
                Span<byte> message = stackalloc byte[10];
                message.Fill(s.id == 1 ? (byte)'!' : (byte)('a' + s.id));
                stream.Write(message);
                Span<byte> back = stackalloc byte[10];
                if (s.id == 1)
                {
                    s.guarded.Dropped = stream.Read(back) == 0; // closed, nothing echoed
                    return;
                }
                stream.ReadExactly(back);
                client.Client.Shutdown(SocketShutdown.Send);
                if (Same(back, message) && stream.Read(back) == 0)
                    s.guarded.Echoed++;
            });
        Fiber.Run();
        guarded.Listener.Stop();
        Check("the connection whose serving threw is closed", guarded.Failed == 1 && guarded.Dropped);
        Check("and the others are served", guarded.Echoed == 2);
        Section("Catching");

        // docs:udp
        // UDP: a datagram there and back, between two fibers.
        var udp = new Datagrams
        {
            Server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)),
            Client = new UdpClient(),
        };
        udp.Port = ((IPEndPoint)udp.Server.Client.LocalEndPoint).Port;
        Fiber.Spawn(udp, static u =>
        {
            Span<byte> datagram = stackalloc byte[64];
            int n = u.Server.Receive(datagram, out IPEndPoint from);
            u.Server.Send(datagram.Slice(0, n), from);
        });
        Fiber.Spawn(udp, static u =>
        {
            u.Client.Send("ping"u8, new IPEndPoint(IPAddress.Loopback, u.Port));
            IPEndPoint from = null;
            byte[] reply = u.Client.Receive(ref from);
            u.Echoed = Same(reply, "ping"u8) && from.Port == u.Port;
        });
        Fiber.Run();
        // docs:end
        udp.Server.Dispose();
        udp.Client.Dispose();
        Check("echoed", udp.Echoed);
        Section("UDP");

        // A name lookup in a fiber runs on a thread of its own, so the other
        // fibers run while it waits.
        var lookup = new Lookup();
        Fiber.Spawn(lookup, static l =>
        {
            if (Dns.TryGetHostAddresses("localhost", out IPAddress[] found, out _))
                foreach (IPAddress address in found)
                    l.Found |= IPAddress.IsLoopback(address);
        });
        Fiber.Spawn(lookup, static l => { l.OthersRan++; Fiber.Yield(); l.OthersRan++; });
        Fiber.Run();
        Check("localhost, in a fiber", lookup.Found);
        Check("others ran", lookup.OthersRan == 2);
        Check("localhost, outside", Dns.TryGetHostAddresses("localhost", out IPAddress[] direct, out _) && direct.Length > 0);
        Check("an address is its own answer", Dns.GetHostAddresses("10.1.2.3")[0].ToString() == "10.1.2.3");
        Check("host name", Dns.GetHostName().Length > 0);
        Section("Names");

        // Async methods: after an await they go on in a fiber shared by all
        // of them (lib/Tasks.cs). One that waits for a socket keeps that
        // fiber, and another takes over for the rest: here the writer, which
        // is queued behind the reader and is the only one that can wake it.
        var asyncListener = new TcpListener(IPAddress.Loopback, 0);
        asyncListener.Start();
        var dialer = new TcpClient();
        dialer.Connect(IPAddress.Loopback, ((IPEndPoint)asyncListener.LocalEndpoint).Port);
        TcpClient answered = asyncListener.AcceptTcpClient();
        var pair = new AsyncPair { Reader = answered.GetStream(), Writer = dialer.GetStream() };
        Task reading = ReadAsync(pair);
        Task writing = WriteAsync(pair);
        Fiber.Run();
        Check("both done", reading.IsCompleted && writing.IsCompleted);
        Check("the writer ran", pair.WriterRan);
        Check("read what was written", pair.Read == 5 && pair.Buffer[4] == (byte)'o');
        dialer.Dispose();
        answered.Dispose();
        asyncListener.Stop();
        Section("Async");

        // docs:timeouts
        // A read that waits longer than ReadTimeout returns 0, with Error
        // TimedOut. The connection stays open, and the next read goes on.
        var timed = new TcpListener(IPAddress.Loopback, 0);
        timed.Start();
        using var silent = new TcpClient();
        silent.Connect(IPAddress.Loopback, ((IPEndPoint)timed.LocalEndpoint).Port);
        using TcpClient other = timed.AcceptTcpClient();
        NetworkStream patient = silent.GetStream();
        patient.ReadTimeout = 50; // milliseconds
        Span<byte> one = stackalloc byte[1];
        int got = patient.Read(one);
        // got is 0, and patient.Error is SocketError.TimedOut
        // docs:end
        Check("timed out", got == 0 && patient.Error == SocketError.TimedOut);
        other.GetStream().Write("y"u8);
        Check("read after", patient.Read(one) == 1 && one[0] == 'y' && patient.Error == SocketError.Success);

        silent.Client.ReceiveTimeout = 20;
        long start = Clock.Monotonic;
        silent.Client.Receive(one, SocketFlags.None, out SocketError receiveError);
        long waited = (Clock.Monotonic - start) / 1_000_000;
        Check($"socket receive timed out ({waited} ms)", receiveError == SocketError.TimedOut && waited >= 19 && silent.Connected);

        // In a fiber, the others run while one waits for its timeout.
        var patience = new Patience { Socket = silent.Client };
        Fiber.Spawn(patience, static p =>
        {
            Span<byte> b = stackalloc byte[1];
            p.Socket.Receive(b, SocketFlags.None, out p.Error);
        });
        Fiber.Spawn(patience, static p =>
        {
            for (int i = 0; i < 5; i++, p.OthersRan++)
                Fiber.Sleep(1);
        });
        Fiber.Run();
        Check("fiber timed out", patience.Error == SocketError.TimedOut && patience.OthersRan == 5);

        // A send into a full buffer gives up after SendTimeout, saying how
        // much went.
        other.Client.SendTimeout = 50;
        byte[] chunk = new byte[1 << 20];
        long sent = 0;
        SocketError sendError = SocketError.Success;
        for (int i = 0; i < 256 && sendError == SocketError.Success; i++)
            sent += other.Client.Send(chunk, SocketFlags.None, out sendError);
        Check("send timed out", sendError == SocketError.TimedOut && sent > 0 && other.Connected);
        timed.Stop();
        Section("Timeouts");

        AsyncChecksAsync().Wait();

        return s_failures;
    }
}
