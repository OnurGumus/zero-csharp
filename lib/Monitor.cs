// Monitor and Lock: what C#'s `lock` statement calls, so code ported from
// .NET that locks compiles and works, between threads (Parallel.For,
// Task.RunOnThread, a C library's threads) and between fibers.
//
//   static readonly object s_gate = new();    // or: new Lock() (C# 13)
//   lock (s_gate)
//       s_items.Add(item);
//
// Who holds a lock: the fiber that took it, or, outside fibers, the
// thread. As in .NET, the holder may take it again (and must leave as many
// times). A fiber that finds the lock taken yields, so the others run
// (among them, if it's on this thread, the holder); a thread outside fibers
// spins until it's free. So keep what's locked short: there's no waiting
// without using the processor. C# allows no await inside a lock; a fiber
// that sleeps or waits on a channel while holding one keeps it.
//
// Two cases can never get the lock, and end the program with a message
// instead of hanging: a fiber waiting for a lock its own thread took
// outside the fibers (that code can't run again until the fibers stop), and
// code outside the fibers waiting for a lock one of its thread's fibers
// holds.
//
// Lock keeps its state in itself. Monitor locks any object: the objects
// locked right now are in a table that one spin lock guards, which a
// lookup holds for a moment. Lock is the cheaper of the two.
//
// Not here: Monitor.Wait, Pulse and PulseAll. Between fibers and async
// methods, SemaphoreSlim and Channel do that job.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Threading
{
    /// <summary>A lock for C#'s lock statement (C# 13's System.Threading.Lock), held by a fiber or a thread.</summary>
    public sealed class Lock
    {
        LockOwner _state;

        /// <summary>Takes the lock, waiting while another fiber or thread holds it.</summary>
        public void Enter()
        {
            unsafe { LockOwner.Enter((LockOwner*)Unsafe.AsPointer(ref _state)); } // SAFETY: zero mode's objects don't move
        }

        /// <summary>Takes the lock if it's free (or already ours): true then.</summary>
        public bool TryEnter()
        {
            unsafe { return LockOwner.TryTake((LockOwner*)Unsafe.AsPointer(ref _state)); } // SAFETY: as in Enter
        }

        /// <summary>Takes the lock, waiting at most <paramref name="millisecondsTimeout"/> (-1: no limit): true if it did.</summary>
        public bool TryEnter(int millisecondsTimeout)
        {
            unsafe { return LockOwner.Enter((LockOwner*)Unsafe.AsPointer(ref _state), millisecondsTimeout); } // SAFETY: as in Enter
        }

        /// <summary>TryEnter, waiting at most <paramref name="timeout"/>.</summary>
        public bool TryEnter(TimeSpan timeout) => TryEnter(LockOwner.Milliseconds(timeout));

        /// <summary>Leaves the lock once. Ends the program if it isn't held here.</summary>
        public void Exit()
        {
            unsafe { LockOwner.Exit((LockOwner*)Unsafe.AsPointer(ref _state), "Lock.Exit"); } // SAFETY: as in Enter
        }

        /// <summary>Whether the current fiber (or, outside fibers, thread) holds the lock.</summary>
        public bool IsHeldByCurrentThread => Volatile.Read(ref _state.Owner) == LockOwner.Current;

        /// <summary>Takes the lock until the scope is disposed: what `lock (aLock)` calls.</summary>
        public Scope EnterScope()
        {
            Enter();
            return new Scope(this);
        }

        /// <summary>A held Lock, left by Dispose.</summary>
        public ref struct Scope
        {
            Lock? _lock;

            internal Scope(Lock held) => _lock = held;

            /// <summary>Leaves the lock (once, however often it's called).</summary>
            public void Dispose()
            {
                Lock? held = _lock;
                _lock = null;
                held?.Exit();
            }
        }
    }

    /// <summary>Locks on any object: what C#'s lock statement calls for an object that isn't a Lock.</summary>
    public static class Monitor
    {
        /// <summary>Takes <paramref name="obj"/>'s lock, waiting while another fiber or thread holds it.</summary>
        public static void Enter(object obj) => MonitorTable.Enter(obj, Timeout.Infinite);

        /// <summary>Enter, setting <paramref name="lockTaken"/> once taken (what `lock` calls).</summary>
        public static void Enter(object obj, ref bool lockTaken)
        {
            if (lockTaken)
                throw new ArgumentException("lockTaken must be false", nameof(lockTaken));
            MonitorTable.Enter(obj, Timeout.Infinite);
            lockTaken = true;
        }

        /// <summary>Takes the lock if it's free (or already ours): true then.</summary>
        public static bool TryEnter(object obj) => MonitorTable.Enter(obj, 0);

        /// <summary>TryEnter, setting <paramref name="lockTaken"/>.</summary>
        public static void TryEnter(object obj, ref bool lockTaken)
        {
            if (lockTaken)
                throw new ArgumentException("lockTaken must be false", nameof(lockTaken));
            lockTaken = MonitorTable.Enter(obj, 0);
        }

        /// <summary>Takes the lock, waiting at most <paramref name="millisecondsTimeout"/> (-1: no limit): true if it did.</summary>
        public static bool TryEnter(object obj, int millisecondsTimeout) => MonitorTable.Enter(obj, millisecondsTimeout);

        /// <summary>TryEnter, waiting at most <paramref name="timeout"/>.</summary>
        public static bool TryEnter(object obj, TimeSpan timeout) => MonitorTable.Enter(obj, LockOwner.Milliseconds(timeout));

        /// <summary>TryEnter(obj, millisecondsTimeout), setting <paramref name="lockTaken"/>.</summary>
        public static void TryEnter(object obj, int millisecondsTimeout, ref bool lockTaken)
        {
            if (lockTaken)
                throw new ArgumentException("lockTaken must be false", nameof(lockTaken));
            lockTaken = MonitorTable.Enter(obj, millisecondsTimeout);
        }

        /// <summary>TryEnter(obj, timeout), setting <paramref name="lockTaken"/>.</summary>
        public static void TryEnter(object obj, TimeSpan timeout, ref bool lockTaken) =>
            TryEnter(obj, LockOwner.Milliseconds(timeout), ref lockTaken);

        /// <summary>Leaves <paramref name="obj"/>'s lock once. Ends the program if it isn't held here.</summary>
        public static void Exit(object obj) => MonitorTable.Exit(obj);

        /// <summary>Whether the current fiber (or, outside fibers, thread) holds <paramref name="obj"/>'s lock.</summary>
        public static bool IsEntered(object obj) => MonitorTable.IsEntered(obj);
    }
}

// A lock's state: who holds it, from which thread, how many times. Owner
// changes by compare-and-swap; Thread and Count are the holder's to write.
struct LockOwner
{
    public nint Owner;      // the holder: its fiber's record, or its thread's marker; 0: free
    public nint Thread;     // the holder's thread's marker; 0 while it's being set or left
    public int Count;

    [ThreadStatic] static byte t_marker;

    // This thread, by an address only it has.
    internal static nint ThisThread
    {
        get { unsafe { return (nint)Unsafe.AsPointer(ref t_marker); } } // SAFETY: only compared, never read through
    }

    // The fiber running, or, outside fibers, this thread.
    internal static nint Current
    {
        get
        {
            unsafe
            {
                // SAFETY: only compared, never read through.
                Fiber.Record* fiber = Fiber.CurrentRecord;
                return fiber != null ? (nint)fiber : ThisThread;
            }
        }
    }

    internal static int Milliseconds(TimeSpan timeout)
    {
        long ms = (long)timeout.TotalMilliseconds;
        if (ms < -1 || ms > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return (int)ms;
    }

    // Takes it if free or ours.
    internal static unsafe bool TryTake(LockOwner* state)
    {
        unsafe
        {
            // SAFETY: a Lock's own state, which doesn't move.
            nint me = Current;
            if (Volatile.Read(ref state->Owner) == me)
            {
                state->Count++;
                return true;
            }
            if (Interlocked.CompareExchange(ref state->Owner, me, 0) != 0)
                return false;
            state->Thread = ThisThread;
            state->Count = 1;
            return true;
        }
    }

    internal static unsafe bool Enter(LockOwner* state, int millisecondsTimeout = Timeout.Infinite)
    {
        if (millisecondsTimeout < -1)
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
        unsafe
        {
            // SAFETY: as in TryTake.
            if (TryTake(state))
                return true;
            if (millisecondsTimeout == 0)
                return false;
            long deadline = millisecondsTimeout < 0 ? long.MaxValue : Clock.Monotonic + millisecondsTimeout * 1_000_000L;
            while (true)
            {
                if (!WaitATurn(Volatile.Read(ref state->Owner), Volatile.Read(ref state->Thread), deadline))
                    return false;
                if (TryTake(state))
                    return true;
            }
        }
    }

    internal static unsafe void Exit(LockOwner* state, string what)
    {
        unsafe
        {
            // SAFETY: as in TryTake.
            if (Volatile.Read(ref state->Owner) != Current)
                Environment.FailFast(what + ": the lock isn't held by this fiber or thread (SynchronizationLockException)");
            if (--state->Count > 0)
                return;
            state->Thread = 0;
            Volatile.Write(ref state->Owner, 0);
        }
    }

    // Lets the holder get on, once: a fiber yields, a thread spins. False
    // once the deadline has passed. Ends the program if the holder can't
    // run until we stop waiting.
    internal static bool WaitATurn(nint owner, nint ownerThread, long deadline)
    {
        if (Clock.Monotonic >= deadline)
            return false;
        bool inFiber;
        unsafe { inFiber = Fiber.CurrentRecord != null; } // SAFETY: a plain read
        if (owner != 0 && ownerThread == ThisThread)
        {
            if (inFiber && owner == ThisThread)
                Environment.FailFast("lock: a fiber waits for a lock its thread took outside the fibers, which can't run again until the fibers stop");
            if (!inFiber)
                Environment.FailFast("lock: code outside the fibers waits for a lock one of its thread's fibers holds, which can't run while it waits");
        }
        if (inFiber)
        {
            unsafe { Fiber.s_yield(); } // SAFETY: set by Spawn, so set in any fiber
        }
        else
            for (int i = 0; i < 64; i++)
                Volatile.Read(ref owner); // a short pause before looking again
        return true;
    }
}

// Monitor's table: the objects locked right now, each with its LockOwner,
// in an array from calloc (not the heap, which an arena may have replaced).
// One spin lock guards it; a look holds it for a moment, never while
// waiting. An object's entry goes when its lock is left for the last time.
static class MonitorTable
{
    [DllImport("libc")] static unsafe extern void* calloc(nuint count, nuint size);
    [DllImport("libc")] static unsafe extern void free(void* memory);

    struct Entry
    {
        public nint Object;
        public LockOwner State;
    }

    static int s_guard;
    static unsafe Entry* s_entries;
    static int s_count, s_capacity;

    static nint Address(object obj) => Unsafe.As<object, nint>(ref obj);

    static void Guard()
    {
        while (Interlocked.CompareExchange(ref s_guard, 1, 0) != 0)
        {
        }
    }

    static void Unguard() => Volatile.Write(ref s_guard, 0);

    static unsafe Entry* Find(nint address)
    {
        unsafe
        {
            // SAFETY: under the guard; s_count entries are in use.
            for (int i = 0; i < s_count; i++)
                if (s_entries[i].Object == address)
                    return &s_entries[i];
            return null;
        }
    }

    internal static bool Enter(object obj, int millisecondsTimeout)
    {
        if (obj == null)
            throw new ArgumentNullException(nameof(obj));
        if (millisecondsTimeout < -1)
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
        nint address = Address(obj);
        nint me = LockOwner.Current;
        long deadline = millisecondsTimeout < 0 ? long.MaxValue : Clock.Monotonic + millisecondsTimeout * 1_000_000L;
        while (true)
        {
            nint owner, ownerThread;
            unsafe
            {
                // SAFETY: the table is only touched under the guard.
                Guard();
                Entry* entry = Find(address);
                if (entry == null)
                {
                    entry = Add(address);
                    entry->State.Owner = me;
                    entry->State.Thread = LockOwner.ThisThread;
                    entry->State.Count = 1;
                    Unguard();
                    return true;
                }
                if (entry->State.Owner == me)
                {
                    entry->State.Count++;
                    Unguard();
                    return true;
                }
                owner = entry->State.Owner;
                ownerThread = entry->State.Thread;
                Unguard();
            }
            if (millisecondsTimeout == 0 || !LockOwner.WaitATurn(owner, ownerThread, deadline))
                return false;
        }
    }

    internal static void Exit(object obj)
    {
        if (obj == null)
            throw new ArgumentNullException(nameof(obj));
        nint address = Address(obj);
        unsafe
        {
            // SAFETY: the table is only touched under the guard.
            Guard();
            Entry* entry = Find(address);
            if (entry == null || entry->State.Owner != LockOwner.Current)
            {
                Unguard();
                Environment.FailFast("Monitor.Exit: the lock isn't held by this fiber or thread (SynchronizationLockException)");
            }
            if (--entry->State.Count == 0)
                *entry = s_entries[--s_count]; // the last one takes its place
            Unguard();
        }
    }

    internal static bool IsEntered(object obj)
    {
        if (obj == null)
            throw new ArgumentNullException(nameof(obj));
        nint address = Address(obj);
        nint me = LockOwner.Current;
        unsafe
        {
            // SAFETY: the table is only touched under the guard.
            Guard();
            Entry* entry = Find(address);
            bool held = entry != null && entry->State.Owner == me;
            Unguard();
            return held;
        }
    }

    // A new entry at the end, growing the array if it's full. Under the guard.
    static unsafe Entry* Add(nint address)
    {
        unsafe
        {
            // SAFETY: under the guard; the old array is copied, then freed.
            if (s_count == s_capacity)
            {
                int capacity = s_capacity == 0 ? 16 : s_capacity * 2;
                Entry* grown = (Entry*)calloc((nuint)capacity, (nuint)sizeof(Entry));
                if (grown == null)
                {
                    Unguard();
                    Environment.FailFast("Monitor.Enter: out of memory");
                }
                for (int i = 0; i < s_count; i++)
                    grown[i] = s_entries[i];
                free(s_entries);
                s_entries = grown;
                s_capacity = capacity;
            }
            Entry* entry = &s_entries[s_count++];
            entry->Object = address;
            return entry;
        }
    }
}
