---
title: Work with paths and directories
index: 28
---

# Work with paths and directories

`lib/Path.cs` takes paths apart, joins them and tidies them up. It only looks at the text, so it works
on every target. `lib/Directory.cs` creates, lists, moves and deletes directories, and gets or sets the
current directory, on Linux, macOS, Windows and WebAssembly. `samples/Paths.cs` and
`samples/Directories.cs` run every example below.

## The parts of a path

`GetFileName`, `GetFileNameWithoutExtension`, `GetExtension` and `GetDirectoryName` return slices of
the path you pass, so they allocate nothing:

<!-- sample: samples/Paths.cs parts -->
```csharp
// The parts of a path are slices of it: nothing is allocated.
ReadOnlySpan<char> path = "logs/2026/today.txt";
ReadOnlySpan<char> name = Path.GetFileName(path);      // today.txt
ReadOnlySpan<char> stem =
    Path.GetFileNameWithoutExtension(path);             // today
ReadOnlySpan<char> extension = Path.GetExtension(path); // .txt
ReadOnlySpan<char> folder = Path.GetDirectoryName(path); // logs/2026
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs parts -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// The parts of a path are slices of it: nothing is allocated.
let parts (path: string) =
    let name = Path.GetFileName(path)                    // today.txt
    let stem = Path.GetFileNameWithoutExtension(path)    // today
    let extension = Path.GetExtension(path)              // .txt
    let folder = Path.GetDirectoryName(path)             // logs/2026
    name.SequenceEqual("today.txt") && stem.SequenceEqual("today")
    && extension.SequenceEqual(".txt") && folder.SequenceEqual("logs/2026")
```

They follow .NET's `Path`: `GetExtension("archive.tar.gz")` is `.gz`, a name that starts with a dot
(`.profile`) is all extension, and `GetDirectoryName` of a name on its own is empty. `IsRooted` says
whether a path starts from a root rather than the current directory, and `GetRoot` returns that root.

`/` separates names everywhere. On Windows, DOS and UEFI `\` does too, and a root can also be a drive
(`C:\`, or `C:` for that drive's current directory) or a share (`\\server\share\`). On Linux and macOS
`\` is an ordinary character in a name. `Path.Separator` is the one the system writes, and
`Path.IsSeparator` tells you whether a character separates names.

## Join and tidy paths

`Combine` joins paths with the system's separator and returns a new string:

<!-- sample: samples/Paths.cs combine -->
```csharp
// Combine puts the system's separator between two paths, unless the
// first ends with one; a rooted second path replaces the first.
string log = Path.Combine("logs", "today.txt"); // logs/today.txt
string other = Path.Combine("logs", "/tmp/x");   // /tmp/x
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs combine -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Combine puts the system's separator between two paths, unless the first
// ends with one; a rooted second path replaces the first.
let log = Path.Combine("logs", "today.txt") // logs/today.txt
let other = Path.Combine("logs", "/tmp/x")   // /tmp/x
```

`Combine` takes up to three paths. `TryCombine` writes into a span you give it instead, and returns
false if the result doesn't fit. `ChangeExtension("a/b.txt", ".md")` swaps the extension, and an empty
extension removes it.

`Normalize` tidies a path from its text alone:

<!-- sample: samples/Paths.cs normalize -->
```csharp
// Normalize tidies a path from its text: "." goes, ".." takes the name
// before it away, and separators become the system's.
string tidy = Path.Normalize("logs/./old/../today.txt"); // logs/today.txt
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs normalize -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Normalize tidies a path from its text: "." goes, ".." takes the name
// before it away, and separators become the system's.
let tidy = Path.Normalize("logs/./old/../today.txt") // logs/today.txt
```

A relative path keeps any `..` it can't take away (`../..`), a rooted one stops at its root, and a path
that ends up empty is `.`. Normalize doesn't look at the disk, so it doesn't know about symbolic links:
`link/..` may not be where the system would go. `TryNormalize` writes into a span. `GetFullPath`
combines a relative path with the current directory and normalizes the result. It needs the current
directory, so it works where directories do.

## Create and delete directories

`Directory.Create` makes the directory and any missing directories on the way to it. Like every
function below, it returns a `FileError` instead of ending the program:

<!-- sample: samples/Directories.cs create -->
```csharp
// Create makes the directories on the way too; one that's already
// there is fine. Failures come back as a FileError.
FileError error = Directory.Create("dirs.tmp/logs/2026");
if (error != FileError.None)
    Out.Line($"couldn't create it: {(int)error}");
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs create -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Create makes the directories on the way too; one that's already there is
// fine. Failures come back as a FileError.
let created = Directory.Create("dirs.tmp/logs/2026")
if created <> FileError.None then
    printfn "couldn't create it: %d" (int created)
```

A directory that's already there is fine. If a file has the name, `Create` returns `AlreadyExists`,
and if a file is in the way (`notes.txt/x`), `NotADirectory`. `Directory.Exists` is true only for a
directory, and `File.Exists` only for something that isn't one.

`Directory.Delete` removes an empty directory and returns `NotEmpty` for one that isn't:

<!-- sample: samples/Directories.cs delete -->
```csharp
// Delete with recursive: true deletes everything inside first.
error = Directory.Delete("dirs.tmp", recursive: true);
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs delete -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Delete with recursive = true deletes everything inside first.
let deleted = Directory.Delete("dirs.tmp", recursive = true)
```

With `recursive: true` it deletes everything inside first. It deletes symbolic links inside but doesn't
follow them, so it never deletes what a link points to. `Directory.Move` moves or renames a directory,
but only within one file system, and it returns `AlreadyExists` if the new name is taken.

## List a directory

`Directory.Enumerate` gives a directory's entries to `foreach`, without `.` and `..`, in whatever
order the system keeps them:

<!-- sample: samples/Directories.cs enumerate -->
```csharp
// Enumerate lists a directory for foreach: each entry's name (a span,
// good until the next entry) and kind, without "." and "..".
int files = 0, folders = 0;
bool sawSnow = false;
DirectoryEntries entries = Directory.Enumerate("dirs.tmp");
foreach (DirectoryEntry entry in entries)
{
    if (entry.IsDirectory)
        folders++;
    else if (entry.IsFile)
        files++;
    sawSnow |= entry.Name.SequenceEqual("snö☃.txt".AsSpan());
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Files.fs enumerate -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Enumerate lists a directory for a for loop: each entry's name (a span,
// good until the next entry) and kind, without "." and "..".
let mutable files = 0
let mutable folders = 0
let mutable sawSnow = false
for entry in Directory.Enumerate("dirs.tmp") do
    if entry.IsDirectory then folders <- folders + 1
    elif entry.IsFile then files <- files + 1
    sawSnow <- sawSnow || entry.Name.SequenceEqual("snö☃.txt".AsSpan())
```

Each entry's `Name` is a span over a buffer the listing reuses, so it's good until the next entry. Copy
it (`Str.ToArenaString`, or `Path.Combine` with the directory) if you need it later. `IsDirectory`,
`IsFile` and `IsSymbolicLink` say what kind of entry it is. A symbolic link to a directory counts as a
link, not a directory.

The listing holds the directory open, and `foreach` closes it at the end. If the directory can't be
read, `Error` says why (`NotFound`, `NotADirectory`, `AccessDenied`) and there are no entries. If you
call `Enumerate` without looping over the result, call `Dispose` on it yourself.

## The current directory

`Directory.GetCurrentDirectory` returns the current directory as an absolute path, and
`SetCurrentDirectory` changes it. Relative paths start from there. Under wasmtime, a WebAssembly
program starts in `/`, and `./run.sh --wasm` grants it the directory you run it from.

## On each target

Directories, `File.GetInfo`, `Copy`, `Move` and the `FileError` forms of `File`'s functions work on
Linux, macOS, Windows and WebAssembly. They call the C library: `opendir` and `readdir64`, `mkdir`,
`rmdir`, `rename`, `stat`, `getcwd` and `chdir`. Linux's are used directly. On the other systems,
`mac/runtime.c`, `win/runtime.c` and `wasm/runtime.c` translate where their C library differs.
Windows passes paths to the system as UTF-16, as `File` does.

DOS, UEFI, microcontrollers and the C64 don't have them yet. A program that uses them there fails to
link, so you find out at build time.

See also: [Read and write files](read-and-write-files.html), and the
[`Path`](../reference/path.html) and [`Directory`](../reference/directory.html) reference.
