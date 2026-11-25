---
title: Run a language model
index: 13
---

# Run a language model

`lib/Llama.cs` runs small Llama-style language models in the formats of Andrej Karpathy's llama2.c:
a model file and a tokenizer file. `samples/Llama.cs` uses it with stories260K, a model of about
260,000 parameters trained on children's stories, and tells a story:

```bash
./build.sh Llama
./run.sh Llama                              # "Once upon a time", the likeliest word each time
./run.sh Llama -t 0.8 -s 7 -i "The dragon"  # a different start, words picked by chance
./run.sh Llama -c                           # take turns telling a story with it
```

The program is about 1.1 MB, of which the model is 1 MB, and it needs no files: `build.sh` compiles
`models/stories260K.bin` and `models/tok512.bin` into it as byte literals. It runs on every target,
DOS on a 386 included (64 tokens in a few seconds of an emulated 386), and
[in your browser](../get-started/browser.html).

## With the .NET SDK

Without the repository, `dotnet new zerollama` makes the same program as a project. The model comes
from the `ZeroCSharp.Stories260K` package, compiled in the same way, and the engine (`Llama`,
`LlamaSampler`) is in the SDK's library:

```bash
dotnet new install ZeroCSharp.Templates
dotnet new zerollama -o Story
cd Story
dotnet run                     # "Once upon a time", the likeliest word each time
dotnet run -- -c               # take turns telling a story with it
dotnet build -p:ZeroTarget=win-x64
```

`dotnet run` builds Debug, which isn't optimized and tells stories at about half the speed;
`dotnet run -c Release` is the fast one. The project's `.vscode` folder debugs it in VS Code
([Debug a program](debug-a-program.html)).

Its `Program.cs` is `samples/Llama.cs`. To use another model of llama2.c's format, pass its bytes
to `Llama.Create` instead of `Stories260K.Model` and `Stories260K.Tokenizer`.
[Install with dotnet](../get-started/install-with-dotnet.html) covers the SDK itself.

## Load the model

`Llama.Create` takes the model and tokenizer as byte spans and reads them in place. The weights are
never copied, and the run state (activations and the key/value cache) comes from an arena:

<!-- sample: samples/Llama.cs create -->
```csharp
Arena arena = Arena.Create(64 * 1024, "llama", stopOnOutOfMemory: true);
// Room for the tokens this run makes: -n of them, or all the model
// allows when taking turns.
int positions = turns ? 0 : steps;
Llama llama = Llama.Create(
    Stories260K.Model, Stories260K.Tokenizer, arena, positions);
```

The last argument is how many positions the run will use. The key/value cache grows with it: all
512 positions of this model take 655 KB, 64 take 82 KB, which is what lets the program run on a
Raspberry Pi Pico 2 with its 520 KB of RAM.

The spans can be anything that stays put while the model is used. Here they are literals compiled
into the program; a file read into an arena works the same way.

## Generate text

Each step runs the model on one token at one position and gets a score (a logit) for every token in
the vocabulary. While the prompt lasts, the next token is the prompt's; after that, the sampler picks
one. `Decode` turns a token back into its UTF-8 bytes:

<!-- sample: samples/Llama.cs generate -->
```csharp
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
```

Generating allocates nothing: `Forward` works in the buffers `Create` took from the arena, and
`Decode` points into the tokenizer.

## Take turns telling a story

With `-c`, you and the model take turns: each line you type joins the story, and the model adds a
sentence or two. An empty line lets it go on by itself; end of input (Ctrl-D) stops.

```text
> Tom found a tiny robot in the garden.
 He was very fast and could not pick it up. He wanted to make a big, soft, store, but it was not bad.
> The robot said hello.

Tom was surprised.
```

It isn't a chat: the model was trained only to continue children's stories, so a question becomes
the start of one. What makes the turns work is the model's memory, its key/value cache: it keeps
every token of the story so far, so each turn only runs the new tokens, yours and then its own.

<!-- sample: samples/Llama.cs turns -->
```csharp
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
```

When the story reaches the 512 tokens the model can remember, or the model ends it, a new story
begins. `-e` prints each line it reads, for turns that come from a file.

## Choose how words are picked

- **Greedy** (`-t 0`, the default): always the likeliest token. The same prompt gives the same story
  every run, on every target, which is what `test.sh` checks.
- **Temperature** (`-t 0.8`): divide the scores by the temperature and pick by chance. Lower is safer,
  higher more surprising.
- **Top p** (`-p 0.9`): with a temperature, pick only among the likeliest tokens whose probabilities
  add up to p, so the unlikely tail never comes up.

`-s` seeds the random numbers, so a sampled story can be repeated.

## How it fits together

The forward pass is llama2.c's: per layer, RMS normalization, attention with rotary position
encoding and grouped key/value heads, then a SwiGLU feed-forward block. The matrix-vector products,
`RmsNorm`, `Softmax` and `Silu` come from `lib/Kernels.cs`, with SIMD where the build's `-m` allows
(the sample is built like Simd and Kernels). The tokenizer turns text into tokens the same way as
llama2.c: a piece per character, falling back to bytes, then merging the best-scoring adjacent pair
until none is left.

The model's greedy story was checked token for token against an independent Python implementation
of the same algorithm.
