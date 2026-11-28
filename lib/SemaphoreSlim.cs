// SemaphoreSlim: limits how many fibers or async methods are inside a piece
// of code at once, as .NET's does. With a count of 1 it's the lock you need
// around an await:
//
//   static readonly SemaphoreSlim s_gate = new(1);
//
//   await s_gate.WaitAsync();
//   try { int seen = s_total; await Save(seen); s_total = seen + 1; }
//   finally { s_gate.Release(); }
//
// Fibers take turns on one thread and only switch where code waits, so code
// that doesn't wait needs no lock at all. A semaphore is for the code that
// does: without it, another fiber could run between reading s_total and
// writing it back.
//
// WaitAsync costs no fiber: a waiter is a Task<bool> in the semaphore's
// list, completed by the Release that hands it the count (in order: first
// to wait, first served). Wait() blocks the fiber, as Task.Wait does (and
// outside a fiber runs the others until it gets the count). A timeout
// completes the waiter with false; a canceled token cancels it, and
// awaiting it then ends the program as .NET's OperationCanceledException
// would (see lib/Cancellation.cs).
//
// One thread only, like the rest of lib/Tasks.cs: Parallel.For's threads
// can't share a semaphore. Not on WebAssembly, which has no fibers.

using System;
using System.Threading.Tasks;

namespace System.Threading
{
    /// <summary>Limits how many fibers or async methods run a piece of code at once.</summary>
    public class SemaphoreSlim : IDisposable
    {
        int _count;
        readonly int _max;
        // Async methods and fibers waiting, oldest first.
        Waiter? _first, _last;

        static readonly Task<bool> s_true = Task.FromResult(true);
        static readonly Task<bool> s_false = Task.FromResult(false);

        /// <summary>A semaphore with <paramref name="initialCount"/> free, and no maximum.</summary>
        public SemaphoreSlim(int initialCount) : this(initialCount, int.MaxValue) { }

        /// <summary>A semaphore with <paramref name="initialCount"/> free, and at most <paramref name="maxCount"/>.</summary>
        public SemaphoreSlim(int initialCount, int maxCount)
        {
            if (initialCount < 0 || initialCount > maxCount)
                throw new ArgumentOutOfRangeException(nameof(initialCount));
            if (maxCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCount));
            _count = initialCount;
            _max = maxCount;
        }

        /// <summary>How many more can enter now without waiting.</summary>
        public int CurrentCount => _count;

        /// <summary>Waits (without a fiber) to enter.</summary>
        public Task WaitAsync() => WaitAsync(Timeout.Infinite, default);

        /// <summary>Waits (without a fiber) to enter; canceled with <paramref name="cancellationToken"/>.</summary>
        public Task WaitAsync(CancellationToken cancellationToken) => WaitAsync(Timeout.Infinite, cancellationToken);

        /// <summary>Waits (without a fiber) to enter: true if it did, false after <paramref name="millisecondsTimeout"/> (-1: no limit).</summary>
        public Task<bool> WaitAsync(int millisecondsTimeout) => WaitAsync(millisecondsTimeout, default);

        /// <summary>Waits (without a fiber) to enter: true if it did, false after <paramref name="timeout"/>.</summary>
        public Task<bool> WaitAsync(TimeSpan timeout) => WaitAsync(DelayTimer.Milliseconds(timeout), default);

        /// <summary>WaitAsync(timeout), canceled with <paramref name="cancellationToken"/>.</summary>
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => WaitAsync(DelayTimer.Milliseconds(timeout), cancellationToken);

        /// <summary>
        /// Waits (without a fiber) to enter: true if it did, false after
        /// <paramref name="millisecondsTimeout"/> (-1: no limit, 0: only if
        /// free now). Canceled with <paramref name="cancellationToken"/>.
        /// </summary>
        public Task<bool> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<bool>(cancellationToken);
            if (_count > 0)
            {
                _count--;
                return s_true;
            }
            if (millisecondsTimeout == 0)
                return s_false;
            var waiter = new Waiter(this);
            if (_last == null)
                _first = waiter;
            else
                _last.Next = waiter;
            waiter.Previous = _last;
            _last = waiter;
            if (cancellationToken.CanBeCanceled)
                waiter.Registration = cancellationToken.Register(static w => ((Waiter)w!).Finish(false, true), waiter);
            if (millisecondsTimeout > 0)
            {
                waiter.Timer = new TimeoutTimer(waiter);
                waiter.Timer.Schedule(millisecondsTimeout);
            }
            return waiter;
        }

        /// <summary>Waits to enter: in a fiber, the fiber waits and the others run.</summary>
        public void Wait() => WaitAsync(Timeout.Infinite, default).Wait();

        /// <summary>Wait(), canceled with <paramref name="cancellationToken"/>: that ends the program, as the exception would in .NET.</summary>
        public void Wait(CancellationToken cancellationToken) => WaitAsync(Timeout.Infinite, cancellationToken).Wait();

        /// <summary>Waits to enter: true if it did, false after <paramref name="millisecondsTimeout"/> (-1: no limit).</summary>
        public bool Wait(int millisecondsTimeout) => WaitAsync(millisecondsTimeout, default).Result;

        /// <summary>Waits to enter: true if it did, false after <paramref name="timeout"/>.</summary>
        public bool Wait(TimeSpan timeout) => WaitAsync(timeout, default).Result;

        /// <summary>Wait(millisecondsTimeout), canceled with <paramref name="cancellationToken"/>.</summary>
        public bool Wait(int millisecondsTimeout, CancellationToken cancellationToken) => WaitAsync(millisecondsTimeout, cancellationToken).Result;

        /// <summary>Wait(timeout), canceled with <paramref name="cancellationToken"/>.</summary>
        public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) => WaitAsync(timeout, cancellationToken).Result;

        /// <summary>Leaves, letting the next waiter in. Returns the count before.</summary>
        public int Release() => Release(1);

        /// <summary>
        /// Leaves <paramref name="releaseCount"/> times, letting that many
        /// waiters in, oldest first. Returns the count before. Going over the
        /// maximum ends the program (.NET's SemaphoreFullException).
        /// </summary>
        public int Release(int releaseCount)
        {
            if (releaseCount < 1)
                throw new ArgumentOutOfRangeException(nameof(releaseCount));
            int previous = _count;
            if (_max - previous < releaseCount)
                Environment.FailFast("SemaphoreSlim.Release: adding the count would exceed the maximum (SemaphoreFullException)");
            while (releaseCount > 0 && _first != null)
            {
                _first.Finish(true, false);
                releaseCount--;
            }
            _count += releaseCount;
            return previous;
        }

        /// <summary>Changes nothing: a semaphore holds no system resources.</summary>
        public void Dispose() { }

        void Unlink(Waiter waiter)
        {
            if (waiter.Previous == null)
                _first = waiter.Next;
            else
                waiter.Previous.Next = waiter.Next;
            if (waiter.Next == null)
                _last = waiter.Previous;
            else
                waiter.Next.Previous = waiter.Previous;
            waiter.Next = waiter.Previous = null;
        }

        // One WaitAsync that had to wait. Completed once: by Release (true),
        // its timeout (false) or its token (canceled), whichever is first.
        sealed class Waiter(SemaphoreSlim owner) : Task<bool>
        {
            internal Waiter? Next, Previous;
            internal CancellationTokenRegistration Registration;
            internal TimeoutTimer? Timer;

            internal void Finish(bool entered, bool canceled)
            {
                if (IsCompleted)
                    return;
                owner.Unlink(this);
                Registration.Dispose();
                Registration = default;
                if (Timer != null)
                {
                    DelayTimer.Remove(Timer);
                    Timer = null;
                }
                if (canceled)
                    Complete(true);
                else
                    TrySetResult(entered);
            }
        }

        // A waiter's timeout, on Task.Delay's timer fiber.
        sealed class TimeoutTimer(Waiter waiter) : DelayTimer
        {
            internal override void Due() => waiter.Finish(false, false);
        }
    }
}
