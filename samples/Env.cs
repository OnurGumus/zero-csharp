// Example: System.Environment (zerocore/System/Environment.cs): environment
// variables, the processor count, a millisecond clock, NewLine and Exit.
// With an argument, prints that environment variable's value instead;
// with "--exit N", ends with exit code N.
// Exit code = number of failed checks, through Environment.Exit.
// Build: ./build.sh Env

using System;

namespace EnvSample;

public static class Program
{
    static int s_failures;

    static void Check(string name, bool ok)
    {
        if (!ok)
        {
            s_failures++;
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    public static int Main(string[] args)
    {
        // "Env --exit 7": ends with that code, after a line with no newline.
        if (args.Length == 2 && args[0] == "--exit"
            && Str.TryParse(args[1].AsSpan(), out int code))
        {
            Console.Write($"exiting with {code}");
            Environment.Exit(code);
        }
        if (args.Length > 0)
        {
            // docs:variable
            string value = Environment.GetEnvironmentVariable(args[0]);
            if (value == null)
                Console.WriteLine($"{args[0]} isn't set");
            else
                Console.WriteLine($"{args[0]}={value}");
            // docs:end
            return 0;
        }

        Check("an unset variable is null",
            Environment.GetEnvironmentVariable("ZERO_CSHARP_UNSET") == null);
        Check("an empty name is null",
            Environment.GetEnvironmentVariable("") == null);
        Check("NewLine", Environment.NewLine == "\n");
        Check("ProcessorCount", Environment.ProcessorCount >= 1);

        long start = Environment.TickCount64;
        long later = start;
        for (int i = 0; i < 1_000_000 && later == start; i++)
            later = Environment.TickCount64;
        Check("TickCount64 goes forward", later >= start);
        Console.WriteLine("checked Environment");

        // docs:exit
        // No newline: Exit writes what Console still holds before it ends.
        Console.Write(s_failures == 0 ? "PASS" : "FAIL");
        Environment.Exit(s_failures);
        // docs:end
        return 99; // not reached
    }
}
