// A microcontroller's "hello world": the LED blinks, turned over by a C#
// method that the timer's interrupt calls, while the main program sleeps.
//
// On a Raspberry Pi Pico 2, Out.Line goes to its UART: GP0 (TX, pin 1) and
// GP1 (RX, pin 2), 115200 baud; the Raspberry Pi Debug Probe's UART port, or
// any 3.3 V USB-serial adapter, shows it. In QEMU (dotnet run) it's printed
// in the terminal.
//
// Board (the LED, hardware registers, the timer) is in the SDK's library:
// https://onurgumus.github.io/zero-csharp/how-to/build-for-microcontrollers.html
using System;
using System.Runtime.InteropServices;

public static class Program
{
    // Changed by the interrupt handler, read by Main.
    static int s_ticks;

    // 10 times a second: the LED turns over every tick, five blinks a
    // second. A handler is static, takes nothing, and is short: no
    // allocating, no printing.
    [UnmanagedCallersOnly]
    static void OnTick()
    {
        s_ticks++;
        Board.Led(!Board.LedIsOn);
    }

    public static int Main()
    {
        Console.WriteLine("Hello from C# on a microcontroller!");
        unsafe { Board.StartTicks(10, &OnTick); }
        while (s_ticks < 50)
            Board.WaitForInterrupt();       // sleeps until the next tick
        Board.StopTicks();
        Board.Led(false);
        Console.WriteLine($"Blinked for {s_ticks / 10} seconds.");
        return 0;
    }
}
