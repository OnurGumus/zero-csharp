// Writes zerocore/System/Globalization/CollationData.cs: how each culture
// sorts text, as weights in the Unicode Collation Algorithm's shape (a
// primary weight for the letter, a secondary for accents, a tertiary for
// case and variants), derived from .NET's own ICU comparisons: every
// assigned character is sorted with CompareInfo at each strength, for the
// root order and for each culture, and a culture keeps only where it
// differs.
//
// Code points: single ones, or a few in a row (a contraction: Turkish ç is
// c + U+0327, a letter of its own) with one or more collation elements (an
// expansion: ß sorts as ss). Precomposed characters and Hangul syllables are
// decomposed (NFD) at run time, so only their decompositions are listed.
// Han ideographs are a block: one element for where the block goes, then
// the ideograph's rank in the culture's order (pinyin, strokes, Japanese
// order; Korean puts many with the Hangul syllable of their reading).

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

static class Collation
{
    const CompareOptions Primary = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
    const CompareOptions Variant = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    const CompareOptions Secondary = CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
    const long Spacing = 1 << 15;

    // A collation element: weights and flags (1: a symbol or punctuation
    // mark, which IgnoreSymbols drops; 2: upper case, for the case level;
    // bits 4-7: kana and width variants, see below).
    record struct Element(long P, int S, int T, int F);

    sealed class Table
    {
        public Dictionary<string, Element[]> Map = new(StringComparer.Ordinal);
        public bool Backwards;
        public long[] GroupWeights = [];
        public List<List<string>> Groups = [];
    }

    // Unified ideographs among the compatibility ones.
    static readonly int[] UnifiedCompatibility = [0xFA0E, 0xFA0F, 0xFA11, 0xFA13, 0xFA14, 0xFA1F, 0xFA21, 0xFA23, 0xFA24, 0xFA27, 0xFA28, 0xFA29];

    // Han ideographs, and where ICU's root order puts them: the main block
    // and the unified compatibility ones, then extension A, then the rest,
    // each in code point order.
    internal static int HanBase(int cp)
    {
        if (cp >= 0x4E00 && cp <= 0x9FFF || Array.IndexOf(UnifiedCompatibility, cp) >= 0)
            return 0;
        if (cp >= 0x3400 && cp <= 0x4DBF)
            return 1;
        // (Not the compatibility ones, which decompose to the others.)
        if (cp >= 0x20000 && cp <= 0x323AF && !(cp >= 0x2F800 && cp <= 0x2FA1F))
            return 2;
        return -1;
    }

    static bool IsHan(int cp) => HanBase(cp) >= 0 && Assigned(cp);

    static long ImplicitKey(int cp) => ((long)HanBase(cp) << 21) + cp;

    static bool Assigned(int cp)
    {
        var c = CharUnicodeInfo.GetUnicodeCategory(cp);
        return c != UnicodeCategory.OtherNotAssigned && c != UnicodeCategory.PrivateUse && c != UnicodeCategory.Surrogate;
    }

    static string S(int cp) => char.ConvertFromUtf32(cp);

    // Not Han, but sorted in the Han block (radicals).
    static HashSet<int> s_blockMembers = [];
    static List<int> s_rootBlock = [];

    // The ideograph the root order sorts u with, or -1.
    static int RootVariantOf(string u)
    {
        var rootCi = CultureInfo.InvariantCulture.CompareInfo;
        var block = s_rootBlock;
        if (rootCi.Compare(u, S(block[0]), Primary) < 0 || rootCi.Compare(u, S(block[^1]), Primary) > 0)
            return -1;
        int low = 0, high = block.Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (rootCi.Compare(S(block[middle]), u, Primary) < 0)
                low = middle + 1;
            else
                high = middle;
        }
        return rootCi.Compare(S(block[low]), u, Primary) == 0 ? block[low] : -1;
    }

    static List<int> Repertoire(int hanAnchor)
    {
        var list = new List<int>();
        for (int c = 0; c <= 0x10FFFF; c++)
        {
            if (c >= 0xD800 && c <= 0xDFFF || !Assigned(c) || HanBase(c) >= 0 || c >= 0xAC00 && c <= 0xD7A3 || s_blockMembers.Contains(c))
                continue;
            // Compatibility ideographs decompose to unified ones.
            if (c >= 0xF900 && c <= 0xFAFF || c >= 0x2F800 && c <= 0x2FA1F)
                continue;
            list.Add(c);
        }
        list.Add(hanAnchor);
        return list;
    }

    static int Flags(CompareInfo ci, string s)
    {
        int f = 0;
        if (ci.Compare("x" + s + "b", "xb", CompareOptions.IgnoreSymbols) == 0)
        {
            f |= 1;
            // 4: a symbol ICU weighs with two elements, the second of which
            // its search keeps with IgnoreSymbols (a match can't end before it).
            if (ci.Compare("x" + s + "b", "xb", CompareOptions.None) != 0 && ci.IndexOf("x" + s + "y", "x", CompareOptions.IgnoreSymbols) < 0)
                f |= 4;
        }
        else if (ci.Compare("x" + s + "b", "xb", CompareOptions.None) != 0)
        {
            // 4 too: a character ICU weighs with two elements (a long primary
            // weight), whose second ends a backward match at the char's end.
            if (ci.LastIndexOf(s + "!y", s, CompareOptions.IgnoreSymbols, out int length) == 0 && length == s.Length + 1)
                f |= 4;
        }
        if (s.Length > 0 && char.IsUpper(s, 0))
            f |= 2;
        return f;
    }

    static bool IsContraction(CompareInfo ci, string s, CompareOptions options = Primary)
        => ci.Compare(s, s[..^1] + "\u0001" + s[^1..], options) != 0;

    // Letters that may begin or continue a contraction of letters: Latin
    // ones (Czech ch, Danish aa, Hungarian dzs) and the vowels written
    // before their consonant in Thai, Lao and others (ICU sorts them after).
    static List<string> ContractionLetters()
    {
        var letters = new List<string>();
        for (int c = 'a'; c <= 0x24F; c++)
        {
            if ((c <= 'z' || c >= 0xDF) && char.IsLower((char)c) && S(c).Normalize(NormalizationForm.FormD).Length == 1)
                letters.Add(S(c));
        }
        return letters;
    }

    static readonly (int From, int To)[] PrevowelRanges = [(0x0E40, 0x0E44), (0x0EC0, 0x0EC4), (0x19B5, 0x19B7), (0x19BA, 0x19BA), (0xAAB5, 0xAAB6), (0xAAB9, 0xAAB9), (0xAABB, 0xAABC)];
    static readonly (int From, int To)[] ConsonantRanges = [(0x0E01, 0x0E2E), (0x0E81, 0x0EAE), (0x1980, 0x19AB), (0xAA80, 0xAAAF)];

    static Table Compute(CompareInfo ci, List<int> repertoire, Table root, HanOrder han, int anchor)
    {
        var table = new Table();
        var units = new List<string>();
        var unitSet = new HashSet<string>(StringComparer.Ordinal);
        var marks = new List<string>();
        var expansions = new List<(string Unit, string As, CompareInfo Ci)>();
        void AddUnit(string u)
        {
            if (unitSet.Add(u))
                units.Add(u);
        }
        foreach (int cp in repertoire)
        {
            string s = S(cp);
            string d = s.Normalize(NormalizationForm.FormD);
            if (d != s)
            {
                // A contraction when the decomposed letter doesn't sort as its
                // parts do (at any level: Arabic tailors alef with hamza as an
                // accent of hamza).
                if (d.Length > 1 && IsContraction(ci, d, CompareOptions.None))
                    AddUnit(d);
                continue;
            }
            if (ci.Compare("x" + s + "b", "xb", CompareOptions.None) == 0)
            {
                table.Map[s] = [new Element(0, 0, 0, 0)];
                continue;
            }
            // (On x: in Swedish a and o with a diaeresis are letters of their own.)
            if (ci.Compare("x" + s, "x", Primary) == 0)
            {
                marks.Add(s);
                continue;
            }
            string compat = s.Normalize(NormalizationForm.FormKD);
            string special = s switch { "ß" => "ss", "æ" => "ae", "Æ" => "AE", "œ" => "oe", "Œ" => "OE", _ => null };
            string expansion = null;
            var expansionCi = ci;
            foreach (string candidate in new[] { compat, special })
            {
                if (candidate == null || candidate.Length <= 1 || candidate == s)
                    continue;
                if (ci.Compare(s, candidate, Primary) == 0)
                {
                    expansion = candidate;
                    break;
                }
                // (Korean moves ideographs to their Hangul reading, but not
                // the compatibility characters made of them.)
                if (Key(candidate).Any(IsHan) && CultureInfo.InvariantCulture.CompareInfo.Compare(s, candidate, Primary) == 0)
                {
                    expansion = candidate;
                    expansionCi = CultureInfo.InvariantCulture.CompareInfo;
                    break;
                }
            }
            if (expansion != null)
                expansions.Add((s, expansion, expansionCi));
            else
                AddUnit(s);
        }

        // Contractions of letters: letters that sort differently when a
        // completely ignorable character (U+0001) separates them.
        var letters = ContractionLetters();
        var found = new List<string>();
        foreach (string x in letters)
        {
            foreach (string y in letters)
            {
                if (IsContraction(ci, x + y))
                    found.Add(x + y);
            }
        }
        foreach (string xy in found.ToArray())
        {
            foreach (string z in letters)
            {
                if (IsContraction(ci, xy + z))
                    found.Add(xy + z);
            }
        }
        foreach (string lower in found)
        {
            // And in each mix of cases.
            for (int mask = 0; mask < 1 << lower.Length; mask++)
            {
                var chars = lower.ToCharArray();
                for (int k = 0; k < chars.Length; k++)
                {
                    if ((mask & (1 << k)) != 0)
                        chars[k] = char.ToUpperInvariant(chars[k]);
                }
                string variant = new string(chars);
                if (mask == 0 || IsContraction(ci, variant))
                    AddUnit(variant);
            }
        }
        foreach (var (pf, pt) in PrevowelRanges)
        {
            for (int p = pf; p <= pt; p++)
            {
                foreach (var (cf, ct) in ConsonantRanges)
                {
                    for (int c = cf; c <= ct; c++)
                    {
                        if (Assigned(c) && Assigned(p) && IsContraction(ci, S(p) + S(c), CompareOptions.None))
                            AddUnit(S(p) + S(c));
                    }
                }
            }
        }

        // Characters that sort with a Han ideograph (Kangxi radicals, circled
        // ideographs): that ideograph's elements, a step after it.
        var hanVariants = new List<(string Unit, int Han, CompareInfo Ci)>();
        string hanFirst = S(han.Block[0]), hanLast = S(han.Block[^1]);
        var members = new List<string>();
        foreach (string u in units.ToArray())
        {
            if (Key(u).Length == 1 && han.Members.Contains(Key(u)[0]))
            {
                members.Add(u);
                units.Remove(u);
                continue;
            }
            if (u == S(anchor) || ci.Compare(u, hanFirst, Primary) < 0 || ci.Compare(u, hanLast, Primary) > 0)
                continue;
            int low = 0, high = han.Block.Count - 1;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (ci.Compare(S(han.Block[middle]), u, Primary) < 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            var hci = ci;
            int h = han.Block[low];
            if (ci.Compare(S(h), u, Primary) != 0)
            {
                // Korean moves some ideographs to their Hangul reading but
                // leaves their variants where the root order has them.
                var rootCi = CultureInfo.InvariantCulture.CompareInfo;
                var rootBlock = s_rootBlock;
                low = 0;
                high = rootBlock.Count - 1;
                while (low < high)
                {
                    int middle = (low + high) / 2;
                    if (rootCi.Compare(S(rootBlock[middle]), u, Primary) < 0)
                        low = middle + 1;
                    else
                        high = middle;
                }
                h = rootBlock[low];
                if (rootCi.Compare(S(h), u, Primary) != 0)
                    throw new Exception($"{ci.Name}: U+{(int)u[0]:X4} sorts among the Han ideographs");
                hci = rootCi;
            }
            hanVariants.Add((u, h, hci));
            units.Remove(u);
        }

        // Primary weights: groups of letters that sort the same.
        units.Sort((x, y) => ci.Compare(x, y, Primary) is int c && c != 0 ? c : string.CompareOrdinal(x, y));
        var groups = new List<List<string>>();
        foreach (string u in units)
        {
            if (groups.Count > 0 && ci.Compare(groups[^1][0], u, Primary) == 0)
                groups[^1].Add(u);
            else
                groups.Add([u]);
        }
        long[] weights = new long[groups.Count];
        if (root == null)
        {
            for (int g = 0; g < groups.Count; g++)
                weights[g] = (g + 1) * Spacing;
        }
        else
        {
            AssignAgainstRoot(groups, root, weights);
        }
        table.Groups = groups;
        table.GroupWeights = weights;

        // Secondary: marks by their own order; letters by their place in their group.
        marks.Sort((x, y) => ci.Compare("x" + x, "x" + y, Secondary) is int c && c != 0 ? c : string.CompareOrdinal(x, y));
        int markRank = 0;
        for (int i = 0; i < marks.Count; i++)
        {
            if (i > 0 && ci.Compare("x" + marks[i - 1], "x" + marks[i], Secondary) != 0)
                markRank++;
            table.Map[marks[i]] = [new Element(0, 0x2000 + markRank * 4, 4, Flags(ci, marks[i]))];
        }
        if (0x2000 + markRank * 4 > 0xFFFF)
            throw new Exception("too many secondary weights");
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            group.Sort((x, y) => ci.Compare(x, y, CompareOptions.None) is int c && c != 0 ? c : string.CompareOrdinal(x, y));
            int secondaryRank = 0, tertiaryRank = 0;
            for (int i = 0; i < group.Count; i++)
            {
                if (i > 0)
                {
                    if (ci.Compare(group[i - 1], group[i], Secondary) != 0)
                    {
                        secondaryRank++;
                        tertiaryRank = 0;
                    }
                    else if (ci.Compare(group[i - 1], group[i], CompareOptions.None) != 0)
                    {
                        tertiaryRank++;
                    }
                }
                if (4 + tertiaryRank * 4 > 250 || 4 + secondaryRank * 4 >= 0x2000)
                    throw new Exception($"{group[i]}: too many variants");
                table.Map[group[i]] = [new Element(weights[g], 4 + secondaryRank * 4, 4 + tertiaryRank * 4, Flags(ci, group[i]))];
            }

            // .NET's own rules for the options besides None and IgnoreSymbols
            // make hiragana and katakana, and full and half width, differ at
            // the first level: a rank within the group for those (flags bits 4-7).
            if (group.Count == 1)
                continue;
            var variants = group.ToList();
            variants.Sort((x, y) => ci.Compare(x, y, Variant) is int c && c != 0 ? c : ci.Compare(x, y, CompareOptions.None));
            int variantRank = 0;
            for (int i = 0; i < variants.Count; i++)
            {
                if (i > 0 && ci.Compare(variants[i - 1], variants[i], Variant) != 0)
                    variantRank++;
                if (variantRank > 15)
                    throw new Exception($"{variants[i]}: too many variants");
                var e = table.Map[variants[i]][0];
                table.Map[variants[i]] = [e with { F = e.F | (variantRank << 4) }];
            }
        }

        // Letters ICU weighs as a base letter and an accent (d with a stroke
        // is d and U+0335): two elements, as matches in search end there.
        var strokeMarks = marks.Where(m => m[0] >= 0x0300 && m[0] <= 0x036F || m[0] >= 0x1DC0 && m[0] <= 0x1DFF || m[0] >= 0x20D0 && m[0] <= 0x20FF).ToList();
        foreach (var group in groups)
        {
            foreach (string u in group)
            {
                var e = table.Map[u];
                if (e.Length != 1 || e[0].S == 4 || u.Length != 1)
                    continue;
                string b = group.Where(x => x.Length == 1 && table.Map[x][0].S == 4 && (table.Map[x][0].F & 2) == (e[0].F & 2))
                    .OrderBy(x => table.Map[x][0].T).FirstOrDefault();
                if (b == null)
                    continue;
                string m = strokeMarks.FirstOrDefault(m => ci.Compare(u, b + m, CompareOptions.None) == 0);
                if (m != null)
                    table.Map[u] = [table.Map[b][0], table.Map[m][0]];
            }
        }

        // Expansions: the elements of what they sort as, nudged at the level
        // where they differ (every element, for a tertiary difference: ICU
        // marks each of æ's elements as a variant). Their parts may be
        // expansions too, so until nothing changes.
        var pending = expansions;
        while (pending.Count > 0)
        {
            var later = new List<(string Unit, string As, CompareInfo Ci)>();
            var waiting = new HashSet<string>(pending.Select(p => p.Unit), StringComparer.Ordinal);
            foreach (var (unit, expansion, eci) in pending)
            {
                var elements = new List<Element>();
                bool missing = false;
                for (int i = 0; i < expansion.Length; i += char.IsSurrogatePair(expansion, i) ? 2 : 1)
                {
                    int partCp = char.ConvertToUtf32(expansion, i);
                    string part = S(partCp);
                    if (IsHan(partCp) || s_blockMembers.Contains(partCp))
                        elements.AddRange([table.Map[S(anchor)][0], new Element(partCp, 0, eci == ci ? 0 : 1, 8)]);
                    else if (table.Map.TryGetValue(part, out var e))
                        elements.AddRange(e);
                    else if (waiting.Contains(part))
                        missing = true;
                }
                if (missing)
                {
                    later.Add((unit, expansion, eci));
                    continue;
                }
                if (elements.Count == 0)
                    continue;
                int secondary = eci.Compare(unit, expansion, Secondary), tertiary = eci.Compare(unit, expansion, CompareOptions.None);
                if (secondary != 0)
                    elements[0] = elements[0] with { S = elements[0].S + Math.Sign(secondary) * 2 };
                else if (tertiary != 0)
                {
                    for (int k = 0; k < elements.Count; k++)
                        elements[k] = elements[k] with { T = elements[k].T + Math.Sign(tertiary) * 2 };
                }
                // (The parts keep their own flags; the case is the character's.)
                elements[0] = elements[0] with { F = (elements[0].F & ~2) | (Flags(ci, unit) & 2) };
                table.Map[unit] = elements.ToArray();
            }
            if (later.Count == pending.Count)
                throw new Exception("expansions refer to each other");
            pending = later;
        }

        // The Han variants: the anchor's element with a step (secondary or
        // tertiary, by how they differ), then a marker (flag 8) for the
        // ideograph whose rank the run time puts there.
        var anchorElement = table.Map[S(anchor)][0];
        foreach (string u in members)
            table.Map[u] = [anchorElement with { F = Flags(ci, u) }, new Element(Key(u)[0], 0, 0, 8)];
        foreach (var group in hanVariants.GroupBy(v => v.Han))
        {
            string h = S(group.Key);
            var ci0 = ci;
            ci = group.First().Ci;
            var list = group.Select(v => v.Unit).ToList();
            list.Sort((a, b) => ci.Compare(a, b));
            foreach (string u in list)
            {
                int secondaryStep = list.Count(x => ci.Compare(x, u, Secondary) < 0 && ci.Compare(x, h, Secondary) != 0) + (ci.Compare(u, h, Secondary) != 0 ? 1 : 0);
                int tertiaryStep = list.Count(x => ci.Compare(x, u, CompareOptions.None) < 0 && ci.Compare(x, u, Secondary) == 0 && ci.Compare(x, h, CompareOptions.None) != 0) + 1;
                var first = ci.Compare(u, h, Secondary) != 0
                    ? anchorElement with { S = 0x1000 + secondaryStep, F = Flags(ci, u) }
                    : anchorElement with { T = anchorElement.T + tertiaryStep, F = Flags(ci, u) };
                if (ci.Compare(u, h, Secondary) < 0 || ci.Compare(u, h, CompareOptions.None) < 0)
                    throw new Exception($"U+{(int)u[0]:X4} sorts before its ideograph");
                // (T 1: the ideograph's root position, when the culture moved
                // the ideograph but not this.)
                table.Map[u] = [first, new Element(group.Key, 0, ci == ci0 ? 0 : 1, 8)];
            }
            ci = ci0;
        }

        // French (Canada) compares accents from the end of the word.
        table.Backwards = root != null && Math.Sign(ci.Compare("coté", "côte")) != Math.Sign(CultureInfo.InvariantCulture.CompareInfo.Compare("coté", "côte"));
        return table;
    }

    // A culture's primary weights: its groups in its order, keeping the root
    // order's weight wherever the longest run of groups in root order allows,
    // and weights in the gaps for the rest.
    static void AssignAgainstRoot(List<List<string>> groups, Table root, long[] weights)
    {
        int n = groups.Count;
        long[] rootWeight = new long[n];
        for (int g = 0; g < n; g++)
        {
            rootWeight[g] = -1;
            foreach (string u in groups[g])
            {
                if (root.Map.TryGetValue(u, out var e) && e.Length == 1 && e[0].P > 0)
                    rootWeight[g] = rootWeight[g] < 0 ? e[0].P : Math.Min(rootWeight[g], e[0].P);
            }
        }
        // Longest strictly increasing run of root weights (patience sorting).
        var tails = new List<int>();
        int[] previous = new int[n];
        for (int i = 0; i < n; i++)
        {
            previous[i] = -1;
            if (rootWeight[i] < 0)
                continue;
            int low = 0, high = tails.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (rootWeight[tails[middle]] < rootWeight[i])
                    low = middle + 1;
                else
                    high = middle;
            }
            if (low > 0)
                previous[i] = tails[low - 1];
            if (low == tails.Count)
                tails.Add(i);
            else
                tails[low] = i;
        }
        bool[] kept = new bool[n];
        for (int i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i])
            kept[i] = true;
        for (int g = 0; g < n; g++)
        {
            if (kept[g])
                weights[g] = rootWeight[g];
        }
        int start = 0;
        while (start < n)
        {
            if (kept[start])
            {
                start++;
                continue;
            }
            int end = start;
            while (end < n && !kept[end])
                end++;
            long low = start > 0 ? weights[start - 1] : 0;
            long high = end < n ? weights[end] : low + (end - start + 1) * Spacing;
            int count = end - start;
            if (high - low <= count)
                throw new Exception($"no room for {count} groups between {low} and {high}");
            for (int k = 0; k < count; k++)
                weights[start + k] = low + (high - low) * (k + 1) / (count + 1);
            start = end;
        }
    }

    // ---- Han ideographs ----

    sealed class HanOrder
    {
        public List<int> Explicit = []; // the first ones, in order; the rest follow in root order
        public List<int> Block = []; // all, in order
        public HashSet<int> Members = []; // not Han, but the culture sorts them among the ideographs
        public List<(int Hanja, int Syllable, int Step)> Korean = []; // with a Hangul syllable, a secondary step after it
        public int First, Last; // of the block
        public HashSet<int> Tail = [];
    }

    static HanOrder ComputeHan(CompareInfo ci, List<int> han)
    {
        var order = new HanOrder();
        var block = new List<int>();
        var hangulFirst = "가";
        var hangulLast = "힣";
        var withSyllable = new List<int>();
        foreach (int h in han)
        {
            string s = S(h);
            if (ci.Compare(s, hangulFirst, Primary) >= 0 && ci.Compare(s, hangulLast, Primary) <= 0)
                withSyllable.Add(h);
            else
                block.Add(h);
        }
        if (withSyllable.Count > 0)
        {
            // Korean: the syllable each sorts with, and its step after it.
            var bySyllable = new SortedDictionary<int, List<int>>();
            foreach (int h in withSyllable)
            {
                int low = 0xAC00, high = 0xD7A3;
                string s = S(h);
                while (low < high)
                {
                    int middle = (low + high) / 2;
                    if (ci.Compare(S(middle), s, Primary) < 0)
                        low = middle + 1;
                    else
                        high = middle;
                }
                if (ci.Compare(S(low), s, Primary) != 0 || ci.Compare(S(low), s, Secondary) >= 0)
                    throw new Exception($"U+{h:X4} doesn't sort with a syllable");
                if (!bySyllable.TryGetValue(low, out var list))
                    bySyllable[low] = list = [];
                list.Add(h);
            }
            foreach (var (syllable, list) in bySyllable)
            {
                // (A step at the secondary level.)
                list.Sort((a, b) => ci.Compare(S(a), S(b)));
                int step = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (i == 0 || ci.Compare(S(list[i - 1]), S(list[i]), Secondary) != 0)
                        step++;
                    else if (ci.Compare(S(list[i - 1]), S(list[i])) != 0)
                        throw new Exception("hanja differing at the tertiary level");
                    if (step > 200)
                        throw new Exception("too many hanja for a syllable");
                    order.Korean.Add((list[i], syllable, step));
                }
            }
            order.Korean.Sort((a, b) => a.Hanja.CompareTo(b.Hanja));
        }
        block.Sort((a, b) => ci.Compare(S(a), S(b), CompareOptions.None) is int c && c != 0 ? c : ImplicitKey(a).CompareTo(ImplicitKey(b)));
        for (int i = 1; i < block.Count; i++)
        {
            if (ci.Compare(S(block[i - 1]), S(block[i]), Primary) == 0)
                throw new Exception($"U+{block[i - 1]:X4} and U+{block[i]:X4} have one primary weight");
        }
        order.Explicit = block;
        order.Block = block;
        order.First = block[0];
        order.Last = block[^1];
        return order;
    }

    // The explicit list: runs of consecutive code points, each a zigzag
    // varint step from the last run's end and a varint length.
    static byte[] EncodeOrder(List<int> cps)
    {
        var bytes = new List<byte>();
        int previous = 0;
        for (int i = 0; i < cps.Count; )
        {
            int j = i + 1;
            while (j < cps.Count && cps[j] == cps[j - 1] + 1)
                j++;
            long delta = cps[i] - previous;
            Varint(bytes, (ulong)((delta << 1) ^ (delta >> 63)));
            Varint(bytes, (ulong)(j - i));
            previous = cps[j - 1];
            i = j;
        }
        return bytes.ToArray();
    }

    static void Varint(List<byte> bytes, ulong v)
    {
        while (v >= 0x80)
        {
            bytes.Add((byte)(v | 0x80));
            v >>= 7;
        }
        bytes.Add((byte)v);
    }

    // ---- combining classes ----

    // The canonical combining classes, as ranks (the order NFD puts marks
    // in), found by normalizing pairs.
    static List<(int Cp, int Rank)> CombiningRanks()
    {
        var nonzero = new List<int>();
        for (int c = 0x300; c <= 0x10FFFF; c++)
        {
            if (c >= 0xD800 && c <= 0xDFFF || !Assigned(c))
                continue;
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat != UnicodeCategory.NonSpacingMark && cat != UnicodeCategory.SpacingCombiningMark && cat != UnicodeCategory.EnclosingMark)
                continue;
            string s = S(c);
            if (s.Normalize(NormalizationForm.FormD) != s)
                continue;
            string a = "a" + s + "̴", b = "á" + s;
            if (a.Normalize(NormalizationForm.FormD) != a || b.Normalize(NormalizationForm.FormD) != b || c == 0x0334 || c == 0x0301)
                nonzero.Add(c);
        }
        // x before y when NFD puts it there.
        int Order(int x, int y)
        {
            if (x == y)
                return 0;
            string xy = "a" + S(x) + S(y), yx = "a" + S(y) + S(x);
            if (xy.Normalize(NormalizationForm.FormD) == yx)
                return 1;
            if (yx.Normalize(NormalizationForm.FormD) == xy)
                return -1;
            return 0;
        }
        // Classes: count, for each, how many go before it.
        var result = new List<(int, int)>();
        foreach (int x in nonzero)
        {
            int before = 0;
            foreach (int y in nonzero)
            {
                if (Order(y, x) < 0)
                    before++;
            }
            result.Add((x, 0));
            result[^1] = (x, before);
        }
        // Ranks 1..: by the count.
        var distinct = result.Select(r => r.Item2).Distinct().OrderBy(v => v).ToList();
        if (distinct.Count > 250)
            throw new Exception("too many combining classes");
        return result.Select(r => (r.Item1, distinct.IndexOf(r.Item2) + 1)).OrderBy(r => r.Item1).ToList();
    }

    public static void Write(List<string> names, string root, StringBuilder log)
    {
        var han = new List<int>();
        for (int c = 0; c <= 0x323AF; c++)
        {
            if (IsHan(c))
                han.Add(c);
        }
        var cultures = new[] { "" }.Concat(names).ToList();
        var hanOrders = new HanOrder[cultures.Count];
        // Characters with a primary weight of their own among the ideographs
        // (radicals, by radical): members of the block.
        {
            var rootCi = CultureInfo.InvariantCulture.CompareInfo;
            var rootBlock = ComputeHan(rootCi, han).Block;
            var set = new HashSet<int>(han);
            string first = S(rootBlock[0]), last = S(rootBlock[^1]);
            foreach (int c in Repertoire(rootBlock[^1]))
            {
                string u = S(c);
                if (set.Contains(c) || u.Normalize(NormalizationForm.FormKD) != u || rootCi.Compare(u, first, Primary) < 0 || rootCi.Compare(u, last, Primary) > 0)
                    continue;
                int low = 0, high = rootBlock.Count - 1;
                while (low < high)
                {
                    int middle = (low + high) / 2;
                    if (rootCi.Compare(S(rootBlock[middle]), u, Primary) < 0)
                        low = middle + 1;
                    else
                        high = middle;
                }
                if (rootCi.Compare(S(rootBlock[low]), u, Primary) != 0)
                    han.Add(c);
            }
            s_blockMembers = new HashSet<int>(han.Where(c => !IsHan(c)));
        }
        Parallel.For(0, cultures.Count, i =>
            hanOrders[i] = ComputeHan(CultureInfo.GetCultureInfo(cultures[i]).CompareInfo, han));
        s_rootBlock = hanOrders[0].Block;

        // And those a culture puts there itself (Chinese puts 々 among them).
        var preliminary = Repertoire(han.Max());
        Parallel.For(1, cultures.Count, i =>
        {
            var ci = CultureInfo.GetCultureInfo(cultures[i]).CompareInfo;
            var block = hanOrders[i].Block;
            string first = S(block[0]), last = S(block[^1]);
            var members = new List<int>();
            foreach (int c in preliminary)
            {
                string u = S(c);
                if (IsHan(c) || u.Normalize(NormalizationForm.FormKD) != u || ci.Compare(u, first, Primary) < 0 || ci.Compare(u, last, Primary) > 0)
                    continue;
                int low = 0, high = block.Count - 1;
                while (low < high)
                {
                    int middle = (low + high) / 2;
                    if (ci.Compare(S(block[middle]), u, Primary) < 0)
                        low = middle + 1;
                    else
                        high = middle;
                }
                if (ci.Compare(S(block[low]), u, Primary) != 0 && RootVariantOf(u) < 0)
                    members.Add(c);
            }
            if (members.Count > 0)
            {
                hanOrders[i] = ComputeHan(ci, han.Concat(members).ToList());
                hanOrders[i].Members = new HashSet<int>(members);
            }
        });
        // The root order (by radical and strokes): every culture's is the
        // shortest list of first ones after which the rest are in root order.
        var rootRank = new Dictionary<int, int>();
        for (int i = 0; i < hanOrders[0].Explicit.Count; i++)
            rootRank[hanOrders[0].Explicit[i]] = i;
        for (int i = 0; i < cultures.Count; i++)
        {
            var block = hanOrders[i].Explicit;
            int p = block.Count;
            if (i > 0)
            {
                while (p > 0 && (p == block.Count || rootRank.ContainsKey(block[p - 1]) && rootRank.ContainsKey(block[p])
                    && rootRank[block[p - 1]] < rootRank[block[p]]))
                    p--;
                int lastMember = block.FindLastIndex(c => !rootRank.ContainsKey(c));
                p = Math.Max(p, lastMember + 1);
            }
            hanOrders[i].Explicit = block.Take(p).ToList();
            foreach (int h in block.Skip(p))
                hanOrders[i].Tail.Add(h);
        }
        foreach (int h in han)
        {
            if (!hanOrders[0].Explicit.Contains(h))
                throw new Exception("an ideograph out of the root order");
        }
        foreach (int h in hanOrders[0].Explicit)
            hanOrders[0].Tail.Add(h);
        // A Han ideograph every culture sorts in root order: it stands for the block.
        int anchor = han.Where(h => hanOrders.All(o => o.Tail.Contains(h))).Max();

        var repertoire = Repertoire(anchor);
        var tables = new Table[cultures.Count];
        tables[0] = Compute(CultureInfo.InvariantCulture.CompareInfo, repertoire, null, hanOrders[0], anchor);
        var rootTable = tables[0];
        Parallel.For(1, cultures.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var t = Compute(CultureInfo.GetCultureInfo(cultures[i]).CompareInfo, repertoire, rootTable, hanOrders[i], anchor);
            // Only what's needed later: the elements.
            tables[i] = t;
        });

        // The Han block sits where its anchor does: check nothing else is inside it.
        for (int i = 0; i < cultures.Count; i++)
        {
            var ci = CultureInfo.GetCultureInfo(cultures[i]).CompareInfo;
            var t = tables[i];
            int g = t.Groups.FindIndex(x => x.Contains(S(anchor)));
            if (t.Groups[g].Count != 1)
                throw new Exception($"{cultures[i]}: the Han anchor sorts with something else");
            var o = hanOrders[i];
            if (g > 0 && ci.Compare(t.Groups[g - 1][0], S(o.First), Primary) >= 0
                || g + 1 < t.Groups.Count && ci.Compare(t.Groups[g + 1][0], S(o.Last), Primary) <= 0)
                throw new Exception($"{cultures[i]}: something sorts among the Han ideographs");
        }

        // Decompositions (NFD), the same for every culture.
        var decompositions = new List<byte>();
        for (int cp = 0xC0; cp <= 0x10FFFF; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF || cp >= 0xAC00 && cp <= 0xD7A3 || !Assigned(cp))
                continue;
            string s = S(cp), d = s.Normalize(NormalizationForm.FormD);
            if (d == s)
                continue;
            int[] cps = Key(d);
            if (cps.Length > 4)
                throw new Exception($"U+{cp:X4} decomposes to more than 4");
            decompositions.AddRange(Cp(cp));
            for (int k = 0; k < 4; k++)
                decompositions.AddRange(Cp(k < cps.Length ? cps[k] : 0));
        }

        var combining = new List<byte>();
        foreach (var (cp, rank) in CombiningRanks())
        {
            combining.AddRange(Cp(cp));
            combining.Add((byte)rank);
        }

        // The root's single code points, in runs: consecutive code points
        // whose primary weights go up a group at a time with the same other
        // weights; and its longer entries.
        var rootRuns = new List<byte>();
        var singles = new List<(int Cp, Element E)>();
        var rootSequences = new List<(string Key, Element[] Elements)>();
        foreach (var (key, elements) in rootTable.Map)
        {
            int[] k = Key(key);
            if (k.Length == 1 && elements.Length == 1)
                singles.Add((k[0], elements[0]));
            else
                rootSequences.Add((key, elements));
        }
        singles.Sort((a, b) => a.Cp.CompareTo(b.Cp));
        int runCount = 0;
        for (int i = 0; i < singles.Count; )
        {
            int j = i + 1;
            var e0 = singles[i].E;
            while (j < singles.Count && j - i < 0xFFFF && singles[j].Cp == singles[j - 1].Cp + 1 && e0.P != 0
                && singles[j].E == e0 with { P = e0.P + (j - i) * Spacing })
                j++;
            rootRuns.AddRange(Cp(singles[i].Cp));
            rootRuns.Add((byte)(j - i));
            rootRuns.Add((byte)((j - i) >> 8));
            rootRuns.AddRange(ElementBytes(e0));
            runCount++;
            i = j;
        }

        // Each culture's differences from the root, shared by cultures with the same.
        var tailorings = new List<byte[]>();
        var tailoringOf = new List<int>();
        var flags = new List<int>();
        var orderBlobs = new List<byte[]> { EncodeOrder(hanOrders[0].Explicit) };
        var koreanBlobs = new List<byte[]>();
        var hanOf = new List<int>();
        var koreanOf = new List<int>();
        for (int i = 1; i < cultures.Count; i++)
        {
            string name = cultures[i];
            var ci = CultureInfo.GetCultureInfo(name).CompareInfo;
            var table = tables[i];
            // Danish puts upper case first: a flag, read at run time, instead
            // of every cased letter's tertiary weight.
            bool upperFirst = ci.Compare("a", "A") > 0;
            var diff = new List<(string Key, Element[] Elements)>();
            foreach (var (key, elements) in table.Map)
            {
                if (rootTable.Map.TryGetValue(key, out var r) && r.SequenceEqual(elements))
                    continue;
                if (upperFirst && r != null && r.Length == elements.Length
                    && r.Zip(elements).All(p => p.First with { T = 0 } == p.Second with { T = 0 }))
                    continue;
                diff.Add((key, elements));
            }
            byte[] bytes = Sequences(diff);
            int index = bytes.Length == 0 ? -1 : tailorings.FindIndex(t => t.AsSpan().SequenceEqual(bytes));
            if (index < 0 && bytes.Length > 0)
            {
                index = tailorings.Count;
                tailorings.Add(bytes);
            }
            tailoringOf.Add(index + 1);
            flags.Add((table.Backwards ? 0x80 : 0) | (upperFirst ? 0x40 : 0));

            var o = hanOrders[i];
            byte[] order = EncodeOrder(o.Explicit);
            int h = order.Length == 0 ? 0 : orderBlobs.FindIndex(1, b => b.AsSpan().SequenceEqual(order));
            if (h < 0)
            {
                h = orderBlobs.Count;
                orderBlobs.Add(order);
            }
            if (h > 15)
                throw new Exception("too many Han orders");
            hanOf.Add(h);
            var korean = new List<byte>();
            foreach (var (hanja, syllable, step) in o.Korean)
            {
                korean.AddRange(Cp(hanja));
                korean.Add((byte)syllable);
                korean.Add((byte)(syllable >> 8));
                korean.Add((byte)step);
            }
            int kIndex = korean.Count == 0 ? -1 : koreanBlobs.FindIndex(b => b.AsSpan().SequenceEqual(korean.ToArray()));
            if (kIndex < 0 && korean.Count > 0)
            {
                kIndex = koreanBlobs.Count;
                koreanBlobs.Add(korean.ToArray());
            }
            koreanOf.Add(kIndex + 1);
            log.Append($"  {name}: {diff.Count} differences{(table.Backwards ? ", accents backwards" : "")}{(upperFirst ? ", upper case first" : "")}"
                + $"{(o.Explicit.Count > 0 ? $", {o.Explicit.Count} ideographs in its own order" : "")}{(o.Korean.Count > 0 ? $", {o.Korean.Count} with Hangul" : "")}\n");
        }

        var sb = new StringBuilder();
        sb.Append($$"""
// <auto-generated>
// Written by scripts/gen-cultures from .NET {{Environment.Version}}'s ICU collation.
// Don't edit; rerun: dotnet run --project scripts/gen-cultures
// </auto-generated>

namespace System.Globalization
{
    internal static partial class CollationData
    {
        // The Han ideograph that stands for the block (see Collator).
        internal const int HanAnchor = 0x{{anchor:X}};

""");
        Emit(sb, "RootRuns", rootRuns, "The root order's single code points, in runs: the first code point (3 bytes), how many (2),\n        // and the first's element: primary weight (4), secondary (2), tertiary (1), flags (1: symbol, 2: upper\n        // case, bits 4-7: kana and width variant); each next code point's primary is a group (1 << 15) more.");
        Emit(sb, "RootSequences", Sequences(rootSequences).ToList(), "The root order's contractions and expansions: entries as in Tailorings.");
        Emit(sb, "Decompositions", decompositions, "NFD: a code point and the up to 4 it decomposes to (0 after the last), 3 bytes each, sorted.");
        Emit(sb, "CombiningClasses", combining, "Marks NFD reorders: the code point (3 bytes) and its class as a rank (1), sorted.");
        EmitBlobs(sb, "Tailorings", tailorings, "Each culture's differences from the root: entries sorted by their code points, each the number of\n        // code points (1 byte), they (3 each), the number of elements (1) and they (8 bytes each, as in RootRuns).");
        EmitBlobs(sb, "HanOrders", orderBlobs, "The root order of Han ideographs (by radical and strokes), then those cultures put first in an\n        // order of their own (the rest follow in root order): runs of consecutive code points, a zigzag varint\n        // step from the last run's end and a varint length.");
        EmitBlobs(sb, "KoreanHanja", koreanBlobs, "Hanja Korean sorts with a Hangul syllable: the hanja (3 bytes), the syllable (2) and its step after\n        // it (1), sorted.");
        var perCulture = new List<byte>();
        for (int i = 0; i < names.Count; i++)
        {
            perCulture.Add((byte)(tailoringOf[i] | flags[i]));
            perCulture.Add((byte)(hanOf[i] | (koreanOf[i] << 4)));
        }
        Emit(sb, "Cultures", perCulture, "Each culture's two bytes: its tailoring (1 + its index; 0: the root order) with 0x80 if it compares\n        // accents from the end and 0x40 if it puts upper case first; then its Han order (its index; 0: the root's) and,\n        // shifted 4, its hanja (1 + index).");
        sb.Append("    }\n}\n");
        File.WriteAllText(Path.Combine(root, "zerocore/System/Globalization/CollationData.cs"), sb.ToString());
        int total = rootRuns.Count + decompositions.Count + combining.Count + tailorings.Sum(t => t.Length) + orderBlobs.Sum(b => b.Length) + koreanBlobs.Sum(b => b.Length);
        log.Append($"collation: {repertoire.Count} code points, {runCount} root runs, {rootSequences.Count} root sequences, {tailorings.Count} tailorings, "
            + $"{orderBlobs.Count} Han orders ({orderBlobs.Sum(b => b.Length)} bytes), {koreanBlobs.Count} hanja tables, {total} bytes\n");
    }

    static int[] Key(string s)
    {
        var cps = new List<int>();
        for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
            cps.Add(char.ConvertToUtf32(s, i));
        return cps.ToArray();
    }

    sealed class KeyComparer : IComparer<int[]>
    {
        public int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] != b[i])
                    return a[i].CompareTo(b[i]);
            }
            return b.Length.CompareTo(a.Length); // longer first
        }
    }

    static byte[] Sequences(List<(string Key, Element[] Elements)> entries)
    {
        var bytes = new List<byte>();
        foreach (var (key, elements) in entries.OrderBy(e => Key(e.Key), new KeyComparer()))
        {
            int[] cps = Key(key);
            bytes.Add((byte)cps.Length);
            foreach (int c in cps)
                bytes.AddRange(Cp(c));
            bytes.Add((byte)elements.Length);
            foreach (var e in elements)
                bytes.AddRange(ElementBytes(e));
        }
        return bytes.ToArray();
    }

    static byte[] Cp(int cp) => [(byte)cp, (byte)(cp >> 8), (byte)(cp >> 16)];

    static byte[] ElementBytes(Element e)
    {
        if (e.P < 0 || e.P > uint.MaxValue || e.T > 255)
            throw new Exception($"element out of range: {e}");
        return [(byte)e.P, (byte)(e.P >> 8), (byte)(e.P >> 16), (byte)(e.P >> 24), (byte)e.S, (byte)(e.S >> 8), (byte)e.T, (byte)e.F];
    }

    // Blobs one after another, and where each starts (and the last ends),
    // 4 bytes each.
    static void EmitBlobs(StringBuilder sb, string name, List<byte[]> blobs, string comment)
    {
        var starts = new List<byte>();
        var all = new List<byte>();
        foreach (byte[] b in blobs)
        {
            starts.AddRange(BitConverter.GetBytes(all.Count));
            all.AddRange(b);
        }
        starts.AddRange(BitConverter.GetBytes(all.Count));
        Emit(sb, name, all, comment);
        Emit(sb, name + "Starts", starts, $"Where each of {name} starts, and where the last ends (4 bytes each).");
    }

    static void Emit(StringBuilder sb, string name, List<byte> bytes, string comment)
    {
        sb.Append($"        // {comment}\n");
        sb.Append($"        internal static ReadOnlySpan<byte> {name} =>\n        [\n");
        for (int i = 0; i < bytes.Count; i += 32)
        {
            sb.Append("            ");
            int n = Math.Min(32, bytes.Count - i);
            for (int k = 0; k < n; k++)
            {
                sb.Append(bytes[i + k]);
                sb.Append(k + 1 < n ? "," : ",\n");
            }
        }
        sb.Append("        ];\n\n");
    }
}
