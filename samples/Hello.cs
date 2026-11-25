// Example: zero-mode "Hello World" through Console (~13 KB, no .NET runtime).
// Build: ./build.sh Hello
//
// lib/ puts Console in zero mode: Console.WriteLine buffers its text and
// writes it out as UTF-8, without the .NET runtime behind it.

using System;

namespace HelloSample;

public static class Program
{
    public static void Main()
    {
        Console.WriteLine("Hello from zero-mode!");
    }
}
