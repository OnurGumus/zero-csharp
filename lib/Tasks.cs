// Tasks and async/await on fibers (lib/Fibers.cs).
//
//   static async Task<int> Fetch(int id) { await Task.Delay(100); return id * 2; }
//
//   static async Task<int> Main()
//   {
//       Task<int> a = Fetch(1), b = Fetch(2);      // both start now
//       int[] both = await Task.WhenAll(a, b);     // ~100 ms, not 200
//       return both[0] + both[1];
//   }
//
// The C# compiler turns an async method into a state machine and calls the
// builder here (AsyncTaskMethodBuilder) to run it. The method starts on its
// caller's stack, as in .NET, and runs until it awaits something that isn't
// done yet. Then, also as in .NET, the state machine is copied into a box on
// the heap (its size: a hundred bytes or so), which waits on the task, and
// the call returns to its caller. When the task completes, the box joins a
// queue that one fiber per thread, the dispatcher, works through: it calls
// each state machine again, which runs to its next await or its end.
// - A method that never waits makes no box, and its Task<T> is the only
//   allocation (Task, for one that returns nothing, is shared).
// - Task.Delay makes no fiber: one timer fiber per thread completes them all.
// - Awaiting outside a fiber (in an async Main, or task.Wait() / .Result
//   there) runs the fibers, as Fiber.Run does, until the task is done.
// - Wait() and .Result park the fiber rather than block the thread, so
//   they're safe, unlike in .NET. So are the fibers' own blocking calls
//   (Channel.Receive, Fiber.Sleep) in an async method: when the dispatcher
//   runs into one, it stops being the dispatcher, keeps that method until it
//   ends or awaits, and a new dispatcher runs the other continuations.
// - [AsyncMethodBuilder(typeof(OwnFiberMethodBuilder))] (or <> for a
//   Task<T>) on a method gives it a fiber of its own instead, from its first
//   wait to its end: for one that mostly blocks.
//
// Exceptions: each step of an async method (from its start or an await to
// its next await or its end) runs as a Catch.Run (lib/Catch.cs). A throw in
// a step faults the method's task, as in .NET, after Catch.Run has closed
// what the step opened, rather than ending the program; an
// OperationCanceledException cancels it instead. Awaiting the task, Wait()
// and .Result throw the exception again, so it climbs the awaits until it
// reaches a Catch.Run (around a Wait() in code that isn't async, or
// Catch.RunAsync in code that is) or ends the program, from an async Main.
// Task.Exception is the exception itself: there's no AggregateException.
// An `async void` method that throws ends the program, as in .NET. A task
// that faults and that nothing awaits, waits for or asks about (IsFaulted,
// Exception, Status) is reported on stderr when Main returns or Exit is
// called, where .NET keeps quiet; the exit code stays. ConfigureAwait(
// ConfigureAwaitOptions.SuppressThrowing) awaits without throwing (look at
// IsFaulted and IsCanceled after). ValueTask is in lib/ValueTasks.cs. No
// ContinueWith or SynchronizationContext; ConfigureAwait(bool) changes
// nothing.
//
// Tasks are objects like any other: from the fiber's ObjectAllocator,
// calloc by default (never freed), or an arena's while one is set. A task
// must outlive whoever awaits it, so don't let an arena that made a task
// end before the task's awaiters have finished with it.
//
// Not on WebAssembly, which has no fibers.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace System.Threading.Tasks
{
    // Faults no one has observed yet, reported when Main returns or
    // Environment.Exit ends the program (not by FailFast, nor if the program
    // never ends, as a server's Fiber.Run doesn't): how many,
    // and a message: the first fault's while no other was waiting to be
    // observed (the first of a run of failures is usually the cause). It's
    // copied, not kept: the task and its exception may be in an arena that's
    // gone by then. If that fault is observed later, the report has no
    // message.
    static class UnobservedFaults
    {
        const int MessageCapacity = 200;

        struct Message
        {
            public fixed byte Bytes[MessageCapacity];
        }

        static int s_count;
        static int s_lastNumber;
        // The fault whose message Message holds; 0 once it's been observed.
        static int s_messageNumber;
        static int s_messageLength;
        static Message s_message;
        static int s_installed;
        static unsafe delegate*<void> s_nextAtMainReturn;
        static unsafe delegate*<void> s_nextAtExit;

        internal static int Faulted(string? message)
        {
            if (s_installed == 0 && Interlocked.CompareExchange(ref s_installed, 1, 0) == 0)
                unsafe
                {
                    // SAFETY: static methods.
                    s_nextAtMainReturn = Environment.AddAtMainReturn(&AtMainReturn);
                    s_nextAtExit = Environment.AddAtExit(&AtExit);
                }
            Interlocked.Increment(ref s_count);
            int number = Interlocked.Increment(ref s_lastNumber);
            if (s_messageNumber != 0)
                return number;
            int length = 0;
            if (message != null)
                for (int i = 0; i < message.Length && length < MessageCapacity; i++)
                {
                    char c = message[i];
                    unsafe { s_message.Bytes[length++] = c < 0x80 ? (byte)c : (byte)'?'; } // SAFETY: length < MessageCapacity
                }
            s_messageLength = length;
            s_messageNumber = number;
            return number;
        }

        internal static void Observed(int number)
        {
            Interlocked.Decrement(ref s_count);
            if (s_messageNumber == number)
                s_messageNumber = 0;
        }

        static void AtMainReturn()
        {
            Report();
            unsafe
            {
                // SAFETY: the check this one replaced, a static method.
                if (s_nextAtMainReturn != null)
                    s_nextAtMainReturn();
            }
        }

        static void AtExit()
        {
            Report();
            unsafe
            {
                // SAFETY: the report this one replaced, a static method.
                if (s_nextAtExit != null)
                    s_nextAtExit();
            }
        }

        static void Report()
        {
            int count = s_count;
            if (count > 0)
            {
                Write(count == 1 ? "Unobserved task exception: a task faulted and nothing awaited it"
                    : string.Concat("Unobserved task exceptions: ", count.ToString(), " tasks faulted and nothing awaited them"));
                if (s_messageNumber != 0)
                {
                    Write(count == 1 ? ": " : "; the first: ");
                    unsafe
                    {
                        // SAFETY: s_messageLength bytes of the buffer were written.
                        fixed (byte* bytes = s_message.Bytes)
                            Platform.write(2, bytes, (nuint)s_messageLength);
                    }
                }
                Write("\n");
            }
        }

        // Our own ASCII text, under 128 characters.
        static void Write(string text)
        {
            unsafe
            {
                // SAFETY: text is shorter than the buffer.
                byte* buffer = stackalloc byte[128];
                int length = 0;
                foreach (char c in text)
                    buffer[length++] = (byte)c;
                Platform.write(2, buffer, (nuint)length);
            }
        }
    }

    /// <summary>An operation that completes later: await it, or Wait for it.</summary>
    public partial class Task
    {
        bool _completed;
        bool _canceled;
        // Faulted: what the method threw. Canceled: the
        // OperationCanceledException, if one did the canceling.
        internal Exception? _exception;
        // An async void method's: a fault ends the program.
        internal bool FaultEndsProgram;
        // Faulted and not yet observed (awaited, waited for, or asked about):
        // its number among faults, for the report when Main returns. 0 once
        // observed, or if it never faulted.
        int _faultNumber;
        // Fibers parked in Wait, in order, linked through their records' Next.
        unsafe Fiber.Record* _waiters;
        // Told when this task completes: WhenAll, WhenAny, OnCompleted.
        Observer? _observers;

        // An async method's task, once the method has waited: its boxed state
        // machine. Or, with OwnFiberMethodBuilder, once the method has a
        // fiber: what that fiber waits for before it calls the state machine
        // again.
        internal Continuation? Box;
        internal Task? Pending;
        internal bool HasFiber;

        // A task that its owner uses again and again (PeriodicTimer's, a
        // channel's), handed out only inside a ValueTask, as .NET's
        // IValueTaskSource is. Each use has a Version, which the ValueTask
        // keeps; reading the result (GetResult, AsTask) frees the task for its
        // next use (Owned false). So a loop that awaits one allocates nothing.
        internal bool Reusable;
        internal bool Owned;
        internal int Version;

        // Starts the next use: not completed, a new version, owned until read.
        internal void Reset()
        {
            Observe();
            _completed = false;
            _canceled = false;
            _exception = null;
            Version++;
            Owned = true;
        }

        // A ValueTask over this task belongs to this use of it?
        internal void CheckVersion(int version)
        {
            if (Reusable && version != Version)
                Environment.FailFast("ValueTask: awaited after its operation was reused (a ValueTask from a timer or channel can be awaited only once, as in .NET: use AsTask() to keep it)");
        }

        internal void Release()
        {
            if (Reusable)
                Owned = false;
        }

        internal Task() { }
        internal Task(bool completed) { _completed = completed; }

        /// <summary>Whether the task has finished.</summary>
        public bool IsCompleted => _completed;

        /// <summary>Whether the task has finished, wasn't canceled, and didn't throw.</summary>
        public bool IsCompletedSuccessfully => _completed && !_canceled && _exception == null;

        /// <summary>Whether the task ended with an exception (Exception says which).</summary>
        public bool IsFaulted
        {
            get
            {
                Observe();
                return _completed && !_canceled && _exception != null;
            }
        }

        /// <summary>Whether the task was canceled (it has then also completed).</summary>
        public bool IsCanceled => _canceled;

        /// <summary>
        /// What the task threw, once it has faulted; null otherwise. The
        /// exception itself, not an AggregateException as in .NET.
        /// </summary>
        public Exception? Exception => IsFaulted ? _exception : null;

        /// <summary>RanToCompletion, Faulted, Canceled, or WaitingForActivation while it runs.</summary>
        public TaskStatus Status
        {
            get
            {
                Observe();
                return !_completed ? TaskStatus.WaitingForActivation
                    : _canceled ? TaskStatus.Canceled
                    : _exception != null ? TaskStatus.Faulted
                    : TaskStatus.RanToCompletion;
            }
        }

        /// <summary>A task that has already completed.</summary>
        public static Task CompletedTask { get; } = new Task(true);

        // Marks the task done (or canceled) and wakes whoever waits for it.
        // False if it already was.
        internal bool Complete(bool canceled = false)
        {
            if (_completed)
                return false;
            _completed = true;
            _canceled = canceled;
            unsafe
            {
                // SAFETY: parked fibers' records, alive while parked.
                while (_waiters != null)
                {
                    Fiber.Record* f = _waiters;
                    _waiters = f->Next;
                    Fiber.Wake(f, true);
                }
            }
            Observer? o = _observers;
            _observers = null;
            while (o != null)
            {
                Observer? next = o.Next; // Completed may reuse o.Next
                o.Next = null;
                o.Completed(this);
                o = next;
            }
            return true;
        }

        // Ends the task with what was thrown: faulted, or canceled for an
        // OperationCanceledException (which awaiting throws again). False if
        // it had already completed.
        internal bool Fail(Exception exception)
        {
            if (_completed)
                return false;
            if (FaultEndsProgram)
                Environment.FailFast(string.Concat("Unhandled exception: ", exception.Message));
            _exception = exception;
            if (exception is not OperationCanceledException)
                _faultNumber = UnobservedFaults.Faulted(exception.Message);
            return Complete(exception is OperationCanceledException);
        }

        // Somebody looked at how the task ended: its fault, if any, isn't
        // reported when Main returns.
        internal void Observe()
        {
            if (_faultNumber != 0)
            {
                UnobservedFaults.Observed(_faultNumber);
                _faultNumber = 0;
            }
        }

        // Ends the task as done ended: canceled, faulted, or completed.
        internal bool CompleteAs(Task done)
        {
            if (done._exception != null)
                return Fail(done._exception);
            return Complete(done._canceled);
        }

        // What awaiting the task throws, or null: its exception, or for a
        // cancel without one, a TaskCanceledException (made here, so only
        // when it's thrown).
        internal Exception? Failure
        {
            get
            {
                Observe();
                return _exception ?? (_canceled ? new TaskCanceledException() : null);
            }
        }

        internal void AddObserver(Observer observer)
        {
            observer.Next = _observers;
            _observers = observer;
        }

        /// <summary>
        /// Waits for the task to complete. In a fiber, the fiber waits and the
        /// others run; outside one, the fibers run until the task is done.
        /// Throws what the task threw, or a TaskCanceledException if it was
        /// canceled: inside a Catch.Run that comes back there, and outside
        /// one it ends the program.
        /// </summary>
        public void Wait()
        {
            WaitDone();
            ThrowIfFailed();
        }

        // Throws as awaiting the task does in .NET: what it threw, or for a
        // cancel, the OperationCanceledException that did it or a
        // TaskCanceledException.
        internal void ThrowIfFailed()
        {
            Observe();
            if (_exception != null || _canceled)
                throw Failure!;
        }

        // Waits until the task has completed or been canceled.
        internal void WaitDone()
        {
            if (_completed)
                return;
            unsafe
            {
                // SAFETY: the current fiber's record, which stays alive while
                // it's parked on our list.
                Fiber.Record* current = Fiber.CurrentRecord;
                if (current == null)
                {
                    Fiber.RunUntil(this);
                    return;
                }
                current->Next = null;
                if (_waiters == null)
                {
                    _waiters = current;
                }
                else
                {
                    Fiber.Record* last = _waiters;
                    while (last->Next != null)
                        last = last->Next;
                    last->Next = current;
                }
            }
            Fiber.Park("Task.Wait: not in a fiber");
        }

        public TaskAwaiter GetAwaiter() => new TaskAwaiter(this);

        /// <summary>Returns something to await, as in .NET. The argument changes nothing: there's no context to return to.</summary>
        public ConfiguredTaskAwaitable ConfigureAwait(bool continueOnCapturedContext) => new ConfiguredTaskAwaitable(this, ConfigureAwaitOptions.None);

        /// <summary>
        /// Returns something to await. SuppressThrowing: awaiting a canceled or
        /// faulted task returns rather than throwing (look at IsCanceled and
        /// IsFaulted after). ForceYielding: the await lets the others run even if the
        /// task is done.
        /// </summary>
        public ConfiguredTaskAwaitable ConfigureAwait(ConfigureAwaitOptions options) => new ConfiguredTaskAwaitable(this, options);

        /// <summary>
        /// A task that completes when this one does, or is canceled when
        /// <paramref name="cancellationToken"/> is, whichever comes first. This
        /// task carries on either way.
        /// </summary>
        public Task WaitAsync(CancellationToken cancellationToken)
        {
            if (_completed || !cancellationToken.CanBeCanceled)
                return this;
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled(cancellationToken);
            var wait = new WaitAsyncTask<VoidResult>(this);
            wait.Start(cancellationToken);
            return wait;
        }

        /// <summary>A task canceled already.</summary>
        public static Task FromCanceled(CancellationToken cancellationToken)
        {
            Task task = new Task();
            task.Complete(true);
            return task;
        }

        /// <summary>A task canceled already.</summary>
        public static Task<TResult> FromCanceled<TResult>(CancellationToken cancellationToken)
        {
            Task<TResult> task = new Task<TResult>();
            task.Complete(true);
            return task;
        }

        /// <summary>A task with <paramref name="result"/>, already completed.</summary>
        public static Task<TResult> FromResult<TResult>(TResult result) => new Task<TResult>(result);

        /// <summary>A task that completes after <paramref name="millisecondsDelay"/>. 0 or less: completed now.</summary>
        public static Task Delay(int millisecondsDelay)
        {
            if (millisecondsDelay <= 0)
                return CompletedTask;
            return DelayTimer.Add(millisecondsDelay);
        }

        /// <summary>A task that completes after <paramref name="delay"/> (rounded up to milliseconds).</summary>
        public static Task Delay(TimeSpan delay) => Delay(DelayTimer.Milliseconds(delay));

        /// <summary>
        /// A task that completes after <paramref name="millisecondsDelay"/>, or
        /// is canceled when <paramref name="cancellationToken"/> is, whichever
        /// is first. -1 (Timeout.Infinite): only when canceled.
        /// </summary>
        public static Task Delay(int millisecondsDelay, CancellationToken cancellationToken)
        {
            if (millisecondsDelay < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled(cancellationToken);
            if (millisecondsDelay == 0)
                return CompletedTask;
            if (!cancellationToken.CanBeCanceled)
                return millisecondsDelay == -1 ? new Task() : DelayTimer.Add(millisecondsDelay);
            DelayTimer task = millisecondsDelay == -1 ? new DelayTimer() : DelayTimer.Add(millisecondsDelay);
            task.Registration = cancellationToken.Register(static t => DelayTimer.Cancel((DelayTimer)t!), task);
            return task;
        }

        /// <summary>Delay(milliseconds, cancellationToken), with the delay rounded up to milliseconds.</summary>
        public static Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Delay(DelayTimer.Milliseconds(delay), cancellationToken);

        /// <summary>A task that completes after <paramref name="delay"/> by <paramref name="timeProvider"/>'s clock.</summary>
        public static Task Delay(TimeSpan delay, TimeProvider timeProvider) => Delay(delay, timeProvider, default);

        /// <summary>
        /// A task that completes after <paramref name="delay"/> by
        /// <paramref name="timeProvider"/>'s clock (a timer it creates), or is
        /// canceled when <paramref name="cancellationToken"/> is.
        /// TimeProvider.System is the same as Delay(delay, cancellationToken).
        /// </summary>
        public static Task Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            if (timeProvider == null)
                throw new ArgumentNullException(nameof(timeProvider));
            if (timeProvider == TimeProvider.System)
                return Delay(delay, cancellationToken);
            if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delay));
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled(cancellationToken);
            if (delay == TimeSpan.Zero)
                return CompletedTask;
            var task = new ProviderDelay();
            task.Start(delay, timeProvider, cancellationToken);
            return task;
        }

        /// <summary>Runs <paramref name="action"/> on a new fiber.</summary>
        public static Task Run(Action action) => Run(action, default);

        /// <summary>
        /// Runs <paramref name="action"/> on a new fiber, unless
        /// <paramref name="cancellationToken"/> is canceled before the fiber
        /// starts: then the task is canceled. A running action isn't stopped.
        /// </summary>
        public static Task Run(Action action, CancellationToken cancellationToken)
        {
            Task task = new Task();
            Fiber.Spawn(new RunState<Action, Task>(action, task, cancellationToken), static r =>
            {
                if (r.Token.IsCancellationRequested)
                {
                    r.Task.Complete(true);
                    return;
                }
                Exception? thrown = Catch.Run(ref r, static (ref RunState<Action, Task> s) => s.Body());
                if (thrown != null)
                    r.Task.Fail(thrown);
                else
                    r.Task.Complete();
            });
            return task;
        }

        /// <summary>Runs <paramref name="function"/> on a new fiber: the task has its result.</summary>
        public static Task<TResult> Run<TResult>(Func<TResult> function) => Run(function, default);

        /// <summary>Run, canceled instead if <paramref name="cancellationToken"/> is before the fiber starts.</summary>
        public static Task<TResult> Run<TResult>(Func<TResult> function, CancellationToken cancellationToken)
        {
            Task<TResult> task = new Task<TResult>();
            Fiber.Spawn(new RunState<Func<TResult>, Task<TResult>>(function, task, cancellationToken), static r =>
            {
                if (r.Token.IsCancellationRequested)
                {
                    r.Task.Complete(true);
                    return;
                }
                Exception? thrown = Catch.Run(r.Body, out TResult result);
                if (thrown != null)
                    r.Task.Fail(thrown);
                else
                    r.Task.TrySetResult(result);
            });
            return task;
        }

        /// <summary>Runs <paramref name="function"/> (an async lambda) on a new fiber, and completes (or is canceled) when its task does.</summary>
        public static Task Run(Func<Task> function) => Run(function, default);

        /// <summary>Run, canceled instead if <paramref name="cancellationToken"/> is before the fiber starts.</summary>
        public static Task Run(Func<Task> function, CancellationToken cancellationToken)
        {
            Task task = new Task();
            Fiber.Spawn(new RunState<Func<Task>, Task>(function, task, cancellationToken), static r =>
            {
                if (r.Token.IsCancellationRequested)
                {
                    r.Task.Complete(true);
                    return;
                }
                // A lambda that isn't async can throw here, before it has a task.
                Exception? thrown = Catch.Run(r.Body, out Task inner);
                if (thrown != null)
                {
                    r.Task.Fail(thrown);
                    return;
                }
                inner.WaitDone();
                r.Task.CompleteAs(inner);
            });
            return task;
        }

        /// <summary>Runs <paramref name="function"/> (an async lambda) on a new fiber: the task has its task's result.</summary>
        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function) => Run(function, default);

        /// <summary>Run, canceled instead if <paramref name="cancellationToken"/> is before the fiber starts.</summary>
        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function, CancellationToken cancellationToken)
        {
            Task<TResult> task = new Task<TResult>();
            Fiber.Spawn(new RunState<Func<Task<TResult>>, Task<TResult>>(function, task, cancellationToken), static r =>
            {
                if (r.Token.IsCancellationRequested)
                {
                    r.Task.Complete(true);
                    return;
                }
                Exception? thrown = Catch.Run(r.Body, out Task<TResult> inner);
                if (thrown != null)
                {
                    r.Task.Fail(thrown);
                    return;
                }
                inner.WaitDone();
                if (inner.IsCompletedSuccessfully)
                    r.Task.TrySetResult(inner._result);
                else
                    r.Task.CompleteAs(inner);
            });
            return task;
        }

        readonly struct RunState<TBody, TTask>(TBody body, TTask task, CancellationToken token)
        {
            public readonly TBody Body = body;
            public readonly TTask Task = task;
            public readonly CancellationToken Token = token;
        }

        /// <summary>Awaiting it lets the fibers that are ready run first.</summary>
        public static YieldAwaitable Yield() => default;

        // What YieldAwaiter hands the builder: the fiber yields instead of
        // waiting. Never completes.
        internal static readonly Task YieldMarker = new Task();

        /// <summary>A task that completes when all of <paramref name="tasks"/> have.</summary>
        public static Task WhenAll(params Task[] tasks)
        {
            WhenAllTask all = new WhenAllTask();
            all.Watch(tasks);
            return all;
        }

        /// <summary>A task that completes when all of <paramref name="tasks"/> have, with their results in order.</summary>
        public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks)
        {
            WhenAllTask<TResult> all = new WhenAllTask<TResult>(tasks);
            all.Watch(tasks);
            return all;
        }

        /// <summary>A task that completes when the first of <paramref name="tasks"/> does: its result is that task.</summary>
        public static Task<Task> WhenAny(params Task[] tasks)
        {
            WhenAnyTask<Task> any = new WhenAnyTask<Task>();
            any.Watch(tasks);
            return any;
        }

        /// <summary>A task that completes when the first of <paramref name="tasks"/> does: its result is that task.</summary>
        public static Task<Task<TResult>> WhenAny<TResult>(params Task<TResult>[] tasks)
        {
            WhenAnyTask<Task<TResult>> any = new WhenAnyTask<Task<TResult>>();
            any.Watch(tasks);
            return any;
        }

        /// <summary>Waits for all of <paramref name="tasks"/>. Ends the program if one was canceled.</summary>
        public static void WaitAll(params Task[] tasks)
        {
            foreach (Task t in tasks)
                t.Wait();
        }
    }

    /// <summary>A Task with a result.</summary>
    public class Task<TResult> : Task
    {
        internal TResult _result = default!;

        internal Task() { }
        internal Task(TResult result) : base(true) { _result = result; }

        /// <summary>The result, waiting for it first as Wait does, which throws if the task faulted or was canceled.</summary>
        public TResult Result
        {
            get
            {
                Wait();
                return _result;
            }
        }

        /// <summary>A task that completes with this one's result, or is canceled when <paramref name="cancellationToken"/> is, whichever is first.</summary>
        public new Task<TResult> WaitAsync(CancellationToken cancellationToken)
        {
            if (IsCompleted || !cancellationToken.CanBeCanceled)
                return this;
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled<TResult>(cancellationToken);
            var wait = new WaitAsyncTask<TResult>(this);
            wait.Start(cancellationToken);
            return wait;
        }

        internal bool TrySetResult(TResult result)
        {
            if (IsCompleted)
                return false;
            _result = result;
            return Complete();
        }

        public new TaskAwaiter<TResult> GetAwaiter() => new TaskAwaiter<TResult>(this);

        /// <summary>Returns something to await, as in .NET. The argument changes nothing.</summary>
        public new ConfiguredTaskAwaitable<TResult> ConfigureAwait(bool continueOnCapturedContext) => new ConfiguredTaskAwaitable<TResult>(this, ConfigureAwaitOptions.None);

        /// <summary>
        /// Returns something to await. ForceYielding: the await lets the others
        /// run even if the task is done. SuppressThrowing isn't allowed (there
        /// would be no result to return), as in .NET: await the task as a Task.
        /// </summary>
        public new ConfiguredTaskAwaitable<TResult> ConfigureAwait(ConfigureAwaitOptions options)
        {
            if ((options & ConfigureAwaitOptions.SuppressThrowing) != 0)
                throw new ArgumentOutOfRangeException(nameof(options), "ConfigureAwaitOptions.SuppressThrowing isn't supported by Task<TResult>: cast the task to Task first.");
            return new ConfiguredTaskAwaitable<TResult>(this, options);
        }
    }

    /// <summary>A task completed by hand: SetResult.</summary>
    public class TaskCompletionSource
    {
        readonly Task _task = new Task();

        // The thread that made it: only that one may complete it.
        readonly nint _thread = OwnThread.Current;

        public Task Task => _task;

        /// <summary>Completes the task. Stops the program if it already was.</summary>
        public void SetResult()
        {
            OwnThread.Check(_thread);
            if (!_task.Complete())
                Environment.FailFast("TaskCompletionSource.SetResult: the task has already completed");
        }

        /// <summary>Completes the task. False if it already was.</summary>
        public bool TrySetResult() => OwnThread.Check(_thread) && _task.Complete();

        /// <summary>Cancels the task. Stops the program if it had already completed.</summary>
        public void SetCanceled() => SetCanceled(default);

        /// <summary>Cancels the task. Stops the program if it had already completed.</summary>
        public void SetCanceled(CancellationToken cancellationToken)
        {
            OwnThread.Check(_thread);
            if (!_task.Complete(true))
                Environment.FailFast("TaskCompletionSource.SetCanceled: the task has already completed");
        }

        /// <summary>Cancels the task. False if it had already completed.</summary>
        public bool TrySetCanceled() => OwnThread.Check(_thread) && _task.Complete(true);

        /// <summary>Cancels the task. False if it had already completed.</summary>
        public bool TrySetCanceled(CancellationToken cancellationToken) => OwnThread.Check(_thread) && _task.Complete(true);

        /// <summary>Faults the task with <paramref name="exception"/> (an OperationCanceledException cancels it). Stops the program if it had already completed.</summary>
        public void SetException(Exception exception)
        {
            if (!_task.Fail(exception ?? throw new ArgumentNullException(nameof(exception))))
                Environment.FailFast("TaskCompletionSource.SetException: the task has already completed");
        }

        /// <summary>Faults the task. False if it had already completed.</summary>
        public bool TrySetException(Exception exception) => _task.Fail(exception ?? throw new ArgumentNullException(nameof(exception)));
    }

    /// <summary>A task with a result, completed by hand: SetResult.</summary>
    public class TaskCompletionSource<TResult>
    {
        readonly Task<TResult> _task = new Task<TResult>();

        // The thread that made it: only that one may complete it.
        readonly nint _thread = OwnThread.Current;

        public Task<TResult> Task => _task;

        /// <summary>Completes the task with <paramref name="result"/>. Stops the program if it already was.</summary>
        public void SetResult(TResult result)
        {
            OwnThread.Check(_thread);
            if (!_task.TrySetResult(result))
                Environment.FailFast("TaskCompletionSource.SetResult: the task has already completed");
        }

        /// <summary>Completes the task with <paramref name="result"/>. False if it already was.</summary>
        public bool TrySetResult(TResult result) => OwnThread.Check(_thread) && _task.TrySetResult(result);

        /// <summary>Cancels the task. Stops the program if it had already completed.</summary>
        public void SetCanceled() => SetCanceled(default);

        /// <summary>Cancels the task. Stops the program if it had already completed.</summary>
        public void SetCanceled(CancellationToken cancellationToken)
        {
            OwnThread.Check(_thread);
            if (!_task.Complete(true))
                Environment.FailFast("TaskCompletionSource.SetCanceled: the task has already completed");
        }

        /// <summary>Cancels the task. False if it had already completed.</summary>
        public bool TrySetCanceled() => OwnThread.Check(_thread) && _task.Complete(true);

        /// <summary>Cancels the task. False if it had already completed.</summary>
        public bool TrySetCanceled(CancellationToken cancellationToken) => OwnThread.Check(_thread) && _task.Complete(true);

        /// <summary>Faults the task with <paramref name="exception"/> (an OperationCanceledException cancels it). Stops the program if it had already completed.</summary>
        public void SetException(Exception exception)
        {
            if (!_task.Fail(exception ?? throw new ArgumentNullException(nameof(exception))))
                Environment.FailFast("TaskCompletionSource.SetException: the task has already completed");
        }

        /// <summary>Faults the task. False if it had already completed.</summary>
        public bool TrySetException(Exception exception) => _task.Fail(exception ?? throw new ArgumentNullException(nameof(exception)));
    }


    // Which thread made a TaskCompletionSource. Tasks belong to their thread
    // and nothing in them is locked, so completing one from another thread
    // (a C library's callback thread, say) would corrupt it: stopped here.
    static class OwnThread
    {
        [ThreadStatic] static byte t_marker;

        // This thread, by an address only it has.
        internal static nint Current
        {
#if ZERO_DOTNET
            get => Fiber.ThreadIdentity; // real .NET: each fiber is a thread, and they count as one
#else
            get { unsafe { return (nint)Unsafe.AsPointer(ref t_marker); } } // SAFETY: only compared, never read through
#endif
        }

        internal static bool Check(nint owner)
        {
            if (owner != Current)
                Environment.FailFast("TaskCompletionSource: completed from another thread than the one that made it; use ThreadCompletionSource (lib/ThreadCompletion.cs), which any thread may complete");
            return true;
        }
    }

    /// <summary>What a task is doing (only the states a task here can be in).</summary>
    public enum TaskStatus
    {
        Created,
        WaitingForActivation,
        WaitingToRun,
        Running,
        WaitingForChildrenToComplete,
        RanToCompletion,
        Canceled,
        Faulted,
    }

    /// <summary>How ConfigureAwait awaits (.NET 8's flags).</summary>
    [Flags]
    public enum ConfigureAwaitOptions
    {
        /// <summary>A plain await.</summary>
        None = 0,
        /// <summary>Changes nothing here: there's no context to return to.</summary>
        ContinueOnCapturedContext = 1,
        /// <summary>Awaiting a canceled task returns instead of ending the program.</summary>
        SuppressThrowing = 2,
        /// <summary>The await lets the others run first even if the task is done.</summary>
        ForceYielding = 4,
    }

    // Task.Delay's tasks, in the order they're due, and the one fiber per
    // thread that completes them: it sleeps until the earliest is due, and
    // ends when there are none left, so Fiber.Run can return. A canceled
    // delay leaves the list, so it doesn't keep the fiber (or Run) waiting.
    // Owners that wait again and again (CancelAfter, PeriodicTimer) keep one
    // subclass, which overrides Due rather than completing, and Schedule it
    // each time: no allocation per wait.
    class DelayTimer : Task
    {
        long _due;
        DelayTimer? _next, _previous;
        bool _queued;
        // Task.Delay with a token: removed once the delay is over.
        internal CancellationTokenRegistration Registration;

        [ThreadStatic] static DelayTimer? t_first, t_last;
        [ThreadStatic] static bool t_fiber;
        [ThreadStatic] static unsafe Fiber.Record* t_record;

        internal static int Milliseconds(TimeSpan delay)
        {
            if (delay.Ticks == Timeout.InfiniteTimeSpan.Ticks)
                return -1;
            if (delay.Ticks < 0)
                throw new ArgumentOutOfRangeException(nameof(delay));
            long ms = (delay.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
            if (ms > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(delay));
            return (int)ms;
        }

        internal static DelayTimer Add(int milliseconds)
        {
            DelayTimer task = new DelayTimer();
            task.Schedule(milliseconds);
            return task;
        }

        // What the timer fiber does when the delay is over: completes the task.
        internal virtual void Due() => Complete();

        // Puts this delay on the list, due in `milliseconds`. Not while it's on it.
        internal void Schedule(int milliseconds)
        {
            DelayTimer task = this;
            task._due = Clock.Monotonic + milliseconds * 1_000_000L;
            task._queued = true;
            if (t_last != null && task._due >= t_last._due)
            {
                // The usual case, many delays of one length: due last.
                task._previous = t_last;
                t_last._next = task;
                t_last = task;
                return;
            }
            DelayTimer? before = null, after = t_first;
            while (after != null && after._due <= task._due)
            {
                before = after;
                after = after._next;
            }
            task._previous = before;
            task._next = after;
            if (before == null)
                t_first = task;
            else
                before._next = task;
            if (after == null)
                t_last = task;
            else
                after._previous = task;
            if (before == null)
            {
                // Due sooner than what the fiber sleeps for, or the first.
                if (!t_fiber)
                {
                    t_fiber = true;
                    Fiber.Spawn(0, static _ => Run(), Fiber.MinStackSize);
                }
                else
                {
                    WakeFiber();
                }
            }
        }

        // Takes a delay off the list without completing it.
        internal static void Remove(DelayTimer task)
        {
            if (!task._queued)
                return;
            bool first = task == t_first;
            Unlink(task);
            if (first)
                WakeFiber(); // to sleep until the new first, or end
        }

        // Its token was canceled.
        internal static void Cancel(DelayTimer task)
        {
            Remove(task);
            task.Complete(true);
        }

        static void Unlink(DelayTimer task)
        {
            if (task._previous != null)
                task._previous._next = task._next;
            else
                t_first = task._next;
            if (task._next != null)
                task._next._previous = task._previous;
            else
                t_last = task._previous;
            task._next = task._previous = null;
            task._queued = false;
        }

        static void WakeFiber()
        {
            unsafe
            {
                // SAFETY: the timer fiber's record, alive while t_fiber.
                if (t_record != null && t_record != Fiber.CurrentRecord)
                    Fiber.WakeEarly(t_record);
            }
        }

        static void Run()
        {
            unsafe { t_record = Fiber.CurrentRecord; } // SAFETY: this fiber's record
            while (t_first != null)
            {
                long now = Clock.Monotonic;
                while (t_first != null && t_first._due <= now)
                {
                    DelayTimer due = t_first;
                    Unlink(due);
                    due.Registration.Dispose();
                    due.Due();
                }
                if (t_first != null)
                    Fiber.Sleep((t_first._due - now + 999_999) / 1_000_000);
            }
            unsafe { t_record = null; } // SAFETY: a plain store
            t_fiber = false;
        }
    }

    // Task.Delay on a TimeProvider's timer, which it disposes once done.
    sealed class ProviderDelay : Task
    {
        ITimer? _timer;
        CancellationTokenRegistration _registration;

        internal void Start(TimeSpan delay, TimeProvider provider, CancellationToken token)
        {
            if (token.CanBeCanceled)
                _registration = token.Register(static d => ((ProviderDelay)d!).Finish(true), this);
            if (delay != Timeout.InfiniteTimeSpan)
                _timer = provider.CreateTimer(static d => ((ProviderDelay)d!).Finish(false), this, delay, Timeout.InfiniteTimeSpan);
            if (IsCompleted)
                _timer?.Dispose(); // the timer fired, or was canceled, as it was made
        }

        void Finish(bool canceled)
        {
            if (IsCompleted)
                return;
            _timer?.Dispose();
            _registration.Dispose();
            Complete(canceled);
        }
    }

    // No result: for WaitAsyncTask on a plain Task.
    struct VoidResult { }

    // Task.WaitAsync(token): completes as the task does, or is canceled with
    // the token. Task<VoidResult> stands in for a plain Task.
    sealed class WaitAsyncTask<TResult>(Task task) : Task<TResult>, IObserved
    {
        CancellationTokenRegistration _registration;

        internal void Start(CancellationToken token)
        {
            task.AddObserver(new Forward(this));
            _registration = token.Register(static w => ((WaitAsyncTask<TResult>)w!).Complete(true), this);
        }

        public void Completed(Task done)
        {
            _registration.Dispose();
            if (!done.IsCompletedSuccessfully)
                CompleteAs(done);
            else if (done is Task<TResult> withResult)
                TrySetResult(withResult._result);
            else
                Complete();
        }
    }

    // Told when a task completes. A list per task, linked through Next, so a
    // task can be in several WhenAll/WhenAny at once (each its own Observer).
    internal abstract class Observer
    {
        internal Observer? Next;
        internal abstract void Completed(Task task);
    }

    sealed class Forward(IObserved target) : Observer
    {
        internal override void Completed(Task task) => target.Completed(task);
    }

    interface IObserved
    {
        void Completed(Task task);
    }

    sealed class ActionObserver(Action action) : Observer
    {
        internal override void Completed(Task task) => action();
    }

    // WhenAll, once all its tasks have completed: faulted with the first
    // exception if any faulted (.NET gathers them all in an
    // AggregateException), else canceled if any was.
    sealed class WhenAllTask : Task, IObserved
    {
        int _left;
        bool _canceled;
        Exception? _first;

        internal void Watch(Task[] tasks)
        {
            foreach (Task t in tasks)
                if (!t.IsCompleted)
                {
                    _left++;
                    t.AddObserver(new Forward(this));
                }
                else
                {
                    Note(t);
                }
            if (_left == 0)
                Finish();
        }

        public void Completed(Task task)
        {
            Note(task);
            if (--_left == 0)
                Finish();
        }

        void Note(Task task)
        {
            _canceled |= task.IsCanceled;
            if (task.IsFaulted)
                _first ??= task._exception;
        }

        void Finish()
        {
            if (_first != null)
                Fail(_first);
            else
                Complete(_canceled);
        }
    }

    sealed class WhenAllTask<TResult>(Task<TResult>[] tasks) : Task<TResult[]>, IObserved
    {
        int _left;
        bool _canceled;
        Exception? _first;

        internal void Watch(Task<TResult>[] watched)
        {
            foreach (Task t in watched)
                if (!t.IsCompleted)
                {
                    _left++;
                    t.AddObserver(new Forward(this));
                }
                else
                {
                    Note(t);
                }
            if (_left == 0)
                Finish();
        }

        public void Completed(Task task)
        {
            Note(task);
            if (--_left == 0)
                Finish();
        }

        void Note(Task task)
        {
            _canceled |= task.IsCanceled;
            if (task.IsFaulted)
                _first ??= task._exception;
        }

        void Finish()
        {
            if (_first != null)
            {
                Fail(_first);
                return;
            }
            if (_canceled)
            {
                Complete(true);
                return;
            }
            TResult[] results = new TResult[tasks.Length];
            for (int i = 0; i < results.Length; i++)
                results[i] = tasks[i]._result;
            TrySetResult(results);
        }
    }

    sealed class WhenAnyTask<TTask> : Task<TTask>, IObserved where TTask : Task
    {
        internal void Watch(TTask[] tasks)
        {
            if (tasks.Length == 0)
                Environment.FailFast("Task.WhenAny: no tasks");
            foreach (TTask t in tasks)
                if (t.IsCompleted)
                {
                    TrySetResult(t);
                    return;
                }
            foreach (TTask t in tasks)
                t.AddObserver(new Forward(this));
        }

        // The first to complete wins; later ones find the task done.
        public void Completed(Task task)
        {
            unsafe { TrySetResult(System.Runtime.CompilerServices.Unsafe.As<TTask>(task)); } // SAFETY: only tasks of this type are given this observer
        }
    }
}

namespace System.Runtime.CompilerServices
{
    using System.Threading.Tasks;

    public interface IAsyncStateMachine
    {
        void MoveNext();
        void SetStateMachine(IAsyncStateMachine stateMachine);
    }

    public interface INotifyCompletion
    {
        void OnCompleted(Action continuation);
    }

    public interface ICriticalNotifyCompletion : INotifyCompletion
    {
        void UnsafeOnCompleted(Action continuation);
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class AsyncStateMachineAttribute(Type stateMachineType) : Attribute
    {
        public Type StateMachineType { get; } = stateMachineType;
    }

    // Marks the awaiters of this file: a struct whose one reference field is
    // the Task to wait for (the runtime lays out reference fields first). The
    // builders read it (AsyncMethods.Target) rather than register a
    // continuation.
    internal interface IFiberAwaiter { }

    // An awaiter with more fields than its task (ValueTaskAwaiter<T>): the
    // builders ask it, through a box, for the task to wait for.
    internal interface IWaitTarget
    {
        Task WaitTarget { get; }
    }

    public readonly struct TaskAwaiter : ICriticalNotifyCompletion, IFiberAwaiter
    {
        internal readonly Task m_task;

        internal TaskAwaiter(Task task) { m_task = task; }

        public bool IsCompleted => m_task.IsCompleted;
        public void GetResult() => m_task.Wait();
        public void OnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
        public void UnsafeOnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
    }

    public readonly struct TaskAwaiter<TResult> : ICriticalNotifyCompletion, IFiberAwaiter
    {
        internal readonly Task<TResult> m_task;

        internal TaskAwaiter(Task<TResult> task) { m_task = task; }

        public bool IsCompleted => m_task.IsCompleted;
        public TResult GetResult() => m_task.Result;
        public void OnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
        public void UnsafeOnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
    }

    public readonly struct ConfiguredTaskAwaitable
    {
        readonly Task _task;
        readonly ConfigureAwaitOptions _options;

        internal ConfiguredTaskAwaitable(Task task, ConfigureAwaitOptions options)
        {
            _task = task;
            _options = options;
        }

        public ConfiguredTaskAwaiter GetAwaiter() => new ConfiguredTaskAwaiter(_task, _options);

        public readonly struct ConfiguredTaskAwaiter : ICriticalNotifyCompletion, IFiberAwaiter
        {
            internal readonly Task m_task;
            readonly ConfigureAwaitOptions _options;

            internal ConfiguredTaskAwaiter(Task task, ConfigureAwaitOptions options)
            {
                m_task = task;
                _options = options;
            }

            public bool IsCompleted => m_task.IsCompleted && (_options & ConfigureAwaitOptions.ForceYielding) == 0;

            public void GetResult()
            {
                m_task.WaitDone();
                if ((_options & ConfigureAwaitOptions.SuppressThrowing) == 0)
                    m_task.ThrowIfFailed();
                else
                    m_task.Observe(); // ignoring the fault is a choice made
            }

            public void OnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
            public void UnsafeOnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
        }
    }

    public readonly struct ConfiguredTaskAwaitable<TResult>
    {
        readonly Task<TResult> _task;
        readonly ConfigureAwaitOptions _options;

        internal ConfiguredTaskAwaitable(Task<TResult> task, ConfigureAwaitOptions options)
        {
            _task = task;
            _options = options;
        }

        public ConfiguredTaskAwaiter GetAwaiter() => new ConfiguredTaskAwaiter(_task, _options);

        public readonly struct ConfiguredTaskAwaiter : ICriticalNotifyCompletion, IFiberAwaiter
        {
            internal readonly Task<TResult> m_task;
            readonly ConfigureAwaitOptions _options;

            internal ConfiguredTaskAwaiter(Task<TResult> task, ConfigureAwaitOptions options)
            {
                m_task = task;
                _options = options;
            }

            public bool IsCompleted => m_task.IsCompleted && (_options & ConfigureAwaitOptions.ForceYielding) == 0;
            public TResult GetResult() => m_task.Result;
            public void OnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
            public void UnsafeOnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task, continuation);
        }
    }

    /// <summary>Runs `async Task` methods (the C# compiler calls it).</summary>
    public struct AsyncTaskMethodBuilder
    {
        Task? _task;

        public static AsyncTaskMethodBuilder Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // The first step, on the caller's stack: a throw faults the task.
            Exception? thrown = Catch.RunStep(ref stateMachine);
            if (thrown != null)
                SetException(thrown);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        public Task Task => _task ??= new Task();

        // A method that ends without having waited gets the shared CompletedTask.
        public void SetResult()
        {
            if (_task == null)
                _task = Task.CompletedTask;
            else
                _task.Complete();
        }

        public void SetException(Exception exception) => (_task ??= new Task()).Fail(exception);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(Task, ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(Task, ref awaiter, ref stateMachine);
    }

    /// <summary>Runs `async Task&lt;T&gt;` methods (the C# compiler calls it).</summary>
    public struct AsyncTaskMethodBuilder<TResult>
    {
        Task<TResult>? _task;

        public static AsyncTaskMethodBuilder<TResult> Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // The first step, on the caller's stack: a throw faults the task.
            Exception? thrown = Catch.RunStep(ref stateMachine);
            if (thrown != null)
                SetException(thrown);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        public Task<TResult> Task => _task ??= new Task<TResult>();

        public void SetResult(TResult result)
        {
            if (_task == null)
                _task = new Task<TResult>(result);
            else
                _task.TrySetResult(result);
        }

        public void SetException(Exception exception) => (_task ??= new Task<TResult>()).Fail(exception);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(Task, ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(Task, ref awaiter, ref stateMachine);
    }

    /// <summary>Runs `async void` methods (the C# compiler calls it). Nothing can wait for one.</summary>
    public struct AsyncVoidMethodBuilder
    {
        Task? _task;

        public static AsyncVoidMethodBuilder Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // The first step, on the caller's stack: a throw faults the task.
            Exception? thrown = Catch.RunStep(ref stateMachine);
            if (thrown != null)
                SetException(thrown);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        public void SetResult() => _task?.Complete();

        // Nothing can await an async void method, so a throw in one ends the
        // program, as in .NET.
        public void SetException(Exception exception)
            => Environment.FailFast(string.Concat("Unhandled exception: ", exception.Message));

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(_task ??= new Task { FaultEndsProgram = true }, ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(_task ??= new Task { FaultEndsProgram = true }, ref awaiter, ref stateMachine);
    }

    /// <summary>What Task.Yield returns.</summary>
    public readonly struct YieldAwaitable
    {
        public YieldAwaiter GetAwaiter() => new YieldAwaiter(Task.YieldMarker);

        public readonly struct YieldAwaiter : ICriticalNotifyCompletion, IFiberAwaiter
        {
            readonly Task _marker; // first, as IFiberAwaiter needs

            internal YieldAwaiter(Task marker) { _marker = marker; }

            public bool IsCompleted => false;
            public void GetResult() { }
            public void OnCompleted(Action continuation) => Fiber.Spawn(continuation, static c => { Fiber.Yield(); c(); });
            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
        }
    }

    /// <summary>
    /// Put [AsyncMethodBuilder(typeof(OwnFiberMethodBuilder))] on an async
    /// method (or OwnFiberMethodBuilder&lt;&gt; on one returning Task&lt;T&gt;) for
    /// the compiler to use another builder for it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Delegate | AttributeTargets.Enum | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class AsyncMethodBuilderAttribute(Type builderType) : Attribute
    {
        public Type BuilderType { get; } = builderType;
    }

    /// <summary>
    /// Runs an `async Task` method on a fiber of its own from its first wait
    /// to its end, rather than as continuations on the dispatcher:
    /// [AsyncMethodBuilder(typeof(OwnFiberMethodBuilder))]. For a method that
    /// blocks (channels, Fiber.Sleep) more than it awaits. The fiber has
    /// Fiber.DefaultStackSize.
    /// </summary>
    public struct OwnFiberMethodBuilder
    {
        Task? _task;

        public static OwnFiberMethodBuilder Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // The first step, on the caller's stack: a throw faults the task.
            Exception? thrown = Catch.RunStep(ref stateMachine);
            if (thrown != null)
                SetException(thrown);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        public Task Task => _task ??= new Task();

        public void SetResult()
        {
            if (_task == null)
                _task = Task.CompletedTask;
            else
                _task.Complete();
        }

        public void SetException(Exception exception) => (_task ??= new Task()).Fail(exception);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.SuspendOnFiber(Task, ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.SuspendOnFiber(Task, ref awaiter, ref stateMachine);
    }

    /// <summary>OwnFiberMethodBuilder for an `async Task&lt;T&gt;` method.</summary>
    public struct OwnFiberMethodBuilder<TResult>
    {
        Task<TResult>? _task;

        public static OwnFiberMethodBuilder<TResult> Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // The first step, on the caller's stack: a throw faults the task.
            Exception? thrown = Catch.RunStep(ref stateMachine);
            if (thrown != null)
                SetException(thrown);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        public Task<TResult> Task => _task ??= new Task<TResult>();

        public void SetResult(TResult result)
        {
            if (_task == null)
                _task = new Task<TResult>(result);
            else
                _task.TrySetResult(result);
        }

        public void SetException(Exception exception) => (_task ??= new Task<TResult>()).Fail(exception);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.SuspendOnFiber(Task, ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.SuspendOnFiber(Task, ref awaiter, ref stateMachine);
    }

    // A waiting async method's state machine, boxed: told when what it awaits
    // completes, it joins the dispatcher's queue.
    internal abstract class Continuation : Observer
    {
        internal Continuation? NextReady;

        internal abstract void Resume();

        internal override void Completed(Task task) => AsyncMethods.Ready(this);
    }

    internal static class AsyncMethods
    {
        sealed class Box<TStateMachine>(TStateMachine stateMachine, Task task) : Continuation where TStateMachine : IAsyncStateMachine
        {
            TStateMachine _stateMachine = stateMachine;

            // A step on the dispatcher: a throw faults the method's task.
            internal override void Resume()
            {
                Exception? thrown = Catch.RunStep(ref _stateMachine);
                if (thrown != null)
                    task.Fail(thrown);
            }
        }

        // Continuations ready to run, and those that awaited Task.Yield, which
        // run after the other fibers have had a turn.
        [ThreadStatic] static Continuation? t_readyHead, t_readyTail, t_yieldedHead, t_yieldedTail;
        [ThreadStatic] static bool t_starting; // a dispatcher spawned, not yet running

        // An await found its awaiter not done. The compiler's code has stored
        // the state and the awaiter in the state machine and returns once
        // this does. The first time, the state machine is copied into a box,
        // which then runs it; later awaits happen in the box's copy.
        internal static void Suspend<TAwaiter, TStateMachine>(Task task, ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            Continuation box = task.Box ??= new Box<TStateMachine>(stateMachine, task);
            Task target = Target(ref awaiter);
            if (target == Task.YieldMarker || target.IsCompleted) // IsCompleted: ForceYielding
            {
                Append(ref t_yieldedHead, ref t_yieldedTail, box);
                EnsureDispatcher();
            }
            else
            {
                target.AddObserver(box);
            }
        }

        internal static void Ready(Continuation box)
        {
            Append(ref t_readyHead, ref t_readyTail, box);
            EnsureDispatcher();
        }

        static void Append(ref Continuation? head, ref Continuation? tail, Continuation box)
        {
            box.NextReady = null;
            if (tail == null)
                head = box;
            else
                tail.NextReady = box;
            tail = box;
        }

        // 8 KB on a microcontroller, whose default fiber stack is 4 KB: each
        // step of an async method runs as a Catch.Run, whose saved registers
        // take a few hundred bytes, and an async method that calls another
        // before its first await runs both on this stack. The C64 keeps its
        // 2 KB (its jmp_buf is small and its heap can't spare 2 KB more);
        // elsewhere the default (32 KB and up) has room.
        static int DispatcherStackSize => Fiber.DefaultStackSize == 4 * 1024 ? 8 * 1024 : 0;

        static void EnsureDispatcher()
        {
            bool running;
            unsafe { running = Fiber.t_dispatcher != null; } // SAFETY: a plain read
            if (running || t_starting)
                return;
            t_starting = true;
            Fiber.Spawn(0, static _ => Dispatch(), DispatcherStackSize);
        }

        // The dispatcher gave its role up to wait in a blocking call: the
        // continuations already queued need another.
        static void DispatcherLeft()
        {
            if (t_readyHead != null || t_yieldedHead != null)
                EnsureDispatcher();
        }

        // The dispatcher: runs continuations until there are none, then ends
        // (so Fiber.Run can return). One that waits in a blocking call stops
        // being the dispatcher (Fiber.Park and Sleep see to it); once it's
        // back, it takes the role again if nobody has.
        static void Dispatch()
        {
            unsafe
            {
                // SAFETY: this fiber's record, and plain reads and writes of
                // the thread's dispatcher.
                Fiber.Record* me = Fiber.CurrentRecord;
                t_starting = false;
                Fiber.s_dispatcherLeft = &DispatcherLeft;
                Fiber.t_dispatcher = me;
                while (true)
                {
                    if (Fiber.t_dispatcher != me)
                    {
                        if (Fiber.t_dispatcher != null || t_starting)
                            return;
                        Fiber.t_dispatcher = me;
                    }
                    Continuation? box = t_readyHead;
                    if (box == null)
                    {
                        if (t_yieldedHead == null)
                            break;
                        // Let the other fibers run, then the yielded ones.
                        Fiber.Yield();
                        if (t_readyHead == null)
                        {
                            t_readyHead = t_yieldedHead;
                            t_readyTail = t_yieldedTail;
                        }
                        else
                        {
                            t_readyTail!.NextReady = t_yieldedHead;
                            t_readyTail = t_yieldedTail;
                        }
                        t_yieldedHead = t_yieldedTail = null;
                        continue;
                    }
                    t_readyHead = box.NextReady;
                    if (t_readyHead == null)
                        t_readyTail = null;
                    box.NextReady = null;
                    box.Resume();
                }
                Fiber.t_dispatcher = null;
            }
        }

        // OwnFiberMethodBuilder's await. The first time, on the caller's
        // stack, the state machine is copied onto a new fiber, which waits and
        // calls it again; later awaits happen on that fiber and only say what
        // to wait for next.
        internal static void SuspendOnFiber<TAwaiter, TStateMachine>(Task task, ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            Task target = Target(ref awaiter);
            task.Pending = target.IsCompleted ? Task.YieldMarker : target; // IsCompleted: ForceYielding
            if (task.HasFiber)
                return;
            task.HasFiber = true;
            Fiber.Spawn(new Resumer<TStateMachine>(stateMachine, task), static r => r.Run());
        }

        // The task to wait for. An awaiter from this file holds it; any other
        // gets a task of its own, completed by its OnCompleted callback.
        static Task Target<TAwaiter>(ref TAwaiter awaiter) where TAwaiter : INotifyCompletion
        {
            if (awaiter is IFiberAwaiter)
                unsafe { return Unsafe.As<TAwaiter, TaskAwaiter>(ref awaiter).m_task; } // SAFETY: an IFiberAwaiter is a struct whose one field is its task
            if (awaiter is IWaitTarget target) // boxes: ValueTask<T>'s awaiters, which hold a result too
                return target.WaitTarget;
            Task signal = new Task();
            awaiter.OnCompleted(() => signal.Complete());
            return signal;
        }

        internal static void OnCompleted(Task task, Action continuation)
        {
            if (task.IsCompleted)
                continuation();
            else
                task.AddObserver(new ActionObserver(continuation));
        }

        struct Resumer<TStateMachine>(TStateMachine stateMachine, Task task) where TStateMachine : IAsyncStateMachine
        {
            TStateMachine _stateMachine = stateMachine;

            public void Run()
            {
                while (task.Pending is Task pending)
                {
                    task.Pending = null;
                    if (pending == Task.YieldMarker)
                        Fiber.Yield();
                    else
                        pending.WaitDone(); // GetResult, in MoveNext, says what a cancel or a fault means
                    // A step on the method's own fiber: a throw faults its task.
                    Exception? thrown = Catch.RunStep(ref _stateMachine);
                    if (thrown != null)
                    {
                        task.Fail(thrown);
                        return;
                    }
                }
            }
        }
    }
}
