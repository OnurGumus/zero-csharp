// Example: Generic List<T> and Dict<K,V> (lib/List.cs, lib/Dict.cs) in --stdlib:zero,
// and their versions for references: RefList<T>, StrDict<TValue> (string keys
// compared by text) and RefDict<TKey, TValue> (lib/RefList.cs, StrDict.cs, RefDict.cs).
// Build: ./build.sh Collections
//
// All of them take their memory from the Arena. List and Dict work with any
// unmanaged element type.

using System;
using System.Collections.Generic;

namespace CollectionsSample;

public struct Point
{
    public int X;
    public int Y;
}

// docs:descending
// A comparer as a struct: Sort is compiled for it; Compare is a direct call.
public struct Descending : IComparer<int>
{
    public int Compare(int a, int b) => b.CompareTo(a);
}
// docs:end

// docs:modulo
// A comparer with state: keys that leave the same remainder are one key.
public struct Modulo : IEqualityComparer<int>
{
    public int By;
    public bool Equals(int a, int b) => a % By == b % By;
    public int GetHashCode(int value) => value % By;
}
// docs:end

// The 500-key check's map and what it found, passed to Arena.Scratch by ref.
public struct SquareWork
{
    public StrDict<long> Map;
    public int Removed;
    public int OddFound;
}

public static class Program
{
    public static int Main()
    {
        Arena arena = Arena.Create(64 * 1024);
        if (!arena.IsCreated)
            return 1;

        Console.WriteLine("=== Collections Demo ===");
        Console.WriteLine();

        Console.WriteLine("--- List<int> ---");
        var numbers = List<int>.Create(arena, 0); // capacity is clamped to a minimum of 4
        for (int i = 1; i <= 10; i++)
            numbers.Add(i * 10);
        Console.WriteLine($"Count: {numbers.Count} | Capacity: {numbers.Capacity}");
        PrintList(numbers);

        numbers.RemoveAt(5);
        Console.WriteLine("After RemoveAt(5):");
        PrintList(numbers);

        // List is a handle: the callee's Adds are visible to the caller.
        AddMore(numbers, 3);
        Console.WriteLine($"After AddMore(numbers, 3) in another method, Count: {numbers.Count}");

        List<int> notCreated = default;
        Console.Write("Add on default(List) returns ");
        Console.Write(notCreated.Add(1));
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("--- List<Point> ---");
        var points = List<Point>.Create(arena);
        points.Add(new Point { X = 1, Y = 2 });
        points.Add(new Point { X = 10, Y = 20 });
        points.Add(new Point { X = 100, Y = 200 });
        for (int i = 0; i < points.Count; i++)
        {
            Point p = points.Get(i);
            Console.Write("[");
            Console.Write(i);
            Console.Write("] (");
            Console.Write(p.X);
            Console.Write(", ");
            Console.Write(p.Y);
            Console.WriteLine(")");
        }
        Console.WriteLine();

        Console.WriteLine("--- Dict<int, int> ---");
        // docs:dict
        var scores = Dict<int, int>.Create(arena);
        scores.Set(1, 100);
        scores.Set(2, 250);
        scores.Set(3, 175);
        scores.Set(-42, 999);
        // docs:end
        Console.WriteLine($"Count: {scores.Count}");

        if (scores.TryGet(2, out int value))
            Console.WriteLine($"scores[2] = {value}");
        if (scores.TryGet(-42, out value))
            Console.WriteLine($"scores[-42] = {value}");
        if (!scores.TryGet(99, out value))
            Console.WriteLine("scores[99] not found");

        // Growth: 1000 inserts into a map created with capacity 8.
        var squares = Dict<int, long>.Create(arena, 8);
        for (int i = 0; i < 1000; i++)
            squares.Set(i, (long)i * i);
        int found = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (squares.TryGet(i, out long sq) && sq == (long)i * i)
                found++;
        }
        Console.WriteLine($"1000 inserts after growth, found: {found}");

        Dict<int, int> notCreatedMap = default;
        Console.Write("Lookup in default(Dict) misses instead of crashing: ");
        Console.Write(!notCreatedMap.ContainsKey(1));
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("--- Dict<Point, int> ---");
        var pointIds = Dict<Point, int>.Create(arena);
        pointIds.Set(new Point { X = 0, Y = 0 }, 1);
        pointIds.Set(new Point { X = 10, Y = 10 }, 2);
        pointIds.Set(new Point { X = 5, Y = 5 }, 3);
        if (pointIds.TryGet(new Point { X = 10, Y = 10 }, out value))
            Console.WriteLine($"ID for (10, 10): {value}");

        Console.WriteLine();

        Console.WriteLine("--- Sort, Remove, foreach ---");
        var unsorted = List<int>.Create(arena);
        int[] values = { 42, 7, 19, 3, 88, 7, 61 };
        foreach (int v in values)
            unsorted.Add(v);
        unsorted.Sort(&Ascending); // any comparison: pass a static method's address
        Console.Write("Ascending:  ");
        PrintList(unsorted);
        unsorted.Sort(&ByLastDigit);
        Console.Write("By last digit, then value: ");
        PrintList(unsorted);
        // docs:sort-comparers
        unsorted.Sort();                  // by int's IComparable<int>
        Console.Write("Sort():     ");
        PrintList(unsorted);
        unsorted.Sort(new Descending());  // by a struct: an IComparer<int>
        Console.Write("Descending: ");
        PrintList(unsorted);

        Span<int> span = values;          // spans sort in place too
        span.Sort();
        Console.WriteLine($"BinarySearch(19): {span.BinarySearch(19)}, "
            + $"(20) would go at {~span.BinarySearch(20)}");
        // docs:end
        span.Reverse();
        Console.WriteLine($"Reversed: {span[0]} ... {span[^1]}");
        span.Sort((a, b) => a % 10 - b % 10);
        Console.WriteLine($"By a lambda, last digit first: {span[0]}, then {span[1]}");

        Console.WriteLine($"Remove(3): {scores.Remove(3)}, again: {scores.Remove(3)}, Count: {scores.Count}");
        long total = 0;
        int pairs = 0;
        foreach (var pair in scores) // no particular order
        {
            total += pair.Value;
            pairs++;
        }
        Console.WriteLine($"foreach over scores: {pairs} pairs, values sum to {total}");
        for (int i = 0; i < 1000; i += 2)
            squares.Remove(i);
        int odd = 0;
        foreach (var pair in squares)
        {
            if (pair.Key % 2 == 1 && pair.Value == (long)pair.Key * pair.Key)
                odd++;
        }
        Console.WriteLine($"After removing the 500 even keys: Count {squares.Count}, odd keys intact: {odd}");
        Console.WriteLine();

        // A separate arena, so the byte count printed at the end stays the same.
        Arena more = Arena.Create(64 * 1024);

        Console.WriteLine("--- Dict with a comparer ---");
        // docs:dict-comparer
        // As in .NET, 0.0 and -0.0 are one key; by raw bytes, they're two.
        var byValue = Dict<double, int>.Create(more);
        var byBytes =
            Dict<double, int, ByteEqualityComparer<double>>.Create(more);
        byBytes.Set(0.0, 1);
        byBytes.Set(-0.0, 2);
        byValue.Set(0.0, 1);
        byValue.Set(-0.0, 2);
        Console.WriteLine($"By bytes: {byBytes.Count} keys; by value: {byValue.Count}");

        var lastDigit =
            Dict<int, int, Modulo>.Create(more, comparer: new Modulo { By = 10 });
        lastDigit.Set(13, 1);
        lastDigit.Set(23, 2);  // the same key as 13
        lastDigit.TryGet(3, out int atThree);
        Console.WriteLine($"Modulo 10: {lastDigit.Count} key, [3] = {atThree}");
        // docs:end
        Console.WriteLine($"TryAdd: {lastDigit.TryAdd(4, 9)}, again: {lastDigit.TryAdd(14, 9)}");
        // Point implements nothing: compared by its bytes, without boxing.
        long allocated = GC.GetTotalAllocatedBytes();
        int hits = 0;
        for (int i = 0; i < 100; i++)
            if (pointIds.TryGet(new Point { X = 10, Y = 10 }, out _))
                hits++;
        Console.WriteLine($"100 lookups by a struct key: {hits} found, {GC.GetTotalAllocatedBytes() - allocated} bytes allocated");
        Console.WriteLine();

        Console.WriteLine("--- HashSet<int> ---");
        // docs:hashset
        var seen = HashSet<int>.Create(more);
        int duplicates = 0;
        foreach (int id in new[] { 3, 1, 4, 1, 5, 9, 2, 6, 5, 3, 5 })
        {
            if (!seen.Add(id))
                duplicates++;
        }
        Console.WriteLine($"{seen.Count} distinct, {duplicates} duplicates");

        var odds = HashSet<int>.Create(more);
        for (int i = 1; i < 10; i += 2)
            odds.Add(i);
        var both = seen.Clone(more);
        both.IntersectWith(odds);      // in seen and odds: 1, 3, 5, 9
        var evens = seen.Clone(more);
        evens.ExceptWith(odds);        // in seen, not odds: 2, 4, 6
        // docs:end
        Console.WriteLine($"Intersect: {both.Count} items summing to {Sum(both)}; except: {evens.Count} summing to {Sum(evens)}");
        odds.UnionWith(seen);
        Console.WriteLine($"Union: {odds.Count} items summing to {Sum(odds)}");
        var thousand = HashSet<int>.Create(more);
        var everyThird = HashSet<int>.Create(more);
        for (int i = 0; i < 1000; i++)
        {
            thousand.Add(i);
            if (i % 3 == 0)
                everyThird.Add(i);
        }
        thousand.IntersectWith(everyThird);
        int multiples = 0;
        foreach (int item in thousand)
            if (item % 3 == 0)
                multiples++;
        Console.WriteLine($"1000 intersected with every third: {thousand.Count} left, multiples of 3: {multiples}");
        evens.ExceptWith(evens);
        Console.WriteLine($"ExceptWith itself: {evens.Count}");
        Console.WriteLine();

        Console.WriteLine("--- Queue, Stack, PriorityQueue ---");
        // docs:queue
        var queue = Queue<int>.Create(more);
        for (int i = 1; i <= 40; i++)
            queue.Enqueue(i);
        int expected = 1;
        bool inOrder = true;
        for (int i = 0; i < 30; i++)   // past half: the rest move to the front
        {
            queue.TryDequeue(out int item);
            inOrder &= item == expected++;
        }
        for (int i = 41; i <= 50; i++)
            queue.Enqueue(i);
        queue.TryPeek(out int front);
        while (queue.TryDequeue(out int item))
            inOrder &= item == expected++;
        // docs:end
        Console.WriteLine($"Queue: front was {front}, 50 out in order: {inOrder && expected == 51}, empty: {!queue.TryDequeue(out _)}");

        var stack = Stack<int>.Create(more);
        for (int i = 1; i <= 5; i++)
            stack.Push(i);
        Console.Write("Stack, top first:");
        foreach (int item in stack)
            Console.Write($" {item}");
        stack.TryPop(out int top);
        stack.TryPeek(out int next);
        Console.WriteLine($"; popped {top}, then {next} on top");

        // docs:priority-queue
        var tasks = PriorityQueue<char, int>.Create(more);
        tasks.Enqueue('c', 3);
        tasks.Enqueue('a', 1);
        tasks.Enqueue('d', 4);
        tasks.Enqueue('b', 2);
        Console.Write("By priority:");
        while (tasks.TryDequeue(out char task, out int priority))
            Console.Write($" {task}{priority}");
        Console.WriteLine();
        // docs:end

        var largestFirst =
            PriorityQueue<int, int, Descending>.Create(more);
        uint seed = 12345;
        for (int i = 0; i < 200; i++)
        {
            seed = seed * 1664525 + 1013904223;
            int priority = (int)(seed >> 16) % 1000;
            largestFirst.Enqueue(i, priority);
        }
        int previous = int.MaxValue, dequeued = 0;
        bool descending = true;
        while (largestFirst.TryDequeue(out _, out int priority))
        {
            descending &= priority <= previous;
            previous = priority;
            dequeued++;
        }
        Console.WriteLine($"200 random priorities, largest first: {descending && dequeued == 200}");
        Console.WriteLine();

        // Collections are handles, so `var b = a` shares contents. Clone makes a
        // real copy, in any arena: here one that outlives the original's.
        // List<T> takes only unmanaged types; RefList<T> holds references.
        Console.WriteLine("--- RefList<string> ---");
        var names = RefList<string>.Create(arena, 0);
        string[] words = { "delta", "alpha", "charlie", "bravo" };
        foreach (string word in words)
            names.Add(Str.Format(arena, $"{word}:{word.Length}")); // strings built in the arena
        names.Add(null);
        names.Sort(&ByName);
        Console.Write("Sorted (null first): ");
        PrintNames(names);
        names.RemoveAt(0);
        names.Set(0, "ALPHA");
        Console.Write("RemoveAt(0), Set(0): ");
        PrintNames(names);
        var codes = RefList<string>.Create(arena, 0);
        codes.Add("b");
        codes.Add("C");
        codes.Add("a");
        codes.Sort(); // string's CompareTo: by UTF-16 code unit, so "C" first
        Console.Write("Sort(): ");
        PrintNames(codes);
        RefList<string> noNames = default;
        Console.WriteLine($"default(RefList): Count {noNames.Count}, Add returns {noNames.Add("x")}");
        Console.WriteLine();

        // Keys compared by their text; each new key is copied into the map.
        Console.WriteLine("--- StrDict ---");
        var counts = StrDict<int>.Create(arena, 0);
        Span<char> wordBuffer = stackalloc char[8]; // every word is read into this buffer
        // docs:word-counts
        foreach (ReadOnlySpan<char> w in "the cat and the hat and the bat".Split(' '))
        {
            for (int i = 0; i < w.Length; i++)
                wordBuffer[i] = w[i];
            ReadOnlySpan<char> key = wordBuffer.Slice(0, w.Length);
            counts.TryGet(key, out int n);
            counts.Set(key, n + 1);
        }
        // docs:end
        Console.WriteLine($"Word counts: {counts.Count} distinct, the = {Count(counts, "the")}, and = {Count(counts, "and")}, bat = {Count(counts, "bat")}, dog = {Count(counts, "dog")}");

        // Keys formatted into scratch text, cleared for each one: the map
        // copies each new key into its own arena.
        var squareWork = new SquareWork { Map = StrDict<long>.Create(arena, 0) };
        Arena.Scratch(ref squareWork, static (scoped ScratchScope scratch, scoped ref SquareWork work) =>
        {
            ScratchText key = scratch.Text();
            for (int i = 0; i < 500; i++)
            {
                key.Clear();
                key.Append($"key{i}");
                work.Map.Set(key.AsSpan(), (long)i * i);
            }
            for (int i = 0; i < 500; i += 2)
            {
                key.Clear();
                key.Append($"key{i}");
                if (work.Map.Remove(key.AsSpan()))
                    work.Removed++;
            }
            for (int i = 1; i < 500; i += 2)
            {
                key.Clear();
                key.Append($"key{i}");
                if (work.Map.TryGet(key.AsSpan(), out long square) && square == (long)i * i)
                    work.OddFound++;
            }
        });
        StrDict<long> squareMap = squareWork.Map;
        int removed = squareWork.Removed;
        int oddFound = squareWork.OddFound;
        long squareSum = 0;
        int squarePairs = 0;
        foreach (var pair in squareMap)
        {
            squareSum += pair.Value;
            squarePairs++;
        }
        Console.WriteLine($"500 keys, {removed} even ones removed: Count {squareMap.Count}, odd keys intact: {oddFound}, foreach: {squarePairs} pairs summing to {squareSum}");
        Console.WriteLine($"key0 gone: {!squareMap.ContainsKey("key0")}, key499 = {(squareMap.TryGet("key499", out long last) ? last : -1)}");

        // docs:labels
        var labels = StrDict<string>.Create(arena); // values can be references too
        labels.Set("ok", "fine");
        labels.Set("ok", Str.Format(arena, $"fine x{2}"));
        // docs:end
        Console.WriteLine($"StrDict<string>: ok = {(labels.TryGet("ok", out string label) ? label : "missing")}, Count {labels.Count}");
        StrDict<int> noCounts = default;
        Console.WriteLine($"default(StrDict): Count {noCounts.Count}, Set returns {noCounts.Set("x", 1)}, found: {noCounts.ContainsKey("x")}");
        Console.WriteLine();

        // Unmanaged keys, reference values.
        Console.WriteLine("--- RefDict<int, string> ---");
        // docs:refdict
        var byId = RefDict<int, string>.Create(arena);
        byId.Set(7, "seven");
        byId.Set(3, Str.Format(arena, $"three is {3}"));
        byId.Set(9, null);
        // docs:end
        bool nineFound = byId.TryGet(9, out string nine);
        bool fourFound = byId.TryGet(4, out string four);
        Console.WriteLine($"3 = {(byId.TryGet(3, out string three) ? three : "missing")}, 9 found: {nineFound} and null: {nine is null}, 4 found: {fourFound} and null: {four is null}");
        Console.WriteLine($"Remove(7): {byId.Remove(7)}, again: {byId.Remove(7)}, Count: {byId.Count}");
        int ids = 0;
        foreach (var pair in byId)
            ids += pair.Key;
        Console.WriteLine($"foreach: keys sum to {ids}");
        Console.WriteLine();

        Console.WriteLine("--- Clone ---");
        // docs:clone
        Arena keep = Arena.Create(4096);
        StrDict<int> countsCopy = counts.Clone(keep); // keys are copied into keep too
        List<int> snapshot = numbers.Clone(keep);
        Dict<int, int> scoresCopy = scores.Clone(keep);
        // docs:end
        numbers.Set(0, -1);
        scores.Set(2, 0);
        Console.WriteLine($"Original changed: numbers[0] = {numbers.Get(0)}, scores[2] = {(scores.TryGet(2, out value) ? value : -1)}");
        Console.WriteLine($"Clones unchanged: snapshot[0] = {snapshot.Get(0)}, scoresCopy[2] = {(scoresCopy.TryGet(2, out value) ? value : -1)}");
        Console.WriteLine($"Clone capacity fits its {snapshot.Count} items: {snapshot.Capacity}");
        Console.Write("Clone of default(List) is not created: ");
        Console.Write(!notCreated.Clone(keep).IsCreated);
        Console.WriteLine();

        Console.WriteLine();
        Console.WriteLine($"Arena used: {(long)arena.Used} bytes");

        unsafe { arena.Destroy(); } // SAFETY: only the clones, which live in `keep`, are used after this.

        snapshot.Add(1234); // the clone grows in its own arena
        countsCopy.Set("dog", 1);
        Console.WriteLine($"StrDict clone, keys copied: the = {Count(countsCopy, "the")}, dog = {Count(countsCopy, "dog")}, Count {countsCopy.Count}");
        Console.WriteLine($"After destroying the first arena, clones still work: {snapshot.Count} items, last = {snapshot.Get(snapshot.Count - 1)}, scoresCopy[-42] = {(scoresCopy.TryGet(-42, out value) ? value : -1)}");
        unsafe { keep.Destroy(); } // SAFETY: the clones are not used after this.
        Console.WriteLine("Done!");
        return 0;
    }

    static void AddMore(List<int> list, int n)
    {
        for (int i = 0; i < n; i++)
            list.Add(1000 + i);
    }

    static int Ascending(int a, int b) => a < b ? -1 : a > b ? 1 : 0;

    static int ByLastDigit(int a, int b) => a % 10 != b % 10 ? a % 10 - b % 10 : Ascending(a, b);

    static int ByName(string a, string b) => Str.Compare(a, b);

    static int Sum(HashSet<int> set)
    {
        int total = 0;
        foreach (int item in set)
            total += item;
        return total;
    }

    static int Count(StrDict<int> counts, string word) => counts.TryGet(word, out int n) ? n : 0;

    static void PrintNames(RefList<string> names)
    {
        Console.Write("[");
        bool first = true;
        foreach (string name in names)
        {
            if (!first)
                Console.Write(", ");
            Console.Write(name is null ? "null" : name);
            first = false;
        }
        Console.WriteLine($"] ({names.Count} items)");
    }

    static void PrintList(List<int> list)
    {
        Console.Write("[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0)
                Console.Write(", ");
            Console.Write(list.Get(i));
        }
        Console.WriteLine("]");
    }
}
