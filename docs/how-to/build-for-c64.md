---
title: Build for the Commodore 64
index: 21
---

# Build for the Commodore 64

`./build.sh --c64` compiles a sample, `lib/` and zerocore to a `.prg` file for a Commodore 64: an 8-bit
6502 processor at 1 MHz, with 64 KB of memory, of which a program gets 50. It is the same C#, with
classes, interfaces, generics, lambdas, arenas and strings. What changes is how much of it fits.

## With dotnet build

A project made with `dotnet new zero` builds for the C64 too:

```bash
dotnet build -c Release -p:ZeroTarget=c64    # bin/Release/zero03/c64/Hello.prg
```

The first build downloads what the C64 needs beyond the .NET SDK: NativeAOT-LLVM, which compiles C#
to LLVM bitcode, and [llvm-mos](https://llvm-mos.org/), which compiles that for the 6502, 150 to
250 MB together. They come from their own releases, are checked against the hashes the SDK names,
and stay in a cache in your user folder (`~/.cache/zerocsharp/tools`, or
`%LOCALAPPDATA%\ZeroCSharp\tools` on Windows; `ZERO_TOOLS_CACHE` picks another) for every project
after that.

It builds on Linux x64, Windows x64 and Apple Silicon Macs:

- **On a Mac, Docker has to be running.** Microsoft publishes no NativeAOT-LLVM for Apple Silicon, so
  the build runs its Linux version in a container. Docker Desktop works, and so do free alternatives
  such as [Colima](https://github.com/abiosoft/colima) (`brew install colima docker`, then
  `colima start`) or Podman.
- **Linux arm64 can't:** llvm-mos has no build for it.

To run the program, open it in VICE, the Commodore emulator: `x64sc bin/Release/zero03/c64/Hello.prg`
(`brew install vice`, `sudo apt install vice`). Linux distributions ship VICE without the C64's ROMs;
[the next section](#get-the-tools) says where the repository gets them.

An F# project builds for the C64 the same way, from SDK 0.12.0 on. [Write a program in F#](write-fsharp.html#build-for-the-commodore-64)
has `Paddles`, the game below, in F#.

The rest of this page is about the repository's scripts, which build and test its samples.

## Get the tools

```bash
./setup.sh --c64
```

This downloads NativeAOT-LLVM (shared with `--wasm` and `--dos`) and
[llvm-mos](https://llvm-mos.org/), an LLVM with a 6502 code generator and a C library for the C64,
into `tools/`: a 190 MB download, 750 MB unpacked. llvm-mos has builds for macOS and for x64 Linux.
`setup.sh` also checks for what it can't download:

- **The .NET SDK** (10 or newer), to build `zero-bitcode` (`sdk/ZeroCSharp.Bitcode`), which rewrites
  the compiler's output for the 6502.
- **Python 3**, for `c64/run.sh`, which types input into the emulator and reads its output.
- **VICE**, the Commodore emulator (`x64sc`). `brew install vice` on macOS, `sudo apt install vice` on
  Debian or Ubuntu.

Linux distributions ship VICE without the C64's ROMs (its BASIC, its KERNAL and its character set),
which are copyrighted. On Linux, `setup.sh` takes them from VICE's own source release into
`tools/c64/vice-roms`, and `run.sh` points VICE at them. Homebrew's VICE has them already.

## Build

```bash
./build.sh --c64              # the samples that fit, into bin/c64/*.prg
./build.sh --c64 QuizStep2    # just one
./build.sh --c64 Strings      # any sample can be tried
```

A sample that is too big stops the build with how much too big it is:

```text
zero-bitcode: Strings doesn't fit in a C64's memory:
  too big by 99789 bytes
```

To build a program of your own, add its name to `C64_SAMPLES` in `build.sh`.

## Run in the emulator

```bash
./run.sh --c64 Refs                    # prints the output, exits with the program's code
./run.sh --c64 --window C64Demo        # shows the C64's screen, at its real speed
./run.sh --c64 QuizStep2 < answers.txt # types the file into the C64's keyboard
./test.sh --c64                        # every C64 sample, compared with tests/expected
```

A C64 has no standard output and no exit code, so `run.sh --c64` watches from outside. Every character
a program prints goes through one function in `c64/runtime.c`, and VICE's monitor logs the processor's
registers each time that function starts: the character is in register A. The runtime writes the exit
code to an address that VICE's "debug cartridge" watches, and VICE quits with it. The machine runs as
fast as the host allows, so a run takes a second or two. VICE has no mode without a window; one opens,
minimized, for the run. On a Linux machine without a screen, such as a server or CI, give it a virtual
one: `xvfb-run -a ./test.sh --c64` (`sudo apt install xvfb xauth`). A run that hasn't finished after `C64_TIMEOUT` seconds of C64 time (default
600) is stopped and exits with 124.

With `--window` you get the machine itself: its screen, sound and keyboard, at 1 MHz. Close the window
to end.

A program that stops (a `NullReferenceException`, an index out of range, `Environment.FailFast`) also
says where: the calls it was in, innermost first, read from the processor's stack. `run.sh --c64`
shows them as method names:

```text
NullReferenceException
  at Crash_Program__Read
  at Crash_Program__Outer
  at Crash_Program__Main
```

On the C64's own screen (`--window`, or a real machine) they stay addresses, `at $0A80 $19AF $089B
...`, the first two being the helpers that stopped the program. `llvm-nm -n bin/c64/<Sample>.prg.elf`
lists where each method starts: an address belongs to the last method that starts before it.

## Memory

A program, its data, its heap and its stack share the 50 KB from `$0801` to `$CFFF`. These are the
programs `./test.sh --c64` runs:

| Sample | Size | What is left for heap and stack |
|---|---|---|
| `Tiny`, `Minimal` | 6 KB | 44 KB |
| `Hello` | 7 KB | 43 KB |
| `QuizStep1` | 11 KB | 39 KB |
| `VirtualTest` | 13 KB | 37 KB |
| `SmallIntegers`, `SmallNative`, `QuizStep2` | 17 to 18 KB | 32 to 33 KB |
| `SmallKeys` | 20 KB | 30 KB |
| `SmallEncoding`, `SmallStrings`, `SmallDicts` | 26 to 28 KB | 22 to 24 KB |
| `Refs`, `SmallStrDicts`, `SmallObjects`, `SmallScratchBuffer`, `SmallParsing` | 31 to 32 KB | 18 to 19 KB |
| `SmallDecimalFormats`, `SmallLists`, `SmallText`, `SmallUnicode`, `Paddles`, `C64Demo`, `SmallArena` | 34 to 36 KB | 14 to 16 KB |
| `SmallBuilders` | 39 KB | 11 KB |
| `SmallFibers`, `SmallChannels`, `SmallDoubleFormats` | 40 to 41 KB | 9 to 10 KB |
| `SmallFloats`, `SmallScratch` | 43 KB | 6.6 to 7 KB |
| `SmallDoubles`, `SmallFormats` | 45 KB | 4.6 to 5.4 KB |

The size includes the 2 KB shadow stack NativeAOT-LLVM's code keeps object references on. The Small
samples are tests written to fit: each covers a piece of what Strings, Casts, Collections and the
other large samples check.

What a feature adds to a program, measured as the difference between small programs with and without
it (the collections and strings on top of an arena):

| Feature | Cost |
|---|---|
| The runtime, with nothing else | 7.5 KB |
| `Console.WriteLine` with `$"..."` formatting of whole numbers | 4 KB |
| An arena | 9 KB |
| `List<T>`, for two element types | 5.5 KB |
| `Dict<int, int>` | 7.5 KB |
| `StrDict<int>` | 10 KB |
| `StrBuilder`, `Str.Format` and `Str.Concat` | 10 KB |
| Arithmetic on `float` | 4.5 KB |
| Arithmetic on `double` | 22 KB |
| Printing a `float` or a `double` (`ToString`) | 24.5 KB |
| `char.IsLetter`, `IsDigit`, `IsUpper`, `IsLower`, `GetUnicodeCategory` (their Unicode table) | 16.5 KB |
| `char.ToUpper`, `ToLower` above ASCII (their table) | 2 KB |
| `int.TryParse` (`int.Parse`, with its exceptions) | 6.5 KB (9.5 KB) |
| `double.Parse` | 42 KB |

Floating point is the expensive one: the 6502 has none, and the software that stands in for it is far
larger for `double` than for `float`. Printing either brings the code that prints a value exactly,
with big-number arithmetic. A program that computes with `double` and prints the result doesn't fit;
with `float` it does, with about 8 KB to spare. Reading a `double` from text (`double.Parse`) leaves
room for little else, as it divides big numbers exactly; `int.TryParse` is cheap. Every kind of
collection (each set of type arguments) is compiled separately and costs again.

The `char` methods know every Unicode character, so they bring tables. For text typed on a C64, the
ASCII ones do the same job for free: `char.IsAsciiDigit`, `IsAsciiLetter`, `IsAsciiLetterUpper` and the
rest are plain comparisons, and so are `IsWhiteSpace` and `IsControl`.

So keep to whole numbers where you can, and to `float` where you can't; share collection types, and
keep arenas small: 1 KB blocks instead of the default 4 KB. Memory really does run out here;
`Arena.Create(..., stopOnOutOfMemory: true)` says so when it does (see [Catch arena
mistakes](catch-arena-mistakes.html)). `maxCapacity` caps an arena so it leaves the rest of the heap
alone, and `Arena.FromBuffer` over a static buffer takes its memory out of the program's data instead,
so the arena is sure to have it once the program has loaded (see [An arena in a buffer of your own](../concepts/memory.html#an-arena-in-a-buffer-of-your-own)).

The build already favors size everywhere: the C# compiler runs with `--Os`, every function is marked
to be compiled for size, and llvm-mos optimizes the whole program at link time, which makes it a fifth
smaller again.

## The screen, sprites and the keyboard

`lib/C64.cs` has the machine. `Console.WriteLine` and `In.ReadLine` work as everywhere, through the C64's own
screen editor; `C64.Print` puts text at a position, in a color:

<!-- sample: samples/C64Demo.cs text -->
```csharp
static void Draw()
{
    C64.Clear(C64.Black);
    C64.Border(C64.Black);

    string title = "C# on the Commodore 64";
    for (int i = 0; i < title.Length; i++)
        C64.Print(9 + i, 2, title[i], C64.Red + i % 6);
    C64.Print(4, 4, "zero-mode .NET, no runtime, 6502", C64.LightGray);

    // Interface calls on objects from the heap.
    IShape[] shapes = { new Rect { W = 6, H = 7 }, new Circle { Radius = 10 } };
    for (int i = 0; i < shapes.Length; i++)
    {
        int column = C64.Print(4, 7 + i, shapes[i].Name, C64.Yellow);
        column = C64.Print(column, 7 + i, " area = ", C64.Yellow);
        C64.Print(column, 7 + i, shapes[i].Area());
    }

    // A generic list in an arena, filled with Fibonacci numbers.
    Arena arena = Arena.Create(1024);
    List<int> fib = List<int>.Create(arena, 16);
    int a = 0, b = 1;
    for (int i = 0; i < 16; i++)
    {
        fib.Add(a);
        int next = a + b;
        a = b;
        b = next;
    }
    int x = C64.Print(4, 11, "Fib: ", C64.LightGreen);
    for (int i = 0; i < fib.Count && x < 36; i++)
        x = C64.Print(x, 11, fib.Get(i)) + 1;

    // A lambda that captures a local.
    int offset = 1900;
    Func<int, int> year = n => n + offset;
    x = C64.Print(4, 13, "Lambda: 82 + 1900 = ", C64.LightBlue);
    C64.Print(x, 13, Apply(year, 82));

    x = C64.Print(4, 15, "32-bit int: 123456 * 789 = ", C64.LightRed);
    C64.Print(4, 16, 123456 * Opaque(789));

    C64.Print(4, 22, "Press a key to stop", C64.Gray);
}
```

A sprite's shape is 63 bytes, 21 rows of 24 pixels:

<!-- sample: samples/C64Demo.cs sprite -->
```csharp
// A ball, 24 pixels wide and 21 high: three bytes a row.
static ReadOnlySpan<byte> Ball => new byte[]
{
    0x00, 0x7E, 0x00,  0x03, 0xFF, 0xC0,  0x07, 0xFF, 0xE0,
    0x1F, 0xFF, 0xF8,  0x1F, 0xFF, 0xF8,  0x3F, 0xFF, 0xFC,
    0x7F, 0xFF, 0xFE,  0x7F, 0xFF, 0xFE,  0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF,  0xFF, 0xFF, 0xFF,  0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF,  0x7F, 0xFF, 0xFE,  0x7F, 0xFF, 0xFE,
    0x3F, 0xFF, 0xFC,  0x1F, 0xFF, 0xF8,  0x1F, 0xFF, 0xF8,
    0x07, 0xFF, 0xE0,  0x03, 0xFF, 0xC0,  0x00, 0x7E, 0x00,
};
```

`C64.Sprite(0, 0, Ball, C64.White)` shows it. `C64.WaitLine` waits until the video chip draws a given
line of the picture; changing the border color then changes it from that line down, which is how the
bars in the border are made. `C64.WaitFrame` waits for the picture's lower border, the moment to move
things without flicker:

<!-- sample: samples/C64Demo.cs loop -->
```csharp
// One frame: color bars down the border, then move the ball while the
// video chip is below the screen. Returns the ball's new position.
static void Frame(int frame, ref int x, ref int y, ref int dx, ref int dy)
{
    for (int line = 56; line < 248; line += 8)
    {
        C64.WaitLine(line);
        C64.Border(((line >> 3) + frame) & 15);
    }
    C64.WaitFrame();
    C64.Border(C64.Black);

    x += dx;
    y += dy;
    if (x <= 24 || x >= 320)
        dx = -dx;
    if (y <= 50 || y >= 229)
        dy = -dy;
    C64.MoveSprite(0, x, y);
}
```

`C64.ReadKey` gives the next key pressed without waiting, `C64.KeyHeld('w')` whether a key is held down
(several at once: move and fire), `C64.Joystick` the joystick in port 2, and
`C64.Peek` and `C64.Poke` reach every address there is, and cost what a load or a store costs:
`C64.Poke(0xD020, 0)` compiles to one `STA $D020`. `C64.Print` takes about 170 processor cycles a
character, of the 20,000 in a frame, so a full line is a third of a frame.

The KERNAL's interrupt scans the keyboard, counts `C64.Ticks` and blinks the cursor 60 times a second,
at about a tenth of the processor's time. A game that reads keys with `KeyHeld` can switch it off while
it plays, `C64.KernalInterrupt = false`; `ReadKey` then gets nothing and `Ticks` stands still. The
program's end switches it back on.

`Clock`, `Stopwatch`, `Environment.TickCount64`, `Fiber.Sleep` and the timers count the same sixtieths,
and go on while the interrupt is off, a tenth of a second at a time: they then read CIA 2's time-of-day
clock, which counts the mains' cycles and needs no interrupt. Switching the interrupt off the first time
starts that clock at 0. CIA 2's timers are left alone, for RS-232 and for NMI routines. The code that
reads the time-of-day clock is only in programs that switch the interrupt off.

## Memory beyond 64 KB: an REU

A RAM Expansion Unit adds memory the 6502 can't address: the 1700 (128 KB), 1764 (256 KB), 1750 (512
KB) or the C64 Ultimate's, up to 16 MB. `C64.Reu.Read` and `C64.Reu.Write` copy between it and the
C64's own memory, by DMA, a byte a cycle (a KB in about a millisecond), so a program can keep far more
data than fits in 50 KB and bring in what it needs, a piece at a time:

<!-- sample: samples/C64Reu.cs reu -->
```csharp
Span<byte> bytes = stackalloc byte[300];
for (int i = 0; i < bytes.Length; i++)
    bytes[i] = (byte)(i * 7);
// Across the boundary between the first and the second 64 KB.
C64.Reu.Write(0xFF80, bytes);

Span<byte> back = stackalloc byte[300];
C64.Reu.Read(0xFF80, back);
```

`C64.Reu.Size` is the size in bytes, 0 without an REU. Measuring it writes a byte at nine addresses and
puts back what was there. An address past the end of a smaller REU wraps around to 0.

In VICE, `-reu -reusize 512` adds one, and `-reuimage file` fills it from a file first;
`c64/run.sh` passes them on from `C64_VICE_OPTIONS`:

```bash
C64_VICE_OPTIONS="-reu -reusize 512" ./run.sh --c64 C64Reu
```

On a C64 Ultimate, switch the REU on in its menu, and load a file into it from the file browser.

## A game

`Paddles` is a paddle-and-ball game: you against the computer, first to 7 points, with W and S or
a joystick in port 2. It plays a tune on the SID's second and third voices (M turns it off) and its
sound effects on the first.

<video controls preload="none" poster="../img/c64-paddles.jpg" style="width:100%;max-width:720px;border-radius:8px" aria-label="Paddles running in VICE, with its music">
  <source src="../img/c64-paddles.mp4" type="video/mp4" />
</video>

The title screen, the computer playing itself, and a point scored, recorded in VICE with the C64's own
sound.

```bash
./build.sh --c64 Paddles
./run.sh --c64 --window Paddles
```

Its state is records, and unions say who moves a paddle and what each frame of play led to:

<!-- sample: samples/Paddles.cs unions -->
```csharp
// Who moves a paddle, and how fast.
readonly record struct Human(int Speed);
readonly record struct Computer(int Speed);
union Controller(Human, Computer);

// What a frame of play led to.
readonly record struct InPlay();
readonly record struct HitWall();
readonly record struct HitPaddle(Side Side);
readonly record struct Scored(Side By);
```

A frame of play returns an `Outcome`, and the game takes it apart with a `switch`:

<!-- sample: samples/Paddles.cs play -->
```csharp
// Plays the match until someone wins (returns true) or a key stops it:
// Q always, any key in a demo (returns false).
static bool Play(ref Match match, Controller left, Controller right, bool demo,
    int maxFrames = int.MaxValue)
{
    Screen.DrawCourt(match.Score);
    int pause = 50;    // a second before each serve
    for (; match.Frames < maxFrames; match.Frames++)
    {
        Screen.NextFrame();
        Screen.Place(match);
        int key = C64.ReadKey();
        if (key == 'm')
            Music.Toggle();
        else if (key == 'q' || (demo && key != 0))
            return false;
        if (pause > 0)
        {
            pause--;
            continue;
        }

        Outcome outcome = match.Step(left, right);
        if (!match.OnField)
            match.Strays++;
        switch (outcome)
        {
            case HitWall:
                Sound.Play(Effect.Wall);
                break;
            case HitPaddle:
                Sound.Play(Effect.Paddle);
                break;
            case Scored(var side):
                match.Score = match.Score.For(side);
                Screen.ShowScore(match.Score);
                Sound.Play(Effect.Point);
                if (match.Score.Winner is not null)
                    return true;
                match.Serve(towards: side.Opposite);
                pause = 50;
                break;
        }
    }
    return true;
}
```

`union Outcome(InPlay, HitWall, HitPaddle, Scored)` would allocate 24 bytes every frame, which zero
mode never frees: 70 KB a minute, more than the machine has. So `Outcome` is written out by hand, a
struct that keeps its case in its own fields. The `switch` and the `return new HitWall();` above work
on it unchanged:

<!-- sample: samples/Paddles.cs struct-union -->
```csharp
// Outcome is a union too, written out by hand. `union Outcome(InPlay, ...)`
// would keep its case in a field of type object, so each of the 50 outcomes
// a second would be boxed, and zero mode never frees a box. This one keeps
// the case in its own fields. The compiler takes it for a union all the
// same: `return new HitWall();` goes through a constructor, and a switch
// through TryGetValue, so nothing is allocated.
[Union]
readonly struct Outcome : IUnion
{
    enum Case : byte { None, InPlay, HitWall, HitPaddle, Scored }

    readonly Case _case;
    readonly Side _side;    // HitPaddle's Side, Scored's By

    public Outcome(InPlay value) => _case = Case.InPlay;
    public Outcome(HitWall value) => _case = Case.HitWall;
    public Outcome(HitPaddle value) =>
        (_case, _side) = (Case.HitPaddle, value.Side);
    public Outcome(Scored value) => (_case, _side) = (Case.Scored, value.By);

    public bool HasValue => _case != Case.None;

    public bool TryGetValue(out InPlay value)
    {
        value = new();
        return _case == Case.InPlay;
    }

    public bool TryGetValue(out HitWall value)
    {
        value = new();
        return _case == Case.HitWall;
    }

    public bool TryGetValue(out HitPaddle value)
    {
        value = new(_side);
        return _case == Case.HitPaddle;
    }

    public bool TryGetValue(out Scored value)
    {
        value = new(_side);
        return _case == Case.Scored;
    }


    // What IUnion asks for. It boxes; nothing in the game calls it.
    public object Value => _case switch
    {
        Case.InPlay => new InPlay(),
        Case.HitWall => new HitWall(),
        Case.HitPaddle => new HitPaddle(_side),
        Case.Scored => new Scored(_side),
        _ => null,
    };
}
```

`Controller` stays an ordinary union: there are two of them, made once.

Typing `T` at its title screen plays one match computer against computer and reports on it, which is
what `./test.sh --c64` runs: the match is the same every time, and the program checks that it ended
with a winner, that nothing left the field, and that playing allocated nothing.

## Run on a real machine

A `.prg` file is what a C64 loads. An SD card reader for the C64 takes it as it is; for a disk drive or
another emulator, VICE's `c1541` puts it on a disk image:

```bash
c1541 -format "zero,01" d64 zero.d64 -write bin/c64/QuizStep1.prg quizstep1
```

On the C64, `LOAD"*",8,1` and then `RUN`. The program ends at BASIC's `READY.`

## How it works

1. **bflat and NativeAOT-LLVM** compile the sample to LLVM bitcode, as for WebAssembly and DOS, but with
   `--Os`.
2. **`zero-bitcode ll2mos`** rewrites the bitcode. NativeAOT-LLVM lays out every object for 4-byte pointers,
   and the 6502's are 2 bytes. Memory keeps the 32-bit layout: a pointer in memory stays a 4-byte slot
   holding the address and two zero bytes, and only pointers in registers shrink.
3. **`zero-bitcode wasm2mos`** turns the program's data, which NativeAOT-LLVM writes as a WebAssembly
   object file, into assembly. `zero-bitcode` is a small .NET program in `sdk/ZeroCSharp.Bitcode`,
   built on first use.
4. **llvm-mos** compiles and links it all with `c64/runtime.c`, zerocore's native half: the entry point,
   memory, the keyboard and the screen as standard input and output, the clock, and the floating-point
   functions llvm-mos's library lacks.

C's `int` is 16 bits on the 6502 and C#'s is 32, so every function C# imports is bound to a `cs_` name
that `c64/runtime.c` defines with 32-bit parameters: C#'s `calloc` is `cs_calloc` there.

## What's different

- **No end of input.** A keyboard has none, and programs wait for one. A line holding only the `←` key
  (top left of the keyboard) ends the input, as Ctrl-Z does on DOS. `run.sh` types it after a file.
- **Input is a line at a time,** edited with the C64's own cursor keys and handed over at RETURN. A line
  is at most 80 characters.
- **40 columns, and ASCII.** Text prints in the C64's character set with both cases. Characters outside
  ASCII come out as something else.
- **No arguments, files, environment or threads.** `Environment` has no variables; `lib/File.cs` and
  `Parallel.For` don't link. [Fibers](use-fibers.html#fibers-on-a-commodore-64) work, on small stacks.
- **No `Sin`, `Pow`, `Log` and the like:** a program that calls them doesn't link. `+ - * /`, `%`, `Sqrt`,
  `Floor`, `Ceiling`, `Truncate` and the conversions work, in software; `SmallDoubles` checks that they
  give the same results as on other targets.
- **The clock starts when the machine is switched on** and counts sixtieths of a second. There is no
  date.
- **Failure messages go to the screen.** A failed cast or an index out of range ends the program with
  exit code 134, which only an emulator sees.
- **C# can call only the C functions `c64/runtime.c` defines,** as `cs_<name>`.

## Watch out for

- **Tested in VICE only:** 3.10 on macOS, and 3.7.1 on Ubuntu 24.04 x64, where CI runs
  `./test.sh --c64` on every change. Nothing has run on a real C64 yet. The disk image route was
  checked in VICE with its drive emulation.
- **Programs that fit can still run out of memory.** `SmallFormats` runs with 4.6 KB to spare.
- **A long method can be too much for llvm-mos:** the 6502 has three registers, and the error is
  "ran out of registers". `build.sh` then compiles that method's module separately and tries again; if
  it still fails, split the method.
- **No pointers to generic methods over classes.** `&Method<SomeClass>` is a "fat" pointer, marked by
  the top bit of 32, which a 16-bit pointer can't keep; the build stops with a message saying so.
  Call through a generic class's virtual method instead, as `lib/Fibers.cs` does.
- **The compiler versions are a set,** as for WebAssembly and DOS, and llvm-mos is one more.
- **A C64 build is always optimized for size.** `ZeroOptimizeFor` doesn't apply, nor do
  `ZeroInstructionSet` and `ZeroDllImports`.

See also: [How the targets work](../concepts/targets.html) and [Build for DOS](build-for-dos.html).
