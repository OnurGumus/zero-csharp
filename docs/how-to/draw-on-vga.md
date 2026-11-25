---
title: Draw on the VGA screen
index: 7
---

# Draw on the VGA screen

`lib/Dos.cs` gives a DOS program the VGA's 320x200 graphics mode with 256 colors, the keyboard, a
clock, the mouse and the PC speaker. This guide draws frames the way `samples/Vga.cs`, a small game,
does. It works only in programs built with `./build.sh --dos`: the functions behind `Dos` are in
`dos/runtime.c`, and a program for another target that calls them won't link.

The sample calls them through the name `Machine`, which is `Dos` in a DOS build and `Uefi` in a UEFI
one: `lib/Uefi.cs` has the same members, so the game also runs with no operating system (see
[Build for UEFI](build-for-uefi.html)). In your own DOS program, write `Dos` where the excerpts say
`Machine`.

## Switch to graphics mode

Make a buffer for the picture, switch the screen to graphics mode, and set the colors you use:

<!-- sample: samples/Vga.cs setup -->
```csharp
Span<byte> picture = new byte[W * H];

Machine.Graphics();
SetPalette();
```

`Dos.Graphics()` selects 320x200 with 256 colors (mode 0x13). The picture is one byte per pixel, row
after row: the pixel at `x, y` is `picture[y * Dos.Width + x]`, and its value is a color number. The
sample allocates the buffer once with `new`, which is never freed, and keeps it for the whole run.

## Choose the colors

Each of the 256 color numbers gets its color from `Dos.SetColor(index, red, green, blue)`, with each
component from 0 to 63 (the VGA has 6 bits each):

<!-- sample: samples/Vga.cs palette -->
```csharp
Machine.SetColor(Black, 0, 0, 4);
Machine.SetColor(DimStar, 20, 20, 28);
Machine.SetColor(BrightStar, 50, 50, 63);
Machine.SetColor(Hull, 10, 50, 60);
Machine.SetColor(Cockpit, 63, 55, 10);
```

The sample names its color numbers as `byte` constants (`Black`, `Hull` and so on), so drawing code
says which thing it draws, not which number.

## Draw into your own buffer

Draw the whole frame into the buffer, not onto `Dos.Screen`. Writing the screen while it's being shown
flickers. `Dos.Fill` sets a span of bytes to one color with the processor's block instructions, so a
row of a rectangle is one call:

<!-- sample: samples/Vga.cs fill-rect -->
```csharp
int left = Math.Max(x, 0), right = Math.Min(x + width, W);
int top = Math.Max(y, 0), bottom = Math.Min(y + height, H);
if (left >= right)
    return;
for (int row = top; row < bottom; row++)
    Machine.Fill(picture.Slice(row * W + left, right - left), color);
```

`Dos.Fill(picture, Black)` clears the whole picture the same way.

## Show it after `WaitFrame`

When the frame is finished, wait for the screen refresh and copy the picture to the screen:

<!-- sample: samples/Vga.cs frame -->
```csharp
Draw(picture, hit);
Machine.WaitFrame();
Machine.Show(picture);
```

`Dos.WaitFrame()` returns when the monitor's beam starts back to the top, 70 times a second in this
mode: the moment to change the screen without tearing. `Dos.Show` copies the picture with block
instructions. At 386DX-33 speed under Bochs, a whole screen takes `Show` or `Fill` a few milliseconds, against
about a hundred for `Span`'s `CopyTo` and `Fill`. That is the emulator's instruction count, not a
measurement on hardware.

Some emulators don't imitate the VGA's timing (QEMU flips the status bit on every read), so a frame
also never takes less than 13 ms by the clock. On a real VGA, where frames are 14.3 ms apart, that
wait never happens.

## Read the keyboard

Call `Dos.PollKeys()` once per frame. It takes in what happened on the keyboard since the last call.
Then `Dos.KeyDown(scanCode)` says whether a key is held, and `Dos.ReadKey()` returns the presses one
at a time:

<!-- sample: samples/Vga.cs input -->
```csharp
// Esc is taken from the key presses, not from what is held down
// right now: pressed and released during the pause after a crash,
// it would otherwise go unnoticed.
Machine.PollKeys();
for (int key = Machine.ReadKey(); key != 0; key = Machine.ReadKey())
    if (key >> 8 == Machine.Esc)
        return;
int steer = (Machine.KeyDown(Machine.Right) ? 1 : 0)
            - (Machine.KeyDown(Machine.Left) ? 1 : 0);
```

- **Scan codes** identify a key by its position, whatever is printed on it. `Dos` names the common
  ones: `Esc`, `Enter`, `Space`, `Up`, `Down`, `Left` and `Right`.
- **`KeyDown`** is for steering: it is true while the key is held.
- **`ReadKey`** is for commands: a press is kept until you read it, so a quick tap isn't missed. It
  returns the scan code in the high byte and the character in the low byte (0 for keys without one),
  or 0 when no press is waiting. Up to 32 presses wait.

## Leave graphics mode

`Dos.Text()` switches back to the 80x25 text screen. The game does that before it prints the best
score:

<!-- sample: samples/Vga.cs back-to-text -->
```csharp
Play(picture);
Machine.Sound(0);
Machine.Text();
if (s_best > bestBefore)
    SaveBest(s_best);
Console.WriteLine($"Best score: {s_best}");
```

`Dos.Sound(0)` turns the speaker off; a tone from `Dos.Sound(hertz)` sounds until the next call.

## Build and run

```bash
./build.sh --dos Vga
./run.sh --dos --window Vga      # arrow keys steer, Esc quits
./run.sh --dos Vga test          # 300 frames by itself, then a report
```

`Vga test` plays by itself, then checks that the screen holds the picture it drew, that the clock
runs, that the speaker switches on and off, and that a file can be written, appended to, read back and
deleted. `test.sh --dos` uses it.

## Watch out for

- **Move by the clock, not by frames.** A machine too slow to draw 70 frames a second would otherwise
  slow the game down. `Vga.cs` takes 70 steps a second by `Dos.Ticks` (milliseconds) and draws after
  them, so a slow machine drops frames instead.
- **Emulators are faster than a 386.** `DOS_IPS=6000000 ./run.sh --dos --window Vga` is about a
  386DX-33 under Bochs. There a frame of the game takes 11 ms, inside the 14.3 ms between refreshes.
- **Start the mouse after `Graphics()`.** `Dos.MouseStart()` returns the number of buttons, or 0
  without a driver; drivers reset themselves when the screen mode changes. `Dos.Mouse(out x, out y)`
  returns the buttons held and the position in screen pixels.
- **File names are DOS names:** at most 8 characters, a dot and 3 more, such as `VGA.DAT`.

See also: the [`Dos` reference](../reference/dos.html), [Build for DOS](build-for-dos.html), and
[How the targets work](../concepts/targets.html).
