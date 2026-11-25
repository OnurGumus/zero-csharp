// A window with a square the arrow keys move; Escape or closing the window
// quits. SDL 3 opens the window and draws: on Windows it comes with the
// ZeroCSharp.SDL3 package, on Linux and macOS it's the system's (Homebrew's
// sdl3; a libsdl3-dev or SDL3-devel package, or SDL built from source: see
// the guide).
//
// "--frames N" stops after N frames and prints where the square ended up
// (with SDL_VIDEO_DRIVER=dummy or offscreen, it runs without a display).
//
// Guide: https://onurgumus.github.io/zero-csharp/how-to/make-a-game-window.html

using System;
using Sdl3;

public static class Program
{
    public static int Main(string[] args)
    {
        int frames = -1;
        if (args.Length == 2 && args[0] == "--frames"
            && !Str.TryParse(args[1].AsSpan(), out frames))
            frames = 1;

        // docs:open
        if (!SDL.Init(InitFlags.Video))
            return Fail("SDL_Init failed: ");
        Window window = SDL.CreateWindow("Zero C# and SDL 3"u8, 640, 480);
        if (window.IsNull)
            return Fail("no window: ");
        Renderer renderer = SDL.CreateRenderer(window);
        if (renderer.IsNull)
            return Fail("no renderer: ");
        SDL.SetRenderVSync(renderer, 1);
        // docs:end

        // docs:loop
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
            // docs:end
            // A run with --frames presses nothing: drift, so it shows the
            // loop working.
            if (frames > 0) x += 1;

            // docs:draw
            SDL.SetRenderDrawColor(renderer, 24, 24, 40);
            SDL.RenderClear(renderer);
            SDL.SetRenderDrawColor(renderer, 240, 180, 40);
            SDL.RenderFillRect(renderer, new FRect(x, y, 40, 40));
            SDL.RenderPresent(renderer);
            frame++;
        }
        // docs:end

        // docs:close
        SDL.DestroyRenderer(renderer);
        SDL.DestroyWindow(window);
        SDL.Quit();
        // docs:end
        Console.WriteLine($"{frame} frames, square at {(int)x},{(int)y}");
        return 0;
    }

    // SDL's message for what went wrong, after ours.
    static int Fail(string what)
    {
        Console.Write(what);
        Out.WriteUtf8(SDL.GetError());
        Console.WriteLine();
        Out.Flush();
        SDL.Quit();
        return 1;
    }
}
