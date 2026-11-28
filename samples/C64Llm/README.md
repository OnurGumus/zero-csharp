# A language model on a Commodore 64, in F#

`C64Llm` runs [stories260K](../../models/README.md), Karpathy's smallest TinyStories model (about
260,000 parameters, trained on children's stories), on a Commodore 64 with a RAM Expansion Unit. Type
the start of a story and it goes on, a word at a time:

```
STORIES260K ON A C64
Type the start of a story.

> /12
tokens: 12

> Once upon a time
, there was a little gir
(7 tokens, 626 s)
```

A video of it, sped up: [`docs/img/c64-llm.mp4`](../../docs/img/c64-llm.mp4) (51 s).

It needs an REU of 512 KB or more (a 1750, or the C64 Ultimate's), which holds the model. It is all
F#: the hardware it needs is in the SDK's library, as `C64.Reu` (the REU), `C64.KernalRom` (8 KB more
RAM) and `Kernels.DotI8` (an int8 dot product, in 6502 assembly).

## Build and run

1. **The REU image**, with the host tool (an ordinary .NET program):

   ```bash
   dotnet run --project samples/C64Llm/tool -c Release -- \
       pack models/stories260K.bin models/tok512.bin bin/stories260K.reu
   ```

   It writes 512 KB: the model, int8, about 270 KB, then room for the key/value cache.

2. **The program**, with the Zero C# SDK (on a Mac, with Docker running). It needs `C64.Reu`,
   `C64.KernalRom`, the 6502 `Kernels.DotI8`, `ZeroC64ShadowStack` and `ZeroC64FastMultiply`, which
   came in SDK 0.16.0.

   ```bash
   cd samples/C64Llm && dotnet build -p:ZeroTarget=c64
   ```

   The repository's `Directory.Build.props` puts the program in
   `samples/C64Llm/obj/C64Llm/bin/Debug/zero03/c64/C64Llm.prg`.

3. **In VICE**, with the image in an REU:

   ```bash
   x64sc -reu -reusize 512 -reuimage bin/stories260K.reu \
       samples/C64Llm/obj/C64Llm/bin/Debug/zero03/c64/C64Llm.prg
   ```

   Or `c64/run.sh`, which types standard input into the C64 and prints what it prints (copy the `.prg`
   and `.prg.elf` to `bin/c64/` first):

   ```bash
   printf '/24\nOnce upon a time\n' | \
       C64_VICE_OPTIONS="-reu -reusize 512 -reuimage $PWD/bin/stories260K.reu" c64/run.sh C64Llm
   ```

4. **On a C64 Ultimate**: switch the REU on (512 KB or more), load `stories260K.reu` into it from the
   file browser, then run `C64Llm.prg`, with the turbo on. (Not tried on real hardware yet.)

A line like `/24` limits a story to 24 tokens (prompt included; the most is 128, all the model can
hold). An empty line lets the model start a story of its own.

## How fast

On a stock C64 (1 MHz) a token takes about 60 seconds early in a story, and more as the story grows:
attention reads everything so far for each token. At the Ultimate's 64 MHz that is about a second a
token (the REU's copying doesn't speed up with the turbo). Most of the time goes on multiplying int8
weights, about 260,000 multiplications a token at about 100 cycles each. In VICE, the 12-token story
above takes 626 s of C64 time; an earlier version, with its inner loops in C, took 663.

Three things made the F# that fast:

- **Power-of-two row scales.** Each row's sum is scaled with a shift: a scale m / 2^k would need a
  multiplication of 32-bit numbers per row, about 2,500 cycles, for 3,500 rows a token. The weights
  lose nothing measurable (the same next token as the float model 98% of the time either way).
- **`DotI8` in assembly from end to end**, about 480 cycles a call and 100 a product.
- **`ZeroC64FastMultiply`** for the multiplications that are left (attention's, the feed-forward
  gate's): a table of quarter squares, about 700 cycles instead of 1,100, for 1 KB of RAM.

## How it is checked

`tool/` runs the same F# on the PC, the model and its arithmetic, over the image in memory, and
checks it:

```bash
dotnet run --project samples/C64Llm/tool -c Release -- run bin/stories260K.reu "Once upon a time" 24
dotnet run --project samples/C64Llm/tool -c Release -- \
    compare models/stories260K.bin models/tok512.bin bin/stories260K.reu 100
```

- `run` prints what the C64 prints, token for token: the two compute the same bits.
- `compare` measures the integer model against the float one (llama2.c's arithmetic): reading the
  float model's stories for six prompts, the integer model picks the same next token 98% of the time.

## How it works

| File | What it is |
|---|---|
| `Model.fs` | The model: its shape, its memory, the steps of a layer, the key/value cache |
| `Program.fs` | The C64 program: reads a line, tells the story |
| `Machine.fs` | The arithmetic over vectors and the tokenizer; at the top, what differs between the C64 and the PC (the REU, the dot product, the clock) |
| `tool/` | The host tool: packs the image (`Pack.fs`), the float reference (`Float.fs`) |

**Integers only.** The 6502 has no floating point, and llvm-mos's software floats would take 20 of the
50 KB a program gets. Activations are Q16.16 fixed point; weights are int8 with a power-of-two scale
per row; a row times a vector rounds the vector to int8 too, with one power-of-two scale. RMS norm uses an integer
square root, softmax and SiLU tables of exp and sigmoid, and RoPE a table of cosines.

**The REU.** The C64 can't address the REU, only copy between it and its own 64 KB, a byte per cycle.
Each row of weights is copied in as it is needed, and the key/value cache lives there too.

**The multiplications.** The 6502 can't multiply. `Kernels.DotI8` takes each product from a table of
quarter squares, `a * b = (a + b)^2 / 4 - (a - b)^2 / 4`, about 100 cycles for a multiplication and
the sum, and `ZeroC64FastMultiply` does the same for every multiplication of 32-bit numbers. Elsewhere
the F# keeps to 32 bits: a 64-bit product is made from two 32-bit ones (`mulShift`), since llvm-mos's
64-bit multiplication takes about 20,000 cycles.

**Memory.** A C64 program gets 50 KB for code, data, heap and stacks together, and this one is 47 KB,
with 2 KB of tables in RAM (`DotI8`'s and the multiplication's).
Every buffer (the model's vectors, the kernels' rows, the typed line) lives in the 8 KB of RAM under
the KERNAL ROM, which the program switches out at the start (`C64.KernalRom = false`), at a constant
address: Machine.fs's `Map` and Model.fs's `Layout` lay them out. Constant addresses matter on the
6502: buffers allocated at the start would be module values, which a static constructor sets up and
every use checks, 1.6 KB of code. It also fits because the buffers are native pointers, not arrays
(each array access costs a null check, a bounds check and work for the garbage collector's
bookkeeping, which made the code three times as big), and because the project shrinks the runtime's
two stack reserves after measuring what it uses (`ZeroC64ShadowStack`, `ZeroC64StackReserve` in
`C64Llm.fsproj`). That leaves about 1 KB of heap, which is enough: the program allocates almost
nothing as it runs. It also prints its numbers with a few lines of its own, since `Console.Write(n)`
brings in .NET's number formatting, 700 bytes it couldn't spare.

## Limits

- Greedy decoding only (always the likeliest token), so the same prompt tells the same story.
- 128 tokens of context: the prompt and the story together.
- A line is at most 40 characters, ASCII.
