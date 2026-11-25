// Tutorial, step 2: read an answer to each question with In.ReadLine.
// Build: ./build.sh QuizStep2    Run: ./run.sh QuizStep2 (Ctrl-D ends early)

using System;

namespace QuizStep2Sample;

public static class Program
{
    const int Questions = 6;

    static uint s_seed = 2026;

    static int Next(int limit)
    {
        s_seed = s_seed * 1103515245 + 12345;
        return (int)((s_seed >> 16) % (uint)limit);
    }

    public static int Main()
    {
        // docs:buffer
        // Each answer is read into this; nothing is allocated per question.
        Span<char> buffer = stackalloc char[32];
        int right = 0, asked = 0;
        // docs:end

        for (int i = 1; i <= Questions; i++)
        {
            int a = Next(50), b = Next(50);
            // docs:ask
            // Console.Write holds the prompt in a buffer; In.ReadLine writes it
            // out first, so it shows before the program waits for the answer.
            Console.Write($"Q{i}: {a} + {b} = ");
            int length = In.ReadLine(buffer);
            if (length < 0)
            {
                Console.WriteLine();
                Console.WriteLine("(end of input)");
                break;
            }
            asked++;
            // docs:end

            // docs:check
            ReadOnlySpan<char> answer = buffer.Slice(0, Math.Min(length, buffer.Length));
            if (Str.TryParse(answer.Trim(), out long value) && value == a + b)
            {
                right++;
                Console.WriteLine("right");
            }
            else
            {
                Console.WriteLine($"no, it's {a + b}");
            }
            // docs:end
        }

        Console.WriteLine($"{right} of {asked} right");
        return 0;
    }
}
