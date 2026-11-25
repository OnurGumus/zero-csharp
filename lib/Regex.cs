// Regex: regular expressions, compiled into an Arena, matched in linear time.
//
//   Regex date = Regex.Create(arena, @"(\d{4})-(\d\d)-(\d\d)");
//   RegexMatch m = date.Match(line);
//   if (m.Success) Console.WriteLine($"year: {m.Groups[1].Value}");
//   foreach (RegexMatch number in Regex.Create(arena, @"\d+").Matches(text)) ...
//   string swapped = date.Replace(arena, line, "$3/$2/$1");
//   foreach (ReadOnlySpan<char> field in Regex.Create(arena, @"\s*,\s*").Split(line)) ...
//
// A subset of .NET's syntax, with .NET's results where the two overlap:
// literals and escapes (\t \n \r \f \v \e \xHH \uHHHH, \ before punctuation);
// . and classes ([a-z], [^...], \d \w \s and their negations, ASCII only);
// anchors ^ $ \A \z \Z \b \B; capturing groups (...), named ones (?<name>...)
// and (?'name'...), numbered after the others as in .NET, and (?:...); |; and the
// quantifiers * + ? {n} {n,} {n,m}, greedy or lazy (with a ? after). Options:
// IgnoreCase (ASCII letters), Multiline (^ and $ at every line), Singleline
// (. matches \n too). Not supported, and refused with an error: backreferences
// (\1), lookaround ((?=...) and the like), numbered names ((?<2>...)), inline
// options, atomic groups. At most 15 capturing groups; names are ASCII
// letters, digits and _.
//
// The pattern is compiled into a small program that a Pike VM runs: every
// possible way of matching advances through the input together, one character
// at a time. So a match takes time proportional to the input's length times
// the program's, whatever the pattern: no pattern can make it backtrack for
// ages, as (a+)+b can in a backtracking engine. When several matches start at
// the same place, the one chosen is the one .NET's engine would find first.
//
// Replace takes .NET's substitutions: $1, ${1} and ${name} for a group, $$ for a $,
// $& for the match, $` and $' for the input before and after it, $+ for the
// last group and $_ for the whole input; anything else after a $ is kept as
// it is. It writes into an arena, a StrBuilder or a ScratchText you pass, or
// calls a function you give for each match. Split gives the pieces between
// matches as slices of the input, with the text of any groups in between, as
// .NET's Split does.
//
// Create allocates the program and the memory matching works in, in the arena
// you pass; matching and Split allocate nothing. That working memory is shared, so use
// a Regex from one thread at a time. An invalid pattern ends the program, as
// .NET throws ArgumentException; TryCreate returns the reason instead.

using System;
using System.Runtime.CompilerServices;

[Flags]
public enum RegexOptions
{
    None = 0,
    /// <summary>Letters match either case (ASCII letters only).</summary>
    IgnoreCase = 1,
    /// <summary>^ and $ match at the start and end of every line, not just of the input.</summary>
    Multiline = 2,
    /// <summary>. matches \n too.</summary>
    Singleline = 16,
}

public struct Regex
{
    internal const int MaxGroups = 16; // group 0, the whole match, and 15 more

    internal enum Op : byte
    {
        Char,           // C
        CharFold,       // C, a lowercase letter, either case
        Any,            // anything but \n
        AnyNewline,     // anything
        Class,          // ranges X .. X+Y, Negate
        Split,          // continue at X (preferred) and at Y
        Jump,           // continue at X
        Save,           // capture slot X = position
        LineStart, LineEnd, InputStart, InputEnd, InputEndOrFinalNewline, WordBoundary, NotWordBoundary,
        Match,
    }

    internal struct Inst
    {
        public Op Op;
        public bool Negate;
        public bool FoldCase;
        public char C;
        public int X, Y;
    }

    internal struct CharRange
    {
        public char Low, High;
    }

    struct Header
    {
        public Arena Arena;
        public Inst* Program;
        public int Length;
        public CharRange* Ranges;
        public int Groups;     // including group 0
        public RegexOptions Options;

        // Working memory for matching, Length entries each.
        public int* Marks;     // the generation a pc was last added to a list in
        public int Generation;
        public int* Pcs0, Pcs1;
        public int* Caps0, Caps1; // 2 * Groups slots per entry
        public int* Stack;     // 3 ints per entry, 4 * Length + 8 entries
        public int* Work;      // 2 * Groups slots
        public nint* Names;    // per group, its name (a string in Arena), or 0
    }

    Header* _header;

    /// <summary>
    /// Compiles <paramref name="pattern"/> into <paramref name="arena"/>. Ends the
    /// program with the reason if the pattern isn't valid (see TryCreate), or if
    /// the arena is full.
    /// </summary>
    public static Regex Create(Arena arena, ReadOnlySpan<char> pattern, RegexOptions options = RegexOptions.None)
    {
        if (!TryCreate(arena, pattern, options, out Regex regex, out string error, out int position))
        {
            if (position < 0)
                Environment.FailFast("Regex: " + error);
            Environment.FailFast("Regex: " + error + " at " + position.ToString() + " in the pattern");
        }
        return regex;
    }

    /// <summary>
    /// Compiles <paramref name="pattern"/> into <paramref name="arena"/>, or
    /// returns false with the reason and the position in the pattern it was
    /// found at (-1 for the pattern as a whole, or for a full arena).
    /// </summary>
    public static bool TryCreate(Arena arena, ReadOnlySpan<char> pattern, RegexOptions options,
        out Regex regex, out string error, out int errorPosition)
    {
        regex = default;
        // The syntax tree and the program as it's built go in an arena of their
        // own, freed at the end; only the finished program goes in the caller's.
        Arena scratch = Arena.Create(4096, "Regex.Create");
        var compiler = new RegexCompiler(scratch, pattern, options);
        bool ok = compiler.Run(out error, out errorPosition);
        if (ok)
        {
            regex = Store(arena, ref compiler);
            if (!regex.IsCreated)
            {
                ok = false;
                error = "the arena is full";
                errorPosition = -1;
            }
        }
        unsafe { scratch.Destroy(); } // SAFETY: nothing allocated in scratch is used after this.
        return ok;
    }

    static Regex Store(Arena arena, ref RegexCompiler compiler)
    {
        int length = compiler.Program.Count;
        int groups = compiler.Groups;
        int slots = 2 * groups;
        Header* header = arena.Alloc<Header>();
        Inst* program = arena.AllocArray<Inst>(length);
        CharRange* ranges = arena.AllocArray<CharRange>(compiler.Ranges.Count + 1);
        int* marks = arena.AllocArray<int>(length);
        int* pcs0 = arena.AllocArray<int>(length);
        int* pcs1 = arena.AllocArray<int>(length);
        int* caps0 = arena.AllocArray<int>(length * slots);
        int* caps1 = arena.AllocArray<int>(length * slots);
        int* stack = arena.AllocArray<int>(3 * (4 * length + 8));
        int* work = arena.AllocArray<int>(slots);
        nint* names = arena.AllocArray<nint>(groups);
        if (header == null || program == null || ranges == null || marks == null || pcs0 == null || pcs1 == null
            || caps0 == null || caps1 == null || stack == null || work == null || names == null)
            return default;
        for (int i = 0; i < compiler.NameCount; i++)
        {
            string name = Str.ToArenaString(arena, compiler.NameAt(i));
            if (name == null)
                return default;
            unsafe { names[compiler.FirstNamed + i] = Unsafe.As<string, nint>(ref name); } // SAFETY: groups slots.
        }

        unsafe
        {
            // SAFETY: every array was just allocated with the length it is filled to.
            for (int i = 0; i < length; i++)
                program[i] = compiler.Program.Get(i);
            for (int i = 0; i < compiler.Ranges.Count; i++)
                ranges[i] = compiler.Ranges.Get(i);
            header->Arena = arena;
            header->Program = program;
            header->Length = length;
            header->Ranges = ranges;
            header->Groups = groups;
            header->Names = names;
            header->Options = compiler.Options;
            header->Marks = marks;
            header->Pcs0 = pcs0;
            header->Pcs1 = pcs1;
            header->Caps0 = caps0;
            header->Caps1 = caps1;
            header->Stack = stack;
            header->Work = work;
        }
        return new Regex { _header = header };
    }

    public bool IsCreated => _header != null;

    /// <summary>The number of capturing groups, not counting group 0, the whole match.</summary>
    public int GroupCount
    {
        get
        {
            if (_header == null)
                return 0;
            unsafe { return _header->Groups - 1; }
        }
    }

    /// <summary>The number of the group named <paramref name="name"/>, or -1 if there is none.</summary>
    public int GroupNumberFromName(ReadOnlySpan<char> name)
    {
        if (_header == null)
            return -1;
        unsafe
        {
            // SAFETY: a live header; Names has Groups slots.
            for (int i = 1; i < _header->Groups; i++)
            {
                nint slot = _header->Names[i];
                if (slot != 0 && Unsafe.As<nint, string>(ref slot).AsSpan().SequenceEqual(name))
                    return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The name of group <paramref name="number"/>, or null if it has none (or
    /// doesn't exist). .NET gives an unnamed group's number as text instead.
    /// </summary>
    public string GroupNameFromNumber(int number)
    {
        if (_header == null)
            return null;
        unsafe
        {
            // SAFETY: a live header; number is checked against Groups.
            if ((uint)number >= (uint)_header->Groups)
                return null;
            nint slot = _header->Names[number];
            return slot == 0 ? null : Unsafe.As<nint, string>(ref slot);
        }
    }

    /// <summary>Whether the pattern matches anywhere in <paramref name="input"/>.</summary>
    public bool IsMatch(ReadOnlySpan<char> input)
    {
        Span<int> captures = stackalloc int[2];
        return Run(input, 0, captures);
    }

    /// <summary>
    /// The first match at or after <paramref name="start"/>, with its groups.
    /// Check Success; NextMatch() finds the one after it.
    /// </summary>
    public RegexMatch Match(ReadOnlySpan<char> input, int start = 0)
    {
        var match = new RegexMatch(this, input);
        if ((uint)start <= (uint)input.Length && _header != null)
        {
            int groups;
            unsafe { groups = _header->Groups; } // SAFETY: a live header.
            Span<int> captures = stackalloc int[2 * MaxGroups];
            captures = captures.Slice(0, 2 * groups);
            if (Run(input, start, captures))
                match.SetCaptures(captures, groups);
        }
        return match;
    }

    /// <summary>For foreach: every match, left to right, not overlapping.</summary>
    public RegexMatches Matches(ReadOnlySpan<char> input) => new RegexMatches(this, input);

    /// <summary>How many matches there are, not overlapping.</summary>
    public int Count(ReadOnlySpan<char> input)
    {
        int count = 0;
        foreach (RegexMatch _ in Matches(input))
            count++;
        return count;
    }

    /// <summary>
    /// <paramref name="input"/> with each match replaced by
    /// <paramref name="replacement"/> ($1, ${1}, $$, $&amp;, $`, $', $+, $_), as a
    /// new string in <paramref name="arena"/>; the first <paramref name="count"/>
    /// matches only, if it isn't -1. Null if the arena is out of memory.
    /// </summary>
    public string Replace(Arena arena, ReadOnlySpan<char> input, ReadOnlySpan<char> replacement, int count = -1)
    {
        StrBuilder output = StrBuilder.Create(arena, input.Length + 16);
        return Replace(output, input, replacement, count) ? output.TakeString() : null;
    }

    /// <summary>
    /// The same, appended to <paramref name="output"/>. False if it isn't
    /// created or its arena is out of memory.
    /// </summary>
    public bool Replace(StrBuilder output, ReadOnlySpan<char> input, ReadOnlySpan<char> replacement, int count = -1)
    {
        if (!output.IsCreated)
            return false;
        bool ok = true;
        int last = 0;
        if (count != 0)
        {
            foreach (RegexMatch found in Matches(input))
            {
                RegexMatch match = found;
                ok &= output.Append(input.Slice(last, match.Index - last));
                ok &= AppendReplacement(output, replacement, ref match, input);
                last = match.Index + match.Length;
                if (--count == 0)
                    break;
            }
        }
        return output.Append(input.Slice(last)) && ok;
    }

    /// <summary>The same, appended to <paramref name="output"/>, inside Arena.Scratch.</summary>
    public void Replace(ScratchText output, ReadOnlySpan<char> input, ReadOnlySpan<char> replacement, int count = -1)
        => Replace(output.BuilderToAppendTo(), input, replacement, count); // the scratch arena stops the program rather than fail

    /// <summary>
    /// <paramref name="input"/> with each match replaced by what
    /// <paramref name="evaluator"/> appends to the builder it is given, as a new
    /// string in <paramref name="arena"/>, like .NET's MatchEvaluator:
    /// <code>
    /// static void Double(RegexMatch m, StrBuilder output)
    /// {
    ///     Str.TryParse(m.Value, out long number);
    ///     output.Append(2 * number);
    /// }
    /// string doubled = numbers.Replace(arena, text, &amp;Double);
    /// </code>
    /// Null if the arena is out of memory.
    /// </summary>
    public string Replace(Arena arena, ReadOnlySpan<char> input, delegate*<RegexMatch, StrBuilder, void> evaluator, int count = -1)
    {
        StrBuilder output = StrBuilder.Create(arena, input.Length + 16);
        return Replace(output, input, evaluator, count) ? output.TakeString() : null;
    }

    /// <summary>The same, appended to <paramref name="output"/>. False if it isn't created or its arena is full.</summary>
    public bool Replace(StrBuilder output, ReadOnlySpan<char> input, delegate*<RegexMatch, StrBuilder, void> evaluator, int count = -1)
    {
        if (!output.IsCreated || evaluator == null)
            return false;
        bool ok = true;
        int last = 0;
        if (count != 0)
        {
            foreach (RegexMatch match in Matches(input))
            {
                ok &= output.Append(input.Slice(last, match.Index - last));
                unsafe { evaluator(match, output); } // SAFETY: a function pointer the caller gave.
                last = match.Index + match.Length;
                if (--count == 0)
                    break;
            }
        }
        return output.Append(input.Slice(last)) && ok;
    }

    /// <summary>
    /// For foreach: the pieces of <paramref name="input"/> between matches, as
    /// slices of it, with the text of each group that took part in a match
    /// between the pieces before and after it, as .NET's Split. With a
    /// <paramref name="count"/> above 0, at most that many pieces: the last is
    /// the rest of the input.
    /// </summary>
    public RegexSplit Split(ReadOnlySpan<char> input, int count = 0) => new RegexSplit(this, input, count);

    // Appends the replacement pattern for one match.
    static bool AppendReplacement(StrBuilder output, ReadOnlySpan<char> replacement, scoped ref RegexMatch match, ReadOnlySpan<char> input)
    {
        bool ok = true;
        int groups = match.GroupsInMatch;
        int literal = 0;
        int i = 0;
        while (i < replacement.Length)
        {
            if (replacement[i] != '$' || i + 1 == replacement.Length)
            {
                i++;
                continue;
            }
            int end = i + 2;    // just past the substitution
            int group = -1;     // the group it stands for, if it does
            ReadOnlySpan<char> text = default;
            bool substituted = true;
            char c = replacement[i + 1];
            if (c == '$')
                text = "$";
            else if (c == '&')
                group = 0;
            else if (c == '`')
                text = input.Slice(0, match.Index);
            else if (c == '\'')
                text = input.Slice(match.Index + match.Length);
            else if (c == '+')
                group = groups - 1;
            else if (c == '_')
                text = input;
            else
            {
                // $n or ${n}: every digit is part of the number, and it must be a group.
                bool braced = c == '{';
                int digits = braced ? i + 2 : i + 1;
                int number = 0;
                int j = digits;
                while (j < replacement.Length && replacement[j] >= '0' && replacement[j] <= '9' && j - digits < 9)
                    number = number * 10 + (replacement[j++] - '0');
                if (j < replacement.Length && replacement[j] >= '0' && replacement[j] <= '9')
                    number = int.MaxValue; // too many digits: no such group
                bool closed = !braced || (j < replacement.Length && replacement[j] == '}');
                substituted = j > digits && closed && number < groups;
                if (substituted)
                {
                    group = number;
                    end = braced ? j + 1 : j;
                }
                else if (braced && j == digits)
                {
                    // ${name}: a named group's name.
                    int close = replacement.Slice(digits).IndexOf('}');
                    if (close > 0)
                    {
                        int named = match.Regex.GroupNumberFromName(replacement.Slice(digits, close));
                        substituted = named > 0;
                        if (substituted)
                        {
                            group = named;
                            end = digits + close + 1;
                        }
                    }
                }
            }
            if (!substituted)
            {
                i++;
                continue;
            }
            if (group >= 0)
                text = match.Groups[group].Value; // empty if the group didn't take part
            ok &= output.Append(replacement.Slice(literal, i - literal));
            ok &= output.Append(text);
            i = end;
            literal = end;
        }
        return output.Append(replacement.Slice(literal)) && ok;
    }

    // ---- the Pike VM ---------------------------------------------------------

    // Finds the first match starting at or after start. captures gets the
    // start and end of each group it has room for, -1 for one that didn't take
    // part. Returns whether there was a match.
    internal bool Run(ReadOnlySpan<char> input, int start, Span<int> captures)
    {
        if (_header == null)
            return false;
        unsafe
        {
            // SAFETY: a live header; every pc stays below Length (the compiler
            // only emits targets inside the program), every list holds at most
            // Length threads (a pc is added once per list), and the stack has
            // room for 4 entries per instruction, more than the closure pushes.
            Header* h = _header;
            int slots = 2 * h->Groups;
            int* pcs = h->Pcs0, nextPcs = h->Pcs1;
            int* caps = h->Caps0, nextCaps = h->Caps1;
            int count = 0, nextCount;
            int listGeneration = 0;
            bool matched = false;

            for (int pos = start; ; pos++)
            {
                // An empty list starts afresh; a list built in the last step
                // keeps its generation, so the new thread can't duplicate one.
                if (count == 0)
                    listGeneration = NextGeneration(h);
                // A new thread starting here, lower in priority than those that
                // started earlier: the leftmost match wins. Once there is a
                // match, nothing starting later can beat it.
                if (!matched)
                {
                    for (int i = 0; i < slots; i++)
                        h->Work[i] = -1;
                    AddThread(h, input, pos, 0, pcs, caps, ref count, listGeneration);
                }
                if (count == 0)
                {
                    if (matched || pos >= input.Length)
                        break;
                    continue;
                }

                nextCount = 0;
                int nextGeneration = NextGeneration(h);
                char c = pos < input.Length ? input[pos] : '\0';
                for (int t = 0; t < count; t++)
                {
                    int pc = pcs[t];
                    int* threadCaps = caps + t * slots;
                    Inst* inst = &h->Program[pc];
                    if (inst->Op == Op.Match)
                    {
                        matched = true;
                        for (int i = 0; i < captures.Length && i < slots; i++)
                            captures[i] = threadCaps[i];
                        break; // threads after this one are lower in priority
                    }
                    if (pos < input.Length && Consumes(h, inst, c))
                    {
                        for (int i = 0; i < slots; i++)
                            h->Work[i] = threadCaps[i];
                        AddThread(h, input, pos + 1, pc + 1, nextPcs, nextCaps, ref nextCount, nextGeneration);
                    }
                }

                int* swap = pcs; pcs = nextPcs; nextPcs = swap;
                swap = caps; caps = nextCaps; nextCaps = swap;
                count = nextCount;
                listGeneration = nextGeneration;
                if (pos >= input.Length)
                    break; // nothing was consumed past the end, so the list is empty
            }
            return matched;
        }
    }

    // Generations tell whether a pc is on the list being built; when they run
    // out, the marks start over.
    static unsafe int NextGeneration(Header* h)
    {
        unsafe
        {
            if (h->Generation == int.MaxValue)
            {
                for (int i = 0; i < h->Length; i++)
                    h->Marks[i] = 0;
                h->Generation = 0;
            }
            return ++h->Generation;
        }
    }

    static unsafe bool Consumes(Header* h, Inst* inst, char c)
    {
        unsafe
        {
            switch (inst->Op)
            {
                case Op.Char: return c == inst->C;
                case Op.CharFold: return Lower(c) == inst->C;
                case Op.Any: return c != '\n';
                case Op.AnyNewline: return true;
                case Op.Class:
                {
                    bool found = InRanges(h->Ranges + inst->X, inst->Y, c);
                    if (!found && inst->FoldCase)
                    {
                        char other = c >= 'a' && c <= 'z' ? (char)(c - 32) : c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
                        if (other != c)
                            found = InRanges(h->Ranges + inst->X, inst->Y, other);
                    }
                    return found != inst->Negate;
                }
                default: return false;
            }
        }
    }

    static unsafe bool InRanges(CharRange* ranges, int count, char c)
    {
        unsafe
        {
            for (int i = 0; i < count; i++)
            {
                if (c >= ranges[i].Low && c <= ranges[i].High)
                    return true;
            }
            return false;
        }
    }

    // Adds the thread at pc to the list, after following every instruction
    // that consumes no input (jumps, splits, saves, anchors), in priority
    // order. h->Work holds the thread's captures; it's restored on the way out.
    static unsafe void AddThread(Header* h, ReadOnlySpan<char> input, int pos, int pc0,
        int* pcs, int* caps, ref int count, int generation)
    {
        unsafe
        {
            int slots = 2 * h->Groups;
            int* stack = h->Stack;
            int top = 0;
            // Entries: (pc, 0, 0) to visit pc, or (-1, slot, value) to put a
            // capture back when everything after a Save has been visited.
            stack[0] = pc0; stack[1] = 0; stack[2] = 0;
            top = 1;
            while (top > 0)
            {
                top--;
                int pc = stack[3 * top];
                if (pc < 0)
                {
                    h->Work[stack[3 * top + 1]] = stack[3 * top + 2];
                    continue;
                }
                if (h->Marks[pc] == generation)
                    continue;
                h->Marks[pc] = generation;
                Inst* inst = &h->Program[pc];
                switch (inst->Op)
                {
                    case Op.Jump:
                        Push(stack, ref top, inst->X, 0, 0);
                        break;
                    case Op.Split:
                        Push(stack, ref top, inst->Y, 0, 0); // visited second
                        Push(stack, ref top, inst->X, 0, 0); // visited first
                        break;
                    case Op.Save:
                        if (inst->X < slots)
                        {
                            Push(stack, ref top, -1, inst->X, h->Work[inst->X]);
                            h->Work[inst->X] = pos;
                        }
                        Push(stack, ref top, pc + 1, 0, 0);
                        break;
                    case Op.LineStart:
                    case Op.LineEnd:
                    case Op.InputStart:
                    case Op.InputEnd:
                    case Op.InputEndOrFinalNewline:
                    case Op.WordBoundary:
                    case Op.NotWordBoundary:
                        if (Holds(inst->Op, input, pos))
                            Push(stack, ref top, pc + 1, 0, 0);
                        break;
                    default:
                        pcs[count] = pc;
                        int* threadCaps = caps + count * slots;
                        for (int i = 0; i < slots; i++)
                            threadCaps[i] = h->Work[i];
                        count++;
                        break;
                }
            }
        }
    }

    static unsafe void Push(int* stack, ref int top, int a, int b, int c)
    {
        unsafe
        {
            stack[3 * top] = a;
            stack[3 * top + 1] = b;
            stack[3 * top + 2] = c;
            top++;
        }
    }

    static bool Holds(Op op, ReadOnlySpan<char> input, int pos)
    {
        switch (op)
        {
            case Op.InputStart: return pos == 0;
            case Op.InputEnd: return pos == input.Length;
            case Op.InputEndOrFinalNewline:
                return pos == input.Length || (pos == input.Length - 1 && input[pos] == '\n');
            case Op.LineStart: return pos == 0 || input[pos - 1] == '\n';
            case Op.LineEnd: return pos == input.Length || input[pos] == '\n';
            case Op.WordBoundary: return IsWordAt(input, pos - 1) != IsWordAt(input, pos);
            case Op.NotWordBoundary: return IsWordAt(input, pos - 1) == IsWordAt(input, pos);
            default: return false;
        }
    }

    static bool IsWordAt(ReadOnlySpan<char> input, int pos)
    {
        if ((uint)pos >= (uint)input.Length)
            return false;
        char c = input[pos];
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
    }

    internal static char Lower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
}

/// <summary>
/// A match, or its absence (Success false). It refers to the input it was
/// found in, so it lives only as long as that span.
/// </summary>
public ref struct RegexMatch
{
    [InlineArray(2 * Regex.MaxGroups)]
    internal struct Captures
    {
        int _first;
    }

    readonly Regex _regex;
    readonly ReadOnlySpan<char> _input;
    Captures _captures;
    int _groups;

    internal RegexMatch(Regex regex, ReadOnlySpan<char> input)
    {
        _regex = regex;
        _input = input;
        _groups = 0;
    }

    internal void SetCaptures(scoped ReadOnlySpan<int> captures, int groups)
    {
        for (int i = 0; i < captures.Length; i++)
            _captures[i] = captures[i];
        _groups = groups;
    }

    public bool Success => _groups > 0;

    // The pattern's groups, group 0 included (0 for no match).
    internal int GroupsInMatch => _groups;

    internal Regex Regex => _regex;

    /// <summary>Where the match starts in the input (0 if there is none).</summary>
    public int Index => Success ? _captures[0] : 0;

    public int Length => Success ? _captures[1] - _captures[0] : 0;

    /// <summary>The matched text, a slice of the input (empty if there is none).</summary>
    public ReadOnlySpan<char> Value => Success ? _input.Slice(Index, Length) : default;

    /// <summary>Group 0 is the whole match; group n, the n-th ( of the pattern.</summary>
    public RegexGroups Groups => new RegexGroups(this);

    internal RegexGroup Group(int number)
    {
        if ((uint)number >= (uint)_groups || _captures[2 * number] < 0 || _captures[2 * number + 1] < 0)
            return default;
        int start = _captures[2 * number];
        return new RegexGroup(_input.Slice(start, _captures[2 * number + 1] - start), start);
    }

    /// <summary>
    /// The next match after this one. After an empty match, the search starts
    /// one character further on, so the loop always ends.
    /// </summary>
    public RegexMatch NextMatch()
    {
        if (!Success)
            return this;
        int next = Index + Length;
        if (Length == 0)
            next++;
        if (next > _input.Length)
            return new RegexMatch(_regex, _input);
        return _regex.Match(_input, next);
    }
}

/// <summary>A match's groups: <c>match.Groups[1].Value</c>.</summary>
public readonly ref struct RegexGroups
{
    readonly RegexMatch _match;

    internal RegexGroups(RegexMatch match) => _match = match;

    /// <summary>Group <paramref name="number"/>; Success false if it didn't take part, or doesn't exist.</summary>
    public RegexGroup this[int number] => _match.Group(number);

    /// <summary>The group named <paramref name="name"/>; Success false if it didn't take part, or doesn't exist.</summary>
    public RegexGroup this[ReadOnlySpan<char> name] => _match.Group(_match.Regex.GroupNumberFromName(name));
}

/// <summary>One group of a match.</summary>
public readonly ref struct RegexGroup
{
    readonly ReadOnlySpan<char> _value;
    readonly int _index;
    readonly bool _success;

    internal RegexGroup(ReadOnlySpan<char> value, int index)
    {
        _value = value;
        _index = index;
        _success = true;
    }

    public bool Success => _success;

    public int Index => _index;

    public int Length => _value.Length;

    public ReadOnlySpan<char> Value => _value;
}

/// <summary>For foreach over Regex.Matches.</summary>
public readonly ref struct RegexMatches
{
    readonly Regex _regex;
    readonly ReadOnlySpan<char> _input;

    internal RegexMatches(Regex regex, ReadOnlySpan<char> input)
    {
        _regex = regex;
        _input = input;
    }

    public Enumerator GetEnumerator() => new Enumerator(_regex, _input);

    public ref struct Enumerator
    {
        readonly Regex _regex;
        readonly ReadOnlySpan<char> _input;
        RegexMatch _current;
        bool _started;

        internal Enumerator(Regex regex, ReadOnlySpan<char> input)
        {
            _regex = regex;
            _input = input;
            _current = default;
            _started = false;
        }

        public bool MoveNext()
        {
            _current = _started ? _current.NextMatch() : _regex.Match(_input);
            _started = true;
            return _current.Success;
        }

        public RegexMatch Current => _current;
    }
}

/// <summary>For foreach over Regex.Split: the pieces of the input, and the text of groups between them.</summary>
public readonly ref struct RegexSplit
{
    readonly Regex _regex;
    readonly ReadOnlySpan<char> _input;
    readonly int _count;

    internal RegexSplit(Regex regex, ReadOnlySpan<char> input, int count)
    {
        _regex = regex;
        _input = input;
        _count = count;
    }

    /// <summary>For foreach: the pieces, in order.</summary>
    public Enumerator GetEnumerator() => new Enumerator(_regex, _input, _count);

    public ref struct Enumerator
    {
        readonly ReadOnlySpan<char> _input;
        RegexMatch _match;     // the match after the piece last given
        int _start;            // where the next piece starts
        int _remaining;        // pieces still allowed, or -1 for no limit
        int _group;            // the next group of _match to give, or 0
        bool _done;
        ReadOnlySpan<char> _current;

        internal Enumerator(Regex regex, ReadOnlySpan<char> input, int count)
        {
            _input = input;
            _remaining = count > 0 ? count : -1;
            _match = _remaining == 1 ? default : regex.Match(input);
            _start = 0;
            _group = 0;
            _done = false;
            _current = default;
        }

        public bool MoveNext()
        {
            if (_group > 0)
            {
                // The groups of the match after the last piece, then the next match.
                while (_group < _match.GroupsInMatch)
                {
                    RegexGroup group = _match.Groups[_group++];
                    if (group.Success)
                    {
                        _current = group.Value;
                        return true;
                    }
                }
                _group = 0;
                _match = _remaining == 1 ? default : _match.NextMatch();
            }
            if (_done)
                return false;
            if (_match.Success)
            {
                _current = _input.Slice(_start, _match.Index - _start);
                _start = _match.Index + _match.Length;
                if (_remaining > 0)
                    _remaining--;
                _group = 1;
                return true;
            }
            _current = _input.Slice(_start);
            _done = true;
            return true;
        }

        public ReadOnlySpan<char> Current => _current;
    }
}

// The parser and code generator: pattern -> syntax tree -> program. Its
// memory is a scratch arena that TryCreate frees afterwards.
internal ref struct RegexCompiler
{
    enum Kind : byte { Empty, Char, Any, Class, Concat, Alternate, Repeat, Group, Assert }

    struct Node
    {
        public Kind Kind;
        public bool Greedy;
        public bool Negate;
        public Regex.Op Assert;
        public char C;
        public int Min, Max;        // Repeat; Max -1 for no limit
        public int Child;           // first child, or -1
        public int Next;            // next sibling, or -1
        public int Group;           // capture number, or -1
        public int RangeStart, RangeCount;
    }

    readonly ReadOnlySpan<char> _pattern;
    int _pos;
    List<Node> _nodes;
    public List<Regex.Inst> Program;
    public List<Regex.CharRange> Ranges;
    public int Groups;
    public RegexOptions Options;
    string _error;
    int _errorPosition;

    // Named groups: while parsing, a named group's Group is NamedBase + the
    // name's index; once every unnamed group is counted, the names are
    // numbered after them, in order of first appearance, as .NET does.
    const int NamedBase = 1 << 20;
    List<int> _names;      // start and length in the pattern, per name
    public int FirstNamed;  // the number of the first named group
    public int NameCount => _names.Count / 2;
    public ReadOnlySpan<char> NameAt(int index) => _pattern.Slice(_names.Get(2 * index), _names.Get(2 * index + 1));

    public RegexCompiler(Arena scratch, ReadOnlySpan<char> pattern, RegexOptions options)
    {
        _pattern = pattern;
        _pos = 0;
        _nodes = List<Node>.Create(scratch, 64);
        Program = List<Regex.Inst>.Create(scratch, 64);
        Ranges = List<Regex.CharRange>.Create(scratch, 16);
        _names = List<int>.Create(scratch, 8);
        FirstNamed = 0;
        Groups = 1;
        Options = options;
        _error = null;
        _errorPosition = -1;
    }

    public bool Run(out string error, out int position)
    {
        if (!_nodes.IsCreated || !Program.IsCreated || !Ranges.IsCreated || !_names.IsCreated)
            Fail("the arena is full", -1);
        int root = _error == null ? ParseAlternation() : -1;
        if (_error == null && _pos < _pattern.Length)
            Fail("too many )'s", _pos);
        if (_error == null)
        {
            FirstNamed = Groups;
            Groups += NameCount;
            Emit(Regex.Op.Save, 0, 0);
            Compile(root);
            Emit(Regex.Op.Save, 1, 0);
            Emit(Regex.Op.Match, 0, 0);
        }
        error = _error;
        position = _errorPosition;
        return _error == null;
    }

    void Fail(string message, int position)
    {
        if (_error != null)
            return;
        _error = message;
        _errorPosition = position;
    }

    int NewNode(Kind kind)
    {
        if (!_nodes.Add(new Node { Kind = kind, Child = -1, Next = -1, Group = -1 }))
        {
            Fail("the arena is full", -1);
            return 0;
        }
        return _nodes.Count - 1;
    }

    ref Node At(int index)
    {
        unsafe { return ref *_nodes.GetPtr(index); } // SAFETY: index came from NewNode; nodes aren't added while the ref is used.
    }

    // ---- parsing -------------------------------------------------------------

    bool AtEnd => _pos >= _pattern.Length;

    int ParseAlternation()
    {
        int first = ParseConcat();
        if (AtEnd || _pattern[_pos] != '|' || _error != null)
            return first;
        int alternate = NewNode(Kind.Alternate);
        At(alternate).Child = first;
        int last = first;
        while (!AtEnd && _pattern[_pos] == '|' && _error == null)
        {
            _pos++;
            int next = ParseConcat();
            At(last).Next = next;
            last = next;
        }
        return alternate;
    }

    int ParseConcat()
    {
        int concat = NewNode(Kind.Concat);
        int last = -1;
        while (!AtEnd && _pattern[_pos] != '|' && _pattern[_pos] != ')' && _error == null)
        {
            int item = ParseRepeat();
            if (_error != null)
                break;
            if (last < 0)
                At(concat).Child = item;
            else
                At(last).Next = item;
            last = item;
        }
        return concat;
    }

    int ParseRepeat()
    {
        int atomStart = _pos;
        int atom = ParseAtom();
        bool quantified = false;
        while (!AtEnd && _error == null)
        {
            int min, max;
            int at = _pos;
            char c = _pattern[_pos];
            if (c == '*') { min = 0; max = -1; _pos++; }
            else if (c == '+') { min = 1; max = -1; _pos++; }
            else if (c == '?') { min = 0; max = 1; _pos++; }
            else if (c == '{' && TryParseCount(out min, out max)) { }
            else break;

            if (quantified)
            {
                Fail("nested quantifier", at);
                break;
            }
            Kind kind = At(atom).Kind;
            if (kind == Kind.Assert || (kind == Kind.Concat && At(atom).Child < 0))
            {
                Fail("quantifier following nothing", at);
                break;
            }
            if (max >= 0 && min > max)
            {
                Fail("{n,m} with n greater than m", at);
                break;
            }
            bool greedy = true;
            if (!AtEnd && _pattern[_pos] == '?')
            {
                greedy = false;
                _pos++;
            }
            int repeat = NewNode(Kind.Repeat);
            At(repeat).Child = atom;
            At(repeat).Min = min;
            At(repeat).Max = max;
            At(repeat).Greedy = greedy;
            atom = repeat;
            quantified = true;
        }
        if (atomStart == _pos && _error == null)
            Fail("unexpected character", _pos);
        return atom;
    }

    // {n}, {n,} or {n,m}. Anything else, as in .NET, is a literal {.
    bool TryParseCount(out int min, out int max)
    {
        int p = _pos + 1;
        min = 0;
        max = -1;
        if (!ReadNumber(ref p, out min))
            return false;
        if (p < _pattern.Length && _pattern[p] == '}')
        {
            max = min;
            _pos = p + 1;
            return true;
        }
        if (p >= _pattern.Length || _pattern[p] != ',')
            return false;
        p++;
        if (p < _pattern.Length && _pattern[p] == '}')
        {
            _pos = p + 1;
            return true;
        }
        if (!ReadNumber(ref p, out max) || p >= _pattern.Length || _pattern[p] != '}')
            return false;
        _pos = p + 1;
        return true;
    }

    bool ReadNumber(ref int p, out int value)
    {
        value = 0;
        int start = p;
        while (p < _pattern.Length && _pattern[p] >= '0' && _pattern[p] <= '9')
        {
            value = value * 10 + (_pattern[p] - '0');
            if (value > 100000)
            {
                Fail("count too large", start);
                return false;
            }
            p++;
        }
        return p > start;
    }

    int ParseAtom()
    {
        char c = _pattern[_pos];
        switch (c)
        {
            case '(':
                return ParseGroup();
            case '[':
                return ParseClass();
            case '.':
                _pos++;
                return NewNode(Kind.Any);
            case '^':
                _pos++;
                return AssertNode((Options & RegexOptions.Multiline) != 0 ? Regex.Op.LineStart : Regex.Op.InputStart);
            case '$':
                _pos++;
                return AssertNode((Options & RegexOptions.Multiline) != 0 ? Regex.Op.LineEnd : Regex.Op.InputEndOrFinalNewline);
            case '\\':
                return ParseEscape();
            case '*':
            case '+':
            case '?':
                Fail("quantifier following nothing", _pos);
                return NewNode(Kind.Empty);
            default:
                _pos++;
                return CharNode(c);
        }
    }

    int ParseGroup()
    {
        int open = _pos;
        _pos++;
        int group = -1;
        if (!AtEnd && _pattern[_pos] == '?')
        {
            char kind = _pos + 1 < _pattern.Length ? _pattern[_pos + 1] : '\0';
            bool lookbehind = kind == '<' && _pos + 2 < _pattern.Length && (_pattern[_pos + 2] == '=' || _pattern[_pos + 2] == '!');
            if (kind == ':')
            {
                _pos += 2;
            }
            else if ((kind == '<' || kind == '\'') && !lookbehind)
            {
                group = ParseGroupName(open, kind == '<' ? '>' : '\'');
                if (group < 0)
                    return NewNode(Kind.Empty);
            }
            else
            {
                Fail(kind == '=' || kind == '!' || lookbehind
                    ? "lookaround isn't supported"
                    : "this kind of group isn't supported", open);
                return NewNode(Kind.Empty);
            }
        }
        else
        {
            if (Groups + NameCount >= Regex.MaxGroups)
            {
                Fail("too many capturing groups (15 at most)", open);
                return NewNode(Kind.Empty);
            }
            group = Groups++;
        }
        int inner = ParseAlternation();
        if (_error != null)
            return inner;
        if (AtEnd || _pattern[_pos] != ')')
        {
            Fail("missing )", open);
            return inner;
        }
        _pos++;
        int node = NewNode(Kind.Group);
        At(node).Child = inner;
        At(node).Group = group;
        return node;
    }

    // (?<name> or (?'name': returns NamedBase + the name's index, the same for
    // a name seen before, or -1 after a Fail. _pos is at the ?.
    int ParseGroupName(int open, char close)
    {
        int start = _pos + 2;
        int end = start;
        while (end < _pattern.Length && IsNameChar(_pattern[end]))
            end++;
        if (end == start || end == _pattern.Length || _pattern[end] != close)
        {
            Fail("invalid group name: letters, digits and _ only, starting with a letter or _", start);
            return -1;
        }
        if (_pattern[start] >= '0' && _pattern[start] <= '9')
        {
            Fail("numbered group names aren't supported", start);
            return -1;
        }
        ReadOnlySpan<char> name = _pattern.Slice(start, end - start);
        _pos = end + 1;
        for (int i = 0; i < NameCount; i++)
        {
            if (NameAt(i).SequenceEqual(name))
                return NamedBase + i;
        }
        if (Groups + NameCount >= Regex.MaxGroups)
        {
            Fail("too many capturing groups (15 at most)", open);
            return -1;
        }
        if (!_names.Add(start) || !_names.Add(end - start))
        {
            Fail("the arena is full", -1);
            return -1;
        }
        return NamedBase + NameCount - 1;
    }

    static bool IsNameChar(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    int ParseEscape()
    {
        int at = _pos;
        _pos++;
        if (AtEnd)
        {
            Fail("\\ at the end of the pattern", at);
            return NewNode(Kind.Empty);
        }
        char c = _pattern[_pos++];
        switch (c)
        {
            case 'd': case 'D': case 'w': case 'W': case 's': case 'S':
            {
                int node = NewNode(Kind.Class);
                At(node).RangeStart = Ranges.Count;
                AddShorthand(c);
                At(node).RangeCount = Ranges.Count - At(node).RangeStart;
                return node;
            }
            case 'b': return AssertNode(Regex.Op.WordBoundary);
            case 'B': return AssertNode(Regex.Op.NotWordBoundary);
            case 'A': return AssertNode(Regex.Op.InputStart);
            case 'z': return AssertNode(Regex.Op.InputEnd);
            case 'Z': return AssertNode(Regex.Op.InputEndOrFinalNewline);
        }
        if (c >= '1' && c <= '9')
        {
            Fail("backreferences aren't supported", at);
            return NewNode(Kind.Empty);
        }
        _pos--;
        char literal = ParseCharEscape(at);
        return CharNode(literal);
    }

    // After a \ (at points at it, _pos at the next character): a character
    // escape. Fails for a letter or digit that isn't one.
    char ParseCharEscape(int at)
    {
        char c = _pattern[_pos++];
        switch (c)
        {
            case 't': return '\t';
            case 'n': return '\n';
            case 'r': return '\r';
            case 'f': return '\f';
            case 'v': return '\v';
            case 'a': return '\a';
            case 'e': return '\u001B';
            case '0': return '\0';
            case 'x': return (char)ReadHex(2, at);
            case 'u': return (char)ReadHex(4, at);
        }
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            Fail("unrecognized escape", at);
        return c;
    }

    int ReadHex(int digits, int at)
    {
        int value = 0;
        for (int i = 0; i < digits; i++)
        {
            int d = AtEnd ? -1 : HexDigit(_pattern[_pos]);
            if (d < 0)
            {
                Fail("not enough hex digits", at);
                return 0;
            }
            value = value * 16 + d;
            _pos++;
        }
        return value;
    }

    static int HexDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    int ParseClass()
    {
        int open = _pos;
        _pos++;
        int node = NewNode(Kind.Class);
        if (!AtEnd && _pattern[_pos] == '^')
        {
            At(node).Negate = true;
            _pos++;
        }
        At(node).RangeStart = Ranges.Count;
        bool first = true;
        while (true)
        {
            if (AtEnd)
            {
                Fail("missing ]", open);
                return node;
            }
            char c = _pattern[_pos];
            if (c == ']' && !first)
            {
                _pos++;
                break;
            }
            first = false;
            char low;
            if (c == '\\')
            {
                int at = _pos;
                _pos++;
                if (AtEnd)
                {
                    Fail("missing ]", open);
                    return node;
                }
                char e = _pattern[_pos];
                if (e == 'd' || e == 'D' || e == 'w' || e == 'W' || e == 's' || e == 'S')
                {
                    _pos++;
                    AddShorthand(e);
                    continue;
                }
                if (e == 'b')
                {
                    _pos++;
                    low = '\b';
                }
                else
                {
                    low = ParseCharEscape(at);
                }
            }
            else
            {
                low = c;
                _pos++;
            }
            if (_error != null)
                return node;
            char high = low;
            // a-z; a - before ] or at the start is a literal -.
            if (_pos + 1 < _pattern.Length && _pattern[_pos] == '-' && _pattern[_pos + 1] != ']')
            {
                int dash = _pos;
                _pos++;
                char h = _pattern[_pos];
                if (h == '\\')
                {
                    int at = _pos;
                    _pos++;
                    if (AtEnd)
                    {
                        Fail("missing ]", open);
                        return node;
                    }
                    high = ParseCharEscape(at);
                }
                else
                {
                    high = h;
                    _pos++;
                }
                if (high < low)
                {
                    Fail("[x-y] range in reverse order", dash);
                    return node;
                }
            }
            AddRange(low, high);
        }
        At(node).RangeCount = Ranges.Count - At(node).RangeStart;
        return node;
    }

    void AddRange(char low, char high)
    {
        if (!Ranges.Add(new Regex.CharRange { Low = low, High = high }))
            Fail("the arena is full", -1);
    }

    // \d \w \s, or their negations as the ranges around them.
    void AddShorthand(char c)
    {
        switch (c)
        {
            case 'd': AddRange('0', '9'); break;
            case 'D': AddRange('\0', (char)('0' - 1)); AddRange((char)('9' + 1), '￿'); break;
            case 'w': AddRange('0', '9'); AddRange('A', 'Z'); AddRange('_', '_'); AddRange('a', 'z'); break;
            case 'W':
                AddRange('\0', (char)('0' - 1)); AddRange((char)('9' + 1), (char)('A' - 1));
                AddRange((char)('Z' + 1), (char)('_' - 1)); AddRange((char)('_' + 1), (char)('a' - 1));
                AddRange((char)('z' + 1), '￿');
                break;
            case 's': AddRange('\t', '\r'); AddRange(' ', ' '); break; // \t \n \v \f \r and space
            case 'S': AddRange('\0', (char)('\t' - 1)); AddRange((char)('\r' + 1), (char)(' ' - 1)); AddRange((char)(' ' + 1), '￿'); break;
        }
    }

    int CharNode(char c)
    {
        int node = NewNode(Kind.Char);
        At(node).C = c;
        return node;
    }

    int AssertNode(Regex.Op op)
    {
        int node = NewNode(Kind.Assert);
        At(node).Assert = op;
        return node;
    }

    // ---- code generation -----------------------------------------------------

    int Emit(Regex.Op op, int x, int y)
    {
        if (Program.Count >= 1 << 16)
        {
            Fail("the pattern is too large", -1);
            return 0;
        }
        if (!Program.Add(new Regex.Inst { Op = op, X = x, Y = y }))
            Fail("the arena is full", -1);
        return Program.Count - 1;
    }

    ref Regex.Inst Inst(int index)
    {
        unsafe { return ref *Program.GetPtr(index); } // SAFETY: index came from Emit; nothing is emitted while the ref is used.
    }

    void Compile(int index)
    {
        if (_error != null || index < 0)
            return;
        Node node = At(index);
        bool fold = (Options & RegexOptions.IgnoreCase) != 0;
        switch (node.Kind)
        {
            case Kind.Empty:
                break;
            case Kind.Char:
            {
                char lower = Regex.Lower(node.C);
                bool letter = lower >= 'a' && lower <= 'z';
                int pc = Emit(fold && letter ? Regex.Op.CharFold : Regex.Op.Char, 0, 0);
                Inst(pc).C = fold && letter ? lower : node.C;
                break;
            }
            case Kind.Any:
                Emit((Options & RegexOptions.Singleline) != 0 ? Regex.Op.AnyNewline : Regex.Op.Any, 0, 0);
                break;
            case Kind.Class:
            {
                int pc = Emit(Regex.Op.Class, node.RangeStart, node.RangeCount);
                Inst(pc).Negate = node.Negate;
                Inst(pc).FoldCase = fold;
                break;
            }
            case Kind.Assert:
                Emit(node.Assert, 0, 0);
                break;
            case Kind.Concat:
                for (int child = node.Child; child >= 0 && _error == null; child = At(child).Next)
                    Compile(child);
                break;
            case Kind.Group:
                if (node.Group >= NamedBase)
                    node.Group = FirstNamed + node.Group - NamedBase;
                if (node.Group >= 0)
                    Emit(Regex.Op.Save, 2 * node.Group, 0);
                Compile(node.Child);
                if (node.Group >= 0)
                    Emit(Regex.Op.Save, 2 * node.Group + 1, 0);
                break;
            case Kind.Alternate:
                CompileAlternatives(node.Child);
                break;
            case Kind.Repeat:
                CompileRepeat(node);
                break;
        }
    }

    // a|b|c: split to a or the rest; each alternative jumps to the end.
    void CompileAlternatives(int first)
    {
        int child = first;
        int jumps = -1; // a chain of Jump instructions to patch, linked through X
        while (child >= 0 && _error == null)
        {
            int next = At(child).Next;
            if (next < 0)
            {
                Compile(child);
                break;
            }
            int split = Emit(Regex.Op.Split, 0, 0);
            Inst(split).X = Program.Count;
            Compile(child);
            int jump = Emit(Regex.Op.Jump, jumps, 0);
            jumps = jump;
            Inst(split).Y = Program.Count;
            child = next;
        }
        int end = Program.Count;
        while (jumps >= 0 && _error == null)
        {
            int previous = Inst(jumps).X;
            Inst(jumps).X = end;
            jumps = previous;
        }
    }

    void CompileRepeat(Node node)
    {
        // The required copies, then either a loop or the optional copies.
        for (int i = 0; i < node.Min && _error == null; i++)
            Compile(node.Child);
        if (node.Max < 0)
        {
            int split = Emit(Regex.Op.Split, 0, 0);
            Compile(node.Child);
            Emit(Regex.Op.Jump, split, 0);
            SetSplit(split, Program.Count, node.Greedy);
            return;
        }
        int optional = node.Max - node.Min;
        int splits = -1; // chain of splits whose exit goes to the end, linked through their exit
        for (int i = 0; i < optional && _error == null; i++)
        {
            int split = Emit(Regex.Op.Split, 0, 0);
            Compile(node.Child);
            // Remember the split; its exit is patched once the end is known.
            Inst(split).Y = splits;
            splits = split;
        }
        int end = Program.Count;
        while (splits >= 0 && _error == null)
        {
            int previous = Inst(splits).Y;
            SetSplit(splits, end, node.Greedy);
            splits = previous;
        }
    }

    // A split before a repeated part: go into it (greedy first) or skip to exit.
    void SetSplit(int split, int exit, bool greedy)
    {
        int into = split + 1;
        Inst(split).X = greedy ? into : exit;
        Inst(split).Y = greedy ? exit : into;
    }
}
