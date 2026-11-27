using System.Runtime;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BflatLib")]

namespace System
{
    public static unsafe class Environment
    {
        /// <summary>
        /// "\n", on every target: the line ending lib/'s Out.Line writes. (.NET's is "\r\n"
        /// on Windows; here a program's output is the same bytes everywhere.)
        /// </summary>
        public static string NewLine => "\n";

        /// <summary>The number of processors: at least 1. 1 on DOS and UEFI.</summary>
        public static int ProcessorCount
        {
            get
            {
                int count = Platform.get_nprocs();
                return count < 1 ? 1 : count;
            }
        }

        /// <summary>Milliseconds since an arbitrary start, from a clock that never goes back.</summary>
        public static long TickCount64
        {
            get
            {
                long* timespec = stackalloc long[2];
                if (Platform.clock_gettime(1, timespec) != 0)
                    return 0;
                return timespec[0] * 1000 + timespec[1] / 1_000_000;
            }
        }

        // Set by lib/'s Out to its buffer and to its count of bytes not written
        // yet, so Exit, returning from Main (StartupCodeHelpers.Shutdown) and
        // FailFast write them first. Data rather than a function to call: a
        // managed call through a pointer makes FailFast take the shadow stack
        // compiled C# passes as a hidden argument, and then every call to it
        // and to the throw helpers passes one too (1.3 KB on a Commodore 64).
        internal static byte* s_outBuffer;
        internal static int* s_outLength;

        // Writes what Out holds, once.
        internal static void WritePendingOutput()
        {
            int* length = s_outLength;
            if (length == null || *length <= 0)
                return;
            int count = *length;
            *length = 0;
            Platform.write(1, s_outBuffer, (nuint)count);
        }

        // Set by lib/ to check, once Main has returned, for work a program
        // started and never let run (lib/Timers.cs: a timer started without
        // Fiber.Run) or failures nobody looked at (lib/Tasks.cs: a faulted
        // task nothing awaited). Called after WritePendingOutput, so output
        // comes first, and not by Exit, which ends the program on purpose.
        internal static delegate*<void> s_atMainReturn;

        // Makes check the one called when Main returns and gives back the one
        // it replaces, which check must call: so each check is added once and
        // all of them run.
        internal static delegate*<void> AddAtMainReturn(delegate*<void> check)
        {
            fixed (delegate*<void>* slot = &s_atMainReturn)
                return (delegate*<void>)System.Threading.Interlocked.Exchange(ref *(nint*)slot, (nint)check);
        }

        // Set by lib/ to report, when Exit ends the program, what it would
        // otherwise lose without a word (lib/Tasks.cs: a faulted task nothing
        // awaited). Not for work left undone: Exit means to leave it.
        internal static delegate*<void> s_atExit;

        // As AddAtMainReturn, for Exit.
        internal static delegate*<void> AddAtExit(delegate*<void> report)
        {
            fixed (delegate*<void>* slot = &s_atExit)
                return (delegate*<void>)System.Threading.Interlocked.Exchange(ref *(nint*)slot, (nint)report);
        }

        /// <summary>
        /// Ends the program with <paramref name="exitCode"/>, as returning it from Main
        /// would. Text Out holds is written first; so are files the C library buffers.
        /// </summary>
        [DoesNotReturn]
        public static void Exit(int exitCode)
        {
            WritePendingOutput();
            delegate*<void> atExit = s_atExit;
            if (atExit != null)
                atExit();
            Platform.exit(exitCode);
            while (true) { }
        }

        /// <summary>
        /// The value of the environment variable <paramref name="variable"/>, or null if it
        /// isn't set. The string is allocated like the result of a + b: in the current
        /// thread's arena, if lib/'s Arena.AllocateObjectsHere chose one. The value is
        /// decoded from UTF-8; an invalid byte becomes U+FFFD.
        /// </summary>
        public static string GetEnvironmentVariable(string variable)
        {
            if (variable == null)
                FailFast("Environment.GetEnvironmentVariable: variable is null");

            // The name, as null-terminated UTF-8.
            const int MaxName = 256;
            byte* name = stackalloc byte[MaxName];
            int length = 0;
            for (int i = 0; i < variable.Length; i++)
            {
                int c = variable[i];
                if (c >= 0xD800 && c <= 0xDBFF && i + 1 < variable.Length
                    && variable[i + 1] >= 0xDC00 && variable[i + 1] <= 0xDFFF)
                    c = 0x10000 + ((c - 0xD800) << 10) + (variable[++i] - 0xDC00);
                else if (c >= 0xD800 && c <= 0xDFFF)
                    c = 0xFFFD;
                if (c == 0 || length + 4 >= MaxName)
                    return null; // no variable has a NUL in its name, or one this long
                if (c < 0x80)
                {
                    name[length++] = (byte)c;
                }
                else if (c < 0x800)
                {
                    name[length++] = (byte)(0xC0 | (c >> 6));
                    name[length++] = (byte)(0x80 | (c & 0x3F));
                }
                else if (c < 0x10000)
                {
                    name[length++] = (byte)(0xE0 | (c >> 12));
                    name[length++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    name[length++] = (byte)(0x80 | (c & 0x3F));
                }
                else
                {
                    name[length++] = (byte)(0xF0 | (c >> 18));
                    name[length++] = (byte)(0x80 | ((c >> 12) & 0x3F));
                    name[length++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    name[length++] = (byte)(0x80 | (c & 0x3F));
                }
            }
            name[length] = 0;

            byte* value = Platform.getenv(name);
            if (value == null)
                return null;

            // Two passes over the value: count the chars, then store them.
            int count = 0;
            for (byte* p = value; *p != 0;)
                count += Decode(ref p) >= 0x10000 ? 2 : 1;
            string result = string.Allocate(count);
            char* to = (char*)Unsafe.AsPointer(ref result.FirstChar);
            for (byte* p = value; *p != 0;)
            {
                int c = Decode(ref p);
                if (c >= 0x10000)
                {
                    c -= 0x10000;
                    *to++ = (char)(0xD800 + (c >> 10));
                    *to++ = (char)(0xDC00 + (c & 0x3FF));
                }
                else
                {
                    *to++ = (char)c;
                }
            }
            return result;
        }

        // One code point from null-terminated UTF-8, moving p past it. A byte that
        // can't start or continue a character is U+FFFD and is skipped by itself.
        static int Decode(ref byte* p)
        {
            int lead = *p++;
            if (lead < 0x80)
                return lead;
            int need, min, c;
            if (lead >= 0xC2 && lead <= 0xDF) { need = 1; min = 0x80; c = lead & 0x1F; }
            else if (lead >= 0xE0 && lead <= 0xEF) { need = 2; min = 0x800; c = lead & 0x0F; }
            else if (lead >= 0xF0 && lead <= 0xF4) { need = 3; min = 0x10000; c = lead & 0x07; }
            else return 0xFFFD;
            for (; need > 0; need--)
            {
                if ((*p & 0xC0) != 0x80) // also stops at the terminating 0
                    return 0xFFFD;
                c = (c << 6) | (*p++ & 0x3F);
            }
            if (c < min || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF))
                return 0xFFFD;
            return c;
        }

        // Zero mode has no exceptions: anything that would throw ends the
        // process here, after printing the reason to stderr. What the program
        // printed before it (Out.Write without a newline holds it back)
        // comes first: that is often where it got to. The message is encoded
        // before that call, so no object reference is live across it, which
        // would also make FailFast take the shadow stack (see s_outBuffer).
        [DoesNotReturn]
        public static void FailFast(string message)
        {
            byte* buffer = stackalloc byte[256];
            int length = 0;
            if (message != null)
            {
                for (int i = 0; i < message.Length && length < 255; i++)
                {
                    char c = message[i];
                    buffer[length++] = c < 0x80 ? (byte)c : (byte)'?';
                }
                buffer[length++] = (byte)'\n';
            }
            WritePendingOutput();
            if (length > 0)
                Platform.write(2, buffer, (nuint)length);
            Platform.abort();
            while (true) { }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class InternalsVisibleToAttribute : Attribute
    {
        public InternalsVisibleToAttribute(string assemblyName) { }
    }
}
