// Tutorial, step 3: ask again when an answer isn't a number or doesn't fit,
// and let an empty answer skip a question.
// Build: ./build.sh QuizStep3    Run: ./run.sh QuizStep3 (Ctrl-D ends early)

using System;

namespace QuizStep3Sample;

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
        Span<char> buffer = stackalloc char[32];
        int right = 0, asked = 0;

        Console.WriteLine($"{Questions} questions. An empty answer skips one.");
        for (int i = 1; i <= Questions; i++)
        {
            int a = Next(50), b = Next(50);
            asked++;
            // docs:loop
            while (true)
            {
                Console.Write($"Q{i}: {a} + {b} = ");
                int length = In.ReadLine(buffer);
                if (length < 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("(end of input)");
                    asked--;
                    goto Done;
                }
                // A line longer than the buffer returns its full length; the
                // buffer holds only its start, so ask again.
                if (length > buffer.Length)
                {
                    Console.Write($"that's {length} characters; ");
                    Console.WriteLine($"answers are at most {buffer.Length}");
                    continue;
                }

                ReadOnlySpan<char> answer = buffer.Slice(0, length).Trim();
                if (answer.Length == 0)
                {
                    Console.WriteLine("skipped");
                    break;
                }
                if (!Str.TryParse(answer, out long value))
                {
                    Console.WriteLine($"\"{answer}\" isn't a number");
                    continue;
                }

                if (value == a + b)
                {
                    right++;
                    Console.WriteLine("right");
                }
                else
                {
                    Console.WriteLine($"no, it's {a + b}");
                }
                break;
            }
            // docs:end
        }

    Done:
        Console.WriteLine($"{right} of {asked} right");
        return 0;
    }
}
