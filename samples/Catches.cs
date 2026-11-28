// Example: Catch.Run (lib/Catch.cs), getting back what a `throw` threw.
// Exit code = number of failed checks.
// Build: ./build.sh Catches
//
// A throw inside Catch.Run jumps straight back to it: no catch or finally
// block on the way runs. Run then closes the files and sockets opened inside
// and left open, frees the Arena.Scratch memory of the calls jumped out of,
// and puts the object allocator back.

using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace CatchesSample;

public struct Counter
{
    public int Calls;
    public int Result;
}

public static class Program
{
    static int s_failed;

    static void Check(bool ok, string what)
    {
        if (ok)
        {
            Console.WriteLine("ok   " + what);
        }
        else
        {
            Console.WriteLine("FAIL " + what);
            s_failed++;
        }
    }

    static int s_after;

    // A throw three calls down: nothing after it, at any level, runs.
    static int Deep(int level, string message)
    {
        if (level == 0)
            throw new InvalidOperationException(message);
        int result = Deep(level - 1, message);
        s_after++;
        return result;
    }

    // docs:copy
    // Copies a file. A failure is one to report and go on from.
    static Exception Copy(string from, string to) => Catch.Run(() =>
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read);
        using var target = new FileStream(to, FileMode.Create);
        source.CopyTo(target);
    });
    // docs:end

    // docs:retry
    static void Connect(string host)
        => throw new IOException(string.Concat("no answer from ", host));

    // Tries up to limit times. What a failed attempt news, the exception
    // and its message included, goes to the arena, and the Reset after the
    // attempt frees it: nothing else ever would.
    static int Retry(Arena attempts, int limit)
    {
        int failed = 0;
        while (failed < limit)
        {
            Exception error = Catch.Run(ref attempts, static (ref Arena a) =>
            {
                // SAFETY: nothing made in the scope is used after the Reset
                unsafe
                {
                    using (a.AllocateObjectsHere())
                        Connect("example.org");
                }
            });
            if (error == null)
                break;
            failed++; // error and its Message are the arena's: use them here
            unsafe { attempts.Reset(); } // SAFETY: nothing from it is kept
        }
        return failed;
    }
    // docs:end

    public static int Main()
    {
        // Nothing thrown: null.
        int ran = 0;
        Check(Catch.Run(() => ran++) == null && ran == 1, "a callback that returns gives null");

        // A throw deep down comes back here, and nothing in between runs.
        s_after = 0;
        Exception e = Catch.Run(() => Deep(3, "deep"));
        Check(e is InvalidOperationException && e.Message == "deep" && s_after == 0,
            "a throw three calls down comes back, skipping the rest of each call");

        // Rethrown, it goes to the next Catch.Run out.
        Exception inner = null;
        Exception outer = Catch.Run(() =>
        {
            inner = Catch.Run(() => throw new ArgumentException("first"));
            throw inner;
        });
        Check(inner is ArgumentException && outer == inner, "rethrown, an exception goes to the Catch.Run around");

        // The inner one catches; the outer sees nothing.
        outer = Catch.Run(() => { inner = Catch.Run(() => throw new FormatException("x")); });
        Check(inner is FormatException && outer == null, "an inner Catch.Run keeps its exception from the outer");

        // throw null throws a NullReferenceException, as in .NET.
        e = Catch.Run(static () => throw null);
        Check(e is NullReferenceException, "throw null throws a NullReferenceException");

        // A function: its result, or default when it throws.
        e = Catch.Run(() => 6 * 7, out int answer);
        Check(e == null && answer == 42, "a function's result comes back");
        e = Catch.Run(() => Deep(1, "no result"), out answer);
        Check(e != null && answer == 0, "a function that throws gives default");

        // A Lazy whose factory throws is as it was: the next read tries again.
        int tries = 0;
        var lazy = new Lazy<int>(() => ++tries == 1 ? throw new IOException("not yet") : 42);
        e = Catch.Run(() => lazy.Value, out int early);
        Check(e is IOException && !lazy.IsValueCreated, "a Lazy whose factory threw has no value");
        Check(lazy.Value == 42 && tries == 2, "and its next read runs the factory again");

        // State by ref, without a closure; what changed before the throw stays.
        // docs:state
        var counter = new Counter();
        e = Catch.Run(ref counter, static (ref Counter c) =>
        {
            c.Calls++;
            c.Result = 7;
            throw new InvalidOperationException("after the changes");
        });
        // counter.Calls is 1 and counter.Result 7: they stay as they were
        // docs:end
        Check(e != null && counter.Calls == 1 && counter.Result == 7, "changes to a ref state before the throw stay");

        // Many in a row: the stack doesn't drift.
        int caught = 0;
        for (int i = 0; i < 10000; i++)
            if (Catch.Run(ref caught, static (ref int n) => { n++; throw new InvalidOperationException("again"); }) != null)
                caught += 0;
        Check(caught == 10000, "10000 throws in a row, each caught");

        // I/O: a file that isn't there.
        e = Catch.Run(() => new FileStream("no-such-dir/no-such-file", FileMode.Open, FileAccess.Read));
        Check(e is IOException, "opening a missing file throws an IOException");

        // A file opened inside and left open (the using block was jumped
        // over) is closed: the file, not the FileStream, whose Dispose never
        // runs. The object finds itself closed at its next use, and what it
        // held in its buffer is lost, as when a stream is never disposed.
        const string Path = "catches.tmp";
        FileStream kept = null;
        e = Catch.Run(() =>
        {
            using var file = new FileStream(Path, FileMode.Create, FileAccess.Write);
            kept = file;
            file.Write(new byte[] { 1, 2, 3 }, 0, 3);
            file.Flush(); // these three are in the file
            file.Write(new byte[] { 4, 5 }, 0, 2);
            throw new IOException("halfway");
        });
        Check(e is IOException && kept != null && !kept.CanWrite, "a file opened inside is closed after a throw");
        Check(Length(Path) == 3, "what was flushed is in it; what was still buffered isn't");
        e = Catch.Run(() => kept.WriteByte(6));
        Check(e is ObjectDisposedException, "using the stream afterwards throws, as for a disposed one");
        kept.Dispose();
        Check(Length(Path) == 3, "and disposing it then does nothing");

        // Catch.Keep: a file opened inside that should outlive a failure.
        // docs:keep
        // A log opened at its first line, wherever that is written from:
        // kept, it stays open when the work around it fails.
        FileStream log = null;
        e = Catch.Run(() =>
        {
            log = new FileStream("catches.log", FileMode.Create);
            Catch.Keep(log); // stored above: closing it is ours now
            log.WriteByte((byte)'1');
            throw new IOException("the work failed");
        });
        log.WriteByte((byte)'2'); // still open
        log.Dispose();
        // docs:end
        Check(e is IOException && Length("catches.log") == 2, "a kept file stays open after a throw, buffer and all");
        File.Delete("catches.log");

        // A throw while a write still runs on the I/O thread (lib/FileAsync.cs):
        // Catch.Run leaves the closing to the write, which closes the file
        // when it's done, rather than pull it from under that thread.
        FileStream writing = null;
        System.Threading.Tasks.Task pending = null;
        e = Catch.Run(() =>
        {
            writing = new FileStream("catches-async.tmp", FileMode.Create, FileAccess.Write, FileShare.Read, 0);
            pending = writing.WriteAsync(new byte[AsyncWriteSize]).AsTask();
            throw new IOException("while writing");
        });
        pending.Wait();
        Check(e is IOException && pending.IsCompletedSuccessfully && !writing.CanWrite && Length("catches-async.tmp") == AsyncWriteSize,
            "a file in use on the I/O thread is closed once its write is done");
        File.Delete("catches-async.tmp");

        // A plain File handle is closed too, and every copy of it knows.
        // (The C library holds its buffer, and writes it out on closing.)
        File plain = default;
        e = Catch.Run(() =>
        {
            plain = File.Create("catches.txt");
            plain.Write("abc");
            throw new IOException("with a File open");
        });
        File copy = plain;
        Check(e is IOException && !plain.IsOpen && !copy.IsOpen && !copy.Write("d"),
            "a File opened inside is closed after a throw, for every copy");
        Check(Length("catches.txt") == 3, "and what the C library held for it is written");

        // Kept, it stays open, and closing it is the program's again.
        e = Catch.Run(() =>
        {
            plain = File.OpenAppend("catches.txt");
            Catch.Keep(plain);
            throw new IOException("with a kept File open");
        });
        Check(e is IOException && plain.IsOpen && plain.Write("de"), "a kept File stays open after a throw");
        unsafe { plain.Close(); } // SAFETY: plain isn't used after this
        Check(Length("catches.txt") == 5, "and takes what is written to it afterwards");
        File.Delete("catches.txt");

        // Files opened inside one Catch.Run and closed by other threads: the
        // list they are in is changed from several threads at once. Round
        // after round, and then with half of them left for Run to close.
        // (Few at a time: DOS has a small limit on open files.)
        File[] shared = new File[8];
        int rounds = Parallel.ProcessorCount > 1 ? 2000 : 20;
        e = Catch.Run(() =>
        {
            for (int round = 0; round < rounds; round++)
            {
                for (int i = 0; i < shared.Length; i++)
                    shared[i] = File.OpenRead(Path);
                Parallel.For(0, shared.Length, shared, static (files, i) =>
                {
                    unsafe { files[i].Close(); } // SAFETY: each thread closes its own, once
                });
            }
            for (int i = 0; i < shared.Length; i++)
                shared[i] = File.OpenRead(Path);
            Parallel.For(0, shared.Length / 2, shared, static (files, i) =>
            {
                unsafe { files[2 * i].Close(); } // SAFETY: as above
            });
            throw new IOException("with half of them open");
        });
        bool allClosed = true;
        foreach (File file in shared)
            allClosed &= !file.IsOpen;
        Check(e is IOException && allClosed, "files closed by other threads, and the rest by Run, are each closed once");

        // The same with the stream's memory gone: objects went to an arena
        // that was reset before the throw. Run closes the file without
        // touching the stream.
        Arena frame = Arena.Create();
        e = Catch.Run(() =>
        {
            // SAFETY: the stream made in the scope isn't used after the Reset
            unsafe
            {
                using (frame.AllocateObjectsHere())
                {
                    var lost = new FileStream(Path, FileMode.Open, FileAccess.Read);
                    lost.ReadByte();
                }
                frame.Reset();
            }
            throw new IOException("after the reset");
        });
        Check(e is IOException, "a stream whose arena was reset is closed too, by its file alone");
        unsafe { frame.Destroy(); } // SAFETY: nothing from it is used after this

        // Opened inside and disposed inside: nothing left for Run to do.
        e = Catch.Run(() =>
        {
            using (var file = new FileStream(Path, FileMode.Open, FileAccess.Read)) { }
            throw new IOException("after closing");
        });
        Check(e is IOException, "a file closed before the throw is left alone");

        // Opened inside a callback that returns, kept: still open.
        e = Catch.Run(() => new FileStream(Path, FileMode.Open, FileAccess.Read), out FileStream open);
        Check(e == null && open.CanRead, "a file a callback returns stays open");
        open.Dispose();

        // The example: a copy that fails is reported, not fatal.
        int failedCopies = 0;
        // docs:report
        Exception error = Copy("no-such-file", "copy.tmp");
        if (error is IOException)
            failedCopies++;  // count it, report it, and go on
        else if (error != null)
            throw error;     // not an I/O failure: pass it on
        // docs:end
        Check(error is IOException && failedCopies == 1, "Copy reports a missing source");
        e = Copy(Path, "copy.tmp");
        Check(e == null && Length("copy.tmp") == 3, "Copy copies");
        File.Delete(Path);
        File.Delete("copy.tmp");

        // Arena.Scratch calls jumped out of free their memory.
        nint before = ScratchStart();
        e = Catch.Run(static () => Arena.Scratch(static s =>
        {
            s.AllocSpan<byte>(1000);
            Arena.Scratch(static t =>
            {
                t.AllocSpan<byte>(100000); // more than the first block: malloc
                throw new InvalidOperationException("in scratch");
            });
        }));
        // (Not with Arena.GuardPages on, which never hands an address out twice.)
        Check(e != null && (ScratchStart() == before || Arena.GuardPages), "scratch memory from the calls jumped out of is freed");

        // The object allocator is put back.
        // (The exception itself was made inside the scope, so it's in the arena.)
        Arena arena = Arena.Create();
        e = Catch.Run(() =>
        {
            // SAFETY: nothing allocated in the scope is used after it
            unsafe { using var scope = arena.AllocateObjectsHere(); throw new InvalidOperationException("in an object scope"); }
        });
        nuint used = arena.Used;
        int[] after = new int[16];
        Check(e != null && after.Length == 16 && arena.Used == used, "objects go where they went before the scope, not to its arena");
        unsafe { arena.Destroy(); } // SAFETY: nothing from it is used after this

        // Arena memory is the code around's to free: Catch.Run leaves it.
        // docs:arena
        // The Reset comes after Catch.Run, so it runs whether the work
        // returned or threw, and frees what either left in the arena.
        Arena work = Arena.Create();
        int failures = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Exception failure = Catch.Run(ref work, static (ref Arena a) =>
            {
                Span<int> numbers;
                // SAFETY: not used after the Reset below
                unsafe { numbers = a.AllocSpan<int>(1000); }
                numbers.Fill(1);
                throw new IOException("the disk is full");
            });
            if (failure != null)
                failures++; // look at the exception here, before the Reset
            unsafe { work.Reset(); } // SAFETY: nothing from the arena is kept
        }
        // docs:end
        Check(failures == 3 && work.Used == 0, "a Reset after Catch.Run frees what the callback allocated");
        unsafe { work.Destroy(); } // SAFETY: nothing from it is used after this

        // A loop that keeps failing: with the attempts' objects in an arena,
        // nothing is left behind. (One attempt first: it makes what is made
        // once, such as the lambda's delegate.)
        Arena attempts = Arena.Create();
        Retry(attempts, 1);
        long bytesBefore = GC.GetTotalAllocatedBytes();
        int failedAttempts = Retry(attempts, 1000);
        long left = GC.GetTotalAllocatedBytes() - bytesBefore;
        Check(failedAttempts == 1000 && left == 0, "1000 failed attempts in an arena leave nothing behind");
        unsafe { attempts.Destroy(); } // SAFETY: nothing from it is used after this

        // Fibers: each has its own Catch.Run, even across a yield.
        string first = null, second = null;
        Fiber.Spawn(0, _ => { first = Catch.Run(() => { Fiber.Yield(); throw new IOException("one"); })?.Message; });
        Fiber.Spawn(0, _ => { second = Catch.Run(() => { Fiber.Yield(); Fiber.Yield(); throw new IOException("two"); })?.Message; });
        Fiber.Run();
        Check(first == "one" && second == "two", "each fiber's throw comes back to its own Catch.Run");

        Console.WriteLine(s_failed == 0 ? "All checks passed." : s_failed + " checks failed.");
        return s_failed;
    }

    // Big enough to be still running on the I/O thread when the throw comes;
    // where there are no threads the write runs at once, and a small one
    // leaves a microcontroller's heap for the tests after it.
    static int AsyncWriteSize => Parallel.ProcessorCount > 1 ? 200_000 : 2_000;

    static long Length(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
        return file.Length;
    }

    // Where the next Arena.Scratch call's memory starts.
    static nint ScratchStart()
    {
        nint start = 0;
        Arena.Scratch(ref start, static (scoped ScratchScope s, ref nint where) =>
        {
            Span<byte> span = s.AllocSpan<byte>(1);
            unsafe { where = (nint)Unsafe.AsPointer(ref span[0]); } // SAFETY: only the address is kept, as a number
        });
        return start;
    }
}
