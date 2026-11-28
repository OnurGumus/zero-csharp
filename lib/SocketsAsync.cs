// The async socket methods, as in .NET: Socket's ReceiveAsync, SendAsync,
// AcceptAsync, ConnectAsync; TcpListener.AcceptTcpClientAsync,
// TcpClient.ConnectAsync, UdpClient's ReceiveAsync and SendAsync, and
// NetworkStream's ReadAsync and WriteAsync (lib/Tasks.cs has the tasks).
//
//   static async Task Serve(TcpClient client)
//   {
//       NetworkStream stream = client.GetStream();
//       byte[] buffer = new byte[4096];
//       int n;
//       while ((n = await stream.ReadAsync(buffer)) > 0)
//           await stream.WriteAsync(buffer.AsMemory(0, n));
//   }
//
// An operation that can finish at once does, with no task and no
// allocation. One that has to wait costs no fiber: the socket goes on
// Fiber.Run's list of sockets to poll, with the operation in place of a
// fiber, and when poll says the socket is ready, Fiber.Run tries the
// operation again and completes its task. So a server can wait on many
// thousands of connections with async methods, each costing what its state
// machine does (a hundred bytes or so), where a fiber each would cost a stack.
// Each socket reuses one task for its receives and one for its sends, so a
// loop of ReadAsync and WriteAsync allocates nothing.
//
// What differs from .NET:
// - One receive (ReceiveAsync, ReceiveFromAsync) and one send at a time on a
//   socket: starting another while one waits ends the program. .NET queues
//   them. Likewise one AcceptAsync and one ConnectAsync.
// - A failure ends the program as SocketException would, when it happens.
//   NetworkStream's ReadAsync and WriteAsync don't: as Read and Write, they
//   return 0 (or drop the bytes) and set Error.
// - Canceling the token cancels the task, as in .NET, and awaiting a canceled
//   task ends the program (see lib/Tasks.cs). NetworkStream again doesn't:
//   canceled, ReadAsync returns 0 with Error OperationAborted, and the next
//   call goes on. Its ReadTimeout and WriteTimeout apply to the async calls
//   too (in .NET they don't), returning 0 with Error TimedOut. So an async
//   server can drop a slow client without ending the program.
// - Closing a socket wakes its waiting operations: Socket's tasks (and
//   TcpListener's, UdpClient's) are canceled, where .NET's fault with
//   OperationAborted, and NetworkStream's return 0 with Error OperationAborted.
//   To stop an accept loop, Stop the listener (or cancel a token), and await
//   the accept as a plain Task with SuppressThrowing, then look at IsCanceled:
//     Task<TcpClient> next = listener.AcceptTcpClientAsync();
//     await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
//     if (next.IsCanceled) return;

using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Sockets;

// One kind of operation on one socket (receiving, say), waiting or not. Its
// record goes on Fiber.Run's poll list while it waits; Ready tries again.
internal abstract class SocketOperation<TResult> : Fiber.IoWaiter
{
    protected readonly Socket Socket;
#if ZERO_DOTNET
    // Real .NET: the scheduler keeps this record's address, so it can't be in
    // an object the GC moves.
    readonly nint _recordMemory = Fiber.NewRecord();
    ref Fiber.Record _record { get { unsafe { return ref *(Fiber.Record*)_recordMemory; } } } // SAFETY: allocated with this object, freed by its finalizer
    ~SocketOperation() => Fiber.FreeRecord(_recordMemory); // off the scheduler's list by now, or this object would still be referenced
#else
    Fiber.Record _record;           // on Fiber.Run's list while waiting
#endif
    readonly short _events;
    readonly Task<TResult> _task = new Task<TResult> { Reusable = true };
    Registration? _onCancel;
    CancellationTokenRegistration _registration;
    long _deadline;
    bool _waiting;
    // Quiet (NetworkStream's, and ConnectAsync(host)'s tries): failures and
    // cancellation complete with Result, and Error says why, rather than
    // ending the program or canceling. A stream's Error is set too.
    bool _quiet;
    NetworkStream? _stream;
    protected SocketError Error;
    protected TResult Result = default!;

    protected SocketOperation(Socket socket, short events)
    {
        Socket = socket;
        _events = events;
    }

    // Tries without waiting: true once done, with Result or Error set.
    protected abstract bool Attempt(int fd);

    protected ValueTask<TResult> Start(bool quiet, NetworkStream? stream, long deadline, CancellationToken cancellationToken)
    {
        if (_waiting)
            throw new InvalidOperationException("Socket: an operation of this kind is already waiting on this socket (one receive and one send at a time)");
        _quiet = quiet || stream != null;
        _stream = stream;
        Error = SocketError.Success;
        int fd = (int)Socket.Handle;
        if (fd < 0)
        {
            Error = SocketError.OperationAborted;
            return Now();
        }
        if (cancellationToken.IsCancellationRequested)
        {
            if (!_quiet)
                return ValueTask.FromCanceled<TResult>(cancellationToken);
            Error = SocketError.OperationAborted;
            return Now();
        }
        if (Attempt(fd))
            return Now();
        _waiting = true;
        _deadline = deadline;
        _task.Reset();
        Wait(fd);
        if (cancellationToken.CanBeCanceled)
        {
            _onCancel ??= new Registration(null, null, static o => ((SocketOperation<TResult>)o!).Cancel(), this);
            _registration = cancellationToken.Attach(_onCancel);
        }
        return new ValueTask<TResult>(_task);
    }

    // Done without waiting.
    ValueTask<TResult> Now()
    {
        Report();
        return new ValueTask<TResult>(Result);
    }

    // A failure: the stream's Error, or the end of the program.
    void Report()
    {
        if (Error == SocketError.Success)
            return;
        if (!_quiet)
            throw new SocketException((int)Error);
        if (_stream != null && _stream.Error == SocketError.Success)
            _stream.Error = Error;
    }

    void Wait(int fd)
    {
        unsafe
        {
            // SAFETY: zero mode's objects don't move, and the record leaves
            // Fiber.Run's list (Ready, Cancel) before this operation can end.
            _record.Body = this;
            SocketNative.UsePoll();
            Fiber.WaitForIOAsync((Fiber.Record*)Unsafe.AsPointer(ref _record), fd, _events, _deadline);
        }
    }

    internal override void Ready(short revents)
    {
        if (revents == Fiber.Cancelled)
        {
            // The socket was closed: canceled, as .NET's task would fault
            // with OperationAborted (a stream's ReadAsync returns 0).
            Finish(canceled: true);
            return;
        }
        if (revents == Fiber.TimedOut)
        {
            Error = SocketError.TimedOut;
        }
        else
        {
            int fd = (int)Socket.Handle;
            if (fd < 0)
            {
                Error = SocketError.OperationAborted;
            }
            else if (!Attempt(fd))
            {
                Wait(fd); // not ready after all
                return;
            }
        }
        Finish(canceled: false);
    }

    // The token was canceled while waiting.
    void Cancel()
    {
        bool waiting;
        unsafe { waiting = Fiber.StopWaitingForIO((Fiber.Record*)Unsafe.AsPointer(ref _record)); } // SAFETY: as in Wait
        if (waiting)
            Finish(canceled: true);
    }

    void Finish(bool canceled)
    {
        _registration.Dispose();
        _registration = default;
        _waiting = false;
        if (canceled)
        {
            if (!_quiet)
            {
                _task.Complete(true);
                return;
            }
            Error = SocketError.OperationAborted;
        }
        Report();
        _task.TrySetResult(Result);
    }
}

// Receive and ReceiveFrom: Result is how many bytes came.
internal sealed class ReceiveOperation(Socket socket) : SocketOperation<int>(socket, SocketNative.POLLIN)
{
    Memory<byte> _buffer;
    SocketFlags _flags;
    IPEndPoint? _from;

    internal ValueTask<int> Start(Memory<byte> buffer, SocketFlags flags, IPEndPoint? from, NetworkStream? stream, long deadline, CancellationToken cancellationToken)
    {
        _buffer = buffer;
        _flags = flags;
        _from = from;
        Result = 0;
        return Start(false, stream, deadline, cancellationToken);
    }

    protected override bool Attempt(int fd)
    {
        SocketError error = SocketNative.ReceiveOnce(fd, _buffer.Span, _flags, out int received, _from);
        if (error == SocketError.WouldBlock)
            return false;
        Error = error;
        Result = received;
        Socket.Failed(error);
        return true;
    }
}

// Send and SendTo: all of the bytes; Result is how many went.
internal sealed class SendOperation(Socket socket) : SocketOperation<int>(socket, SocketNative.POLLOUT)
{
    ReadOnlyMemory<byte> _bytes;
    SocketFlags _flags;
    IPEndPoint? _to;

    internal ValueTask<int> Start(ReadOnlyMemory<byte> bytes, SocketFlags flags, IPEndPoint? to, NetworkStream? stream, long deadline, CancellationToken cancellationToken)
    {
        _bytes = bytes;
        _flags = flags;
        _to = to;
        Result = 0;
        return Start(false, stream, deadline, cancellationToken);
    }

    protected override bool Attempt(int fd)
    {
        int sent = Result;
        SocketError error = SocketNative.SendSome(fd, _bytes.Span, _flags, _to, ref sent);
        Result = sent;
        if (error == SocketError.WouldBlock)
            return false;
        Error = error;
        Socket.Failed(error);
        return true;
    }
}

// Accept: Result is the new connection's socket.
internal sealed class AcceptOperation(Socket socket) : SocketOperation<Socket>(socket, SocketNative.POLLIN)
{
    internal ValueTask<Socket> Start(CancellationToken cancellationToken)
    {
        Result = null!;
        return Start(false, null, 0, cancellationToken);
    }

    protected override bool Attempt(int fd)
    {
        SocketError error = SocketNative.AcceptOnce(fd, out int accepted);
        if (error == SocketError.WouldBlock)
            return false;
        Error = error;
        if (error == SocketError.Success)
            Result = new Socket(accepted, Socket.AddressFamily, Socket.SocketType, Socket.ProtocolType);
        return true;
    }
}

// Connect: the first Attempt starts connecting, the next (once the socket
// can be written) asks how it went. Quiet for the tries of
// ConnectAsync(host, port), which go on to the next address.
internal sealed class ConnectOperation(Socket socket) : SocketOperation<bool>(socket, SocketNative.POLLOUT)
{
    IPEndPoint _endpoint = null!;
    bool _started;

    internal SocketError LastError => Error;

    internal ValueTask<bool> Start(IPEndPoint endpoint, bool quiet, CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _started = false;
        Result = false;
        return Start(quiet, null, 0, cancellationToken);
    }

    protected override bool Attempt(int fd)
    {
        SocketError error;
        if (!_started)
        {
            _started = true;
            error = SocketNative.ConnectStart(fd, _endpoint);
            if (error == SocketError.InProgress)
                return false;
        }
        else
        {
            error = SocketNative.ConnectResult(fd);
        }
        Error = error;
        Result = error == SocketError.Success;
        Socket.SetConnected(Result);
        return true;
    }
}

// A ValueTask's result, passed through a function once it comes: canceled
// if the source is. (An async method awaiting the source would end the
// program on a cancel, instead of passing it on.)
internal sealed class Mapped<TIn, TOut> : Observer
{
    readonly Task<TOut> _result = new Task<TOut>();
    readonly Func<TIn, TOut> _map;

    Mapped(Func<TIn, TOut> map) => _map = map;

    internal static ValueTask<TOut> Map(ValueTask<TIn> source, Func<TIn, TOut> map)
    {
        if (source.IsCompletedSuccessfully)
            return new ValueTask<TOut>(map(source.Result));
        Task<TIn> task = source.AsTask(); // a reused task gets a copy
        var mapped = new Mapped<TIn, TOut>(map);
        if (task.IsCompleted)
            mapped.Completed(task);
        else
            task.AddObserver(mapped);
        return new ValueTask<TOut>(mapped._result);
    }

    internal override void Completed(Task task)
    {
        if (task.IsCanceled)
            _result.Complete(true);
        else
            _result.TrySetResult(_map(((Task<TIn>)task)._result));
    }
}

/// <summary>What ReceiveFromAsync returns: how many bytes came, and from where.</summary>
public struct SocketReceiveFromResult
{
    /// <summary>How many bytes came.</summary>
    public int ReceivedBytes;
    /// <summary>Where they came from.</summary>
    public EndPoint RemoteEndPoint;
}

/// <summary>What UdpClient.ReceiveAsync returns: the datagram, and where it came from.</summary>
public readonly struct UdpReceiveResult
{
    /// <summary>A datagram and its sender.</summary>
    public UdpReceiveResult(byte[] buffer, IPEndPoint remoteEndPoint)
    {
        Buffer = buffer;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>The datagram.</summary>
    public byte[] Buffer { get; }
    /// <summary>Where it came from.</summary>
    public IPEndPoint RemoteEndPoint { get; }
}

public partial class Socket
{
    ReceiveOperation? _receiving;
    SendOperation? _sending;
    AcceptOperation? _accepting;
    ConnectOperation? _connecting;

    internal ReceiveOperation Receiving => _receiving ??= new ReceiveOperation(this);
    internal SendOperation Sending => _sending ??= new SendOperation(this);

    /// <summary>
    /// Receives what has arrived, up to buffer.Length bytes, waiting while
    /// nothing has; 0 when the other end has shut its sending down. Failing
    /// ends the program.
    /// </summary>
    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Receiving.Start(buffer, SocketFlags.None, null, null, 0, cancellationToken);

    /// <summary>ReceiveAsync, with flags (Peek).</summary>
    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketFlags socketFlags, CancellationToken cancellationToken = default)
        => Receiving.Start(buffer, socketFlags, null, null, 0, cancellationToken);

    /// <summary>Sends all of the bytes, waiting while the system's buffer is full. Failing ends the program.</summary>
    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => Sending.Start(buffer, SocketFlags.None, null, null, 0, cancellationToken);

    /// <summary>SendAsync, with flags.</summary>
    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, SocketFlags socketFlags, CancellationToken cancellationToken = default)
        => Sending.Start(buffer, socketFlags, null, null, 0, cancellationToken);

    /// <summary>Receives a datagram (UDP), and says where it came from. Bytes past buffer.Length are lost.</summary>
    public ValueTask<SocketReceiveFromResult> ReceiveFromAsync(Memory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        return Mapped<int, SocketReceiveFromResult>.Map(Receiving.Start(buffer, SocketFlags.None, from, null, 0, cancellationToken),
            n => new SocketReceiveFromResult { ReceivedBytes = n, RemoteEndPoint = from });
    }

    /// <summary>Sends a datagram to the endpoint (UDP).</summary>
    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEP, CancellationToken cancellationToken = default)
        => Sending.Start(buffer, SocketFlags.None, ToIP(remoteEP), null, 0, cancellationToken);

    /// <summary>The next connection, once one comes. Failing ends the program.</summary>
    public Task<Socket> AcceptAsync() => AcceptAsync(CancellationToken.None).AsTask();

    /// <summary>The next connection, once one comes, or canceled with the token.</summary>
    public ValueTask<Socket> AcceptAsync(CancellationToken cancellationToken)
        => (_accepting ??= new AcceptOperation(this)).Start(cancellationToken);

    /// <summary>Connects to the endpoint. Failing ends the program.</summary>
    public Task ConnectAsync(EndPoint remoteEP) => ConnectAsync(remoteEP, CancellationToken.None).AsTask();

    /// <summary>Connects to the endpoint, or is canceled with the token (a way to time a connection out). Failing ends the program.</summary>
    public ValueTask ConnectAsync(EndPoint remoteEP, CancellationToken cancellationToken)
        => Untyped((_connecting ??= new ConnectOperation(this)).Start(ToIP(remoteEP), false, cancellationToken));

    /// <summary>Connects to the address and port.</summary>
    public Task ConnectAsync(IPAddress address, int port) => ConnectAsync(new IPEndPoint(address, port));

    /// <summary>Connects to the address and port, or is canceled with the token.</summary>
    public ValueTask ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        => ConnectAsync(new IPEndPoint(address, port), cancellationToken);

    /// <summary>Connects to a host name's port, trying each of its addresses in turn.</summary>
    public Task ConnectAsync(string host, int port) => ConnectAsync(host, port, CancellationToken.None).AsTask();

    /// <summary>
    /// Connects to a host name's port, trying each of its addresses in turn.
    /// Failing, or the token's cancellation, ends the program (a canceled
    /// await would too).
    /// </summary>
    public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var connecting = _connecting ??= new ConnectOperation(this);
        SocketError error = SocketError.HostNotFound;
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
            // Quiet: a failure goes on to the next address.
            if (await connecting.Start(new IPEndPoint(target, port), true, cancellationToken))
                return;
            error = connecting.LastError;
            cancellationToken.ThrowIfCancellationRequested();
        }
        throw new SocketException((int)error);
    }

    // A ValueTask<bool> as a ValueTask: the same task, or done.
    static ValueTask Untyped(ValueTask<bool> value) => value._task == null ? default : new ValueTask(value._task);
}

public partial class NetworkStream
{
    /// <summary>
    /// Receives up to buffer.Length bytes, once some come; 0 when the other
    /// end is done sending, the connection broke (Error says how), ReadTimeout
    /// ran out (Error TimedOut) or the token was canceled (Error
    /// OperationAborted). The last two leave the stream usable.
    /// </summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (!Usable())
            return new ValueTask<int>(0);
        return Socket.Receiving.Start(buffer, SocketFlags.None, null, this, SocketNative.Deadline(Socket.ReceiveTimeout), cancellationToken);
    }

    /// <summary>
    /// Sends all of the bytes; on a broken connection, a WriteTimeout that ran
    /// out or a canceled token, it stops, and Error says why.
    /// </summary>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (!Usable())
            return default;
        ValueTask<int> sending = Socket.Sending.Start(buffer, SocketFlags.None, null, this, SocketNative.Deadline(Socket.SendTimeout), cancellationToken);
        return sending._task == null ? default : new ValueTask(sending._task);
    }
}

public partial class TcpListener
{
    /// <summary>The next connection as a TcpClient, once one comes. Failing ends the program.</summary>
    public Task<TcpClient> AcceptTcpClientAsync() => AcceptTcpClientAsync(CancellationToken.None).AsTask();

    /// <summary>The next connection as a TcpClient, or canceled with the token.</summary>
    public ValueTask<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
        => Mapped<Socket, TcpClient>.Map(Started.AcceptAsync(cancellationToken), static socket => new TcpClient(socket));

    /// <summary>The next connection as a socket, once one comes.</summary>
    public Task<Socket> AcceptSocketAsync() => Started.AcceptAsync();

    /// <summary>The next connection as a socket, or canceled with the token.</summary>
    public ValueTask<Socket> AcceptSocketAsync(CancellationToken cancellationToken) => Started.AcceptAsync(cancellationToken);
}

public partial class TcpClient
{
    /// <summary>Connects to the address and port. Failing ends the program.</summary>
    public Task ConnectAsync(IPAddress address, int port) => _socket.ConnectAsync(address, port);

    /// <summary>Connects to the address and port, or is canceled with the token.</summary>
    public ValueTask ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken) => _socket.ConnectAsync(address, port, cancellationToken);

    /// <summary>Connects to the endpoint.</summary>
    public Task ConnectAsync(IPEndPoint remoteEP) => _socket.ConnectAsync(remoteEP);

    /// <summary>Connects to the endpoint, or is canceled with the token.</summary>
    public ValueTask ConnectAsync(IPEndPoint remoteEP, CancellationToken cancellationToken) => _socket.ConnectAsync(remoteEP, cancellationToken);

    /// <summary>Connects to a host name's port.</summary>
    public Task ConnectAsync(string host, int port) => _socket.ConnectAsync(host, port);

    /// <summary>Connects to a host name's port, or is canceled with the token.</summary>
    public ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken) => _socket.ConnectAsync(host, port, cancellationToken);
}

public partial class UdpClient
{
    byte[]? _datagram; // 64 KB, for ReceiveAsync, which then copies what came

    /// <summary>The next datagram, in a new array, and where it came from.</summary>
    public Task<UdpReceiveResult> ReceiveAsync() => ReceiveAsync(CancellationToken.None).AsTask();

    /// <summary>The next datagram, in a new array, and where it came from; or canceled with the token.</summary>
    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = _datagram ??= new byte[65536];
        var from = new IPEndPoint(IPAddress.Any, 0);
        return Mapped<int, UdpReceiveResult>.Map(_socket.Receiving.Start(buffer, SocketFlags.None, from, null, 0, cancellationToken), n =>
        {
            byte[] datagram = new byte[n];
            new ReadOnlySpan<byte>(buffer, 0, n).CopyTo(datagram);
            return new UdpReceiveResult(datagram, from);
        });
    }

    /// <summary>Sends a datagram to the endpoint (null: the connected one).</summary>
    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint? endPoint, CancellationToken cancellationToken = default)
        => _socket.Sending.Start(datagram, SocketFlags.None, endPoint == null ? null : _socket.ToIP(endPoint), null, 0, cancellationToken);

    /// <summary>Sends a datagram to the connected endpoint.</summary>
    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
        => SendAsync(datagram, null, cancellationToken);

    /// <summary>Sends <paramref name="bytes"/> bytes of the array as a datagram to the endpoint.</summary>
    public Task<int> SendAsync(byte[] datagram, int bytes, IPEndPoint? endPoint)
        => SendAsync(new ReadOnlyMemory<byte>(datagram, 0, bytes), endPoint).AsTask();

    /// <summary>Sends <paramref name="bytes"/> bytes of the array as a datagram to the connected endpoint.</summary>
    public Task<int> SendAsync(byte[] datagram, int bytes)
        => SendAsync(new ReadOnlyMemory<byte>(datagram, 0, bytes), null).AsTask();
}
