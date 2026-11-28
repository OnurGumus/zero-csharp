---
title: Read and write files
index: 18
---

# Read and write files

`lib/File.cs` reads and writes files on every target: a whole file in one call, or an open file read,
written and moved through piece by piece. Text is UTF-8 on disk and UTF-16 in a string, as with `Console`
and `In`. `samples/Files.cs` runs every example below.

## A whole file at once

`WriteAllText` replaces a file, `AppendAllText` adds to its end, and `TryReadAllText` reads it back
into a string in an arena:

<!-- sample: samples/Files.cs whole -->
```csharp
File.WriteAllText(Path, "naïve café\n");
File.AppendAllText(Path, "snow ☃ and a clef 𝄞\n");
if (File.TryReadAllText(arena, Path, out string text))
{
    foreach (ReadOnlySpan<char> line in text.Split('\n'))
        if (line.Length > 0)
            Console.WriteLine(line);
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs whole -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
File.WriteAllText(Name, "naïve café\n") |> ignore
File.AppendAllText(Name, "snow ☃ and a clef 𝄞\n") |> ignore
match File.TryReadAllText(arena, Name) with
| true, text ->
    for line in text.Split('\n') do
        if line.Length > 0 then
            Console.WriteLine(line)
| false, _ -> printfn "couldn't read %s" Name
```

The string lives in the arena you pass, like any string from `Str.Format`, so it goes away when the
arena is reset. `TryReadAllBytes` does the same for bytes; it is `unsafe`, as `Arena.AllocSpan` is,
because the span it gives you points into the arena. `WriteAllBytes`, `Exists` and `Delete` complete
the set. `Exists` is true for a file, readable or not, and false for a directory. (On DOS, UEFI and
microcontrollers it says whether the file can be opened for reading.)

Bytes that aren't UTF-8 read as U+FFFD, the replacement character, as they do with `In`.

## An open file

`File.Create`, `File.OpenRead` and `File.OpenAppend` return a `File`, a handle like `Arena`: one
pointer, and copies share the file. `Read` fills a span you provide, so a large file can be read in
pieces without allocating:

<!-- sample: samples/Files.cs open -->
```csharp
File file = File.Create(Path);
if (!file.IsOpen)
    return;
file.Write("0123456789");
file.Write("abcdef"u8);
Console.WriteLine($"Wrote {file.Length} bytes");

file.Seek(4);
file.Write("--");
unsafe { file.Close(); } // SAFETY: file isn't used after this.

File input = File.OpenRead(Path);
Span<byte> chunk = stackalloc byte[6];
int count;
while ((count = input.Read(chunk)) > 0)
    Console.WriteLine($"{count} bytes at {input.Position - count}");
unsafe { input.Close(); } // SAFETY: input isn't used after this.
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs open -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// A file is a handle to close: [<Safety>] says why nothing uses it after.
[<Safety "file isn't used after Close">]
let writeDigits () =
    let file = File.Create(Name)
    if file.IsOpen then
        file.Write("0123456789") |> ignore
        file.Write(ReadOnlySpan<byte>("abcdef"B)) |> ignore
        printfn "Wrote %d bytes" file.Length
        file.Seek(4L) |> ignore
        file.Write("--") |> ignore
        file.Close()

[<Safety "input isn't used after Close">]
let readInChunks () =
    let input = File.OpenRead(Name)
    let chunk: byte[] = Array.zeroCreate 6
    let mutable count = input.Read(Span<byte>(chunk))
    while count > 0 do
        printfn "%d bytes at %d" count (input.Position - int64 count)
        count <- input.Read(Span<byte>(chunk))
    input.Close()

writeDigits ()
readInChunks ()
```

That prints:

```text
Wrote 16 bytes
6 bytes at 0
6 bytes at 6
4 bytes at 12
```

`Seek` moves to a byte position from the start, `Position` says where the next read or write
happens, and `Length` is the file's size. `Write` takes bytes, or text, which it encodes as UTF-8.

`Close` is `unsafe` for the same reason `Arena.Reset` is: every copy of the handle still points at the
closed file. The `SAFETY` comment says why it can't be used afterwards; in F#, the `[<Safety "...">]`
attribute on the function does, or the build warns (ZERO007). Close each file you open: it writes out
what is buffered, and a program can only have so many open. F# writes `"abcdef"B` for C#'s
`"abcdef"u8`, and passes `&error` where C# writes `out error`.

To make sense of a binary file's bytes, `BinaryPrimitives` reads numbers out of them in either byte
order: `BinaryPrimitives.ReadUInt32LittleEndian(header[4..])` (see
[Limits](../concepts/limits.html#binary-formats-hex-and-base64)).

## Without blocking

A read from a slow disk or a network drive blocks its thread, and with it every fiber on the thread.
The async methods don't. They are `File.ReadAllBytesAsync`, `ReadAllTextAsync`, `WriteAllBytesAsync`,
`WriteAllTextAsync` and `AppendAllTextAsync`, and `FileStream`'s `ReadAsync`, `WriteAsync`,
`FlushAsync` and `DisposeAsync`. The reads and writes run on an I/O thread, as in .NET on Linux,
and the fiber or async method that asked lets the others run until it's done:

<!-- sample: samples/Tasks.cs files -->
```csharp
// The write and the read run on another thread; this async
// method waits, and the others run meanwhile.
await File.WriteAllBytesAsync(name, data);
byte[] back = await File.ReadAllBytesAsync(name);
```

A file can't be waited on the way a socket can, because the system calls a file always ready, then
blocks in the read. Hence the thread. Each thread that uses the async calls gets one I/O thread, started
by its first call. The I/O thread runs the jobs in order and, when they're done, wakes `Fiber.Run` through
a pipe that it polls along with the sockets. Handing a job over and back costs some microseconds, more
than reading a few cached KB. So the async calls pay off for big reads and writes, slow disks and
network drives, and the blocking calls stay faster for small files on a local disk. A `FileStream`'s
buffer still gathers small reads and writes, and those need no thread. Where there are no threads
(DOS, UEFI, microcontrollers), the calls block, as `Read` and `Write` do, and their tasks are done
when they return.

Some things differ from .NET:

- **One at a time.** A `FileStream` runs one operation at a time. Another call, or `Dispose`, while
  one runs ends the program.
- **Tokens.** A token is checked when an operation starts. A read already running can't be stopped.
- **Failures.** A file that can't be opened ends the program, as .NET's exception would. Open with
  `FileStream.TryOpen`, or check `File.Exists`, where that can happen.

## When something goes wrong

Nothing in `File` ends the program. A file that can't be opened gives a handle whose `IsOpen` is false,
and every method on it does nothing: `Read` returns 0, `Position` and `Length` return -1. The
whole-file functions return false.

To find out why, use the forms that return a `FileError`: `None`, `NotFound`, `AccessDenied`,
`AlreadyExists`, `NotEmpty`, `NotADirectory`, `IsADirectory`, `InvalidPath`, `NoSpace` or `Other`:

<!-- sample: samples/Directories.cs errors -->
```csharp
// OpenRead, Create and OpenAppend can say why they failed.
File file = File.OpenRead("dirs.tmp/none", out error);
// file.IsOpen is false, and error is FileError.NotFound
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs errors -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// OpenRead, Create and OpenAppend can say why they failed.
let mutable error = FileError.None
let missing = File.OpenRead("dirs.tmp/none", &error)
// missing.IsOpen is false, and error is FileError.NotFound
```

`OpenRead`, `Create` and `OpenAppend` take an `out FileError`. `OpenRead` also refuses a directory,
which the C library would otherwise open on Linux and macOS. `CreateNew` creates a file only if
nothing has the name yet, so no other program can create it in between. `File.Remove` is `Delete` with
a reason.

## Copy, move and look at a file

`File.Copy` copies a file's bytes, and `File.Move` moves or renames it. Neither replaces a file that's
already there unless you pass `overwrite: true`:

<!-- sample: samples/Directories.cs copy-move -->
```csharp
// Copy and Move refuse to replace a file unless told to overwrite.
error = File.Copy("dirs.tmp/notes.txt", "dirs.tmp/copy.txt");
FileError again = File.Copy("dirs.tmp/notes.txt", "dirs.tmp/copy.txt");
// again is FileError.AlreadyExists
File.Move("dirs.tmp/copy.txt", "dirs.tmp/logs/moved.txt");
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs copy-move -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Copy and Move refuse to replace a file unless told to overwrite.
let copied = File.Copy("dirs.tmp/notes.txt", "dirs.tmp/copy.txt")
let again = File.Copy("dirs.tmp/notes.txt", "dirs.tmp/copy.txt")
// again is FileError.AlreadyExists
File.Move("dirs.tmp/copy.txt", "dirs.tmp/logs/moved.txt") |> ignore
```

`Move` renames when it can. Between file systems, where a rename can't go, it copies the file and then
deletes the original. `File.GetInfo` returns a file's length, whether it's a directory, and when it was
last written:

<!-- sample: samples/Directories.cs info -->
```csharp
// GetInfo: a file's length, whether it's a directory, and when it was
// last written (milliseconds since 1970, as Clock.UnixMilliseconds).
File.GetInfo("dirs.tmp/notes.txt", out FileInfo info);
long age = Clock.UnixMilliseconds - info.LastWriteUnixMilliseconds;
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs info -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// GetInfo: a file's length, whether it's a directory, and when it was last
// written (milliseconds since 1970, as Clock.UnixMilliseconds).
let mutable info = FileInfo()
File.GetInfo("dirs.tmp/notes.txt", &info) |> ignore
let age = Clock.UnixMilliseconds - info.LastWriteUnixMilliseconds
```

The error codes, `Copy`, `Move` and `GetInfo` work on Linux, macOS, Windows and WebAssembly, as
directories do (see [Work with paths and directories](work-with-paths-and-directories.html)).

## On each target

`File` calls the C library's `fopen`, `fread`, `fwrite`, `fseeko`, `ftello`, `fclose` and `remove`.
They are the system's own on Linux, macOS, Windows and WebAssembly, and `dos/runtime.c` and
`efi/machine.c` supply them on DOS and UEFI. What differs:

- **WebAssembly** programs see only the directories the runtime grants. `./run.sh --wasm` runs
  wasmtime with `--dir=.`, the current directory.
- **DOS** names are 8.3: `files.tmp`, not `settings.json`. Files stop at 2 GB.
- **UEFI** paths start at the root of the volume the program was loaded from; `/` and `\` both work.
- **Windows** paths go to the system as UTF-16, converted from UTF-8 by `win/runtime.c`, so any name
  works, as on Linux.

On DOS and UEFI there is no buffering underneath: each `Read` or `Write` is one call to DOS or the
firmware, so read and write in large pieces.

See also: the [`File` reference](../reference/file.html), and [Read input](read-input.html) for
standard input.
