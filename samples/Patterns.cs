// Example: regular expressions (lib/Regex.cs): matching, groups, every match
// in a text, options, the errors an invalid pattern gives, and that a pattern
// which makes a backtracking engine take ages takes no time here.
// Exit code = number of failed checks.
// Build: ./build.sh Patterns

using System;

namespace PatternsSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

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

    static Arena s_arena;

    static Regex R(string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Create(s_arena, pattern, options);

    static bool Is(ReadOnlySpan<char> actual, string expected) => actual.SequenceEqual(expected);

    // R(pattern).Replace, checked against what .NET gives.
    static bool Replaced(string pattern, string input, string replacement, string expected, int count = -1)
    {
        string actual = R(pattern).Replace(s_arena, input, replacement, count);
        if (actual != expected)
            Console.WriteLine($"  {pattern} on {input} with {replacement}: {actual}, not {expected}");
        return actual == expected;
    }

    // The pieces of R(pattern).Split as [a|b|c], checked against .NET.
    static bool SplitInto(string pattern, string input, string expected, int count = 0)
    {
        StrBuilder joined = StrBuilder.Create(s_arena);
        joined.Append('[');
        bool first = true;
        foreach (ReadOnlySpan<char> piece in R(pattern).Split(input, count))
        {
            if (!first)
                joined.Append('|');
            joined.Append(piece);
            first = false;
        }
        joined.Append(']');
        bool ok = joined.AsSpan().SequenceEqual(expected);
        if (!ok)
            Console.WriteLine($"  {pattern} on {input}: {joined.AsSpan()}, not {expected}");
        return ok;
    }

    // docs:evaluator
    // A MatchEvaluator: appends what replaces each match.
    static void Doubled(RegexMatch match, StrBuilder output)
    {
        Str.TryParse(match.Value, out long number);
        output.Append(2 * number);
    }
    // docs:end

    static bool Refused(string pattern, string reason, int position)
    {
        bool ok = !Regex.TryCreate(s_arena, pattern, RegexOptions.None, out _, out string error, out int at);
        if (ok && (error != reason || at != position))
            Console.WriteLine($"  {pattern}: \"{error}\" at {at}");
        return ok && error == reason && at == position;
    }

    public static int Main()
    {
        s_arena = Arena.Create(64 * 1024);
        Arena arena = s_arena;

        // docs:match
        Regex date = Regex.Create(arena, @"(\d{4})-(\d\d)-(\d\d)");
        RegexMatch m = date.Match("released on 2026-11-24, at last");
        Check("a match and its groups", m.Success && m.Index == 12
            && Is(m.Value, "2026-11-24") && Is(m.Groups[1].Value, "2026")
            && Is(m.Groups[3].Value, "24"));
        // docs:end
        Check("IsMatch", R("ab+c").IsMatch("xabbbcx") && !R("ab+c").IsMatch("ac"));
        m = R(@"\d+").Match("abc 123 def 45");
        Check("the first match", m.Index == 4 && Is(m.Value, "123"));
        m = m.NextMatch();
        Check("NextMatch", m.Success && m.Index == 12 && Is(m.Value, "45"));
        Check("then no more", !m.NextMatch().Success);
        m = R(@"(\w+)@(\w+)\.com").Match("mail bob@example.com now");
        Check("groups", Is(m.Value, "bob@example.com") && Is(m.Groups[1].Value, "bob")
            && Is(m.Groups[2].Value, "example") && !m.Groups[3].Success);
        m = R("(a)|(b)").Match("b");
        Check("a group that didn't take part", !m.Groups[1].Success && Is(m.Groups[2].Value, "b"));
        Check("the last iteration's capture", Is(R(@"(\d)+").Match("123").Groups[1].Value, "3"));
        Section("matching");

        Check("leftmost alternative first", Is(R("a|ab").Match("ab").Value, "a"));
        m = R("(a|ab)(c|bcd)").Match("abcd");
        Check("as a backtracking engine chooses",
            Is(m.Value, "abcd") && Is(m.Groups[1].Value, "a") && Is(m.Groups[2].Value, "bcd"));
        Check("greedy", Is(R("<.+>").Match("<a><b>").Value, "<a><b>"));
        Check("lazy", Is(R("<.+?>").Match("<a><b>").Value, "<a>"));
        Check("{2,3} greedy and lazy",
            Is(R("a{2,3}").Match("aaaa").Value, "aaa") && Is(R("a{2,3}?").Match("aaaa").Value, "aa"));
        m = R("x*").Match("aaa");
        Check("an empty match", m.Success && m.Index == 0 && m.Length == 0);
        Check("{ that isn't a count is a literal", R("a{,2}").IsMatch("a{,2}"));
        m = R("a|").Match("b");
        Check("an empty alternative", m.Success && m.Length == 0);
        Section("which match");

        // docs:matches
        int total = 0;
        foreach (RegexMatch number in Regex.Create(arena, @"\d+").Matches("10 20 30"))
        {
            Str.TryParse(number.Value, out int value);
            total += value;
        }
        Check("foreach over the matches", total == 60);
        // docs:end
        Check("Count", R(@"\d{2,3}").Count("1 12 1234") == 2);
        Check("empty matches advance", R("a*").Count("baaa") == 3);
        Section("every match");

        Check("^ and $", R("^abc$").IsMatch("abc") && !R("^abc$").IsMatch("abc\nx"));
        Check("$ before a final \\n", R("^abc$").IsMatch("abc\n"));
        Check("Multiline",
            !R("^b").IsMatch("a\nb") && R("^b", RegexOptions.Multiline).IsMatch("a\nb")
            && R("a$", RegexOptions.Multiline).IsMatch("a\nb"));
        Check(@"\b and \B",
            R(@"\bcat\b").Match("concat cat").Index == 7 && R(@"\Bcat").Match("concat cat").Index == 3);
        Check(@"\A \z \Z", R(@"\Aab").IsMatch("ab") && !R(@"b\z").IsMatch("ab\n") && R(@"b\Z").IsMatch("ab\n"));
        Section("anchors");

        Check("negated class", Is(R(@"[^aeiou\s]+").Match("hello world").Value, "h"));
        m = R(@"[\d.]+").Match("v1.2.3");
        Check(@"\d in a class", m.Index == 1 && Is(m.Value, "1.2.3"));
        Check("- at the end of a class", Is(R("[a-c-]+").Match("x-ab-cz").Value, "-ab-c"));
        Check("] first in a class", Is(R("[]a]+").Match("x]a]").Value, "]a]"));
        Check(@"\W", Is(R(@"\W+").Match("ab, cd").Value, ", "));
        Check(@"\D in a class", Is(R(@"[\D]+").Match("12ab3").Value, "ab"));
        Check(@"\x \u \t", R(@"\x41B\t").IsMatch("AB\t"));
        Section("classes and escapes");

        // docs:options
        var hello = Regex.Create(arena, "hello", RegexOptions.IgnoreCase);
        Check("IgnoreCase", hello.Match("Say HeLLo").Index == 4);
        // docs:end
        Check("IgnoreCase in a class", Is(R("[a-z]+", RegexOptions.IgnoreCase).Match("ABC1").Value, "ABC"));
        Check("IgnoreCase in a negated class",
            R("[^a-z]+", RegexOptions.IgnoreCase).Match("ABC1").Index == 3);
        Check("Singleline", !R("a.b").IsMatch("a\nb") && R("a.b", RegexOptions.Singleline).IsMatch("a\nb"));
        Section("options");

        // docs:errors
        bool valid = Regex.TryCreate(arena, "(abc", RegexOptions.None,
            out Regex _, out string error, out int position);
        Check("TryCreate gives the reason",
            !valid && error == "missing )" && position == 0);
        // docs:end
        Check("a{2,1}", Refused("a{2,1}", "{n,m} with n greater than m", 1));
        Check("backreference", Refused(@"(a)\1", "backreferences aren't supported", 3));
        Check("lookahead", Refused("(?=a)", "lookaround isn't supported", 0));
        Check("lookbehind", Refused("(?<=a)b", "lookaround isn't supported", 0));
        Check("numbered name", Refused("(?<2>a)", "numbered group names aren't supported", 3));
        Check("bad name", Refused("(?<>a)", "invalid group name: letters, digits and _ only, starting with a letter or _", 3)
            && Refused("(?P<n>a)", "this kind of group isn't supported", 0));
        Check("nothing to repeat", Refused("*a", "quantifier following nothing", 0));
        Check("nested quantifier", Refused("a**", "nested quantifier", 2));
        Check("unclosed class", Refused("[a", "missing ]", 0));
        Check("stray )", Refused("abc)", "too many )'s", 3));
        Check("unknown escape", Refused(@"\q", "unrecognized escape", 0));
        Section("errors");

        // docs:linear
        // A backtracking engine tries 2^40 ways to split the a's before it
        // gives up; this one looks at each character once per instruction.
        string aaa = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Check("no catastrophic backtracking",
            !Regex.Create(arena, "(a+)+b").IsMatch(aaa));
        // docs:end
        Check("nor here", !R("(a|aa)*c").IsMatch(aaa) && !R("(a*)*b").IsMatch(aaa));
        Regex word = R(@"\w+");
        nuint used = arena.Used;
        long allocated = GC.GetTotalAllocatedBytes();
        int words = 0;
        for (int i = 0; i < 100; i++)
            words += word.Count("the quick brown fox");
        Check("matching allocates nothing",
            words == 400 && arena.Used == used && GC.GetTotalAllocatedBytes() == allocated);
        Section("cost");

        // docs:replace
        string swapped = date.Replace(arena,
            "on 2026-11-25 and 2025-01-02", "$3/$2/$1");
        // "on 25/11/2026 and 02/01/2025"
        // docs:end
        Check("groups in the replacement", swapped == "on 25/11/2026 and 02/01/2025");
        Check("${n} and $$", Replaced(@"(\w)(\d)?", "a1b", "[${1}${2}$$]", "[a1$][b$]"));
        Check("$` $& $' $_", Replaced("b", "abc", "<$`|$&|$'|$_>", "a<a|b|c|abc>c"));
        Check("$+, the last group", Replaced("(a)(b)?", "ab a", "$+;", "b; ;"));
        Check("not substitutions", Replaced("(a)", "a", "$10|$01|$2|${2}|${x}|$x|$", "$10|a|$2|${2}|${x}|$x|$"));
        Check("more edges", Replaced("b", "abc", "<$+>", "a<b>c")
            && Replaced("(b)", "abc", "<${01}|$0|${}|${1|$>", "a<b|b|${}|${1|$>c")
            && Replaced("(x)?b", "abc", "<$1|$+>", "a<|>c"));
        Check("twelve groups",
            Replaced("(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)(l)", "abcdefghijkl", "$12$13|${12}", "l$13|l"));
        Check("empty matches", Replaced("x*", "abc", "-", "-a-b-c-") && Replaced("a*", "baaa", "<$&>", "<>b<aaa><>"));
        Check("a count", Replaced(@"\d", "1 2 3 4", "#", "# # 3 4", 2) && Replaced(@"\d", "1 2", "#", "1 2", 0));
        // docs:evaluator-use
        string doubled = R(@"\d+").Replace(arena, "3 apples, 12 pears", &Doubled);
        // docs:end
        Check("an evaluator", doubled == "6 apples, 24 pears");
        int scratchLength = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            ScratchText text = scratch.Text();
            R(@"\s+").Replace(text, "a  b \t c", " ");
            return text.AsSpan().SequenceEqual("a b c") ? text.Length : -1;
        });
        Check("into a ScratchText", scratchLength == 5);
        Section("replace");

        // docs:split
        int fields = 0;
        foreach (ReadOnlySpan<char> field in R(@"\s*,\s*").Split("a , b,c,,d"))
            fields++;           // "a", "b", "c", "", "d"
        // docs:end
        Check("pieces", fields == 5 && SplitInto(@"\s*,\s*", "a , b,c,,d", "[a|b|c||d]"));
        Check("groups between the pieces", SplitInto(@"(-)|(\+)", "1-2+3", "[1|-|2|+|3]"));
        Check("empty matches", SplitInto("", "abc", "[|a|b|c|]") && SplitInto(@"\d*", "a1b", "[|a||b|]"));
        Check("a count", SplitInto(",", "a,b,c,d", "[a|b,c,d]", 2) && SplitInto(",", "a,b,c", "[a,b,c]", 1)
            && SplitInto("(,)", "a,b,c", "[a|,|b,c]", 2));
        Check("no match, and the ends", SplitInto("x", "abc", "[abc]") && SplitInto(",", ",a,", "[|a|]"));
        Section("split");

        // docs:named
        Regex stamp = R(@"(?<year>\d{4})-(?<month>\d\d)");
        RegexMatch when = stamp.Match("since 2026-11");
        ReadOnlySpan<char> year = when.Groups["year"].Value;     // "2026"
        string swappedNamed = stamp.Replace(arena, "2026-11", "${month}/${year}");
        // docs:end
        Check("by name", Is(year, "2026") && Is(when.Groups["month"].Value, "11") && !when.Groups["day"].Success);
        Check("${name}", swappedNamed == "11/2026"
            && Replaced(@"(?<year>\d{4})-(?<mon>\d\d)", "2026-11", "${mon}/${year} $2 $1 ${nope} $+", "11/2026 11 2026 ${nope} 11"));
        // Numbered as .NET does: the unnamed groups first, then the named ones.
        Regex mixed = R(@"(?<y>\d)(\d)(?'m'x)?(?<y>z)?");
        RegexMatch both = mixed.Match("12");
        Check("named after unnamed", Is(both.Groups[1].Value, "2") && Is(both.Groups[2].Value, "1")
            && Is(both.Groups["y"].Value, "1") && !both.Groups["m"].Success && mixed.GroupCount == 3);
        Check("the same name twice", Is(R("(?<y>a)(?<y>b)").Match("ab").Groups["y"].Value, "b"));
        Check("names and numbers", R("(a)(?<y>b)(c)").GroupNumberFromName("y") == 3
            && mixed.GroupNameFromNumber(2) == "y" && mixed.GroupNameFromNumber(1) == null
            && mixed.GroupNumberFromName("nope") == -1);
        Check("split", SplitInto("(?<sep>[a-z])", "1a2", "[1|a|2]"));
        Section("named groups");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
