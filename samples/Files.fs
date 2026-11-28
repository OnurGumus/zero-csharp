// Test: files, directories and paths from F#, the parts
// docs/how-to/read-and-write-files.md and work-with-paths-and-directories.md
// show next to Files.cs's, Directories.cs's and Paths.cs's. Exit code =
// number of failed checks. The SDK's tests (fsharp_doc_samples) build it as an
// F# zero project's Program.fs and compare what it prints with
// tests/expected/Files.fs.txt.

open System

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

[<Literal>]
let Name = "files.tmp" // 8.3, for DOS

let arena = Arena.Create(4096un)

// docs:whole
File.WriteAllText(Name, "naïve café\n") |> ignore
File.AppendAllText(Name, "snow ☃ and a clef 𝄞\n") |> ignore
match File.TryReadAllText(arena, Name) with
| true, text ->
    for line in text.Split('\n') do
        if line.Length > 0 then
            Console.WriteLine(line)
| false, _ -> printfn "couldn't read %s" Name
// docs:end

// docs:open
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
// docs:end

// docs:create
// Create makes the directories on the way too; one that's already there is
// fine. Failures come back as a FileError.
let created = Directory.Create("dirs.tmp/logs/2026")
if created <> FileError.None then
    printfn "couldn't create it: %d" (int created)
// docs:end

File.WriteAllText("dirs.tmp/notes.txt", "notes\n") |> ignore
File.WriteAllText("dirs.tmp/snö☃.txt", "snow\n") |> ignore

// docs:errors
// OpenRead, Create and OpenAppend can say why they failed.
let mutable error = FileError.None
let missing = File.OpenRead("dirs.tmp/none", &error)
// missing.IsOpen is false, and error is FileError.NotFound
// docs:end
check "not found" (not missing.IsOpen && error = FileError.NotFound)

// docs:copy-move
// Copy and Move refuse to replace a file unless told to overwrite.
let copied = File.Copy("dirs.tmp/notes.txt", "dirs.tmp/copy.txt")
let again = File.Copy("dirs.tmp/notes.txt", "dirs.tmp/copy.txt")
// again is FileError.AlreadyExists
File.Move("dirs.tmp/copy.txt", "dirs.tmp/logs/moved.txt") |> ignore
// docs:end
check "copy" (copied = FileError.None && again = FileError.AlreadyExists)

// docs:info
// GetInfo: a file's length, whether it's a directory, and when it was last
// written (milliseconds since 1970, as Clock.UnixMilliseconds).
let mutable info = FileInfo()
File.GetInfo("dirs.tmp/notes.txt", &info) |> ignore
let age = Clock.UnixMilliseconds - info.LastWriteUnixMilliseconds
// docs:end
check "info" (info.Length = 6L && not info.IsDirectory && age >= 0L && age < 600_000L)

// docs:enumerate
// Enumerate lists a directory for a for loop: each entry's name (a span,
// good until the next entry) and kind, without "." and "..".
let mutable files = 0
let mutable folders = 0
let mutable sawSnow = false
for entry in Directory.Enumerate("dirs.tmp") do
    if entry.IsDirectory then folders <- folders + 1
    elif entry.IsFile then files <- files + 1
    sawSnow <- sawSnow || entry.Name.SequenceEqual("snö☃.txt".AsSpan())
// docs:end
check "enumerate" (files = 2 && folders = 1 && sawSnow)

// docs:delete
// Delete with recursive = true deletes everything inside first.
let deleted = Directory.Delete("dirs.tmp", recursive = true)
// docs:end
check "deleted" (deleted = FileError.None && not (Directory.Exists "dirs.tmp"))

// docs:parts
// The parts of a path are slices of it: nothing is allocated.
let parts (path: string) =
    let name = Path.GetFileName(path)                    // today.txt
    let stem = Path.GetFileNameWithoutExtension(path)    // today
    let extension = Path.GetExtension(path)              // .txt
    let folder = Path.GetDirectoryName(path)             // logs/2026
    name.SequenceEqual("today.txt") && stem.SequenceEqual("today")
    && extension.SequenceEqual(".txt") && folder.SequenceEqual("logs/2026")
// docs:end
check "parts" (parts "logs/2026/today.txt")

// docs:combine
// Combine puts the system's separator between two paths, unless the first
// ends with one; a rooted second path replaces the first.
let log = Path.Combine("logs", "today.txt") // logs/today.txt
let other = Path.Combine("logs", "/tmp/x")   // /tmp/x
// docs:end
check "combine" (log = "logs" + string Path.DirectorySeparatorChar + "today.txt" && other = "/tmp/x")

// docs:normalize
// Normalize tidies a path from its text: "." goes, ".." takes the name
// before it away, and separators become the system's.
let tidy = Path.Normalize("logs/./old/../today.txt") // logs/today.txt
// docs:end
check "normalize" (tidy = Path.Combine("logs", "today.txt"))

File.Delete(Name) |> ignore
printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
