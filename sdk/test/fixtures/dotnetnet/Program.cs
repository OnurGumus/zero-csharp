// For -p:ZeroTarget=dotnet -p:ZeroDotnetTypes=Net: .NET's own Task and
// streams, handed to .NET code that zero mode doesn't have (StreamWriter,
// GZipStream), beside lib/'s fibers, channels and arenas
// (sdk/test/run.sh, dotnet_net_types).
using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

static class Program
{
    static async Task<int> Main()
    {
        // The types are .NET's.
        Console.WriteLine($"Task is .NET's: {typeof(Task).Assembly == typeof(object).Assembly}");
        Console.WriteLine($"Stream is .NET's: {typeof(Stream).Assembly == typeof(object).Assembly}");

        // So .NET's own libraries take them.
        var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new StreamWriter(gzip))
            await writer.WriteAsync(new string('z', 1000));
        Console.WriteLine($"1000 characters, compressed to fewer than 100 bytes: {packed.Length < 100}");

        // An await comes back to the program's own thread.
        int own = Environment.CurrentManagedThreadId;
        await Task.Delay(10);
        await Task.Yield();
        Console.WriteLine($"after await, on the same thread: {Environment.CurrentManagedThreadId == own}");

        // lib/'s own: fibers and a channel, and an arena. With .NET's tasks,
        // the code after an await isn't on a fiber, so what waits on a
        // channel is one: two fibers here, and Fiber.Run runs them.
        var channel = new Channel<int>(2);
        var total = new int[1];
        Fiber.Spawn(channel, static ch =>
        {
            for (int i = 1; i <= 5; i++)
                ch.Send(i * i);
            ch.Close();
        });
        Fiber.Spawn((channel, total), static s =>
        {
            while (s.channel.Receive(out int square))
                s.total[0] += square;
        });
        Fiber.Run();
        Console.WriteLine($"sum of squares {total[0]}");

        Arena arena = Arena.Create(4096, "test");
        Span<int> numbers;
        unsafe { numbers = arena.AllocSpan<int>(100); } // SAFETY: not used after the arena is destroyed
        numbers.Fill(3);
        Console.WriteLine($"arena: {numbers.Length * numbers[99]}");
        unsafe { arena.Destroy(); } // SAFETY: numbers isn't used after this
        return 0;
    }
}
