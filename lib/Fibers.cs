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
// Rules:
// - Yield, Sleep, Send and Receive block only inside a fiber. Outside one
//   (in Main, before or after Run), Yield and Sleep return at once, and a
//   Send or Receive that would wait stops the program.
// - When every fiber left is waiting on a channel, none can ever run again:
//   Run stops the program with a message saying so.
// - Linux (x64, arm64), macOS (arm64), Windows (x64), UEFI (x64, arm64) and
//   Cortex-M microcontrollers. UEFI and microcontrollers have no guard page:
//   an overflow is only noticed when the fiber next switches back, by the
//   pattern check, and one that goes past the (unprotected) guard page may
//   already have overwritten other memory. The Pico 2's Cortex-M33 stops the
//   machine stack in hardware (arm/runtime.c).

using System;
using System.Runtime.InteropServices;

/// <summary>Green threads: functions that take turns on one system thread, each on its own stack.</summary>
public static class Fiber
{
    [DllImport("*"), SuppressGCTransition]
    static unsafe extern void ZeroFiberSwitch(void** save, void* resume);

    [DllImport("*"), SuppressGCTransition]
    static unsafe extern void* ZeroFiberInit(void* top, void* bottom, delegate* unmanaged<void*, void> entry, void* argument);

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

    // A small machine without guard pages (a microcontroller: arm/runtime.c;
    // DOS: dos/runtime.c) reports pages smaller than 4 KB, and gets stacks
    // sized for its RAM.
    static bool SmallMachine => getpagesize() < 4096;

    /// <summary>
    /// The stack size Spawn gives a fiber unless told otherwise: 256 KB, or
    /// 4 KB on DOS and microcontrollers.
    /// </summary>
    public static int DefaultStackSize => SmallMachine ? 4 * 1024 : 256 * 1024;

    /// <summary>
    /// The smallest stack Spawn gives a fiber, whatever it's asked for: 16 KB,
    /// or 1 KB on DOS and microcontrollers.
    /// </summary>
    public static int MinStackSize => SmallMachine ? 1024 : 16 * 1024;

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
        public long WakeAt;         // Clock.Monotonic, while sleeping
        public delegate*<Record*, void> Run; // RunBody<TState>'s instantiation
        public object Body;         // the Action<TState>
        public void* State;         // where the TState is
        public delegate*<void*, nuint, void*> Allocate; // its ObjectAllocator setting
        public void* AllocateContext;
        public void* Scratch;       // its Arena.Scratch state, while switched out
        public int Id;
        public bool Done;
        public bool Woken;          // by a channel, as opposed to closed
        public void* Slot;          // while waiting on a channel: the T to send, or to receive into
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
    }

    [ThreadStatic] static Scheduler* t_scheduler;

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
    public static int Spawn<TState>(TState state, Action<TState> body, int stackSize = 0)
    {
        Scheduler* s = This;
        unsafe
        {
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
            f->Run = &RunBody<TState>;
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
    public static int Count { get { unsafe { return This->Live; } } } // SAFETY: the thread's scheduler

    /// <summary>How many times this thread has switched to a fiber: for measuring.</summary>
    public static long Switches { get { unsafe { return This->Switches; } } } // SAFETY: the thread's scheduler

    /// <summary>
    /// Runs the fibers spawned on this thread, and the ones they spawn, until
    /// all have ended.
    /// </summary>
    public static void Run()
    {
        Scheduler* s = This;
        unsafe
        {
            if (s->Current != null)
                Environment.FailFast("Fiber.Run: called from a fiber");
            // SAFETY: every record in the queues is a live fiber's; a fiber's
            // memory is freed here, on Run's own stack, after it has ended.
            while (s->Live > 0)
            {
                Record* f = Dequeue(s);
                if (f == null)
                {
                    if (s->Sleepers == null)
                        Environment.FailFast("Fiber.Run: every fiber is waiting on a channel, so none can run again");
                    WakeSleepers(s, wait: true);
                    continue;
                }

                System.Runtime.ObjectAllocator.Get(out var mainAllocate, out void* mainContext);
                if (f->Allocate != mainAllocate || f->AllocateContext != mainContext)
                    System.Runtime.ObjectAllocator.Set(f->Allocate, f->AllocateContext);
                void* mainScratch = Arena.SwapScratch(f->Scratch);
                s->Current = f;
                s->Switches++;
                ZeroFiberSwitch(&s->MainStack, f->StackPointer);
                s->Current = null;
                for (int i = 0; i < CanaryWords; i++)
                    if (f->Bottom[i] != Canary)
                        StackOverflowed(f);
                f->Scratch = Arena.SwapScratch(mainScratch);
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
            }
        }
    }

    static void StackOverflowed(Record* f)
    {
        int id;
        unsafe { id = f->Id; } // SAFETY: the record of the fiber that just switched back
        Environment.FailFast("Fiber " + id.ToString() + " overflowed its stack: give it a bigger one (Spawn's stackSize)");
    }

    /// <summary>Lets the other fibers that are ready run before this one carries on.</summary>
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
                usleep((uint)((s->Sleepers->WakeAt - now + 999) / 1000));
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
    internal static bool Park(string operation)
    {
        Scheduler* s = This;
        Record* current;
        unsafe { current = s->Current; } // SAFETY: the thread's scheduler
        if (current == null)
            Environment.FailFast(operation);
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
    [UnmanagedCallersOnly]
    static void Entry(void* record)
    {
        unsafe
        {
            // SAFETY: Spawn's record, alive until Run frees it after this switch.
            Record* f = (Record*)record;
            f->Run(f);
            f->Done = true;
            f->Body = null;
            Scheduler* s = This;
            s->Live--;
            ZeroFiberSwitch(&f->StackPointer, s->MainStack);
        }
    }

    static void RunBody<TState>(Record* f)
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

/// <summary>
/// A queue that fibers pass values through: Send waits while it's full,
/// Receive while it's empty. Capacity 0 makes every Send wait for a Receive.
/// </summary>
public sealed class Channel<T>
{
    readonly T[] _items;
    int _head, _count;
    bool _closed;
    // Fibers waiting, in order. Each one's Slot points into its own stack: the
    // value a sender waits to send, or the variable a receiver waits to fill.
    // A value moves straight into or out of a slot, so a woken fiber finds its
    // work done and nothing can take its place in the meantime.
    unsafe Fiber.Record* _senders, _receivers;

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
            if (_count < Capacity)
            {
                Push(value);
                return;
            }
            Fiber.Record* current = Fiber.CurrentRecord;
            if (current != null)
                current->Slot = System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
            Append(ref _senders, current);
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

    /// <summary>Takes a value if one is ready, without waiting. False when there's none.</summary>
    public bool TryReceive(out T value)
    {
        unsafe
        {
            if (_count > 0 || _senders != null)
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
