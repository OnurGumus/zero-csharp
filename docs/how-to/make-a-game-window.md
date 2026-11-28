---
title: Make a game window
index: 23
---

# Make a game window

The `ZeroCSharp.SDL3` package lets a zero program open a window, draw on it, and read the keyboard
and mouse, with [SDL 3](https://libsdl.org), the C library many games are built on. The program is
still a small native one with no .NET runtime: SDL does the windowing, your C# does the rest.

This guide uses the .NET SDK packages ([Install with dotnet](../get-started/install-with-dotnet.html)).
The [last section](#bindings-for-another-c-library) shows how the package is made, for wrapping a C
library of your own.

## Get SDL

On **Windows** there's nothing to install: the package carries SDL's own `SDL3.dll`, and the build
copies it next to your program.

On **macOS** and **Linux**, the program links with the system's SDL 3, which the package doesn't
carry. On a Mac:

```bash
brew install sdl3
```

On Linux, newer distributions package SDL 3: Debian 13, Ubuntu 25.04 and Fedora 41, and later
versions of each.

```bash
sudo apt install libsdl3-dev       # Debian 13, Ubuntu 25.04
sudo dnf install SDL3-devel        # Fedora 41
```

**Ubuntu 24.04 and Debian 12 don't have it.** There, build SDL from source: install the build
dependencies that [SDL's Linux guide](https://wiki.libsdl.org/SDL3/README-linux#build-dependencies)
lists for your distribution (SDL's configure step names any that are missing), then:

```bash
git clone --depth 1 -b release-3.4.20 https://github.com/libsdl-org/SDL
cmake -S SDL -B SDL/build -DCMAKE_BUILD_TYPE=Release
cmake --build SDL/build
sudo cmake --install SDL/build && sudo ldconfig
```

That installs it in `/usr/local/lib`. Without SDL 3, the build stops with error ZERO010 and the
folders it looked in. On any Linux, a program [for Windows](#ship-it) builds without SDL installed.

The build finds SDL in Homebrew's folder, or in the usual library folders on Linux. If it's
elsewhere, add the folder to `ZeroLibraryPath` (see [below](#where-a-library-is-found)).

## Make the program

```bash
dotnet new zerosdl -o Game
cd Game
dotnet run
```

A window opens with a square that the arrow keys move; Escape, or closing the window, quits. The
project is a program that references the package:

```xml
<Project Sdk="ZeroCSharp.Sdk/0.16.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ZeroCSharp.SDL3" Version="0.16.0" />
  </ItemGroup>
</Project>
```

## The code

The program starts SDL, opens a window, and makes a renderer, SDL's 2D drawing for that window.
Each step can fail (no display, say), and `SDL.GetError()` says why:

<!-- sample: sdk/ZeroCSharp.Templates/content/zerosdl/Program.cs open -->
```csharp
if (!SDL.Init(InitFlags.Video))
    return Fail("SDL_Init failed: ");
Window window = SDL.CreateWindow("Zero C# and SDL 3"u8, 640, 480);
if (window.IsNull)
    return Fail("no window: ");
Renderer renderer = SDL.CreateRenderer(window);
if (renderer.IsNull)
    return Fail("no renderer: ");
SDL.SetRenderVSync(renderer, 1);
```

Text for SDL is UTF-8: a `u8` literal, or any `ReadOnlySpan<byte>`. With vsync on, presenting a frame
waits for the display, so the loop runs once per screen refresh.

Each frame, the loop takes the waiting events (the window closing, a key going down), then reads
which keys are held now. Events are for things that happen once; `IsKeyDown` is for movement that
lasts while a key is held:

<!-- sample: sdk/ZeroCSharp.Templates/content/zerosdl/Program.cs loop -->
```csharp
float x = 300, y = 220;
bool running = true;
int frame = 0;
while (running && frame != frames)
{
    while (SDL.PollEvent(out Event e))
    {
        if (e.Type == EventType.Quit)
            running = false;
        if (e.Type == EventType.KeyDown
            && e.Scancode == Scancode.Escape)
            running = false;
    }
    if (SDL.IsKeyDown(Scancode.Left)) x -= 4;
    if (SDL.IsKeyDown(Scancode.Right)) x += 4;
    if (SDL.IsKeyDown(Scancode.Up)) y -= 4;
    if (SDL.IsKeyDown(Scancode.Down)) y += 4;
```

Then it draws the frame: clear to a color, draw, and present what was drawn:

<!-- sample: sdk/ZeroCSharp.Templates/content/zerosdl/Program.cs draw -->
```csharp
    SDL.SetRenderDrawColor(renderer, 24, 24, 40);
    SDL.RenderClear(renderer);
    SDL.SetRenderDrawColor(renderer, 240, 180, 40);
    SDL.RenderFillRect(renderer, new FRect(x, y, 40, 40));
    SDL.RenderPresent(renderer);
    frame++;
}
```

`Event` and `FRect` are structs, and the window and renderer are handles, so the loop works on
values. At the end, the program gives SDL's resources back:

<!-- sample: sdk/ZeroCSharp.Templates/content/zerosdl/Program.cs close -->
```csharp
SDL.DestroyRenderer(renderer);
SDL.DestroyWindow(window);
SDL.Quit();
```

## What the package has

The bindings are thin: each method is SDL's function of the same name without the `SDL_` prefix.

| Area | Methods and types |
|---|---|
| Start and stop | `Init`, `Quit`, `GetError` |
| Windows | `CreateWindow`, `DestroyWindow`, `SetWindowTitle`; `Window`, `WindowFlags` |
| Drawing | `CreateRenderer`, `DestroyRenderer`, `SetRenderVSync`, `SetRenderDrawColor`, `RenderClear`, `RenderFillRect`, `RenderRect`, `RenderLine`, `RenderPresent`; `Renderer`, `FRect` |
| Input | `PollEvent`, `IsKeyDown`; `Event` (`Type`, `Scancode`, `Down`, `Repeat`, `X`, `Y`, `Button`), `EventType`, `Scancode` |
| Time | `GetTicks`, `Delay` |

SDL has much more (textures, audio, game controllers). The [last section](#bindings-for-another-c-library)
shows how a binding is written, to add what you need in your own project.

## Run it without a display

SDL picks its video driver from `SDL_VIDEO_DRIVER`. For tests and CI, where there's no display, the
template's `--frames` option stops after that many frames:

```bash
SDL_VIDEO_DRIVER=dummy SDL_RENDER_DRIVER=software dotnet run -- --frames 30   # Windows
SDL_VIDEO_DRIVER=offscreen dotnet run -- --frames 30                          # Linux
```

On macOS, SDL's offscreen driver needs OpenGL, which a Mac doesn't give it: run with a real window
there.

## Ship it

`dotnet publish` puts the program in the publish folder, and on Windows `SDL3.dll` next to it: copy
the folder. A Linux or macOS program needs SDL 3 installed on the machine that runs it, as on the one
that built it.

Building for another system works too. A Windows program builds anywhere, since SDL for Windows is
in the package:

```bash
dotnet publish -p:ZeroTarget=win-x64
```

For Linux or macOS from another system, the build needs that system's SDL library file in
`ZeroLibraryPath`.

## Bindings for another C library

`ZeroCSharp.SDL3` is an ordinary [zero library](create-a-library.html). Two things make it bindings:
its imports, and the C library it tells programs to link with.

### The imports

Zero mode binds every `[DllImport]` when the program is linked: there's no loading at run time, and
no marshalling. So every signature is blittable, with pointers, numbers and structs only. A C `bool`
is one byte, so the import returns `byte`:

<!-- sample: sdk/ZeroCSharp.SDL3/SDL.cs imports -->
```csharp
[DllImport(Lib)] static safe extern byte SDL_Init(InitFlags flags);
```

and the public method turns it into a C# `bool`:

<!-- sample: sdk/ZeroCSharp.SDL3/SDL.cs wrappers -->
```csharp
public static bool Init(InitFlags flags) => SDL_Init(flags) != 0;
```

A function that takes a pointer is `unsafe`, and its wrapper passes a local's address, which can't
outlive the call:

<!-- sample: sdk/ZeroCSharp.SDL3/SDL.cs imports-pointer -->
```csharp
[DllImport(Lib)]
static unsafe extern byte SDL_RenderFillRect(Renderer renderer, FRect* rect);
```

<!-- sample: sdk/ZeroCSharp.SDL3/SDL.cs wrapper-pointer -->
```csharp
public static bool RenderFillRect(Renderer renderer, FRect rect)
{
    unsafe
    {
        // SAFETY: rect is a local; SDL reads it during the call.
        return SDL_RenderFillRect(renderer, &rect) != 0;
    }
}
```

Handles like `SDL_Window*` are a struct with one `nint` field, which passes as the pointer does and
can't be mixed up with another kind of handle. A C union like `SDL_Event` is a struct with
`[StructLayout(LayoutKind.Explicit, Size = ...)]` and its fields at their offsets, each marked
`safe`.

### Linking: ZeroLinkLibrary

The project names the C library with a `ZeroLinkLibrary` item:

```xml
<ItemGroup>
  <ZeroLinkLibrary Include="SDL3" />
</ItemGroup>
```

A name does two things: the program links with `libSDL3.so`, `libSDL3.tbd` or `libSDL3.dylib`, or
`SDL3.lib`, whichever the target uses, and the `[DllImport("SDL3")]` functions are bound to it.
The item goes to every program that uses the library, through a `ProjectReference`, and through a
`PackageReference`: `dotnet pack` writes it into the package.

The item can also be a file, with `DllImport` naming the imports it binds. `ZeroTarget` makes an item
count for one target only, and there it takes the name's place. `RuntimeFile` is a file the program
needs at run time, copied next to it and published with it. This is how the package brings SDL for
Windows:

```xml
<ZeroLinkLibrary Include="native/win-x64/SDL3.lib" DllImport="SDL3"
                 ZeroTarget="win-x64" RuntimeFile="native/win-x64/SDL3.dll" />
```

`dotnet pack` packs files inside the project with it (in `native/win-x64/` here), and programs using
the package link with the packed copy. A file outside the project isn't packed: warning ZERO011.

### Where a library is found

A name is looked for in the folders of `ZeroLibraryPath` (separated by `;`), then, when the program is
for the machine that builds it, where that system keeps libraries: `/usr/local/lib`,
`/usr/lib/<architecture>-linux-gnu`, `/usr/lib64` and `/usr/lib` on Linux; `/opt/homebrew/lib` and
`/usr/local/lib` on macOS. A Windows program, or one for another system, has only `ZeroLibraryPath`.
A library that isn't found is error ZERO010, which lists the folders it looked in.

## What to watch out for

- **Every import has to be linked.** An import of a library that isn't in `ZeroLinkLibrary` or
  `ZeroDllImports` fails the link with `undefined symbol`.
- **No marshalling.** Zero mode converts nothing between C# and C, so keep signatures to numbers,
  pointers and structs of them: `byte*` for text instead of `string`, `byte` for a C `bool`, a
  pointer and a length for an array, and `delegate* unmanaged` for a callback.
- **The bindings' assembly name.** On Windows, file names ignore case: a library named `Sdl3` would
  have its `Sdl3.dll` collide with `SDL3.dll` next to the program. The package's is `ZeroCSharp.SDL3`.
