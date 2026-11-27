// Example: Ptr<T> (lib/Ptr.cs), pointers between items in an arena, for the
// structures a struct can't hold by itself: a doubly linked list, which is
// full of cycles, and a tree. Then Pool<T> and Handle<T> (lib/Pool.cs), for
// items removed one by one, handed over with Move, and kept from a
// ScratchPool, and LinkedList<T> (lib/LinkedList.cs), which is built on them.
// Exit code = number of failed checks.
// Build: ./build.sh Links
//
// No unsafe anywhere: Ptr.Value gives a ref to the item, a null Ptr stops
// the program when used, and the items live until the arena is reset.

using System;

namespace LinksSample;

// docs:node
// One item of a doubly linked list: a struct can't contain itself, but it
// can hold pointers to others of its kind.
public struct Node
{
    public int Number;
    public Ptr<Node> Prev;
    public Ptr<Node> Next;
}
// docs:end

// docs:chain
// The list itself: its two ends, and the arena its items live in.
public struct Chain
{
    public Arena Arena;
    public Ptr<Node> First;
    public Ptr<Node> Last;

    public void AddLast(int number)
    {
        Ptr<Node> node = Arena.New(new Node { Number = number, Prev = Last });
        if (Last.IsNull)
            First = node;
        else
            Last.Value.Next = node;
        Last = node;
    }

    // Takes the node out by joining its neighbours to each other.
    public void Remove(Ptr<Node> node)
    {
        ref Node n = ref node.Value;
        if (n.Prev.IsNull)
            First = n.Next;
        else
            n.Prev.Value.Next = n.Next;
        if (n.Next.IsNull)
            Last = n.Prev;
        else
            n.Next.Value.Prev = n.Prev;
        n.Prev = default;
        n.Next = default;
    }
}
// docs:end

// docs:tree
public struct TreeNode
{
    public int Key;
    public Ptr<TreeNode> Left;
    public Ptr<TreeNode> Right;
}
// docs:end

// docs:task
// A task that waits for another: items in a pool point at each other with
// handles, which are plain structs too.
public struct Job
{
    public int Id;
    public Handle<Job> WaitsFor;
}
// docs:end

// docs:map
// A map: towns joined by roads, in two pools. A town holds a handle to the
// first of its roads and each road one to the next, so a town has as many
// roads as it needs, and every link is a plain struct.
public struct Town
{
    public int Id;
    public Handle<Road> FirstRoad;
}

public struct Road
{
    public Handle<Town> To;
    public int Km;
    public Handle<Road> Next;
}
// docs:end

public struct Map
{
    // docs:map-join
    public Pool<Town> Towns;
    public Pool<Road> Roads;

    public static Map Create(Arena arena) => new Map
    {
        Towns = Pool<Town>.Create(arena),
        Roads = Pool<Road>.Create(arena),
    };

    public Handle<Town> AddTown(int id) => Towns.Add(new Town { Id = id });

    // A road both ways.
    public void Join(Handle<Town> a, Handle<Town> b, int km)
    {
        OneWay(a, b, km);
        OneWay(b, a, km);
    }

    // The new road goes in front of the town's chain of roads.
    void OneWay(Handle<Town> from, Handle<Town> to, int km)
    {
        ref Town town = ref Towns[from];
        town.FirstRoad = Roads.Add(
            new Road { To = to, Km = km, Next = town.FirstRoad });
    }
    // docs:end

    // docs:map-remove
    // The town and its own roads go. Roads to it from other towns stay,
    // but their handle no longer finds it.
    public void RemoveTown(Handle<Town> town)
    {
        Handle<Road> road = Towns[town].FirstRoad;
        while (!road.IsNull)
        {
            Handle<Road> next = Roads[road].Next;
            Roads.Remove(road);
            road = next;
        }
        Towns.Remove(town);
    }
    // docs:end

    // docs:map-prune
    // Takes out the roads to removed towns, and returns how many. Each is
    // unlinked from its town's chain first, so no chain leads to a road
    // that is gone.
    public int Prune()
    {
        int pruned = 0;
        foreach (Handle<Town> town in Towns)
        {
            ref Handle<Road> link = ref Towns[town].FirstRoad;
            while (!link.IsNull)
            {
                Handle<Road> road = link;
                if (Towns.Contains(Roads[road].To))
                {
                    link = ref Roads[road].Next;
                    continue;
                }
                link = Roads[road].Next;    // the chain skips it now
                Roads.Remove(road);
                pruned++;
            }
        }
        return pruned;
    }
    // docs:end
}

public struct Trip
{
    public Map Map;
    public Handle<Town> From;
    public Handle<Town> To;
}

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void PoolBasics(Arena arena)
    {
        // docs:pool-basics
        var jobs = Pool<Job>.Create(arena);
        Handle<Job> build = jobs.Add(new Job { Id = 1 });
        jobs[build].Id = 10;          // a ref: changes the job in place
        jobs.Remove(build);           // its slot is free again
        Handle<Job> lint = jobs.Add(new Job { Id = 5 });  // reuses it
        bool found = jobs.Contains(build);    // false: build is stale
        // jobs[build] here would stop the program.
        // docs:end
        Check("a stale handle misses the item in its old slot",
            !found && jobs[lint].Id == 5 && jobs.Count == 1 && lint != build);
    }

    // docs:distance
    // The shortest way by road, in km, or -1 if there is none. The search's
    // own tables are scratch memory, gone when it returns.
    static int Distance(Map map, Handle<Town> from, Handle<Town> to)
    {
        var trip = new Trip { Map = map, From = from, To = to };
        return Arena.Scratch(ref trip,
            static (scoped ScratchScope scratch, scoped ref Trip trip) =>
            {
                var best = scratch.Dict<Handle<Town>, int>();
                var done = scratch.HashSet<Handle<Town>>();
                best.Set(trip.From, 0);
                while (true)
                {
                    // The nearest town not done yet (a scan, to keep it short).
                    Handle<Town> here = default;
                    int km = int.MaxValue;
                    foreach (var entry in best)
                        if (!done.Contains(entry.Key) && entry.Value < km)
                        {
                            here = entry.Key;
                            km = entry.Value;
                        }
                    if (here.IsNull)
                        return -1;
                    if (here == trip.To)
                        return km;
                    done.Add(here);

                    Handle<Road> road = trip.Map.Towns[here].FirstRoad;
                    for (; !road.IsNull; road = trip.Map.Roads[road].Next)
                    {
                        Road r = trip.Map.Roads[road];
                        if (!trip.Map.Towns.Contains(r.To))
                            continue;           // a road to a removed town
                        int via = km + r.Km;
                        if (!best.TryGet(r.To, out int known) || via < known)
                            best.Set(r.To, via);
                    }
                }
            });
    }
    // docs:end

    static void Section(string section)
    {
        Console.WriteLine($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    // The numbers front to back, then back to front, as one number each.
    static int Forward(Chain chain)
    {
        int digits = 0;
        for (Ptr<Node> p = chain.First; !p.IsNull; p = p.Value.Next)
            digits = digits * 10 + p.Value.Number;
        return digits;
    }

    static int Backward(Chain chain)
    {
        int digits = 0;
        for (Ptr<Node> p = chain.Last; !p.IsNull; p = p.Value.Prev)
            digits = digits * 10 + p.Value.Number;
        return digits;
    }

    // docs:insert
    // Recursion works as it would with classes.
    static Ptr<TreeNode> Insert(Arena arena, Ptr<TreeNode> node, int key)
    {
        if (node.IsNull)
            return arena.New(new TreeNode { Key = key });
        ref TreeNode n = ref node.Value;
        if (key < n.Key)
            n.Left = Insert(arena, n.Left, key);
        else
            n.Right = Insert(arena, n.Right, key);
        return node;
    }
    // docs:end

    // The keys in order, as digits of one number.
    static long InOrder(Ptr<TreeNode> node, long digits)
    {
        if (node.IsNull)
            return digits;
        digits = InOrder(node.Value.Left, digits);
        digits = digits * 10 + node.Value.Key;
        return InOrder(node.Value.Right, digits);
    }

    // The values as pairs of digits, first to last.
    static int Digits(LinkedList<int> list)
    {
        int digits = 0;
        foreach (int item in list)
            digits = digits * 100 + item;
        return digits;
    }

    static int Height(Ptr<TreeNode> node)
        => node.IsNull ? 0 : 1 + Math.Max(Height(node.Value.Left), Height(node.Value.Right));

    public static int Main()
    {
        Arena arena = Arena.Create(4096);

        // docs:use
        var chain = new Chain { Arena = arena };
        for (int i = 1; i <= 5; i++)
            chain.AddLast(i);
        chain.Remove(chain.First.Value.Next);   // takes out the 2
        // docs:end
        Check("walks forward", Forward(chain) == 1345);
        Check("walks backward", Backward(chain) == 5431);
        chain.Remove(chain.First);
        chain.Remove(chain.Last);
        Check("ends removed", Forward(chain) == 34 && chain.First.Value.Prev.IsNull);
        Section("a doubly linked list");

        // A ring: the last node points back at the first.
        chain.Last.Value.Next = chain.First;
        chain.First.Value.Prev = chain.Last;
        int steps = 0;
        int sum = 0;
        Ptr<Node> p = chain.First;
        do
        {
            sum += p.Value.Number;
            p = p.Value.Next;
            steps++;
        } while (p != chain.First && steps < 10);
        Check("goes round once", steps == 2 && sum == 7);
        Check("same node, same Ptr", chain.First.Value.Next.Value.Next == chain.First);
        Section("a ring");

        Ptr<TreeNode> root = default;
        foreach (int key in new[] { 5, 3, 8, 1, 4, 7, 9 })
            root = Insert(arena, root, key);
        Check("keys in order", InOrder(root, 0) == 1345789);
        Check("balanced as inserted", Height(root) == 3);
        Section("a tree");

        var zeroed = arena.New<Node>();
        Check("New<T>() gives a zeroed item", zeroed.Value.Number == 0 && zeroed.Value.Next.IsNull);
        Ptr<Node> none = default;
        Check("default is null", none.IsNull && none == default(Ptr<Node>) && none != chain.First);
        Arena.DebugChecks = true;
        Arena checkedArena = Arena.Create(256);
        Ptr<int> counter = checkedArena.New(41);
        counter.Value++;
        Check("DebugChecks lets a live item through", counter.Value == 42);
        Arena.DebugChecks = false;
        Section("Ptr itself");

        PoolBasics(arena);
        Section("pool basics");

        // docs:pool
        var jobs = Pool<Job>.Create(arena);
        Handle<Job> build = jobs.Add(new Job { Id = 1 });
        Handle<Job> test = jobs.Add(new Job { Id = 2, WaitsFor = build });
        Handle<Job> ship = jobs.Add(new Job { Id = 3, WaitsFor = test });
        jobs.Remove(test);
        Handle<Job> docs = jobs.Add(new Job { Id = 4 });  // test's old slot
        // ship.WaitsFor is test's handle: it finds nothing, not docs.
        bool waiting = jobs.Contains(jobs[ship].WaitsFor);   // false
        // docs:end
        Check("a removed item is refused", !waiting && !jobs.TryGet(test, out _) && jobs.Count == 3);
        Check("its slot was reused", docs != test && jobs[docs].Id == 4);
        jobs[build].Id += 10;
        int ids = 0;
        foreach (Handle<Job> job in jobs)
            ids += jobs[job].Id;
        Check("change in place, walk the handles", ids == 11 + 3 + 4);
        var numbers = Pool<int>.Create(arena, 4);
        for (int i = 1; i <= 20; i++)
            numbers.Add(i);
        // docs:remove-while-walking
        foreach (Handle<int> number in numbers)
            if (numbers[number] % 2 == 1)
                numbers.Remove(number);      // allowed while walking
        // docs:end
        int evens = 0;
        foreach (Handle<int> number in numbers)
            evens += numbers[number];
        Check("Remove while walking", numbers.Count == 10 && evens == 110);
        var growing = Pool<int>.Create(arena, 8);
        Handle<int> firstNumber = growing.Add(1);
        ref int kept = ref growing[firstNumber];
        for (int i = 0; i < 100; i++)
            growing.Add(i);
        kept = 42;
        Check("items don't move as the pool grows", growing[firstNumber] == 42 && growing.Count == 101);
        growing.Clear();
        Check("Clear refuses every handle", growing.Count == 0 && !growing.Contains(firstNumber));
        Section("a pool");

        // docs:move
        Handle<Job> mine = jobs.Add(new Job { Id = 7 });
        Handle<Job> copy = mine;
        Handle<Job> theirs = jobs.Move(ref mine);
        // mine is now null, and every copy of it is refused: only the new
        // owner reaches the job.
        // docs:end
        Check("Move: the new handle finds the item", mine.IsNull && jobs[theirs].Id == 7);
        Check("Move: old copies are refused", !jobs.Contains(copy));
        var done = Pool<Job>.Create(arena);
        Handle<Job> finished = jobs.MoveTo(done, ref theirs);
        Check("MoveTo: into the other pool", theirs.IsNull && done[finished].Id == 7 && done.Count == 1);
        Check("MoveTo: gone from the first", jobs.Count == 3);
        Check("another pool's handles are refused", !done.Contains(build) && !jobs.Contains(finished));
        Section("moving");

        // docs:scratch-pool
        // Work in a scratch pool, then move the one result to keep into a
        // pool of yours; the rest goes with the scratch memory.
        var results = Pool<Job>.Create(arena);
        Handle<Job> best = Arena.Scratch(ref results,
            static (scoped ScratchScope scratch, scoped ref Pool<Job> keep) =>
            {
                ScratchPool<Job> tries = scratch.Pool<Job>();
                Handle<Job> top = default;
                for (int id = 1; id <= 20; id++)
                {
                    Handle<Job> next = tries.Add(new Job { Id = id * 7 % 20 });
                    if (top.IsNull || tries[next].Id > tries[top].Id)
                        top = next;
                }
                return tries.MoveTo(keep, ref top);
            });
        // docs:end
        Check("the result was kept", results.Count == 1 && results[best].Id == 19);
        Section("a scratch pool");

        // docs:linked-list
        var list = LinkedList<int>.Create(arena);
        var two = list.AddLast(2);
        list.AddFirst(1);
        var four = list.AddAfter(two, 4);
        list.AddBefore(four, 3);
        list.AddLast(5);
        list.Remove(two);
        foreach (ref int item in list)
            item *= 10;                         // 10, 30, 40, 50
        // docs:end
        Check("Add and Remove", Digits(list) == 10304050 && list.Count == 4);
        Check("a removed node is refused", !list.Remove(two) && !list.Contains(two));
        // docs:linked-list-walk
        // Each node's Next is the handle of the one after it; the last
        // node's Next is the null handle, which ends the walk.
        int total = 0;
        for (var node = list.First; !node.IsNull; node = list[node].Next)
            total += list[node].Value;          // 10 + 30 + 40 + 50
        // docs:end
        Check("walks forward along Next", total == 130);
        int backward = 0;
        for (var node = list.Last; !node.IsNull; node = list[node].Previous)
            backward = backward * 100 + list[node].Value;
        Check("walks backward", backward == 50403010);
        Check("Find", list[list.Find(40)].Value == 40 && list.Find(20).IsNull && list.Contains(30));
        list.RemoveFirst();
        list.RemoveLast();
        Check("RemoveFirst, RemoveLast", Digits(list) == 3040 && list[list.First].Next == list.Last);
        list.Clear();
        Check("Clear", list.Count == 0 && list.First.IsNull && !list.Contains(four));
        Section("LinkedList");

        // docs:map-use
        Map map = Map.Create(arena);
        Handle<Town> harbor = map.AddTown(1), mill = map.AddTown(2);
        Handle<Town> ford = map.AddTown(3), hill = map.AddTown(4);
        Handle<Town> lake = map.AddTown(5);
        map.Join(harbor, mill, 230);
        map.Join(mill, ford, 150);
        map.Join(ford, lake, 150);
        map.Join(mill, lake, 330);
        map.Join(harbor, hill, 260);
        int before = Distance(map, harbor, lake);   // 530, through ford
        map.RemoveTown(ford);
        int after = Distance(map, harbor, lake);    // 560, mill to lake
        // docs:end
        Check("the shortest way", before == 530);
        Check("around a removed town", after == 560);
        // Ten one-way roads; ford's own two went with it, the two to it stay.
        Check("a removed town's roads went with it", map.Towns.Count == 4 && map.Roads.Count == 8);
        Check("a road to a removed town is skipped", Distance(map, mill, ford) == -1);
        Check("Prune takes out the two left", map.Prune() == 2 && map.Roads.Count == 6 && map.Prune() == 0);
        Check("the way is the same after Prune", Distance(map, harbor, lake) == 560);
        Handle<Town> island = map.AddTown(6);
        Check("its slot went to a new town", map.Towns[island].Id == 6 && !map.Towns.Contains(ford));
        Check("no way there", Distance(map, harbor, island) == -1);
        Check("from a town to itself", Distance(map, hill, hill) == 0);
        map.RemoveTown(mill);
        Check("cut off", Distance(map, harbor, lake) == -1 && Distance(map, harbor, hill) == 260);
        Check("Prune after another removal", map.Prune() == 2 && map.Roads.Count == 2);
        Section("a map");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
