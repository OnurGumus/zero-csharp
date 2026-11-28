namespace System.Globalization
{
    // How a named culture sorts text: the Unicode Collation Algorithm over
    // the weights scripts/gen-cultures took from .NET's ICU collation
    // (CollationData.cs), for every assigned character. Text becomes
    // collation elements (decomposed to NFD, marks in canonical order, the
    // longest contraction first, the culture's own weights before the root
    // order's), and two texts compare level by level: the letters (primary),
    // then accents (secondary), then case and variants (tertiary). The
    // options drop levels: IgnoreNonSpace the secondary, keeping a case level
    // unless IgnoreCase drops that too; IgnoreSymbols drops punctuation and
    // symbols. Han ideographs are one element for where the block goes, then
    // one for the ideograph's rank in the culture's order (by radical and
    // strokes; pinyin in Chinese, strokes in Traditional Chinese, Japan's
    // order; Korean puts many with the Hangul syllable of their reading).
    // Unassigned and private-use code points sort last, in code point order.
    //
    // CompareInfo reaches this through Linguistic, an abstract class: only a
    // culture made from the table creates a Collator, so a program compares
    // with collation (and links its tables) only if it both uses a named
    // culture and compares.
    internal abstract class Linguistic
    {
        internal abstract int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b, CompareOptions options);
        internal abstract int GetHashCode(ReadOnlySpan<char> text, CompareOptions options);
        // Where value is in source (-1: nowhere), as found (searching from
        // the start or the end, or only there: a prefix or suffix).
        internal abstract int Find(ReadOnlySpan<char> source, ReadOnlySpan<char> value, CompareOptions options, Search search, out int matchLength);
    }

    internal enum Search { First, Last, Prefix, Suffix }

    internal sealed unsafe class Collator : Linguistic
    {
        // An element packed into a ulong: primary (bits 32-63), secondary
        // (16-31), tertiary (8-15) and flags (0-7: 1 symbol, 2 upper case,
        // 4 a character ICU weighs with two elements (the second of a symbol
        // stays in searches with IgnoreSymbols), 8 in the
        // tables: the rank of the ideograph in the primary goes here, at its
        // root position if the tertiary is 1, bits 4-7 kana and width
        // variants).
        private const int Symbol = 1, Upper = 2, SearchKept = 4, HanMarker = 8;
        // In searches: the second element of a character ICU weighs with two.
        private const ulong Continuation = 1UL << 31;

        private readonly int _culture;
        private int _tailoringStart, _tailoringLength; // the culture's entries in Tailorings
        private int[] _tailoringFirst; // each entry's first code point
        private int[] _tailoringAt; // and where it starts
        private bool _backwards, _upperFirst, _loaded;
        private int _hanOrder, _hanja; // index into HanOrders; 1 + index into KoreanHanja, or 0
        private HanRanks _ranks, _rootRanks;

        private static int[] s_rootFirst, s_rootAt;
        private static HanRanks s_rootRanks;

        // The tables are read on first use: a program that only formats with
        // a culture doesn't link them.
        internal Collator(int culture, bool english)
        {
            _culture = culture;
            _asciiOrdinal = english;
        }

        // English cultures: .NET searches text of plain ASCII ordinally.
        private readonly bool _asciiOrdinal;

        private void Load()
        {
            if (_loaded)
                return;
            ReadOnlySpan<byte> cultures = CollationData.Cultures;
            int t = cultures[_culture * 2], h = cultures[_culture * 2 + 1];
            _backwards = (t & 0x80) != 0;
            _upperFirst = (t & 0x40) != 0;
            t &= 0x3F;
            _hanOrder = h & 0xF;
            _hanja = h >> 4;
            if (t != 0)
            {
                ReadOnlySpan<byte> starts = CollationData.TailoringsStarts;
                _tailoringStart = Read4(starts, (t - 1) * 4);
                _tailoringLength = Read4(starts, t * 4) - _tailoringStart;
                Index(Table(0), out _tailoringFirst, out _tailoringAt);
            }
            if (s_rootFirst == null)
            {
                Index(CollationData.RootSequences, out int[] first, out s_rootAt);
                s_rootFirst = first;
            }
            _loaded = true;
        }

        private static ReadOnlySpan<byte> Blob(ReadOnlySpan<byte> all, ReadOnlySpan<byte> starts, int index)
        {
            int start = Read4(starts, index * 4), end = Read4(starts, index * 4 + 4);
            return all.Slice(start, end - start);
        }

        private static int Read3(ReadOnlySpan<byte> b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16);
        private static int Read4(ReadOnlySpan<byte> b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

        // Entries: the number of code points, they (3 bytes each), the number
        // of elements and they (8 bytes each).
        // The tables of entries, read in place: 0 the culture's, 1 the root's.
        private ReadOnlySpan<byte> Table(int which)
            => which == 0 ? CollationData.Tailorings.Slice(_tailoringStart, _tailoringLength) : CollationData.RootSequences;

        private static void Index(ReadOnlySpan<byte> entries, out int[] first, out int[] at)
        {
            int count = 0;
            for (int i = 0; i < entries.Length; count++)
                i += 1 + entries[i] * 3 + 1 + entries[i + 1 + entries[i] * 3] * 8;
            first = new int[count];
            at = new int[count];
            int n = 0;
            for (int i = 0; i < entries.Length; n++)
            {
                first[n] = Read3(entries, i + 1);
                at[n] = i;
                i += 1 + entries[i] * 3 + 1 + entries[i + 1 + entries[i] * 3] * 8;
            }
        }

        // An element: primary (4 bytes), secondary (2), tertiary (1), flags (1).
        private static ulong Element(ReadOnlySpan<byte> b, int at)
            => ((ulong)(uint)Read4(b, at) << 32) | ((ulong)(uint)(b[at + 4] | (b[at + 5] << 8)) << 16)
                | ((ulong)b[at + 6] << 8) | b[at + 7];

        // ---- growing lists: on the stack, moving to the heap when full ----

        private ref struct Buffer
        {
            public Span<int> Items;
            public int Count;

            public void Add(int value)
            {
                if (Count == Items.Length)
                {
                    int[] bigger = new int[Items.Length * 2];
                    Items.CopyTo(bigger);
                    Items = bigger;
                }
                Items[Count++] = value;
            }

            public void RemoveAt(int i)
            {
                for (int k = i; k + 1 < Count; k++)
                    Items[k] = Items[k + 1];
                Count--;
            }
        }

        private ref struct Elements
        {
            public Span<ulong> Items;
            public int Count;

            public void Add(ulong value)
            {
                if (Count == Items.Length)
                {
                    ulong[] bigger = new ulong[Items.Length * 2];
                    Items.CopyTo(bigger);
                    Items = bigger;
                }
                Items[Count++] = value;
            }
        }

        // ---- text to code points (NFD, marks in canonical order) ----

        private static void Decompose(int cp, ref Buffer into)
        {
            if (cp >= 0xAC00 && cp <= 0xD7A3)
            {
                // A Hangul syllable: its jamo.
                int s = cp - 0xAC00;
                into.Add(0x1100 + s / 588);
                into.Add(0x1161 + s % 588 / 28);
                if (s % 28 != 0)
                    into.Add(0x11A7 + s % 28);
                return;
            }
            ReadOnlySpan<byte> table = CollationData.Decompositions;
            int low = 0, high = table.Length / 15 - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int key = Read3(table, middle * 15);
                if (key == cp)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        int c = Read3(table, middle * 15 + 3 + k * 3);
                        if (c == 0)
                            break;
                        into.Add(c);
                    }
                    return;
                }
                if (key < cp)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            into.Add(cp);
        }

        // A mark's canonical combining class, as a rank (0: a starter).
        private static int Combining(int cp)
        {
            if (cp < 0x300)
                return 0;
            ReadOnlySpan<byte> table = CollationData.CombiningClasses;
            int low = 0, high = table.Length / 4 - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int key = Read3(table, middle * 4);
                if (key == cp)
                    return table[middle * 4 + 3];
                if (key < cp)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return 0;
        }

        // The text's code points, decomposed; with track, where each one's
        // char starts and ends in the text (starts, ends).
        private static void CodePoints(ReadOnlySpan<char> text, ref Buffer into, ref Buffer starts, ref Buffer ends, bool track)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int start = i;
                int c = text[i];
                if (c >= 0xD800 && c <= 0xDBFF && i + 1 < text.Length && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF)
                {
                    c = 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00);
                    i++;
                }
                int before = into.Count;
                if (c < 0xC0)
                    into.Add(c);
                else
                    Decompose(c, ref into);
                if (track)
                {
                    for (int k = before; k < into.Count; k++)
                    {
                        starts.Add(start);
                        ends.Add(i + 1);
                    }
                }
            }
            // Canonical order: marks after a starter, sorted by class (stable).
            for (int i = 1; i < into.Count; i++)
            {
                int c = Combining(into.Items[i]);
                if (c == 0)
                    continue;
                for (int k = i; k > 0; k--)
                {
                    int before = Combining(into.Items[k - 1]);
                    if (before <= c || before == 0)
                        break;
                    Swap(into.Items, k);
                    if (track)
                    {
                        Swap(starts.Items, k);
                        Swap(ends.Items, k);
                    }
                }
            }
        }

        private static void Swap(Span<int> items, int k)
        {
            int t = items[k];
            items[k] = items[k - 1];
            items[k - 1] = t;
        }

        // ---- code points to elements ----

        // The first entry whose first code point is cp, or -1.
        private static int FirstEntry(int[] first, int cp)
        {
            int low = 0, high = first.Length - 1, found = -1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                if (first[middle] < cp)
                    low = middle + 1;
                else
                {
                    if (first[middle] == cp)
                        found = middle;
                    high = middle - 1;
                }
            }
            return found;
        }

        // The longest entry matching the code points at i: how many it
        // covers (0: none), and where it is (at).
        private static int Longest(ReadOnlySpan<byte> entries, int[] first, int[] at, Span<int> cps, int i, int count, out int where)
        {
            where = 0;
            if (first == null)
                return 0;
            int cp = cps[i];
            int found = FirstEntry(first, cp);
            if (found < 0)
                return 0;
            int best = 0;
            for (int e = found; e < first.Length && first[e] == cp; e++)
            {
                int p = at[e];
                int length = entries[p];
                if (length <= best || i + length > count)
                    continue;
                bool same = true;
                for (int k = 1; k < length && same; k++)
                    same = Read3(entries, p + 1 + k * 3) == cps[i + k];
                if (same)
                {
                    best = length;
                    where = p;
                }
            }
            return best;
        }

        // The entry for exactly these code points, or -1.
        private static int Exact(ReadOnlySpan<byte> entries, int[] first, int[] at, ReadOnlySpan<int> key)
        {
            if (first == null)
                return -1;
            int found = FirstEntry(first, key[0]);
            if (found < 0)
                return -1;
            for (int e = found; e < first.Length && first[e] == key[0]; e++)
            {
                int p = at[e];
                if (entries[p] != key.Length)
                    continue;
                bool same = true;
                for (int k = 1; k < key.Length && same; k++)
                    same = Read3(entries, p + 1 + k * 3) == key[k];
                if (same)
                    return p;
            }
            return -1;
        }

        private void AddEntry(ReadOnlySpan<byte> entries, int p, ref Elements into)
        {
            int length = entries[p];
            int n = entries[p + 1 + length * 3];
            for (int k = 0; k < n; k++)
                Add(Element(entries, p + 2 + length * 3 + k * 8), ref into);
        }

        // An element from the tables: a marker becomes the ideograph's rank.
        private void Add(ulong e, ref Elements into)
        {
            if ((e & HanMarker) != 0)
                e = (ulong)(uint)(HanRank((int)(e >> 32), (e >> 8 & 0xFF) == 1) + 1) << 32;
            into.Add(e);
        }

        private static bool RootUnit(int cp, out ulong element)
        {
            ReadOnlySpan<byte> table = CollationData.RootRuns;
            int low = 0, high = table.Length / 13 - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int start = Read3(table, middle * 13);
                if (cp < start)
                    high = middle - 1;
                else if (cp >= start + (table[middle * 13 + 3] | (table[middle * 13 + 4] << 8)))
                    low = middle + 1;
                else
                {
                    element = Element(table, middle * 13 + 5);
                    if (cp != start)
                        element += (ulong)(uint)((cp - start) << 15) << 32;
                    return true;
                }
            }
            element = 0;
            return false;
        }

        // A single code point's elements: ideographs, then the culture's,
        // the root's, then the rest (last, by code point).
        private void Single(int cp, ref Elements into)
        {
            if (IsHan(cp))
            {
                if (_hanja != 0 && Hanja(cp, out int syllable, out int step))
                {
                    // Korean: the syllable of its reading, a secondary step after.
                    int first = into.Count;
                    Buffer jamo = default;
                    jamo.Items = stackalloc int[4];
                    Decompose(syllable, ref jamo);
                    for (int k = 0; k < jamo.Count; k++)
                        Single(jamo.Items[k], ref into);
                    ulong head = into.Items[first];
                    into.Items[first] = (head & ~(0xFFFFUL << 16)) | ((ulong)(uint)(0x1000 + step) << 16);
                    return;
                }
                into.Add(AnchorElement());
                into.Add((ulong)(uint)(HanRank(cp) + 1) << 32);
                return;
            }
            int p = -1;
            if (_tailoringFirst != null)
                p = Exact(Table(0), _tailoringFirst, _tailoringAt, new ReadOnlySpan<int>(&cp, 1));
            if (p >= 0)
            {
                AddEntry(Table(0), p, ref into);
                return;
            }
            p = Exact(Table(1), s_rootFirst, s_rootAt, new ReadOnlySpan<int>(&cp, 1));
            if (p >= 0)
            {
                AddEntry(Table(1), p, ref into);
                return;
            }
            if (RootUnit(cp, out ulong e))
            {
                into.Add(e);
                return;
            }
            into.Add(((ulong)(0xF0000000u + (uint)cp) << 32) | (4UL << 16) | (4UL << 8));
        }

        // Where the Han block goes: the anchor ideograph's element.
        private ulong AnchorElement()
        {
            int cp = CollationData.HanAnchor;
            int p;
            if (_tailoringFirst != null && (p = Exact(Table(0), _tailoringFirst, _tailoringAt, new ReadOnlySpan<int>(&cp, 1))) >= 0)
                return Element(Table(0), p + 2 + Table(0)[p] * 3);
            RootUnit(cp, out ulong e);
            return e;
        }

        private void Collate(ReadOnlySpan<char> text, ref Elements into)
        {
            Buffer none = default;
            Collate(text, ref into, ref none, ref none, false);
        }

        // With track, where each element is in the text, as ICU's collation
        // element iterator tells: low, where its char starts (where the char
        // ends for the second and later elements of a char's, as with an
        // expansion) and high, where it ends.
        private void Collate(ReadOnlySpan<char> text, ref Elements into, ref Buffer lows, ref Buffer highs, bool track)
        {
            Buffer cps = default, starts = default, ends = default;
            cps.Items = stackalloc int[64];
            if (track)
            {
                starts.Items = stackalloc int[64];
                ends.Items = stackalloc int[64];
            }
            CodePoints(text, ref cps, ref starts, ref ends, track);
            Span<int> key = stackalloc int[16];
            for (int i = 0; i < cps.Count; )
            {
                int first = into.Count;
                int which = 0;
                int n = Longest(Table(0), _tailoringFirst, _tailoringAt, cps.Items, i, cps.Count, out int where);
                if (n == 0)
                {
                    which = 1;
                    n = Longest(Table(1), s_rootFirst, s_rootAt, cps.Items, i, cps.Count, out where);
                }
                // Discontiguous contractions: a later mark not blocked by the
                // marks between may extend the match (Finnish u with a
                // diaeresis, after another accent).
                int length = n == 0 ? 1 : n;
                if (length < key.Length && i + length < cps.Count && Combining(cps.Items[i + length]) != 0)
                {
                    for (int k = 0; k < length; k++)
                        key[k] = cps.Items[i + k];
                    int keyLength = length;
                    int blocking = 0;
                    for (int j = i + length; j < cps.Count && keyLength < key.Length; )
                    {
                        int c = Combining(cps.Items[j]);
                        if (c == 0)
                            break;
                        if (c > blocking)
                        {
                            key[keyLength] = cps.Items[j];
                            int p = Exact(Table(0), _tailoringFirst, _tailoringAt, key.Slice(0, keyLength + 1));
                            int from = 0;
                            if (p < 0)
                            {
                                p = Exact(Table(1), s_rootFirst, s_rootAt, key.Slice(0, keyLength + 1));
                                from = 1;
                            }
                            if (p >= 0)
                            {
                                keyLength++;
                                which = from;
                                where = p;
                                n = keyLength;
                                // The mark moves next to the match.
                                int moved = cps.Items[j];
                                for (int k = j; k > i + keyLength - 1; k--)
                                    cps.Items[k] = cps.Items[k - 1];
                                cps.Items[i + keyLength - 1] = moved;
                                if (track)
                                {
                                    int s0 = starts.Items[j], e0 = ends.Items[j];
                                    for (int k = j; k > i + keyLength - 1; k--)
                                    {
                                        starts.Items[k] = starts.Items[k - 1];
                                        ends.Items[k] = ends.Items[k - 1];
                                    }
                                    starts.Items[i + keyLength - 1] = s0;
                                    ends.Items[i + keyLength - 1] = e0;
                                }
                                j++;
                                continue;
                            }
                        }
                        if (c > blocking)
                            blocking = c;
                        j++;
                    }
                }
                if (n > 0)
                    AddEntry(Table(which), where, ref into);
                else
                {
                    Single(cps.Items[i], ref into);
                    n = 1;
                }
                if (track)
                {
                    bool startsChar = i == 0 || starts.Items[i] != starts.Items[i - 1];
                    int end = ends.Items[i + n - 1];
                    for (int k = first; k < into.Count; k++)
                    {
                        lows.Add(k == first && startsChar ? starts.Items[i] : end);
                        highs.Add(end);
                    }
                }
                i += n;
            }
        }

        // ---- Han ideographs ----

        private static bool IsHan(int cp)
            => cp >= 0x4E00 && cp <= 0x9FFF || cp >= 0x3400 && cp <= 0x4DBF || cp >= 0x20000 && cp <= 0x323AF && !(cp >= 0x2F800 && cp <= 0x2FA1F)
                || cp >= 0xFA0E && cp <= 0xFA29 && (cp <= 0xFA0F || cp == 0xFA11 || cp == 0xFA13 || cp == 0xFA14 || cp == 0xFA1F
                    || cp == 0xFA21 || cp == 0xFA23 || cp == 0xFA24 || cp >= 0xFA27);

        // Korean: the syllable a hanja sorts with, and its step after it.
        private bool Hanja(int cp, out int syllable, out int step)
        {
            ReadOnlySpan<byte> table = Blob(CollationData.KoreanHanja, CollationData.KoreanHanjaStarts, _hanja - 1);
            int low = 0, high = table.Length / 6 - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int key = Read3(table, middle * 6);
                if (key == cp)
                {
                    syllable = table[middle * 6 + 3] | (table[middle * 6 + 4] << 8);
                    step = table[middle * 6 + 5];
                    return true;
                }
                if (key < cp)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            syllable = step = 0;
            return false;
        }

        // An ideograph's place in the culture's order: in its own list, or
        // after that in the root order (by radical and strokes), or after all
        // those by code point.
        private int HanRank(int cp, bool rootPosition = false)
        {
            if (_hanOrder != 0 && !rootPosition)
            {
                _ranks ??= new HanRanks(Blob(CollationData.HanOrders, CollationData.HanOrdersStarts, _hanOrder));
                int r = _ranks.Rank(cp);
                if (r >= 0)
                    return r;
            }
            if (_hanOrder != 0)
                _ranks ??= new HanRanks(Blob(CollationData.HanOrders, CollationData.HanOrdersStarts, _hanOrder));
            int skip = _hanOrder != 0 ? _ranks.Count : 0;
            _rootRanks ??= s_rootRanks ??= new HanRanks(Blob(CollationData.HanOrders, CollationData.HanOrdersStarts, 0));
            int root = _rootRanks.Rank(cp);
            return skip + (root >= 0 ? root : _rootRanks.Count + cp);
        }

        // An order of code points, from its runs: each run packed in a ulong
        // (its first code point in bits 40-57, length 20-39, rank 0-19),
        // sorted by code point for a binary search.
        private sealed class HanRanks
        {
            private readonly ulong[] _runs;
            internal readonly int Count;

            internal HanRanks(ReadOnlySpan<byte> runs)
            {
                int n = 0;
                for (int i = 0; i < runs.Length; n++)
                {
                    Varint(runs, ref i);
                    Varint(runs, ref i);
                }
                _runs = new ulong[n];
                int previous = 0, rank = 0, at = 0;
                for (int i = 0; i < runs.Length; at++)
                {
                    long z = (long)Varint(runs, ref i);
                    int length = (int)Varint(runs, ref i);
                    int start = previous + (int)((z >> 1) ^ -(z & 1));
                    _runs[at] = ((ulong)(uint)start << 40) | ((ulong)(uint)length << 20) | (uint)rank;
                    rank += length;
                    previous = start + length - 1;
                }
                Count = rank;
                for (int gap = n / 2; gap > 0; gap /= 2)
                {
                    for (int i = gap; i < n; i++)
                    {
                        ulong v = _runs[i];
                        int j = i;
                        while (j >= gap && _runs[j - gap] > v)
                        {
                            _runs[j] = _runs[j - gap];
                            j -= gap;
                        }
                        _runs[j] = v;
                    }
                }
            }

            private static ulong Varint(ReadOnlySpan<byte> b, ref int i)
            {
                ulong v = 0;
                int shift = 0;
                while (true)
                {
                    byte x = b[i++];
                    v |= (ulong)(x & 0x7F) << shift;
                    if (x < 0x80)
                        return v;
                    shift += 7;
                }
            }

            internal int Rank(int cp)
            {
                int low = 0, high = _runs.Length - 1;
                while (low <= high)
                {
                    int middle = (low + high) >> 1;
                    ulong run = _runs[middle];
                    int start = (int)(run >> 40), length = (int)(run >> 20) & 0xFFFFF;
                    if (cp < start)
                        high = middle - 1;
                    else if (cp >= start + length)
                        low = middle + 1;
                    else
                        return (int)(run & 0xFFFFF) + cp - start;
                }
                return -1;
            }
        }

        // ---- comparing ----

        private static uint Primary(ulong e) => (uint)(e >> 32);
        private static int SecondaryOf(ulong e) => (int)(e >> 16) & 0xFFFF;
        // Danish puts upper case first, before the tertiary differences
        // that aren't case.
        private int TertiaryOf(ulong e)
        {
            int t = (int)(e >> 8) & 0xFF;
            return _upperFirst && (e & Upper) == 0 ? t | 0x100 : t;
        }

        // With IgnoreSymbols: without symbols and punctuation, and the
        // accents on them (zeroed, so the elements keep their places).
        private static void Filter(Span<ulong> e, bool ignoreSymbols)
        {
            if (!ignoreSymbols)
                return;
            bool afterSymbol = false;
            for (int i = 0; i < e.Length; i++)
            {
                ulong x = e[i];
                if ((x & Symbol) != 0 && Primary(x) != 0)
                {
                    afterSymbol = true;
                    e[i] = 0;
                    continue;
                }
                if (Primary(x) == 0 && afterSymbol)
                {
                    e[i] = 0;
                    continue;
                }
                afterSymbol = false;
                if ((x & Symbol) != 0)
                    e[i] = 0;
            }
        }

        internal override int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b, CompareOptions options)
        {
            Load();
            Elements x = default, y = default;
            x.Items = stackalloc ulong[64];
            y.Items = stackalloc ulong[64];
            Collate(a, ref x);
            Collate(b, ref y);
            bool ignoreSymbols = (options & CompareOptions.IgnoreSymbols) != 0;
            Span<ulong> xs = x.Items.Slice(0, x.Count), ys = y.Items.Slice(0, y.Count);
            Filter(xs, ignoreSymbols);
            Filter(ys, ignoreSymbols);
            return Compare(xs, ys, options);
        }

        private int Compare(Span<ulong> x, Span<ulong> y, CompareOptions options)
        {
            // .NET's options other than None and IgnoreSymbols tell kana and
            // widths apart first.
            int order = CompareLevel(x, y, FirstLevel(options));
            if (order != 0)
                return order;
            if ((options & CompareOptions.IgnoreNonSpace) == 0)
            {
                order = _backwards ? CompareBackwards(x, y) : CompareLevel(x, y, 2);
                if (order != 0)
                    return order;
            }
            if ((options & CompareOptions.IgnoreCase) != 0)
                return 0;
            return CompareLevel(x, y, (options & CompareOptions.IgnoreNonSpace) != 0 ? 4 : 3);
        }

        private static int FirstLevel(CompareOptions options)
            => (options & (CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)) != 0 ? 0 : 1;

        // The weight at a level (0: primary with the variant rank; 1: primary;
        // 2: secondary; 3: tertiary; 4: case, whether a letter is upper case),
        // or 0 when the element has none.
        private long Weight(ulong e, int level)
        {
            switch (level)
            {
                case 0: return Primary(e) == 0 ? 0 : ((long)Primary(e) << 4) | (long)(e >> 4 & 0xF);
                case 1: return Primary(e);
                case 2: return SecondaryOf(e);
                case 3: return SecondaryOf(e) == 0 && Primary(e) == 0 ? 0 : TertiaryOf(e);
                default:
                    if (Primary(e) == 0)
                        return 0;
                    bool upper = (e & Upper) != 0;
                    return upper != _upperFirst ? 2 : 1;
            }
        }

        private int CompareLevel(Span<ulong> x, Span<ulong> y, int level)
        {
            int i = 0, j = 0;
            while (true)
            {
                long wx = 0, wy = 0;
                while (i < x.Length && (wx = Weight(x[i], level)) == 0)
                    i++;
                while (j < y.Length && (wy = Weight(y[j], level)) == 0)
                    j++;
                if (i == x.Length || j == y.Length)
                    return i == x.Length ? (j == y.Length ? 0 : -1) : 1;
                if (wx != wy)
                    return wx < wy ? -1 : 1;
                i++;
                j++;
            }
        }

        private static int CompareBackwards(Span<ulong> x, Span<ulong> y)
        {
            int i = x.Length - 1, j = y.Length - 1;
            while (true)
            {
                int wx = 0, wy = 0;
                while (i >= 0 && (wx = SecondaryOf(x[i])) == 0)
                    i--;
                while (j >= 0 && (wy = SecondaryOf(y[j])) == 0)
                    j--;
                if (i < 0 || j < 0)
                    return i < 0 ? (j < 0 ? 0 : -1) : 1;
                if (wx != wy)
                    return wx < wy ? -1 : 1;
                i--;
                j--;
            }
        }

        // A hash of the weights Compare looks at, so texts that compare equal
        // hash the same.
        internal override int GetHashCode(ReadOnlySpan<char> text, CompareOptions options)
        {
            Load();
            Elements x = default;
            x.Items = stackalloc ulong[64];
            Collate(text, ref x);
            Span<ulong> xs = x.Items.Slice(0, x.Count);
            Filter(xs, (options & CompareOptions.IgnoreSymbols) != 0);
            bool ignoreNonSpace = (options & CompareOptions.IgnoreNonSpace) != 0;
            uint hash = Hash(xs, FirstLevel(options), 2166136261);
            if (!ignoreNonSpace)
                hash = Hash(xs, 2, hash);
            if ((options & CompareOptions.IgnoreCase) == 0)
                hash = Hash(xs, ignoreNonSpace ? 4 : 3, hash);
            return (int)hash;
        }

        private uint Hash(Span<ulong> x, int level, uint hash)
        {
            for (int i = 0; i < x.Length; i++)
            {
                long w = Weight(x[i], level);
                if (w != 0)
                    hash = (hash ^ (uint)w ^ (uint)(w >> 32)) * 16777619;
            }
            return (hash ^ 0xFFFF) * 16777619;
        }

        // ---- searching, as .NET does with ICU ----

        // Whether two elements are the same at the strength the options give
        // (ICU compares whole elements when searching, not level by level).
        private bool Same(ulong a, ulong b, CompareOptions options)
        {
            if ((a & Continuation) != (b & Continuation))
                return false;
            int level = FirstLevel(options);
            if (Weight(a, level) != Weight(b, level))
                return false;
            // (Searching has no case level: IgnoreNonSpace ignores case too.)
            if ((options & CompareOptions.IgnoreNonSpace) != 0)
                return true;
            if (SecondaryOf(a) != SecondaryOf(b))
                return false;
            return (options & CompareOptions.IgnoreCase) != 0 || (int)(a >> 8 & 0xFF) == (int)(b >> 8 & 0xFF);
        }

        // An element as ICU's search sees it: 0 (skipped) when it has no
        // weight at the strength, or is a symbol (or an accent after one)
        // with IgnoreSymbols.
        private ulong Processed(ulong e, CompareOptions options, ref bool shifted)
        {
            bool ignoreSymbols = (options & CompareOptions.IgnoreSymbols) != 0;
            if (ignoreSymbols && ((e & Symbol) != 0 && Primary(e) != 0 || shifted && Primary(e) == 0))
            {
                shifted = true;
                // (A symbol whose second element ICU keeps: that stays.)
                return (e & Symbol) != 0 && (e & SearchKept) != 0 ? e : 0;
            }
            if (ignoreSymbols && (e & Symbol) != 0)
            {
                shifted = true;
                return 0;
            }
            shifted = false;
            if ((options & CompareOptions.IgnoreNonSpace) != 0)
                return Primary(e) == 0 ? 0 : e;
            if ((options & CompareOptions.IgnoreCase) != 0)
                return Primary(e) == 0 && SecondaryOf(e) == 0 ? 0 : e;
            return e;
        }

        internal override int Find(ReadOnlySpan<char> source, ReadOnlySpan<char> value, CompareOptions options, Search search, out int matchLength)
        {
            Load();
            Elements v = default, s = default;
            v.Items = stackalloc ulong[32];
            s.Items = stackalloc ulong[64];
            Buffer lows = default, highs = default, none = default;
            lows.Items = stackalloc int[64];
            highs.Items = stackalloc int[64];
            Collate(value, ref v);
            Collate(source, ref s, ref lows, ref highs, true);
            Span<ulong> vs = v.Items.Slice(0, v.Count), ss = s.Items.Slice(0, s.Count);
            Span<int> low = lows.Items.Slice(0, lows.Count), high = highs.Items.Slice(0, highs.Count);
            matchLength = 0;
            if (value.Length == 0)
                return search == Search.Last ? source.Length : search == Search.Suffix ? source.Length : 0;

            if (_asciiOrdinal && (options & CompareOptions.IgnoreSymbols) == 0)
            {
                int quick = Ascii.Find(source, value, (options & CompareOptions.IgnoreCase) != 0, search, out matchLength);
                if (quick != Ascii.Unsure)
                    return quick;
            }

            bool simple = (options & ~CompareOptions.IgnoreCase) == 0;
            if (simple && (search == Search.Prefix || search == Search.Suffix))
                return SimpleAffix(ss, low, high, vs, source.Length, options, search == Search.Prefix, out matchLength);

            if (source.Length == 0)
            {
                // (Prefixes and suffixes: ICU's search fails on empty text.)
                if (search == Search.Prefix || search == Search.Suffix)
                    return -1;
                // ICU can't search empty text: .NET compares instead.
                Elements empty = default;
                Filter(vs, (options & CompareOptions.IgnoreSymbols) != 0);
                return Compare(vs, empty.Items, options) == 0 ? 0 : -1;
            }

            // The value's elements as ICU's search sees them, with the second
            // element of characters ICU weighs with two (and only that, for
            // such a symbol with IgnoreSymbols). Its emptiness test also drops
            // accents with IgnoreSymbols.
            bool ignoreSymbols = (options & CompareOptions.IgnoreSymbols) != 0;
            bool shifted = false, empty2 = true;
            Span<ulong> pv = new ulong[vs.Length * 2];
            int count = 0;
            for (int i = 0; i < vs.Length; i++)
            {
                ulong p = Processed(vs[i], options, ref shifted);
                if (p == 0)
                    continue;
                bool symbolOnly = ignoreSymbols && (p & Symbol) != 0;
                if (!symbolOnly)
                    pv[count++] = p;
                if ((p & SearchKept) != 0)
                    pv[count++] = p | Continuation;
                if (!ignoreSymbols || Primary(p) != 0)
                    empty2 = false;
            }
            vs = pv.Slice(0, count);

            // And the text's, keeping where each is (the second element of a
            // char's, where it ends).
            shifted = false;
            ulong[] ps = new ulong[ss.Length * 2];
            int[] pl = new int[ss.Length * 2], ph = new int[ss.Length * 2];
            int m = 0;
            for (int i = 0; i < ss.Length; i++)
            {
                ulong p = Processed(ss[i], options, ref shifted);
                if (p == 0)
                    continue;
                if (!(ignoreSymbols && (p & Symbol) != 0))
                {
                    ps[m] = p;
                    pl[m] = low[i];
                    ph[m] = high[i];
                    m++;
                }
                if ((p & SearchKept) != 0)
                {
                    ps[m] = p | Continuation;
                    pl[m] = high[i];
                    ph[m] = high[i];
                    m++;
                }
            }
            ss = new Span<ulong>(ps, 0, m);
            low = new Span<int>(pl, 0, m);
            high = new Span<int>(ph, 0, m);

            int index, length = 0;
            bool last = search == Search.Last || search == Search.Suffix;
            if (empty2)
            {
                // An empty pattern: ICU matches it at the start, or at the
                // last char.
                index = !last ? 0 : source.Length - (source.Length >= 2 && char.IsLowSurrogate(source[source.Length - 1])
                    && char.IsHighSurrogate(source[source.Length - 2]) ? 2 : 1);
            }
            else
            {
                index = -1;
                for (int at = 0; at + vs.Length <= ss.Length; at++)
                {
                    int start, limit;
                    if (!MatchAt(ss, low, high, vs, at, source, options, last, out start, out limit))
                        continue;
                    if (!last || start >= index)
                    {
                        index = start;
                        length = limit - start;
                    }
                    if (!last)
                        break;
                }
                if (index < 0)
                    return -1;
            }

            switch (search)
            {
                case Search.Prefix:
                    // Only completely ignorable chars before the match.
                    if (index != 0 && !Ignorable(source.Slice(0, index)))
                        return -1;
                    matchLength = index + length;
                    return 0;
                case Search.Suffix:
                    if (index + length != source.Length && !Ignorable(source.Slice(index + length)))
                        return -1;
                    matchLength = source.Length - index;
                    return index;
                case Search.Last:
                    // .NET: an empty match at the last char is at the end.
                    if (index >= source.Length - 2 && length == 0)
                        index = source.Length;
                    break;
            }
            matchLength = length;
            return index;
        }

        // Whether all of the text's elements are completely ignorable.
        private bool Ignorable(ReadOnlySpan<char> text)
        {
            Elements e = default;
            e.Items = stackalloc ulong[32];
            Collate(text, ref e);
            for (int i = 0; i < e.Count; i++)
            {
                if (e.Items[i] != 0)
                    return false;
            }
            return true;
        }

        // ICU's usearch: the value's elements at element at, then the match
        // widened to grapheme boundaries, refused when that would split a
        // char's elements or take in more than matched.
        private bool MatchAt(Span<ulong> ss, Span<int> low, Span<int> high, Span<ulong> vs, int at,
            ReadOnlySpan<char> text, CompareOptions options, bool backwards, out int start, out int limit)
        {
            start = limit = 0;
            for (int k = 0; k < vs.Length; k++)
            {
                if (!Same(ss[at + k], vs[k], options))
                    return false;
            }
            int lastIx = at + vs.Length - 1, nextIx = at + vs.Length;
            start = low[at];
            int minLimit = low[lastIx];
            int maxLimit = nextIx < ss.Length ? low[nextIx] : text.Length;
            // The next element is the rest of a char's (an expansion).
            if (nextIx < ss.Length && low[nextIx] == high[nextIx])
                return false;
            if (!Graphemes.IsBoundary(text, start) || start == high[at])
                return false;
            // Searching backwards, a match with nothing after it ends at the
            // grapheme boundary after its last element, if not at the end.
            if (backwards && nextIx == ss.Length)
            {
                int after = Graphemes.Next(text, minLimit);
                limit = after > 0 && text.Length > after ? after : text.Length;
                return true;
            }
            limit = maxLimit;
            if (minLimit < maxLimit)
            {
                if (!backwards && minLimit == high[lastIx] && Graphemes.IsBoundary(text, minLimit))
                    limit = minLimit;
                else
                {
                    int next = Graphemes.Next(text, minLimit);
                    if (next >= high[lastIx])
                        limit = next;
                }
            }
            return limit <= maxLimit && Graphemes.IsBoundary(text, limit);
        }

        // .NET's IsPrefix and IsSuffix with None or IgnoreCase: the value's
        // elements against the text's from the start (or the end), skipping
        // completely ignorable ones; a prefix may not end before an accent.
        private int SimpleAffix(Span<ulong> ss, Span<int> low, Span<int> high, Span<ulong> vs, int textLength,
            CompareOptions options, bool forward, out int matchLength)
        {
            int i = 0, j = 0; // the next pattern and source elements to read
            int n = vs.Length, m = ss.Length;
            bool movePattern = true, moveSource = true;
            ulong p = 0, e = 0;
            bool pEnd = false, sEnd = false;
            int captured = 0;
            while (true)
            {
                if (movePattern)
                {
                    pEnd = i >= n;
                    if (!pEnd)
                        p = forward ? vs[i] : vs[n - 1 - i];
                    i++;
                }
                if (moveSource)
                {
                    sEnd = j >= m;
                    // Where the source iterator is before reading.
                    captured = forward ? (sEnd ? textLength : low[j]) : (j == 0 ? textLength : low[m - j]);
                    if (!sEnd)
                        e = forward ? ss[j] : ss[m - 1 - j];
                    j++;
                }
                movePattern = moveSource = true;
                if (pEnd)
                {
                    if (!sEnd && e != 0 && forward && Primary(e) == 0 && SecondaryOf(e) != 0)
                    {
                        matchLength = 0;
                        return -1;
                    }
                    matchLength = forward ? captured : textLength - captured;
                    return forward ? 0 : captured;
                }
                if (p == 0)
                    moveSource = false;
                else if (!sEnd && e == 0)
                    movePattern = false;
                else if (sEnd || !Same(p, e, options))
                {
                    matchLength = 0;
                    return -1;
                }
            }
        }
    }

    // .NET's ordinal shortcuts for English cultures (CompareInfo.Icu.cs),
    // with their quirks: the answer, or Unsure when the text needs ICU.
    internal static class Ascii
    {
        internal const int Unsure = -2;

        // Chars ICU may treat specially: controls (but tab, vertical tab and
        // form feed), DEL and everything past ASCII.
        private static bool Special(int c) => c >= 0x7F || c < 0x20 && c != 9 && c != 11 && c != 12;

        private static bool AllPlain(ReadOnlySpan<char> s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (Special(s[i]))
                    return false;
            }
            return true;
        }

        private static int Upper(int c) => (uint)(c - 'a') <= 'z' - 'a' ? c - 0x20 : c;

        internal static int Find(ReadOnlySpan<char> source, ReadOnlySpan<char> value, bool ignoreCase, Search search, out int matchLength)
        {
            matchLength = 0;
            switch (search)
            {
                case Search.Prefix:
                case Search.Suffix:
                {
                    bool forward = search == Search.Prefix;
                    int length = source.Length < value.Length ? source.Length : value.Length;
                    int a = forward ? 0 : source.Length - 1, b = forward ? 0 : value.Length - 1, step = forward ? 1 : -1;
                    for (; length != 0; length--, a += step, b += step)
                    {
                        int ca = source[a], cb = value[b];
                        if (ca >= 0x80 || cb >= 0x80 || Special(ca) || Special(cb))
                            return Unsure;
                        if (ca == cb || ignoreCase && Upper(ca) == Upper(cb))
                            continue;
                        // A special char next may change the answer.
                        if (forward ? a < source.Length - 1 && source[a + 1] >= 0x80 : a > 0 && source[a - 1] >= 0x80)
                            return Unsure;
                        if (forward ? b < value.Length - 1 && value[b + 1] >= 0x80 : b > 0 && value[b - 1] >= 0x80)
                            return Unsure;
                        return -1;
                    }
                    if (source.Length < value.Length)
                        return Special(value[b]) ? Unsure : -1;
                    if (source.Length > value.Length && Special(source[a]))
                        return Unsure;
                    matchLength = value.Length;
                    return forward ? 0 : source.Length - value.Length;
                }
                default:
                {
                    if (!AllPlain(value))
                        return Unsure;
                    if (value.Length > source.Length)
                        return ignoreCase && !AllPlain(source) ? Unsure : -1;
                    bool forward = search == Search.First;
                    int start = forward ? 0 : source.Length - value.Length, end = forward ? source.Length - value.Length + 1 : -1, step = forward ? 1 : -1;
                    for (int i = start; i != end; i += step)
                    {
                        int k = 0, at = i;
                        bool next = false;
                        for (; k < value.Length; k++, at++)
                        {
                            int c = source[at], t = value[k];
                            if (Special(c))
                                return Unsure;
                            if (c == t || ignoreCase && Upper(c) == Upper(t))
                                continue;
                            if (at < source.Length - 1 && source[at + 1] >= 0x80)
                                return Unsure;
                            next = true;
                            break;
                        }
                        if (next)
                            continue;
                        if (at < source.Length && source[at] >= 0x80)
                            return Unsure;
                        matchLength = value.Length;
                        return i;
                    }
                    // (Only the ignore-case search checks the rest.)
                    if (ignoreCase && !AllPlain(forward ? source.Slice(end) : source.Slice(0, start)))
                        return Unsure;
                    return -1;
                }
            }
        }
    }

    // Grapheme cluster boundaries, as the break iterator .NET gives ICU's
    // search (Unicode's rules, without the one keeping CR LF together).
    internal static class Graphemes
    {
        private enum Kind { Other, Control, Extend, SpacingMark, Prepend, L, V, T, LV, LVT, ZWJ, RegionalIndicator }

        private static int CodePointAt(ReadOnlySpan<char> text, int i)
        {
            int c = text[i];
            if (c >= 0xD800 && c <= 0xDBFF && i + 1 < text.Length && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF)
                return 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00);
            return c;
        }

        private static int CodePointBefore(ReadOnlySpan<char> text, int i, out int start)
        {
            start = i - 1;
            int c = text[i - 1];
            if (c >= 0xDC00 && c <= 0xDFFF && i >= 2 && text[i - 2] >= 0xD800 && text[i - 2] <= 0xDBFF)
            {
                start = i - 2;
                return 0x10000 + ((text[i - 2] - 0xD800) << 10) + (c - 0xDC00);
            }
            return c;
        }

        private static Kind KindOf(int cp)
        {
            if (cp == 0x200D)
                return Kind.ZWJ;
            if (cp == 0x200C || cp >= 0xFF9E && cp <= 0xFF9F || cp >= 0x1F3FB && cp <= 0x1F3FF || cp >= 0xE0020 && cp <= 0xE007F)
                return Kind.Extend;
            if (cp >= 0x1100 && cp <= 0x115F || cp >= 0xA960 && cp <= 0xA97C)
                return Kind.L;
            if (cp >= 0x1160 && cp <= 0x11A7 || cp >= 0xD7B0 && cp <= 0xD7C6)
                return Kind.V;
            if (cp >= 0x11A8 && cp <= 0x11FF || cp >= 0xD7CB && cp <= 0xD7FB)
                return Kind.T;
            if (cp >= 0xAC00 && cp <= 0xD7A3)
                return (cp - 0xAC00) % 28 == 0 ? Kind.LV : Kind.LVT;
            if (cp >= 0x1F1E6 && cp <= 0x1F1FF)
                return Kind.RegionalIndicator;
            if (cp >= 0x0600 && cp <= 0x0605 || cp == 0x06DD || cp == 0x070F || cp == 0x0890 || cp == 0x0891 || cp == 0x08E2 || cp == 0x0D4E
                || cp == 0x110BD || cp == 0x110CD || cp == 0x111C2 || cp == 0x111C3 || cp == 0x1193F || cp == 0x11941 || cp == 0x11A3A
                || cp >= 0x11A84 && cp <= 0x11A89 || cp == 0x11D46 || cp == 0x11F02)
                return Kind.Prepend;
            switch (CharUnicodeInfo.GetUnicodeCategory(cp))
            {
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                    return Kind.Extend;
                case UnicodeCategory.SpacingCombiningMark:
                    // (Some vowel signs a cluster doesn't keep: Myanmar's and others.)
                    if (cp == 0x102B || cp == 0x102C || cp == 0x1038 || cp >= 0x1062 && cp <= 0x1064 || cp >= 0x1067 && cp <= 0x106D
                        || cp == 0x1083 || cp >= 0x1087 && cp <= 0x108C || cp == 0x108F || cp >= 0x109A && cp <= 0x109C || cp == 0x1A61
                        || cp == 0x1A63 || cp == 0x1A64 || cp == 0xAA7B || cp == 0xAA7D || cp == 0x11720 || cp == 0x11721)
                        return Kind.Other;
                    return Kind.SpacingMark;
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                    return Kind.Control;
                default:
                    return Kind.Other;
            }
        }

        // Emoji and other pictographs (approximately Extended_Pictographic).
        private static bool Pictographic(int cp)
            => cp == 0x00A9 || cp == 0x00AE || cp == 0x203C || cp == 0x2049 || cp == 0x2122 || cp == 0x2139 || cp >= 0x2194 && cp <= 0x21AA
                || cp >= 0x2300 && cp <= 0x23FF || cp >= 0x24C2 && cp <= 0x24C2 || cp >= 0x25AA && cp <= 0x27BF || cp >= 0x2934 && cp <= 0x2935
                || cp >= 0x2B05 && cp <= 0x2B55 || cp == 0x3030 || cp == 0x303D || cp == 0x3297 || cp == 0x3299
                || cp >= 0x1F000 && cp <= 0x1FAFF && !(cp >= 0x1F1E6 && cp <= 0x1F1FF) && !(cp >= 0x1F3FB && cp <= 0x1F3FF) || cp >= 0x1FC00 && cp <= 0x1FFFD;

        private static bool Virama(int cp) => cp == 0x094D || cp == 0x09CD || cp == 0x0ACD || cp == 0x0B4D || cp == 0x0C4D || cp == 0x0D4D;

        // A consonant that joins through a virama (Devanagari, Bengali,
        // Gujarati, Oriya, Telugu, Malayalam).
        private static bool LinkingConsonant(int cp)
            => cp >= 0x0915 && cp <= 0x0939 || cp >= 0x0958 && cp <= 0x095F || cp >= 0x0978 && cp <= 0x097F
                || cp >= 0x0995 && cp <= 0x09A8 || cp >= 0x09AA && cp <= 0x09B0 || cp == 0x09B2 || cp >= 0x09B6 && cp <= 0x09B9 || cp >= 0x09DC && cp <= 0x09DD || cp == 0x09DF || cp >= 0x09F0 && cp <= 0x09F1
                || cp >= 0x0A95 && cp <= 0x0AA8 || cp >= 0x0AAA && cp <= 0x0AB0 || cp >= 0x0AB2 && cp <= 0x0AB3 || cp >= 0x0AB5 && cp <= 0x0AB9 || cp == 0x0AF9
                || cp >= 0x0B15 && cp <= 0x0B28 || cp >= 0x0B2A && cp <= 0x0B30 || cp >= 0x0B32 && cp <= 0x0B33 || cp >= 0x0B35 && cp <= 0x0B39 || cp >= 0x0B5C && cp <= 0x0B5D || cp == 0x0B5F || cp == 0x0B71
                || cp >= 0x0C15 && cp <= 0x0C28 || cp >= 0x0C2A && cp <= 0x0C39 || cp >= 0x0C58 && cp <= 0x0C5A
                || cp >= 0x0D15 && cp <= 0x0D3A;

        internal static bool IsBoundary(ReadOnlySpan<char> text, int i)
        {
            if (i <= 0 || i >= text.Length)
                return true;
            if (char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]))
                return false;
            int beforeCp = CodePointBefore(text, i, out int previousStart), afterCp = CodePointAt(text, i);
            Kind before = KindOf(beforeCp), after = KindOf(afterCp);
            if (before == Kind.Control || after == Kind.Control)
                return true;
            if (before == Kind.L && (after == Kind.L || after == Kind.V || after == Kind.LV || after == Kind.LVT))
                return false;
            if ((before == Kind.LV || before == Kind.V) && (after == Kind.V || after == Kind.T))
                return false;
            if ((before == Kind.LVT || before == Kind.T) && after == Kind.T)
                return false;
            if (after == Kind.Extend || after == Kind.ZWJ || after == Kind.SpacingMark || before == Kind.Prepend)
                return false;
            if (LinkingConsonant(afterCp))
            {
                // A consonant, a virama (with marks) and a consonant: one cluster.
                bool virama = false;
                for (int k = i; k > 0; )
                {
                    int c = CodePointBefore(text, k, out int s);
                    if (Virama(c))
                        virama = true;
                    else if (LinkingConsonant(c))
                        return !virama;
                    else if (KindOf(c) != Kind.Extend && KindOf(c) != Kind.ZWJ)
                        break;
                    k = s;
                }
            }
            if (before == Kind.ZWJ && Pictographic(afterCp))
            {
                // A pictograph, marks, a zero-width joiner and a pictograph.
                for (int k = previousStart; k > 0; )
                {
                    int c = CodePointBefore(text, k, out int s);
                    if (Pictographic(c))
                        return false;
                    if (KindOf(c) != Kind.Extend)
                        break;
                    k = s;
                }
            }
            if (before == Kind.RegionalIndicator && after == Kind.RegionalIndicator)
            {
                // Pairs: count the indicators before.
                int count = 0;
                for (int k = i; k > 0; )
                {
                    if (KindOf(CodePointBefore(text, k, out int s)) != Kind.RegionalIndicator)
                        break;
                    count++;
                    k = s;
                }
                return count % 2 == 0;
            }
            return true;
        }

        // The first boundary after i.
        internal static int Next(ReadOnlySpan<char> text, int i)
        {
            for (int k = i + 1; k < text.Length; k++)
            {
                if (IsBoundary(text, k))
                    return k;
            }
            return text.Length;
        }
    }
}
