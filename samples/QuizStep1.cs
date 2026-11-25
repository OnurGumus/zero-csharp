// Tutorial, step 1: print the quiz's questions with Out and $"..." formatting.
// Steps 2 and 3 are QuizStep2.cs and QuizStep3.cs; step 4 is Quiz.cs.
// Build: ./build.sh QuizStep1    Run: ./run.sh QuizStep1

using System;

namespace QuizStep1Sample;

public static class Program
{
    // docs:numbers
    const int Questions = 6;

    static uint s_seed = 2026;

    // The same questions every run, so a file of answers fits them.
    static int Next(int limit)
    {
        s_seed = s_seed * 1103515245 + 12345;
        return (int)((s_seed >> 16) % (uint)limit);
    }
    // docs:end

    // docs:main
    public static int Main()
    {
        Console.WriteLine($"{Questions} questions:");
        for (int i = 1; i <= Questions; i++)
        {
            int a = Next(50), b = Next(50);
            // Each hole is formatted straight into the output buffer: no string is
            // built. {a,2} pads to two characters, so the columns line up.
            Console.WriteLine($"Q{i}: {a,2} + {b,2} = {a + b,3}");
        }
        return 0;
    }
    // docs:end
}
