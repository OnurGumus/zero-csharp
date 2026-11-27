---
title: Run green threads
index: 27
---

# Run green threads

`Fiber` from `lib/Fibers.cs` runs many functions on one system thread, each on a stack of its own. They
take turns: a fiber runs until it yields, sleeps, waits on a channel or ends, and then the next one in
line runs. `Channel<T>` passes values between them, and `Pipe` streams bytes. Switching is
cooperative, so fibers need no locks among themselves. `samples/Fibers.cs` runs every example below (and `samples/Tasks.cs` the async ones);
`SmallFibers`, `SmallChannels` and `SmallScratch` test the same on machines too small for it.

Fibers work on Linux (x64 and arm64), macOS, Windows, UEFI, DOS, microcontrollers (the Raspberry Pi
Pico 2 and the Arduino Uno R4) and the Commodore 64. They need a way to switch stacks, so there are
none on WebAssembly.

## Take turns

Spawn a fiber with the state it needs and a `static` lambda, as with `Parallel.For`. `Fiber.Run`
runs the fibers until every one has ended:

<!-- sample: samples/Fibers.cs yield -->
```csharp
// Three fibers take turns: each logs its number and yields, so the
// log interleaves them, in the order they were spawned.
var log = new Log();
for (int id = 1; id <= 3; id++)
    Fiber.Spawn((log, id), static s =>
    {
        for (int round = 0; round < 3; round++)
        {
            s.log.Add(s.id);
            Fiber.Yield();
        }
    });
Fiber.Run();
```

Spawn copies the state onto the fiber's stack, so it allocates nothing beyond the stack itself. A
fiber can spawn more fibers. They join the end of the line.

## Pass values through channels

`Send` waits while a channel is full and `Receive` while it's empty. A channel made with a capacity
holds that many values before `Send` waits. One made without (`new Channel<int>()`) hands each value
straight from a sender to a receiver. `Close` ends the stream: `Receive` drains what's left and then
returns false.

<!-- sample: samples/Fibers.cs pipeline -->
```csharp
// A pipeline: one fiber produces numbers, one squares them, and one
// adds them up. Receive returns false once its channel is closed and
// drained, which ends each loop.
var stages = new Stages();
Fiber.Spawn(stages, static p =>
{
    for (int i = 1; i <= 100; i++)
        p.Numbers.Send(i);
    p.Numbers.Close();
});
Fiber.Spawn(stages, static p =>
{
    while (p.Numbers.Receive(out int n))
        p.Squares.Send(n * n);
    p.Squares.Close();
});
Fiber.Spawn(stages, static p =>
{
    while (p.Squares.Receive(out int square))
        p.Total += square;
});
Fiber.Run();
```

If every fiber left is waiting on a channel, none of them can run again, and `Run` stops the program
with a message saying so. `Send` and `Receive` wait only inside a fiber. Outside one, a call that would
wait stops the program.

## Stream bytes through a pipe

A `Pipe` carries bytes rather than values, through a buffer of fixed size that it takes from an arena
once. `Write` copies in what fits and waits for the reader to make room for the rest. `Read` returns
whatever is there, at least one byte, and waits while the pipe is empty. However far the writer
runs ahead, the pipe never holds more than its capacity:

<!-- sample: samples/Fibers.cs pipe -->
```csharp
// A pipe streams bytes through a buffer of fixed size from an arena:
// here 64 bytes carry 10,000. The writer waits whenever the buffer is
// full, so it never runs more than 64 bytes ahead of the reader.
Arena arena = Arena.Create(4096);
var transfer = new Transfer { Pipe = new Pipe(arena, 64) };
Fiber.Spawn(transfer, static t =>
{
    Span<byte> chunk = stackalloc byte[100];
    while (t.Sent < 10_000)
    {
        int n = Math.Min(chunk.Length, 10_000 - t.Sent);
        for (int i = 0; i < n; i++)
            chunk[i] = (byte)(t.Sent + i);
        t.Pipe.Write((ReadOnlySpan<byte>)chunk.Slice(0, n));
        t.Sent += n;
    }
    t.Pipe.Close();
});
Fiber.Spawn(transfer, static t =>
{
    Span<byte> buffer = stackalloc byte[37];
    int n;
    while ((n = t.Pipe.Read(buffer)) > 0)
    {
        for (int i = 0; i < n; i++)
            if (buffer[i] != (byte)(t.Received + i))
                t.Wrong++;
        t.Received += n;
        t.MostBuffered = Math.Max(t.MostBuffered, t.Pipe.Count + n);
    }
});
Fiber.Run();
```

`Close` works from either end. After it, `Read` returns what's left and then 0, and `Write` returns
false instead of waiting, so a reader that has seen enough can close the pipe to stop the writer.
`Write` also takes text, which goes in as UTF-8. The arena has to outlive every fiber that uses the
pipe, so don't take it from a fiber's `Arena.Scratch`, which is freed when that fiber ends. Several
fibers can write to one pipe, but a write bigger than the room left goes in as room appears, so
another fiber's bytes can land in the middle of it.

`AsStream` makes the pipe a `System.IO.Stream`, for code that takes one (see
[Use streams](use-streams.html)). Disposing the writer's stream closes the pipe, which ends the
reader's `CopyTo`:

<!-- sample: samples/Fibers.cs pipe-stream -->
```csharp
// AsStream makes the pipe a System.IO.Stream, for code that takes one:
// CopyTo here reads until the writer disposes its end.
var streamed = new Transfer
{
    Pipe = new Pipe(arena, 16),
    Copy = new System.IO.MemoryStream(),
};
Fiber.Spawn(streamed, static t =>
{
    using System.IO.Stream output = t.Pipe.AsStream();
    for (int i = 0; i < 10; i++)
        output.Write("0123456789"u8);
});
Fiber.Spawn(streamed, static t => t.Pipe.AsStream().CopyTo(t.Copy));
Fiber.Run();
```

## Sleep

`Fiber.Sleep` lets the other fibers run for at least that many milliseconds. When nothing else is
ready, the thread sleeps until the earliest deadline:

<!-- sample: samples/Fibers.cs sleep -->
```csharp
// Sleeping fibers wake in the order of their deadlines, whatever the
// order they were spawned in.
var woke = new Log();
foreach (int ms in (int[])[30, 10, 20])
    Fiber.Spawn((woke, ms), static s =>
    {
        Fiber.Sleep(s.ms);
        s.woke.Add(s.ms);
    });
long start = Clock.Monotonic;
Fiber.Run();
long took = (Clock.Monotonic - start) / 1_000_000;
```

## Fire on a timer

`System.Timers.Timer` (`lib/Timers.cs`) raises `Elapsed` every `Interval` milliseconds, as in .NET,
but on a fiber of its own rather than a thread pool thread. So a timer fires only while the thread
is in `Fiber.Run`, and `Run` returns once every timer has stopped and every other fiber has ended:

<!-- sample: samples/Timers.cs timer -->
```csharp
// Fires every 20 ms, on a fiber of its own, until its handler
// stops it. Fiber.Run returns once every timer has stopped.
var timer = new Timer(20);
int ticks = 0;
timer.Elapsed += (sender, e) =>
{
    Console.WriteLine($"tick {++ticks}");
    if (ticks == 5)
        timer.Stop();
};
timer.Start();
Fiber.Run();
```

`System.Threading.Timer` works the same way, with a callback, a due time and a period
(`Timeout.Infinite` for a timer that fires once, or `Change(Timeout.Infinite, Timeout.Infinite)` to
stop it).

`PeriodicTimer` has .NET's `WaitForNextTickAsync`, and also `WaitForNextTick()`, which blocks. In a
fiber it lets the other fibers run; outside one it sleeps the thread, so it needs no `Fiber.Run`.
The async one waits on `Task.Delay`'s timer, with no fiber of its own:

<!-- sample: samples/Tasks.cs periodic-async -->
```csharp
// Ticks every 10 ms without a fiber of its own; Dispose ends the loop.
using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10)))
{
    int ticks = 0;
    while (await timer.WaitForNextTickAsync())
        if (++ticks == 3)
            timer.Dispose();
    Console.WriteLine($"ticks: {ticks}");
}
```

A handler runs on the same thread as everything else, so it needs no locks, and two calls of one
timer's handlers never overlap. It can use `Console`, `Fiber.Sleep` and channels. `AutoReset = false`
makes a timer fire once: it is stopped before its handlers run, so a handler can start it again.
`Stop` ends the timer's fiber at once, even in the middle of a long interval.

Where it differs from .NET:

- Nothing fires outside `Fiber.Run`. A program that waits in `Console.ReadLine` while its timers
  fire, as .NET programs often do, has to call `Fiber.Run` instead. Reading input blocks every
  fiber, the timers' included. So that the .NET way doesn't go quietly wrong, `Main` returning
  while a timer is still running stops the program with a message saying to call `Fiber.Run`.
  `Environment.Exit` doesn't check.
- A handler that takes longer than the interval delays the next tick. The timer then fires once
  straight away and keeps its rhythm from there, rather than making up every tick it missed.
- `SignalTime` is a `CalendarTime` in UTC, not a local `DateTime`. Each timer reuses one
  `ElapsedEventArgs`, so a tick allocates nothing.
- `-=` removes the same delegate object that `+=` added: delegates here can't be combined, so the
  timer keeps a list.
- `StackSize` sets the size of the timer's fiber's stack (0, the default, is
  `Fiber.DefaultStackSize`). There's no `SynchronizingObject`, and `System.Threading.Timer` has no
  `TimeSpan` overloads, `Dispose(WaitHandle)` or `ActiveCount`.

## Use async and await

`lib/Tasks.cs` builds `Task`, `Task<T>` and `async`/`await` on fibers. As in .NET, an async method
starts on its caller's stack and runs until it awaits something that isn't done yet. Then its state
machine is copied into a small box on the heap, about a hundred bytes, and the call returns, so the
caller goes on:

<!-- sample: samples/Tasks.cs order -->
```csharp
static async Task<int> Twice(string name, int value, int delay)
{
    s_log += name + "1 ";
    await Task.Delay(delay);
    s_log += name + "2 ";
    return value * 2;
}
```

<!-- sample: samples/Tasks.cs await -->
```csharp
// Each call runs until its first await, then returns, and the
// caller goes on: a and b wait at the same time, on fibers.
Task<int> a = Twice("a", 1, 30), b = Twice("b", 2, 10);
s_log += "main ";
```

When the awaited task completes, the box goes on a queue that one fiber per thread, the dispatcher,
works through: it runs each method on to its next await or its end. `Task.WhenAll` and
`Task.WhenAny` wait for several tasks at once:

<!-- sample: samples/Tasks.cs when-all -->
```csharp
// Three delays of 50 ms side by side take about 50 ms, not 150.
int[] all = await Task.WhenAll(
    Twice("x", 1, 50), Twice("y", 2, 50), Twice("z", 3, 50));
```

`Main` can be `async`. Awaiting outside a fiber, as `Main` does, runs the fibers until the task is
done, so an async program needs no `Fiber.Run`. `samples/Tasks.cs` runs these examples and more.

- A method that finishes without waiting makes no box. Its `Task<T>` is the only allocation, and a
  `Task` that returns nothing is a shared, already completed one. A million calls waiting at once
  take about 250 MB, and no fiber each: `Task.Delay` has one timer fiber per thread for all of them.
- `Task.Run` runs its function on a new fiber, `Task.Yield` lets the other fibers run first, and
  `TaskCompletionSource` completes a task by hand.
- `Wait()` and `.Result` are safe here, unlike in .NET: in a fiber they park only that fiber, and
  outside one they run the fibers until the task is done. An async method may also block on a
  channel or call `Fiber.Sleep` after its first await. The dispatcher that runs into such a call
  keeps that method until it ends or awaits, and a new dispatcher (a new fiber) takes over the
  others.
- A method that blocks more than it awaits can have a fiber of its own instead, from its first wait
  to its end:

<!-- sample: samples/Tasks.cs own-fiber -->
```csharp
// A fiber of its own from the first await to the end, for a method
// that mostly blocks.
[AsyncMethodBuilder(typeof(OwnFiberMethodBuilder<>))]
static async Task<int> Sleeper(int times)
{
    await Task.Yield();
    for (int i = 0; i < times; i++)
        Fiber.Sleep(1);
    return times;
}
```

- `ValueTask` and `ValueTask<T>` work as in .NET, and so do `async ValueTask` methods: one that
  finishes without waiting allocates nothing at all. A timer's or a channel's `ValueTask` reuses
  its task, so, as in .NET, it can be awaited only once: awaiting it again after the timer or
  channel has reused it ends the program with a message. `AsTask()` or `Preserve()` gives
  something to keep. `IAsyncEnumerable<T>` works with `await foreach`, but there are no async
  iterators (`yield return` in an async method).
- `throw` ends the program, so a task is never faulted. There's no `ContinueWith` or
  `SynchronizationContext`. `ConfigureAwait(bool)` changes nothing, since there's no context to
  return to.
- Tasks are ordinary objects, and zero mode never frees them. An async method's box is made once
  and reused for every await in it, and timers and channels reuse their tasks. But `Task.Delay`
  and an async method that waits return a new `Task` each time, which stays allocated:

  | Each round of a loop | Bytes |
  |---|---|
  | `await Task.Yield()` | 0 |
  | `await timer.WaitForNextTickAsync(token)` | 0 |
  | `await channel.ReceiveAsync(token)`, `SendAsync`, `await foreach` | 0 |
  | `channel.Receive` and `Fiber.Sleep` in a fiber | 0 |
  | `await Task.Delay(1)` | 96 |
  | `await` an async method that waits | 120 |

  That's fine for a program that does its work and ends. A loop that never ends, on a
  microcontroller above all, should wait on a timer or a channel, or with the fibers' own calls,
  rather than `Task.Delay`. An arena doesn't help: after its first wait an async method runs on
  the dispatcher, under its allocator.
- If an arena makes a task, the arena must stay alive until whoever awaits the task has finished
  with it.

## Await a channel

A channel also has .NET's `ChannelReader`/`ChannelWriter` methods: `ReceiveAsync`, `SendAsync`,
`WaitToReceiveAsync` (with `TryReceive`) and `ReceiveAllAsync` for `await foreach`. An async method
waiting on a channel costs no fiber, and fibers and async methods can share a channel. When both
wait, the fibers are served first:

<!-- sample: samples/Tasks.cs channel-async -->
```csharp
// Sends 1..10 and closes; the reader's await foreach ends there.
static async Task Produce(Channel<int> channel)
{
    for (int i = 1; i <= 10; i++)
        await channel.SendAsync(i);
    channel.Close();
}

static async Task<int> Consume(Channel<int> channel)
{
    int sum = 0;
    await foreach (int value in channel.ReceiveAllAsync())
        sum += value;
    return sum;
}
```

`ReceiveAllAsync` and `WaitToReceiveAsync` stop when the channel is closed and empty. Awaiting
`ReceiveAsync` then ends the program, as .NET's `ReadAsync` throws. The channel reuses the tasks
behind these waits, so a loop over them allocates nothing, and a program that never calls them
doesn't link their code.

## Cancel

`CancellationTokenSource` and `CancellationToken` (`lib/Cancellation.cs`) work as in .NET: `Cancel`,
`CancelAfter`, `Register`, linked sources, and token overloads of `Task.Delay`, `Task.Run` and
`WaitAsync`. A canceled delay leaves the timer, so `Fiber.Run` doesn't wait for it.

The difference is what a canceled task does to its awaiter. .NET throws `OperationCanceledException`,
and zero mode has no exceptions, so a plain `await` of a canceled task ends the program, as an
unhandled exception would. So do `.Result`, `Wait()` and `ThrowIfCancellationRequested`. To carry on
instead, await with .NET 8's `SuppressThrowing` and look at `IsCanceled`:

<!-- sample: samples/Tasks.cs cancel -->
```csharp
// A delay of 10 s, canceled after 20 ms: SuppressThrowing makes the
// await return (a plain await would end the program).
using (var cts = new CancellationTokenSource(20))
{
    Task pause = Task.Delay(10_000, cts.Token);
    await pause.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    Console.WriteLine($"canceled: {pause.IsCanceled}");
}
```

Or check the token between steps:

<!-- sample: samples/Tasks.cs cancel-loop -->
```csharp
// Works until canceled, checking the token between steps; returns how
// many steps it did.
static async Task<int> Work(CancellationToken token)
{
    int steps = 0;
    while (!token.IsCancellationRequested)
    {
        await Task.Delay(5);
        steps++;
    }
    return steps;
}
```

Code ported from .NET that catches `OperationCanceledException` has to be changed to one of these.
`SuppressThrowing` works on a `Task`, not a `Task<T>` (there would be no result to return), as in
.NET: cast to `Task` first. Fibers' own waits take a token too: `Fiber.Sleep(ms, token)` and
`channel.Receive(out value, token)` return `false` if canceled.

## Use another clock

`TimeProvider` (`lib/TimeProvider.cs`) is .NET 8's way to give code its time from outside. Code that
waits with `Task.Delay(delay, timeProvider)`, `new CancellationTokenSource(delay, timeProvider)` or
`new PeriodicTimer(period, timeProvider)`, and measures with `GetTimestamp` and `GetElapsedTime`, runs on
the real clock when given `TimeProvider.System`. A test can give it a fake clock instead, whose time moves
only when the test says, so code that waits an hour is checked at once. `ManualTime` in
`samples/Timers.cs` is one, in some 80 lines, like .NET's `FakeTimeProvider`:

<!-- sample: samples/Timers.cs fake-time -->
```csharp
var time = new ManualTime();
Task delay = Task.Delay(TimeSpan.FromHours(1), time);
time.Advance(TimeSpan.FromMinutes(59));
bool early = delay.IsCompleted;     // false
time.Advance(TimeSpan.FromMinutes(1));
bool done = delay.IsCompleted;      // true
```

A provider of your own overrides `GetTimestamp`, `TimestampFrequency`, `GetUtcNow` and `CreateTimer`.
The waits above then run on the timers it creates. The system's timers are `System.Threading.Timer`s,
which fire inside `Fiber.Run`. Driving a fake clock from a test fires the timers at once, so the
tasks they complete are done when `Advance` returns. The async methods waiting on them carry on at the
next `Fiber.Run`, or the next `await` outside a fiber.

Three things differ from .NET. `GetUtcNow` returns a `CalendarTime`, since there is no `DateTimeOffset`.
There's no `LocalTimeZone`, because there are no time zones. And there's no
`Task.WaitAsync(timeout, timeProvider)`: a timed-out wait would fault its task, and tasks here can't
fault. Use `Task.WhenAny` with `Task.Delay(timeout, timeProvider)` instead.

## Use scratch memory and arenas

Each fiber has its own `Arena.Scratch` arena, made on its first `Scratch` call and destroyed when the
fiber ends. A fiber can wait inside `Scratch` without another fiber's scope freeing its memory:

<!-- sample: samples/Fibers.cs scratch -->
```csharp
// Each fiber has its own Arena.Scratch arena, so a fiber can wait
// inside Scratch: both fill a buffer, yield, and find their values
// still there after the other has opened and closed a scope.
var intact = new Log();
for (int id = 1; id <= 2; id++)
    Fiber.Spawn((intact, id),
        static s => Arena.Scratch(s, FillYieldCheck));
Fiber.Run();
```

<!-- sample: samples/Fibers.cs scratch-body -->
```csharp
static void FillYieldCheck(
    scoped ScratchScope scratch, (Log log, int id) s)
{
    Span<int> mine = scratch.AllocSpan<int>(256);
    mine.Fill(s.id);
    Fiber.Yield();
    Arena.Scratch(static other => other.AllocSpan<int>(256).Fill(-1));
    Fiber.Yield();
    bool ok = true;
    foreach (int value in mine)
        ok &= value == s.id;
    if (ok)
        s.log.Add(s.id);
}
```

A fiber can also wait inside a `Scratch` call in a buffer of its own, or inside `ScratchOnStack`, whose
memory is on the fiber's stack (see [Scratch memory](../concepts/memory.html)): that memory stands in for
the fiber's scratch arena until the call returns, so a fiber needs no scratch arena from malloc at all.

The `ObjectAllocator` setting is per fiber too, and starts with calloc. An `AllocateObjectsHere` scope
opened in one fiber doesn't send another fiber's objects to its arena.

## Choose a stack size

A fiber's stack is `Fiber.DefaultStackSize` unless `Spawn` is told otherwise
(`stackSize: 16 * 1024`), and never less than `Fiber.MinStackSize`. Both depend on the machine:

| Machine | `DefaultStackSize` | `MinStackSize` | Reserved below each stack |
|---|---|---|---|
| Linux, macOS, Windows, UEFI | 256 KB | 16 KB | a page, 4 KB or more |
| DOS | 32 KB | 4 KB | 2 KB |
| Pico 2, Uno R4 | 4 KB | 1 KB | 1 KB |
| Commodore 64 | 2 KB | 512 bytes | 256 bytes |

On Linux, macOS and Windows the page below the stack is a guard page, so a fiber that overflows its
stack stops the program with a segmentation fault instead of overwriting memory.

UEFI, DOS, microcontrollers and the C64 can't protect a page, so there the memory below the stack is
only reserved. Each time a fiber switches back, `Run` also checks a
pattern at the bottom of its stack, and stops the program with `Fiber 1 overflowed its stack` if the
pattern has changed. That catches the overflow, but only at the next switch. A fiber that goes past
the reserved memory before switching may already have overwritten other memory, so give fibers there
room to spare. The Pico 2's processor (a Cortex-M33) does better: it has a register for the
lowest address the stack may reach, the switch sets it for each fiber, and a fiber that goes past it
stops the program with a fault at once.

## Fibers on a microcontroller

The Pico 2 has 520 KB of RAM and the Uno R4 32 KB, so count what fibers cost. Each takes its stack,
the memory reserved below it, and about a hundred bytes for its record and state, all freed when it
ends. Six fibers at the default size come to about 30 KB, more than an Uno R4 has to spare. Give
them small stacks there (`stackSize: 1024`), or run fewer at once.

The bottom quarter of a microcontroller fiber's stack holds its shadow stack: there, C# code keeps
the local variables that refer to objects in a stack of their own, and each fiber needs one. (On DOS
and the C64 the shadow stack also starts at the bottom of the fiber's stack, without a fixed share.)
An interrupt that arrives while a fiber runs uses the fiber's stack for its handler, so leave room
for that too.

## Fibers on a Commodore 64

A program and its heap share 50 KB, and fibers' code takes several of them: `SmallFibers` leaves
about 7 KB of heap, room for seven fibers on the smallest stacks (900 bytes each, with what's
reserved below the stack and the fiber's record). Spawn them with `stackSize: Fiber.MinStackSize`,
and make the objects they're given before spawning: an object made between two spawns stays between
the memory the fibers free, and can split it into pieces too small for the next ones.

The 6502 keeps return addresses and saved registers on its own stack, which is always the same 256
bytes of memory. So a switch copies the part of it in use onto the fiber's stack, and the next
fiber's part back: the deeper the calls when a fiber waits, the slower the switch, and the more of
its stack the copy takes.

## What to watch out for

- A fiber that never yields keeps the others waiting. Long loops should call `Fiber.Yield`.
- Fibers spawned on a system thread run on that thread, inside its `Fiber.Run`. To use several
  processors, give each `Parallel.For` part its own fibers and its own `Run`.
- Reading a file or standard input blocks the whole thread, every fiber on it included. That holds
  for a fiber that feeds a `Pipe` from a file too: only `Pipe`'s own `Read` and `Write`, and sockets
  (see [Use the network](use-the-network.html)), let the other fibers run while one waits.
- A `throw` ends the program, as it does everywhere in zero mode, unless a
  [`Catch.Run`](recover-from-errors.html) on the same fiber is waiting for it.
