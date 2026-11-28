// Task.RunOnThread: runs a call that would block the fibers, a slow C
// function or a long computation, on another system thread, and gives a
// task to await, while this thread's fibers and async methods carry on.
//
//   int sum = await Task.RunOnThread(() => SumOfPrimes(10_000_000));
//   await Task.RunOnThread(() => compress(input, output));   // a C call
//
// Task.Run starts a fiber, which shares the thread: what it does between
// two waits stops every other fiber. RunOnThread is what .NET's Task.Run
// is, a call on a pool thread, and what .NET's LongRunning tasks are.
//
// The threads: a pool per thread that calls RunOnThread, started by the
// first call. A call goes to an idle worker; when every worker is busy, a
// new one starts, up to 64; past that, calls queue on the least busy. Workers
// stay for the program's life. When a call is done, its worker wakes
// Fiber.Run (through a pipe that Run polls, as lib/FileAsync.cs's I/O thread
// does), which completes the task on this thread. So awaiting it is like
// awaiting anything else, and Fiber.Run doesn't return while calls are out.
//
// The call runs on another thread, so what Parallel.For's body must be
// (lib/Threads.cs), it must be too:
// - it may touch only what nothing else touches meanwhile, or use
//   Interlocked: not this thread's tasks, channels, semaphores or fibers,
//   nor lib's collections or arenas if they're shared;
// - objects it makes with new are fine (zerocore's allocator is
//   thread-safe), and so is returning them;
// - static constructors aren't thread-safe: touch a type's statics first.
// A token is looked at when the call is made: a running call isn't stopped.
//
// Where there are no threads (DOS, UEFI, microcontrollers), the call runs at
// once, on this thread, and its task is done when RunOnThread returns.
// Not on WebAssembly, which has no fibers.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Threading.Tasks
{
    public partial class Task
    {
        /// <summary>
        /// Runs <paramref name="function"/> on another system thread; the task
        /// completes with its result. Meanwhile this thread's fibers run.
        /// </summary>
        public static Task<TResult> RunOnThread<TResult>(Func<TResult> function) => RunOnThread(function, default);

        /// <summary>RunOnThread, canceled instead if <paramref name="cancellationToken"/> is when it's called.</summary>
        public static Task<TResult> RunOnThread<TResult>(Func<TResult> function, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled<TResult>(cancellationToken);
            var call = new ThreadCall<TResult>(function);
            WorkerThreads.Run(call);
            return call;
        }

        /// <summary>
        /// Runs <paramref name="action"/> on another system thread; the task
        /// completes when it returns. Meanwhile this thread's fibers run.
        /// </summary>
        public static Task RunOnThread(Action action) => RunOnThread(action, default);

        /// <summary>RunOnThread, canceled instead if <paramref name="cancellationToken"/> is when it's called.</summary>
        public static Task RunOnThread(Action action, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled(cancellationToken);
            var call = new ThreadAction(action);
            WorkerThreads.Run(call);
            return call;
        }
    }

    // A call for a worker: Run there, then Done back on the calling thread.
    interface IThreadWork
    {
        void Run();
        void Done();
    }

    // The worker writes only _value; the task's own state is set in Done.
    sealed class ThreadCall<TResult>(Func<TResult> function) : Task<TResult>, IThreadWork
    {
        TResult _value = default!;

        public void Run() => _value = function();

        public void Done()
        {
            TResult value = _value;
            _value = default!;
            TrySetResult(value);
        }
    }

    sealed class ThreadAction(Action action) : Task, IThreadWork
    {
        public void Run() => action();

        public void Done() => Complete();
    }
}

// The pool. Each worker has its own job list and wake pipe: the calling
// thread pushes a job and writes a byte; the worker waits in read, takes
// its whole list, runs the jobs, and pushes each on the shared done list
// with a byte into the done pipe, which Fiber.Run polls (Fiber.IoWaiter).
// One list per worker keeps every list single-taker, so the lock-free
// lists need no more than a compare-and-swap.
sealed class WorkerThreads : Fiber.IoWaiter
{
    [DllImport("libc")] static unsafe extern int pipe(int* fds);
    [DllImport("libc")] static unsafe extern nint read(int fd, void* buffer, nuint count);
    [DllImport("libc")] static unsafe extern nint write(int fd, void* buffer, nuint count);
    [DllImport("libc")] static unsafe extern int poll(void* fds, nuint count, int timeout);
    [DllImport("libc")] static unsafe extern int pthread_create(nint* thread, void* attributes, delegate* unmanaged<void*, void*> start, void* argument);
    [DllImport("libc")] static unsafe extern void* calloc(nuint count, nuint size);
    [DllImport("libc")] static unsafe extern void free(void* memory);

    const int MaxWorkers = 64;
    const short POLLIN = 1;

    struct Job
    {
        public Job* Next;
        public Worker* Worker;
#if ZERO_DOTNET
        nint _work; // real .NET: a GC handle, since the GC neither sees a reference in calloc'd memory nor updates it when the object moves
        public IThreadWork Work { get => (IThreadWork)Fiber.Held(_work); set => Fiber.Hold(ref _work, value); }
#else
        public IThreadWork Work;
#endif
    }

    // From calloc, kept for the program's life.
    struct Worker
    {
        public long Queued;     // Job*, the latest first: for the worker
        public int WakeRead, WakeWrite;
        public Shared* Shared;
        public int Busy;        // jobs given and not yet done: the calling thread's alone
    }

    struct Shared
    {
        public long Done;       // Job*, the latest first: for Fiber.Run
        public int DoneRead, DoneWrite;
    }

    [ThreadStatic] static WorkerThreads? t_pool;
    [ThreadStatic] static bool t_none;  // no pipes or threads here

    readonly Shared* _shared;
    readonly Worker** _workers;
    int _count;
#if ZERO_DOTNET
    // Real .NET: the scheduler keeps this record's address, so it can't be in
    // an object the GC moves.
    readonly nint _recordMemory = Fiber.NewRecord();
    ref Fiber.Record _record { get { unsafe { return ref *(Fiber.Record*)_recordMemory; } } } // SAFETY: allocated with this object, freed by its finalizer
    ~WorkerThreads() => Fiber.FreeRecord(_recordMemory); // off the scheduler's list by now, or this object would still be referenced
#else
    Fiber.Record _record;               // on Fiber.Run's poll list while jobs are out
#endif
    int _out;
    bool _waiting;

    WorkerThreads(Shared* shared, Worker** workers)
    {
        _shared = shared;
        _workers = workers;
    }

    // Runs the work on a worker, or here if this thread can't have any.
    internal static void Run(IThreadWork work)
    {
        WorkerThreads? pool = t_pool ?? Start();
        if (pool == null)
        {
            work.Run();
            work.Done();
            return;
        }
        pool.Queue(work);
    }

    // The pool, with its first worker; null where there are no pipes or
    // threads.
    static WorkerThreads? Start()
    {
        if (t_none)
            return null;
        unsafe
        {
            // SAFETY: Shared and the worker array are from calloc, never freed.
            Shared* shared = (Shared*)calloc(1, (nuint)sizeof(Shared));
            Worker** workers = (Worker**)calloc(MaxWorkers, (nuint)sizeof(Worker*));
            int* fds = stackalloc int[2];
            // Pipes first: where they fail there are no threads either (and
            // UEFI's pthread_create would run the worker's loop in place).
            if (shared == null || workers == null || pipe(fds) != 0)
            {
                t_none = true;
                return null;
            }
            shared->DoneRead = fds[0];
            shared->DoneWrite = fds[1];
            var pool = new WorkerThreads(shared, workers);
            if (pool.AddWorker() == null)
            {
                t_none = true;
                return null;
            }
            if (Fiber.Poll == null)
                Fiber.Poll = &Poll;
            return t_pool = pool;
        }
    }

    static int Poll(void* fds, nuint count, int timeout)
    {
        unsafe { return poll(fds, count, timeout); } // SAFETY: Fiber.Run's array of count entries
    }

    // Starts one more worker; null if it can't.
    Worker* AddWorker()
    {
        unsafe
        {
            // SAFETY: the worker is from calloc and lives as long as its thread,
            // the program's life.
            if (_count == MaxWorkers)
                return null;
            Worker* worker = (Worker*)calloc(1, (nuint)sizeof(Worker));
            if (worker == null)
                return null;
            int* fds = stackalloc int[2];
            if (pipe(fds) != 0)
            {
                free(worker);
                return null;
            }
            worker->WakeRead = fds[0];
            worker->WakeWrite = fds[1];
            worker->Shared = _shared;
            nint thread = 0;
            if (pthread_create(&thread, null, &Work, worker) != 0)
                return null; // the pipe and worker are kept: rare, and small
            _workers[_count++] = worker;
            return worker;
        }
    }

    void Queue(IThreadWork work)
    {
        unsafe
        {
            // SAFETY: workers are _workers' first _count entries; the job is
            // the worker's from the push until it's on Done, then ours again.
            Worker* chosen = null, least = null;
            for (int i = 0; i < _count; i++)
            {
                Worker* worker = _workers[i];
                if (worker->Busy == 0)
                {
                    chosen = worker;
                    break;
                }
                if (least == null || worker->Busy < least->Busy)
                    least = worker;
            }
            if (chosen == null)
                chosen = AddWorker();
            if (chosen == null)
                chosen = least;
            Job* job = (Job*)calloc(1, (nuint)sizeof(Job));
            if (job == null)
                Environment.FailFast("Task.RunOnThread: out of memory");
            job->Worker = chosen;
            job->Work = work;
            chosen->Busy++;
            Push(ref chosen->Queued, job);
            byte wake = 1;
            write(chosen->WakeWrite, &wake, 1); // after the push: the worker sees the job
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

    // A worker said some jobs are done (poll found its byte).
    internal override void Ready(short revents)
    {
        _waiting = false;
        unsafe
        {
            // SAFETY: the jobs on Done are back from their workers, which are
            // done with them.
            byte* bytes = stackalloc byte[64];
            read(_shared->DoneRead, bytes, 64); // there is something: no wait
            Job* done = Reverse(TakeAll(ref _shared->Done));
            while (done != null)
            {
                Job* job = done;
                done = job->Next;
                IThreadWork work = job->Work;
                job->Worker->Busy--;
                job->Work = null!;
                free(job);
                _out--;
                work.Done();
            }
        }
        if (_out > 0 && !_waiting)
            Wait();
    }

    // A worker thread: waits for jobs and runs them, for the program's life.
    [UnmanagedCallersOnly]
    static void* Work(void* argument)
    {
        unsafe
        {
            // SAFETY: the worker and Shared live as long as the program; a job
            // is the worker's until pushed on Done.
            Worker* worker = (Worker*)argument;
            Shared* shared = worker->Shared;
            byte* bytes = stackalloc byte[64];
            while (true)
            {
                nint got = read(worker->WakeRead, bytes, 64);
                if (got == 0)
                    return null; // the pipe was closed
                Job* jobs = Reverse(TakeAll(ref worker->Queued));
                while (jobs != null)
                {
                    Job* job = jobs;
                    jobs = job->Next;
                    job->Work.Run();
                    Push(ref shared->Done, job);
                    byte done = 1;
                    write(shared->DoneWrite, &done, 1); // each as it's done
                }
            }
        }
    }

    // A lock-free list of jobs: Push from any thread, TakeAll from one.
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
}
