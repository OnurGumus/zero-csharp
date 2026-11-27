// expect: CS9362
// Arena.Scratch in a buffer of yours is unsafe: the caller promises that
// nothing else uses the buffer during the call, so it needs an unsafe block.
using System;

public static class Program
{
    public static int Main()
    {
        Span<byte> memory = stackalloc byte[512];
        return Arena.Scratch(memory, static (scoped ScratchScope scratch) => scratch.AllocSpan<int>(4).Length);
    }
}
