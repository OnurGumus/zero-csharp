// What lib/ takes from zerocore (its internals: lib/ is the one assembly
// allowed to see them), for real .NET.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

static class ZeroCoreExtensions
{
    extension(string)
    {
        // zerocore: a string of `length` zero characters, for the caller to
        // fill in through FirstChar. Fine on .NET too for a string nobody else
        // has seen yet (never "", which is shared, but then nothing is written).
        internal static string Allocate(int length) => length == 0 ? "" : new string('\0', length);
        internal static string FromChars(ReadOnlySpan<char> value) => new string(value);
    }

    extension(string text)
    {
        internal ref char FirstChar
        {
            get { unsafe { return ref Unsafe.AsRef(in text.GetPinnableReference()); } } // SAFETY: only for strings from Allocate, not yet seen
        }
    }

    // lib/Out.cs keeps its buffer and hands zerocore pointers to it, which
    // zerocore writes out when the program ends. Here a ProcessExit handler
    // does that.
    extension(Environment)
    {
        internal static unsafe byte* s_outBuffer
        {
            get { unsafe { return (byte*)ZeroExit.OutBuffer; } }
            set { unsafe { ZeroExit.OutBuffer = (nint)value; } }
        }
        internal static unsafe int* s_outLength
        {
            get { unsafe { return (int*)ZeroExit.OutLength; } }
            set { unsafe { ZeroExit.OutLength = (nint)value; } ZeroExit.Hook(); }
        }

        // zerocore calls the checks lib/ adds here when Main returns, or when
        // Exit ends the program (timers nothing ran, faulted tasks nothing
        // looked at). Here both run from .NET's ProcessExit, which can't
        // tell the two apart.
        internal static unsafe delegate*<void> AddAtMainReturn(delegate*<void> check)
        {
            ZeroExit.Hook();
            return (delegate*<void>)System.Threading.Interlocked.Exchange(ref ZeroExit.AtMainReturn, (nint)check);
        }

        internal static unsafe delegate*<void> AddAtExit(delegate*<void> report)
        {
            ZeroExit.Hook();
            return (delegate*<void>)System.Threading.Interlocked.Exchange(ref ZeroExit.AtExit, (nint)report);
        }
    }
}

static class ZeroExit
{
    internal static nint OutBuffer; // byte*
    internal static nint OutLength; // int*
    internal static nint AtMainReturn, AtExit; // delegate*<void>
    static int s_hooked;

    internal static void Hook()
    {
        if (System.Threading.Interlocked.Exchange(ref s_hooked, 1) != 0)
            return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            WritePendingOutput();
            unsafe
            {
                // SAFETY: static methods of lib/, or null.
                var check = (delegate*<void>)AtMainReturn;
                if (check != null)
                    check();
            }
            WritePendingOutput();
        };
#if ZERO_DOTNET_BCL
        // lib/Console.cs writes what Out holds before its own text, so the
        // two come out in order. The BCL's Console knows nothing of Out:
        // this writer in front of it does the same.
        Console.SetOut(new OutFirstWriter(Console.Out));
        Console.SetError(new OutFirstWriter(Console.Error));
#endif
    }

#if ZERO_DOTNET_BCL
    sealed class OutFirstWriter(System.IO.TextWriter inner) : System.IO.TextWriter
    {
        public override System.Text.Encoding Encoding => inner.Encoding;
        public override void Write(char value) { WritePendingOutput(); inner.Write(value); }
        public override void Write(char[] buffer, int index, int count) { WritePendingOutput(); inner.Write(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { WritePendingOutput(); inner.Write(buffer); }
        public override void Write(string value) { WritePendingOutput(); inner.Write(value); }
        public override void WriteLine() { WritePendingOutput(); inner.WriteLine(); }
        public override void WriteLine(string value) { WritePendingOutput(); inner.WriteLine(value); }
        public override void Flush() { WritePendingOutput(); inner.Flush(); }
    }
#endif

    internal static void WritePendingOutput()
    {
        unsafe
        {
            // SAFETY: lib/Out.cs's buffer and length are statics of a struct
            // with a fixed array, which don't move.
            int* length = (int*)OutLength;
            if (length == null || *length <= 0)
                return;
            int count = *length;
            *length = 0;
            System.Runtime.Platform.write(1, (byte*)OutBuffer, (nuint)count);
        }
    }
}

namespace System.Runtime
{
    // zerocore: where `new` puts objects (Arena.AllocateObjectsHere). Real
    // .NET's GC owns every object, so the setting is kept but has no effect:
    // objects made in an object scope are ordinary ones, and the scope checks
    // find nothing.
    public static class ObjectAllocator
    {
        [ThreadStatic] static nint t_allocate;
        [ThreadStatic] static nint t_context;

        public static unsafe void Set(delegate*<void*, nuint, void*> allocate, void* context)
        {
            t_allocate = (nint)allocate;
            t_context = (nint)context;
        }

        public static unsafe void Get(out delegate*<void*, nuint, void*> allocate, out void* context)
        {
            allocate = (delegate*<void*, nuint, void*>)t_allocate;
            context = (void*)t_context;
        }

        public static unsafe bool StaticsReferInto(void* start, void* end) => false;
    }

    // zerocore/Runtime/Catch.cs: the mark a `throw` comes back to, under
    // lib/Catch.cs's Catch.Run. Zero mode jumps back to it as C's longjmp
    // does; real .NET has exceptions, so here the mark is a try/catch, and
    // everything lib/Catch.cs does around it (closing the files left open,
    // freeing Arena.Scratch memory, Catch.Keep) is the same code. Differences:
    // - catch and finally blocks and using disposals between the throw and
    //   Run do run here (zero mode skips them): a FileStream in a using is
    //   flushed and closed by its Dispose, where zero mode loses its buffer;
    // - what the runtime throws for a bug (an index out of range, a failed
    //   cast, a null dereference) is caught too, where zero mode ends the
    //   program: .NET can't tell `throw null` from a null dereference.
    internal struct CatchFrame
    {
        public unsafe void* Saved;
        public unsafe CatchFrame* Outer;
        public Exception Exception;
        public unsafe void* Handles;
        public bool Inner;
    }

    internal static class CatchFrames
    {
        [ThreadStatic] static unsafe CatchFrame* t_innermost;

        // Zero mode's `throw` and fiber switch go through these once a
        // Catch.Run has run. Here nothing does: an exception finds its catch
        // by itself, and each fiber is a thread with its own marks.
        internal static unsafe delegate*<object, void> s_throw;
        internal static unsafe delegate*<void*, void*> s_swap;

        internal static unsafe void Run(CatchFrame* frame, delegate*<void*, void> body, void* argument)
        {
            unsafe
            {
                // SAFETY: frame is in the caller's stack frame, as zerocore's Run needs.
                frame->Exception = null;
                frame->Outer = t_innermost;
                t_innermost = frame;
                try
                {
                    body(argument);
                }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or InsufficientExecutionStackException))
                {
                    frame->Exception = e;
                }
                finally
                {
                    t_innermost = frame->Outer;
                }
            }
        }

        internal static unsafe void RunInner(CatchFrame* frame, delegate*<void*, void> body, void* argument)
        {
            unsafe { Run(frame, body, argument); } // SAFETY: as Run
        }

        internal static unsafe CatchFrame* Innermost { get { unsafe { return t_innermost; } } }
    }

    // zerocore's Platform, the part lib/ uses: pthread keys (Arena.Scratch's
    // per-thread state). The keys made are remembered, for the fibers
    // (./Fibers.cs), which pass their values from thread to thread.
    internal static class Platform
    {
        internal const int MaxKeys = 16;
        static readonly nuint[] s_keys = new nuint[MaxKeys];
        static int s_keyCount;

        internal static int KeyCount => System.Threading.Volatile.Read(ref s_keyCount);
        internal static nuint Key(int index) => s_keys[index];

        internal static unsafe int pthread_key_create(nuint* key, void* destructor)
        {
            unsafe
            {
                // SAFETY: the caller's arguments, passed on; key is written on success.
                int result = KeyCreate(key, destructor);
                if (result == 0)
                {
                    lock (s_keys)
                    {
                        if (s_keyCount == MaxKeys)
                            Environment.FailFast("ZeroCompat: more pthread keys than Platform.MaxKeys");
                        s_keys[s_keyCount] = *key;
                        System.Threading.Volatile.Write(ref s_keyCount, s_keyCount + 1);
                    }
                }
                return result;
            }
        }

        [DllImport("libc", EntryPoint = "pthread_key_create")] static unsafe extern int KeyCreate(nuint* key, void* destructor);
        [DllImport("libc")] internal static unsafe extern void* pthread_getspecific(nuint key);
        [DllImport("libc")] internal static unsafe extern nint write(int fd, void* buffer, nuint count);
        [DllImport("libc")] internal static unsafe extern int pthread_setspecific(nuint key, void* value);
    }
}

namespace System.Runtime.Intrinsics.Mos
{
    // zerocore: true only on the Commodore 64.
    public static class Mos6502
    {
        public static bool IsSupported => false;
    }
}

// lib/'s DllImport("*") names functions bflat links in (zerocore/native's
// assembly, the target's runtime file), and ("libc") and ("libm.so.6") are
// glibc's names, some only Linux has: all go to libzerocompat
// (dotnet/native), which has the functions bflat would link and passes the
// rest on to the system's C library.
public static class NativeResolver
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Register(typeof(NativeResolver).Assembly);

    /// <summary>The same for a program's own DllImports (dotnet/Sample/ZeroStartup.cs).</summary>
    public static void Register(System.Reflection.Assembly assembly)
    {
        NativeLibrary.SetDllImportResolver(assembly, static (name, assembly, path) =>
        {
            if (name is not ("*" or "libc" or "libm.so.6"))
                return 0;
            string file = OperatingSystem.IsMacOS() ? "libzerocompat.dylib" : "libzerocompat.so";
            return NativeLibrary.Load(System.IO.Path.Combine(AppContext.BaseDirectory, file));
        });
    }
}
