// Rewrites NativeAOT-LLVM's 32-bit code (LLVM IR, as text) for the 6502.
//
// NativeAOT-LLVM compiles C# for a machine with 4-byte pointers: every object
// layout, field offset and array stride in its output assumes them. The
// 6502's pointers are 2 bytes. The compiler can't be told that, so memory
// keeps the 32-bit layout: a pointer in memory stays a 4-byte slot, holding
// the 16-bit address and two zero bytes. Only pointers in registers shrink.
// In the IR:
//
//   store ptr %p, ...        stores the address widened to 32 bits, so the
//                            slot's upper half is always zero (it is read as
//                            an integer in places)
//   load ptr                 unchanged: little-endian, the low half is the
//                            address
//   alloca ptr               four bytes
//   getelementptr ptr, ...   steps of four bytes
//   <{ ptr, i32 }>           pointers inside struct and array types become
//                            i32; extractvalue and insertvalue convert on the
//                            way. NativeAOT-LLVM only uses packed structs, so
//                            nothing else in a layout depends on the target.
//
// Six more things, each explained where it is done: the exception flag is
// folded away, imported functions are called directly, 32-bit division and
// multiplication call the runtime, every function is marked to be compiled
// for size, float-to-integer conversions call the runtime, and functions
// whose declaration and definition disagree are refused.
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroBitcode;

static class Ll2Mos
{
    const RegexOptions Options = RegexOptions.CultureInvariant;

    public static ToolFailure Fail(string message) => new("ll2mos", message);

    /// <summary>Writes OUTDIR/&lt;name of each source&gt;.</summary>
    public static void Run(string outDir, string[] sources)
    {
        CheckSignatures(sources);
        foreach (string path in sources)
        {
            string text = Files.ReadText(path);
            var rewriter = new Rewriter(text);
            var lines = new List<string>();
            foreach (string line in text.Split('\n'))
                lines.AddRange(rewriter.Line(line));
            lines.AddRange(rewriter.Declarations(text));
            Files.WriteText(Path.Combine(outDir, Path.GetFileName(path)), string.Join("\n", lines));
        }
    }

    // ---- types -------------------------------------------------------------------

    abstract record IrType;
    sealed record Scalar(string Name) : IrType;
    sealed record Aggregate(bool Packed, List<IrType> Fields) : IrType;
    sealed record ArrayOf(long Count, IrType Element) : IrType;

    sealed class ParseError(string message) : Exception(message);

    sealed class Cursor(string text, int at = 0)
    {
        public readonly string Text = text;
        public int At = at;

        public void Skip()
        {
            while (At < Text.Length && Text[At] == ' ')
                At++;
        }

        public bool Peek(string token)
        {
            Skip();
            return At <= Text.Length && Text.AsSpan(At).StartsWith(token, StringComparison.Ordinal);
        }

        public void Eat(string token)
        {
            if (!Peek(token))
                throw new ParseError($"expected {token} at {Snippet()}");
            At += token.Length;
        }

        public string Snippet() => Text.Substring(At, Math.Min(30, Text.Length - At));
    }

    static readonly Regex ScalarType = new(@"\G(ptr|i\d+|float|double|void)\b", Options);
    static readonly Regex ArrayType = new(@"\G\[\s*(\d+) x ", Options);

    static IrType ParseType(Cursor c)
    {
        c.Skip();
        foreach (var (opening, closing, packed) in new[] { ("<{", "}>", true), ("{", "}", false) })
        {
            if (c.Peek(opening))
            {
                c.Eat(opening);
                var fields = new List<IrType>();
                while (!c.Peek(closing))
                {
                    fields.Add(ParseType(c));
                    if (c.Peek(","))
                        c.Eat(",");
                }
                c.Eat(closing);
                return new Aggregate(packed, fields);
            }
        }
        Match m = ArrayType.Match(c.Text, c.At);
        if (m.Success)
        {
            c.At = m.Index + m.Length;
            IrType element = ParseType(c);
            c.Eat("]");
            return new ArrayOf(long.Parse(m.Groups[1].Value), element);
        }
        m = ScalarType.Match(c.Text, c.At);
        if (!m.Success)
            throw new ParseError($"unknown type at {c.Snippet()}");
        c.At = m.Index + m.Length;
        return new Scalar(m.Groups[1].Value);
    }

    static bool HasPointer(IrType t)
    {
        switch (t)
        {
            case Scalar s:
                return s.Name == "ptr";
            case ArrayOf a:
                return HasPointer(a.Element);
            default:
                var s2 = (Aggregate)t;
                bool inside = s2.Fields.Any(HasPointer);
                // An unpacked struct is laid out by the target's alignment
                // rules, which differ; a rewrite would have to add the padding.
                if (inside && !s2.Packed)
                    throw Fail("a struct that isn't packed holds a pointer; its layout would change");
                return inside;
        }
    }

    /// <summary>The type as text, with pointers inside aggregates as i32.</summary>
    static string Show(IrType t, bool inside = false)
    {
        switch (t)
        {
            case Scalar s:
                return inside && s.Name == "ptr" ? "i32" : s.Name;
            case ArrayOf a:
                return $"[{a.Count} x {Show(a.Element, true)}]";
            default:
                var s2 = (Aggregate)t;
                var (opening, closing) = s2.Packed ? ("<{", "}>") : ("{", "}");
                if (s2.Fields.Count == 0)
                    return opening + closing;
                return $"{opening} {string.Join(", ", s2.Fields.Select(f => Show(f, true)))} {closing}";
        }
    }

    static IrType Member(IrType t, IEnumerable<int> indices)
    {
        foreach (int i in indices)
            t = t is Aggregate s ? s.Fields[i] : ((ArrayOf)t).Element;
        return t;
    }

    static readonly Regex AggregateStart = new(@"<\{|\[\d+ x ", Options);

    /// <summary>Rewrites every aggregate type in the line that holds a pointer.</summary>
    static string RewriteTypes(string line)
    {
        var output = new StringBuilder();
        int at = 0;
        while (true)
        {
            Match m = AggregateStart.Match(line, at);
            if (!m.Success)
            {
                output.Append(line, at, line.Length - at);
                return output.ToString();
            }
            output.Append(line, at, m.Index - at);
            var c = new Cursor(line, m.Index);
            IrType t;
            try
            {
                t = ParseType(c);
            }
            catch (ParseError)
            {
                // A constant ("<{ i32 1, i32 2 }>"), not a type. One holding a
                // pointer would need its value converted; none has been seen.
                if (line.IndexOf("ptr", m.Index, StringComparison.Ordinal) >= 0)
                    throw Fail($"a constant aggregate with a pointer: {line.Trim()}");
                output.Append(m.Value);
                at = m.Index + m.Length;
                continue;
            }
            output.Append(HasPointer(t) ? Show(t) : line.Substring(m.Index, c.At - m.Index));
            at = c.At;
        }
    }

    // ---- instructions --------------------------------------------------------------

    /// <summary>Splits at the commas that aren't inside brackets.</summary>
    static List<string> Operands(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        foreach (char ch in text)
        {
            if ("([{<".Contains(ch))
                depth++;
            else if (")]}>".Contains(ch))
                depth--;
            if (ch == ',' && depth == 0)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        parts.Add(current.ToString().Trim());
        return parts;
    }

    sealed class Rewriter
    {
        static readonly Regex Store = new(@"^(\s*)store (volatile )?ptr (.*)$", Options);
        static readonly Regex Extract = new(@"^(\s*)(%[\w.$]+) = extractvalue (.*)$", Options);
        static readonly Regex Insert = new(@"^(\s*)(%[\w.$]+) = insertvalue (.*)$", Options);
        static readonly Regex Cell = new(@"^(\s*%[\w.$]+) = load ptr, ptr @cell\.([\w$]+), align \d+$", Options);
        static readonly Regex Flag = new(@"^(\s*%[\w.$]+) = load i32, ptr @RhpExceptionThrown, align 4$", Options);
        static readonly Regex Unoptimized = new(@"^attributes (#\d+) = \{[^}]*\boptnone\b", Options | RegexOptions.Multiline);
        static readonly Regex Group = new(@" (#\d+) \{$", Options);
        static readonly Regex AllocaPtr = new(@"= alloca ptr\b", Options);
        static readonly Regex GepPtr = new(@"(getelementptr(?: inbounds| nuw| nusw)* \(?)ptr,", Options);
        static readonly Regex Arithmetic = new(@"^(\s*%[\w.$]+) = (udiv|urem|sdiv|srem|mul)(?: exact| nuw| nsw)* i32 (.*)$", Options);
        static readonly Regex FpToIntSat = new(@"@llvm\.fpto([su])i\.sat\.(i\d+)\.(f\d+)", Options);

        // Constructs that would need a rewrite of their own, and don't occur.
        static readonly string[] Unsupported = ["cmpxchg", "atomicrmw", "x ptr>", "byval(", "sret(", "va_arg"];

        int _temporaries;
        // The functions that replaced cells (cs_<name>), each with the type
        // it is called with ("ret (args)"), or null if no call was seen.
        readonly SortedDictionary<string, string?> _imports = new(StringComparer.Ordinal);
        // In the function being read: the values that hold one, by name.
        readonly Dictionary<string, string> _importIn = new(StringComparer.Ordinal);
        // The arithmetic helpers this module now calls (zero_udiv32, ...).
        readonly SortedSet<string> _helpers = new(StringComparer.Ordinal);
        // The attribute groups ("attributes #1 = { noinline optnone }") that
        // switch optimization off.
        readonly HashSet<string> _unoptimized;

        public Rewriter(string text) =>
            _unoptimized = Unoptimized.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

        // Declarations of the functions that replaced cells, unless the
        // module has them. Each has the type its calls use: the optimizer
        // treats a call to a function of another type as undefined.
        // And of the arithmetic helpers (zero_udiv32, ...).
        public IEnumerable<string> Declarations(string text) =>
            _imports.Where(i => !text.Contains($"@cs_{i.Key}(", StringComparison.Ordinal))
                .Select(i => i.Value is { } type
                    ? $"declare {type[..type.IndexOf(' ')]} @cs_{i.Key}{type[(type.IndexOf(' ') + 1)..]}"
                    : $"declare void @cs_{i.Key}()")
                .Concat(_helpers.Where(name => !text.Contains($"@{name}(", StringComparison.Ordinal))
                    .Select(name => $"declare i32 @{name}(i32, i32)"));

        public List<string> Line(string line)
        {
            if (line.StartsWith("define ", StringComparison.Ordinal))
                _importIn.Clear();
            List<string> result = Rewrite(line);
            if (_importIn.Count > 0)
            {
                foreach (string output in result)
                    NoteCalls(output);
            }
            return result;
        }

        // A call through a value that holds an import: what types it is called with.
        void NoteCalls(string line)
        {
            int call = line.IndexOf("call ", StringComparison.Ordinal);
            if (call < 0)
                return;
            foreach (var (value, name) in _importIn)
            {
                int at = line.IndexOf(" " + value + "(", call, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                string before = line[(call + 5)..at].Trim();
                string result = before[(before.LastIndexOf(' ') + 1)..];
                int open = at + value.Length + 2, depth = 1, close = open;
                for (; close < line.Length && depth > 0; close++)
                {
                    if (line[close] == '(')
                        depth++;
                    else if (line[close] == ')')
                        depth--;
                }
                string arguments = line[open..(close - 1)];
                var types = arguments.Trim().Length == 0
                    ? []
                    : Operands(arguments).Select(a => a[..(a + " ").IndexOf(' ')]).ToList();
                string type = $"{result} ({string.Join(", ", types)})";
                if (_imports[name] is { } known && known != type)
                    throw Fail($"cs_{name} is called as {known} and as {type} in one module");
                _imports[name] = type;
            }
        }

        // Named, so the numbered values around it keep their numbers.
        string Temporary() => $"%mos.{++_temporaries}";

        List<string> Rewrite(string line)
        {
            if (line.StartsWith("target datalayout", StringComparison.Ordinal)
                || line.StartsWith("target triple", StringComparison.Ordinal))
                return [];          // the 6502's, from the compiler's command line

            // After every call, compiled code checks a flag that says an
            // exception is on its way up. In zero mode nothing sets it: a throw
            // stops the program (zerocore's RhpThrowEx), and the code that
            // would set it sits behind a check of the flag itself. So the flag
            // reads as zero, and the optimizer removes the checks, the handlers
            // and every store: a few percent of a program, and a test after
            // every call. The flag isn't defined anywhere (c64/runtime.c), so a
            // program this is wrong for fails to link instead of misbehaving.
            Match m = Flag.Match(line);
            if (m.Success)
                return [$"{m.Groups[1].Value} = add i32 0, 0"];

            // A function C# imports ([DllImport]) is called through its
            // "cell", a slot that holds its address, and the cells are data
            // (Wasm2Mos.cs) that the optimizer can't see into: every call is
            // an indirect one. The cell always holds cs_<name>, so that is
            // what is called, and at link time the optimizer can inline it:
            // C64.Poke becomes one store instead of a call through a
            // pointer with 8 bytes of arguments.
            m = Cell.Match(line);
            if (m.Success)
            {
                string value = m.Groups[1].Value.Trim(), name = m.Groups[2].Value;
                _imports.TryAdd(name, null);
                _importIn[value] = name;
                return [$"{m.Groups[1].Value} = getelementptr i8, ptr @cs_{name}, i32 0"];
            }

            // C#'s 32-bit divisions, and multiplications of two variables,
            // call c64/runtime.c, which is several times faster than
            // llvm-mos's routines when the divisor or a factor fits in 16 bits,
            // as it usually does. A division by a power of two stays: the
            // optimizer makes it a shift. So does a multiplication by a
            // constant (an array index times the element's size): the
            // optimizer makes it a few shifts and adds.
            m = Arithmetic.Match(line);
            if (m.Success)
            {
                List<string> operands = Operands(m.Groups[3].Value);
                bool constant = operands.Count == 2 && (m.Groups[2].Value == "mul"
                    ? operands.Any(o => long.TryParse(o, out _))
                    : long.TryParse(operands[1], out long divisor) && divisor > 0 && (divisor & (divisor - 1)) == 0);
                if (operands.Count == 2 && !constant)
                {
                    string helper = $"zero_{m.Groups[2].Value}32";
                    _helpers.Add(helper);
                    return [$"{m.Groups[1].Value} = call i32 @{helper}(i32 {operands[0]}, i32 {operands[1]})"];
                }
            }

            m = Store.Match(line);
            if (m.Success)
            {
                string indent = m.Groups[1].Value, volatileWord = m.Groups[2].Value;
                List<string> parts = Operands(m.Groups[3].Value);   // the value, "ptr %where", "align 4"
                string t = Temporary();
                return [$"{indent}{t} = ptrtoint ptr {parts[0]} to i32",
                        $"{indent}store {volatileWord}i32 {t}, {string.Join(", ", parts.Skip(1))}"];
            }

            m = Extract.Match(line);
            if (m.Success)
            {
                string indent = m.Groups[1].Value, result = m.Groups[2].Value, rest = m.Groups[3].Value;
                var c = new Cursor(rest);
                IrType t = ParseType(c);
                List<string> parts = Operands(rest[c.At..]);    // the aggregate, then indices
                if (HasPointer(t) && Member(t, parts.Skip(1).Select(int.Parse)) is Scalar { Name: "ptr" })
                {
                    string number = Temporary();
                    return [$"{indent}{number} = extractvalue {Show(t)} {string.Join(", ", parts)}",
                            $"{indent}{result} = inttoptr i32 {number} to ptr"];
                }
                return [RewriteTypes(line)];
            }

            m = Insert.Match(line);
            if (m.Success)
            {
                string indent = m.Groups[1].Value, result = m.Groups[2].Value, rest = m.Groups[3].Value;
                var c = new Cursor(rest);
                IrType t = ParseType(c);
                List<string> parts = Operands(rest[c.At..]);    // the aggregate, "T value", indices
                if (HasPointer(t) && Member(t, parts.Skip(2).Select(int.Parse)) is Scalar { Name: "ptr" })
                {
                    if (!parts[1].StartsWith("ptr ", StringComparison.Ordinal))
                        throw Fail($"unexpected insertvalue: {line.Trim()}");
                    string number = Temporary();
                    return [$"{indent}{number} = ptrtoint {parts[1]} to i32",
                            $"{indent}{result} = insertvalue {Show(t)} {parts[0]}, i32 {number}, "
                            + string.Join(", ", parts.Skip(2))];
                }
                return [RewriteTypes(line)];
            }

            if (!line.TrimStart().StartsWith(';'))
            {
                foreach (string construct in Unsupported)
                {
                    if (line.Contains(construct, StringComparison.Ordinal))
                        throw Fail($"'{construct}' isn't supported: {line.Trim()}");
                }
            }

            // Every function is compiled for size above all: there are 50 KB
            // for the whole program, and this alone makes it an eighth smaller.
            // (Not the few the compiler marked "don't optimize": the two can't
            // be combined.)
            if (line.StartsWith("define ", StringComparison.Ordinal) && line.EndsWith(" {", StringComparison.Ordinal))
            {
                Match group = Group.Match(line);
                if (!group.Success || !_unoptimized.Contains(group.Groups[1].Value))
                    line = line[..^2] + " minsize optsize {";
            }

            line = AllocaPtr.Replace(line, "= alloca i32");
            line = GepPtr.Replace(line, "${1}i32,");
            // One thread, so thread-local variables are ordinary ones.
            line = line.Replace(" thread_local ", " ", StringComparison.Ordinal);
            // C# converts floating point to integers with saturation (NaN is
            // 0, out of range is the nearest limit), which the 6502 compiler
            // can't generate: c64/runtime.c has the conversions as functions.
            line = FpToIntSat.Replace(line, "@zero_fpto${1}i_sat_${2}_${3}");
            return [RewriteTypes(line)];
        }
    }

    // ---- signatures ------------------------------------------------------------------
    //
    // A function declared with one signature in the module that calls it and
    // defined with another in the module that has it works on a 32-bit
    // processor as long as the sizes agree: a pointer and a 32-bit integer
    // travel the same way. On the 6502 they use different registers, so the
    // call silently passes garbage. This finds such pairs, and wasm2mos asks
    // for signatures here.

    /// <summary>A function's result type and parameter types, as written in the IR.</summary>
    public sealed record Signature(string Result, string[] Parameters) : IComparable<Signature>
    {
        public bool Equals(Signature? other) =>
            other is not null && Result == other.Result && Parameters.SequenceEqual(other.Parameters);

        public override int GetHashCode() => HashCode.Combine(Result, string.Join(",", Parameters));

        // As Python compares (result, (parameters...)) tuples.
        public int CompareTo(Signature? other)
        {
            int order = string.CompareOrdinal(Result, other!.Result);
            for (int i = 0; order == 0 && i < Math.Min(Parameters.Length, other.Parameters.Length); i++)
                order = string.CompareOrdinal(Parameters[i], other.Parameters[i]);
            return order != 0 ? order : Parameters.Length.CompareTo(other.Parameters.Length);
        }

        public override string ToString() =>
            $"('{Result}', ({string.Join(", ", Parameters.Select(p => $"'{p}'"))}{(Parameters.Length == 1 ? "," : "")}))";
    }

    static readonly Regex Function = new(@"^(define|declare)\s+(.*?)@(""[^""]+""|[\w.$]+)\((.*)$", Options);
    static readonly Regex Alias = new(@"^@(""[^""]+""|[\w.$]+) = alias (\S+) \((.*?)\), ptr @", Options);
    static readonly char[] Whitespace = [' ', '\t', '\n', '\r', '\f', '\v'];

    /// <summary>The parameter list's types, from the text after the opening bracket.</summary>
    static string[] ArgumentTypes(string text)
    {
        int depth = 0, end = text.Length;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                if (depth == 0)
                {
                    end = i;
                    break;
                }
                depth--;
            }
        }
        return Operands(text[..end])
            .Where(p => p.Length > 0)
            .Select(p => p.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToArray();
    }

    /// <summary>Definitions by name, and every signature each name is declared with.</summary>
    public static (Dictionary<string, Signature> Defined, Dictionary<string, HashSet<Signature>> Declared)
        Signatures(IEnumerable<string> paths)
    {
        var defined = new Dictionary<string, Signature>(StringComparer.Ordinal);
        var declared = new Dictionary<string, HashSet<Signature>>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            foreach (string line in Files.ReadText(path).Split('\n'))
            {
                Match m = Alias.Match(line);
                if (m.Success)
                {
                    defined[m.Groups[1].Value.Trim('"')] =
                        new Signature(m.Groups[2].Value, ArgumentTypes(m.Groups[3].Value + ")"));
                    continue;
                }
                m = Function.Match(line);
                if (m.Success)
                {
                    string before = m.Groups[2].Value;
                    var signature = new Signature(
                        before.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)[^1],
                        ArgumentTypes(m.Groups[4].Value));
                    string name = m.Groups[3].Value.Trim('"');
                    if (m.Groups[1].Value == "define")
                        defined[name] = signature;
                    else if (declared.TryGetValue(name, out var found))
                        found.Add(signature);
                    else
                        declared[name] = [signature];
                }
            }
        }
        return (defined, declared);
    }

    static void CheckSignatures(IEnumerable<string> paths)
    {
        var (defined, declared) = Signatures(paths);
        foreach (var (name, found) in declared.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            foreach (Signature signature in found.Order())
            {
                if (defined.TryGetValue(name, out Signature? definition) && !signature.Equals(definition))
                    throw Fail($"{name} is defined as {definition} and called as {signature}: "
                               + "on the 6502 the arguments or the result would be lost");
            }
        }
    }
}
