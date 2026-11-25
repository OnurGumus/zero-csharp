---
title: Catch arena mistakes
index: 9
---

# Catch arena mistakes

After an arena frees memory, an old pointer into it still reads plausible old data, and by default
nothing reports that. Zero mode has no exceptions, so when `lib/` does catch a mistake it can only stop
the program with a message. This guide turns on the checks, names arenas in the messages, and lists
what the library stops the program for.

## Turn on `Arena.DebugChecks`

Set it first thing in `Main`:

```csharp no-sample
Arena.DebugChecks = true;
```

While it is on:

- **Freed memory is poisoned.** Arenas created after this line fill memory with `0xDD` bytes when it
  is freed (`Restore`, `Reset`, `Destroy`) and when a block is first obtained. A stale pointer then
  reads `0xDDDDDDDD` instead of the old value, and so does memory allocated with `zero: false` and never
  written. Arenas created before the line aren't poisoned, which is why it goes first.
- **Collections check themselves.** `List`, `Dict`, `StrBuilder`, `StrDict`, and `RefList` and
  `RefDict` through the collections under them, check on every use that their memory is still
  allocated, and stop the program if it isn't. `List`, `Dict` and `StrBuilder` check their whole
  buffer; `StrDict` checks only that its table's start is still allocated.

Here a list is used after its arena was reset:

<!-- sample: samples/Guards.cs list-after-reset -->
```csharp
Arena.DebugChecks = true;
Arena arena = Arena.Create(256);
List<int> list = List<int>.Create(arena);
list.Add(1);
unsafe { arena.Reset(); }
list.Add(2);
```

```text
Arena.DebugChecks: collection used after its arena was reset or restored
```

The checks cost time: a fill on every free and a check on every collection operation. Leave them off
in release builds.

## Name your arenas

`Arena.Create(size, name)` gives an arena a name, and the messages from `Restore`, from running out of
memory and from the freed-buffer checks start with that name. (Two don't: a collection whose own header
was freed, and a list index out of range.)
In a program with several arenas, that tells you which one to look at. The arena keeps a reference to
the name, so pass a string that outlives it, such as a literal.

Here the checks catch the `Restore` hazard: an older collection grows inside a scope, its new memory
is freed when the scope ends, and the collection still points at it. The list is the arena's newest
allocation, so it grows in place, past the scope's mark:

<!-- sample: samples/Guards.cs grew-in-place -->
```csharp
Arena.DebugChecks = true;
Arena arena = Arena.Create(256, "in place");
List<int> list = List<int>.Create(arena, 4);

Arena.Scope temp;
unsafe { temp = arena.OpenScope(); }
using (temp)
{
    // The list is still the arena's newest allocation, so its buffer
    // is made longer where it is: it starts before the scope's mark
    // and now ends after it.
    for (int i = 0; i < 10; i++)
        list.Add(i);
}
list.Get(0);
```

```text
Arena "in place":
Arena.DebugChecks: collection's buffer was freed (it grew after Save() and was used after Restore(), or its arena was reset)
```

Without `DebugChecks`, nothing reports this. The list keeps reading memory that later allocations
overwrite. The fix is to add only to collections created after the mark, or to do the temporary work
in `Arena.Scratch`, whose memory is separate from your arenas, and copy the result out.

## What the library stops the program for

[`samples/Guards.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/Guards.cs) makes one mistake per run, and the repository's `test.sh`
checks the message of each against `tests/expected/Guards.fail`. Run one with the case as its
argument: `bin/Debug/zero03/Guards <case>` in a zero project, or `./run.sh Guards <case>` in the
repository:

| Case | The mistake | Needs `DebugChecks` |
|---|---|---|
| `restore-order` | an older mark restored before a newer one | no |
| `restore-other-arena` | a mark restored in another arena | no |
| `restore-after-reset` | a mark from before a `Reset()` | no |
| `list-after-reset` | a list used after its arena was reset | yes |
| `list-grew-in-scope` | an older list that grew into a new buffer inside a scope, used after it | yes |
| `list-grew-in-place` | the same, grown in place | yes |
| `builder-grew-in-place` | the same for a `StrBuilder` | yes |
| `list-index` | a list index out of range | no |
| `builder-index` | a `StrBuilder` index out of range | no |
| `out-of-memory` | an allocation the system can't satisfy, in a `stopOnOutOfMemory` arena | no |

`Guards.fail` holds each case's message. A named arena's name goes to standard output, the message to
standard error (both to standard output on DOS), and the program aborts with a non-zero exit code.

## Use `OpenScope` instead of `Save` and `Restore`

Marks must be restored newest first. Restoring an older mark ends every mark saved after it, and
restoring such an ended mark stops the program:

<!-- sample: samples/Guards.cs restore-order -->
```csharp
Arena arena = Arena.Create(256, "order");
Arena.Mark outer = arena.Save();
arena.AllocArray<byte>(64);
Arena.Mark inner = arena.Save();
arena.AllocArray<byte>(64);
unsafe
{
    arena.Restore(outer); // also ends `inner`
    arena.Restore(inner);
}
```

That check can miss an ended mark once enough has been allocated again to cover its position.
`OpenScope()` rules the mistake out: it is `Save()` and `Restore()` as a `using` block, and leaving the
block by any path, early returns included, restores the mark. The `grew-in-place` excerpt above shows
the pattern. `OpenScope` is `unsafe` because everything allocated inside the block dangles after it.

## Stop instead of checking for null

By default a failed allocation returns null, and `Add` or `Set` on a collection returns false. An arena
created with `stopOnOutOfMemory: true` ends the program with a message instead, so nothing built on it
has to check:

<!-- sample: samples/Guards.cs out-of-memory -->
```csharp
Arena arena = Arena.Create(256, "small", stopOnOutOfMemory: true);
// Half the address space: no system has that much to give.
arena.Alloc(~(nuint)0 / 2);
```

Use it wherever the program couldn't carry on anyway. It matters most on DOS and WebAssembly, where
memory really does run out. Requests that are wrong in themselves, such as a bad alignment or a
negative count, still return null.

## See where the memory goes

`arena.Dump()` prints the arena's name, how full it is, and its blocks:

```text
Arena "frame": 920 of 1152 bytes used (peak 920 of 1152), 3 blocks
  block 1: 128 bytes
  block 2: 256 bytes
  block 3: 768 bytes, 88 free (current)
```

Many small blocks mean the first block is too small for the workload. `arena.PeakUsed` is the most the
arena ever held at once: run the workload, read it, and pass that as the first block's size so the
arena never has to grow.

## What the checks can't catch

- **Memory handed out again.** Once freed memory has been allocated anew, a stale pointer into it looks
  valid.
- **Raw pointers and strings.** Only collections check themselves. A stale pointer or string reads
  `0xDD` bytes under `DebugChecks`, which makes the bug visible, but nothing stops the program.

See also: [Memory and arenas](../concepts/memory.html), [Safety rules](../concepts/safety-rules.html),
and the [`Arena` reference](../reference/arena.html).
