// Compares zerocore's collation (namespace ZeroTest here) with .NET's with
// ICU, culture by culture: random texts compared with each set of options,
// word lists sorted, and equal texts hashed alike.
// Usage: check-collation.sh [iterations] [seed]
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using G = System.Globalization;
using Z = ZeroTest;

namespace ZeroTest
{
    internal static partial class CultureData
    {
        internal static string NameOf(int row)
        {
            ReadOnlySpan<byte> records = Records, strings = Strings;
            int at = (row * FieldCount + Name) * 2;
            int index = records[at] | (records[at + 1] << 8);
            int start = strings[index * 5] | (strings[index * 5 + 1] << 8) | (strings[index * 5 + 2] << 16);
            int length = strings[index * 5 + 3] | (strings[index * 5 + 4] << 8);
            return Encoding.UTF8.GetString(Pool.Slice(start, length));
        }
    }
}

static class Program
{
    static Random R;
    static readonly bool Searches = Environment.GetEnvironmentVariable("SEARCH") != "0";
    static readonly string Show = Environment.GetEnvironmentVariable("SHOW");
    static long checks;
    static int failures, shown;

    static bool HasHan(string s) => s.Any(c => c >= 0x3400 && c <= 0x9FFF);
    static readonly Dictionary<string, int> perLabel = new();

    static readonly G.CompareOptions[] Options =
    {
        G.CompareOptions.None, G.CompareOptions.IgnoreCase, G.CompareOptions.IgnoreNonSpace,
        G.CompareOptions.IgnoreCase | G.CompareOptions.IgnoreNonSpace, G.CompareOptions.IgnoreSymbols,
        G.CompareOptions.IgnoreSymbols | G.CompareOptions.IgnoreCase,
        G.CompareOptions.NumericOrdering, G.CompareOptions.NumericOrdering | G.CompareOptions.IgnoreCase,
        G.CompareOptions.IgnoreKanaType, G.CompareOptions.IgnoreWidth, G.CompareOptions.IgnoreKanaType | G.CompareOptions.IgnoreWidth,
        G.CompareOptions.IgnoreCase | G.CompareOptions.IgnoreKanaType, G.CompareOptions.IgnoreCase | G.CompareOptions.IgnoreWidth,
        G.CompareOptions.IgnoreCase | G.CompareOptions.IgnoreKanaType | G.CompareOptions.IgnoreWidth,
        G.CompareOptions.IgnoreNonSpace | G.CompareOptions.IgnoreKanaType | G.CompareOptions.IgnoreWidth | G.CompareOptions.IgnoreSymbols,
        G.CompareOptions.NumericOrdering | G.CompareOptions.IgnoreWidth,
    };

    // Kana and characters of every width, for IgnoreKanaType and IgnoreWidth.
    static readonly string Kana = "\u3042\u3041\u304B\u304C\u3071\u30A2\u30A1\u30AB\u30AC\u30D1\u30FC\u30FD\u309D\uFF71\uFF76\uFF9E\uFF9F\uFF21\uFF41\uFF10\uFF01\uFFE5\uFFA1\u3131\uFF64\u3001";

    // Digits of several scripts, for NumericOrdering.
    static readonly string Digits = "0123456789000\u0660\u0661\u0662\u0669\u0966\u0967\u096F\uFF10\uFF11\uFF19\u0E50\u0E51\u00B2\u2460";

    // Letters every culture uses, the culture's own (from its alphabet),
    // accents, punctuation, digits and spaces.
    static readonly string Common = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -_.,'!?éèêëàáâäåæøöüçñßÅÄÖÜÇıİğĞşŞłŁąężźćńčšžřěůýőűășțãõơưđạȩ̂́́̀̈";
    static readonly string Scripts = "αβγΑάабвАёЁйїґאבابأअकあア©€$%&*@#";

    // Every assigned character (not private use or unassigned, which
    // zerocore sorts last, by code point).
    static readonly int[] Everything = Enumerable.Range(0, 0x110000)
        .Where(c => (c < 0xD800 || c > 0xDFFF) && G.CharUnicodeInfo.GetUnicodeCategory(c) is var k
            && k != G.UnicodeCategory.OtherNotAssigned && k != G.UnicodeCategory.PrivateUse && k != G.UnicodeCategory.Surrogate)
        .ToArray();
    static readonly int[] Marks = Everything.Where(c => G.CharUnicodeInfo.GetUnicodeCategory(c) is G.UnicodeCategory.NonSpacingMark
        or G.UnicodeCategory.SpacingCombiningMark or G.UnicodeCategory.EnclosingMark).ToArray();

    static string RandomText(string alphabet)
    {
        int n = R.Next(0, 7);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            int k = R.Next(21);
            if (k < 11)
                sb.Append(alphabet[R.Next(alphabet.Length)]);
            else if (k < 14)
                sb.Append(Common[R.Next(Common.Length)]);
            else if (k < 15)
                sb.Append(Scripts[R.Next(Scripts.Length)]);
            else if (k < 16)
                sb.Append(char.ConvertFromUtf32(Marks[R.Next(Marks.Length)]));
            else if (k < 17)
                sb.Append((char)R.Next(0x4E00, 0xA000));
            else if (k < 18)
                sb.Append((char)R.Next(0xAC00, 0xD7A4));
            else if (k < 19 && R.Next(2) == 0)
                sb.Append(Kana[R.Next(Kana.Length)]);
            else if (k < 19)
            {
                // A run of digits, sometimes a long one.
                int digits = R.Next(5) == 0 ? R.Next(240, 520) : R.Next(1, 6);
                for (int d = 0; d < digits; d++)
                    sb.Append(Digits[R.Next(Digits.Length)]);
            }
            else
                sb.Append(char.ConvertFromUtf32(Everything[R.Next(Everything.Length)]));
            // (k 19 and 20: anything.)
        }
        return sb.ToString();
    }

    static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(c >= 0x20 && c < 0x7F ? c.ToString() : $"\\u{(int)c:X4}");
        return sb.ToString();
    }

    static void Fail(string label, string message)
    {
        failures++;
        perLabel.TryGetValue(label, out int n);
        perLabel[label] = n + 1;
        if (n < 4 && shown < 300 && (Show == null || label.StartsWith(Show + " ")))
        {
            shown++;
            Console.WriteLine($"FAIL {label}: {message}");
        }
    }

    static int Main(string[] args)
    {
        // PAIR="culture|a|b": compare just these, with each set of options.
        if (Environment.GetEnvironmentVariable("PAIR") is string pair)
        {
            var parts = pair.Split('|');
            int row = Enumerable.Range(0, Z.CollationData.Cultures.Length / 2).First(r => Z.CultureData.NameOf(r) == parts[0]);
            var z = new Z.Collator(row, parts[0].StartsWith("en"));
            var n = G.CultureInfo.GetCultureInfo(parts[0]).CompareInfo;
            foreach (var o in Options)
                Console.WriteLine($"{o}: zero {z.Compare(parts[1], parts[2], o)}, .NET {n.Compare(parts[1], parts[2], o)}");
            return 0;
        }
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 20000;
        R = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);
        for (int row = 0; row < Z.CollationData.Cultures.Length / 2; row++)
        {
            string name = Z.CultureData.NameOf(row);
            var net = G.CultureInfo.GetCultureInfo(name).CompareInfo;
            var zero = new Z.Collator(row, name == "en" || name.StartsWith("en-"));
            // The culture's alphabet: what its exemplar-ish letters are, from
            // the month and day names, plus the common set.
            var dtf = G.CultureInfo.GetCultureInfo(name).DateTimeFormat;
            string alphabet = new string(string.Concat(dtf.MonthNames.Concat(dtf.DayNames))
                .SelectMany(c => new[] { char.ToLowerInvariant(c), char.ToUpperInvariant(c) }).Distinct().ToArray()) + "chlsz";
            foreach (var options in Options)
            {
                string label = $"{name} {options}";
                for (int i = 0; i < iterations; i++)
                {
                    string a = RandomText(alphabet), b = R.Next(4) == 0 ? Mutate(a, alphabet) : RandomText(alphabet);
                    checks++;
                    int expected = Math.Sign(net.Compare(a, b, options)), actual = Math.Sign(zero.Compare(a, b, options));
                    if (expected != actual)
                    {
                        Fail(label, $"[{Escape(a)}] vs [{Escape(b)}]: zero {actual}, .NET {expected}");
                        continue;
                    }
                    // (With NumericOrdering .NET's Compare can say equal where its
                    // sort keys, and so hashes, differ: ICU skips common prefixes.)
                    bool sameKeys = (options & G.CompareOptions.NumericOrdering) == 0
                        || net.GetSortKey(a, options).KeyData.AsSpan().SequenceEqual(net.GetSortKey(b, options).KeyData);
                    if (expected == 0 && sameKeys && zero.GetHashCode(a, options) != zero.GetHashCode(b, options))
                        Fail(label + " hash", $"[{Escape(a)}] and [{Escape(b)}] are equal but hash differently");
                }
                // (.NET refuses NumericOrdering for searching.)
                if (Searches && (options & G.CompareOptions.NumericOrdering) == 0)
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        string source = RandomText(alphabet) + RandomText(alphabet);
                        string value = R.Next(3) > 0 && source.Length > 0 ? Piece(source, alphabet) : RandomText(alphabet);
                        checks += 4;
                        string what = $"[{Escape(source)}] [{Escape(value)}]";
                        bool np = net.IsPrefix(source, value, options), zp = zero.Find(source, value, options, Z.Search.Prefix, out _) >= 0;
                        if (np != zp)
                            Fail(label + " IsPrefix", $"{what}: zero {zp}, .NET {np}");
                        bool ns = net.IsSuffix(source, value, options), zs = zero.Find(source, value, options, Z.Search.Suffix, out _) >= 0;
                        if (ns != zs)
                            Fail(label + " IsSuffix", $"{what}: zero {zs}, .NET {ns}");
                        int ni = net.IndexOf(source, value, options, out int nl), zi = zero.Find(source, value, options, Z.Search.First, out int zl);
                        if (ni != zi || (ni >= 0 && nl != zl))
                            Fail(label + " IndexOf", $"{what}: zero {zi} ({zl}), .NET {ni} ({nl})");
                        int nj = net.LastIndexOf(source, value, options, out int nm), zj = zero.Find(source, value, options, Z.Search.Last, out int zm);
                        if (nj != zj || (nj >= 0 && nm != zm))
                            Fail(label + " LastIndexOf", $"{what}: zero {zj} ({zm}), .NET {nj} ({nm})");
                    }
                }
            }
        }
        foreach (var group in perLabel.GroupBy(p => p.Key.Split(' ')[0]))
            Console.WriteLine($"  {group.Sum(p => p.Value),7} {group.Key}: " + string.Join(", ", group.Select(p => $"{p.Key.Substring(group.Key.Length + 1)} {p.Value}")));
        Console.WriteLine(failures == 0 ? $"OK: {checks} checks" : $"{failures} of {checks} checks failed");
        return failures == 0 ? 0 : 1;
    }

    // A piece of source, sometimes changed a little.
    static string Piece(string source, string alphabet)
    {
        int start = R.Next(source.Length), length = R.Next(1, Math.Min(4, source.Length - start) + 1);
        string piece = source.Substring(start, length);
        return R.Next(4) == 0 ? Mutate(piece, alphabet) : piece;
    }

    // A text near a: one character changed, added or removed.
    static string Mutate(string a, string alphabet)
    {
        var sb = new StringBuilder(a);
        int k = R.Next(3);
        string from = R.Next(3) == 0 ? Common : alphabet;
        if (k == 0 && sb.Length > 0)
            sb[R.Next(sb.Length)] = from[R.Next(from.Length)];
        else if (k == 1 || sb.Length == 0)
            sb.Insert(R.Next(sb.Length + 1), from[R.Next(from.Length)]);
        else
            sb.Remove(R.Next(sb.Length), 1);
        return sb.ToString();
    }
}
