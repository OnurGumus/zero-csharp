---
title: The memory safety rules
index: 4
---

# The memory safety rules

Zero-mode code uses pointers more than ordinary C# does: arenas hand out pointers, collections keep
them, and C functions take them. The samples and `lib/` compile under C# 15's
[updated memory safety model](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code#the-updated-memory-safety-model-preview),
which is in preview. This page explains what those rules say and how this repository uses them to
mark the places where memory can go wrong.

## Turning the rules on

The rules are a compiler feature. The bflat fork adds a `--compiler-feature` option, the same as
`csc -features:`, and `build.sh` passes it, with the preview language version, to every build of
`lib/` and the samples:

```bash
CSHARP_FLAGS=(--langversion preview --compiler-feature updated-memory-safety-rules)
```

zerocore is built with `--langversion preview` only. The editor projects turn the rules on for every
project (`Directory.Build.props`), so VS Code flags the same memory-safety errors in `lib/` and the
samples that bflat does.

## The three rules

**Pointers are safe to create, unsafe to use.** Pointer types, `&`, `stackalloc` and `fixed` work in
safe code. Dereferencing (`*p`, `p->x`, `p[i]`) and calling function pointers need an `unsafe { }`
block. So the `unsafe` block sits on the line that uses a pointer, not on everything that touches
one.

**`unsafe` on a member is a contract.** A member marked `unsafe` can only be called from an unsafe
context, so every caller has to opt in. Marking a member doesn't make its body unsafe: the body still
needs inner `unsafe { }` blocks where it dereferences.

**Every `extern` says `safe` or `unsafe`.** A P/Invoke declaration states whether calling it can break
memory safety. In `lib/Arena.cs`, for example:

```csharp no-sample
static safe extern void* malloc(nuint size);
static unsafe extern void free(void* ptr);
```

`malloc` only returns a pointer, which is harmless until it is used. `free` can leave every pointer
into that memory dangling, so its callers must opt in.

## Allocating is safe, freeing is not

`lib/Arena.cs` follows the same reasoning as `malloc` and `free`. Allocating from an arena is safe: you
get a pointer, and using that pointer needs `unsafe` anyway. Reading and writing through it is where
the unsafe blocks go:

<!-- sample: samples/Arena.cs alloc-and-use -->
```csharp
Entity* e = arena.Alloc<Entity>();
int* squares = arena.AllocArray<int>(10);
unsafe
{
    e->Id = 1;
    e->Position.X = 100;
    for (int i = 0; i < 10; i++)
        squares[i] = i * i;
    Console.WriteLine($"squares[5] = {squares[5]}");
}
```

The dangerous operations are the ones that free memory: `Restore()`, `Reset()`, `Destroy()` and
`OpenScope()`. Calling one of them is what turns other pointers, spans and collections into dangling
ones, so they are marked `unsafe`. Nothing else can check that promise: an arena doesn't know who still
holds a pointer into it, and by default a stale pointer still reads memory that looks valid (see
[Memory without a GC](memory.html)).

So the `unsafe` keyword in this code base points at two kinds of line: those that dereference a
pointer, and those that free memory something else may still point at. A search for `unsafe` finds
every place that needs a second look.

## SAFETY comments

An `unsafe` block says that something here can go wrong. It doesn't say why it doesn't. The
repository's convention is to write that down: an `unsafe` block that relies on a promise carries a
`// SAFETY:` comment with the reason it is correct.

<!-- sample: samples/Strings.cs restore -->
```csharp
// Temporary strings: free them with Restore and keep the rest.
nuint usedBefore = s_arena.Used;
Arena.Mark mark = s_arena.Save();
bool temporaryOk;
{   // temporary can't be named after this block, so not after the Restore
    string temporary = Str.Format(s_arena, $"{name} has {name.Length} letters");
    temporaryOk = temporary.EndsWith("3 letters");
}
unsafe { s_arena.Restore(mark); } // SAFETY: nothing from the block was kept.
```

The comment states what the caller promises, here that nothing made inside the block outlives the
restore. Part of that promise the compiler checks: `temporary` is declared in a plain block that ends
before `Restore`, so naming it afterwards is a compile error. What it can't check is a copy kept
elsewhere, such as `temporary` stored in a field from inside the block, and the comment covers that. A
reviewer checks the comment against the code below it instead of reasoning about the whole function.
`lib/` does the same inside its own implementation ("a live header", "CheckIndex validated header and
index"), and where one argument covers many blocks, a single comment says so once.

The same idea applies to members. Each `unsafe` member in `lib/` documents its contract in a
`<safety>` section of its doc comment. `Arena.Restore` says that every pointer, span and collection
allocated since the mark becomes dangling, including the buffer of an older collection that grew after
the mark. The [Arena reference](../reference/arena.html) shows those sections.

F# has no `unsafe` block. An F# function that calls an `unsafe` member is marked with
`[<Safety "...">]` and the reason instead, and the build warns where the mark is missing
([Write a program in F#](../how-to/write-fsharp.html#say-why-an-unsafe-call-is-safe)).

## What the rules don't do

The rules mark where memory can go wrong; they don't check it. A `SAFETY` comment is a claim, and a
wrong claim compiles. Two tools in `lib/` catch some wrong claims at run time:

- `Arena.Restore` ends the program for a mark from another arena or from before a `Reset()`, and for
  most marks restored out of order. `OpenScope()` rules out the ordering mistake altogether.
- `Arena.DebugChecks` makes freed memory read as `0xDD` bytes and makes collections stop the program
  when used after their memory was freed.

[Catch arena mistakes](../how-to/catch-arena-mistakes.html) shows both. Neither catches a stale
pointer into memory that has since been handed out again, so the comment remains the main defense.
