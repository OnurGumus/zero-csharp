// expect: CS8352
// A dictionary from the scope can't be put in the state, which outlives the callback.
using System;

public ref struct Holder
{
    public ScratchDict<int, int> Kept;
}

public static class Program
{
    public static void Main()
    {
        var holder = new Holder();
        Arena.Scratch(ref holder, static (scoped ScratchScope scratch, scoped ref Holder state) =>
        {
            state.Kept = scratch.Dict<int, int>();
        });
    }
}
