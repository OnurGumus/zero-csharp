// Catch.Run: get back what a `throw` threw, instead of the program ending.
//
//   Exception error = Catch.Run(() => Download(url, path));
//   if (error is IOException or SocketException)
//       Out.Line("couldn't download: ", error.Message);   // try again later
//   else if (error != null)
//       throw error;                                      // not ours: pass it on
//
// Run returns null when the callback returns, and the exception when a
// `throw` anywhere inside it, however deep, comes back here. Rethrowing goes
// to the next Catch.Run out, or with none ends the program, as every `throw`
// did before. Catch.Run calls nest. Each thread and each fiber has its own:
// a throw comes back to a Catch.Run on its own stack, and a Catch.Run around
// Fiber.Run() or Parallel.For catches nothing thrown inside the fibers or
// on the other threads.
//
// It isn't C#'s try/catch: a `throw` jumps straight back here, as C's
// longjmp does (zerocore/Runtime/Catch.cs). Nothing between the throw and
// Run runs on the way: neither catch and finally blocks nor using disposals.
// But Run returns either way, so what comes after it is the finally block:
//
//   Arena.Scope scope;
//   unsafe { scope = arena.OpenScope(); } // SAFETY: nothing from inside is kept
//   using (scope)
//   {
//       Exception error = Catch.Run(ref arena, static (ref Arena a) => Serve(a));
//       ...
//   }   // frees what Serve allocated, thrown or not
//
// For what a callback acquires inside, deep down, where the code around
// can't reach, Run does this after a throw:
//
// - Files and sockets: a File, FileStream or Socket opened inside the
//   callback and still open is closed, at the system's level (fclose,
//   close). Its Dispose doesn't run: the object, if anything still has it,
//   finds itself closed at its next use, and what a FileStream held in its
//   buffer is lost, as when one is never disposed. A callback that returns hands what it left
//   open to the Catch.Run around it, if any. Run can't tell a stream whose
//   using was jumped over from one the callback stored to keep, a log file
//   or a pooled connection: it closes both. Open what should outlive a
//   failure outside any Catch.Run, or take it off the list with Catch.Keep.
// - Arena.Scratch calls jumped out of free their memory.
// - Objects go where they went when Run was called (AllocateObjectsHere
//   scopes opened inside are over).
//
// Arena memory is the code around's to free, as always: what the callback
// allocated in an arena stays until that arena's next Restore or Reset, an
// Arena.OpenScope jumped out of included (restoring an older mark ends it).
// An arena created inside and not destroyed is lost, and so is NativeMemory
// not freed: create and free those around Run, not inside. The exception
// itself is an object like any other: made while objects went to an arena, it
// lives as long as that arena's memory. Made with nothing set, it's never
// freed, like everything `new` makes: a loop that catches and tries again
// should send each attempt's objects to an arena (AllocateObjectsHere, inside
// the callback) and Reset it after Run, or it grows with every failure.
//
// Catch.Run is for failures that come as exceptions, from the world outside:
// a missing file, a dropped connection, text that doesn't parse. A program's
// bugs (an index out of range, a failed cast, a null dereference) still stop
// it, and so does a throw out of a static constructor, out of a
// Parallel.For iteration, or out of a microcontroller's interrupt handler
// (Board.StartTicks) into the code it interrupted.
//
// Async code: each step of an async method runs as a Catch.Run of its own
// (lib/Tasks.cs), so a throw in one faults the method's task, and awaiting
// the task throws again. In async code, Catch.RunAsync awaits and gives the
// exception back.
//
// On WebAssembly nothing can jump back up the stack: there a `throw` still
// ends the program.
//
// To pass data in and out without a closure (which allocates), use the
// overload with a state passed by ref and a static lambda:
//
//   Exception error = Catch.Run(ref job, static (ref Job j) => j.Bytes = Fetch(j.Url));

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>The callback of <see cref="Catch.Run{TState}(ref TState, CatchAction{TState})"/>.</summary>
public delegate void CatchAction<TState>(ref TState state) where TState : allows ref struct;

/// <summary>Runs code and returns what it throws, rather than letting the program end.</summary>
public static class Catch
{
    /// <summary>
    /// Runs <paramref name="body"/>. Returns null if it returns, or the
    /// exception if something inside it throws. Code between the throw and
    /// here doesn't run (no catch or finally blocks, no using disposals);
    /// files and sockets opened inside and left open are closed.
    /// </summary>
    public static Exception Run(Action body)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        unsafe
        {
            // SAFETY: body is a local of this frame, which outlives the call.
            return Invoke(ActionInvoker.Instance, Unsafe.AsPointer(ref body));
        }
    }

    /// <summary>
    /// The same, for a function: <paramref name="result"/> is what it
    /// returned, or default if it threw.
    /// </summary>
    public static Exception Run<TResult>(Func<TResult> body, out TResult result)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        FuncFrame<TResult> frame = default;
        frame.Body = body;
        Exception error;
        unsafe
        {
            // SAFETY: frame is a local of this frame, which outlives the call.
            error = Invoke(FuncInvoker<TResult>.Instance, Unsafe.AsPointer(ref frame));
        }
        result = error == null ? frame.Result : default;
        return error;
    }

    /// <summary>
    /// The same, with a state the callback can read and change, passed by
    /// ref: with a static lambda, nothing is allocated. Changes the callback
    /// made before a throw stay.
    /// </summary>
    public static Exception Run<TState>(ref TState state, CatchAction<TState> body)
        where TState : allows ref struct
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        StateFrame<TState> frame;
        frame.Body = body;
        unsafe
        {
            // SAFETY: state outlives the call (the caller's), and frame is a
            // local of this frame, which does too.
            frame.State = Unsafe.AsPointer(ref state);
            return Invoke(StateInvoker<TState>.Instance, Unsafe.AsPointer(ref frame));
        }
    }

    /// <summary>
    /// Leaves <paramref name="stream"/> open whatever is thrown later: for a
    /// file opened inside a Catch.Run that should outlive a failure there,
    /// such as a log. Closing it is the program's job again. Call it once the
    /// stream is stored where the program will find it: a throw before this
    /// call still closes the file.
    /// </summary>
    public static void Keep(System.IO.FileStream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));
        stream.KeepOpen();
    }

    /// <summary>The same for a File handle, and every copy of it.</summary>
    public static void Keep(File file) => Kept(file.Ticket);

    /// <summary>
    /// The same for a socket, such as a connection made for a pool. For a
    /// TcpClient, a TcpListener or a UdpClient, pass its socket (Client or
    /// Server).
    /// </summary>
    public static void Keep(System.Net.Sockets.Socket socket)
    {
        if (socket == null)
            throw new ArgumentNullException(nameof(socket));
        socket.KeepOpen();
    }

    /// <summary>
    /// Catch.Run for async code: awaits what <paramref name="body"/> returns
    /// and gives back what it threw, before or after any await, or null. A
    /// canceled one gives back its OperationCanceledException. Each step of
    /// an async method runs as a Catch.Run of its own (lib/Tasks.cs), so
    /// what a step opened is closed when it throws.
    /// </summary>
    public static async System.Threading.Tasks.Task<Exception> RunAsync(Func<System.Threading.Tasks.Task> body)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        // A lambda that isn't async can throw before it has a task.
        Exception thrown = Run(body, out System.Threading.Tasks.Task task);
        if (thrown != null)
            return thrown;
        await task.ConfigureAwait(System.Threading.Tasks.ConfigureAwaitOptions.SuppressThrowing);
        return task.Failure;
    }

    // For lib/Tasks.cs: one step of an async method (its state machine's
    // MoveNext) as a Catch.Run. Returns what the step threw, or null.
    internal static Exception RunStep<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine
    {
        unsafe
        {
            // SAFETY: the state machine is the caller's (on its stack, or in
            // a box it holds), which outlives the call.
            return Invoke(StepInvoker<TStateMachine>.Instance, Unsafe.AsPointer(ref stateMachine));
        }
    }

    // ---- how ------------------------------------------------------------------

    // ZeroCatchCall calls a plain function with one pointer: Trampoline, with
    // a Call, which says which Invoker to run on which frame. A virtual call
    // rather than a pointer to a generic method: on the C64 such a pointer to
    // a method shared between types is a "fat" one that C can't call (see
    // Fibers.cs's Runner).
    struct Call
    {
        public Invoker Invoker;
        public unsafe void* Frame;
    }

    abstract class Invoker
    {
        public abstract unsafe void Invoke(void* frame);
    }

    static unsafe void Trampoline(void* argument)
    {
        unsafe
        {
            // SAFETY: Invoke passes a Call of its own frame.
            ref Call call = ref Unsafe.AsRef<Call>(argument);
            call.Invoker.Invoke(call.Frame);
        }
    }

    sealed class ActionInvoker : Invoker
    {
        public static readonly ActionInvoker Instance = new ActionInvoker();

        public override unsafe void Invoke(void* frame)
        {
            unsafe { Unsafe.AsRef<Action>(frame)(); } // SAFETY: Run(Action) passes its body's address.
        }
    }

    struct FuncFrame<TResult>
    {
        public Func<TResult> Body;
        public TResult Result;
    }

    sealed class FuncInvoker<TResult> : Invoker
    {
        public static readonly FuncInvoker<TResult> Instance = new FuncInvoker<TResult>();

        public override unsafe void Invoke(void* frame)
        {
            unsafe
            {
                // SAFETY: Run(Func) passes its FuncFrame's address.
                ref FuncFrame<TResult> f = ref Unsafe.AsRef<FuncFrame<TResult>>(frame);
                f.Result = f.Body();
            }
        }
    }

    sealed class StepInvoker<TStateMachine> : Invoker where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine
    {
        public static readonly StepInvoker<TStateMachine> Instance = new StepInvoker<TStateMachine>();

        public override unsafe void Invoke(void* frame)
        {
            unsafe { Unsafe.AsRef<TStateMachine>(frame).MoveNext(); } // SAFETY: RunStep passes its state machine's address.
        }
    }

    // A pointer rather than a ref field, which can't refer to a ref struct.
    struct StateFrame<TState> where TState : allows ref struct
    {
        public unsafe void* State;
        public CatchAction<TState> Body;
    }

    sealed class StateInvoker<TState> : Invoker where TState : allows ref struct
    {
        public static readonly StateInvoker<TState> Instance = new StateInvoker<TState>();

        public override unsafe void Invoke(void* frame)
        {
            unsafe
            {
                // SAFETY: Run(ref TState) passes its StateFrame's address,
                // whose State is the caller's state.
                ref StateFrame<TState> f = ref Unsafe.AsRef<StateFrame<TState>>(frame);
                f.Body(ref Unsafe.AsRef<TState>(f.State));
            }
        }
    }

    // Runs invoker.Invoke(frame) under a fresh mark, and after a throw puts
    // back what the code jumped over would have. To this method,
    // CatchFrames.Run is a call like any other, however it returns: the
    // registers and its own locals are as a call leaves them.
    static unsafe Exception Invoke(Invoker invoker, void* frame)
    {
        unsafe
        {
            // SAFETY: call and mark are locals of this frame, which outlives
            // CatchFrames.Run. mark.Handles is this Run's list of OpenHandle,
            // which only the functions below touch.
            if (Tracker.s_instance == null)
                Tracker.s_instance = RunTracker.Instance;
            Call call;
            call.Invoker = invoker;
            call.Frame = frame;
            CatchFrame mark;
            mark.Saved = null;
            mark.Handles = null;
            mark.Inner = false;
            ObjectAllocator.Get(out delegate*<void*, nuint, void*> allocate, out void* context);
            // Null until a thread calls Arena.Scratch (Scratch.cs).
            ScratchForCatch scratchRules = ScratchForCatch.s_instance;
            Arena.Mark scratchMark = default;
            int scratchDepth = 0;
            void* scratch = scratchRules != null ? scratchRules.Before(out scratchMark, out scratchDepth) : null;

            CatchFrames.Run(&mark, &Trampoline, &call);

            Exception error = mark.Exception;
            if (error == null)
            {
                HandOver(&mark);
                return null;
            }
            ObjectAllocator.Get(out delegate*<void*, nuint, void*> allocateNow, out void* contextNow);
            if (allocateNow != allocate || contextNow != context)
                ObjectAllocator.Set(allocate, context);
            CloseAll(&mark);
            // Again: the callback may have been the first to call Scratch.
            scratchRules = ScratchForCatch.s_instance;
            if (scratchRules != null)
                scratchRules.After(scratch, scratchMark, scratchDepth);
            return error;
        }
    }

    // ---- files and sockets ------------------------------------------------------
    //
    // A File (a FileStream has one) or Socket opened inside a Catch.Run has a
    // record here until it's closed: the system's handle and the function
    // that closes it, in a list of the innermost Catch.Run's. After a throw,
    // Run closes the handles in its list.
    //
    // The records don't point at the FileStream or Socket, on purpose: that
    // object's memory may be an arena's, which the program can free at any
    // time, open stream or not (see FileStream). So a record is plain memory
    // from calloc, never given back to the system, only reused, and the
    // object finds out by itself: it keeps a Ticket, the record's address
    // and how many times the record had been used, and ClosedByThrow says
    // whether the record has moved on since.
    //
    // Another thread than the one that opened a file may close it, so every
    // change to a list is made under one lock (a spin lock, held for a few
    // pointer writes), and so is the decision of who closes a handle: its
    // owner asks first (Closing), and a Catch.Run takes the ones it's about
    // to close the same way, so each handle is closed once. What the lock
    // can't cover is a thread using a file while another's Catch.Run closes
    // it: what goes to another thread should be kept (Catch.Keep), or opened
    // there.

    internal struct OpenHandle
    {
        public unsafe OpenHandle* Next;             // in the list, newest first (or the next spare one)
        public unsafe OpenHandle* Previous;
        public unsafe CatchFrame* Frame;            // the Catch.Run whose list it's in; null: none
        public unsafe delegate*<nint, void> Close;  // closes Value
        public nint Value;                   // the FILE*, or the socket's number
        public uint Uses;                    // goes up each time the record is given up
        public int Busy;                     // operations on another thread using Value (FileAsync.cs)
        public bool CloseLater;              // a Catch.Run took it while Busy: the last of them closes it
    }

    /// <summary>What a File or Socket keeps of its record, from Opened.</summary>
    internal struct Ticket
    {
        internal unsafe OpenHandle* Handle;  // null: opened outside any Catch.Run
        internal uint Uses;
    }

    static unsafe OpenHandle* s_spare;
    static int s_lock;

    [DllImport("libc")] static unsafe extern void* calloc(nuint count, nuint size);

    // Making and giving up a record are virtual methods of an object that
    // exists once a Catch.Run has run, called by File and Socket: the
    // compiler leaves a virtual method out of a program unless something
    // creates its class and something calls it, so only a program that both
    // catches and opens files or sockets has this code (see ScratchForCatch,
    // in Scratch.cs, for the same the other way round).
    internal abstract class Tracker
    {
        // Null until the first Catch.Run.
        internal static Tracker s_instance;

        internal abstract unsafe Ticket Track(nint value, delegate*<nint, void> close);
        internal abstract bool Claim(Ticket ticket);
        internal abstract void Unlist(Ticket ticket);
    }

    sealed class RunTracker : Tracker
    {
        internal static readonly RunTracker Instance = new RunTracker();

        internal override unsafe Ticket Track(nint value, delegate*<nint, void> close)
        {
            unsafe { return Catch.Track(value, close); } // SAFETY: Opened's caller's
        }

        internal override bool Claim(Ticket ticket) => Catch.Claim(ticket);

        internal override void Unlist(Ticket ticket) => Catch.Unlist(ticket);
    }

    /// <summary>
    /// For File and Socket, once open: a ticket to keep, for ClosedByThrow
    /// and Closing.
    /// </summary>
    internal static unsafe Ticket Opened(nint value, delegate*<nint, void> close)
    {
        Tracker tracker = Tracker.s_instance;
        unsafe
        {
            // SAFETY: close closes value, as Track needs.
            return tracker == null ? default : tracker.Track(value, close);
        }
    }

    static unsafe Ticket Track(nint value, delegate*<nint, void> close)
    {
        unsafe
        {
            // SAFETY: the marks are in frames still running on this stack,
            // and Invoke empties a mark's list before its frame returns.
            // Records are calloc's, and never freed.
            CatchFrame* frame = Listing(CatchFrames.Innermost);
            if (frame == null)
                return default;
            Lock();
            OpenHandle* handle = s_spare;
            if (handle != null)
                s_spare = handle->Next;
            Unlock();
            if (handle == null)
                handle = (OpenHandle*)calloc(1, (nuint)sizeof(OpenHandle));
            if (handle == null)
                return default; // out of memory: this one goes unlisted
            handle->Close = close;
            handle->Value = value;
            Lock();
            handle->Frame = frame;
            handle->Previous = null;
            handle->Next = (OpenHandle*)frame->Handles;
            if (handle->Next != null)
                handle->Next->Previous = handle;
            frame->Handles = handle;
            Ticket ticket = new Ticket { Handle = handle, Uses = handle->Uses };
            Unlock();
            return ticket;
        }
    }

    // The first mark from frame outwards that keeps a list: zerocore's own
    // marks (Lazy's) don't.
    static unsafe CatchFrame* Listing(CatchFrame* frame)
    {
        unsafe
        {
            // SAFETY: a chain of marks in running frames.
            while (frame != null && frame->Inner)
                frame = frame->Outer;
            return frame;
        }
    }

    /// <summary>
    /// Whether a Catch.Run closed the handle after a throw (or is about
    /// to): the owner must forget it, not close it again.
    /// </summary>
    internal static bool ClosedByThrow(Ticket ticket)
    {
        unsafe
        {
            // SAFETY: a record is never freed, so a ticket can always read it.
            return ticket.Handle != null && ticket.Handle->Uses != ticket.Uses;
        }
    }

    /// <summary>
    /// For the owner, about to close the handle itself: whether it is the
    /// owner's to close. True for a handle with no record, and for one this
    /// call took off its list; false when a Catch.Run has taken it to close.
    /// </summary>
    internal static bool Closing(Ticket ticket)
    {
        unsafe
        {
            // SAFETY: a ticket with a record came from a Tracker, so there is one.
            return ticket.Handle == null || Tracker.s_instance.Claim(ticket);
        }
    }

    static bool Claim(Ticket ticket)
    {
        unsafe
        {
            // SAFETY: under the lock, a record with this ticket's Uses is
            // this owner's, in the list its Frame says or in none.
            OpenHandle* handle = ticket.Handle;
            Lock();
            bool mine = handle->Uses == ticket.Uses;
            if (mine)
            {
                Unlink(handle);
                handle->Uses++;
                handle->Busy = 0;
                handle->Next = s_spare;
                s_spare = handle;
            }
            Unlock();
            return mine;
        }
    }

    /// <summary>
    /// For Catch.Keep: no Catch.Run closes the handle from here on. The
    /// record stays, out of every list, until its owner closes: tickets to
    /// it (a File's copies have several) go on matching.
    /// </summary>
    internal static void Kept(Ticket ticket)
    {
        unsafe
        {
            // SAFETY: a ticket with a record came from a Tracker, so there is one.
            if (ticket.Handle != null)
                Tracker.s_instance.Unlist(ticket);
        }
    }

    static void Unlist(Ticket ticket)
    {
        unsafe
        {
            // SAFETY: as in Claim.
            OpenHandle* handle = ticket.Handle;
            Lock();
            if (handle->Uses == ticket.Uses)
                Unlink(handle);
            Unlock();
        }
    }

    /// <summary>
    /// For an operation that hands the handle to another thread (FileAsync.cs):
    /// counts it as in use, so that a Catch.Run closing it after a throw
    /// meanwhile leaves the closing to DoneUsing. False if a Catch.Run has
    /// closed it already.
    /// </summary>
    internal static bool Using(Ticket ticket)
    {
        unsafe
        {
            // SAFETY: a record is never freed; changed under the lock.
            OpenHandle* handle = ticket.Handle;
            if (handle == null)
                return true;
            Lock();
            bool open = handle->Uses == ticket.Uses;
            if (open)
                handle->Busy++;
            Unlock();
            return open;
        }
    }

    /// <summary>The operation is over: closes the handle if a Catch.Run took it meanwhile.</summary>
    internal static void DoneUsing(Ticket ticket)
    {
        unsafe
        {
            // SAFETY: as in Using. A record taken while busy stays out of the
            // spare list until this closes it, so the pointer is still its.
            OpenHandle* handle = ticket.Handle;
            if (handle == null)
                return;
            Lock();
            handle->Busy--;
            bool closeNow = handle->CloseLater && handle->Busy == 0;
            if (closeNow)
                handle->CloseLater = false;
            Unlock();
            if (!closeNow)
                return;
            handle->Close(handle->Value);
            Lock();
            handle->Next = s_spare;
            s_spare = handle;
            Unlock();
        }
    }

    // Takes the record out of the list it's in, if any. Under the lock.
    static unsafe void Unlink(OpenHandle* handle)
    {
        unsafe
        {
            // SAFETY: the caller holds the lock; a Frame that isn't null is a
            // running Catch.Run's mark.
            if (handle->Frame != null)
            {
                if (handle->Previous != null)
                    handle->Previous->Next = handle->Next;
                else
                    handle->Frame->Handles = handle->Next;
                if (handle->Next != null)
                    handle->Next->Previous = handle->Previous;
            }
            handle->Frame = null;
            handle->Previous = handle->Next = null;
        }
    }

    static void Lock()
    {
        while (Interlocked.CompareExchange(ref s_lock, 1, 0) != 0)
        {
            // another thread is changing a list
        }
    }

    static void Unlock() => Volatile.Write(ref s_lock, 0);

    // The callback returned: what it opened and left open is the Catch.Run
    // around's to close now, if there is one. With none, the records stay
    // out of any list until their owners close.
    static unsafe void HandOver(CatchFrame* mark)
    {
        unsafe
        {
            // SAFETY: mark is the returned Run's, the outer mark is in a
            // frame still running, and the lists are changed under the lock.
            // (Only this thread adds to mark's list, so an empty one stays
            // empty: no need for the lock to see that.)
            if (mark->Handles == null)
                return;
            CatchFrame* outer = Listing(CatchFrames.Innermost);
            Lock();
            OpenHandle* opened = (OpenHandle*)mark->Handles;
            mark->Handles = null;
            OpenHandle* last = null;
            for (OpenHandle* handle = opened; handle != null; handle = handle->Next)
            {
                handle->Frame = outer;
                last = handle;
            }
            if (outer == null)
            {
                while (opened != null)
                {
                    OpenHandle* next = opened->Next;
                    opened->Previous = opened->Next = null;
                    opened = next;
                }
            }
            else if (last != null)
            {
                OpenHandle* outerFirst = (OpenHandle*)outer->Handles;
                last->Next = outerFirst;
                if (outerFirst != null)
                    outerFirst->Previous = last;
                outer->Handles = opened;
            }
            Unlock();
        }
    }

    // Something threw: close what's still open, newest first. The records
    // are taken under the lock (their tickets stop matching there, so an
    // owner closing at the same moment leaves its handle to this), and the
    // handles closed outside it.
    static unsafe void CloseAll(CatchFrame* mark)
    {
        unsafe
        {
            // SAFETY: mark is the Run's that caught; each record holds an
            // open handle and its own closing function, and once taken is
            // only this function's.
            if (mark->Handles == null)
                return;
            Lock();
            OpenHandle* taken = (OpenHandle*)mark->Handles;
            mark->Handles = null;
            for (OpenHandle* handle = taken; handle != null; handle = handle->Next)
            {
                handle->Frame = null;
                handle->Previous = null;
                handle->Uses++;
            }
            Unlock();
            while (taken != null)
            {
                OpenHandle* next = taken->Next;
                Lock();
                // In use on another thread: the last operation closes it.
                bool busy = taken->Busy > 0;
                if (busy)
                    taken->CloseLater = true;
                Unlock();
                if (!busy)
                {
                    taken->Close(taken->Value);
                    Lock();
                    taken->Next = s_spare;
                    s_spare = taken;
                    Unlock();
                }
                taken = next;
            }
        }
    }
}
