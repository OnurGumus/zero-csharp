// One program, built as a zero-mode native program and, with
// -p:ZeroTarget=dotnet, as a .NET one: but for the first line, both must
// print the same (sdk/test/run.sh, dotnet_target).
using System;
using System.IO;
using System.Threading.Tasks;

static class Program
{
    static int s_counter; // no lock: one fiber runs at a time, on .NET too

    static async Task<int> Main()
    {
#if ZERO_DOTNET
        Console.WriteLine("on .NET");
#else
        Console.WriteLine("on zero");
#endif
        // Task.Run starts a fiber: these take turns, and never run at once.
        var tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(() =>
            {
                for (int n = 0; n < 1000; n++)
                {
                    s_counter++;
                    if (n % 100 == 0)
                        Fiber.Yield();
                }
            });
        await Task.WhenAll(tasks);
        Console.WriteLine($"counter {s_counter}");

        var channel = new Channel<int>(2);
        Fiber.Spawn(channel, static ch =>
        {
            for (int i = 1; i <= 5; i++)
                ch.Send(i * i);
            ch.Close();
        });
        int sum = 0;
        await foreach (int square in channel.ReceiveAllAsync())
            sum += square;
        Console.WriteLine($"sum of squares {sum}");

        await Task.Delay(20);
        File.WriteAllText("dotnettarget.tmp", "written by lib/'s File\n");
        using (var stream = new FileStream("dotnettarget.tmp", FileMode.Append))
            stream.Write("and its FileStream\n"u8);
        Exception error = Catch.Run(() => new FileStream("no/such/file", FileMode.Open));
        Console.WriteLine(error is IOException ? "missing file: an IOException came back" : "missing file: " + error);
        Console.WriteLine((await File.ReadAllBytesAsync("dotnettarget.tmp")).Length + " bytes");
        File.Delete("dotnettarget.tmp");
        Console.WriteLine($"{1234.5:N1} {Math.PI:F4} {int.Parse("42") + 1}");
        return 0;
    }
}
