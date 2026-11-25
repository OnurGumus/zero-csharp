---
title: Concepts
index: 0
---

# Concepts

These pages explain how zero-mode C# works and why it works that way: what is left when the .NET
runtime is gone, what replaces the garbage collector, and what each build target does with the same
source. They build a mental model rather than walk through code. For code you can run, start with the
[tutorial](../tutorial/index.html); for a specific task, see the [task guides](../how-to/index.html).

## Start with three facts

Most of what follows comes from three facts about zero mode:

1. **There is no runtime.** The program is C# compiled ahead of time to native code. What runtime code
   it has comes from the core library, `zerocore/`, which is ordinary C# in this repository.
2. **Nothing is ever freed for you.** `new` calls `calloc`, and there is no garbage collector to give
   the memory back. Short-lived data goes in arenas, which free everything at once.
3. **Exceptions can't be caught.** When something goes wrong, including a `throw`, the program prints
   a message and ends.

## The vocabulary

- **zerocore** is the core library: `object`, `string`, `Span<T>`, allocation, casts and static
  fields. It is what the compiler requires.
- **lib** is the library programs use: arenas, collections, string support, input and output.
- An **arena** hands out memory by moving a pointer forward and frees everything at once. Each arena
  stands for one lifetime.
- A **handle** is a struct holding one pointer, such as `Arena`, `List<T>` or `StrBuilder`. Copying it
  shares what it points at.
- A **mark** remembers an arena's position, and a **scope** frees back to it when a `using` block ends.
- A **span** is a view of characters or elements that someone else owns. Reading through one allocates
  nothing.
- A **target** is where the program runs: native Linux, macOS or Windows, WebAssembly, DOS, or UEFI
  with no operating system.

## The pages

- [Zero mode](zero-mode.html): what zero mode is, what bflat and the fork are, and how the core
  library `zerocore/` differs from the library `lib/`.
- [Memory without a GC](memory.html): why `new` never frees, how arenas give memory a lifetime, and the
  tools that catch arena mistakes.
- [Strings without a GC](strings.html): what the core library's `string` can do, how spans and `Console`
  avoid allocating, and where strings that must be kept come from.
- [The memory safety rules](safety-rules.html): C# 15's updated memory safety rules, and how `lib/`
  uses `unsafe` to mark the operations that can leave pointers dangling.
- [Targets](targets.html): how one source becomes a native Linux program, a WebAssembly module, a
  DOS program for a 386 and a UEFI program.
- [Limits](limits.html): what zero mode supports, what it doesn't, and the known issues with their
  workarounds.

Read them in order, or, if you read only one, read [Memory without a GC](memory.html).
