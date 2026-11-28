// Fibers on real .NET. Zero mode switches stacks (zerocore/native's
// ZeroFiberSwitch and ZeroFiberInit); under real .NET's GC that can't be done:
// it walks each thread's stack, and wouldn't find a fiber's. So here each
// fiber is a thread, and they take turns: a switch wakes the next one and
// waits. Only one runs at a time, in the order lib/Fibers.cs's scheduler
// chooses, as in zero mode, and everything above these functions (the
// scheduler, Channel, Pipe, Sleep) is lib/'s own code, unchanged.
//
// What differs: a switch costs a thread handoff (microseconds, not
// nanoseconds), a fiber's stack is a thread's (the size Spawn was given is
// only a minimum, and the guard page and overflow check of lib/Fibers.cs
// watch memory nothing uses), and a fiber that blocks in the system (a
// BCL socket's Receive, Thread.Sleep) blocks them all: the BCL doesn't know
// to switch. Waits inside .NET itself do switch: a lock another fiber holds,
// SemaphoreSlim.Wait, Task.Wait, a wait handle (see ZeroContext in Async.cs).
using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

public static partial class Fiber
{
    // A thread's turn. lib/Fibers.cs keeps a GC handle to it where zero mode
    // keeps a saved stack pointer.
    sealed class Turn
    {
        // Spins a little before sleeping: fibers that pass the turn back and
        // forth find each other still awake, which is 50 times faster.
        readonly ManualResetEventSlim _go = new ManualResetEventSlim(false, spinCount: 400);
        public void Give() => _go.Set();
        public void Wait()
        {
            t_waitingForTurn = true;
            _go.Wait();
            t_waitingForTurn = false;
            _go.Reset();
        }
        public nint Handle;
        public Carried Carried;
        public bool IsFiber;
    }

    // What zero mode's fibers share because they are one thread: the
    // thread-local values of lib/ (Arena.Scratch's, behind a pthread key, and
    // where objects go). lib/Fibers.cs swaps each fiber's in and out of them
    // around a switch, so they travel with the turn: the thread giving it up
    // leaves them here, and the one taking it picks them up.
    sealed class Carried
    {
        public readonly nint[] Values = new nint[Platform.MaxKeys + 2];
        public object[] Objects; // lib/'s [ThreadStatic] fields (ThreadStatics)
        public long[] Numbers;

#if ZERO_DOTNET_BCL
        // Async methods' continuations waiting to run on these threads, and
        // the context that brings them here (Async.cs).
        public readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object)> Posted = new();
        public readonly AutoResetEvent Signal = new AutoResetEvent(false);
        public volatile bool EverPosted;
        public ZeroContext Context;
#endif
    }

    [ThreadStatic] static Turn t_turn;
    [ThreadStatic] static bool t_waitingForTurn;

    static Turn Mine
    {
        get
        {
            Turn turn = t_turn;
            if (turn == null)
            {
                turn = new Turn { Carried = new Carried() };
                turn.Handle = GCHandle.ToIntPtr(GCHandle.Alloc(turn));
                t_turn = turn;
#if ZERO_DOTNET_BCL
                turn.Carried.Context = new ZeroContext(turn.Carried);
                if (SynchronizationContext.Current == null)
        #if ZERO_DOTNET_BCL
            SynchronizationContext.SetSynchronizationContext(turn.Carried.Context);
#endif
#endif
            }
            return turn;
        }
    }

    static Turn TurnOf(nint handle)
    {
        unsafe { return (Turn)GCHandle.FromIntPtr(handle).Target; } // SAFETY: a handle Mine or ZeroFiberInit made
    }

    static unsafe void* ZeroFiberInit(void* top, void* bottom, delegate*<void*, void> entry, void* argument)
    {
        Turn turn = new Turn { Carried = Mine.Carried, IsFiber = true };
        turn.Handle = GCHandle.ToIntPtr(GCHandle.Alloc(turn));
        nint entryAddress = (nint)entry, argumentAddress = (nint)argument, scheduler = (nint)t_scheduler;
        var thread = new Thread(() =>
        {
            t_turn = turn;
#if ZERO_DOTNET_BCL
            SynchronizationContext.SetSynchronizationContext(turn.Carried.Context);
#endif
            unsafe { t_scheduler = (Scheduler*)scheduler; } // SAFETY: the spawning thread's, which outlives its fibers
            turn.Wait();
            TakeCarried(turn.Carried);
            unsafe { ((delegate*<void*, void>)entryAddress)((void*)argumentAddress); } // SAFETY: lib/Fibers.cs's Entry and its record
        });
        thread.IsBackground = true; // a program may end with fibers still waiting
        thread.Name = "fiber";
        thread.Start();
        return (void*)turn.Handle;
    }

    static unsafe void ZeroFiberSwitch(void** save, void* resume)
    {
        Turn mine = Mine;
        unsafe { *save = (void*)mine.Handle; } // SAFETY: the caller's field for this fiber's (or Run's) saved stack
        LeaveCarried(mine.Carried);
        TurnOf((nint)resume).Give();
        mine.Wait();
        TakeCarried(mine.Carried);
    }

    // A fiber's last switch: its thread hands the turn on and ends.
    static unsafe void ZeroFiberEnd(void* resume)
    {
        Turn mine = Mine;
        LeaveCarried(mine.Carried);
        unsafe { GCHandle.FromIntPtr(mine.Handle).Free(); } // SAFETY: made by ZeroFiberInit, and not used again
        TurnOf((nint)resume).Give();
    }

    // The thread zero mode would be: what a fiber's thread and the thread
    // that runs it have in common, for lib/'s "is this the thread that made
    // it" checks (a lock's owner, a TaskCompletionSource's).
    internal static nint ThreadIdentity
    {
        get { unsafe { return (nint)This; } } // SAFETY: only compared, never read through
    }

    // lib/'s [ThreadStatic] fields: in zero mode the fibers of a thread share
    // them, being that thread (the scheduler's state, the queue of
    // continuations in lib/Tasks.cs, the timers due). Here they travel with
    // the turn like the pthread keys' values: one generated method copies
    // them all out of the thread giving the turn up, another into the thread
    // taking it. Not this file's own (each thread's turn), nor the marks of
    // Catch.Run, which each fiber has for itself in zero mode too.
    static class ThreadStatics
    {
        static readonly string[] s_perFiber = { "t_turn", "t_waitingForTurn", "t_plainWait", "t_slice", "t_innermost", "t_allocate", "t_context" };

        public static readonly int Count;
        public static readonly Action<object[], long[]> Save, Restore;

        static ThreadStatics()
        {
            var fields = new System.Collections.Generic.List<System.Reflection.FieldInfo>();
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (Type type in typeof(Fiber).Assembly.GetTypes())
            {
                if (type.ContainsGenericParameters)
                    continue;
                foreach (var field in type.GetFields(all))
                    if (field.IsDefined(typeof(ThreadStaticAttribute), false) && Array.IndexOf(s_perFiber, field.Name) < 0)
                        fields.Add(field);
            }
            Count = fields.Count;
            Save = Build(fields, save: true);
            Restore = Build(fields, save: false);
        }

        static Action<object[], long[]> Build(System.Collections.Generic.List<System.Reflection.FieldInfo> fields, bool save)
        {
            var method = new System.Reflection.Emit.DynamicMethod(save ? "SaveThreadStatics" : "RestoreThreadStatics", typeof(void),
                new[] { typeof(object[]), typeof(long[]) }, typeof(Fiber).Module, skipVisibility: true);
            var il = method.GetILGenerator();
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                Type type = field.FieldType;
                bool reference = !type.IsValueType && !type.IsPointer && !type.IsFunctionPointer;
                if (!reference && !(type.IsPointer || type.IsFunctionPointer || type.IsPrimitive || type.IsEnum))
                    Environment.FailFast("ZeroCompat: can't carry the [ThreadStatic] " + field.DeclaringType + "." + field.Name + " between fibers' threads");
                il.Emit(reference ? System.Reflection.Emit.OpCodes.Ldarg_0 : System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, i);
                if (save)
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, field);
                    if (reference)
                        il.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);
                    else
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Conv_I8);
                        il.Emit(System.Reflection.Emit.OpCodes.Stelem_I8);
                    }
                }
                else if (reference)
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Ldelem_Ref);
                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, type);
                    il.Emit(System.Reflection.Emit.OpCodes.Stsfld, field);
                }
                else
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Ldelem_I8);
                    // Pointers and native ints take conv.i; the smaller types are truncated by the store.
                    il.Emit(type.IsPointer || type.IsFunctionPointer || type == typeof(nint) || type == typeof(nuint)
                        ? System.Reflection.Emit.OpCodes.Conv_I
                        : type == typeof(long) || type == typeof(ulong) ? System.Reflection.Emit.OpCodes.Nop : System.Reflection.Emit.OpCodes.Conv_I4);
                    il.Emit(System.Reflection.Emit.OpCodes.Stsfld, field);
                }
            }
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return method.CreateDelegate<Action<object[], long[]>>();
        }
    }

    static void LeaveCarried(Carried carried)
    {
        ThreadStatics.Save(carried.Objects ??= new object[ThreadStatics.Count], carried.Numbers ??= new long[ThreadStatics.Count]);
        nint[] values = carried.Values;
        int keys = Platform.KeyCount;
        unsafe
        {
            // SAFETY: keys Platform made. Cleared here, so that this thread's
            // end doesn't run a key's destructor on what the others still use.
            for (int i = 0; i < keys; i++)
            {
                values[i] = (nint)Platform.pthread_getspecific(Platform.Key(i));
                Platform.pthread_setspecific(Platform.Key(i), null);
            }
            ObjectAllocator.Get(out var allocate, out void* context);
            values[Platform.MaxKeys] = (nint)allocate;
            values[Platform.MaxKeys + 1] = (nint)context;
        }
    }

    static void TakeCarried(Carried carried)
    {
        ThreadStatics.Restore(carried.Objects, carried.Numbers);
        nint[] values = carried.Values;
        int keys = Platform.KeyCount;
        unsafe
        {
            // SAFETY: what LeaveCarried read on the thread that had the turn.
            for (int i = 0; i < keys; i++)
                Platform.pthread_setspecific(Platform.Key(i), (void*)values[i]);
            ObjectAllocator.Set((delegate*<void*, nuint, void*>)values[Platform.MaxKeys], (void*)values[Platform.MaxKeys + 1]);
        }
    }

    // The body and state of a fiber, which zero mode keeps in the fiber's
    // own memory (see Record.Body in lib/Fibers.cs).
    sealed class Start<TState>(Action<TState> body, TState state)
    {
        public readonly Action<TState> Body = body;
        public readonly TState State = state;
    }

    // An async operation's record for the scheduler's list (lib/SocketsAsync.cs
    // and the like keep theirs inside the object in zero mode): native
    // memory, which doesn't move.
    internal static nint NewRecord()
    {
        unsafe { return (nint)NativeMemory.AllocZeroed((nuint)sizeof(Record)); } // SAFETY: freed by FreeRecord
    }

    internal static void FreeRecord(nint memory)
    {
        unsafe
        {
            // SAFETY: from NewRecord, and off every list.
            Record* record = (Record*)memory;
            record->Body = null;
            record->Run = null;
            NativeMemory.Free(record);
        }
    }

    internal static object Held(nint handle)
    {
        if (handle == 0)
            return null;
        unsafe { return GCHandle.FromIntPtr(handle).Target; } // SAFETY: a handle Hold made
    }

    internal static void Hold(ref nint handle, object value)
    {
        if (handle != 0)
        {
            unsafe { GCHandle.FromIntPtr(handle).Free(); } // SAFETY: a handle Hold made, freed once
            handle = 0;
        }
        if (value != null)
            handle = GCHandle.ToIntPtr(GCHandle.Alloc(value));
    }
}
