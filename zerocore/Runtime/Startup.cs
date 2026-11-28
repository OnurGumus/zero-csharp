using Internal.Runtime;
using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Internal.Runtime.CompilerHelpers
{
    // The compiler's generated entry point calls InitializeCommandLineArgs
    // before Main. That's where static fields get set up.
    internal static unsafe class StartupCodeHelpers
    {
        private const int GCStaticRegionSection = 201;
        private const int ThreadStaticRegionSection = 202;

        // The low bits of a static base's address, as the compiler emits it.
        private const nint Uninitialized = 1;
        private const nint HasPreInitializedData = 2;
        private const nint FlagsMask = 3;

        // Main's return value, which Shutdown hands back.
        private static int s_exitCode;

        // The compiler's generated entry point calls these two after Main
        // returns (both or neither must exist): SetLatchedExitCode with Main's
        // int, if it returns one, then Shutdown, whose result is the exit code.
        // Shutdown writes what lib/'s Out still holds, as Environment.Exit does,
        // so a last Console.Write without a newline isn't lost, then runs lib/'s
        // check for work left undone (Environment.s_atMainReturn).
        internal static void SetLatchedExitCode(int exitCode) => s_exitCode = exitCode;

        internal static int Shutdown()
        {
            Environment.WritePendingOutput();
            delegate*<void> atMainReturn = Environment.s_atMainReturn;
            if (atMainReturn != null)
                atMainReturn();
            return s_exitCode;
        }

#if WASM
        // WebAssembly can't take a data symbol's address from C#, so
        // wasm/runtime.c hands over the module header.
        [RuntimeImport("*", "ZeroGetModuleHeader"), MethodImpl(MethodImplOptions.InternalCall)]
        private static extern ModuleHeader* GetModuleHeader();
#else
        // The linker defines these two symbols around the __modules section,
        // which holds a pointer to each module's header. They're data, declared
        // as functions only to get at their addresses.
        [RuntimeImport("*", "__start___modules"), MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ModulesStart();

        [RuntimeImport("*", "__stop___modules"), MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ModulesStop();

        // Not inlined on purpose: the compiler can't know where the linker will
        // put these symbols, and when it sees both addresses in one method it
        // decides the loop between them never runs.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ModuleHeader** ModulesBegin() => (ModuleHeader**)(delegate*<void>)&ModulesStart;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ModuleHeader** ModulesEnd() => (ModuleHeader**)(delegate*<void>)&ModulesStop;
#endif

        private static int s_argc;
        private static byte** s_argv;

        private static void InitializeCommandLineArgs(int argc, byte** argv)
        {
            s_argc = argc;
            s_argv = argv;
#if WASM
            InitializeModule(GetModuleHeader());
#else
            ModuleHeader** end = ModulesEnd();
            for (ModuleHeader** module = ModulesBegin(); module < end; module++)
            {
                if (*module != null)
                    InitializeModule(*module);
            }
#endif
        }

#if !WASM
        // A native library (NativeLib=Shared) has no main: the startup object
        // linked into it (zerocore/native/library-*.S, mac/library.c,
        // win/library.c) calls this when the library loads, to set up static
        // fields as a program's entry point does.
        [RuntimeExport("ZeroInitializeLibrary")]
        private static void InitializeLibrary() => InitializeCommandLineArgs(0, null);
#endif

        // What the compiler calls instead on Windows, where .NET's arguments
        // are UTF-16. win/start.c's entry point converts them to UTF-8 before
        // calling Main, so they arrive as on Linux under another type.
        private static void InitializeCommandLineArgsW(int argc, char** argv)
            => InitializeCommandLineArgs(argc, (byte**)argv);

        // For Main(string[] args) and top-level statements: the arguments after
        // the program's name, converted from UTF-8.
        private static string[] GetMainMethodArguments()
        {
            int count = s_argc > 1 ? s_argc - 1 : 0;
            string[] args = new string[count];
            for (int i = 0; i < count; i++)
                args[i] = FromUtf8(s_argv[i + 1]);
            return args;
        }

        private static string FromUtf8(byte* text)
        {
            // First pass counts UTF-16 units, second pass writes them.
            int length = DecodeUtf8(text, ref Unsafe.As<byte, char>(ref *text), write: false);
            string result = string.Allocate(length);
            DecodeUtf8(text, ref result.FirstChar, write: true);
            return result;
        }

        private static int DecodeUtf8(byte* text, ref char output, bool write)
        {
            int length = 0;
            while (*text != 0)
            {
                uint lead = *text++;
                int extra = lead < 0x80 ? 0 : lead < 0xC0 ? -1 : lead < 0xE0 ? 1 : lead < 0xF0 ? 2 : lead < 0xF8 ? 3 : -1;
                uint value = extra <= 0 ? lead : lead & (0x3Fu >> extra);
                bool valid = extra >= 0;
                for (int i = 0; i < extra && valid; i++)
                {
                    if ((*text & 0xC0) != 0x80)
                        valid = false; // cut short: the next byte starts a new character
                    else
                        value = (value << 6) | (*text++ & 0x3Fu);
                }
                if (!valid || value > 0x10FFFF || (value >= 0xD800 && value < 0xE000))
                    value = 0xFFFD; // the replacement character

                if (value < 0x10000)
                {
                    if (write)
                        Unsafe.Add(ref output, length) = (char)value;
                    length++;
                }
                else
                {
                    if (write)
                    {
                        value -= 0x10000;
                        Unsafe.Add(ref output, length) = (char)(0xD800 + (value >> 10));
                        Unsafe.Add(ref output, length + 1) = (char)(0xDC00 + (value & 0x3FF));
                    }
                    length += 2;
                }
            }
            return length;
        }

        private struct ModuleHeader
        {
            public uint Signature;
            public ushort MajorVersion;
            public ushort MinorVersion;
            public uint Flags;
            public ushort NumberOfSections;
            public byte EntrySize;
            public byte EntryType;
        }

        private struct ModuleSection
        {
            public int Id;
            public int Flags;
            public byte* Start;
            public byte* End;
        }

        private static void InitializeModule(ModuleHeader* header)
        {
            // A pointer walk, not an index: on a 6502 an index into these
            // 16-byte entries is 32-bit arithmetic, several hundred bytes of it.
            ModuleSection* section = (ModuleSection*)(header + 1);
            ModuleSection* end = section + header->NumberOfSections;
            for (; section < end; section++)
            {
                if (section->Id == ThreadStaticRegionSection && ThreadStatics.s_region == null)
                {
                    ThreadStatics.s_region = (MethodTable**)section->Start;
                    ThreadStatics.s_count = (int)((section->End - section->Start) / sizeof(nint));
                }
                if (section->Id == GCStaticRegionSection)
                {
                    InitializeStatics(section->Start, section->End);
                    if (s_regionStart == null)
                    {
                        s_regionStart = section->Start;
                        s_regionEnd = section->End;
                    }
                }
            }
        }

        // The GC static region, kept for StaticsReferInto. A bflat program is
        // one module, so it has one.
        private static byte* s_regionStart;
        private static byte* s_regionEnd;

        // Whether any static field holds a reference to an object in
        // [start, end). Lib's arena uses it, with debug checks on, to catch a
        // static field left pointing into memory about to be freed. It reads
        // every word of every static base, so a non-reference field whose
        // value happens to be such an address counts too; static bases hold
        // little else.
        internal static bool StaticsReferInto(byte* start, byte* end)
        {
            bool relative = MethodTable.SupportsRelativePointers;
            for (byte* entry = s_regionStart; entry < s_regionEnd; entry += relative ? sizeof(int) : sizeof(nint))
            {
                nint* slot = relative ? (nint*)ReadRelative(entry) : *(nint**)entry;
                byte* statics = (byte*)*slot;
                if (((nint)statics & FlagsMask) != 0)
                    continue; // never initialized
                nuint size = (*(MethodTable**)statics)->BaseSize;
                for (nuint offset = (nuint)sizeof(nint); offset + (nuint)sizeof(nint) <= size; offset += (nuint)sizeof(nint))
                {
                    byte* value = *(byte**)(statics + offset);
                    if (value >= start && value < end)
                        return true;
                }
            }
            return false;
        }

        // Static fields that hold object references live in one object per type
        // (its "GC static base"), which the garbage collector would normally
        // allocate. The region lists a slot for each; a slot starts out holding
        // the base's method table, tagged Uninitialized, and ends up holding the
        // object, which is what compiled code reads.
        private static void InitializeStatics(byte* region, byte* regionEnd)
        {
            bool relative = MethodTable.SupportsRelativePointers;
            for (byte* entry = region; entry < regionEnd; entry += relative ? sizeof(int) : sizeof(nint))
            {
                nint* slot = relative ? (nint*)ReadRelative(entry) : *(nint**)entry;
                nint value = relative ? (nint)ReadRelative(slot) : *slot;
                if ((value & Uninitialized) == 0)
                    continue;

                MethodTable* type = (MethodTable*)(value & ~FlagsMask);
                object statics = RuntimeExports.RhpNewFast(type);
                if ((value & HasPreInitializedData) != 0)
                {
                    // Fields the compiler evaluated at build time (string
                    // literals, for instance): copy them over the new object.
                    byte* source = relative ? ReadRelative((int*)slot + 1) : (byte*)slot[1];
                    byte* target = (byte*)Unsafe.AsPointer(ref Unsafe.As<RawData>(statics).Data);
                    byte* targetEnd = target + ((nint)type->BaseSize - 2 * sizeof(nint));
                    while (target < targetEnd)
                        *target++ = *source++;
                }
                *slot = Unsafe.As<object, nint>(ref statics);
            }
        }

        private static byte* ReadRelative(void* address) => (byte*)address + *(int*)address;
    }

    // What compiled code calls to turn a type, method or field named in the
    // source (typeof, an array initializer's data) into a handle: the handle is
    // the address the compiler passes.
    internal static unsafe class LdTokenHelpers
    {
        // typeof(T): the compiler skips the handle and asks for the Type itself.
        private static Type GetRuntimeType(MethodTable* type) => Type.Of(type);

        private static RuntimeTypeHandle GetRuntimeTypeHandle(MethodTable* type)
        {
            RuntimeTypeHandle handle;
            *(nint*)&handle = (nint)type;
            return handle;
        }

        private static RuntimeMethodHandle GetRuntimeMethodHandle(nint signature)
        {
            RuntimeMethodHandle handle;
            *(nint*)&handle = signature;
            return handle;
        }

        private static RuntimeFieldHandle GetRuntimeFieldHandle(nint signature)
        {
            RuntimeFieldHandle handle;
            *(nint*)&handle = signature;
            return handle;
        }
    }

    // What compiled code calls where .NET would throw.
    internal static class ThrowHelpers
    {
        private static void ThrowNullReferenceException() => Environment.FailFast("NullReferenceException");
        private static void ThrowIndexOutOfRangeException() => Environment.FailFast("IndexOutOfRangeException");
        private static void ThrowOverflowException() => Environment.FailFast("OverflowException");
        private static void ThrowDivideByZeroException() => Environment.FailFast("DivideByZeroException");
        private static void ThrowArrayTypeMismatchException() => Environment.FailFast("ArrayTypeMismatchException");
        private static void ThrowInvalidProgramException() => Environment.FailFast("InvalidProgramException");
        // No ThrowInvalidProgramExceptionWithArgument, on purpose: for a method
        // whose IL it can't compile, ILC would compile a call of it in its place,
        // and the program would fail only when the method runs. Without it the
        // build stops, and the SDK explains ILC's message (ZERO015).
        private static void ThrowPlatformNotSupportedException() => Environment.FailFast("PlatformNotSupportedException");
        private static void ThrowArgumentException() => Environment.FailFast("ArgumentException");
        private static void ThrowArgumentOutOfRangeException() => Environment.FailFast("ArgumentOutOfRangeException");
        private static void ThrowDataMisalignedException() => Environment.FailFast("DataMisalignedException");
    }
}

namespace System.Runtime.CompilerServices
{
    // Compiled code calls these before touching the static fields of a type
    // whose static constructor hasn't run yet.
    internal static unsafe class ClassConstructorRunner
    {
        private static object CheckStaticClassConstructionReturnGCStaticBase(ref StaticClassConstructionContext context, object gcStaticBase)
        {
            CheckStaticClassConstruction(ref context);
            return gcStaticBase;
        }

        private static nint CheckStaticClassConstructionReturnNonGCStaticBase(ref StaticClassConstructionContext context, nint nonGcStaticBase)
        {
            CheckStaticClassConstruction(ref context);
            return nonGcStaticBase;
        }

        // For a type with [ThreadStatic] fields: this thread's storage for
        // them, once the type's static constructor has run.
        private static object CheckStaticClassConstructionReturnThreadStaticBase(void* module, int typeTlsIndex, StaticClassConstructionContext* context)
        {
            object threadStaticBase = ThreadStatics.GetThreadStaticBaseForType(module, typeTlsIndex);
            CheckStaticClassConstruction(ref *context);
            return threadStaticBase;
        }

        // The fast path: the constructor has run (its address is zero).
        private static void CheckStaticClassConstruction(ref StaticClassConstructionContext context)
        {
            if (Volatile.Read(ref context.cctorMethodAddress) != 0)
                RunOrWait((StaticClassConstructionContext*)Unsafe.AsPointer(ref context));
        }

#if WASM
        // One thread (WebAssembly, DOS, the C64): the constructor runs now.
        // It's marked as run first, so that its own uses of the type's
        // statics go on, as in .NET, instead of starting it again.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunOrWait(StaticClassConstructionContext* context)
        {
            nint constructor = context->cctorMethodAddress;
            context->cctorMethodAddress = 0;
            ObjectAllocator.RunStaticConstructor(constructor);
        }
#else
        // Constructors running now, each with the thread running it (its
        // ObjectAllocator record), under a spin lock. The lock is held only
        // to look at or change the list, never while a constructor runs.
        private struct Running
        {
            public StaticClassConstructionContext* Context;
            public void* Owner;
            public Running* Next;
        }

        private static int s_lock;
        private static Running* s_running;
        private static Running* s_spare;   // finished entries, for reuse

        private static void Lock()
        {
            while (Interlocked.CompareExchange(ref s_lock, 1, 0) != 0)
            {
                // another thread is looking at the list
            }
        }

        private static void Unlock() => Volatile.Write(ref s_lock, 0);

        private static Running* Find(StaticClassConstructionContext* context)
        {
            for (Running* entry = s_running; entry != null; entry = entry->Next)
            {
                if (entry->Context == context)
                    return entry;
            }
            return null;
        }

        // Whether owner is waiting, directly or through other threads, for a
        // constructor that me is running: waiting for it would never end.
        private static bool WaitsFor(void* owner, void* me)
        {
            for (int step = 0; step < 64 && owner != null; step++)
            {
                StaticClassConstructionContext* awaited = (StaticClassConstructionContext*)ObjectAllocator.WaitingFor(owner);
                Running* entry = awaited == null ? null : Find(awaited);
                if (entry == null)
                    return false;
                if (entry->Owner == me)
                    return true;
                owner = entry->Owner;
            }
            return false;
        }

        // The first thread to get here runs the constructor; the others wait
        // until it has finished, yielding the processor meanwhile. As in .NET, a thread goes on without waiting
        // when the constructor is its own (it touched its type's statics
        // while the constructor runs) or when waiting would close a cycle (two
        // threads, each in a constructor that needs the other's): it then
        // sees the type's statics as they are so far.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunOrWait(StaticClassConstructionContext* context)
        {
            void* me = ObjectAllocator.CurrentThreadRecord();
            while (true)
            {
                Lock();
                nint constructor = context->cctorMethodAddress;
                if (constructor == 0)
                {
                    ObjectAllocator.SetWaitingFor(me, null);
                    Unlock();
                    return;
                }
                Running* entry = Find(context);
                if (entry == null)
                {
                    entry = s_spare;
                    if (entry != null)
                        s_spare = entry->Next;
                    else
                        entry = (Running*)Platform.calloc(1, (nuint)sizeof(Running));
                    if (entry == null)
                    {
                        Unlock();
                        Environment.FailFast("Static constructor: out of memory");
                    }
                    entry->Context = context;
                    entry->Owner = me;
                    entry->Next = s_running;
                    s_running = entry;
                    ObjectAllocator.SetWaitingFor(me, null);
                    Unlock();

                    ObjectAllocator.RunStaticConstructor(constructor);

                    Lock();
                    Volatile.Write(ref context->cctorMethodAddress, 0);
                    if (s_running == entry)
                    {
                        s_running = entry->Next;
                    }
                    else
                    {
                        Running* previous = s_running;
                        while (previous->Next != entry)
                            previous = previous->Next;
                        previous->Next = entry->Next;
                    }
                    entry->Next = s_spare;
                    s_spare = entry;
                    Unlock();
                    return;
                }
                if (entry->Owner == me || WaitsFor(entry->Owner, me))
                {
                    ObjectAllocator.SetWaitingFor(me, null);
                    Unlock();
                    return;
                }
                ObjectAllocator.SetWaitingFor(me, context);
                Unlock();
                while (Volatile.Read(ref context->cctorMethodAddress) != 0)
                    Platform.sched_yield(); // the other thread is still running it
            }
        }
#endif
    }

    // The address of a type's static constructor; zero once it has run.
    public struct StaticClassConstructionContext
    {
        public nint cctorMethodAddress;
    }
}
