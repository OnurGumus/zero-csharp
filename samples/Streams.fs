// Test: System.IO's streams from F#, the parts docs/how-to/use-streams.md
// shows next to Streams.cs's. Exit code = number of failed checks. The SDK's
// tests (fsharp_doc_samples) build it as an F# zero project's Program.fs and
// compare what it prints with tests/expected/Streams.fs.txt.

open System
open System.IO

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

[<Literal>]
let Name = "streams.tmp" // 8.3, for DOS

[<Literal>]
let Log = "append.tmp"

let text (bytes: byte[]) = String(Array.map char bytes)

// docs:any-stream
// Code that takes a Stream works with any of them.
let chunk: byte[] = Array.zeroCreate 64

let countBytes (stream: Stream) =
    let mutable total = 0L
    let mutable n = stream.Read(Span<byte>(chunk))
    while n > 0 do
        total <- total + int64 n
        n <- stream.Read(Span<byte>(chunk))
    total
// docs:end

// docs:memory
// A MemoryStream grows as it's written; Position moves through it.
let memory = new MemoryStream()
memory.Write(ReadOnlySpan<byte>("hello, "B))
memory.Write(ReadOnlySpan<byte>("world"B))
memory.Position <- 7L
let word: byte[] = Array.zeroCreate 5
memory.ReadExactly(Span<byte>(word)) // "world"
// docs:end
check "memory stream" (text word = "world" && memory.Length = 12L)
memory.Position <- 0L
check "any stream" (countBytes memory = 12L)

// docs:fixed
// Over a byte[] you give it, a MemoryStream never grows: memory is bounded,
// and writing past the end ends the program.
let room: byte[] = Array.zeroCreate 16
let bounded = new MemoryStream(room)
bounded.Write(ReadOnlySpan<byte>("sixteen bytes!!!"B)) // exactly full
// docs:end
check "bounded" (text room = "sixteen bytes!!!")

// docs:file-stream
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
// docs:end
check "file stream" (text start = "0123--")

// docs:modes
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
// docs:end
check "modes" (not opened && error = FileError.AlreadyExists)

// docs:arena
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
// docs:end
check "in the arena" (used > 8192un)
check "written" (File.TryReadAllText(streams, Name) = (true, "from the arena"))

File.Delete(Name) |> ignore
File.Delete(Log) |> ignore
printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
