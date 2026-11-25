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

The string lives in the arena you pass, like any string from `Str.Format`, so it goes away when the
arena is reset. `TryReadAllBytes` does the same for bytes; it is `unsafe`, as `Arena.AllocSpan` is,
because the span it gives you points into the arena. `WriteAllBytes`, `Exists` and `Delete` complete
the set.

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
closed file. The `SAFETY` comment says why it can't be used afterwards. Close each file you open: it
writes out what is buffered, and a program can only have so many open.

To make sense of a binary file's bytes, `BinaryPrimitives` reads numbers out of them in either byte
order: `BinaryPrimitives.ReadUInt32LittleEndian(header[4..])` (see
[Limits](../concepts/limits.html#binary-formats-hex-and-base64)).

## When something goes wrong

Nothing in `File` ends the program. A file that can't be opened gives a handle whose `IsOpen` is false,
and every method on it does nothing: `Read` returns 0, `Position` and `Length` return -1. The
whole-file functions return false. There is no reason given, as there would be in a .NET exception:
check `IsOpen`, or the `bool`, where it matters.

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
