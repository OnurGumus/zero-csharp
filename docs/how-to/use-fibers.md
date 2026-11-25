---
title: Run green threads
index: 26
---

# Run green threads

`Fiber` from `lib/Fibers.cs` runs many functions on one system thread, each on a stack of its own. They
take turns: a fiber runs until it yields, sleeps, waits on a channel or ends, and then the next one in
line runs. `Channel<T>` passes values between them. Switching is cooperative, so fibers need no locks
among themselves. `samples/Fibers.cs` runs every example below.

Fibers work on Linux (x64 and arm64), macOS, Windows and UEFI. They need a way to switch stacks, so
there are none on WebAssembly, DOS, the C64 or microcontrollers yet.

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
var pipe = new Pipe();
Fiber.Spawn(pipe, static p =>
{
    for (int i = 1; i <= 100; i++)
        p.Numbers.Send(i);
    p.Numbers.Close();
});
Fiber.Spawn(pipe, static p =>
{
    while (p.Numbers.Receive(out int n))
        p.Squares.Send(n * n);
    p.Squares.Close();
});
Fiber.Spawn(pipe, static p =>
{
    while (p.Squares.Receive(out int square))
        p.Total += square;
});
Fiber.Run();
```

If every fiber left is waiting on a channel, none of them can run again, and `Run` stops the program
with a message saying so. `Send` and `Receive` wait only inside a fiber. Outside one, a call that would
wait stops the program.

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

The `ObjectAllocator` setting is per fiber too, and starts with calloc. An `AllocateObjectsHere` scope
opened in one fiber doesn't send another fiber's objects to its arena.

## Choose a stack size

A fiber's stack is 256 KB unless `Spawn` is told otherwise (`stackSize: 16 * 1024`). Below the stack
is a guard page, so a fiber that overflows its stack stops the program with a segmentation fault
instead of overwriting memory. UEFI can't protect a page, so there an overflow does overwrite the
memory below.

## What to watch out for

- A fiber that never yields keeps the others waiting. Long loops should call `Fiber.Yield`.
- Fibers spawned on a system thread run on that thread, inside its `Fiber.Run`. To use several
  processors, give each `Parallel.For` part its own fibers and its own `Run`.
- Reading a file or standard input blocks the whole thread, every fiber on it included.
- A `throw` ends the program, as it does everywhere in zero mode.
