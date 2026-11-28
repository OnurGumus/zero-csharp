---
title: Use streams
index: 29
---

# Use streams

`lib/Streams.cs` has .NET's `System.IO.Stream`, and the two streams most programs need: `MemoryStream`
and `FileStream`. Code written against them compiles here with `using System.IO;`. A stream's memory
is bounded, and it comes from wherever objects come from, which can be an arena. `samples/Streams.cs`
runs every example below.

## Any stream

`Stream` is an abstract class, as in .NET, so a function that takes one works with all of them:

<!-- sample: samples/Streams.cs any-stream -->
```csharp
// Code that takes a Stream works with any of them.
static long CountBytes(Stream stream)
{
    Span<byte> chunk = stackalloc byte[64];
    long total = 0;
    int n;
    while ((n = stream.Read(chunk)) > 0)
        total += n;
    return total;
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Streams.fs any-stream -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Code that takes a Stream works with any of them.
let chunk: byte[] = Array.zeroCreate 64

let countBytes (stream: Stream) =
    let mutable total = 0L
    let mutable n = stream.Read(Span<byte>(chunk))
    while n > 0 do
        total <- total + int64 n
        n <- stream.Read(Span<byte>(chunk))
    total
```

`Read` fills a span and returns how many bytes came, or 0 at the end. `ReadExactly` and `ReadAtLeast`
keep reading until they have enough. `Write`, `ReadByte`, `WriteByte`, `Seek`, `Position`, `Length`,
`CopyTo` and `Flush` work as in .NET. `Stream.Null` reads nothing and throws writes away. To write a
stream of your own, override the `byte[]` `Read` and `Write` and the rest of the abstract members. The
span forms go through those unless you override them too.

## In memory

A `MemoryStream` grows as it's written:

<!-- sample: samples/Streams.cs memory -->
```csharp
// A MemoryStream grows as it's written; Position moves through it.
var memory = new MemoryStream();
memory.Write("hello, "u8);
memory.Write("world"u8);
memory.Position = 7;
Span<byte> word = stackalloc byte[5];
memory.ReadExactly(word); // "world"
```

<div class="fs-alt"></div>

<!-- sample: samples/Streams.fs memory -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// A MemoryStream grows as it's written; Position moves through it.
let memory = new MemoryStream()
memory.Write(ReadOnlySpan<byte>("hello, "B))
memory.Write(ReadOnlySpan<byte>("world"B))
memory.Position <- 7L
let word: byte[] = Array.zeroCreate 5
memory.ReadExactly(Span<byte>(word)) // "world"
```

`ToArray` copies the bytes out. `AsSpan`, which .NET doesn't have, looks at them without copying.
`SetLength`, `Capacity`, `WriteTo` and `GetBuffer` are there too. Made over a `byte[]` you give it, a
`MemoryStream` never grows. Writing past the end of that array ends the program, as .NET's
`NotSupportedException` would, so memory can't run away:

<!-- sample: samples/Streams.cs fixed -->
```csharp
// Over a byte[] you give it, a MemoryStream never grows: memory is
// bounded, and writing past the end ends the program.
byte[] room = new byte[16];
var bounded = new MemoryStream(room);
bounded.Write("sixteen bytes!!!"u8); // exactly full
```

<div class="fs-alt"></div>

<!-- sample: samples/Streams.fs fixed -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Over a byte[] you give it, a MemoryStream never grows: memory is bounded,
// and writing past the end ends the program.
let room: byte[] = Array.zeroCreate 16
let bounded = new MemoryStream(room)
bounded.Write(ReadOnlySpan<byte>("sixteen bytes!!!"B)) // exactly full
```

## Files

A `FileStream` opens a file with a `FileMode` and a `FileAccess`, as in .NET, and can read, write and
seek in the same file:

<!-- sample: samples/Streams.cs file-stream -->
```csharp
// A FileStream writes through its buffer, and seeks, reads and
// writes in the same file.
using (var file = new FileStream(Name, FileMode.Create))
{
    file.Write("0123456789"u8);
    file.Position = 4;
    file.Write("--"u8);
    file.Seek(0, SeekOrigin.Begin);
    Span<byte> start = stackalloc byte[6];
    file.ReadExactly(start); // "0123--"
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Streams.fs file-stream -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// A FileStream writes through its buffer, and seeks, reads and writes in the
// same file.
let start: byte[] = Array.zeroCreate 6
do
    use file = new FileStream(Name, FileMode.Create)
    file.Write(ReadOnlySpan<byte>("0123456789"B))
    file.Position <- 4L
    file.Write(ReadOnlySpan<byte>("--"B))
    file.Seek(0L, SeekOrigin.Begin) |> ignore
    file.ReadExactly(Span<byte>(start)) // "0123--"
```

Reads and writes go through a buffer of 4096 bytes, or whatever size you pass as `bufferSize` (0 for
none). A read or write bigger than the buffer skips it and goes straight to the file. The buffer is the
stream's own byte array; the C library's own buffering is turned off. Dispose the stream, with `using`
as here: that writes out what's still in the buffer and closes the file. A stream that's never
disposed loses whatever is still in its buffer.

Every `FileMode` works: `Open`, `Create`, `CreateNew`, `OpenOrCreate`, `Truncate` and `Append`.
Opening a file that isn't there, or a `CreateNew` file that is, throws an `IOException`, as in .NET.
A throw ends the program unless a [`Catch.Run`](recover-from-errors.html) is waiting for it. Where
failing is expected, `TryOpen` returns the reason as a `FileError` instead:

<!-- sample: samples/Streams.cs modes -->
```csharp
// Append writes at the end; CreateNew won't replace a file. TryOpen
// says why a file wouldn't open, rather than ending the program.
using (var log = new FileStream(Log, FileMode.Append))
    log.Write("one\n"u8);
using (var log = new FileStream(Log, FileMode.Append))
    log.Write("two\n"u8);
bool opened = FileStream.TryOpen(Log, FileMode.CreateNew,
    FileAccess.Write, out FileStream stream, out FileError error);
// opened is false, and error is FileError.AlreadyExists
```

<div class="fs-alt"></div>

<!-- sample: samples/Streams.fs modes -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Append writes at the end; CreateNew won't replace a file. TryOpen says why
// a file wouldn't open, rather than ending the program.
do
    use log = new FileStream(Log, FileMode.Append)
    log.Write(ReadOnlySpan<byte>("one\n"B))
do
    use log = new FileStream(Log, FileMode.Append)
    log.Write(ReadOnlySpan<byte>("two\n"B))
let opened, _, error =
    FileStream.TryOpen(Log, FileMode.CreateNew, FileAccess.Write)
// opened is false, and error is FileError.AlreadyExists
```

On DOS, UEFI and microcontrollers the reason is always `Other`. `FileShare` is accepted, but no system
here enforces it. `SetLength` isn't there yet.

## Memory from an arena

Opened inside an arena's `AllocateObjectsHere` scope, a stream and its buffer come from the arena (in
F#, `use _ = arena.AllocateObjectsHere()` in a function marked `[<Safety "...">]`):

<!-- sample: samples/Streams.cs arena -->
```csharp
// Inside an arena's AllocateObjectsHere scope a stream, and its
// buffer, come from the arena. Dispose the stream before the arena
// goes: what's still buffered is written then.
Arena arena = Arena.Create(16 * 1024);
nuint before = arena.Used;
unsafe
{
    // SAFETY: the stream is disposed before the arena is destroyed.
    using (arena.AllocateObjectsHere())
    using (var file = new FileStream(Name, FileMode.Create,
        FileAccess.Write, FileShare.None, bufferSize: 8192))
    {
        file.Write("from the arena"u8);
    }
}
nuint used = arena.Used - before; // over 8192: the buffer
unsafe { arena.Destroy(); } // SAFETY: nothing from it is used after this
```

<div class="fs-alt"></div>

<!-- sample: samples/Streams.fs arena -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Inside an arena's object scope a stream, and its buffer, come from the
// arena. Dispose the stream before the arena goes: what's still buffered is
// written then.
let streams = Arena.Create(16un * 1024un)

[<Safety "the stream is disposed before the scope closes">]
let writeFromArena () =
    use _ = streams.AllocateObjectsHere()
    use file =
        new FileStream(
            Name, FileMode.Create, FileAccess.Write, FileShare.None, 8192)
    file.Write(ReadOnlySpan<byte>("from the arena"B))

let before = streams.Used
writeFromArena ()
let used = streams.Used - before // over 8192: the buffer
```

The memory a stream holds is its buffer and nothing else, so you choose how much. Dispose the stream
before the arena is reset or destroyed. After that the stream's memory is gone, as any object's in
the arena would be. Because the buffer is the stream's own and no C code holds on to it, an undisposed
stream only loses its unwritten bytes; it can't corrupt anything.

## Between fibers

`Pipe.AsStream` (see [Run green threads](use-fibers.html)) turns a pipe between fibers into a `Stream`.
There, `Read` waits for bytes and lets the other fibers run in the meantime.

## Async methods

`Stream` has .NET's `ReadAsync`, `WriteAsync`, `FlushAsync`, `CopyToAsync`, `ReadExactlyAsync`,
`ReadAtLeastAsync` and `DisposeAsync`, so `await using` and code written for .NET work. How they wait
depends on the stream:

- **`FileStream`:** the read or write runs on another thread, and the other fibers and tasks run
  meanwhile (see [Read and write files](read-and-write-files.html#without-blocking)).
- **`MemoryStream`:** there's nothing to wait for, so they finish at once.
- **A pipe's stream:** in a fiber, the wait lets the other fibers run.
- **`NetworkStream`:** the wait blocks nothing at all (see
  [Use the network](use-the-network.html#async-methods)).
 A subclass can override `ReadAsync(Memory<byte>,
CancellationToken)` and `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)` with ones that truly wait.

## What isn't here

- **`catch` blocks:** a throw ends the program, or comes back to a
  [`Catch.Run`](recover-from-errors.html), which closes the files opened inside it without writing
  out what their streams still held. Check `CanRead`, `CanWrite` and `CanSeek`, and open files with
  `TryOpen`, where failing is expected.
- **`BufferedStream`, `StreamReader` and `StreamWriter`:** for text, `File.TryReadAllText`,
  `File.Write(text)` and `Pipe.Write(text)` handle UTF-8.

See also: [Read and write files](read-and-write-files.html), and the
[`Stream`](../reference/stream.html) reference.
