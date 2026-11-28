// task { }, async { } and MailboxProcessor, on lib/'s tasks and fibers
// (lib/Tasks.cs).
//
// Both builders pass continuations (code that takes "what comes next"),
// rather than compiling to state machines as FSharp.Core's task does. When a
// computation waits for a task that isn't done, a Runner (one per running
// computation) hands itself to lib's AsyncTaskMethodBuilder as the "state
// machine", as C#'s async methods do: lib boxes it once, and when the task
// completes, the dispatcher fiber calls it, which calls the continuation. So
// waiting costs what it does in C#, plus the continuation's closure, and
// code after a wait never runs inside whatever completed the task.
//
// Code that doesn't wait (a finished task, a Receive with messages queued)
// goes on at once, on the same stack. To keep a long run of those from
// overflowing it, every hundredth step goes through the dispatcher instead
// (Runner.Step), as FSharp.Core's async does with its trampoline.
//
// Exceptions: zero mode's `throw` ends the program unless a Catch.Run
// (lib/Catch.cs) around it takes it back. Inside a `try ... with` or
// `try ... finally` (and `use`), each piece of the computation, from its
// start or a wait to its next wait, runs under a Catch.Run (Runner.Guard),
// and what it catches goes to the handler current at the throw: the Runner
// keeps it (Error), and the builders set it on the way into a try and put it
// back on the way out. Outside any, a throw goes to the computation's own
// handler: what it was started with (below).
//
// Differences from .NET, besides lib's (one thread, fibers):
// - What a task { } or a computation started with StartAsTask doesn't catch
//   faults its task, as in .NET; awaiting it throws that again (lib's Task
//   holds the exception itself, not an AggregateException). One started
//   with Start or StartImmediate, or an agent's body, ends the program.
//   RunSynchronously, StartChild and Parallel pass theirs on. There's no
//   MailboxProcessor.Error.
// - Async.Start, StartAsTask, StartChild and Parallel's computations run on
//   the same thread as everything else, taking turns where they wait.
//   Nothing runs until the program waits (RunSynchronously, PostAndReply,
//   Task.Wait, Fiber.Run): Main returning ends them, as it ends .NET's
//   thread pool work.
#nowarn "3370"

namespace Microsoft.FSharp.Control

    open System
    open System.Threading
    open System.Threading.Tasks
    open System.Runtime.CompilerServices

    /// Runs one computation from wait to wait: what lib's dispatcher calls
    /// when the task it waits for has completed.
    // Public only because TaskCode names it.
    [<Sealed; AllowNullLiteral>]
    type Runner internal () =
        // The object allocator where the computation started: lib's objects
        // for its waits come from it, not from an arena the computation is in
        // at the time (Async.InArena), which may be freed before they are.
        let home = Arena.CurrentAllocator()
        // The allocator the computation has now, outside synchronous scopes:
        // home, or the arena of the Async.InArena it's in. A throw caught by
        // Guard goes to its handler with this one in effect: Catch.Run puts
        // back the one from when its frame began, which may be an arena
        // whose scope has ended since.
        let mutable scoped = home
        // What lib's dispatcher resumes: made at the first wait, at home.
        [<DefaultValue>] val mutable internal Waiting: RunnerStep
        [<DefaultValue>] val mutable Next: unit -> unit
        // Where a throw goes now: the innermost try's handler. Null outside
        // any: a throw goes on out.
        [<DefaultValue>] val mutable Error: exn -> unit
        // The allocator in effect when the computation last waited: put back
        // when it goes on, so each of its pieces allocates where it did
        // before the wait, whatever ran in between.
        [<DefaultValue>] val mutable internal Resume: Arena.AllocatorSetting
        // Steps taken on this thread's stack since it last started afresh.
        [<ThreadStatic; DefaultValue>] static val mutable private steps: int

        member internal _.Home = home
        member internal _.Scoped with get () = scoped and set value = scoped <- value

        // The step lib's dispatcher resumes, once it's told to.
        member private r.Suspend(k: unit -> unit) : RunnerStep =
            r.Next <- k
            r.Resume <- Arena.CurrentAllocator()
            if isNull r.Waiting then
                Arena.SetAllocator home
                r.Waiting <- RunnerStep(r)
                Arena.SetAllocator r.Resume
            r.Waiting

        /// Goes on with k once t (not completed) has: the dispatcher resumes
        /// this runner then. Lib's own Continuation, so a wait makes nothing.
        member internal r.Await(t: Task, k: unit -> unit) = t.AddObserver(r.Suspend k)

        /// Goes on with k from the dispatcher, after what's ready already.
        member internal r.Yield(k: unit -> unit) = AsyncMethods.Ready(r.Suspend k)

        /// Runs f with the allocator where the computation started: for
        /// objects that may outlive an arena it's in now.
        member internal _.AtHome(f: unit -> 'T) : 'T =
            let current = Arena.CurrentAllocator()
            Arena.SetAllocator home
            let result = f ()
            Arena.SetAllocator current
            result

        /// Goes on with k: now, or every hundredth time from the dispatcher,
        /// on a fresh stack.
        member internal r.Step(k: unit -> unit) =
            if Runner.steps >= 100 then
                Runner.steps <- 0
                r.Yield k
            else
                Runner.steps <- Runner.steps + 1
                k ()

        // Catch.Run's form with a state, so a guarded step makes no delegate
        // (an Action per step would be 48 bytes, outside any arena).
        static member private Invoke(step: byref<unit -> unit>) = step ()
        static member val private InvokeStep = CatchAction<unit -> unit>(fun step -> Runner.Invoke(&step))

        /// Runs f, inside a try (Error set) under a Catch.Run, whose throw
        /// goes to Error as it is then.
        member internal r.Guard(f: unit -> unit) =
            if isNull (box r.Error) then
                f ()
            else
                let mutable step = f
                match Catch.Run(&step, Runner.InvokeStep) with
                | null -> ()
                | e ->
                    Arena.SetAllocator scoped
                    r.Fail e

        /// Hands e to the current handler, or throws it on out.
        member internal r.Fail(e: exn) =
            let handler = r.Error
            if isNull (box handler) then raise e else handler e

        /// The dispatcher goes on with the computation.
        member internal r.Resumed() =
            Runner.steps <- 0
            let k = r.Next
            r.Next <- Unchecked.defaultof<_>
            let dispatcher = Arena.CurrentAllocator()
            Arena.SetAllocator r.Resume
            r.Guard k
            Arena.SetAllocator dispatcher

    /// A Runner as lib's dispatcher sees it: what a task it waits for tells
    /// when it completes (Observer.Completed queues it), and what the
    /// dispatcher calls then.
    and [<Sealed; AllowNullLiteral>] internal RunnerStep(runner: Runner) =
        inherit Continuation()
        override _.Resume() = runner.Resumed()

    // ---------------------------------------------------------------- async

    /// What a running async computation carries: its cancellation token,
    /// its runner, and what to do when it's canceled (which a try ... finally
    /// changes while its body runs).
    [<Sealed; AllowNullLiteral>]
    type internal AsyncContext(token: CancellationToken, runner: Runner, canceled: unit -> unit) =
        member _.Token = token
        member _.Runner = runner
        member val OnCancel = canceled with get, set
        member c.Cancel() = c.OnCancel()

        /// Goes on with k, unless the token was canceled.
        member c.Step(k: unit -> unit) =
            if token.IsCancellationRequested then c.Cancel() else runner.Step k

        /// Goes on with k once t has completed.
        member c.Await(t: Task, k: unit -> unit) =
            if t.IsCompleted then c.Step k else runner.Await(t, k)

    /// A computation that runs when started, and can wait without blocking
    /// the thread: Async.RunSynchronously, Async.Start, or let! in another.
    [<Sealed>]
    type Async<'T> internal (run: AsyncContext -> ('T -> unit) -> unit) =
        member internal _.Invoke(c: AsyncContext, k: 'T -> unit) = run c k

    module internal AsyncCode =
        let inline make run = Async<'T>(run)

        let ret (v: 'T) = make (fun _ k -> k v)

        let bind (a: Async<'T>) (f: 'T -> Async<'U>) =
            make (fun c k -> a.Invoke(c, fun v -> c.Step(fun () -> (f v).Invoke(c, k))))

        // Nothing waits for the computation (Start, StartImmediate, an
        // agent): what it doesn't catch ends the program, as an async void
        // method's throw does in lib (and Async.Start's crashes .NET). It
        // can't go on out: lib runs each step on the dispatcher under a
        // Catch of its own, which would fault a task nobody looks at.
        let unhandled : exn -> unit =
            fun e -> Environment.FailFast(String.Concat("Unhandled exception: ", e.Message))

        // Runs a now, until it first waits. failed gets what it throws and
        // doesn't catch.
        let start (a: Async<'T>) (token: CancellationToken) (k: 'T -> unit) (canceled: unit -> unit) (failed: exn -> unit) =
            let r = Runner()
            r.Error <- failed
            if token.IsCancellationRequested then
                canceled ()
            else
                // A piece that ends waiting inside Async.InArena leaves its
                // arena's allocator in effect: this caller goes on with its own.
                let before = Arena.CurrentAllocator()
                r.Guard(fun () -> a.Invoke(AsyncContext(token, r, canceled), k))
                Arena.SetAllocator before

        // Starts a from the dispatcher, after the other fibers have run.
        let queue (a: Async<'T>) (token: CancellationToken) (k: 'T -> unit) (canceled: unit -> unit) (failed: exn -> unit) =
            let r = Runner()
            r.Error <- failed
            r.Yield(fun () ->
                if token.IsCancellationRequested then canceled ()
                else a.Invoke(AsyncContext(token, r, canceled), k))

        // try ... with: handler gets what body throws, and what follows the
        // try runs with the handler that was current before.
        let tryWith (body: Async<'T>) (handler: exn -> Async<'T>) =
            make (fun c k ->
                let r = c.Runner
                let outer = r.Error
                r.Error <- (fun e ->
                    r.Error <- outer
                    r.Guard(fun () -> (handler e).Invoke(c, k)))
                r.Guard(fun () ->
                    body.Invoke(c, fun v ->
                        r.Error <- outer
                        k v)))

        // try ... finally: compensation runs after body, whether it returns,
        // throws (then the exception goes on out) or is canceled.
        let tryFinally (body: Async<'T>) (compensation: unit -> unit) =
            make (fun c k ->
                let r = c.Runner
                let outer = r.Error
                let outerCancel = c.OnCancel
                let restore () =
                    r.Error <- outer
                    c.OnCancel <- outerCancel
                r.Error <- (fun e ->
                    restore ()
                    r.Guard(fun () ->
                        compensation ()
                        r.Fail e))
                c.OnCancel <- (fun () ->
                    restore ()
                    r.Guard(fun () ->
                        compensation ()
                        c.Cancel()))
                r.Guard(fun () ->
                    body.Invoke(c, fun v ->
                        restore ()
                        compensation ()
                        k v)))

        // Runs f with that allocator in effect.
        let withAllocator (setting: Arena.AllocatorSetting) (f: unit -> 'R) : 'R =
            let current = Arena.CurrentAllocator()
            Arena.SetAllocator setting
            let result = f ()
            Arena.SetAllocator current
            result

        // The outcome of a computation started elsewhere, as an Async: its
        // result, its exception thrown here, or this one canceled. Its task,
        // and what completing it does in lib, use `home`: the allocator where
        // the waiting computation started, not an arena either side may be in
        // (Async.InArena).
        [<Sealed>]
        type Outcome<'T>(home: Arena.AllocatorSetting) =
            let source = withAllocator home (fun () -> TaskCompletionSource<'T>())
            member val Error: exn = null with get, set
            member val Canceled = false with get, set
            member o.Succeed(v: 'T) = withAllocator home (fun () -> source.SetResult v)
            member o.Fail(e: exn) =
                o.Error <- e
                withAllocator home (fun () -> source.SetResult Unchecked.defaultof<'T>)
            member o.Cancel() =
                o.Canceled <- true
                withAllocator home (fun () -> source.SetResult Unchecked.defaultof<'T>)
            member _.Task = source.Task
            member o.Await : Async<'T> =
                make (fun c k ->
                    c.Await(source.Task, fun () ->
                        if not (isNull o.Error) then c.Runner.Fail o.Error
                        elif o.Canceled then c.Cancel()
                        else k source.Task.Result))

        let mutable defaultSource = new CancellationTokenSource()

        // The result of a completed task; a canceled one ends the program,
        // as an await of it would in .NET (TaskCanceledException).
        let inline result (t: Task<'T>) = t.Result

        let awaitTask (t: Task<'T>) = make (fun c k -> c.Await(t, fun () -> k (result t)))

    /// Builds async { } computations.
    [<Sealed>]
    type AsyncBuilder internal () =
        member _.Zero() : Async<unit> = AsyncCode.ret ()
        member _.Return(v: 'T) : Async<'T> = AsyncCode.ret v
        member _.ReturnFrom(a: Async<'T>) : Async<'T> = a
        member _.Delay(f: unit -> Async<'T>) : Async<'T> = AsyncCode.make (fun c k -> (f ()).Invoke(c, k))
        member _.Bind(a: Async<'T>, f: 'T -> Async<'U>) : Async<'U> = AsyncCode.bind a f

        member _.Combine(a: Async<unit>, b: Async<'T>) : Async<'T> =
            AsyncCode.make (fun c k -> a.Invoke(c, fun () -> c.Step(fun () -> b.Invoke(c, k))))

        member _.While(cond: unit -> bool, body: Async<unit>) : Async<unit> =
            AsyncCode.make (fun c k ->
                let rec loop () =
                    if cond () then body.Invoke(c, fun () -> c.Step loop) else k ()
                loop ())

        member _.For(items: seq<'T>, body: 'T -> Async<unit>) : Async<unit> =
            AsyncCode.make (fun c k ->
                let e = items.GetEnumerator()
                let rec loop () =
                    if e.MoveNext() then
                        (body e.Current).Invoke(c, fun () -> c.Step loop)
                    else
                        e.Dispose()
                        k ()
                loop ())

        member _.TryFinally(body: Async<'T>, compensation: unit -> unit) : Async<'T> = AsyncCode.tryFinally body compensation

        member _.TryWith(body: Async<'T>, handler: exn -> Async<'T>) : Async<'T> = AsyncCode.tryWith body handler

        member b.Using(resource: 'R, body: 'R -> Async<'T>) : Async<'T> when 'R :> IDisposable =
            b.TryFinally(b.Delay(fun () -> body resource), fun () -> if not (isNull (box resource)) then resource.Dispose())

    /// Starts async computations and makes them from other things.
    [<Sealed; AbstractClass>]
    type Async =
        /// The token computations get when they're started without one.
        static member DefaultCancellationToken = AsyncCode.defaultSource.Token

        /// Cancels the computations started without a token of their own;
        /// later ones get a new token.
        static member CancelDefaultToken() =
            let old = AsyncCode.defaultSource
            AsyncCode.defaultSource <- new CancellationTokenSource()
            old.Cancel()

        /// The token of the computation that runs this.
        static member CancellationToken: Async<CancellationToken> = AsyncCode.make (fun c k -> k c.Token)

        /// Runs the computation and waits for its result. In a fiber, the
        /// fiber waits; outside one, the fibers run until it's done. Ends the
        /// program if it's canceled (OperationCanceledException in .NET).
        /// Throws what the computation throws and doesn't catch.
        static member RunSynchronously(computation: Async<'T>, ?cancellationToken: CancellationToken) : 'T =
            let token = defaultArg cancellationToken AsyncCode.defaultSource.Token
            let outcome = AsyncCode.Outcome<'T>(Arena.CurrentAllocator())
            AsyncCode.start computation token outcome.Succeed outcome.Cancel outcome.Fail
            let result = outcome.Task.Result
            if not (isNull outcome.Error) then raise outcome.Error
            if outcome.Canceled then raise (OperationCanceledException())
            result

        /// Starts the computation, to run when the program next waits.
        static member Start(computation: Async<unit>, ?cancellationToken: CancellationToken) : unit =
            let token = defaultArg cancellationToken AsyncCode.defaultSource.Token
            AsyncCode.queue computation token ignore ignore AsyncCode.unhandled

        /// Runs the computation now, on this stack, until it first waits.
        static member StartImmediate(computation: Async<unit>, ?cancellationToken: CancellationToken) : unit =
            let token = defaultArg cancellationToken AsyncCode.defaultSource.Token
            AsyncCode.start computation token ignore ignore AsyncCode.unhandled

        /// Starts the computation, to run when the program next waits: a task
        /// with its result, canceled if it is.
        static member StartAsTask(computation: Async<'T>, ?cancellationToken: CancellationToken) : Task<'T> =
            let token = defaultArg cancellationToken AsyncCode.defaultSource.Token
            let source = TaskCompletionSource<'T>()
            AsyncCode.queue computation token (fun v -> source.SetResult v) (fun () -> source.SetCanceled()) (fun e -> source.TrySetException e |> ignore)
            source.Task

        /// Runs the computation now, on this stack, until it first waits: a
        /// task with its result, canceled if it is.
        static member StartImmediateAsTask(computation: Async<'T>, ?cancellationToken: CancellationToken) : Task<'T> =
            let token = defaultArg cancellationToken AsyncCode.defaultSource.Token
            let source = TaskCompletionSource<'T>()
            AsyncCode.start computation token (fun v -> source.SetResult v) (fun () -> source.SetCanceled()) (fun e -> source.TrySetException e |> ignore)
            source.Task

        /// Starts the computation alongside this one (with its token); the
        /// result waits for it to finish.
        static member StartChild(computation: Async<'T>) : Async<Async<'T>> =
            AsyncCode.make (fun c k ->
                let outcome = AsyncCode.Outcome<'T>(c.Runner.Home)
                AsyncCode.queue computation c.Token outcome.Succeed outcome.Cancel outcome.Fail
                k outcome.Await)

        /// Choice1Of2 with the result, or Choice2Of2 with what it threw.
        static member Catch(computation: Async<'T>) : Async<Choice<'T, exn>> =
            AsyncCode.tryWith
                (AsyncCode.bind computation (fun v -> AsyncCode.ret (Choice1Of2 v)))
                (fun e -> AsyncCode.ret (Choice2Of2 e))

        /// Waits for the task; its result. A canceled task ends the program
        /// (TaskCanceledException in .NET).
        static member AwaitTask(task: Task<'T>) : Async<'T> = AsyncCode.awaitTask task

        /// Waits for the task. A canceled task ends the program
        /// (TaskCanceledException in .NET).
        static member AwaitTask(task: Task) : Async<unit> =
            AsyncCode.make (fun c k -> c.Await(task, fun () -> task.Wait(); k ()))

        /// Waits that many milliseconds without blocking the thread; stops
        /// early if the computation is canceled.
        static member Sleep(millisecondsDueTime: int) : Async<unit> =
            AsyncCode.make (fun c k ->
                let t = c.Runner.AtHome(fun () -> Task.Delay(millisecondsDueTime, c.Token))
                c.Await(t, fun () -> if t.IsCanceled then c.Cancel() else k ()))

        /// Waits that long without blocking the thread; stops early if the
        /// computation is canceled.
        static member Sleep(dueTime: TimeSpan) : Async<unit> =
            AsyncCode.make (fun c k ->
                let t = c.Runner.AtHome(fun () -> Task.Delay(dueTime, c.Token))
                c.Await(t, fun () -> if t.IsCanceled then c.Cancel() else k ()))

        /// Starts all the computations at once; their results in order, once
        /// all have finished. When one throws, the others are canceled and
        /// this throws that; canceled if one is.
        static member Parallel(computations: seq<Async<'T>>) : Async<'T[]> =
            AsyncCode.make (fun c k ->
                let all = Seq.toArray computations
                if all.Length = 0 then
                    k [||]
                else
                    let results = Array.zeroCreate all.Length
                    let left = ref all.Length
                    let outcome = AsyncCode.Outcome<'T[]>(c.Runner.Home)
                    let error = ref null
                    let canceled = ref false
                    let siblings = CancellationTokenSource.CreateLinkedTokenSource(c.Token, CancellationToken.None)
                    let finished () =
                        left.Value <- left.Value - 1
                        if left.Value = 0 then
                            if not (isNull error.Value) then outcome.Fail error.Value
                            elif canceled.Value then outcome.Cancel()
                            else outcome.Succeed results
                    for i in 0 .. all.Length - 1 do
                        AsyncCode.start all[i] siblings.Token
                            (fun v -> results[i] <- v; finished ())
                            (fun () -> canceled.Value <- true; finished ())
                            (fun e ->
                                if isNull error.Value then
                                    error.Value <- e
                                    siblings.Cancel()
                                finished ())
                    outcome.Await.Invoke(c, k))

        /// Runs the computations one after another; their results in order.
        static member Sequential(computations: seq<Async<'T>>) : Async<'T[]> =
            AsyncCode.make (fun c k ->
                let all = Seq.toArray computations
                let results = Array.zeroCreate all.Length
                let rec run i =
                    if i = all.Length then k results
                    else all[i].Invoke(c, fun v -> results[i] <- v; c.Step(fun () -> run (i + 1)))
                run 0)

        /// Runs the computation and drops its result.
        static member Ignore(computation: Async<'T>) : Async<unit> =
            AsyncCode.make (fun c k -> computation.Invoke(c, fun _ -> k ()))

        /// Lets the others run first.
        static member SwitchToThreadPool() : Async<unit> =
            AsyncCode.make (fun c k -> c.Runner.Yield(fun () -> c.Step k))

        /// A computation that calls `callback` with its three continuations,
        /// for a result, an exception (thrown here) or being canceled: the
        /// first one called, now or later, decides.
        static member FromContinuations(callback: ('T -> unit) * (exn -> unit) * (OperationCanceledException -> unit) -> unit) : Async<'T> =
            AsyncCode.make (fun c k ->
                let outcome = AsyncCode.Outcome<'T>(c.Runner.Home)
                let called = ref false
                let once () =
                    if called.Value then invalidOp "Async.FromContinuations: a continuation was called twice"
                    called.Value <- true
                callback (
                    (fun v -> once (); outcome.Succeed v),
                    (fun e -> once (); outcome.Fail e),
                    (fun _ -> once (); outcome.Cancel()))
                outcome.Await.Invoke(c, k))

    // ----------------------------------------------------------------- task

    /// Code inside task { }: given the runner and what comes next. (Not a
    /// delegate: fsc gives those BeginInvoke, which needs IAsyncResult.)
    type TaskCode<'T> = Runner -> ('T -> unit) -> unit

    /// Builds task { }: starts at once, on the caller's stack, and gives a
    /// Task with the result, as a C# async method does.
    [<Sealed>]
    type TaskBuilder internal () =
        static let wait (r: Runner) (t: Task) (k: unit -> unit) =
            if t.IsCompleted then r.Step k else r.Await(t, k)

        member _.Zero() : TaskCode<unit> = (fun _ k -> k ())
        member _.Return(v: 'T) : TaskCode<'T> = (fun _ k -> k v)
        member _.Delay(f: unit -> TaskCode<'T>) : TaskCode<'T> = (fun r k -> (f ()) r (k))

        member _.Combine(a: TaskCode<unit>, b: TaskCode<'T>) : TaskCode<'T> =
            (fun r k -> a r (fun () -> b r (k)))

        member _.Bind(t: Task<'T>, f: 'T -> TaskCode<'U>) : TaskCode<'U> =
            (fun r k -> wait r t (fun () -> (f (AsyncCode.result t)) r (k)))

        member _.Bind(t: Task, f: unit -> TaskCode<'U>) : TaskCode<'U> =
            (fun r k -> wait r t (fun () -> t.Wait(); (f ()) r (k)))

        /// Starts the computation (now, with the default token) and waits
        /// for it; what it throws is thrown here.
        member _.Bind(a: Async<'T>, f: 'T -> TaskCode<'U>) : TaskCode<'U> =
            (fun r k ->
                let outcome = AsyncCode.Outcome<'T>(r.Home)
                AsyncCode.start a AsyncCode.defaultSource.Token outcome.Succeed outcome.Cancel outcome.Fail
                wait r outcome.Task (fun () ->
                    if not (isNull outcome.Error) then r.Fail outcome.Error
                    elif outcome.Canceled then raise (OperationCanceledException())
                    else (f outcome.Task.Result) r k))

        // The other awaitables lib's methods return, as .NET's task { }
        // takes any: a ValueTask (Channel.SendAsync), Task.Yield(), and
        // ConfigureAwait's (SuppressThrowing).

        member b.Bind(t: ValueTask<'T>, f: 'T -> TaskCode<'U>) : TaskCode<'U> =
            if t.IsCompletedSuccessfully then (fun r k -> (f t.Result) r k)
            else b.Bind(t.AsTask(), f)

        member b.Bind(t: ValueTask, f: unit -> TaskCode<'U>) : TaskCode<'U> =
            if t.IsCompletedSuccessfully then (fun r k -> (f ()) r k)
            else b.Bind(t.AsTask(), f)

        /// Lets the others run first, as await Task.Yield() does.
        member _.Bind(_: System.Runtime.CompilerServices.YieldAwaitable, f: unit -> TaskCode<'U>) : TaskCode<'U> =
            (fun r k -> r.Yield(fun () -> (f ()) r k))

        member _.Bind(a: System.Runtime.CompilerServices.ConfiguredTaskAwaitable, f: unit -> TaskCode<'U>) : TaskCode<'U> =
            (fun r k ->
                let w = a.GetAwaiter()
                let next () =
                    w.GetResult()
                    (f ()) r k
                if w.IsCompleted then r.Step next else r.Await(w.m_task, next))

        member _.Bind(a: System.Runtime.CompilerServices.ConfiguredTaskAwaitable<'T>, f: 'T -> TaskCode<'U>) : TaskCode<'U> =
            (fun r k ->
                let w = a.GetAwaiter()
                let next () = (f (w.GetResult())) r k
                if w.IsCompleted then r.Step next else r.Await(w.m_task, next))

        member b.ReturnFrom(t: Task<'T>) : TaskCode<'T> = b.Bind(t, fun v -> b.Return v)
        member b.ReturnFrom(t: Task) : TaskCode<unit> = b.Bind(t, fun () -> b.Zero())
        member b.ReturnFrom(a: Async<'T>) : TaskCode<'T> = b.Bind(a, fun v -> b.Return v)
        member b.ReturnFrom(t: ValueTask<'T>) : TaskCode<'T> = b.Bind(t, fun v -> b.Return v)
        member b.ReturnFrom(t: ValueTask) : TaskCode<unit> = b.Bind(t, fun () -> b.Zero())

        member _.While(cond: unit -> bool, body: TaskCode<unit>) : TaskCode<unit> =
            (fun r k ->
                let rec loop () =
                    if cond () then body r (fun () -> r.Step loop) else k ()
                loop ())

        member _.For(items: seq<'T>, body: 'T -> TaskCode<unit>) : TaskCode<unit> =
            (fun r k ->
                let e = items.GetEnumerator()
                let rec loop () =
                    if e.MoveNext() then
                        (body e.Current) r (fun () -> r.Step loop)
                    else
                        e.Dispose()
                        k ()
                loop ())

        // As async's (AsyncCode.tryWith, tryFinally), without cancellation.
        member _.TryFinally(body: TaskCode<'T>, compensation: unit -> unit) : TaskCode<'T> =
            (fun r k ->
                let outer = r.Error
                r.Error <- (fun e ->
                    r.Error <- outer
                    r.Guard(fun () ->
                        compensation ()
                        r.Fail e))
                r.Guard(fun () ->
                    body r (fun v ->
                        r.Error <- outer
                        compensation ()
                        k v)))

        member _.TryWith(body: TaskCode<'T>, handler: exn -> TaskCode<'T>) : TaskCode<'T> =
            (fun r k ->
                let outer = r.Error
                r.Error <- (fun e ->
                    r.Error <- outer
                    r.Guard(fun () -> (handler e) r k))
                r.Guard(fun () ->
                    body r (fun v ->
                        r.Error <- outer
                        k v)))

        member b.Using(resource: 'R, body: 'R -> TaskCode<'T>) : TaskCode<'T> when 'R :> IDisposable =
            b.TryFinally(b.Delay(fun () -> body resource), fun () -> if not (isNull (box resource)) then resource.Dispose())

        member _.Run(code: TaskCode<'T>) : Task<'T> =
            let source = TaskCompletionSource<'T>()
            // As AsyncCode.start: the caller goes on with its own allocator.
            let before = Arena.CurrentAllocator()
            // What the code doesn't catch faults the task, as in .NET.
            let r = Runner()
            r.Error <- (fun e -> source.TrySetException e |> ignore)
            r.Guard(fun () -> code r (fun v -> source.SetResult v))
            Arena.SetAllocator before
            source.Task

    // ------------------------------------------------------------- mailbox

    /// Where a MailboxProcessor's reply goes: PostAndReply waits for it.
    [<Sealed>]
    type AsyncReplyChannel<'Reply> internal (waiter: Arena.AllocatorSetting, reply: 'Reply -> unit) =
        /// Sends the reply. A string is copied to where the waiting
        /// computation started, so a reply made inside Async.InArena
        /// outlives its arena.
        member _.Reply(value: 'Reply) =
            match box value with
            | :? string as text ->
                let copy: 'Reply = unbox (box (Arena.CopyStringWith(waiter, text)))
                AsyncCode.withAllocator waiter (fun () -> reply copy)
            | _ -> AsyncCode.withAllocator waiter (fun () -> reply value)

    /// With Arena.DebugChecks on when they're made, agents are listed here,
    /// and closing an arena { } or Async.InArena scope stops the program if a
    /// message made inside the scope is still in one's queue: the agent would
    /// take it after it's freed.
    module internal Mailboxes =
        [<Sealed; AllowNullLiteral>]
        type Entry(holdsFrom: Arena.OpenedObjects -> bool, next: Entry) =
            member _.HoldsFrom = holdsFrom
            member _.Next = next

        let mutable private agents: Entry = null

        let add (holdsFrom: Arena.OpenedObjects -> bool) = agents <- Entry(holdsFrom, agents)

        let checkBeforeFree (scope: Arena.OpenedObjects) =
            let mutable entry = agents
            while not (isNull entry) do
                if entry.HoldsFrom scope then
                    Environment.FailFast(
                        "MailboxProcessor: a message made inside an arena scope (arena { } or Async.InArena) is still in an agent's queue as the scope ends, "
                        + "so the agent would take it after it's freed. Make the message outside the scope (a [<Struct>] message is copied), "
                        + "or wait in the scope until the agent has taken it.")
                entry <- entry.Next

    [<Sealed; AllowNullLiteral>]
    type internal Message<'Msg>() =
        [<DefaultValue>] val mutable Value: 'Msg
        [<DefaultValue>] val mutable Next: Message<'Msg>

    /// An agent: a computation that takes the messages posted to it, in
    /// order, one at a time (Receive, Scan). Its body runs as an async
    /// computation on this thread, so it takes turns with the rest of the
    /// program where it waits.
    [<Sealed>]
    type MailboxProcessor<'Msg>(body: MailboxProcessor<'Msg> -> Async<unit>, ?cancellationToken: CancellationToken) =
        // The messages not yet taken, oldest first.
        let mutable first: Message<'Msg> = null
        let mutable last: Message<'Msg> = null
        let mutable count = 0
        // Set while the body waits for a message.
        let mutable arrived: TaskCompletionSource = null
        // StartScoped's loop's task, set while it waits for a message.
        let mutable wake: Task = null
        // Nodes taken off the queue, for Post to use again: a busy agent
        // makes none once it has as many as it ever holds at once.
        let mutable spare: Message<'Msg> = null
        let mutable started = false
        let token = defaultArg cancellationToken AsyncCode.defaultSource.Token
        // The allocator where the agent was made: its queue's nodes come from
        // it, not from an arena a poster is in (Async.InArena).
        let home = Arena.CurrentAllocator()
        let atHome (f: unit -> 'R) : 'R =
            let current = Arena.CurrentAllocator()
            Arena.SetAllocator home
            let result = f ()
            Arena.SetAllocator current
            result

        // Whether a queued message is from that scope (Mailboxes).
        let holdsFrom (scope: Arena.OpenedObjects) =
            let mutable m = first
            let mutable found = false
            while not found && not (isNull m) do
                found <- Arena.IsInScope(scope, m.Value)
                m <- m.Next
            found

        do if Arena.DebugChecks then atHome (fun () -> Mailboxes.add holdsFrom)

        // Takes the first message `pick` wants (oldest first).
        let take (pick: 'Msg -> 'R voption) : 'R voption =
            let mutable prev: Message<'Msg> = null
            let mutable m = first
            let mutable found = ValueNone
            while found.IsNone && not (isNull m) do
                match pick m.Value with
                | ValueSome _ as r ->
                    found <- r
                    if isNull prev then first <- m.Next else prev.Next <- m.Next
                    if Object.ReferenceEquals(m, last) then last <- prev
                    count <- count - 1
                    m.Value <- Unchecked.defaultof<'Msg>
                    m.Next <- spare
                    spare <- m
                | ValueNone ->
                    prev <- m
                    m <- m.Next
            found

        // Waits until `pick` takes a message, the timeout (milliseconds, -1
        // for none) passes (None), or the computation is canceled.
        let wait (pick: 'Msg -> 'R voption) (timeout: int) : Async<'R option> =
            AsyncCode.make (fun c k ->
                match take pick with
                | ValueSome r -> k (Some r)
                | ValueNone when timeout = 0 -> k None
                | ValueNone ->
                    // Outside any arena the body is in: the timer can outlive it.
                    let r = c.Runner
                    let timer = r.AtHome(fun () -> if timeout > 0 then Task.Delay(timeout) else null)
                    let registration =
                        if c.Token.CanBeCanceled then
                            r.AtHome(fun () -> c.Token.Register(fun () -> if not (isNull arrived) then arrived.TrySetResult() |> ignore))
                        else
                            Unchecked.defaultof<_>
                    let rec attempt () =
                        match take pick with
                        | ValueSome r ->
                            registration.Dispose()
                            k (Some r)
                        | ValueNone when c.Token.IsCancellationRequested ->
                            registration.Dispose()
                            c.Cancel()
                        | ValueNone when not (isNull timer) && timer.IsCompleted ->
                            registration.Dispose()
                            k None
                        | ValueNone ->
                            let signal, t =
                                r.AtHome(fun () ->
                                    let signal = TaskCompletionSource()
                                    signal, (if isNull timer then signal.Task else Task.WhenAny(signal.Task, timer) :> Task))
                            arrived <- signal
                            c.Runner.Await(t, fun () ->
                                arrived <- null
                                attempt ())
                    attempt ())

        let orTimeout (what: string) (a: Async<'R option>) : Async<'R> =
            AsyncCode.bind a (fun r ->
                match r with
                | Some v -> AsyncCode.ret v
                | None -> raise (Exception(what + ": timed out")))

        /// How long Receive, Scan and PostAndReply wait without a timeout of
        /// their own, in milliseconds (-1: forever, the default).
        member val DefaultTimeout = -1 with get, set

        /// How many messages wait to be taken.
        member _.CurrentQueueLength = count

        /// Starts the body, to run when the program next waits.
        member x.Start() =
            if started then invalidOp "The MailboxProcessor has already been started."
            started <- true
            AsyncCode.queue (body x) token ignore ignore AsyncCode.unhandled

        /// Adds a message to the queue. One posted from inside Async.InArena
        /// must not be made in its arena, as the agent may take it later: a
        /// struct (copied) is fine, as is PostAndReply's.
        member _.Post(message: 'Msg) =
            // Not through atHome: its closure would be an object per Post.
            let current = Arena.CurrentAllocator()
            Arena.SetAllocator home
            let m =
                if isNull spare then
                    Message()
                else
                    let m = spare
                    spare <- m.Next
                    m.Next <- null
                    m
            m.Value <- message
            if isNull last then first <- m else last.Next <- m
            last <- m
            count <- count + 1
            if not (isNull arrived) then
                let a = arrived
                arrived <- null
                a.TrySetResult() |> ignore
            elif not (isNull wake) then
                let w = wake
                wake <- null
                w.Complete(false) |> ignore
            Arena.SetAllocator current

        // For StartScoped (fsharp/control/Arena.fs).
        member internal _.TryTakeFirst() : 'Msg voption =
            let m = first
            if isNull m then
                ValueNone
            else
                first <- m.Next
                if isNull first then last <- null
                count <- count - 1
                let value = m.Value
                m.Value <- Unchecked.defaultof<'Msg>
                m.Next <- spare
                spare <- m
                ValueSome value

        member internal _.Wake with set (task: Task) = wake <- task
        member internal _.Token = token
        member internal _.MarkStarted() =
            if started then invalidOp "The MailboxProcessor has already been started."
            started <- true

        /// Waits for the next message (in the body). A timeout ends the program
        /// (TimeoutException in .NET).
        member x.Receive(?timeout: int) : Async<'Msg> =
            orTimeout "MailboxProcessor.Receive" (x.TryReceive(defaultArg timeout x.DefaultTimeout))

        /// Waits for the next message (in the body); None after the timeout.
        member x.TryReceive(?timeout: int) : Async<'Msg option> =
            wait ValueSome (defaultArg timeout x.DefaultTimeout)

        /// Waits for the first message `scanner` returns Some for, leaving the
        /// others queued, and runs what it returned.
        member x.Scan(scanner: 'Msg -> Async<'T> option, ?timeout: int) : Async<'T> =
            orTimeout "MailboxProcessor.Scan" (x.TryScan(scanner, defaultArg timeout x.DefaultTimeout))

        /// As Scan; None after the timeout.
        member x.TryScan(scanner: 'Msg -> Async<'T> option, ?timeout: int) : Async<'T option> =
            let picked = wait (fun m -> match scanner m with Some a -> ValueSome a | None -> ValueNone) (defaultArg timeout x.DefaultTimeout)
            AsyncCode.bind picked (fun a ->
                match a with
                | Some a -> AsyncCode.bind a (fun v -> AsyncCode.ret (Some v))
                | None -> AsyncCode.ret None)

        /// Posts the message `build` makes around a reply channel, and waits
        /// for the reply; None after the timeout.
        member x.PostAndTryAsyncReply(build: AsyncReplyChannel<'Reply> -> 'Msg, ?timeout: int) : Async<'Reply option> =
            let timeout = defaultArg timeout x.DefaultTimeout
            AsyncCode.make (fun c k ->
                // The message, its channel and the task it completes are made
                // where the agent was, so they outlive an arena this is in
                // (Async.InArena) if the agent takes the message after a
                // timeout; a string reply is copied to where this computation
                // started (not an arena it's in: the reply may come after).
                let waiter = c.Runner.Home
                let reply, message =
                    atHome (fun () ->
                        let source = TaskCompletionSource<'Reply>()
                        source.Task, build (AsyncReplyChannel(waiter, fun v -> source.TrySetResult v |> ignore)))
                x.Post message
                if timeout < 0 || reply.IsCompleted then
                    c.Await(reply, fun () -> k (Some reply.Result))
                else
                    let any = c.Runner.AtHome(fun () -> Task.WhenAny(reply, Task.Delay(timeout)))
                    c.Await(any, fun () -> k (if reply.IsCompleted then Some reply.Result else None)))

        /// Posts the message `build` makes around a reply channel, and waits
        /// for the reply. A timeout ends the program (TimeoutException in .NET).
        member x.PostAndAsyncReply(build: AsyncReplyChannel<'Reply> -> 'Msg, ?timeout: int) : Async<'Reply> =
            orTimeout "MailboxProcessor.PostAndAsyncReply" (x.PostAndTryAsyncReply(build, defaultArg timeout x.DefaultTimeout))

        /// PostAndAsyncReply, waited for as Async.RunSynchronously does.
        member x.PostAndReply(build: AsyncReplyChannel<'Reply> -> 'Msg, ?timeout: int) : 'Reply =
            Async.RunSynchronously(x.PostAndAsyncReply(build, defaultArg timeout x.DefaultTimeout))

        /// PostAndTryAsyncReply, waited for as Async.RunSynchronously does.
        member x.TryPostAndReply(build: AsyncReplyChannel<'Reply> -> 'Msg, ?timeout: int) : 'Reply option =
            Async.RunSynchronously(x.PostAndTryAsyncReply(build, defaultArg timeout x.DefaultTimeout))

        /// Makes an agent and starts it.
        static member Start(body: MailboxProcessor<'Msg> -> Async<unit>, ?cancellationToken: CancellationToken) =
            let agent = new MailboxProcessor<'Msg>(body, defaultArg cancellationToken AsyncCode.defaultSource.Token)
            agent.Start()
            agent

        interface IDisposable with
            member _.Dispose() = ()

    /// The builders, as FSharp.Core names them.
    [<AutoOpen>]
    module Builders =
        /// async { }: a computation that runs when started.
        let async = AsyncBuilder()

        /// task { }: runs at once, a Task with its result.
        let task = TaskBuilder()

        /// The same as task { } here: there's one thread.
        let backgroundTask = TaskBuilder()

namespace Microsoft.FSharp
    [<assembly: AutoOpen("Microsoft.FSharp.Control")>]
    do ()
