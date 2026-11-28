// async/await on real .NET, with zero mode's rule: one thread's worth of code
// at a time. In zero mode lib/Tasks.cs runs every continuation of an async
// method on the thread that awaited, between the fibers. Real .NET's Task
// would run it on whichever thread-pool thread completed the awaited task,
// alongside the rest of the program, which was written with no locks.
//
// So the main thread (and any thread that runs fibers) has a
// SynchronizationContext, which is where `await` sends continuations. They
// queue up here and run on a thread of the group that holds the turn:
// - between fibers, in Fiber.Run (BeforeRound);
// - wherever such a thread waits inside .NET (Task.Wait, a lock, a
//   SemaphoreSlim, a wait handle: .NET calls Wait here), which is also
//   what lets a fiber wait for such a thing without stopping the others, and
//   an `async Main` end: its caller waits for the task, and that wait runs
//   the continuations and the fibers.
//
// What stays different from zero mode:
// - Task.Run and a System.Threading.Timer's callback run on the thread
//   pool, in parallel with the program (zero mode: on a fiber, in turn).
//   What they await comes back here only if they were started from here.
// - Task.Exception is an AggregateException, and Wait and Result throw
//   one (zero mode: the exception itself).
// - A faulted task nothing looked at isn't reported when Main returns.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public static partial class Fiber
{
    [ThreadStatic] static bool t_plainWait;
    [ThreadStatic] static int t_slice;

    sealed class ZeroContext : SynchronizationContext
    {
        const int TimedOut = 258; // WaitHandle.WaitTimeout
        readonly Carried _group;

        internal ZeroContext(Carried group)
        {
            _group = group;
            SetWaitNotificationRequired();
        }

        public override SynchronizationContext CreateCopy() => this;

        public override void Post(SendOrPostCallback d, object state)
        {
            _group.Posted.Enqueue((d, state));
            _group.EverPosted = true;
            _group.Signal.Set();
        }

        public override void Send(SendOrPostCallback d, object state) => d(state);

        // .NET calls this where a thread with this context would block
        // inside the runtime. Until what it waits for is there, the thread
        // runs the continuations queued and lets the fibers run: a fiber by
        // yielding, the thread that runs them (Main's) by running them.
        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            if (t_waitingForTurn || t_plainWait)
                return WaitHelper(waitHandles, waitAll, millisecondsTimeout);
            Turn turn = t_turn;
            bool fiber = turn != null && turn.IsFiber;
            long start = Environment.TickCount64;
            for (int tries = 0; ; tries++)
            {
                Drain(_group);
                // A fiber that finds nothing for a while sleeps a millisecond
                // per look: what it waits for may come from outside (a
                // thread, a timer), and it shouldn't spin a processor.
                int result = WaitHelper(waitHandles, waitAll, fiber && tries >= 200 ? 1 : 0);
                if (result != TimedOut)
                    return result;
                long left = millisecondsTimeout < 0 ? long.MaxValue : millisecondsTimeout - (Environment.TickCount64 - start);
                if (left <= 0)
                    return TimedOut;
                if (fiber)
                    Yield();
                else if (FibersCanRun())
                    RunSlice();
                else
                {
                    // Nothing to run: sleep until what's awaited, a
                    // continuation, or the next sleeping fiber's time.
                    int wait = (int)Math.Min(Math.Min(left, NextSleeperMilliseconds()), int.MaxValue);
                    if (waitAll && waitHandles.Length > 1)
                    {
                        result = WaitHelper(waitHandles, true, Math.Min(wait, 1));
                    }
                    else
                    {
                        IntPtr[] both = new IntPtr[waitHandles.Length + 1];
                        waitHandles.CopyTo(both, 0);
                        both[^1] = _group.Signal.SafeWaitHandle.DangerousGetHandle();
                        result = WaitHelper(both, false, wait);
                        if (result == both.Length - 1)
                            result = TimedOut; // a continuation: round again
                    }
                    if (result != TimedOut)
                        return result;
                }
            }
        }
    }

    // Runs the continuations queued for this group of threads.
    static void Drain(Carried group)
    {
        while (group.Posted.TryDequeue(out var work))
            work.Item1(work.Item2);
    }

    static void WaitPosted(Carried group, int milliseconds)
    {
        t_plainWait = true;
        group.Signal.WaitOne(milliseconds);
        t_plainWait = false;
    }

    // Whether timers or tasks are out that the scheduler can't see: .NET's,
    // which complete on the thread pool. Timer.ActiveCount counts Task.Delay's
    // timers too.
    static bool WorkOutside()
    {
        Carried group = Mine.Carried;
        return !group.Posted.IsEmpty || Timer.ActiveCount > 0;
    }

    static bool FibersCanRun()
    {
        unsafe
        {
            // SAFETY: this thread's scheduler, and its sleepers' records.
            Scheduler* s = t_scheduler;
            return s != null && s->Current == null && s->Live > 0
                && (s->RunHead != null || (s->Sleepers != null && s->Sleepers->WakeAt <= Clock.Monotonic));
        }
    }

    static long NextSleeperMilliseconds()
    {
        unsafe
        {
            // SAFETY: as FibersCanRun.
            Scheduler* s = t_scheduler;
            if (s == null || s->Sleepers == null)
                return long.MaxValue;
            return Math.Max(1, (s->Sleepers->WakeAt - Clock.Monotonic) / 1_000_000 + 1);
        }
    }

    // A few rounds of the scheduler, from a wait outside Fiber.Run: zero
    // mode's "awaiting outside a fiber runs the fibers".
    static void RunSlice()
    {
        int before = t_slice;
        t_slice = 64;
        RunUntil(null);
        t_slice = before;
    }

    // Called by lib/Fibers.cs's scheduler before each fiber's turn. Runs the
    // continuations queued, then says whether to go on: false ends Run.
    static unsafe bool BeforeRound(Scheduler* s, Task until)
    {
        Carried group = Mine.Carried;
        while (true)
        {
            Drain(group);
            if (until != null && until.IsCompleted)
                return false;
            unsafe
            {
                // SAFETY: the scheduler Run passed, between two fibers' turns.
                bool runnable = s->RunHead != null;
                bool sleeperDue = s->Sleepers != null && s->Sleepers->WakeAt <= Clock.Monotonic;
                if (t_slice > 0)
                {
                    // From a wait: only while there is something to run now.
                    if (s->Live == 0 || (!runnable && !sleeperDue) || --t_slice == 0)
                        return false;
                    return true;
                }
                if (s->Live == 0)
                {
                    // No fibers left: Run stays for the timers and tasks out.
                    if (!WorkOutside())
                        return false;
                    WaitPosted(group, 2);
                    continue;
                }
                if (runnable || sleeperDue || s->IoWaiters != null)
                    return true;
                // Every fiber waits. With nothing of .NET's out, that's for
                // lib/Fibers.cs to sleep through or to report; otherwise a
                // continuation may yet wake one.
                if (!group.EverPosted && Timer.ActiveCount == 0)
                    return true;
                WaitPosted(group, s->Sleepers != null ? (int)Math.Min(NextSleeperMilliseconds(), 1000) : 50);
                // (Once the program has awaited something, a continuation may
                // still come from input or output the scheduler can't see: no
                // report then, where zero mode would know and say.)
            }
        }
    }

    /// <summary>
    /// Gives the calling thread zero mode's rule for async methods: their
    /// continuations run on it, one at a time. Called for the main thread
    /// before Main (dotnet/Sample/ZeroStartup.cs).
    /// </summary>
    public static void UseThisThreadForContinuations() => _ = Mine;
}

// ---- what lib/ adds to .NET's tasks and timers --------------------------------

public static class ZeroTaskExtensions
{
    extension(Task)
    {
        /// <summary>lib/ThreadTasks.cs: runs the function on another thread. .NET's Task.Run does that already.</summary>
        public static Task<TResult> RunOnThread<TResult>(Func<TResult> function) => Task.Run(function);
        public static Task<TResult> RunOnThread<TResult>(Func<TResult> function, CancellationToken cancellationToken) => Task.Run(function, cancellationToken);
        public static Task RunOnThread(Action action) => Task.Run(action);
        public static Task RunOnThread(Action action, CancellationToken cancellationToken) => Task.Run(action, cancellationToken);
    }

    /// <summary>lib/Timers.cs: waits for the next tick without await; false once the timer is disposed.</summary>
    public static bool WaitForNextTick(this PeriodicTimer timer) => timer.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult();
}

// lib/ChannelAsync.cs: a channel's async operations. There they wait without
// a fiber, as continuations; here each one that has to wait is a fiber that
// makes lib/Fibers.cs's own blocking call and completes a task.
public sealed partial class Channel<T>
{
    public ValueTask<T> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<T>(cancellationToken);
        if (TryReceive(out T value))
            return new ValueTask<T>(value);
        if (_closed)
            throw new InvalidOperationException("The channel has been closed.");
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Fiber.Spawn((this, source, cancellationToken), static s =>
        {
            if (s.Item1.Receive(out T received, s.cancellationToken))
                s.source.SetResult(received);
            else if (s.cancellationToken.IsCancellationRequested)
                s.source.SetCanceled(s.cancellationToken);
            else
                s.source.SetException(new InvalidOperationException("The channel has been closed."));
        });
        return new ValueTask<T>(source.Task);
    }

    public ValueTask SendAsync(T value, CancellationToken cancellationToken = default)
    {
        if (_closed)
            Environment.FailFast("Channel.SendAsync: the channel is closed");
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        bool receiverWaits;
        unsafe { receiverWaits = _receivers != null; } // SAFETY: a plain read
        if (receiverWaits || _count < Capacity)
        {
            Send(value); // doesn't wait
            return default;
        }
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Fiber.Spawn((this, source, value), static s =>
        {
            s.Item1.Send(s.value);
            s.source.SetResult();
        });
        return new ValueTask(source.Task);
    }

    public ValueTask<bool> WaitToReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<bool>(cancellationToken);
        if (CanReceiveNow)
            return new ValueTask<bool>(true);
        if (_closed)
            return new ValueTask<bool>(false);
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Fiber.Spawn((this, source, cancellationToken), static s =>
        {
            // No call of lib/Fibers.cs's waits for a value without taking it: look, and let the others run.
            for (int looks = 0; ; looks++)
            {
                if (s.cancellationToken.IsCancellationRequested) { s.source.SetCanceled(s.cancellationToken); return; }
                if (s.Item1.CanReceiveNow) { s.source.SetResult(true); return; }
                if (s.Item1._closed) { s.source.SetResult(false); return; }
                if (looks < 100) Fiber.Yield(); else Fiber.Sleep(1);
            }
        });
        return new ValueTask<bool>(source.Task);
    }

    bool CanReceiveNow
    {
        get
        {
            unsafe { return _count > 0 || _senders != null; } // SAFETY: plain reads
        }
    }

    public async IAsyncEnumerable<T> ReceiveAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await WaitToReceiveAsync(cancellationToken))
            while (TryReceive(out T value))
                yield return value;
    }
}

// lib/FileAsync.cs: whole files, read and written off the program's thread.
// The BCL's do that.
public partial struct File
{
    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) => System.IO.File.ReadAllBytesAsync(path, cancellationToken);
    public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) => System.IO.File.ReadAllTextAsync(path, cancellationToken);
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) => System.IO.File.WriteAllBytesAsync(path, bytes, cancellationToken);
    public static Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => System.IO.File.WriteAllBytesAsync(path, bytes, cancellationToken);
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) => System.IO.File.WriteAllTextAsync(path, contents, cancellationToken);
    public static Task AppendAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) => System.IO.File.AppendAllTextAsync(path, contents, cancellationToken);
}

namespace System.Threading.Tasks
{
    /// <summary>
    /// lib/ThreadCompletion.cs: a TaskCompletionSource that any thread may
    /// complete; whoever awaits its task carries on on its own thread. With
    /// .NET's tasks that is a TaskCompletionSource whose continuations don't
    /// run inside SetResult.
    /// </summary>
    public abstract class ThreadCompletion
    {
        private protected ThreadCompletion() { }
    }

    public sealed class ThreadCompletionSource : ThreadCompletion
    {
        readonly TaskCompletionSource _source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => _source.Task;
        public void SetResult() => _source.SetResult();
        public bool TrySetResult() => _source.TrySetResult();
        public void SetCanceled() => _source.SetCanceled();
        public bool TrySetCanceled() => _source.TrySetCanceled();
    }

    public sealed class ThreadCompletionSource<TResult> : ThreadCompletion
    {
        readonly TaskCompletionSource<TResult> _source = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TResult> Task => _source.Task;
        public void SetResult(TResult result) => _source.SetResult(result);
        public bool TrySetResult(TResult result) => _source.TrySetResult(result);
        public void SetCanceled() => _source.SetCanceled();
        public bool TrySetCanceled() => _source.TrySetCanceled();
    }
}

namespace System.Runtime.CompilerServices
{
    // An async method on a fiber of its own from its first wait to its end
    // (lib/Tasks.cs's OwnFiberMethodBuilder). The fiber waits for what the
    // method awaits, then calls it again.
    sealed class OwnFiberRun
    {
        public IAsyncStateMachine Machine;
        public readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        public bool Started, Waiting;
        public readonly Action Signal;

        public OwnFiberRun() => Signal = Ready.Set;

        public void Await<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            Ready.Reset();
            Waiting = true;
            if (!Started)
            {
                Started = true;
                Machine = stateMachine; // boxed here, with the builder that holds this
                awaiter.OnCompleted(Signal);
                Fiber.Spawn(this, static run => run.Loop());
            }
            else
                awaiter.OnCompleted(Signal);
        }

        void Loop()
        {
            do
            {
                Ready.Wait(); // lets the other fibers run meanwhile (ZeroContext.Wait)
                Waiting = false;
                Machine.MoveNext();
            }
            while (Waiting);
        }
    }

    /// <summary>[AsyncMethodBuilder(typeof(OwnFiberMethodBuilder))]: an `async Task` method on a fiber of its own.</summary>
    public struct OwnFiberMethodBuilder
    {
        TaskCompletionSource _source;
        OwnFiberRun _run;

        public static OwnFiberMethodBuilder Create() => new OwnFiberMethodBuilder { _source = new TaskCompletionSource(), _run = new OwnFiberRun() };
        public Task Task => _source.Task;
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
        public void SetResult() => _source.SetResult();

        public void SetException(Exception exception)
        {
            if (exception is OperationCanceledException canceled)
                _source.TrySetCanceled(canceled.CancellationToken);
            else
                _source.SetException(exception);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => _run.Await(ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => _run.Await(ref awaiter, ref stateMachine);
    }

    /// <summary>OwnFiberMethodBuilder for an `async Task&lt;T&gt;` method.</summary>
    public struct OwnFiberMethodBuilder<TResult>
    {
        TaskCompletionSource<TResult> _source;
        OwnFiberRun _run;

        public static OwnFiberMethodBuilder<TResult> Create() => new OwnFiberMethodBuilder<TResult> { _source = new TaskCompletionSource<TResult>(), _run = new OwnFiberRun() };
        public Task<TResult> Task => _source.Task;
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
        public void SetResult(TResult result) => _source.SetResult(result);

        public void SetException(Exception exception)
        {
            if (exception is OperationCanceledException canceled)
                _source.TrySetCanceled(canceled.CancellationToken);
            else
                _source.SetException(exception);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => _run.Await(ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => _run.Await(ref awaiter, ref stateMachine);
    }
}
