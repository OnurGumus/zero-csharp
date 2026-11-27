// ThreadCompletionSource: a TaskCompletionSource whose task can be completed
// from any thread, such as a C library's callback thread or a thread you
// started, and awaited on the thread that made it.
//
//   var done = new ThreadCompletionSource<int>();       // on the fibers' thread
//   start_download(url, &OnFinished, ...);               // C calls back elsewhere
//   int status = await done.Task;
//
//   [UnmanagedCallersOnly]
//   static void OnFinished(int status) => s_done.SetResult(status); // any thread
//
// A plain TaskCompletionSource belongs to its thread, as tasks, channels
// and fibers do: nothing there is locked, so completing it from another
// thread corrupts the waiters' lists. This one is safe from any thread: the
// result goes on a lock-free list and a byte into a pipe that the making
// thread's Fiber.Run polls, as lib/FileAsync.cs's I/O thread does. Fiber.Run
// then completes the task there, so its continuations and woken fibers run
// on their own thread. Until then the task isn't completed, even seen from
// the thread that set it.
//
// A source that isn't completed yet keeps its thread's Fiber.Run waiting
// (that's what lets it wait for the other thread), so complete or cancel
// every one you make, or the program won't end. Only the first SetResult,
// SetCanceled or Try* call wins, whichever thread makes it.
//
// Where there are no pipes or threads (DOS, UEFI, microcontrollers), the
// task completes at once, as a TaskCompletionSource's does. Not on
// WebAssembly, which has no fibers.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Threading.Tasks
{
    /// <summary>A TaskCompletionSource that any thread can complete; its task is awaited on the thread that made it.</summary>
    public sealed class ThreadCompletionSource : ThreadCompletion
    {
        readonly Task _task = new Task();

        /// <summary>The task, for the thread that made this source.</summary>
        public Task Task => _task;

        /// <summary>Completes the task (from any thread). Stops the program if it already was.</summary>
        public void SetResult()
        {
            if (!TrySetResult())
                Environment.FailFast("ThreadCompletionSource.SetResult: the task has already been completed");
        }

        /// <summary>Completes the task (from any thread); false if it already was.</summary>
        public bool TrySetResult() => Claim(canceled: false) && Post();

        /// <summary>Cancels the task (from any thread). Stops the program if it was already completed.</summary>
        public void SetCanceled()
        {
            if (!TrySetCanceled())
                Environment.FailFast("ThreadCompletionSource.SetCanceled: the task has already been completed");
        }

        /// <summary>Cancels the task (from any thread); false if it was already completed.</summary>
        public bool TrySetCanceled() => Claim(canceled: true) && Post();

        internal override void Deliver(bool canceled) => _task.Complete(canceled);
    }

    /// <summary>A TaskCompletionSource&lt;TResult&gt; that any thread can complete; its task is awaited on the thread that made it.</summary>
    public sealed class ThreadCompletionSource<TResult> : ThreadCompletion
    {
        readonly Task<TResult> _task = new Task<TResult>();
        TResult _result = default!;

        /// <summary>The task, for the thread that made this source.</summary>
        public Task<TResult> Task => _task;

        /// <summary>Completes the task with <paramref name="result"/> (from any thread). Stops the program if it already was.</summary>
        public void SetResult(TResult result)
        {
            if (!TrySetResult(result))
                Environment.FailFast("ThreadCompletionSource.SetResult: the task has already been completed");
        }

        /// <summary>Completes the task with <paramref name="result"/> (from any thread); false if it already was.</summary>
        public bool TrySetResult(TResult result)
        {
            if (!Claim(canceled: false))
                return false;
            _result = result; // before Post: the push publishes it
            return Post();
        }

        /// <summary>Cancels the task (from any thread). Stops the program if it was already completed.</summary>
        public void SetCanceled()
        {
            if (!TrySetCanceled())
                Environment.FailFast("ThreadCompletionSource.SetCanceled: the task has already been completed");
        }

        /// <summary>Cancels the task (from any thread); false if it was already completed.</summary>
        public bool TrySetCanceled() => Claim(canceled: true) && Post();

        internal override void Deliver(bool canceled)
        {
            if (canceled)
            {
                _task.Complete(true);
                return;
            }
            TResult result = _result;
            _result = default!;
            _task.TrySetResult(result);
        }
    }

    /// <summary>What the two ThreadCompletionSources share: claiming, and the trip back to the making thread.</summary>
    public abstract class ThreadCompletion
    {
        // 0: not completed; 1: claimed by a Set call, result to follow;
        // 2: canceled claim. Only the first compare-and-swap from 0 wins.
        int _state;
        // On the making thread's inbox list once claimed; null where there's
        // no inbox (no pipes or threads), and the task completes at once.
        readonly unsafe ThreadInbox.Node* _node;

        private protected ThreadCompletion()
        {
            unsafe { _node = ThreadInbox.Expect(this); } // SAFETY: freed when delivered
        }

        // Done on the making thread: completes the task.
        internal abstract void Deliver(bool canceled);

        private protected bool Claim(bool canceled) =>
            Interlocked.CompareExchange(ref _state, canceled ? 2 : 1, 0) == 0;

        // Sends the claim to the making thread (or, with no inbox, delivers it).
        private protected bool Post()
        {
            unsafe
            {
                // SAFETY: the node is ours until pushed, the inbox's after.
                if (_node == null)
                    Deliver(Volatile.Read(ref _state) == 2);
                else
                    ThreadInbox.Push(_node);
            }
            return true;
        }

        internal bool Canceled => Volatile.Read(ref _state) == 2;
    }
}

// One per thread that makes ThreadCompletionSources: a lock-free list that
// any thread pushes claimed sources on, and a pipe whose byte wakes the
// thread's Fiber.Run (Fiber.IoWaiter), which takes the list and completes
// the tasks. Registered with Fiber.Run while sources are outstanding, so Run
// waits for them.
sealed class ThreadInbox : Fiber.IoWaiter
{
    [DllImport("libc")] static unsafe extern int pipe(int* fds);
    [DllImport("libc")] static unsafe extern nint read(int fd, void* buffer, nuint count);
    [DllImport("libc")] static unsafe extern nint write(int fd, void* buffer, nuint count);
    [DllImport("libc")] static unsafe extern int poll(void* fds, nuint count, int timeout);
    [DllImport("libc")] static unsafe extern void* calloc(nuint count, nuint size);
    [DllImport("libc")] static unsafe extern void free(void* memory);

    const short POLLIN = 1;

    // A source's ticket home, made on its thread, pushed by whichever thread
    // completes it. The source holds the node; the node holds the source.
    internal struct Node
    {
        public Node* Next;
        public Shared* Inbox;
        public System.Threading.Tasks.ThreadCompletion Source;
    }

    // From calloc, kept for the program's life: other threads touch this only.
    internal struct Shared
    {
        public long Head;       // Node*, the latest first
        public int ReadFd, WriteFd;
    }

    [ThreadStatic] static ThreadInbox? t_inbox;
    [ThreadStatic] static bool t_none;  // no pipes here

    readonly Shared* _shared;
    Fiber.Record _record;
    int _expected;                      // sources made and not yet delivered
    bool _waiting;

    ThreadInbox(Shared* shared) => _shared = shared;

    // A node for a new source, counted as outstanding; null where there are
    // no pipes (the source then completes at once).
    internal static Node* Expect(System.Threading.Tasks.ThreadCompletion source)
    {
        ThreadInbox? inbox = t_inbox ?? Start();
        if (inbox == null)
            return null;
        unsafe
        {
            // SAFETY: freed in Ready, once delivered.
            Node* node = (Node*)calloc(1, (nuint)sizeof(Node));
            if (node == null)
                Environment.FailFast("ThreadCompletionSource: out of memory");
            node->Inbox = inbox._shared;
            node->Source = source;
            inbox._expected++;
            if (!inbox._waiting)
                inbox.Wait();
            return node;
        }
    }

    static ThreadInbox? Start()
    {
        if (t_none)
            return null;
        unsafe
        {
            // SAFETY: Shared is from calloc and never freed; the fds are its.
            Shared* shared = (Shared*)calloc(1, (nuint)sizeof(Shared));
            int* fds = stackalloc int[2];
            if (shared == null || pipe(fds) != 0)
            {
                t_none = true;
                return null;
            }
            shared->ReadFd = fds[0];
            shared->WriteFd = fds[1];
            if (Fiber.Poll == null)
                Fiber.Poll = &Poll;
            return t_inbox = new ThreadInbox(shared);
        }
    }

    static int Poll(void* fds, nuint count, int timeout)
    {
        unsafe { return poll(fds, count, timeout); } // SAFETY: Fiber.Run's array of count entries
    }

    // From any thread: puts the node on its inbox and wakes the inbox's thread.
    internal static void Push(Node* node)
    {
        unsafe
        {
            // SAFETY: the node is the pushing thread's until the exchange; the
            // inbox lives as long as the program.
            Shared* inbox = node->Inbox;
            long old;
            do
            {
                old = Volatile.Read(ref inbox->Head);
                node->Next = (Node*)old;
            }
            while (Interlocked.CompareExchange(ref inbox->Head, (long)node, old) != old);
            byte wake = 1;
            write(inbox->WriteFd, &wake, 1); // after the push: the thread sees the node
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
            Fiber.WaitForIOAsync((Fiber.Record*)Unsafe.AsPointer(ref _record), _shared->ReadFd, POLLIN);
        }
    }

    // Some thread pushed (poll found its byte): complete those tasks here.
    internal override void Ready(short revents)
    {
        _waiting = false;
        unsafe
        {
            // SAFETY: nodes taken off the list are ours alone; each is freed
            // once its source is delivered.
            byte* bytes = stackalloc byte[64];
            read(_shared->ReadFd, bytes, 64); // there is something: no wait
            long taken;
            do
                taken = Volatile.Read(ref _shared->Head);
            while (taken != 0 && Interlocked.CompareExchange(ref _shared->Head, 0, taken) != taken);
            // Latest first: reverse, so tasks complete in the order they were set.
            Node* list = null;
            for (Node* at = (Node*)taken; at != null;)
            {
                Node* next = at->Next;
                at->Next = list;
                list = at;
                at = next;
            }
            while (list != null)
            {
                Node* node = list;
                list = node->Next;
                System.Threading.Tasks.ThreadCompletion source = node->Source;
                node->Source = null!;
                free(node);
                _expected--;
                source.Deliver(source.Canceled);
            }
        }
        if (_expected > 0 && !_waiting)
            Wait();
    }
}
