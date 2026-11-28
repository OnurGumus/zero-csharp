// Reading and writing files without blocking: FileStream's ReadAsync,
// WriteAsync, FlushAsync and DisposeAsync, and File's ReadAllBytesAsync,
// ReadAllTextAsync, WriteAllBytesAsync, WriteAllTextAsync and
// AppendAllTextAsync, as in .NET.
//
//   byte[] data = await File.ReadAllBytesAsync("big.bin");
//
//   await using var log = new FileStream("log.txt", FileMode.Append, FileAccess.Write);
//   await log.WriteAsync(line);
//
// A file can't be waited on the way a socket is: poll says a file is always
// ready, and the read then blocks in the system while the disk works. So, as
// .NET does on Linux, the read or write runs on another thread: an I/O
// thread, one per thread that uses these calls, started by the first. When
// it's done, it wakes Fiber.Run, which completes the task; meanwhile the
// other fibers and tasks run. So a slow disk or a network drive no longer
// stops every fiber on the thread.
//
// Handing a job to the I/O thread and back costs some microseconds, more than
// reading a few KB the system has cached. The async calls pay off for big
// reads and writes, slow disks and network drives; for small files on a local
// disk, the blocking calls are faster. A FileStream's buffer (4096 bytes by
// default) still gathers small reads and writes, which then need no thread.
//
// Where there are no threads (DOS, UEFI, microcontrollers, WebAssembly), the
// call runs at once, blocking, as Read and Write do, and its task is done
// when it returns.
//
// What differs from .NET:
// - One operation at a time on a FileStream: another call (async or not)
//   while one runs ends the program, as does disposing the stream then. .NET
//   allows some overlap.
// - A token is looked at when an operation starts, not during it: a read
//   already running on the I/O thread can't be stopped.
// - A failure ends the program, as the IOException would (FileNotFound when
//   opening, a short write). FileStream.TryOpen opens without ending it.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// The I/O thread: one per thread that uses the async file calls, started by
// the first. It runs the freads, fwrites and fflushes it's given, in order,
// and wakes Fiber.Run when each is done. Two pipes connect them: Fiber.Run
// writes a byte into one for each job it queues, and the I/O thread waits in
// read on it; the I/O thread writes a byte into the other for each batch it
// finishes, and Fiber.Run waits for that in poll, with the sockets and in
// place of a fiber (Fiber.IoWaiter). Jobs pass between them on two lock-free
// lists. Where there are no pipes or threads (DOS, UEFI, microcontrollers,
// WebAssembly), a job runs at once, blocking, on the calling thread.
sealed class FileThread : Fiber.IoWaiter
{
    [DllImport("libc")] static unsafe extern int pipe(int* fds);
    [DllImport("libc")] static unsafe extern nint read(int fd, void* buffer, nuint count);
    [DllImport("libc")] static unsafe extern nint write(int fd, void* buffer, nuint count);
    [DllImport("libc")] static unsafe extern int poll(void* fds, nuint count, int timeout);
    [DllImport("libc")] static unsafe extern int pthread_create(nint* thread, void* attributes, delegate* unmanaged<void*, void*> start, void* argument);
    [DllImport("libc")] static unsafe extern nuint fread(void* buffer, nuint size, nuint count, nint stream);
    [DllImport("libc")] static unsafe extern nuint fwrite(void* buffer, nuint size, nuint count, nint stream);
    [DllImport("libc")] static unsafe extern int fflush(nint stream);
    [DllImport("libc")] static unsafe extern void* calloc(nuint count, nuint size);
    [DllImport("libc")] static unsafe extern void free(void* memory);

    internal const int Read = 0, Write = 1, Flush = 2;
    const short POLLIN = 1;

    // One fread, fwrite or fflush. The task is the waiting side's alone: the
    // I/O thread touches only the C fields.
    struct Job
    {
        public Job* Next;
        public int Kind;
        public nint Stream;
        public nint Buffer;
        public nint Length;
        public nint Result;     // bytes read or written; fflush's result
        public Task<int> Task;
    }

    // What the two threads share, from calloc, kept for the program's life.
    struct Shared
    {
        public long Queued;     // Job*, the latest first: for the I/O thread
        public long Done;       // Job*, the latest first: for Fiber.Run
        public int WakeRead, WakeWrite;   // Fiber.Run -> the I/O thread
        public int DoneRead, DoneWrite;   // the I/O thread -> Fiber.Run
    }

    [ThreadStatic] static FileThread? t_thread;
    [ThreadStatic] static bool t_none;  // no pipes or threads here

    readonly Shared* _shared;
    Fiber.Record _record;               // on Fiber.Run's poll list while jobs are out
    int _out;                           // jobs queued and not yet completed
    bool _waiting;

    FileThread(Shared* shared) => _shared = shared;

    // Runs the operation on the I/O thread; the task completes, in
    // Fiber.Run, once it's done. Buffer must stay where it is until then:
    // zero mode's arrays don't move.
    internal static ValueTask<int> RunAsync(int kind, nint stream, nint buffer, int length)
    {
        FileThread? thread = t_thread ?? Start();
        unsafe
        {
            // SAFETY: the job is freed once its result is read (Ready), or here.
            Job* job = (Job*)calloc(1, (nuint)sizeof(Job));
            if (job == null)
                Environment.FailFast("FileStream: out of memory for an async operation");
            job->Kind = kind;
            job->Stream = stream;
            job->Buffer = buffer;
            job->Length = length;
            if (thread == null)
            {
                RunJob(job); // no I/O thread here: block
                int result = (int)job->Result;
                free(job);
                return new ValueTask<int>(result);
            }
            var task = new Task<int>();
            job->Task = task;
            thread.Queue(job);
            return new ValueTask<int>(task);
        }
    }

    // The I/O thread, started by the first job on this thread; null if it
    // can't be (no pipes or no threads here).
    static FileThread? Start()
    {
        if (t_none)
            return null;
        unsafe
        {
            // SAFETY: Shared is from calloc and never freed; the fds are its.
            Shared* shared = (Shared*)calloc(1, (nuint)sizeof(Shared));
            int* fds = stackalloc int[2];
            nint thread = 0;
            // Pipes first: where they fail there are no threads either (and
            // UEFI's pthread_create would run the I/O thread's loop in place).
            bool started = shared != null && pipe(fds) == 0;
            if (started)
            {
                shared->WakeRead = fds[0];
                shared->WakeWrite = fds[1];
                started = pipe(fds) == 0;
            }
            if (started)
            {
                shared->DoneRead = fds[0];
                shared->DoneWrite = fds[1];
                started = pthread_create(&thread, null, &Work, shared) == 0;
            }
            if (!started)
            {
                t_none = true;
                return null;
            }
            if (Fiber.Poll == null)
                Fiber.Poll = &Poll;
            // Kept for the thread's life: made outside any object scope the
            // program has open now (scripts/static-stores).
            using (System.Runtime.ObjectAllocator.Lasting())
                return t_thread = new FileThread(shared);
        }
    }

    static int Poll(void* fds, nuint count, int timeout)
    {
        unsafe { return poll(fds, count, timeout); } // SAFETY: Fiber.Run's array of count entries
    }

    void Queue(Job* job)
    {
        unsafe
        {
            // SAFETY: the job is the caller's, the fds Shared's.
            Push(ref _shared->Queued, job);
            byte wake = 1;
            write(_shared->WakeWrite, &wake, 1); // after the push: the I/O thread sees the job
            _out++;
            if (!_waiting)
                Wait();
        }
    }

    void Wait()
    {
        unsafe
        {
            // SAFETY: zero mode's objects don't move; the record leaves the
            // list (Ready) before it's put on again.
            _waiting = true;
            _record.Body = this;
            Fiber.WaitForIOAsync((Fiber.Record*)Unsafe.AsPointer(ref _record), _shared->DoneRead, POLLIN);
        }
    }

    // The I/O thread said some jobs are done (poll found its byte).
    internal override void Ready(short revents)
    {
        _waiting = false;
        unsafe
        {
            // SAFETY: the jobs came back from the I/O thread, which is done
            // with them; each is freed once its task has its result.
            byte* bytes = stackalloc byte[64];
            read(_shared->DoneRead, bytes, 64); // there is something: no wait
            Job* done = Reverse(TakeAll(ref _shared->Done));
            while (done != null)
            {
                Job* job = done;
                done = job->Next;
                Task<int> task = job->Task;
                int result = (int)job->Result;
                job->Task = null!;
                free(job);
                _out--;
                task.TrySetResult(result);
            }
        }
        if (_out > 0 && !_waiting)
            Wait();
    }

    // The I/O thread: C calls and the shared lists only, no objects.
    [UnmanagedCallersOnly]
    static void* Work(void* argument)
    {
        unsafe
        {
            // SAFETY: Shared lives as long as the program; a job is the
            // waiting side's until pushed on Done.
            Shared* shared = (Shared*)argument;
            byte* bytes = stackalloc byte[64];
            while (true)
            {
                nint got = read(shared->WakeRead, bytes, 64);
                if (got == 0)
                    return null; // the pipe was closed
                Job* jobs = Reverse(TakeAll(ref shared->Queued));
                if (jobs == null)
                    continue;
                while (jobs != null)
                {
                    Job* job = jobs;
                    jobs = job->Next;
                    RunJob(job);
                    Push(ref shared->Done, job);
                }
                byte done = 1;
                write(shared->DoneWrite, &done, 1);
            }
        }
    }

    static void RunJob(Job* job)
    {
        unsafe
        {
            // SAFETY: the buffer has Length bytes, held by the waiting side.
            void* buffer = (void*)job->Buffer;
            nuint length = (nuint)job->Length;
            job->Result = job->Kind switch
            {
                Read => (nint)fread(buffer, 1, length, job->Stream),
                Write => (nint)fwrite(buffer, 1, length, job->Stream),
                _ => fflush(job->Stream),
            };
        }
    }

    // A lock-free list of jobs: Push from one thread, TakeAll from the other.
    static void Push(ref long head, Job* job)
    {
        unsafe
        {
            // SAFETY: the job is the pushing thread's until the exchange.
            long old;
            do
            {
                old = Volatile.Read(ref head);
                job->Next = (Job*)old;
            }
            while (Interlocked.CompareExchange(ref head, (long)job, old) != old);
        }
    }

    static Job* TakeAll(ref long head)
    {
        long old;
        do
            old = Volatile.Read(ref head);
        while (old != 0 && Interlocked.CompareExchange(ref head, 0, old) != old);
        return (Job*)old;
    }

    // Latest first to oldest first.
    static Job* Reverse(Job* list)
    {
        unsafe
        {
            // SAFETY: a list taken whole, the caller's alone.
            Job* reversed = null;
            while (list != null)
            {
                Job* next = list->Next;
                list->Next = reversed;
                reversed = list;
                list = next;
            }
            return reversed;
        }
    }

    // Where a Memory's bytes are, for the I/O thread.
    internal static nint Address(ReadOnlyMemory<byte> memory)
    {
        unsafe { return (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(memory.Span)); } // SAFETY: zero mode's arrays and strings don't move
    }
}

namespace System.IO
{
    public partial class FileStream
    {
        /// <summary>
        /// Reads up to buffer.Length bytes on another thread, while the other
        /// fibers and tasks run; 0 at the end. What's read ahead in the
        /// stream's buffer comes at once. One operation at a time.
        /// </summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(cancellationToken);
            EnsureOpen();
            if (!_canRead)
                throw new NotSupportedException("FileStream: opened without FileAccess.Read");
            if (buffer.IsEmpty)
                return new ValueTask<int>(0);
            if (_readPos < _readLen)
                return new ValueTask<int>(TakeReadAhead(buffer.Span));
            return ReadFromFileAsync(buffer);
        }

        async ValueTask<int> ReadFromFileAsync(Memory<byte> buffer)
        {
            _busy = true;
            if (_writeLen > 0)
            {
                await FlushWritesAsync();
                nint stream1 = _file.BeginIo("FileStream");
                await FileThread.RunAsync(FileThread.Flush, stream1, 0, 0); // C's rule: a write, a flush, then a read
                _file.EndIo();
            }
            _read = true;
            int n;
            if (_buffer == null || buffer.Length >= _buffer.Length)
            {
                // A big read goes straight to the file.
                nint stream2 = _file.BeginIo("FileStream");
                n = await FileThread.RunAsync(FileThread.Read, stream2, FileThread.Address(buffer), buffer.Length);
                _file.EndIo();
            }
            else
            {
                _readPos = 0;
                nint stream3 = _file.BeginIo("FileStream");
                _readLen = await FileThread.RunAsync(FileThread.Read, stream3, FileThread.Address(_buffer), _buffer.Length);
                _file.EndIo();
                n = TakeReadAhead(buffer.Span);
            }
            _busy = false;
            return n;
        }

        /// <summary>
        /// Writes all of buffer: into the stream's buffer if it fits, at once,
        /// or to the file on another thread, while the other fibers and tasks
        /// run. One operation at a time.
        /// </summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled(cancellationToken);
            EnsureOpen();
            if (!_canWrite)
                throw new NotSupportedException("FileStream: opened without FileAccess.Write");
            if (!_read && _buffer != null && buffer.Length < _buffer.Length && _writeLen + buffer.Length <= _buffer.Length)
            {
                buffer.Span.CopyTo(new Span<byte>(_buffer, _writeLen, buffer.Length));
                _writeLen += buffer.Length;
                return default;
            }
            return WriteToFileAsync(buffer);
        }

        async ValueTask WriteToFileAsync(ReadOnlyMemory<byte> buffer)
        {
            _busy = true;
            if (_read)
                LeaveReading();
            if (_buffer != null && _writeLen + buffer.Length > _buffer.Length)
                await FlushWritesAsync();
            if (_buffer == null || buffer.Length >= _buffer.Length)
            {
                await WriteOutAsync(buffer); // too big to be worth buffering
            }
            else
            {
                buffer.Span.CopyTo(new Span<byte>(_buffer, _writeLen, buffer.Length));
                _writeLen += buffer.Length;
            }
            _busy = false;
        }

        /// <summary>Writes out what's buffered, on another thread.</summary>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            EnsureOpen();
            return FlushToFileAsync();
        }

        async Task FlushToFileAsync()
        {
            _busy = true;
            await FlushWritesAsync();
            nint stream4 = _file.BeginIo("FileStream");
            await FileThread.RunAsync(FileThread.Flush, stream4, 0, 0);
            _file.EndIo();
            _busy = false;
        }

        /// <summary>Writes out what's buffered, on another thread, then closes the file.</summary>
        public override async ValueTask DisposeAsync()
        {
            if (!_file.IsOpen)
                return;
            EnsureOpen();
            _busy = true;
            await FlushWritesAsync();
            _busy = false;
            Dispose();
        }

        async ValueTask FlushWritesAsync()
        {
            if (_writeLen == 0)
                return;
            int length = _writeLen;
            _writeLen = 0;
            await WriteOutAsync(new ReadOnlyMemory<byte>(_buffer, 0, length));
        }

        async ValueTask WriteOutAsync(ReadOnlyMemory<byte> bytes)
        {
            nint stream5 = _file.BeginIo("FileStream");
            int written = await FileThread.RunAsync(FileThread.Write, stream5, FileThread.Address(bytes), bytes.Length);
            _file.EndIo();
            if (written != bytes.Length)
            {
                // The operation ends here: a Catch.Run may get the exception
                // and go on using, or disposing, the stream (no finally
                // block clears the flag).
                _busy = false;
                throw new IOException(string.Concat("FileStream: couldn't write to '", Name, "'"));
            }
        }
    }
}

public partial struct File
{
    /// <summary>
    /// Reads a whole file, on another thread, while the other fibers and
    /// tasks run. A file that can't be opened ends the program, as .NET's
    /// exception would; check with Exists or FileStream.TryOpen first.
    /// </summary>
    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<byte[]>(cancellationToken);
        return ReadBytesAsync(path);
    }

    static async Task<byte[]> ReadBytesAsync(string path)
    {
        // Unbuffered (1): each read goes straight into the result.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        long length = stream.Length;
        if (length > int.MaxValue)
            throw new IOException(string.Concat("File.ReadAllBytesAsync: '", path, "' is over 2 GB"));
        // Some files say 0 and have more (Linux's /proc): read until the end.
        byte[] bytes = new byte[length > 0 ? (int)length : 4096];
        int total = 0;
        while (true)
        {
            if (total == bytes.Length)
            {
                if (length > 0)
                    break; // all of it, as the length said
                byte[] more = new byte[bytes.Length * 2];
                bytes.AsSpan().CopyTo(more);
                bytes = more;
            }
            int n = await stream.ReadAsync(bytes.AsMemory(total));
            if (n == 0)
                break;
            total += n;
        }
        if (total == bytes.Length)
            return bytes;
        byte[] some = new byte[total];
        new ReadOnlySpan<byte>(bytes, 0, total).CopyTo(some);
        return some;
    }

    /// <summary>Reads a whole UTF-8 file into a string, on another thread. Bytes that aren't UTF-8 become U+FFFD.</summary>
    public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<string>(cancellationToken);
        return ReadTextAsync(path);
    }

    static async Task<string> ReadTextAsync(string path)
    {
        byte[] bytes = await ReadBytesAsync(path);
        // UTF-8 never decodes to more UTF-16 chars than it has bytes.
        char[] chars = new char[bytes.Length];
        int used = Utf8.Decode(bytes, chars);
        return string.FromChars(new ReadOnlySpan<char>(chars, 0, used));
    }

    /// <summary>Writes bytes to a file, replacing it if it exists, on another thread. Failing ends the program.</summary>
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
        => WriteAllBytesAsync(path, new ReadOnlyMemory<byte>(bytes ?? throw new ArgumentNullException(nameof(bytes))), cancellationToken);

    /// <summary>Writes bytes to a file, replacing it if it exists, on another thread. Failing ends the program.</summary>
    public static Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        return WriteBytesAsync(path, FileMode.Create, bytes);
    }

    /// <summary>Writes text to a file as UTF-8, replacing it if it exists, on another thread. Failing ends the program.</summary>
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        return WriteBytesAsync(path, FileMode.Create, Utf8Bytes(contents));
    }

    /// <summary>Adds text to the end of a file as UTF-8, creating it if needed, on another thread. Failing ends the program.</summary>
    public static Task AppendAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        return WriteBytesAsync(path, FileMode.Append, Utf8Bytes(contents));
    }

    static async Task WriteBytesAsync(string path, FileMode mode, ReadOnlyMemory<byte> bytes)
    {
        var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read, 1);
        await stream.WriteAsync(bytes);
        await stream.DisposeAsync();
    }

    // The text as UTF-8, in a new array.
    static byte[] Utf8Bytes(string contents)
    {
        ReadOnlySpan<char> text = (contents ?? "").AsSpan();
        // At most 3 bytes per UTF-16 char.
        byte[] bytes = new byte[text.Length * 3];
        int next = 0;
        int length = Utf8.Encode(text, ref next, bytes);
        if (length == bytes.Length)
            return bytes;
        byte[] exact = new byte[length];
        new ReadOnlySpan<byte>(bytes, 0, length).CopyTo(exact);
        return exact;
    }
}
