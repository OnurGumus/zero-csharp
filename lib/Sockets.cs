// Sockets: .NET's System.Net.Sockets (Socket, TcpListener, TcpClient,
// UdpClient, NetworkStream), so code written against them compiles here.
//
//   var listener = new TcpListener(IPAddress.Loopback, 8080);
//   listener.Start();
//   Fiber.Spawn(listener, static l =>
//   {
//       while (true)
//       {
//           TcpClient client = l.AcceptTcpClient();
//           Fiber.Spawn(client, static c => Serve(c.GetStream()));
//       }
//   });
//   Fiber.Run();
//
// Every socket is non-blocking underneath, and works with fibers: a call that
// would wait (Accept, Connect, Receive, a Send into a full buffer) parks the
// fiber and lets the others run until the socket is ready. Outside a fiber
// the call simply waits. So one thread can serve many connections, a fiber
// each, without callbacks or async.
//
// What differs from .NET:
// - A throw ends the program unless a Catch.Run is waiting for it
//   (Catch.cs), so a failure .NET reports with a SocketException stops
//   the program with its message, or comes back to that Catch.Run, which
//   closes the sockets opened inside it. The forms that return a
//   SocketError don't throw: Receive and Send with `out SocketError`, and
//   TryConnect and TryAccept, which .NET doesn't have.
// - ReceiveTimeout and SendTimeout are kept here, not by the system (every
//   socket is non-blocking underneath): a call that times out fails with
//   TimedOut, as in .NET, and the socket stays usable.
// - The async methods are in lib/SocketsAsync.cs. No Select or
//   SocketAsyncEventArgs.
// - Linux, macOS and Windows. WebAssembly (WASI preview 1), DOS, UEFI and
//   microcontrollers have no sockets: a program that uses them there fails to
//   link.
//
// Underneath are the C library's socket functions, with Linux's numbers and
// layouts. macOS's differ, and the tables below hold both. win/runtime.c gives
// Windows the Linux ones over Winsock, the names that Winsock also has linked
// with /wrap:. ZeroFcntl, in each system's assembly, calls fcntl: macOS's
// arm64 passes a variadic function's extra arguments on the stack, which a
// call from C# doesn't.

using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets;

/// <summary>The kind of address a socket uses.</summary>
public enum AddressFamily
{
    /// <summary>Not known.</summary>
    Unknown = -1,
    /// <summary>Not given.</summary>
    Unspecified = 0,
    /// <summary>IPv4.</summary>
    InterNetwork = 2,
    /// <summary>IPv6.</summary>
    InterNetworkV6 = 23,
}

/// <summary>What a socket carries.</summary>
public enum SocketType
{
    /// <summary>Not known.</summary>
    Unknown = -1,
    /// <summary>A connection's stream of bytes (TCP).</summary>
    Stream = 1,
    /// <summary>Separate messages (UDP).</summary>
    Dgram = 2,
}

/// <summary>The protocol a socket speaks.</summary>
public enum ProtocolType
{
    /// <summary>Not known.</summary>
    Unknown = -1,
    /// <summary>Whatever the socket type implies.</summary>
    Unspecified = 0,
    /// <summary>TCP.</summary>
    Tcp = 6,
    /// <summary>UDP.</summary>
    Udp = 17,
}

/// <summary>Options for Send and Receive.</summary>
[Flags]
public enum SocketFlags
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Look at what has arrived without taking it.</summary>
    Peek = 2,
}

/// <summary>Which direction Shutdown ends.</summary>
public enum SocketShutdown
{
    /// <summary>No more receiving.</summary>
    Receive = 0,
    /// <summary>No more sending: the other end reads the end of the stream.</summary>
    Send = 1,
    /// <summary>Both.</summary>
    Both = 2,
}

/// <summary>Why a socket operation failed, with .NET's (Winsock's) numbers.</summary>
public enum SocketError
{
    /// <summary>It worked.</summary>
    Success = 0,
    /// <summary>Something not listed here.</summary>
    SocketError = -1,
    /// <summary>The socket was closed while the operation waited.</summary>
    OperationAborted = 995,
    /// <summary>Interrupted.</summary>
    Interrupted = 10004,
    /// <summary>Not allowed (a port below 1024 without privileges, say).</summary>
    AccessDenied = 10013,
    /// <summary>A bad argument.</summary>
    InvalidArgument = 10022,
    /// <summary>The program has too many sockets open.</summary>
    TooManyOpenSockets = 10024,
    /// <summary>It would have to wait (only seen inside lib/).</summary>
    WouldBlock = 10035,
    /// <summary>Under way.</summary>
    InProgress = 10036,
    /// <summary>Already under way.</summary>
    AlreadyInProgress = 10037,
    /// <summary>Not a socket.</summary>
    NotSocket = 10038,
    /// <summary>No address to send to.</summary>
    DestinationAddressRequired = 10039,
    /// <summary>The message is too big.</summary>
    MessageSize = 10040,
    /// <summary>The protocol isn't supported.</summary>
    ProtocolNotSupported = 10043,
    /// <summary>The operation isn't supported.</summary>
    OperationNotSupported = 10045,
    /// <summary>The address family isn't supported.</summary>
    AddressFamilyNotSupported = 10047,
    /// <summary>Another socket has this address and port.</summary>
    AddressAlreadyInUse = 10048,
    /// <summary>This machine has no such address.</summary>
    AddressNotAvailable = 10049,
    /// <summary>The network is down.</summary>
    NetworkDown = 10050,
    /// <summary>The network can't be reached.</summary>
    NetworkUnreachable = 10051,
    /// <summary>The network dropped the connection.</summary>
    NetworkReset = 10052,
    /// <summary>The connection was aborted on this side.</summary>
    ConnectionAborted = 10053,
    /// <summary>The other end reset the connection.</summary>
    ConnectionReset = 10054,
    /// <summary>Out of buffer space.</summary>
    NoBufferSpaceAvailable = 10055,
    /// <summary>Already connected.</summary>
    IsConnected = 10056,
    /// <summary>Not connected.</summary>
    NotConnected = 10057,
    /// <summary>The socket was shut down for this direction.</summary>
    Shutdown = 10058,
    /// <summary>The other end didn't answer in time.</summary>
    TimedOut = 10060,
    /// <summary>Nothing listens at that address and port.</summary>
    ConnectionRefused = 10061,
    /// <summary>The host is down.</summary>
    HostDown = 10064,
    /// <summary>The host can't be reached.</summary>
    HostUnreachable = 10065,
    /// <summary>No such host name.</summary>
    HostNotFound = 11001,
    /// <summary>The name servers didn't answer; try again.</summary>
    TryAgain = 11002,
    /// <summary>The name servers failed.</summary>
    NoRecovery = 11003,
    /// <summary>The name has no address of the kind asked for.</summary>
    NoData = 11004,
}

/// <summary>A socket failure. Thrown, it ends the program with its message, unless a Catch.Run is waiting for it.</summary>
public class SocketException : Exception
{
    /// <summary>The failure for this SocketError number.</summary>
    public SocketException(int errorCode) : base(string.Concat("SocketException: ", Describe((SocketError)errorCode)))
        => SocketErrorCode = (SocketError)errorCode;

    /// <summary>Why it failed.</summary>
    public SocketError SocketErrorCode { get; }

    /// <summary>Why it failed, as a number.</summary>
    public int ErrorCode => (int)SocketErrorCode;

    internal static string Describe(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "connection refused",
        SocketError.ConnectionReset => "connection reset by the other end",
        SocketError.ConnectionAborted => "connection aborted",
        SocketError.AddressAlreadyInUse => "address already in use",
        SocketError.AddressNotAvailable => "address not available",
        SocketError.AccessDenied => "access denied",
        SocketError.TimedOut => "timed out",
        SocketError.HostNotFound => "host not found",
        SocketError.TryAgain => "the name servers didn't answer",
        SocketError.NoData => "the name has no address",
        SocketError.NetworkUnreachable => "network unreachable",
        SocketError.HostUnreachable => "host unreachable",
        SocketError.NotConnected => "not connected",
        SocketError.Shutdown => "the socket was shut down",
        SocketError.OperationAborted => "the socket was closed",
        SocketError.InvalidArgument => "invalid argument",
        SocketError.TooManyOpenSockets => "too many open sockets",
        _ => ((int)error).ToString(),
    };
}

/// <summary>Socket.LingerState: whether, and for how many seconds, Close waits to send what's left.</summary>
public class LingerOption
{
    /// <summary>Lingering on or off, for this many seconds.</summary>
    public LingerOption(bool enable, int seconds)
    {
        Enabled = enable;
        LingerTime = seconds;
    }

    /// <summary>Whether Close lingers. On with 0 seconds resets the connection.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many seconds Close may wait.</summary>
    public int LingerTime { get; set; }
}

/// <summary>
/// A network endpoint to send and receive through: TCP or UDP, IPv4 or IPv6.
/// Calls that would wait let other fibers run (see the file's comment).
/// </summary>
public partial class Socket : IDisposable
{
    int _fd;
    bool _connected;
    Catch.Ticket _ticket;  // for a Catch.Run to close the socket after a throw
    int _receiveTimeout, _sendTimeout; // milliseconds; 0: none

    /// <summary>A socket for the family, type and protocol. Failing throws a SocketException.</summary>
    public Socket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
    {
        SocketError error = SocketNative.Create(addressFamily, socketType, protocolType, out _fd);
        if (error != SocketError.Success)
            throw new SocketException((int)error);
        unsafe { _ticket = Catch.Opened(_fd, &SocketNative.CloseHandle); } // SAFETY: CloseHandle closes a socket's number
        AddressFamily = addressFamily;
        SocketType = socketType;
        ProtocolType = protocolType == ProtocolType.Unspecified
            ? (socketType == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp)
            : protocolType;
    }

    /// <summary>An IPv6 socket that also takes IPv4 (DualMode), as .NET makes it.</summary>
    public Socket(SocketType socketType, ProtocolType protocolType)
        : this(AddressFamily.InterNetworkV6, socketType, protocolType)
        => DualMode = true;

    internal Socket(int fd, AddressFamily family, SocketType type, ProtocolType protocol)
    {
        _fd = fd;
        _connected = true;
        unsafe { _ticket = Catch.Opened(_fd, &SocketNative.CloseHandle); } // SAFETY: CloseHandle closes a socket's number
        AddressFamily = family;
        SocketType = type;
        ProtocolType = protocol;
    }

    /// <summary>IPv4 or IPv6.</summary>
    public AddressFamily AddressFamily { get; }
    /// <summary>Stream (TCP) or Dgram (UDP).</summary>
    public SocketType SocketType { get; }
    /// <summary>Tcp or Udp.</summary>
    public ProtocolType ProtocolType { get; }
    /// <summary>The system's handle for the socket; -1 once closed.</summary>
    public nint Handle => Number;
    /// <summary>Whether the last Connect or Accept made a connection, and it hasn't failed or closed since.</summary>
    public bool Connected => _connected && Number >= 0;

    /// <summary>The address and port this end has: after Bind, Connect or Accept.</summary>
    public EndPoint LocalEndPoint => Number < 0 ? null : SocketNative.Name(_fd, peer: false);

    /// <summary>The address and port of the other end, once connected.</summary>
    public EndPoint RemoteEndPoint => Number < 0 || !_connected ? null : SocketNative.Name(_fd, peer: true);

    /// <summary>Whether small writes go out at once (TCP_NODELAY), rather than gathered.</summary>
    public bool NoDelay
    {
        get => SocketNative.GetOption(Fd, SocketNative.Option.NoDelay) != 0;
        set => SocketNative.SetOption(Fd, SocketNative.Option.NoDelay, value ? 1 : 0);
    }

    /// <summary>Whether an IPv6 socket also takes IPv4 (as ::ffff:a.b.c.d). Set it before Bind or Connect.</summary>
    public bool DualMode
    {
        get => AddressFamily == AddressFamily.InterNetworkV6 && SocketNative.GetOption(Fd, SocketNative.Option.V6Only) == 0;
        set
        {
            if (AddressFamily != AddressFamily.InterNetworkV6)
                throw new NotSupportedException("Socket.DualMode: only for an IPv6 socket");
            SocketNative.SetOption(Fd, SocketNative.Option.V6Only, value ? 0 : 1);
        }
    }

    /// <summary>
    /// What closing does with data not yet sent. LingerOption(true, 0) makes
    /// Close reset the connection at once (the other end gets ConnectionReset)
    /// instead of ending it in order.
    /// </summary>
    public LingerOption LingerState
    {
        get
        {
            SocketNative.GetLinger(Fd, out bool on, out int seconds);
            return new LingerOption(on, seconds);
        }
        set
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            SocketNative.SetLinger(Fd, value.Enabled, value.LingerTime);
        }
    }

    /// <summary>Whether a UDP socket may send to a broadcast address.</summary>
    public bool EnableBroadcast
    {
        get => SocketNative.GetOption(Fd, SocketNative.Option.Broadcast) != 0;
        set => SocketNative.SetOption(Fd, SocketNative.Option.Broadcast, value ? 1 : 0);
    }

    /// <summary>
    /// How long, in milliseconds, Receive and ReceiveFrom wait before failing
    /// with TimedOut: 0 (the default) or -1 waits for ever. The socket stays
    /// usable after a timeout. The async methods don't use it, as in .NET:
    /// give them a CancellationToken.
    /// </summary>
    public int ReceiveTimeout
    {
        get => _receiveTimeout;
        set => _receiveTimeout = CheckTimeout(value);
    }

    /// <summary>
    /// How long, in milliseconds, Send and SendTo wait for room before failing
    /// with TimedOut: 0 (the default) or -1 waits for ever. Send then returns
    /// how many bytes went before the timeout.
    /// </summary>
    public int SendTimeout
    {
        get => _sendTimeout;
        set => _sendTimeout = CheckTimeout(value);
    }

    static int CheckTimeout(int milliseconds)
    {
        if (milliseconds < -1)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return milliseconds == -1 ? 0 : milliseconds;
    }

    // An operation failed with this: is the connection gone? Not after a
    // timeout, as in .NET.
    internal void Failed(SocketError error)
    {
        if (error != SocketError.Success && error != SocketError.TimedOut && error != SocketError.WouldBlock)
            _connected = false;
    }

    internal void SetConnected(bool connected) => _connected = connected;

    /// <summary>Gives the socket an address and port of this machine. Failing throws a SocketException.</summary>
    public void Bind(EndPoint localEP)
    {
        SocketError error = SocketNative.Bind(Fd, ToIP(localEP));
        if (error != SocketError.Success)
            throw new SocketException((int)error);
    }

    /// <summary>Starts taking connections, with room for <paramref name="backlog"/> waiting to be accepted.</summary>
    public void Listen(int backlog = 512)
    {
        SocketError error = SocketNative.Listen(Fd, backlog);
        if (error != SocketError.Success)
            throw new SocketException((int)error);
    }

    /// <summary>The next connection, waiting for one (other fibers run meanwhile). Failing throws a SocketException.</summary>
    public Socket Accept()
    {
        if (!TryAccept(out Socket socket, out SocketError error))
            throw new SocketException((int)error);
        return socket;
    }

    /// <summary>The next connection, waiting for one, or why there isn't one.</summary>
    public bool TryAccept(out Socket socket, out SocketError error)
    {
        if (Number < 0)
        {
            socket = null;
            error = SocketError.OperationAborted;
            return false;
        }
        error = SocketNative.Accept(_fd, out int fd);
        socket = error == SocketError.Success ? new Socket(fd, AddressFamily, SocketType, ProtocolType) : null;
        return socket != null;
    }

    /// <summary>Connects to the endpoint, waiting until it answers (other fibers run meanwhile). Failing throws a SocketException.</summary>
    public void Connect(EndPoint remoteEP)
    {
        if (!TryConnect(remoteEP, out SocketError error))
            throw new SocketException((int)error);
    }

    /// <summary>Connects to the address and port. Failing throws a SocketException.</summary>
    public void Connect(IPAddress address, int port) => Connect(new IPEndPoint(address, port));

    /// <summary>
    /// Connects to a host name's port, trying each of its addresses (of this
    /// socket's family) in turn. Failing throws a SocketException.
    /// </summary>
    public void Connect(string host, int port)
    {
        if (!TryConnect(host, port, out SocketError error))
            throw new SocketException((int)error);
    }

    /// <summary>Connects to the endpoint, or says why it couldn't: ConnectionRefused, TimedOut, HostUnreachable...</summary>
    public bool TryConnect(EndPoint remoteEP, out SocketError error)
    {
        if (Number < 0)
        {
            error = SocketError.OperationAborted;
            return false;
        }
        error = SocketNative.Connect(_fd, ToIP(remoteEP));
        _connected = error == SocketError.Success;
        return _connected;
    }

    /// <summary>Connects to a host name's port, trying each address in turn, or says why it couldn't.</summary>
    public bool TryConnect(string host, int port, out SocketError error)
    {
        if (!Dns.TryGetHostAddresses(host, out IPAddress[] addresses, out error))
            return false;
        error = SocketError.HostNotFound;
        foreach (IPAddress address in addresses)
        {
            IPAddress target = address;
            if (address.AddressFamily != AddressFamily)
            {
                if (AddressFamily == AddressFamily.InterNetworkV6 && DualMode)
                    target = address.MapToIPv6();
                else
                    continue;
            }
            if (TryConnect(new IPEndPoint(target, port), out error))
                return true;
        }
        return false;
    }

    /// <summary>Sends all of the bytes, waiting while the system's buffer is full. Failing throws a SocketException.</summary>
    public int Send(ReadOnlySpan<byte> buffer) => Send(buffer, SocketFlags.None);

    /// <summary>Sends all of the bytes. Failing throws a SocketException.</summary>
    public int Send(ReadOnlySpan<byte> buffer, SocketFlags socketFlags)
    {
        int sent = Send(buffer, socketFlags, out SocketError error);
        if (error != SocketError.Success)
            throw new SocketException((int)error);
        return sent;
    }

    /// <summary>Sends all of the bytes, or as many as went before a failure, which <paramref name="errorCode"/> says.</summary>
    public int Send(ReadOnlySpan<byte> buffer, SocketFlags socketFlags, out SocketError errorCode)
    {
        if (Number < 0)
        {
            errorCode = SocketError.OperationAborted;
            return 0;
        }
        errorCode = SocketNative.Send(_fd, buffer, socketFlags, null, out int sent, SocketNative.Deadline(_sendTimeout));
        Failed(errorCode);
        return sent;
    }

    /// <summary>Sends all of the bytes. Failing throws a SocketException.</summary>
    public int Send(byte[] buffer) => Send(new ReadOnlySpan<byte>(buffer ?? throw new ArgumentNullException(nameof(buffer))));

    /// <summary>Sends <paramref name="size"/> bytes from offset. Failing throws a SocketException.</summary>
    public int Send(byte[] buffer, int offset, int size, SocketFlags socketFlags)
    {
        Check(buffer, offset, size);
        return Send(new ReadOnlySpan<byte>(buffer, offset, size), socketFlags);
    }

    /// <summary>
    /// Receives what has arrived, up to buffer.Length bytes, waiting while
    /// nothing has (other fibers run meanwhile). Returns 0 when the other end
    /// has shut its sending down. Failing throws a SocketException.
    /// </summary>
    public int Receive(Span<byte> buffer) => Receive(buffer, SocketFlags.None);

    /// <summary>Receive, with flags (Peek). Failing throws a SocketException.</summary>
    public int Receive(Span<byte> buffer, SocketFlags socketFlags)
    {
        int received = Receive(buffer, socketFlags, out SocketError error);
        if (error != SocketError.Success)
            throw new SocketException((int)error);
        return received;
    }

    /// <summary>Receive, saying why it failed (ConnectionReset...) instead of ending the program.</summary>
    public int Receive(Span<byte> buffer, SocketFlags socketFlags, out SocketError errorCode)
    {
        if (Number < 0)
        {
            errorCode = SocketError.OperationAborted;
            return 0;
        }
        errorCode = SocketNative.Receive(_fd, buffer, socketFlags, out int received, null, SocketNative.Deadline(_receiveTimeout));
        Failed(errorCode);
        return received;
    }

    /// <summary>Receive into an array. Failing throws a SocketException.</summary>
    public int Receive(byte[] buffer) => Receive(new Span<byte>(buffer ?? throw new ArgumentNullException(nameof(buffer))));

    /// <summary>Receive up to <paramref name="size"/> bytes into buffer from offset. Failing throws a SocketException.</summary>
    public int Receive(byte[] buffer, int offset, int size, SocketFlags socketFlags)
    {
        Check(buffer, offset, size);
        return Receive(new Span<byte>(buffer, offset, size), socketFlags);
    }

    /// <summary>Sends a datagram to the endpoint (UDP). Failing throws a SocketException.</summary>
    public int SendTo(ReadOnlySpan<byte> buffer, EndPoint remoteEP)
    {
        SocketError error = SocketNative.Send(Fd, buffer, SocketFlags.None, ToIP(remoteEP), out int sent, SocketNative.Deadline(_sendTimeout));
        if (error != SocketError.Success)
            throw new SocketException((int)error);
        return sent;
    }

    /// <summary>Sends a datagram to the endpoint (UDP). Failing throws a SocketException.</summary>
    public int SendTo(byte[] buffer, EndPoint remoteEP) => SendTo(new ReadOnlySpan<byte>(buffer ?? throw new ArgumentNullException(nameof(buffer))), remoteEP);

    /// <summary>
    /// Receives a datagram (UDP), waiting for one, and says where it came from.
    /// Bytes past buffer.Length are lost. Failing throws a SocketException.
    /// </summary>
    public int ReceiveFrom(Span<byte> buffer, ref EndPoint remoteEP)
    {
        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
        SocketError error = SocketNative.Receive(Fd, buffer, SocketFlags.None, out int received, from, SocketNative.Deadline(_receiveTimeout));
        if (error != SocketError.Success)
            throw new SocketException((int)error);
        remoteEP = from;
        return received;
    }

    /// <summary>Receives a datagram into an array (UDP). Failing throws a SocketException.</summary>
    public int ReceiveFrom(byte[] buffer, ref EndPoint remoteEP)
        => ReceiveFrom(new Span<byte>(buffer ?? throw new ArgumentNullException(nameof(buffer))), ref remoteEP);

    /// <summary>Ends receiving, sending (the other end reads the end of the stream), or both.</summary>
    public void Shutdown(SocketShutdown how)
    {
        SocketError error = SocketNative.Shutdown(Fd, how);
        if (error != SocketError.Success && error != SocketError.NotConnected)
            throw new SocketException((int)error);
    }

    /// <summary>
    /// Closes the socket. Fibers waiting on it wake, and their calls fail with
    /// OperationAborted (NetworkStream's Read returns 0): closing a socket from
    /// another fiber is how to give up on a connection.
    /// </summary>
    public void Close()
    {
        if (Number < 0)
            return;
        int fd = _fd;
        _fd = -1;
        _connected = false;
        // Not if a Catch.Run on another thread has just taken it to close.
        bool mine = Catch.Closing(_ticket);
        _ticket = default;
        if (mine)
            SocketNative.Close(fd);
    }

    /// <summary>Closes the socket.</summary>
    public void Dispose() => Close();

    internal int Fd => Number >= 0 ? _fd : throw new ObjectDisposedException("Socket");

    // Catch.Keep: no Catch.Run closes the socket from here on.
    internal void KeepOpen() => Catch.Kept(_ticket);

    // The socket's number, or -1 once it's closed: by Close, or by a
    // Catch.Run after a throw (Catch.cs), which the socket learns here, at
    // its next use.
    int Number
    {
        get
        {
            if (_fd >= 0 && Catch.ClosedByThrow(_ticket))
            {
                _fd = -1;
                _connected = false;
            }
            return _fd;
        }
    }

    internal IPEndPoint ToIP(EndPoint endPoint)
    {
        if (endPoint is not IPEndPoint ip)
            throw new ArgumentException("Socket: only IPEndPoint addresses");
        if (ip.AddressFamily != AddressFamily)
        {
            // An IPv4 address on a dual-mode IPv6 socket.
            if (AddressFamily == AddressFamily.InterNetworkV6 && ip.AddressFamily == AddressFamily.InterNetwork)
                return new IPEndPoint(ip.Address.MapToIPv6(), ip.Port);
            throw new ArgumentException("Socket: the address's family isn't the socket's");
        }
        return ip;
    }

    static void Check(byte[] buffer, int offset, int size)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || (uint)size > (uint)(buffer.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(size));
    }
}

/// <summary>
/// A connected socket as a Stream: Read receives, Write sends. A broken
/// connection doesn't end the program: Read returns 0, as at the end of the
/// stream, Write drops what it's given, and Error says what went wrong.
/// </summary>
// .NET throws IOException there, for each connection's code to catch. Here a
// throw ends the program unless the connection's fiber runs under a
// Catch.Run, so one client resetting its connection could end a server: a
// broken connection ends its own stream instead.
public partial class NetworkStream : Stream
{
    readonly bool _ownsSocket;
    bool _open = true;

    /// <summary>A stream over a connected socket, which it doesn't close.</summary>
    public NetworkStream(Socket socket) : this(socket, false) { }

    /// <summary>A stream over a connected socket; with <paramref name="ownsSocket"/>, disposing the stream closes it.</summary>
    public NetworkStream(Socket socket, bool ownsSocket)
    {
        Socket = socket ?? throw new ArgumentNullException(nameof(socket));
        if (socket.SocketType != SocketType.Stream)
            throw new IOException("NetworkStream: needs a stream (TCP) socket");
        _ownsSocket = ownsSocket;
    }

    /// <summary>The socket underneath.</summary>
    public Socket Socket { get; }

    /// <summary>
    /// Why the connection broke (ConnectionReset, ConnectionAborted,
    /// OperationAborted when the socket was closed under it...), or Success.
    /// The first such failure stays. TimedOut (a ReadTimeout or WriteTimeout
    /// ran out), or OperationAborted from a canceled ReadAsync or WriteAsync,
    /// says why the last call returned early; the next call tries again.
    /// </summary>
    public SocketError Error { get; internal set; }

    // Whether the next call may go ahead: not on a broken connection. A
    // timeout or a canceled async call leaves the connection as it was.
    internal bool Usable()
    {
        if (Error == SocketError.Success)
            return true;
        if (Error == SocketError.TimedOut || (Error == SocketError.OperationAborted && Socket.Handle >= 0))
        {
            Error = SocketError.Success;
            return true;
        }
        return false;
    }

    /// <summary>True: a network stream's Read and Write can time out.</summary>
    public override bool CanTimeout => true;

    /// <summary>
    /// How long, in milliseconds, Read and ReadAsync wait for data before
    /// returning 0 with Error TimedOut: Timeout.Infinite (-1, the default)
    /// waits for ever. The socket's ReceiveTimeout. Unlike .NET, ReadAsync
    /// uses it too, so an async server can drop a silent client without a
    /// canceled task (whose await would end the program).
    /// </summary>
    public override int ReadTimeout
    {
        get => Socket.ReceiveTimeout == 0 ? System.Threading.Timeout.Infinite : Socket.ReceiveTimeout;
        set => Socket.ReceiveTimeout = StreamTimeout(value);
    }

    /// <summary>
    /// How long, in milliseconds, Write and WriteAsync wait for room before
    /// giving up with Error TimedOut: Timeout.Infinite (-1, the default)
    /// waits for ever. The socket's SendTimeout.
    /// </summary>
    public override int WriteTimeout
    {
        get => Socket.SendTimeout == 0 ? System.Threading.Timeout.Infinite : Socket.SendTimeout;
        set => Socket.SendTimeout = StreamTimeout(value);
    }

    static int StreamTimeout(int milliseconds)
    {
        if (milliseconds <= 0 && milliseconds != System.Threading.Timeout.Infinite)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return milliseconds;
    }

    /// <inheritdoc/>
    public override bool CanRead => _open;
    /// <inheritdoc/>
    public override bool CanWrite => _open;
    /// <inheritdoc/>
    public override bool CanSeek => false;
    /// <summary>Not for a network stream: ends the program.</summary>
    public override long Length => throw new NotSupportedException("NetworkStream: no length");
    /// <summary>Not for a network stream: ends the program.</summary>
    public override long Position
    {
        get => throw new NotSupportedException("NetworkStream: no position");
        set => throw new NotSupportedException("NetworkStream: no position");
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(new Span<byte>(buffer, offset, count));
    }

    /// <summary>
    /// Receives up to buffer.Length bytes, waiting for some. 0 when the other
    /// end is done sending, or the connection broke (Error says how).
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        EnsureOpen();
        if (!Usable())
            return 0;
        int n = Socket.Receive(buffer, SocketFlags.None, out SocketError error);
        if (error != SocketError.Success)
        {
            Error = error;
            return 0;
        }
        return n;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    /// <summary>Sends all of the bytes; on a broken connection (Error), drops them.</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureOpen();
        if (!Usable())
            return;
        Socket.Send(buffer, SocketFlags.None, out SocketError error);
        if (error != SocketError.Success)
            Error = error;
    }

    /// <summary>Nothing to do: sends aren't buffered here.</summary>
    public override void Flush() { }
    /// <summary>Not for a network stream: ends the program.</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("NetworkStream: can't seek");
    /// <summary>Not for a network stream: ends the program.</summary>
    public override void SetLength(long value) => throw new NotSupportedException("NetworkStream: no length");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_open)
            return;
        _open = false;
        if (_ownsSocket)
            Socket.Close();
    }

    internal void EnsureOpen()
    {
        if (!_open)
            throw new ObjectDisposedException("NetworkStream");
    }
}

/// <summary>Listens for TCP connections on an address and port.</summary>
public partial class TcpListener
{
    readonly IPEndPoint _endpoint;
    Socket _server;

    /// <summary>A listener for the address and port (0 lets the system choose; see LocalEndpoint after Start).</summary>
    public TcpListener(IPAddress localaddr, int port) : this(new IPEndPoint(localaddr, port)) { }

    /// <summary>A listener for the endpoint.</summary>
    public TcpListener(IPEndPoint localEP) => _endpoint = localEP ?? throw new ArgumentNullException(nameof(localEP));

    /// <summary>The socket that listens, once started.</summary>
    public Socket Server => _server;

    /// <summary>The address and port listened on: the system's choice if the port was 0.</summary>
    public EndPoint LocalEndpoint => _server?.LocalEndPoint ?? _endpoint;

    /// <summary>Starts listening. Failing (the port is taken, say) throws a SocketException.</summary>
    public void Start() => Start(512);

    /// <summary>Starts listening, with room for <paramref name="backlog"/> connections waiting to be accepted.</summary>
    public void Start(int backlog)
    {
        if (_server != null)
            return;
        var server = new Socket(_endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        SocketNative.ReuseAddress(server);
        server.Bind(_endpoint);
        server.Listen(backlog);
        _server = server;
    }

    /// <summary>Stops listening and closes the socket.</summary>
    public void Stop()
    {
        _server?.Close();
        _server = null;
    }

    /// <summary>The next connection as a socket, waiting for one (other fibers run meanwhile).</summary>
    public Socket AcceptSocket() => Started.Accept();

    /// <summary>The next connection as a TcpClient, waiting for one (other fibers run meanwhile). Failing throws a SocketException.</summary>
    public TcpClient AcceptTcpClient() => new TcpClient(Started.Accept());

    /// <summary>
    /// The next connection as a TcpClient, or why there isn't one: the
    /// program has too many sockets open (TooManyOpenSockets), say, or the
    /// listener was stopped (OperationAborted). A server loop retries, or
    /// stops, rather than ending the program.
    /// </summary>
    public bool TryAcceptTcpClient(out TcpClient client, out SocketError error)
    {
        client = Started.TryAccept(out Socket socket, out error) ? new TcpClient(socket) : null;
        return client != null;
    }

    internal Socket Started => _server ?? throw new InvalidOperationException("TcpListener: call Start first");
}

/// <summary>A TCP connection, and a stream over it.</summary>
public partial class TcpClient : IDisposable
{
    Socket _socket;
    NetworkStream _stream;

    /// <summary>An unconnected IPv4 client.</summary>
    public TcpClient() : this(AddressFamily.InterNetwork) { }

    /// <summary>An unconnected client of the family.</summary>
    public TcpClient(AddressFamily family) => _socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);

    /// <summary>A client connected to a host name's port. Failing throws a SocketException.</summary>
    public TcpClient(string hostname, int port)
    {
        if (!Dns.TryGetHostAddresses(hostname, out IPAddress[] addresses, out SocketError error))
            throw new SocketException((int)error);
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (socket.TryConnect(new IPEndPoint(address, port), out error))
            {
                _socket = socket;
                return;
            }
            socket.Close();
        }
        throw new SocketException((int)error);
    }

    internal TcpClient(Socket connected) => _socket = connected;

    /// <summary>The socket underneath.</summary>
    public Socket Client => _socket;

    /// <summary>Whether it's connected.</summary>
    public bool Connected => _socket != null && _socket.Connected;

    /// <summary>Whether small writes go out at once (TCP_NODELAY).</summary>
    public bool NoDelay
    {
        get => _socket.NoDelay;
        set => _socket.NoDelay = value;
    }

    /// <summary>The socket's ReceiveTimeout, in milliseconds: 0 waits for ever.</summary>
    public int ReceiveTimeout
    {
        get => _socket.ReceiveTimeout;
        set => _socket.ReceiveTimeout = value;
    }

    /// <summary>The socket's SendTimeout, in milliseconds: 0 waits for ever.</summary>
    public int SendTimeout
    {
        get => _socket.SendTimeout;
        set => _socket.SendTimeout = value;
    }

    /// <summary>Connects to a host name's port. Failing throws a SocketException.</summary>
    public void Connect(string hostname, int port) => _socket.Connect(hostname, port);

    /// <summary>Connects to the address and port. Failing throws a SocketException.</summary>
    public void Connect(IPAddress address, int port) => _socket.Connect(address, port);

    /// <summary>Connects to the endpoint. Failing throws a SocketException.</summary>
    public void Connect(IPEndPoint remoteEP) => _socket.Connect(remoteEP);

    /// <summary>The connection as a stream; disposing it, or this client, closes the connection.</summary>
    public NetworkStream GetStream()
    {
        if (_socket == null)
            throw new ObjectDisposedException("TcpClient");
        if (!_socket.Connected)
            throw new InvalidOperationException("TcpClient: not connected");
        return _stream ??= new NetworkStream(_socket, true);
    }

    /// <summary>Closes the connection.</summary>
    public void Close() => Dispose();

    /// <summary>Closes the connection.</summary>
    public void Dispose()
    {
        _stream?.Dispose();
        _socket?.Close();
        _socket = null;
    }
}

/// <summary>Sends and receives UDP datagrams.</summary>
public partial class UdpClient : IDisposable
{
    Socket _socket;

    /// <summary>An IPv4 client on a port the system chooses.</summary>
    public UdpClient() : this(AddressFamily.InterNetwork) { }

    /// <summary>A client of the family, not bound until it first sends.</summary>
    public UdpClient(AddressFamily family) => _socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);

    /// <summary>An IPv4 client bound to the port on every address.</summary>
    public UdpClient(int port) : this(port, AddressFamily.InterNetwork) { }

    /// <summary>A client bound to the port on every address of the family.</summary>
    public UdpClient(int port, AddressFamily family) : this(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, port)) { }

    /// <summary>A client bound to the endpoint.</summary>
    public UdpClient(IPEndPoint localEP)
    {
        if (localEP == null)
            throw new ArgumentNullException(nameof(localEP));
        _socket = new Socket(localEP.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(localEP);
    }

    /// <summary>The socket underneath.</summary>
    public Socket Client => _socket;

    /// <summary>Whether it may send to a broadcast address.</summary>
    public bool EnableBroadcast
    {
        get => _socket.EnableBroadcast;
        set => _socket.EnableBroadcast = value;
    }

    /// <summary>Sets the endpoint Send without one goes to, and the only one Receive takes datagrams from.</summary>
    public void Connect(IPEndPoint endPoint) => _socket.Connect(endPoint);

    /// <summary>Connect to a host name's port.</summary>
    public void Connect(string hostname, int port) => _socket.Connect(hostname, port);

    /// <summary>Sends a datagram to the endpoint.</summary>
    public int Send(ReadOnlySpan<byte> datagram, IPEndPoint endPoint) => _socket.SendTo(datagram, endPoint);

    /// <summary>Sends <paramref name="bytes"/> bytes of the array as a datagram to the endpoint.</summary>
    public int Send(byte[] dgram, int bytes, IPEndPoint endPoint) => _socket.SendTo(new ReadOnlySpan<byte>(dgram, 0, bytes), endPoint);

    /// <summary>Sends a datagram to the connected endpoint.</summary>
    public int Send(ReadOnlySpan<byte> datagram) => _socket.Send(datagram);

    /// <summary>
    /// The next datagram, waiting for one (other fibers run meanwhile), in a
    /// new array, and where it came from. Datagrams up to 64 KB.
    /// </summary>
    public byte[] Receive(ref IPEndPoint remoteEP)
    {
        byte[] buffer = new byte[65536];
        EndPoint from = remoteEP ?? new IPEndPoint(IPAddress.Any, 0);
        int n = _socket.ReceiveFrom(buffer, ref from);
        remoteEP = (IPEndPoint)from;
        byte[] datagram = new byte[n];
        new ReadOnlySpan<byte>(buffer, 0, n).CopyTo(datagram);
        return datagram;
    }

    /// <summary>
    /// The next datagram into <paramref name="buffer"/>, without allocating,
    /// and where it came from. Bytes past the buffer's end are lost.
    /// </summary>
    public int Receive(Span<byte> buffer, out IPEndPoint remoteEP)
    {
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int n = _socket.ReceiveFrom(buffer, ref from);
        remoteEP = (IPEndPoint)from;
        return n;
    }

    /// <summary>Closes the socket.</summary>
    public void Close() => Dispose();

    /// <summary>Closes the socket.</summary>
    public void Dispose()
    {
        _socket?.Close();
        _socket = null;
    }
}

// ---- the C side --------------------------------------------------------------

// The socket functions, with Linux's numbers and layouts, or macOS's where
// they differ. Every socket is non-blocking: a call that would wait waits in
// Wait, which parks a fiber (Fiber.WaitForIO) or polls outside one.
static class SocketNative
{
    [DllImport("libc")] static unsafe extern int socket(int domain, int type, int protocol);
    [DllImport("libc")] static unsafe extern int bind(int fd, byte* address, uint length);
    [DllImport("libc")] static unsafe extern int listen(int fd, int backlog);
    [DllImport("libc")] static unsafe extern int accept(int fd, byte* address, uint* length);
    [DllImport("libc")] static unsafe extern int connect(int fd, byte* address, uint length);
    [DllImport("libc")] static unsafe extern nint sendto(int fd, byte* buffer, nuint length, int flags, byte* address, uint addressLength);
    [DllImport("libc")] static unsafe extern nint recvfrom(int fd, byte* buffer, nuint length, int flags, byte* address, uint* addressLength);
    [DllImport("libc")] static unsafe extern int shutdown(int fd, int how);
    [DllImport("libc")] static unsafe extern int close(int fd);
    [DllImport("libc")] static unsafe extern int setsockopt(int fd, int level, int name, void* value, uint length);
    [DllImport("libc")] static unsafe extern int getsockopt(int fd, int level, int name, void* value, uint* length);
    [DllImport("libc")] static unsafe extern int getsockname(int fd, byte* address, uint* length);
    [DllImport("libc")] static unsafe extern int getpeername(int fd, byte* address, uint* length);
    [DllImport("libc")] static unsafe extern int poll(void* fds, nuint count, int timeout);
    [DllImport("libc")] static unsafe extern int getaddrinfo(byte* node, byte* service, byte* hints, byte** result);
    [DllImport("libc")] static unsafe extern void freeaddrinfo(byte* result);
    [DllImport("libc")] static unsafe extern int gethostname(byte* name, nuint length);
    [DllImport("libc")] static unsafe extern int* __errno_location();
    [DllImport("libc")] static unsafe extern int pthread_create(nint* thread, void* attributes, delegate* unmanaged<void*, void*> start, void* argument);
    [DllImport("libc")] static unsafe extern int pthread_join(nint thread, void** result);
    [DllImport("libc")] static unsafe extern void* calloc(nuint count, nuint size);
    [DllImport("libc")] static unsafe extern void free(void* memory);
#if ZERO_DOTNET
    // Real .NET on Linux: fcntl itself, whose third argument travels as any
    // other there. macOS's goes on the stack, which only C can do
    // (dotnet/native's ZeroFcntl).
    [DllImport("libc", EntryPoint = "fcntl")] static unsafe extern int LinuxFcntl(int fd, int command, int argument);
    [DllImport("*", EntryPoint = "ZeroFcntl")] static unsafe extern int NativeFcntl(int fd, int command, int argument);

    static unsafe int ZeroFcntl(int fd, int command, int argument)
    {
        unsafe { return OperatingSystem.IsLinux() ? LinuxFcntl(fd, command, argument) : NativeFcntl(fd, command, argument); } // SAFETY: plain numbers
    }
#else
    [DllImport("*")] static unsafe extern int ZeroFcntl(int fd, int command, int argument);
#endif

    static bool Mac => FileSystem.System == SystemKind.MacOS;
    // Android's C library takes the BSD side in getaddrinfo (struct addrinfo
    // and the EAI_* numbers), and Linux's everywhere else.
    static bool BsdLookup => Mac || FileSystem.System == SystemKind.Android;

    // Numbers that differ: Linux's (and win/runtime.c's), then macOS's.
    static int AF_INET6 => Mac ? 30 : 10;
    static int SOL_SOCKET => Mac ? 0xFFFF : 1;
    static int SO_REUSEADDR => Mac ? 4 : 2;
    static int SO_ERROR => Mac ? 0x1007 : 4;
    static int SO_BROADCAST => Mac ? 0x20 : 6;
    static int IPV6_V6ONLY => Mac ? 27 : 26;
    static int O_NONBLOCK => Mac ? 4 : 0x800;
    static int MSG_NOSIGNAL => Mac ? 0 : 0x4000; // macOS: SO_NOSIGPIPE on the socket instead
    const int AF_INET = 2, SOCK_STREAM = 1, SOCK_DGRAM = 2, IPPROTO_TCP = 6, IPPROTO_IPV6 = 41, TCP_NODELAY = 1;
    const int F_GETFL = 3, F_SETFL = 4, SO_NOSIGPIPE = 0x1022;
    internal const short POLLIN = 1, POLLOUT = 4;
    const int SockaddrSize = 28; // sockaddr_in6, the bigger

    internal enum Option { NoDelay, V6Only, Broadcast }

    // ---- errors ---------------------------------------------------------------

    static int Errno
    {
        get { unsafe { return *__errno_location(); } } // SAFETY: the thread's errno
    }

    static bool WouldBlock(int e) => e == (Mac ? 35 : 11);       // EAGAIN
    static bool InProgress(int e) => e == (Mac ? 36 : 115);      // EINPROGRESS
    static bool Interrupted(int e) => e == 4;                    // EINTR

    static SocketError LastError()
    {
        int e = Errno;
        if (Mac)
        {
            return e switch
            {
                1 or 13 => SocketError.AccessDenied,
                4 => SocketError.Interrupted,
                9 => SocketError.OperationAborted,
                22 => SocketError.InvalidArgument,
                24 => SocketError.TooManyOpenSockets,
                32 or 58 => SocketError.Shutdown,
                35 => SocketError.WouldBlock,
                36 => SocketError.InProgress,
                37 => SocketError.AlreadyInProgress,
                38 => SocketError.NotSocket,
                39 => SocketError.DestinationAddressRequired,
                40 => SocketError.MessageSize,
                43 => SocketError.ProtocolNotSupported,
                47 => SocketError.AddressFamilyNotSupported,
                48 => SocketError.AddressAlreadyInUse,
                49 => SocketError.AddressNotAvailable,
                50 => SocketError.NetworkDown,
                51 => SocketError.NetworkUnreachable,
                52 => SocketError.NetworkReset,
                53 => SocketError.ConnectionAborted,
                54 => SocketError.ConnectionReset,
                55 => SocketError.NoBufferSpaceAvailable,
                56 => SocketError.IsConnected,
                57 => SocketError.NotConnected,
                60 => SocketError.TimedOut,
                61 => SocketError.ConnectionRefused,
                64 => SocketError.HostDown,
                65 => SocketError.HostUnreachable,
                102 => SocketError.OperationNotSupported,
                _ => SocketError.SocketError,
            };
        }
        return e switch
        {
            1 or 13 => SocketError.AccessDenied,
            4 => SocketError.Interrupted,
            9 => SocketError.OperationAborted,
            11 => SocketError.WouldBlock,
            22 => SocketError.InvalidArgument,
            24 => SocketError.TooManyOpenSockets,
            32 or 108 => SocketError.Shutdown,
            88 => SocketError.NotSocket,
            89 => SocketError.DestinationAddressRequired,
            90 => SocketError.MessageSize,
            93 => SocketError.ProtocolNotSupported,
            95 => SocketError.OperationNotSupported,
            97 => SocketError.AddressFamilyNotSupported,
            98 => SocketError.AddressAlreadyInUse,
            99 => SocketError.AddressNotAvailable,
            100 => SocketError.NetworkDown,
            101 => SocketError.NetworkUnreachable,
            102 => SocketError.NetworkReset,
            103 => SocketError.ConnectionAborted,
            104 => SocketError.ConnectionReset,
            105 => SocketError.NoBufferSpaceAvailable,
            106 => SocketError.IsConnected,
            107 => SocketError.NotConnected,
            110 => SocketError.TimedOut,
            111 => SocketError.ConnectionRefused,
            112 => SocketError.HostDown,
            113 => SocketError.HostUnreachable,
            114 => SocketError.AlreadyInProgress,
            115 => SocketError.InProgress,
            _ => SocketError.SocketError,
        };
    }

    // getaddrinfo's own errors (EAI_*): Linux's (and win/runtime.c's), macOS's
    // and Android's.
    static SocketError LookupError(int e)
    {
        if (BsdLookup)
            return e switch { 2 => SocketError.TryAgain, 4 => SocketError.NoRecovery, 5 or 7 => SocketError.NoData, 8 => SocketError.HostNotFound, _ => SocketError.SocketError };
        return e switch { -3 => SocketError.TryAgain, -4 => SocketError.NoRecovery, -5 or -6 => SocketError.NoData, -2 => SocketError.HostNotFound, _ => SocketError.SocketError };
    }

    // ---- waiting --------------------------------------------------------------

    static int PollAll(void* fds, nuint count, int timeout)
    {
        unsafe { return poll(fds, count, timeout); } // SAFETY: Fiber.Run's array of count entries
    }

    // Waits until fd is ready for events: in a fiber, the others run
    // meanwhile; outside one, poll waits here. Success, TimedOut once the
    // deadline (Clock.Monotonic; 0: none) has passed, or OperationAborted if
    // the socket was closed while a fiber waited (Close cancels its waiters).
    static SocketError Wait(int fd, short events, long deadline)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            UsePoll();
            short revents = Fiber.WaitForIO(fd, events, deadline);
            if (revents == Fiber.Cancelled)
                return SocketError.OperationAborted;
            if (revents == Fiber.TimedOut)
                return SocketError.TimedOut;
            if (revents != 0)
                return SocketError.Success;
            long one = 0;
            *(int*)&one = fd;
            *((short*)&one + 2) = events;
            int timeout = -1;
            if (deadline != 0)
            {
                timeout = Fiber.MillisecondsUntil(deadline);
                if (timeout == 0)
                    return SocketError.TimedOut;
            }
            return poll(&one, 1, timeout) == 0 ? SocketError.TimedOut : SocketError.Success;
        }
    }

    // Fiber.Run polls through Fiber.Poll: set it before anything waits.
    internal static void UsePoll()
    {
        unsafe
        {
            // SAFETY: a static method.
            if (Fiber.Poll == null)
                Fiber.Poll = &PollAll;
        }
    }

    // When a timeout of `milliseconds` (0 or less: none) runs out, for Wait.
    internal static long Deadline(int milliseconds)
        => milliseconds <= 0 ? 0 : Clock.Monotonic + milliseconds * 1_000_000L;


    // ---- operations -----------------------------------------------------------

    internal static SocketError Create(AddressFamily family, SocketType type, ProtocolType protocol, out int fd)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            int domain = family == AddressFamily.InterNetwork ? AF_INET : family == AddressFamily.InterNetworkV6 ? AF_INET6 : -1;
            int kind = type == SocketType.Stream ? SOCK_STREAM : type == SocketType.Dgram ? SOCK_DGRAM : -1;
            fd = -1;
            if (domain < 0)
                return SocketError.AddressFamilyNotSupported;
            if (kind < 0)
                return SocketError.ProtocolNotSupported;
            fd = socket(domain, kind, (int)protocol);
            if (fd < 0)
                return LastError();
            Prepare(fd);
            return SocketError.Success;
        }
    }

    // Non-blocking, and on macOS no SIGPIPE when the other end has gone.
    static void Prepare(int fd)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            int flags = ZeroFcntl(fd, F_GETFL, 0);
            ZeroFcntl(fd, F_SETFL, flags | O_NONBLOCK);
            if (Mac)
            {
                int one = 1;
                setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, 4);
            }
        }
    }

    internal static SocketError Bind(int fd, IPEndPoint endpoint)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            byte* address = stackalloc byte[SockaddrSize];
            uint length = Write(endpoint, address);
            return bind(fd, address, length) == 0 ? SocketError.Success : LastError();
        }
    }

    internal static SocketError Listen(int fd, int backlog)
    {
        unsafe { return listen(fd, backlog) == 0 ? SocketError.Success : LastError(); } // SAFETY: no pointers
    }

    internal static SocketError Accept(int fd, out int accepted)
    {
        SocketError error;
        while ((error = AcceptOnce(fd, out accepted)) == SocketError.WouldBlock)
            if ((error = Wait(fd, POLLIN, 0)) != SocketError.Success)
                return error;
        return error;
    }

    // One try, without waiting: WouldBlock if no connection is waiting.
    internal static SocketError AcceptOnce(int fd, out int accepted)
    {
        unsafe
        {
            // SAFETY: no pointers but nulls.
            accepted = accept(fd, null, null);
            if (accepted >= 0)
            {
                Prepare(accepted);
                return SocketError.Success;
            }
            return Failed();
        }
    }

    // A call failed: WouldBlock if it only would have waited (EAGAIN, EINTR).
    static SocketError Failed()
    {
        int e = Errno;
        return WouldBlock(e) || Interrupted(e) ? SocketError.WouldBlock : LastError();
    }

    internal static SocketError Connect(int fd, IPEndPoint endpoint)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            SocketError started = ConnectStart(fd, endpoint);
            if (started != SocketError.InProgress)
                return started;
            // Under way: done when the socket can be written.
            SocketError waited = Wait(fd, POLLOUT, 0);
            return waited != SocketError.Success ? waited : ConnectResult(fd);
        }
    }

    // Starts connecting: Success, InProgress (wait until the socket can be
    // written, then ask ConnectResult), or why it failed.
    internal static SocketError ConnectStart(int fd, IPEndPoint endpoint)
    {
        unsafe
        {
            // SAFETY: the address is on this stack, of the size connect is told.
            byte* address = stackalloc byte[SockaddrSize];
            uint length = Write(endpoint, address);
            if (connect(fd, address, length) == 0)
                return SocketError.Success;
            int e = Errno;
            if (!InProgress(e) && !WouldBlock(e) && !Interrupted(e))
                return LastError();
            return SocketError.InProgress;
        }
    }

    // How a connection under way turned out: SO_ERROR.
    internal static SocketError ConnectResult(int fd)
    {
        unsafe
        {
            // SAFETY: getsockopt writes 4 bytes into error.
            int error = 0;
            uint size = 4;
            if (getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &size) != 0)
                return LastError();
            if (error == 0)
                return SocketError.Success;
            *__errno_location() = error;
            return LastError();
        }
    }

    // Sends all of it, to `to` if given (UDP), waiting while the buffer is
    // full, until the deadline (0: none).
    internal static SocketError Send(int fd, ReadOnlySpan<byte> bytes, SocketFlags flags, IPEndPoint to, out int sent, long deadline = 0)
    {
        sent = 0;
        while (true)
        {
            SocketError error = SendSome(fd, bytes, flags, to, ref sent);
            if (error != SocketError.WouldBlock)
                return error;
            if ((error = Wait(fd, POLLOUT, deadline)) != SocketError.Success)
                return error;
        }
    }

    // Sends from bytes[sent..] until all of it has gone (Success), the
    // buffer is full (WouldBlock), or it fails; `sent` counts what went.
    // A datagram (to given) goes in one sendto, even an empty one.
    internal static SocketError SendSome(int fd, ReadOnlySpan<byte> bytes, SocketFlags flags, IPEndPoint to, ref int sent)
    {
        unsafe
        {
            // SAFETY: the address is on this stack and the bytes are fixed, of
            // the sizes sendto is told.
            byte* address = stackalloc byte[SockaddrSize];
            uint length = to == null ? 0 : Write(to, address);
            fixed (byte* p = bytes)
            {
                while (sent < bytes.Length || (bytes.IsEmpty && to != null && sent == 0))
                {
                    nint n = sendto(fd, p + sent, (nuint)(bytes.Length - sent), (int)flags | MSG_NOSIGNAL, to == null ? null : address, length);
                    if (n < 0)
                        return Failed();
                    sent += (int)n;
                    if (bytes.IsEmpty)
                        break;
                }
            }
            return SocketError.Success;
        }
    }

    // Receives what has arrived, waiting for something until the deadline
    // (0: none); fills `from` (UDP).
    internal static SocketError Receive(int fd, Span<byte> buffer, SocketFlags flags, out int received, IPEndPoint from, long deadline = 0)
    {
        SocketError error;
        while ((error = ReceiveOnce(fd, buffer, flags, out received, from)) == SocketError.WouldBlock)
            if ((error = Wait(fd, POLLIN, deadline)) != SocketError.Success)
                return error;
        return error;
    }

    // One recvfrom, without waiting: WouldBlock if nothing has arrived.
    internal static SocketError ReceiveOnce(int fd, Span<byte> buffer, SocketFlags flags, out int received, IPEndPoint from)
    {
        unsafe
        {
            // SAFETY: the address is on this stack and the buffer is fixed, of
            // the sizes recvfrom is told.
            byte* address = stackalloc byte[SockaddrSize];
            received = 0;
            fixed (byte* p = buffer)
            {
                uint length = SockaddrSize;
                nint n = recvfrom(fd, p, (nuint)buffer.Length, (int)flags, from == null ? null : address, from == null ? null : &length);
                if (n < 0)
                    return Failed();
                received = (int)n;
                if (from != null)
                {
                    IPEndPoint source = Read(address);
                    if (source != null)
                    {
                        from.Address = source.Address;
                        from.Port = source.Port;
                    }
                }
                return SocketError.Success;
            }
        }
    }

    internal static SocketError Shutdown(int fd, SocketShutdown how)
    {
        unsafe { return shutdown(fd, (int)how) == 0 ? SocketError.Success : LastError(); } // SAFETY: no pointers
    }

    internal static void Close(int fd)
    {
        Fiber.CancelIO(fd); // before the number can go to another socket
        unsafe { close(fd); } // SAFETY: no pointers
    }

    // Close, as Catch.Run's records call it (Catch.cs).
    internal static void CloseHandle(nint fd) => Close((int)fd);

    internal static IPEndPoint Name(int fd, bool peer)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            byte* address = stackalloc byte[SockaddrSize];
            uint length = SockaddrSize;
            int result = peer ? getpeername(fd, address, &length) : getsockname(fd, address, &length);
            return result == 0 ? Read(address) : null;
        }
    }

    internal static void ReuseAddress(Socket socket)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            // Lets a server start again at once on a port whose old connections
            // linger (TIME_WAIT). Windows' SO_REUSEADDR means something else (two
            // listeners on one port), so win/runtime.c ignores it.
            int one = 1;
            setsockopt((int)socket.Handle, SOL_SOCKET, SO_REUSEADDR, &one, 4);
        }
    }

    static void OptionOf(Option option, out int level, out int name)
    {
        switch (option)
        {
            case Option.NoDelay: level = IPPROTO_TCP; name = TCP_NODELAY; break;
            case Option.V6Only: level = IPPROTO_IPV6; name = IPV6_V6ONLY; break;
            default: level = SOL_SOCKET; name = SO_BROADCAST; break;
        }
    }

    // SO_LINGER: Linux's 13; macOS's SO_LINGER_SEC (its SO_LINGER counts
    // clock ticks). struct linger is two ints: on, and seconds.
    static int SO_LINGER => Mac ? 0x1080 : 13;

    internal static void GetLinger(int fd, out bool on, out int seconds)
    {
        long linger = 0;
        uint size = 8;
        unsafe
        {
            // SAFETY: linger has room for struct linger's 8 bytes.
            getsockopt(fd, SOL_SOCKET, SO_LINGER, &linger, &size);
            on = ((int*)&linger)[0] != 0;
            seconds = ((int*)&linger)[1];
        }
    }

    internal static void SetLinger(int fd, bool on, int seconds)
    {
        long linger = 0;
        unsafe
        {
            // SAFETY: struct linger's 8 bytes, on the stack.
            ((int*)&linger)[0] = on ? 1 : 0;
            ((int*)&linger)[1] = seconds;
            if (setsockopt(fd, SOL_SOCKET, SO_LINGER, &linger, 8) != 0)
                throw new SocketException((int)LastError());
        }
    }

    internal static int GetOption(int fd, Option option)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            OptionOf(option, out int level, out int name);
            int value = 0;
            uint size = 4;
            return getsockopt(fd, level, name, &value, &size) == 0 ? value : 0;
        }
    }

    internal static void SetOption(int fd, Option option, int value)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            OptionOf(option, out int level, out int name);
            if (setsockopt(fd, level, name, &value, 4) != 0)
                throw new SocketException((int)LastError());
        }
    }

    // ---- addresses ------------------------------------------------------------

    // sockaddr_in (16 bytes) or sockaddr_in6 (28): the family, the port in
    // network order, then the address (and for IPv6 flow information before
    // it, the scope after). Linux's family is 16 bits; macOS's is a byte after
    // a byte of length.
    static uint Write(IPEndPoint endpoint, byte* address)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            new Span<byte>(address, SockaddrSize).Clear();
            bool v6 = endpoint.AddressFamily == AddressFamily.InterNetworkV6;
            uint length = v6 ? 28u : 16u;
            int family = v6 ? AF_INET6 : AF_INET;
            if (Mac)
            {
                address[0] = (byte)length;
                address[1] = (byte)family;
            }
            else
            {
                *(ushort*)address = (ushort)family;
            }
            address[2] = (byte)(endpoint.Port >> 8);
            address[3] = (byte)endpoint.Port;
            if (v6)
            {
                endpoint.Address.TryWriteBytes(new Span<byte>(address + 8, 16), out _);
                *(uint*)(address + 24) = (uint)endpoint.Address.ScopeId;
            }
            else
            {
                endpoint.Address.TryWriteBytes(new Span<byte>(address + 4, 4), out _);
            }
            return length;
        }
    }

    static IPEndPoint Read(byte* address)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            int family = Mac ? address[1] : *(ushort*)address;
            int port = address[2] << 8 | address[3];
            if (family == AF_INET)
                return new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(address + 4, 4)), port);
            if (family == AF_INET6)
            {
                var ip = new IPAddress(new ReadOnlySpan<byte>(address + 8, 16));
                ip.ScopeId = *(uint*)(address + 24);
                return new IPEndPoint(ip, port);
            }
            return null;
        }
    }

    // ---- names ----------------------------------------------------------------

    internal static string HostName()
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            byte* name = stackalloc byte[256];
            if (gethostname(name, 256) != 0)
                return "";
            name[255] = 0;
            return FileSystem.ToString(new ReadOnlySpan<byte>(name, FileSystem.Length(name)));
        }
    }

    // What getaddrinfo's thread shares with the fiber that waits for it.
    struct Lookup
    {
        public int Done;        // set last, by the thread
        public int Result;
        public unsafe byte* List;
        public byte Hints0;     // the hints (48 bytes) start here
    }

    const int HintsSize = 48, NameRoom = 1024;

    // Looks the name up with getaddrinfo. In a fiber it runs on a thread of
    // its own, while the fiber sleeps a millisecond at a time and the others
    // run: getaddrinfo can take seconds, and can't let them run itself.
    internal static bool Resolve(string host, out IPAddress[] addresses, out SocketError error)
    {
        addresses = null;
        nint lookup = BeginLookup(host, Fiber.Id != 0, out nint thread, out error);
        if (lookup == 0)
            return false;
        while (!LookupDone(lookup))
            Fiber.Sleep(1);
        return EndLookup(lookup, thread, out addresses, out error);
    }

    // Resolve for Dns.GetHostAddressesAsync: the lookup's thread runs while
    // this waits on Task.Delay, which takes no fiber. Failing ends the program.
    internal static async System.Threading.Tasks.Task<IPAddress[]> ResolveAsync(string host)
    {
        nint lookup = BeginLookup(host, true, out nint thread, out SocketError error);
        if (lookup == 0)
            throw new SocketException((int)error);
        while (!LookupDone(lookup))
            await System.Threading.Tasks.Task.Delay(1);
        if (!EndLookup(lookup, thread, out IPAddress[] addresses, out error))
            throw new SocketException((int)error);
        return addresses;
    }

    // Starts looking the name up: on a thread of its own if `threaded` (and
    // one can be started), else here and now. The lookup (a Lookup*), or 0
    // with the error.
    static nint BeginLookup(string host, bool threaded, out nint thread, out SocketError error)
    {
        unsafe
        {
            // SAFETY: the lookup has room for the hints and the name after
            // it; the thread gets it and returns before EndLookup frees it.
            thread = 0;
            error = SocketError.Success;
            Lookup* lookup = (Lookup*)calloc(1, (nuint)(sizeof(Lookup) + HintsSize + NameRoom));
            if (lookup == null)
            {
                error = SocketError.NoBufferSpaceAvailable;
                return 0;
            }
            byte* hints = &lookup->Hints0;
            byte* name = hints + HintsSize;
            if (FileSystem.ToC(host, new Span<byte>(name, NameRoom)) < 0)
            {
                free(lookup);
                error = SocketError.HostNotFound;
                return 0;
            }
            // ai_socktype (offset 8): stream, so each address comes once.
            *(int*)(hints + 8) = SOCK_STREAM;
            nint started = 0;
            if (!threaded || pthread_create(&started, null, &LookUp, lookup) != 0)
                RunLookup(lookup);
            thread = started;
            return (nint)lookup;
        }
    }

    static bool LookupDone(nint lookup)
    {
        unsafe { return Volatile.Read(ref ((Lookup*)lookup)->Done) != 0; } // SAFETY: BeginLookup's, not yet freed
    }

    // The answer, once LookupDone; frees the lookup and joins its thread.
    static bool EndLookup(nint handle, nint thread, out IPAddress[] addresses, out SocketError error)
    {
        unsafe
        {
            // SAFETY: BeginLookup's lookup, done; its thread has finished with it.
            Lookup* lookup = (Lookup*)handle;
            if (thread != 0)
                pthread_join(thread, null);
            addresses = null;
            int result = lookup->Result;
            byte* list = lookup->List;
            free(lookup);
            if (result != 0)
            {
                error = LookupError(result);
                return false;
            }
            addresses = Addresses(list);
            freeaddrinfo(list);
            error = addresses.Length > 0 ? SocketError.Success : SocketError.NoData;
            return addresses.Length > 0;
        }
    }

    [UnmanagedCallersOnly]
    static void* LookUp(void* argument)
    {
        unsafe { RunLookup((Lookup*)argument); } // SAFETY: Resolve's lookup, freed after this thread is joined
        return null;
    }

    static void RunLookup(Lookup* lookup)
    {
        unsafe
        {
            // SAFETY: Resolve's lookup, with the hints and the name after it.
            byte* hints = &lookup->Hints0;
            byte* list = null;
            lookup->Result = getaddrinfo(hints + HintsSize, null, hints, &list);
            lookup->List = list;
            Volatile.Write(ref lookup->Done, 1);
        }
    }

    // struct addrinfo: ai_family at 4, then the address and the next entry.
    // Linux puts ai_addr at 24 and ai_canonname at 32; macOS and Android the
    // other way round (Windows: win/runtime.c turns it into Linux's).
    static IPAddress[] Addresses(byte* list)
    {
        unsafe
        {
            // SAFETY: the C calls get memory on this stack (or from calloc), of
            // the sizes they are told, and keep none of it past the call.
            int count = 0;
            for (byte* entry = list; entry != null; entry = *(byte**)(entry + 40))
                count++;
            var addresses = new IPAddress[count];
            int n = 0;
            for (byte* entry = list; entry != null; entry = *(byte**)(entry + 40))
            {
                byte* address = *(byte**)(entry + (BsdLookup ? 32 : 24));
                IPEndPoint endpoint = address == null ? null : Read(address);
                if (endpoint != null)
                    addresses[n++] = endpoint.Address;
            }
            if (n == count)
                return addresses;
            var some = new IPAddress[n];
            new ReadOnlySpan<IPAddress>(addresses, 0, n).CopyTo(some);
            return some;
        }
    }
}
