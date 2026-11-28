// Fibers: green threads. Many fibers share one system thread, each with its
// own stack, and take turns: a fiber runs until it yields, sleeps, waits on a
// channel or ends, and then the next one in line runs.
//
//   Fiber.Spawn(channel, static ch => { for (int i = 0; i < 3; i++) ch.Send(i); ch.Close(); });
//   Fiber.Spawn(channel, static ch => { while (ch.Receive(out int i)) Out.Line(i); });
//   Fiber.Run(); // returns when every fiber has ended
//
// Switching is cooperative, so fibers need no locks among themselves: nothing
// else runs between two of a fiber's yields. A fiber that never yields keeps
// the others waiting.
//
// Each fiber's stack comes from the C library, with a guard page at its
// bottom, so a fiber that overflows its stack stops the program with a
// segmentation fault instead of overwriting memory. Spawn takes the size.
// The stack's lowest bytes also hold a pattern that Run checks each time the
// fiber switches back: it stops the program, naming the fiber, if the
// pattern has changed. That catches an overflow where there's no guard page
// (UEFI), and a frame big enough to jump over the guard page elsewhere.
// A switch saves and restores the registers a function must keep (the
// assembly in zerocore/native) and costs a few nanoseconds; there's no
// garbage collector to tell about the stacks.
//
// The scheduler belongs to the system thread that calls Run: fibers spawned
// on a thread run on it. Each fiber has its own ObjectAllocator setting
// (AllocateObjectsHere), starting with calloc: a scope opened in one fiber
// doesn't send another's objects to its arena.
//
// Each fiber also has its own Arena.Scratch arena, made on its first Scratch
// call and destroyed when it ends, so a fiber can wait inside Scratch.
//
// A fiber can also wait on a socket (lib/Sockets.cs): when no fiber is ready
// to run, Run waits in poll(2) until a socket is, or the earliest sleeper's
// time comes, and while fibers are busy it looks, without waiting, every 64
// switches.
//
// Rules:
// - Yield, Sleep, Send and Receive (and a Pipe's Read and Write) block only
//   inside a fiber. Outside one (in Main, before or after Run), Yield and
//   Sleep return at once, and a call that would wait stops the program.
// - When every fiber left is waiting on a channel or a pipe, none can ever run again:
//   Run stops the program with a message saying so.
// - Linux (x64, arm64), macOS (arm64), Windows (x64), UEFI (x64, arm64) and
//   Cortex-M microcontrollers. UEFI and microcontrollers have no guard page:
//   an overflow is only noticed when the fiber next switches back, by the
//   pattern check, and one that goes past the (unprotected) guard page may
//   already have overwritten other memory. The Pico 2's Cortex-M33 stops the
//   machine stack in hardware (arm/runtime.c).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>Green threads: functions that take turns on one system thread, each on its own stack.</summary>
public static class Fiber
{
    [DllImport("*"), SuppressGCTransition]
    static unsafe extern void ZeroFiberSwitch(void** save, void* resume);

    [DllImport("*"), SuppressGCTransition]
    static unsafe extern void* ZeroFiberInit(void* top, void* bottom, delegate*<void*, void> entry, void* argument);

    [DllImport("libc")] static unsafe extern void* malloc(nuint size);
    [DllImport("libc")] static unsafe extern void free(void* memory);
    [DllImport("libc")] static unsafe extern void* calloc(nuint count, nuint size);
    [DllImport("libc")] static unsafe extern int mprotect(void* address, nuint length, int protection);
    [DllImport("libc")] static safe extern int getpagesize();
    [DllImport("libc")] static safe extern int usleep(uint microseconds);

    // mprotect's flags, the same on Linux and macOS. Windows and UEFI have no
    // mprotect: win/runtime.c's calls VirtualProtect, efi/runtime.c's does
    // nothing (the firmware's memory has no guard pages), and so do DOS's
    // and a microcontroller's.
    const int ProtectNone = 0, ProtectReadWrite = 3;

    // The pattern at the bottom of every fiber's stack, in this many words.
    const ulong Canary = 0x5AFE_57AC_CAFE_F1BE;
    const int CanaryWords = 8;

    // A machine without guard pages reports pages smaller than 4 KB: the
    // memory Spawn reserves below each stack, and a hint of how much RAM
    // there is. 256 bytes: a C64 (c64/runtime.c); 1 KB: a microcontroller
    // (arm/runtime.c); 2 KB: DOS (dos/runtime.c), a few MB.
    static bool SmallMachine => getpagesize() < 4096;

    /// <summary>
    /// The stack size Spawn gives a fiber unless told otherwise: 256 KB, or
    /// 32 KB on DOS, 4 KB on a microcontroller and 2 KB on a C64.
    /// </summary>
    public static int DefaultStackSize => getpagesize() switch
    {
        <= 256 => 2 * 1024,
        <= 1024 => 4 * 1024,
        < 4096 => 32 * 1024,
        _ => 256 * 1024,
    };

    /// <summary>
    /// The smallest stack Spawn gives a fiber, whatever it's asked for: 16 KB,
    /// or 4 KB on DOS, 1 KB on a microcontroller and 512 bytes on a C64.
    /// </summary>
    public static int MinStackSize => getpagesize() switch
    {
        <= 256 => 512,
        <= 1024 => 1024,
        < 4096 => 4 * 1024,
        _ => 16 * 1024,
    };

    // One fiber. It lives at the top of its own stack's memory, followed by
    // the state Spawn was given:
    //   [guard page][stack, growing down ...][Record][state]
    internal struct Record
    {
        public void* StackPointer;  // while switched out
        public byte* Memory;        // what malloc gave
        public byte* Guard;         // the guard page, the first whole page in it
        public ulong* Bottom;       // the stack's lowest bytes, just above the guard page: the pattern
        public nuint GuardSize;
        public Record* Next;        // in the run queue, the sleepers or a channel's waiters
        public long WakeAt;         // Clock.Monotonic, while sleeping; while waiting on input or
                                    // output, when that times out (0: never)
        public Runner Run;          // Runner<TState>.Instance
        public object Body;         // the Action<TState>
        public void* State;         // where the TState is
        public delegate*<void*, nuint, void*> Allocate; // its ObjectAllocator setting
        public void* AllocateContext;
        public void* Scratch;       // its Arena.Scratch state, while switched out
        public void* Catch;         // its innermost Catch.Run, while switched out
        public int Id;
        public bool Done;
        public bool Woken;          // by a channel, as opposed to closed
        public void* Slot;          // while waiting on a channel: the T to send, or to receive into
        public int Fd;              // while waiting on input or output (WaitForIO): what for,
        public short Events;        // as poll(2) takes it,
        public short Revents;       // and what poll said
    }

    // Someone waiting on input or output without a fiber: an async socket
    // operation (lib/Sockets.cs). Its record (from malloc, not a fiber's)
    // goes on the same list as the fibers waiting in WaitForIO, with Body
    // this object and Run null; when poll says the socket is ready, PollIO
    // calls Ready instead of waking a fiber.
    internal abstract class IoWaiter
    {
        internal abstract void Ready(short revents);
    }

    // One per system thread that runs fibers.
    struct Scheduler
    {
        public Record* Current;     // null: the thread's own stack (in Run, or outside it)
        public void* MainStack;     // Run's stack pointer, while a fiber runs
        public Record* RunHead, RunTail;
        public Record* Sleepers;    // in WakeAt order
        public int Live;            // spawned and not yet ended
        public int Waiting;         // on a channel
        public int NextId;
        public long Switches;
        public Record* IoWaiters;   // waiting on input or output (WaitForIO), newest first
        public int IoWaiting;
        public void* PollFds;       // the array PollIO hands poll(2), from malloc
        public int PollCapacity;    // in entries
        public int AsyncWaiting;    // async operations among the IoWaiters, counted in Live too
    }

    // poll(2), set by the code that first waits on input or output (the
    // sockets in lib/Sockets.cs). A pointer, so that a program that uses
    // fibers and no sockets doesn't link poll: DOS, microcontrollers and the
    // C64 have none. Its array holds struct pollfd: { int fd; short events;
    // short revents; }.
    internal static delegate*<void*, nuint, int, int> Poll;

    // PollIO, set by the first WaitForIO. Run calls it through this pointer,
    // so a program that never waits on input or output doesn't carry its
    // code (1.3 KB on a C64).
    static delegate*<Scheduler*, bool, void> s_pollIO;

    [ThreadStatic] static Scheduler* t_scheduler;

    // The fiber that runs async methods' continuations on this thread
    // (lib/Tasks.cs), if any. A fiber that waits (Park, Sleep, WaitForIO)
    // gives the role up, keeping the continuation it was running, and Tasks
    // starts another dispatcher for the continuations queued or due
    // meanwhile.
    [ThreadStatic] internal static Record* t_dispatcher;

    // What Tasks does when the dispatcher gives the role up: set by the
    // dispatcher itself, so programs without async methods link none of it.
    internal static delegate*<void> s_dispatcherLeft;

    // Yield, set by Spawn: code that may run in a fiber, or not (lock, in
    // lib/Monitor.cs), yields through this, so a program that never spawns
    // a fiber, and so is never in one, doesn't link the stack switch
    // (WebAssembly has none).
    internal static delegate*<void> s_yield;

    static void LeaveDispatcher(Record* current)
    {
        if (current != t_dispatcher)
            return;
        t_dispatcher = null;
        unsafe
        {
            // SAFETY: set before t_dispatcher ever was, to a static method.
            s_dispatcherLeft();
        }
    }

    static Scheduler* This
    {
        get
        {
            Scheduler* s = t_scheduler;
            if (s == null)
            {
                unsafe
                {
                    // SAFETY: calloc'd and kept for the thread's lifetime.
                    s = (Scheduler*)calloc(1, (nuint)sizeof(Scheduler));
                }
                t_scheduler = s;
            }
            return s;
        }
    }

    /// <summary>
    /// Starts <paramref name="body"/>(<paramref name="state"/>) as a fiber. It
    /// runs once Run is called (or, from a fiber, when that fiber next yields).
    /// Pass what the fiber needs as <paramref name="state"/> and use a static
    /// lambda: Spawn copies the state onto the fiber's stack and allocates
    /// nothing else. <paramref name="stackSize"/> 0 means DefaultStackSize.
    /// Returns the fiber's id, from 1.
    /// </summary>
    // Spawn, Yield, Sleep and Park aren't inlined: a call costs little next
    // to what they do, and one copy of each keeps a small machine's program
    // smaller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Spawn<TState>(TState state, Action<TState> body, int stackSize = 0)
    {
        Scheduler* s = This;
        unsafe
        {
            s_yield = &Yield;
            // SAFETY: the memory holds the record and the state at its top and
            // the stack below them; it's freed only once the fiber has ended
            // and the scheduler is back on its own stack.
            nuint page = (nuint)getpagesize();
            nuint stateSize = ((nuint)sizeof(TState) + 15) & ~(nuint)15;
            nuint recordSize = ((nuint)sizeof(Record) + 15) & ~(nuint)15;
            if (stackSize == 0)
                stackSize = DefaultStackSize;
            nuint stack = ((nuint)Math.Max(stackSize, MinStackSize) + page - 1) & ~(page - 1);
            // A page more than needed, so the guard page can start on a page
            // boundary (posix_memalign would do it, but Windows hasn't one).
            // A microcontroller has nothing to protect a page with, so its
            // guard needs no aligning, and its RAM no page to spare.
            nuint slack = SmallMachine ? 0 : page;
            byte* memory = (byte*)malloc(slack + page + stack + recordSize + stateSize);
            if (memory == null)
                Environment.FailFast("Fiber.Spawn: out of memory");
            byte* guard = SmallMachine ? memory : (byte*)(((nuint)memory + page - 1) & ~(page - 1));
            if (mprotect(guard, page, ProtectNone) != 0)
                Environment.FailFast("Fiber.Spawn: can't make the guard page");

            byte* top = guard + page + stack;
            Record* f = (Record*)top;
            *f = default;
            f->Memory = memory;
            f->Guard = guard;
            f->GuardSize = page;
            f->State = top + recordSize;
            *(TState*)f->State = state;
            f->Body = body;
            f->Run = Runner<TState>.Instance;
            f->Id = ++s->NextId;
            f->Bottom = (ulong*)(guard + page);
            for (int i = 0; i < CanaryWords; i++)
                f->Bottom[i] = Canary;
            f->StackPointer = ZeroFiberInit(top, guard + page, &Entry, f);
            s->Live++;
            Enqueue(s, f);
            return f->Id;
        }
    }

    /// <summary>The running fiber's id, or 0 outside a fiber.</summary>
    public static int Id
    {
        get
        {
            unsafe
            {
                // SAFETY: the thread's scheduler, and a live fiber's record.
                Record* current = This->Current;
                return current == null ? 0 : current->Id;
            }
        }
    }

    /// <summary>Fibers spawned on this thread that haven't ended yet.</summary>
    // Live also counts the async operations waiting on input or output
    // (WaitForIOAsync), which aren't fibers.
    public static int Count { get { unsafe { return This->Live - This->AsyncWaiting; } } } // SAFETY: the thread's scheduler

    /// <summary>How many times this thread has switched to a fiber: for measuring.</summary>
    public static long Switches { get { unsafe { return This->Switches; } } } // SAFETY: the thread's scheduler

    /// <summary>
    /// Runs the fibers spawned on this thread, and the ones they spawn, until
    /// all have ended.
    /// </summary>
    public static void Run() => RunUntil(null);

    // Run, stopping early once `until` has completed. Awaiting a task outside
    // a fiber (in an async Main, say) runs the fibers this way until the task
    // is done (lib/Tasks.cs).
    internal static void RunUntil(System.Threading.Tasks.Task? until)
    {
        Scheduler* s = This;
        unsafe
        {
            if (s->Current != null)
                Environment.FailFast("Fiber.Run: called from a fiber");
            // SAFETY: every record in the queues is a live fiber's; a fiber's
            // memory is freed here, on Run's own stack, after it has ended.
            while (s->Live > 0 && (until == null || !until.IsCompleted))
            {
                Record* f = Dequeue(s);
                if (f == null)
                {
                    if (s->IoWaiters != null)
                    {
                        // Nothing to run: wait for input or output, or until
                        // the earliest sleeper's time.
                        s_pollIO(s, true);
                        if (s->Sleepers != null)
                            WakeSleepers(s, wait: false);
                        continue;
                    }
                    if (s->Sleepers == null)
                        Environment.FailFast(until == null
                            ? "Fiber.Run: every fiber is waiting on a channel or a task, so none can run again"
                            : "await: every fiber is waiting on a channel or a task, so the awaited task can never complete");
                    WakeSleepers(s, wait: true);
                    continue;
                }

                System.Runtime.ObjectAllocator.Get(out var mainAllocate, out void* mainContext);
                if (f->Allocate != mainAllocate || f->AllocateContext != mainContext)
                    System.Runtime.ObjectAllocator.Set(f->Allocate, f->AllocateContext);
                void* mainScratch = Arena.SwapScratch(f->Scratch);
                // Null in a program that never calls Catch.Run (Catch.cs).
                delegate*<void*, void*> swapCatch = System.Runtime.CatchFrames.s_swap;
                void* mainCatch = swapCatch != null ? swapCatch(f->Catch) : null;
                s->Current = f;
                s->Switches++;
                ZeroFiberSwitch(&s->MainStack, f->StackPointer);
                s->Current = null;
                for (int i = 0; i < CanaryWords; i++)
                    if (f->Bottom[i] != Canary)
                        StackOverflowed(f);
                f->Scratch = Arena.SwapScratch(mainScratch);
                // The fiber may have been the first to call Catch.Run.
                if (swapCatch == null && (swapCatch = System.Runtime.CatchFrames.s_swap) != null)
                    mainCatch = null;
                if (swapCatch != null)
                    f->Catch = swapCatch(mainCatch);
                System.Runtime.ObjectAllocator.Get(out f->Allocate, out f->AllocateContext);
                if (f->Allocate != mainAllocate || f->AllocateContext != mainContext)
                    System.Runtime.ObjectAllocator.Set(mainAllocate, mainContext);

                if (f->Done)
                {
                    Arena.FreeScratch(f->Scratch);
                    mprotect(f->Guard, f->GuardSize, ProtectReadWrite);
                    free(f->Memory);
                }
                if (s->Sleepers != null)
                    WakeSleepers(s, wait: false);
                // Busy fibers don't keep the waiting ones from their input:
                // a look, without waiting, every 64 switches.
                if (s->IoWaiters != null && (s->Switches & 63) == 0)
                    s_pollIO(s, false);
            }
            if (until != null && !until.IsCompleted)
                Environment.FailFast("await: no fiber is left that could complete the awaited task");
        }
    }

    // Blocks the current fiber until fd is ready for `events` (poll(2)'s
    // POLLIN, POLLOUT...), letting the others run. Returns what poll said,
    // or 0 outside a fiber, where the caller waits by itself. With a
    // deadline (Clock.Monotonic; 0: none), TimedOut once it has passed.
    internal static short WaitForIO(int fd, short events, long deadline = 0)
    {
        Scheduler* s = This;
        unsafe
        {
            // SAFETY: the current fiber's record, parked until PollIO wakes it.
            Record* current = s->Current;
            if (current == null)
                return 0;
            // A continuation waiting for a socket keeps its fiber; the other
            // continuations get a new dispatcher (as in Park and Sleep).
            LeaveDispatcher(current);
            s_pollIO = &PollIO;
            AddIoWaiter(s, current, fd, events, deadline);
            SwitchToScheduler(s, current);
            return current->Revents;
        }
    }

    static void AddIoWaiter(Scheduler* s, Record* r, int fd, short events, long deadline)
    {
        unsafe
        {
            // SAFETY: a parked fiber's record, or an IoWaiter's from malloc.
            r->Fd = fd;
            r->Events = events;
            r->Revents = 0;
            r->WakeAt = deadline;
            r->Next = s->IoWaiters;
            s->IoWaiters = r;
            s->IoWaiting++;
        }
    }

    // Takes a waiter off the list (it was found ready, timed out, or is
    // being cancelled).
    static void RemoveIoWaiter(Scheduler* s, Record** link)
    {
        unsafe
        {
            // SAFETY: *link is on the list.
            Record* r = *link;
            *link = r->Next;
            r->Next = null;
            s->IoWaiting--;
            if (r->Run == null)
            {
                s->Live--; // an async operation: see WaitForIOAsync
                s->AsyncWaiting--;
            }
        }
    }

    // What WaitForIO returns, and Ready gets, when the deadline passed
    // first: not one of poll's bits.
    internal const short TimedOut = 0x2000;

    // An async operation's record (r->Body its IoWaiter) waits for fd to be
    // ready for events, without a fiber: Ready is called, from Fiber.Run,
    // when it is, or with TimedOut at the deadline, or Cancelled. The record
    // goes off the list before Ready is called, so Ready can wait again.
    internal static void WaitForIOAsync(Record* r, int fd, short events, long deadline = 0)
    {
        Scheduler* s = This;
        unsafe
        {
            s_pollIO = &PollIO;
            // SAFETY: the operation's record, kept until it ends.
            r->Run = null;
            AddIoWaiter(s, r, fd, events, deadline);
            // Counted as live while it waits, so Run goes on without a
            // fiber, and a program with no sockets pays nothing for it.
            s->Live++;
            s->AsyncWaiting++;
        }
    }

    // Takes an async operation's record off the list (its token was
    // canceled): false if it wasn't on it.
    internal static bool StopWaitingForIO(Record* r)
    {
        Scheduler* s = This;
        unsafe
        {
            // SAFETY: the waiters' records.
            Record** link = &s->IoWaiters;
            while (*link != null && *link != r)
                link = &(*link)->Next;
            if (*link == null)
                return false;
            RemoveIoWaiter(s, link);
            return true;
        }
    }

    // A waiter is done waiting: a fiber goes back in the run queue, an async
    // operation goes on `ready`, for its Ready once the list is consistent.
    static void Finish(Scheduler* s, Record* r, short revents, ref Record* ready)
    {
        unsafe
        {
            // SAFETY: a record just taken off the list.
            r->Revents = revents;
            if (r->Run == null)
            {
                r->Next = ready;
                ready = r;
            }
            else
            {
                Enqueue(s, r);
            }
        }
    }

    // Calls Ready for the async operations PollIO or CancelIO took off the
    // list, in the order they were found.
    static void CallReady(Record* ready)
    {
        unsafe
        {
            // SAFETY: async operations' records, off every list.
            Record* reversed = null;
            while (ready != null)
            {
                Record* next = ready->Next;
                ready->Next = reversed;
                reversed = ready;
                ready = next;
            }
            while (reversed != null)
            {
                Record* r = reversed;
                reversed = r->Next;
                r->Next = null;
                ((IoWaiter)r->Body).Ready(r->Revents);
            }
        }
    }

    // What WaitForIO returns to a fiber whose socket was closed while it
    // waited (CancelIO): not one of poll's bits.
    internal const short Cancelled = 0x4000;

    // Wakes the fibers waiting on fd, with Cancelled, before it's closed: the
    // system may give the number to the next socket opened, and a waiter left
    // in the list would wake for that one's input.
    internal static void CancelIO(int fd)
    {
        Scheduler* s = This;
        unsafe
        {
            // SAFETY: the waiters are parked fibers' records, or async
            // operations' records.
            Record* ready = null;
            Record** link = &s->IoWaiters;
            while (*link != null)
            {
                Record* f = *link;
                if (f->Fd != fd)
                {
                    link = &f->Next;
                    continue;
                }
                RemoveIoWaiter(s, link);
                Finish(s, f, Cancelled, ref ready);
            }
            CallReady(ready);
        }
    }

    // Asks poll which waiters' input or output is ready, and puts those back
    // in the run queue. With wait, waits until one is, or until the earliest
    // sleeper's time; without, only looks. If poll fails (a socket closed
    // under its waiter, say), every waiter goes back, to try again and find out.
    static void PollIO(Scheduler* s, bool wait)
    {
        unsafe
        {
            long until = !wait ? 0 : s->Sleepers == null ? long.MaxValue : s->Sleepers->WakeAt;
            // SAFETY: the waiters are parked fibers' records; PollFds has
            // room for PollCapacity entries of 8 bytes.
            int n = s->IoWaiting;
            if (n > s->PollCapacity)
            {
                int capacity = Math.Max(n, s->PollCapacity * 2);
                free(s->PollFds);
                s->PollFds = malloc((nuint)capacity * 8);
                if (s->PollFds == null)
                    Environment.FailFast("Fiber.Run: out of memory for the poll array");
                s->PollCapacity = capacity;
            }
            byte* fds = (byte*)s->PollFds;
            int i = 0;
            bool deadlines = false;
            for (Record* f = s->IoWaiters; f != null; f = f->Next, i++)
            {
                *(int*)(fds + i * 8) = f->Fd;
                *(short*)(fds + i * 8 + 4) = f->Events;
                *(short*)(fds + i * 8 + 6) = 0;
                if (f->WakeAt != 0)
                {
                    deadlines = true;
                    if (f->WakeAt < until)
                        until = f->WakeAt;
                }
            }
            int timeout = until == long.MaxValue ? -1 : until == 0 ? 0 : MillisecondsUntil(until);
            int ready = Poll(fds, (nuint)n, timeout);
            if (ready == 0 && !deadlines)
                return;
            long now = deadlines ? Clock.Monotonic : 0;
            Record* done = null;
            Record** link = &s->IoWaiters;
            i = 0;
            while (*link != null)
            {
                Record* f = *link;
                short revents = ready < 0 ? (short)1 : ready == 0 ? (short)0 : *(short*)(fds + i * 8 + 6);
                i++;
                if (revents == 0 && f->WakeAt != 0 && f->WakeAt <= now)
                    revents = TimedOut;
                if (revents == 0)
                {
                    link = &f->Next;
                    continue;
                }
                RemoveIoWaiter(s, link);
                Finish(s, f, revents, ref done);
            }
            CallReady(done);
        }
    }

    // Milliseconds from now to a Clock.Monotonic time, rounded up; 0 if past.
    // * 1074 / 2^30 is a hair more than / 1000000, without the code for
    // 64-bit division (as in WakeSleepers). At most 1000 s at a time; Run's
    // loop comes back.
    internal static int MillisecondsUntil(long time)
    {
        long nanos = Math.Min(time - Clock.Monotonic, 1_000_000_000_000L);
        if (nanos <= 0)
            return 0;
        return (int)((nanos * 1074 >> 30) + 1);
    }

    static void StackOverflowed(Record* f)
    {
        int id;
        unsafe { id = f->Id; } // SAFETY: the record of the fiber that just switched back
        Environment.FailFast("Fiber " + id.ToString() + " overflowed its stack: give it a bigger one (Spawn's stackSize)");
    }

    /// <summary>Lets the other fibers that are ready run before this one carries on.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Yield()
    {
        Scheduler* s = This;
        Record* current;
        unsafe { current = s->Current; } // SAFETY: the thread's scheduler
        if (current == null)
            return;
        Enqueue(s, current);
        SwitchToScheduler(s, current);
    }

    /// <summary>Lets the other fibers run for at least <paramref name="milliseconds"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Sleep(long milliseconds)
    {
        Scheduler* s = This;
        Record* current;
        unsafe { current = s->Current; } // SAFETY: the thread's scheduler
        if (current == null)
        {
            if (milliseconds > 0)
                usleep((uint)(milliseconds * 1000));
            return;
        }
        LeaveDispatcher(current);
        unsafe
        {
            // SAFETY: the sleepers are live fibers' records.
            current->WakeAt = Clock.Monotonic + milliseconds * 1_000_000;
            Record** link = &s->Sleepers;
            while (*link != null && (*link)->WakeAt <= current->WakeAt)
                link = &(*link)->Next;
            current->Next = *link;
            *link = current;
        }
        SwitchToScheduler(s, current);
    }

    /// <summary>
    /// Sleep, ending early if <paramref name="cancellationToken"/> is canceled:
    /// true if it slept the whole time, false if canceled (before or during).
    /// Outside a fiber, the thread sleeps and the token is only checked
    /// before.
    /// </summary>
    public static bool Sleep(long milliseconds, System.Threading.CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        Record* current;
        unsafe { current = This->Current; } // SAFETY: the thread's scheduler
        if (current == null || !cancellationToken.CanBeCanceled)
        {
            Sleep(milliseconds);
            return true;
        }
        var waker = new Waker(current);
        using (cancellationToken.Register(static w => ((Waker)w!).WakeEarly(), waker))
            Sleep(milliseconds);
        return !cancellationToken.IsCancellationRequested;
    }

    // A sleeping fiber, for a cancellation callback to wake.
    sealed class Waker(Record* fiber)
    {
        public void WakeEarly() => Fiber.WakeEarly(fiber);
    }

    // Moves the sleepers whose time has come to the run queue; with wait, first
    // sleeps until the earliest's.
    static void WakeSleepers(Scheduler* s, bool wait)
    {
        unsafe
        {
            // SAFETY: the sleepers are live fibers' records.
            long now = Clock.Monotonic;
            if (wait && s->Sleepers->WakeAt > now)
            {
                // Nanoseconds to microseconds, rounded up: * 1049 / 2^20 is a
                // hair more than / 1000 (by 0.04%), and saves a small
                // machine the code for 64-bit division (1 KB on a C64).
                // At most 1000 s at a time; the loop in Run comes back.
                long nanos = Math.Min(s->Sleepers->WakeAt - now, 1_000_000_000_000L);
                usleep((uint)((nanos * 1049 >> 20) + 1));
                now = Clock.Monotonic;
            }
            while (s->Sleepers != null && s->Sleepers->WakeAt <= now)
            {
                Record* f = s->Sleepers;
                s->Sleepers = f->Next;
                Enqueue(s, f);
            }
        }
    }

    // The current fiber stops until something puts it back in the run queue.
    static void SwitchToScheduler(Scheduler* s, Record* current)
    {
        unsafe { ZeroFiberSwitch(&current->StackPointer, s->MainStack); } // SAFETY: Run's stack is waiting in its switch
    }

    // Blocks the current fiber until Wake. Called by Channel with the fiber
    // already on its list of waiters. Returns what Wake was told.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Park(string operation)
    {
        Scheduler* s = This;
        Record* current;
        unsafe { current = s->Current; } // SAFETY: the thread's scheduler
        if (current == null)
            Environment.FailFast(operation);
        LeaveDispatcher(current);
        unsafe
        {
            // SAFETY: the current fiber's record.
            current->Woken = false;
            s->Waiting++;
            SwitchToScheduler(s, current);
            return current->Woken;
        }
    }

    internal static void Wake(Record* f, bool woken)
    {
        Scheduler* s = This;
        unsafe
        {
            // SAFETY: a parked fiber's record, which Park keeps alive.
            f->Woken = woken;
            s->Waiting--;
            Enqueue(s, f);
        }
    }

    // Ends a sleeping fiber's Sleep early: it goes back in the run queue now.
    // Does nothing to a fiber that isn't sleeping (running, ready, or waiting
    // on a channel). System.Timers.Timer's Stop uses it, so a stopped timer's
    // fiber ends at once rather than when its interval would have.
    internal static void WakeEarly(Record* f)
    {
        Scheduler* s = This;
        unsafe
        {
            // SAFETY: the sleepers are live fibers' records.
            Record** link = &s->Sleepers;
            while (*link != null && *link != f)
                link = &(*link)->Next;
            if (*link == null)
                return;
            *link = f->Next;
            Enqueue(s, f);
        }
    }

    internal static Record* CurrentRecord { get { unsafe { return This->Current; } } } // SAFETY: the thread's scheduler

    static void Enqueue(Scheduler* s, Record* f)
    {
        unsafe
        {
            // SAFETY: live fibers' records.
            f->Next = null;
            if (s->RunTail == null)
                s->RunHead = f;
            else
                s->RunTail->Next = f;
            s->RunTail = f;
        }
    }

    static Record* Dequeue(Scheduler* s)
    {
        unsafe
        {
            // SAFETY: live fibers' records.
            Record* f = s->RunHead;
            if (f != null)
            {
                s->RunHead = f->Next;
                if (s->RunHead == null)
                    s->RunTail = null;
                f->Next = null;
            }
            return f;
        }
    }

    // Where a fiber starts, on its own stack (ZeroFiberStart calls it). It
    // never returns: the last switch leaves the stack for Run to free.
    //
    // An ordinary C# method, not [UnmanagedCallersOnly]: compiled to native
    // code, its convention is C's (one pointer argument), and compiled to
    // bitcode (DOS, microcontrollers, the C64) it takes its shadow stack
    // first, which ZeroFiberStart passes it: the fiber's own. Leaving the
    // runtime's entry from native code alone matters there: it hands every
    // entry the same shadow stack, which lets the optimizer build that
    // address into all the code (2 KB smaller on a C64).
    static void Entry(void* record)
    {
        unsafe
        {
            // SAFETY: Spawn's record, alive until Run frees it after this switch.
            Record* f = (Record*)record;
            f->Run.Run(f);
            f->Done = true;
            f->Body = null;
            Scheduler* s = This;
            s->Live--;
            ZeroFiberSwitch(&f->StackPointer, s->MainStack);
        }
    }

    // Calls a fiber's body with its state, which Spawn knew the type of. A
    // class per type, called virtually, rather than a pointer to a generic
    // RunBody<TState>: for a class TState that would be a "fat" pointer (to
    // code shared by all classes, plus which class), whose mark, the top bit
    // of 32, a C64's 16-bit pointers can't keep.
    internal abstract class Runner
    {
        public abstract void Run(Record* f);
    }

    sealed class Runner<TState> : Runner
    {
        public static readonly Runner<TState> Instance = new Runner<TState>();

        public override void Run(Record* f)
        {
            Action<TState> body;
            TState state;
            unsafe
            {
                // SAFETY: Spawn stored an Action<TState> and a TState here.
                body = System.Runtime.CompilerServices.Unsafe.As<Action<TState>>(f->Body);
                state = *(TState*)f->State;
            }
            body(state);
        }
    }
}

/// <summary>
/// A queue that fibers pass values through: Send waits while it's full,
/// Receive while it's empty. Capacity 0 makes every Send wait for a Receive.
/// </summary>
public sealed partial class Channel<T>
{
    readonly T[] _items;
    int _head, _count;
    bool _closed;
    // Fibers waiting, in order. Each one's Slot points into its own stack: the
    // value a sender waits to send, or the variable a receiver waits to fill.
    // A value moves straight into or out of a slot, so a woken fiber finds its
    // work done and nothing can take its place in the meantime.
    unsafe Fiber.Record* _senders, _receivers;
    // Async methods waiting (lib/ChannelAsync.cs), once ReceiveAsync,
    // SendAsync or WaitToReceiveAsync has had to wait. Fibers waiting go
    // first. Behind an abstract class, so a program that never uses them
    // links none of their code (on a C64, every KB counts).
    AsyncWaiters? _async;

    internal abstract class AsyncWaiters
    {
        internal abstract bool HasSender { get; }
        internal abstract bool HasReceiver { get; }
        internal abstract bool Give(T value);       // to a waiting ReceiveAsync: true if one took it
        internal abstract bool Take(out T value);   // from a waiting SendAsync: true if there was one
        internal abstract void Available();        // a value is ready: WaitToReceiveAsync's wake
        internal abstract void Closed();
    }

    /// <summary>A channel that holds up to <paramref name="capacity"/> values before Send waits.</summary>
    public Channel(int capacity = 0)
    {
        Capacity = capacity < 0 ? 0 : capacity;
        _items = new T[Capacity < 1 ? 1 : Capacity];
    }

    /// <summary>How many values the channel holds before Send waits.</summary>
    public int Capacity { get; }

    /// <summary>Values sent and not yet received.</summary>
    public int Count => _count;

    /// <summary>Whether Close has been called.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Sends <paramref name="value"/>, waiting while the channel is full (with
    /// capacity 0, until a fiber receives it). Sending on a closed channel
    /// stops the program.
    /// </summary>
    public void Send(T value)
    {
        if (_closed)
            Environment.FailFast("Channel.Send: the channel is closed");
        unsafe
        {
            // SAFETY: the waiter lists hold parked fibers' records, alive
            // while parked; their slots point into their parked stacks.
            if (_receivers != null)
            {
                Fiber.Record* receiver = Take(ref _receivers);
                *(T*)receiver->Slot = value;
                Fiber.Wake(receiver, true);
                return;
            }
            if (_async != null && _async.Give(value))
                return;
            if (_count < Capacity)
            {
                Push(value);
                _async?.Available();
                return;
            }
            Fiber.Record* current = Fiber.CurrentRecord;
            if (current != null)
                current->Slot = System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
            Append(ref _senders, current);
            _async?.Available(); // capacity 0: a value waits to be taken
            if (!Fiber.Park("Channel.Send: the channel is full, and only a fiber can wait"))
                Environment.FailFast("Channel.Send: the channel was closed while sending");
        }
    }

    /// <summary>
    /// Receives the next value, waiting while the channel is empty. Returns
    /// false, with the default value, once the channel is closed and empty.
    /// </summary>
    public bool Receive(out T value)
    {
        unsafe
        {
            // SAFETY: as in Send.
            if (_count > 0)
            {
                value = Pop();
                if (_senders != null)
                {
                    // A sender waits for room: its value goes in at the back.
                    Fiber.Record* sender = Take(ref _senders);
                    Push(*(T*)sender->Slot);
                    Fiber.Wake(sender, true);
                }
                else if (_async != null && _async.Take(out T next))
                {
                    Push(next);
                }
                return true;
            }
            if (_senders != null)
            {
                // Unbuffered: take the waiting sender's value.
                Fiber.Record* sender = Take(ref _senders);
                value = *(T*)sender->Slot;
                Fiber.Wake(sender, true);
                return true;
            }
            if (_async != null && _async.Take(out value))
                return true;
            value = default!;
            if (_closed)
                return false;
            Fiber.Record* current = Fiber.CurrentRecord;
            if (current != null)
                current->Slot = System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
            Append(ref _receivers, current);
            return Fiber.Park("Channel.Receive: the channel is empty, and only a fiber can wait");
        }
    }

    /// <summary>
    /// Receive, giving up when <paramref name="cancellationToken"/> is
    /// canceled: false, with the default value, if canceled (before or while
    /// waiting) or once the channel is closed and empty. Look at the token
    /// to tell which.
    /// </summary>
    public bool Receive(out T value, System.Threading.CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            value = default!;
            return false;
        }
        if (_count > 0 || _closed || !cancellationToken.CanBeCanceled)
            return Receive(out value);
        unsafe
        {
            // SAFETY: as in Receive; the registration is removed before the
            // waiter's record and slot can go.
            if (_senders != null || (_async != null && _async.HasSender))
                return Receive(out value);
            Fiber.Record* current = Fiber.CurrentRecord;
            if (current == null)
                Environment.FailFast("Channel.Receive: the channel is empty, and only a fiber can wait");
            value = default!;
            current->Slot = System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
            Append(ref _receivers, current);
            var waiter = new CanceledReceiver(this, current);
            bool received;
            using (cancellationToken.Register(static w => ((CanceledReceiver)w!).Cancel(), waiter))
                received = Fiber.Park("Channel.Receive: the channel is empty, and only a fiber can wait");
            return received;
        }
    }

    // A fiber waiting in Receive(out, token): canceling takes it off the
    // waiters and wakes it with false, unless a sender got to it first.
    sealed class CanceledReceiver(Channel<T> channel, Fiber.Record* fiber)
    {
        public void Cancel()
        {
            if (channel.RemoveReceiver(fiber))
                Fiber.Wake(fiber, false);
        }
    }

    bool RemoveReceiver(Fiber.Record* f)
    {
        unsafe
        {
            // SAFETY: parked fibers' records.
            ref Fiber.Record* link = ref _receivers;
            while (link != null && link != f)
                link = ref link->Next;
            if (link == null)
                return false;
            link = f->Next;
            f->Next = null;
            return true;
        }
    }

    /// <summary>Takes a value if one is ready, without waiting. False when there's none.</summary>
    public bool TryReceive(out T value)
    {
        unsafe
        {
            if (_count > 0 || _senders != null || (_async != null && _async.HasSender))
                return Receive(out value);
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Closes the channel: Receive drains what's left and then returns false,
    /// and fibers waiting to receive wake up with false.
    /// </summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        unsafe
        {
            // SAFETY: as in Send.
            while (_receivers != null)
                Fiber.Wake(Take(ref _receivers), false);
            while (_senders != null)
                Fiber.Wake(Take(ref _senders), false);
        }
        _async?.Closed();
    }

    void Push(T value)
    {
        _items[(_head + _count) % _items.Length] = value;
        _count++;
    }

    T Pop()
    {
        T value = _items[_head];
        _items[_head] = default!;
        _head = (_head + 1) % _items.Length;
        _count--;
        return value;
    }

    // Park stops the program before a null (outside a fiber) is ever followed.
    static void Append(ref Fiber.Record* list, Fiber.Record* f)
    {
        if (f == null)
            return;
        unsafe
        {
            // SAFETY: parked fibers' records.
            f->Next = null;
            ref Fiber.Record* link = ref list;
            while (link != null)
                link = ref link->Next;
            link = f;
        }
    }

    static Fiber.Record* Take(ref Fiber.Record* list)
    {
        unsafe
        {
            // SAFETY: parked fibers' records.
            Fiber.Record* f = list;
            list = f->Next;
            f->Next = null;
            return f;
        }
    }
}

/// <summary>
/// A stream of bytes between fibers, through a buffer of fixed size taken
/// from an arena: Write waits while the buffer is full, Read while it's empty.
/// </summary>
// One ring of bytes, allocated once, so a pipe never holds more than its
// capacity however fast one side runs ahead: the writer waits for the reader.
// Waiters are woken together and check again, rather than being handed
// their bytes as Channel's are: a write can be bigger than the ring, so it
// goes in as room appears, and a woken fiber that finds the room taken by
// another simply waits again. Writes from several fibers can interleave
// where one waits partway through.
public sealed class Pipe
{
    readonly unsafe byte* _buffer;
    int _head, _count;
    bool _closed;
    unsafe Fiber.Record* _readers, _writers;

    /// <summary>
    /// A pipe that holds up to <paramref name="capacity"/> bytes, taken from
    /// <paramref name="arena"/>. The arena must outlive every fiber that uses
    /// the pipe: not a fiber's Arena.Scratch, which goes when that fiber ends.
    /// </summary>
    public Pipe(Arena arena, int capacity)
    {
        if (capacity < 1)
            Environment.FailFast("Pipe: the capacity must be at least 1");
        Capacity = capacity;
        unsafe
        {
            // SAFETY: capacity bytes, alive as long as the arena.
            _buffer = arena.AllocArray<byte>(capacity, 1, false);
            if (_buffer == null)
                Environment.FailFast("Pipe: the arena has no room for the buffer");
        }
    }

    /// <summary>How many bytes the pipe holds before Write waits.</summary>
    public int Capacity { get; }

    /// <summary>Bytes written and not yet read.</summary>
    public int Count => _count;

    /// <summary>Whether Close has been called.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Writes all of <paramref name="bytes"/>, waiting while the pipe is full.
    /// Returns false if the pipe is closed before all of it went in: what did
    /// go in stays readable.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> bytes)
    {
        while (true)
        {
            if (_closed)
                return false;
            int n = Math.Min(bytes.Length, Capacity - _count);
            if (n > 0)
            {
                Put(bytes.Slice(0, n));
                bytes = bytes.Slice(n);
                unsafe { WakeAll(ref _readers, true); } // SAFETY: parked fibers' records
            }
            if (bytes.IsEmpty)
                return true;
            unsafe { Wait(ref _writers, "Pipe.Write: the pipe is full, and only a fiber can wait"); } // SAFETY: as above
        }
    }

    /// <summary>Writes text as UTF-8, as Write(bytes) does.</summary>
    public bool Write(ReadOnlySpan<char> text)
    {
        Span<byte> chunk = stackalloc byte[256];
        int next = 0;
        while (next < text.Length)
        {
            int length = Utf8.Encode(text, ref next, chunk);
            if (!Write((ReadOnlySpan<byte>)chunk.Slice(0, length)))
                return false;
        }
        return true;
    }

    /// <summary>Writes a string as UTF-8, as Write(bytes) does.</summary>
    public bool Write(string text) => Write(text.AsSpan());

    /// <summary>
    /// The pipe as a System.IO.Stream, for code that takes one: Read and Write
    /// wait as the pipe's do, letting the other fibers run. It can't seek.
    /// Writing to a closed pipe ends the program (IOException); disposing the
    /// stream closes the pipe.
    /// </summary>
    public System.IO.Stream AsStream() => new PipeStream(this);

    /// <summary>
    /// Reads up to buffer.Length bytes and returns how many it read, waiting
    /// while the pipe is empty: at least 1, or 0 once the pipe is closed and
    /// empty (or the buffer is empty).
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        while (_count == 0)
        {
            if (_closed)
                return 0;
            unsafe { Wait(ref _readers, "Pipe.Read: the pipe is empty, and only a fiber can wait"); } // SAFETY: parked fibers' records
        }
        int n = Take(buffer);
        unsafe { WakeAll(ref _writers, true); } // SAFETY: as above
        return n;
    }

    /// <summary>
    /// Closes the pipe, from either end: Read drains what's left and then
    /// returns 0, and Write returns false. Waiting fibers wake up.
    /// </summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        unsafe
        {
            // SAFETY: parked fibers' records.
            WakeAll(ref _readers, false);
            WakeAll(ref _writers, false);
        }
    }

    void Put(ReadOnlySpan<byte> bytes)
    {
        int tail = (_head + _count) % Capacity;
        int first = Math.Min(bytes.Length, Capacity - tail);
        unsafe
        {
            // SAFETY: both pieces lie inside the ring's Capacity bytes.
            bytes.Slice(0, first).CopyTo(new Span<byte>(_buffer + tail, first));
            bytes.Slice(first).CopyTo(new Span<byte>(_buffer, bytes.Length - first));
        }
        _count += bytes.Length;
    }

    int Take(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _count);
        int first = Math.Min(n, Capacity - _head);
        unsafe
        {
            // SAFETY: both pieces lie inside the ring's Capacity bytes.
            new ReadOnlySpan<byte>(_buffer + _head, first).CopyTo(buffer);
            new ReadOnlySpan<byte>(_buffer, n - first).CopyTo(buffer.Slice(first));
        }
        _head = (_head + n) % Capacity;
        _count -= n;
        return n;
    }

    // Joins the end of the list, so waiters wake in the order they came.
    // Park stops the program, with the message, outside a fiber.
    static void Wait(ref Fiber.Record* list, string operation)
    {
        Fiber.Record* current = Fiber.CurrentRecord;
        if (current != null)
        {
            unsafe
            {
                // SAFETY: the current fiber's record, parked until woken.
                current->Next = null;
                ref Fiber.Record* link = ref list;
                while (link != null)
                    link = ref link->Next;
                link = current;
            }
        }
        Fiber.Park(operation);
    }

    static void WakeAll(ref Fiber.Record* list, bool woken)
    {
        unsafe
        {
            // SAFETY: parked fibers' records.
            while (list != null)
            {
                Fiber.Record* f = list;
                list = f->Next;
                f->Next = null;
                Fiber.Wake(f, woken);
            }
        }
    }
}

// Pipe.AsStream's stream.
sealed class PipeStream : System.IO.Stream
{
    readonly Pipe _pipe;

    public PipeStream(Pipe pipe) => _pipe = pipe;

    public override bool CanRead => true;
    public override bool CanWrite => !_pipe.IsClosed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException("a pipe has no length");
    public override long Position
    {
        get => throw new NotSupportedException("a pipe has no position");
        set => throw new NotSupportedException("a pipe has no position");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return _pipe.Read(new Span<byte>(buffer, offset, count));
    }

    public override int Read(Span<byte> buffer) => _pipe.Read(buffer);

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_pipe.Write(buffer))
            throw new System.IO.IOException("Pipe: written after it was closed");
    }

    public override void Flush() { }
    public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException("a pipe can't seek");
    public override void SetLength(long value) => throw new NotSupportedException("a pipe has no length");
    protected override void Dispose(bool disposing) => _pipe.Close();
}
