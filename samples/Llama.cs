// Example: a small language model telling a story (lib/Llama.cs), with the
// model compiled into the program.
// Build: ./build.sh Llama    Run: ./run.sh Llama [-n steps] [-t temperature] [-p top-p] [-s seed] [-i prompt] [-v]
//                            or: ./run.sh Llama -c     (take turns telling a story)
//
// The model is Karpathy's stories260K (models/): about 260,000 parameters
// trained on children's stories, and its 512-token tokenizer. build.sh turns
// both into byte literals, so the program reads them in place, with no files.
// By default it continues "Once upon a time" greedily for 64 tokens, which is
// the same story every run and on every target; -t picks words by chance.
// With -c you take turns: type a line, and the model continues the story
// from there, keeping everything said so far as its context.

using System;

namespace LlamaSample;

public static class Program
{
    static void Usage()
    {
        Console.WriteLine("Usage: Llama [-n steps] [-t temperature] [-p top-p] [-s seed] [-i prompt] [-v]");
        Console.WriteLine("       Llama -c [-e] [-t temperature] [-p top-p] [-s seed]");
        Console.WriteLine("  -n  tokens to produce, the prompt's included (default 64)");
        Console.WriteLine("  -t  0 picks the likeliest token (default); higher is more random");
        Console.WriteLine("  -p  with -t, choose only among the likeliest tokens that add up to p (default 0.9)");
        Console.WriteLine("  -s  random seed, for -t (default 42)");
        Console.WriteLine("  -i  the start of the story (default \"Once upon a time\")");
        Console.WriteLine("  -v  print the token ids and the speed");
        Console.WriteLine("  -c  take turns: each line you type joins the story, then the model adds to it");
        Console.WriteLine("  -e  with -c, print each line read (for input from a file)");
    }

    public static int Main(string[] args)
    {
        int steps = 64;
        double temperature = 0, topP = 0.9;
        long seed = 42;
        string prompt = "Once upon a time";
        bool verbose = false, turns = false, echo = false;
        bool temperatureGiven = false;
        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];
            if (flag == "-v" || flag == "-c" || flag == "-e")
            {
                verbose |= flag == "-v";
                turns |= flag == "-c";
                echo |= flag == "-e";
                continue;
            }
            if (i + 1 >= args.Length)
            {
                Usage();
                return 1;
            }
            string value = args[++i];
            bool ok = flag switch
            {
                "-n" => Str.TryParse(value, out steps),
                "-t" => temperatureGiven = Str.TryParse(value, out temperature),
                "-p" => Str.TryParse(value, out topP),
                "-s" => Str.TryParse(value, out seed),
                "-i" => (prompt = value) != null,
                _ => false,
            };
            if (!ok)
            {
                Usage();
                return 1;
            }
        }

        // docs:create
        Arena arena = Arena.Create(64 * 1024, "llama", stopOnOutOfMemory: true);
        // Room for the tokens this run makes: -n of them, or all the model
        // allows when taking turns.
        int positions = turns ? 0 : steps;
        Llama llama = Llama.Create(
            Stories260K.Model, Stories260K.Tokenizer, arena, positions);
        // docs:end
        if (!llama.IsCreated)
        {
            Console.WriteLine("The model or the tokenizer is malformed.");
            return 1;
        }
        if (steps < 1 || steps > llama.SeqLen)
            steps = llama.SeqLen;

        // The prompt as UTF-8, then as tokens, in the arena.
        Span<byte> text;
        Span<int> promptIds;
        unsafe
        {
            // SAFETY: both stay inside the arena, which lives until Main returns.
            text = new Span<byte>(arena.AllocArray<byte>(prompt.Length * 3), prompt.Length * 3);
            promptIds = new Span<int>(arena.AllocArray<int>(prompt.Length * 3 + 2), prompt.Length * 3 + 2);
        }
        int textLength = Utf8(prompt, text);
        int promptCount = llama.Encode(text.Slice(0, textLength), promptIds);

        // Taking turns: greedy picking makes the model repeat itself, so
        // words are picked by chance unless -t says otherwise.
        if (turns && !temperatureGiven)
            temperature = 0.8;
        LlamaSampler sampler = LlamaSampler.Create(llama.VocabSize, (float)temperature, (float)topP, (ulong)seed, arena);
        if (turns)
            return TakeTurns(llama, sampler, arena, echo);

        // docs:generate
        int token = promptIds[0]; // BOS
        for (int position = 0; position + 1 < steps; position++)
        {
            unsafe
            {
                // SAFETY: Forward's logits are read before the next call.
                float* logits = llama.Forward(token, position);
                int next = position + 1 < promptCount
                    ? promptIds[position + 1]           // still reading the prompt
                    : sampler.Sample(logits, llama.VocabSize);
                if (next == 1)
                    break; // BOS: the story is over
                Out.WriteUtf8(llama.Decode(token, next));
                if (verbose)
                    Console.Write($"[{next}]");
                token = next;
            }
        }
        Console.WriteLine();
        // docs:end
        return 0;
    }

    // docs:turns
    // You and the model take turns adding to one story. The model's memory
    // (its key/value cache) keeps every token so far, so each turn only feeds
    // the new tokens: yours, then the ones it picks, until it ends two
    // sentences or 48 tokens. When the story fills the model's context, or
    // the model ends it, a new one begins.
    static int TakeTurns(Llama llama, LlamaSampler sampler, Arena arena, bool echo)
    {
        Console.WriteLine(
            "Tell a story together: type a line, and the model continues it.");
        Console.WriteLine(
            "An empty line lets it go on by itself; end of input stops.");
        Span<char> line = stackalloc char[200];
        Span<byte> text;
        Span<int> ids;
        unsafe
        {
            // SAFETY: both stay inside the arena, which outlives this method.
            text = new Span<byte>(arena.AllocArray<byte>(600), 600);
            ids = new Span<int>(arena.AllocArray<int>(602), 602);
        }
        int position = 0;
        int token = 1; // BOS: the start of a story, not yet fed to the model

        while (true)
        {
            Console.Write("> ");
            int length = In.ReadLine(line);
            if (length < 0)
            {
                Console.WriteLine();
                return 0;
            }
            if (length > line.Length)
            {
                Console.WriteLine($"(a line is at most {line.Length} characters)");
                continue;
            }
            ReadOnlySpan<char> said = line.Slice(0, length).Trim();
            if (echo)
                Console.WriteLine(said);

            // Your line, as tokens after the story so far. Encode starts
            // with BOS, which the model has already seen unless the story is new.
            int count = llama.Encode(text.Slice(0, Utf8(said, text)), ids);
            if (said.Length == 0)
                count = 1;
            if (position + count + 48 >= llama.SeqLen)
            {
                Console.WriteLine(
                    "(The story is as long as the model can remember.");
                Console.WriteLine(" A new one begins.)");
                position = 0;
                token = 1;
            }
            for (int i = 1; i < count; i++)
            {
                // SAFETY: the logits aren't used.
                unsafe { llama.Forward(token, position++); }
                token = ids[i];
            }

            // The model's turn.
            int sentences = 0;
            for (int produced = 0; produced < 48 && sentences < 2; produced++)
            {
                int next;
                unsafe
                {
                    // SAFETY: the logits are sampled before the next Forward.
                    float* logits = llama.Forward(token, position++);
                    next = sampler.Sample(logits, llama.VocabSize);
                }
                if (next == 1)
                {
                    Console.WriteLine();
                    Console.WriteLine("(The end. A new story begins.)");
                    position = 0;
                    token = 1;
                    break;
                }
                ReadOnlySpan<byte> piece = llama.Decode(token, next);
                Out.WriteUtf8(piece);
                byte last = piece.Length > 0 ? piece[piece.Length - 1] : (byte)0;
                if (last == '.' || last == '!' || last == '?' || last == '\n')
                    sentences++;
                token = next;
            }
            Console.WriteLine();
        }
    }
    // docs:end

    // UTF-16 to UTF-8; returns the byte count. output needs 3 bytes per char.
    static int Utf8(ReadOnlySpan<char> s, Span<byte> output)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int c = s[i];
            if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s.Length && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF)
                c = 0x10000 + ((c - 0xD800) << 10) + (s[++i] - 0xDC00);
            if (c < 0x80)
            {
                output[n++] = (byte)c;
            }
            else if (c < 0x800)
            {
                output[n++] = (byte)(0xC0 | (c >> 6));
                output[n++] = (byte)(0x80 | (c & 0x3F));
            }
            else if (c < 0x10000)
            {
                output[n++] = (byte)(0xE0 | (c >> 12));
                output[n++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                output[n++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                output[n++] = (byte)(0xF0 | (c >> 18));
                output[n++] = (byte)(0x80 | ((c >> 12) & 0x3F));
                output[n++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                output[n++] = (byte)(0x80 | (c & 0x3F));
            }
        }
        return n;
    }
}
