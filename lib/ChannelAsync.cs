// Channel<T>'s async side, after .NET's ChannelReader/ChannelWriter:
//
//   await foreach (int value in channel.ReceiveAllAsync())   // until closed
//       ...
//   while (await channel.WaitToReceiveAsync())               // the same, by hand
//       while (channel.TryReceive(out int value))
//           ...
//   int one = await channel.ReceiveAsync();                  // ends the program if closed
//   await channel.SendAsync(42);                             // waits while full
//
// An async method waiting on a channel costs no fiber: it's a task in the
// channel's lists, completed by the Send or Receive that serves it. Those
// tasks are reused (one per waiter at a time, freed once its result is
// read), so a loop that awaits a channel allocates nothing: as in .NET, the
// ValueTask can be awaited once (AsTask() gives a Task to keep). Fibers
// and async methods can share a channel; when both wait, the fibers are
// served first. Waits take a CancellationToken; a canceled one leaves the
// lists, and awaiting it ends the program as .NET's OperationCanceledException
// would (see lib/Cancellation.cs).
//
// Not on WebAssembly, which has no fibers.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public sealed partial class Channel<T>
{
    /// <summary>
    /// Receives the next value, waiting (without a fiber) while the channel is
    /// empty. Awaiting it ends the program if the channel is closed and empty
    /// (as .NET's ReadAsync throws): ReceiveAllAsync and WaitToReceiveAsync
    /// stop at the end instead.
    /// </summary>
    public ValueTask<T> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<T>(cancellationToken);
        if (TryReceive(out T value))
            return new ValueTask<T>(value);
        if (_closed)
            throw new InvalidOperationException("The channel has been closed.");
        return new ValueTask<T>(Waiters().AddReceiver(cancellationToken));

    }

    /// <summary>
    /// Sends <paramref name="value"/>, waiting (without a fiber) while the
    /// channel is full. Sending on a closed channel, or closing it while a
    /// send waits, ends the program, as Send does.
    /// </summary>
    public ValueTask SendAsync(T value, CancellationToken cancellationToken = default)
    {
        if (_closed)
            Environment.FailFast("Channel.SendAsync: the channel is closed");
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        bool receiverWaits;
        unsafe { receiverWaits = _receivers != null; } // SAFETY: a plain read
        if (receiverWaits || (_async != null && _async.HasReceiver) || _count < Capacity)
        {
            Send(value); // doesn't wait
            return default;
        }
        ChannelWaiters<T> waiters = Waiters();
        Task sent = waiters.AddSender(value, cancellationToken);
        var result = new ValueTask(sent);
        waiters.Available(); // capacity 0: a value waits to be taken
        return result;
    }

    /// <summary>
    /// Waits (without a fiber) until a value can be received: true then, false
    /// once the channel is closed and empty. TryReceive takes the value.
    /// </summary>
    public ValueTask<bool> WaitToReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<bool>(cancellationToken);
        bool senderWaits;
        unsafe { senderWaits = _senders != null; } // SAFETY: a plain read
        if (_count > 0 || senderWaits || (_async != null && _async.HasSender))
            return new ValueTask<bool>(true);
        if (_closed)
            return new ValueTask<bool>(false);
        return new ValueTask<bool>(Waiters().AddWaiter(cancellationToken));
    }

    /// <summary>Every value, for `await foreach`, until the channel is closed and empty.</summary>
    public IAsyncEnumerable<T> ReceiveAllAsync(CancellationToken cancellationToken = default) => new ReceiveAll(this, cancellationToken);

    ChannelWaiters<T> Waiters()
    {
        if (_async is ChannelWaiters<T> waiters)
            return waiters;
        waiters = new ChannelWaiters<T>();
        _async = waiters;
        return waiters;
    }

    // await foreach's enumerator, by hand rather than an async method, so a
    // loop allocates nothing: the task it hands out is reused for every
    // step, and it waits on WaitToReceiveAsync's as a continuation itself.
    // A continuation, not a plain observer: it runs from the dispatcher's
    // queue, never inside the Send that woke it (which hasn't parked yet).
    sealed class ReceiveAll(Channel<T> channel, CancellationToken token) : System.Runtime.CompilerServices.Continuation, IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        Task<bool>? _next;
        ValueTask<bool> _waiting;

        public T Current { get; private set; } = default!;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.CanBeCanceled)
                token = cancellationToken;
            return this;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (channel.TryReceive(out T value))
            {
                Current = value;
                return new ValueTask<bool>(true);
            }
            if (_next == null)
                _next = new Task<bool> { Reusable = true, Owned = true };
            else
                _next.Reset();
            var next = new ValueTask<bool>(_next);
            Step();
            return next;
        }

        // Takes a value, or waits until there may be one.
        void Step()
        {
            while (true)
            {
                if (channel.TryReceive(out T value))
                {
                    Current = value;
                    _next!.TrySetResult(true);
                    return;
                }
                ValueTask<bool> wait = channel.WaitToReceiveAsync(token);
                if (!wait.IsCompleted)
                {
                    _waiting = wait;
                    wait._task!.AddObserver(this);
                    return;
                }
                if (!Ended(wait))
                    continue;
                return;
            }
        }

        // WaitToReceiveAsync's task completed (and the dispatcher got to us).
        internal override void Resume()
        {
            if (!Ended(_waiting))
                Step();
        }

        // Reads a finished WaitToReceiveAsync: true if that ends the step
        // (closed, or canceled), having completed _next.
        bool Ended(ValueTask<bool> wait)
        {
            if (wait.IsCanceled)
            {
                wait.AsTask(); // frees it
                _next!.Complete(true);
                return true;
            }
            if (wait.Result)
                return false;
            _next!.TrySetResult(false);
            return true;
        }

        public ValueTask DisposeAsync() => default;
    }
}

// The async methods waiting on one channel, each a task in a list, in order.
// Every node made is kept (Spare) and reused once its result has been read.
sealed class ChannelWaiters<T> : Channel<T>.AsyncWaiters
{
    Receiver? _receivers, _receiversTail, _spareReceivers;
    Sender? _senders, _sendersTail, _spareSenders;
    Waiter? _waiters, _spareWaiters;

    sealed class Receiver : Task<T>
    {
        internal Receiver? Next, Spare;
        internal CancellationTokenRegistration Registration;
        internal Registration? OnCancel; // kept: attached for each wait with a token
        internal ChannelWaiters<T>? Owner;
    }

    sealed class Sender : Task
    {
        internal Sender? Next, Spare;
        internal T Value = default!;
        internal CancellationTokenRegistration Registration;
        internal Registration? OnCancel;
        internal ChannelWaiters<T>? Owner;
    }

    sealed class Waiter : Task<bool>
    {
        internal Waiter? Next, Spare;
        internal CancellationTokenRegistration Registration;
        internal Registration? OnCancel;
        internal ChannelWaiters<T>? Owner;
    }

    internal override bool HasSender => _senders != null;
    internal override bool HasReceiver => _receivers != null;

    // Made by the static constructor, outside any object scope: a lambda the compiler caches on first use could be made inside one (scripts/static-stores).
    static readonly Action<object?> s_cancelReceiver = static o => { var r = (Receiver)o!; r.Owner!.Cancel(r); };

    internal Task<T> AddReceiver(CancellationToken token)
    {
        Receiver? r = _spareReceivers;
        while (r != null && !(r.IsCompleted && !r.Owned))
            r = r.Spare;
        if (r == null)
        {
            r = new Receiver { Reusable = true, Owned = true, Spare = _spareReceivers };
            _spareReceivers = r;
        }
        else
        {
            r.Reset();
        }
        r.Next = null;
        if (_receiversTail == null)
            _receivers = r;
        else
            _receiversTail.Next = r;
        _receiversTail = r;
        if (token.CanBeCanceled)
        {
            r.OnCancel ??= new Registration(null, null, s_cancelReceiver, r);
            r.Owner = this;
            r.Registration = token.Attach(r.OnCancel);
        }
        return r;
    }

    // As above: made by the static constructor (scripts/static-stores).
    static readonly Action<object?> s_cancelSender = static o => { var s = (Sender)o!; s.Owner!.Cancel(s); };

    internal Task AddSender(T value, CancellationToken token)
    {
        Sender? s = _spareSenders;
        while (s != null && !(s.IsCompleted && !s.Owned))
            s = s.Spare;
        if (s == null)
        {
            s = new Sender { Reusable = true, Owned = true, Spare = _spareSenders };
            _spareSenders = s;
        }
        else
        {
            s.Reset();
        }
        s.Value = value;
        s.Next = null;
        if (_sendersTail == null)
            _senders = s;
        else
            _sendersTail.Next = s;
        _sendersTail = s;
        if (token.CanBeCanceled)
        {
            s.OnCancel ??= new Registration(null, null, s_cancelSender, s);
            s.Owner = this;
            s.Registration = token.Attach(s.OnCancel);
        }
        return s;
    }

    // As above: made by the static constructor (scripts/static-stores).
    static readonly Action<object?> s_cancelWaiter = static o => { var w = (Waiter)o!; w.Owner!.Cancel(w); };

    internal Task<bool> AddWaiter(CancellationToken token)
    {
        Waiter? w = _spareWaiters;
        while (w != null && !(w.IsCompleted && !w.Owned))
            w = w.Spare;
        if (w == null)
        {
            w = new Waiter { Reusable = true, Owned = true, Spare = _spareWaiters };
            _spareWaiters = w;
        }
        else
        {
            w.Reset();
        }
        w.Next = _waiters;
        _waiters = w;
        if (token.CanBeCanceled)
        {
            w.OnCancel ??= new Registration(null, null, s_cancelWaiter, w);
            w.Owner = this;
            w.Registration = token.Attach(w.OnCancel);
        }
        return w;
    }

    internal override bool Give(T value)
    {
        Receiver? r = _receivers;
        if (r == null)
            return false;
        _receivers = r.Next;
        if (_receivers == null)
            _receiversTail = null;
        r.Next = null;
        Unregister(ref r.Registration);
        r.TrySetResult(value);
        return true;
    }

    internal override bool Take(out T value)
    {
        Sender? s = _senders;
        if (s == null)
        {
            value = default!;
            return false;
        }
        _senders = s.Next;
        if (_senders == null)
            _sendersTail = null;
        s.Next = null;
        value = s.Value;
        s.Value = default!;
        Unregister(ref s.Registration);
        s.Complete();
        return true;
    }

    internal override void Available()
    {
        Waiter? w = _waiters;
        _waiters = null;
        while (w != null)
        {
            Waiter? next = w.Next;
            w.Next = null;
            Unregister(ref w.Registration);
            w.TrySetResult(true);
            w = next;
        }
    }

    internal override void Closed()
    {
        if (_receivers != null)
            Environment.FailFast("Channel.ReceiveAsync: the channel was closed while receiving (ReceiveAllAsync and WaitToReceiveAsync stop at the end instead)");
        if (_senders != null)
            Environment.FailFast("Channel.SendAsync: the channel was closed while sending");
        Waiter? w = _waiters;
        _waiters = null;
        while (w != null)
        {
            Waiter? next = w.Next;
            w.Next = null;
            Unregister(ref w.Registration);
            w.TrySetResult(false);
            w = next;
        }
    }

    static void Unregister(ref CancellationTokenRegistration registration)
    {
        registration.Dispose();
        registration = default;
    }

    void Cancel(Receiver r)
    {
        Receiver? previous = null;
        for (Receiver? at = _receivers; at != null; previous = at, at = at.Next)
            if (at == r)
            {
                if (previous == null)
                    _receivers = r.Next;
                else
                    previous.Next = r.Next;
                if (_receiversTail == r)
                    _receiversTail = previous;
                r.Next = null;
                r.Registration = default;
                r.Complete(true);
                return;
            }
    }

    void Cancel(Sender s)
    {
        Sender? previous = null;
        for (Sender? at = _senders; at != null; previous = at, at = at.Next)
            if (at == s)
            {
                if (previous == null)
                    _senders = s.Next;
                else
                    previous.Next = s.Next;
                if (_sendersTail == s)
                    _sendersTail = previous;
                s.Next = null;
                s.Value = default!;
                s.Registration = default;
                s.Complete(true);
                return;
            }
    }

    void Cancel(Waiter w)
    {
        Waiter? previous = null;
        for (Waiter? at = _waiters; at != null; previous = at, at = at.Next)
            if (at == w)
            {
                if (previous == null)
                    _waiters = w.Next;
                else
                    previous.Next = w.Next;
                w.Next = null;
                w.Registration = default;
                w.Complete(true);
                return;
            }
    }
}
