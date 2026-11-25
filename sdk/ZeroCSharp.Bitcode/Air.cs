// zero-bitcode ll2air OUT.ll ASSEMBLY MODULE.ll...
//
// C# methods, compiled by NativeAOT-LLVM to (wasm32) LLVM IR, as Metal kernels:
// AIR, the LLVM dialect Apple's Metal compiler reads, as text for `metal -c`.
//
// The kernels are ASSEMBLY's [RuntimeExport("Name")] methods (NativeAOT-LLVM
// writes `@Name = alias ..., ptr @ASSEMBLY_...` for each), named after the
// export. A kernel's parameters, in order, become Metal arguments [[buffer(0)]],
// [[buffer(1)]], ...: a pointer is a device buffer, any other value is a scalar
// (`constant T&`, set by the host with setBytes). Inside a kernel these calls
// (lib/Gpu.cs: [DllImport] + [SuppressGCTransition]) are GPU built-ins:
//
//   gpu_thread_x/y   thread_position_in_grid .x/.y
//   gpu_local_x/y    thread_position_in_threadgroup .x/.y
//   gpu_group_x/y    threadgroup_position_in_grid .x/.y
//   gpu_group_w/h    threads_per_threadgroup .x/.y
//   gpu_grid_w/h     threads_per_grid .x/.y
//   gpu_barrier      threadgroup_barrier(mem_flags::mem_threadgroup)
//   gpu_shared       the threadgroup memory [[threadgroup(0)]]; the host sets its size
//
// and the atomics, gpu_atomic_<op>_<type>(pointer, value[, comparand]), op one
// of add sub min max and or xor xchg cas, type i32 u32 or f32 (add and xchg
// only), on device or threadgroup memory; each returns the old value. They
// become Metal's air.atomic.* functions, relaxed, scoped to the device or the
// threadgroup by where the pointer points; cas is Metal's compare-exchange.
//
// Anything else that can't run on a GPU (calls into the runtime, allocation,
// stackalloc, double) is an error.
//
// The rewrites, besides those:
// - the first parameter (NativeAOT-LLVM's shadow stack) is dropped; it must be
//   unused once the rest is done
// - null checks (`icmp ult ptr %p, inttoptr (i32 1024 to ptr)`) -> false: Metal
//   memory is never at address 0; the throw paths they guard become dead
// - GC polls and RhpExceptionThrown reads -> 0, so the polls are dead too
// - opaque `ptr` -> `i8 addrspace(N)*` (AIR is LLVM from before opaque pointers),
//   N being where the pointer points: 1 device, 2 constant, 3 threadgroup, found
//   by following each pointer back to the parameter or gpu_shared() it came
//   from; loads and stores get a bitcast to the accessed type first
//
// It began as gpu/ll2air.py, removed after this port was checked to write the
// same output, byte for byte.
using System.Text.RegularExpressions;

namespace ZeroBitcode;

static class Air
{
    const string DataLayout =
        "e-p:64:64:64-i1:8:8-i8:8:8-i16:16:16-i32:32:32-i64:64:64-f32:32:32-f64:64:64"
        + "-v16:16:16-v24:32:32-v32:32:32-v48:64:64-v64:64:64-v96:128:128-v128:128:128"
        + "-v192:256:256-v256:256:256-v512:512:512-v1024:1024:1024-n8:16:32";
    const int Device = 1, Constant = 2, Threadgroup = 3;
    static readonly string[] AtomicOps = ["add", "sub", "min", "max", "and", "or", "xor", "xchg", "cas"];

    // Each built-in is a uint2 argument (a 1D launch has y = 0); a call is one of its halves.
    static readonly Dictionary<string, (string Builtin, int Half)> Builtins = MakeBuiltins();
    static readonly Dictionary<string, (string Name, int Size)> Scalars = new()
    {
        ["i8"] = ("char", 1), ["i16"] = ("short", 2), ["i32"] = ("int", 4), ["i64"] = ("long", 8), ["float"] = ("float", 4),
    };

    const string Type = @"(<[^>]+>|\[[^\]]+\]|[\w.]+)";
    const string Value = @"([%@][\w.$""-]+|[-\w.]+)";
    const string Name = @"(%[\w.]+)";

    static Dictionary<string, (string, int)> MakeBuiltins()
    {
        var builtins = new Dictionary<string, (string, int)>();
        foreach (var (prefix, builtin) in new[] { ("thread", "thread_position_in_grid"),
                     ("local", "thread_position_in_threadgroup"), ("group", "threadgroup_position_in_grid") })
        {
            builtins[$"gpu_{prefix}_x"] = (builtin, 0);
            builtins[$"gpu_{prefix}_y"] = (builtin, 1);
        }
        foreach (var (prefix, builtin) in new[] { ("group", "threads_per_threadgroup"), ("grid", "threads_per_grid") })
        {
            builtins[$"gpu_{prefix}_w"] = (builtin, 0);
            builtins[$"gpu_{prefix}_h"] = (builtin, 1);
        }
        return builtins;
    }

    static ToolFailure Fail(string message) => new("ll2air", message);

    // Python's re.match: anchored at the start only.
    internal static Match At(string input, string pattern) => Regex.Match(input, @"\G(?:" + pattern + ")");

    public static void Run(string output, string assembly, string[] inputs)
    {
        string[] sources = inputs.Select(Files.ReadText).ToArray();
        var found = KernelsOf(sources, assembly);
        if (found.Count == 0)
            throw Fail($"{assembly} has no kernels: mark them [RuntimeExport(\"Name\")]");
        var kernels = found.Select(k => Convert(k.Function, FindFunction(sources, k.Function), k.Export)).ToList();
        Files.WriteText(output, Module(kernels, sources));
    }

    internal static List<string> FindFunction(string[] sources, string name)
    {
        var start = new Regex(@"^define [^@]*@" + Regex.Escape(name) + @"\(");
        foreach (string text in sources)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!start.IsMatch(lines[i]))
                    continue;
                var body = new List<string>();
                for (int j = i; j < lines.Length; j++)
                {
                    body.Add(lines[j]);
                    if (lines[j] == "}")
                        return body;
                }
            }
        }
        throw Fail($"{name}: not found in the IR");
    }

    static string Pointer(int space) => $"i8 addrspace({space})*";

    /// <summary>A type with ptr in it, as AIR spells it (ptr inside a type: device).</summary>
    static string Typed(string t, int space) =>
        t == "ptr" ? Pointer(space) : Regex.Replace(t, @"\bptr\b", Pointer(Device));

    // `#N` attribute groups and `!dbg !N`-style metadata attachments.
    internal static string StripAttachments(string line) =>
        Regex.Replace(Regex.Replace(line, @"\s#\d+", ""), @",\s*!\w[\w.]*\s+!\d+", "");

    /// <summary>Where each pointer value points, following GEPs, phis and selects.</summary>
    static Dictionary<string, int> AddressSpaces(List<string> lines, IEnumerable<string> buffers, List<string> shared)
    {
        var space = buffers.ToDictionary(b => b, _ => Device);
        foreach (string name in shared)
            space[name] = Threadgroup;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string s in lines)
            {
                Match m = At(s, Name + @" = (getelementptr|phi|select|bitcast|load ptr)\b(.*)");
                if (!m.Success || space.ContainsKey(m.Groups[1].Value))
                    continue;
                HashSet<int> found;
                if (m.Groups[2].Value == "load ptr")
                    found = [Device]; // a pointer stored in memory: assumed to be device memory
                else
                    found = Regex.Matches(m.Groups[3].Value, Name).Select(v => v.Value)
                        .Where(space.ContainsKey).Select(v => space[v]).ToHashSet();
                if (found.Count > 1)
                    throw Fail($"{m.Groups[1].Value} may point to more than one kind of memory: {s}");
                if (found.Count == 1)
                {
                    space[m.Groups[1].Value] = found.First();
                    changed = true;
                }
            }
        }
        return space;
    }

    /// <summary>
    /// RyuJIT keeps the address of an element it reads and then writes (x[i] += y)
    /// as an integer: `%a = ptrtoint ptr %p to i32` ... `%q = inttoptr i32 %a to ptr`.
    /// An i32 can't hold a GPU's 64-bit address, so each such %q becomes %p itself;
    /// an integer from ptrtoint that is used for anything else is an error.
    /// </summary>
    internal static List<string> PointerRoundTrips(string name, List<string> lines)
    {
        const string toInt = @"\s*" + Name + @" = ptrtoint ptr " + Name + @" to i32$";
        var origin = new Dictionary<string, string>();
        foreach (string s in lines)
        {
            Match m = At(s, toInt);
            if (m.Success)
                origin[m.Groups[1].Value] = m.Groups[2].Value;
        }
        var output = new List<string>();
        foreach (string s in lines)
        {
            if (At(s, toInt).Success)
                continue;
            Match m = At(s, @"\s*" + Name + @" = inttoptr i32 " + Name + @" to ptr$");
            if (m.Success && origin.TryGetValue(m.Groups[2].Value, out string? pointer))
            {
                output.Add($"  {m.Groups[1].Value} = getelementptr i8, ptr {pointer}, i32 0");
                continue;
            }
            foreach (string a in origin.Keys)
                if (Regex.IsMatch(s, Regex.Escape(a) + @"\b"))
                    throw Fail($"{name}: uses a pointer as an integer: {s.Trim()}");
            output.Add(s);
        }
        return output;
    }

    /// <summary>(export name, function) for each of the assembly's [RuntimeExport] methods.</summary>
    internal static List<(string Export, string Function)> KernelsOf(string[] sources, string assembly)
    {
        // NativeAOT's symbol names begin with the assembly's, with anything but
        // letters, digits and _ made _ (My.App: My_App_...).
        string prefix = Regex.Replace(assembly, "[^A-Za-z0-9_]", "_");
        var found = new Dictionary<string, string>();
        var alias = new Regex(@"^@""?([\w.$]+)""? = alias [^\n]*, ptr @(" + Regex.Escape(prefix) + @"_[\w.$]+)$", RegexOptions.Multiline);
        foreach (string text in sources)
            foreach (Match m in alias.Matches(text))
                if (!m.Groups[2].Value.StartsWith(prefix + "__Module___", StringComparison.Ordinal)) // the program's entry point
                    found[m.Groups[1].Value] = m.Groups[2].Value;
        return found.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => (k.Key, k.Value)).ToList();
    }

    sealed record Kernel(string Short, string Signature, List<string> Meta, string Function, HashSet<string> Intrinsics);

    static Kernel Convert(string name, List<string> body, string shortName)
    {
        string header = body[0];
        List<string> lines = PointerRoundTrips(name, body.Skip(1).Take(body.Count - 2).Select(StripAttachments).ToList());
        Match m = At(header, @"define (\w+) @[\w.$]+\((.*)\)");
        if (!m.Success || m.Groups[1].Value != "void")
            throw Fail($"{name}: a kernel must return void");
        string[][] parameters = m.Groups[2].Value.Split(',').Where(p => p.Trim() != "")
            .Select(p => p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        string shadowStack = parameters[0][^1];
        var args = new List<(string Kind, string Type, string Register)>();
        foreach (string[] p in parameters.Skip(1))
        {
            string ptype = p[0], pname = p[^1];
            if (ptype == "ptr")
                args.Add(("buffer", ptype, pname));
            else if (Scalars.ContainsKey(ptype))
                args.Add(("scalar", ptype, pname));
            else
                throw Fail($"{name}: parameter {pname} is {ptype}, which a GPU kernel can't take");
        }

        // What each gpu_* function pointer register is.
        var cells = new Dictionary<string, string>();
        foreach (string s in lines)
        {
            Match mm = At(s, @"\s*" + Name + @" = load ptr, ptr @cell\.(gpu_\w+)");
            if (mm.Success)
                cells[mm.Groups[1].Value] = mm.Groups[2].Value;
        }
        var shared = new List<string>();
        foreach (string s in lines)
        {
            Match mm = At(s, @"\s*" + Name + @" = (?:tail )?call ptr " + Name + @"\(\)");
            if (mm.Success && cells.GetValueOrDefault(mm.Groups[2].Value) == "gpu_shared")
                shared.Add(mm.Groups[1].Value);
        }
        Dictionary<string, int> space = AddressSpaces(lines.Select(l => l.Trim()).ToList(),
            args.Where(a => a.Kind == "buffer").Select(a => a.Register), shared);
        int SpaceOf(string? value) => value != null && space.TryGetValue(value, out int sp) ? sp : Device;

        // `ptr %x` -> `i8 addrspace(N)* %x`; a lone `ptr` takes the result's space.
        string Retype(string s, string? result = null)
        {
            s = Regex.Replace(s, @"\bptr (%[\w.]+)", mm => $"{Pointer(SpaceOf(mm.Groups[1].Value))} {mm.Groups[1].Value}");
            return Regex.Replace(s, @"\bptr\b", Pointer(SpaceOf(result)));
        }

        var builtinsUsed = new List<string>();
        var output = new List<string>();
        var intrinsics = new HashSet<string>();
        var allocas = new List<string>();
        int temp = 0;

        // gpu_atomic_<op>_<type>(p, v[, comparand]) -> air.atomic.<space>.<op>...
        List<string> Atomic(string fn, string result, List<string> callArgs, string line)
        {
            Match am = Regex.Match(fn, @"^gpu_atomic_(\w+)_(i32|u32|f32)$");
            if (!am.Success || !AtomicOps.Contains(am.Groups[1].Value))
                throw Fail($"{name}: {fn} is not a GPU built-in");
            string op = am.Groups[1].Value, kind = am.Groups[2].Value;
            if (kind == "f32" && op != "add" && op != "xchg")
                throw Fail($"{name}: Metal has no atomic {op} on floats");
            string p = Last(callArgs[0]);
            int sp = SpaceOf(p);
            if (sp != Device && sp != Threadgroup)
                throw Fail($"{name}: an atomic needs device or threadgroup memory: {line}");
            var (where, scope) = sp == Device ? ("global", 2) : ("local", 1);
            string t = kind == "f32" ? "float" : "i32";
            string tail = op is "xchg" or "cas" || kind == "f32" ? "" : kind == "i32" ? ".s" : ".u";
            string air = op == "cas" ? "cmpxchg.weak" : op;
            string fname = $"@air.atomic.{where}.{air}{tail}.{(kind == "f32" ? "f32" : "i32")}";
            temp++;
            var linesOut = new List<string> { $"  %air.cast.{temp} = bitcast {Pointer(sp)} {p} to {t} addrspace({sp})*" };
            string value = Last(callArgs[1]);
            if (op == "cas")
            {
                // Metal's compare-exchange reads the expected value from (and writes
                // the old one to) private memory; it returns the old value too.
                string comparand = Last(callArgs[2]);
                string expected = $"%air.expected.{temp}";
                allocas.Add($"  {expected} = alloca i32, align 4");
                linesOut.Add($"  store i32 {comparand}, i32* {expected}, align 4");
                linesOut.Add($"  {result} = call i32 {fname}(i32 addrspace({sp})* %air.cast.{temp}, i32* {expected}, "
                             + $"i32 {value}, i32 0, i32 0, i32 {scope}, i32 0, i1 false) #2");
                intrinsics.Add($"declare i32 {fname}(i32 addrspace({sp})*, i32*, i32, i32, i32, i32, i32, i1) #2");
            }
            else
            {
                string call = $"call {t} {fname}({t} addrspace({sp})* %air.cast.{temp}, {t} {value}, i32 0, i32 {scope}, i32 0, i1 false) #2";
                linesOut.Add(result != "" ? $"  {result} = {call}" : $"  {call}");
                intrinsics.Add($"declare {t} {fname}({t} addrspace({sp})*, {t}, i32, i32, i32, i1) #2");
            }
            return linesOut;
        }

        foreach (string line in lines)
        {
            string s = line.Trim();
            if (s == "" || s.EndsWith(':') || At(s, @"[\w.]+:\s*;").Success)
            {
                output.Add(line);
                continue;
            }
            if (At(s, Name + @" = load ptr, ptr @cell\.gpu_\w+").Success)
                continue;
            Match mm = At(s, @"(?:" + Name + @" = )?(?:tail )?call (\w+) " + Name + @"\(\)");
            if (mm.Success && cells.TryGetValue(mm.Groups[3].Value, out string? fn))
            {
                if (Builtins.TryGetValue(fn, out var builtin))
                {
                    if (!builtinsUsed.Contains(builtin.Builtin))
                        builtinsUsed.Add(builtin.Builtin);
                    output.Add($"  {mm.Groups[1].Value} = extractelement <2 x i32> %{builtin.Builtin}, i64 {builtin.Half}");
                }
                else if (fn == "gpu_barrier")
                {
                    output.Add("  call void @air.wg.barrier(i32 2, i32 5, i32 1) #1");
                    intrinsics.Add("declare void @air.wg.barrier(i32, i32, i32) #1");
                }
                else if (fn == "gpu_shared")
                    output.Add($"  {mm.Groups[1].Value} = getelementptr i8, {Pointer(Threadgroup)} %gpu_shared, i32 0");
                else
                    throw Fail($"{name}: {fn} is not a GPU built-in");
                continue;
            }
            mm = At(s, @"(?:" + Name + @" = )?(?:tail )?call (\w+) " + Name + @"\((.+)\)$");
            if (mm.Success && cells.TryGetValue(mm.Groups[3].Value, out fn))
            {
                var callArgs = mm.Groups[4].Value.Split(',').Select(a => a.Trim()).ToList();
                output.AddRange(Atomic(fn, mm.Groups[1].Value, callArgs, s));
                continue;
            }
            mm = At(s, Name + @" = load i32, ptr @(RhpTrapThreads|RhpExceptionThrown)");
            if (mm.Success)
            {
                output.Add($"  {mm.Groups[1].Value} = add i32 0, 0");
                continue;
            }
            mm = At(s, Name + @" = icmp ult ptr " + Name + @", inttoptr \(i32 1024 to ptr\)");
            if (mm.Success)
            {
                output.Add($"  {mm.Groups[1].Value} = icmp eq i32 0, 1");
                continue;
            }
            if (At(s, @"call void @(\w*RhpGcPoll|S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__Throw\w+)\(").Success)
                continue;
            mm = Regex.Match(s, @"\bcall\b[^@%]*([@%][\w.$]+)");
            if (mm.Success && mm.Groups[1].Value.StartsWith("@llvm.", StringComparison.Ordinal))
                intrinsics.Add(mm.Groups[1].Value);
            else if (mm.Success)
                throw Fail($"{name}: calls {mm.Groups[1].Value}, which can't run on the GPU");
            if (Regex.IsMatch(s, @"\balloca\b"))
                throw Fail($"{name}: uses the stack (alloca); a kernel's locals must fit in registers");
            if (Regex.IsMatch(s, @"\b(ptrtoint|inttoptr)\b"))
                throw Fail($"{name}: converts between pointers and integers: {s}");
            if (Regex.IsMatch(s, @"\bdouble\b"))
                throw Fail($"{name}: uses double, which Metal doesn't have (use float)");

            mm = At(s, Name + @" = load (volatile )?" + Type + @", ptr " + Value + @"(.*)$");
            if (mm.Success)
            {
                string res = mm.Groups[1].Value, vol = mm.Groups[2].Value, t = mm.Groups[3].Value, p = mm.Groups[4].Value, rest = mm.Groups[5].Value;
                int sp = SpaceOf(p);
                temp++;
                output.Add($"  %air.cast.{temp} = bitcast {Pointer(sp)} {p} to {Typed(t, Device)} addrspace({sp})*");
                output.Add($"  {res} = load {vol}{Typed(t, Device)}, {Typed(t, Device)} addrspace({sp})* %air.cast.{temp}{rest}");
                continue;
            }
            mm = At(s, @"store (volatile )?" + Type + " " + Value + @", ptr " + Value + @"(.*)$");
            if (mm.Success)
            {
                string vol = mm.Groups[1].Value, t = mm.Groups[2].Value, v = mm.Groups[3].Value, p = mm.Groups[4].Value, rest = mm.Groups[5].Value;
                int sp = SpaceOf(p);
                temp++;
                output.Add($"  %air.cast.{temp} = bitcast {Pointer(sp)} {p} to {Typed(t, Device)} addrspace({sp})*");
                output.Add($"  store {vol}{Retype(t + " " + v)}, {Typed(t, Device)} addrspace({sp})* %air.cast.{temp}{rest}");
                continue;
            }
            Match result = At(s, Name + " = ");
            output.Add("  " + Retype(s, result.Success ? result.Groups[1].Value : null));
        }

        // Scalars arrive as constant references: load them at the start.
        var entry = allocas.Concat(args.Where(a => a.Kind == "scalar")
            .Select(a => $"  {a.Register} = load {a.Type}, {a.Type} addrspace({Constant})* {a.Register}.ref")).ToList();
        int at = output.Count > 0 && output[0].Trim().EndsWith(':') ? 1 : 0; // after the entry block's label
        output.InsertRange(at, entry);

        string text = string.Join("\n", output);
        if (Regex.IsMatch(text, Regex.Escape(shadowStack) + @"\b"))
            throw Fail($"{name}: still uses the shadow stack after the rewrite");
        // Dropping parameter %0 breaks LLVM's rule that unnamed values count up
        // from %0, so give every numbered value and block a name.
        text = Regex.Replace(text, @"%(\d+)\b", "%v$1");
        text = Regex.Replace(text, @"^(\d+):", "v$1:", RegexOptions.Multiline);

        static string Reg(string r) => r[1..].All(char.IsAsciiDigit) && r.Length > 1 ? "%v" + r[1..] : r;

        // Metal arguments: the C# parameters, then threadgroup memory, then built-ins.
        var sig = new List<string>();
        var decl = new List<string>();
        var meta = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var (kind, t, r) = args[i];
            if (kind == "buffer")
            {
                sig.Add(Pointer(Device));
                decl.Add($"{Pointer(Device)} noundef \"air-buffer-no-alias\" {Reg(r)}");
                meta.Add($"!\"air.buffer\", !\"air.location_index\", i32 {i}, i32 1, !\"air.read_write\", "
                         + "!\"air.address_space\", i32 1, !\"air.arg_type_size\", i32 1, !\"air.arg_type_align_size\", i32 1, "
                         + $"!\"air.arg_type_name\", !\"uchar\", !\"air.arg_name\", !\"{Reg(r)[1..]}\"");
            }
            else
            {
                var (tname, size) = Scalars[t];
                sig.Add($"{t} addrspace(2)*");
                decl.Add($"{t} addrspace(2)* noundef \"air-buffer-no-alias\" {Reg(r)}.ref");
                meta.Add($"!\"air.buffer\", !\"air.buffer_size\", i32 {size}, !\"air.location_index\", i32 {i}, i32 1, !\"air.read\", "
                         + $"!\"air.address_space\", i32 2, !\"air.arg_type_size\", i32 {size}, !\"air.arg_type_align_size\", i32 {size}, "
                         + $"!\"air.arg_type_name\", !\"{tname}\", !\"air.arg_name\", !\"{Reg(r)[1..]}\"");
            }
        }
        if (shared.Count > 0)
        {
            sig.Add(Pointer(Threadgroup));
            decl.Add($"{Pointer(Threadgroup)} noundef \"air-buffer-no-alias\" %gpu_shared");
            meta.Add("!\"air.buffer\", !\"air.location_index\", i32 0, i32 1, !\"air.read_write\", "
                     + "!\"air.address_space\", i32 3, !\"air.arg_type_size\", i32 1, !\"air.arg_type_align_size\", i32 1, "
                     + "!\"air.arg_type_name\", !\"uchar\", !\"air.arg_name\", !\"gpu_shared\"");
        }
        foreach (string builtin in builtinsUsed)
        {
            sig.Add("<2 x i32>");
            decl.Add($"<2 x i32> noundef %{builtin}");
            meta.Add($"!\"air.{builtin}\", !\"air.arg_type_name\", !\"uint2\", !\"air.arg_name\", !\"{builtin}\"");
        }
        meta = meta.Select((m2, i) => $"!{{i32 {i}, {m2}}}").ToList();

        string function = $"define void @{shortName}({string.Join(", ", decl)}) #0 {{\n{text}\n}}\n";
        return new Kernel(shortName, string.Join(", ", sig), meta, function, intrinsics);
    }

    internal static string Last(string argument) => argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1];

    static string Module(List<Kernel> kernels, string[] sources)
    {
        // No target triple: metal sets its own (the AIR version and the macOS it targets).
        var output = new List<string> { $"target datalayout = \"{DataLayout}\"", "" };
        var declares = new HashSet<string>();
        foreach (Kernel kernel in kernels)
        {
            output.Add(kernel.Function);
            foreach (string i in kernel.Intrinsics)
            {
                if (i.StartsWith("declare", StringComparison.Ordinal))
                {
                    declares.Add(i);
                    continue;
                }
                foreach (string text in sources) // an @llvm.* intrinsic: take its declaration from the input
                {
                    Match mm = Regex.Match(text, @"^declare [^\n]*" + Regex.Escape(i) + @"\(.*$", RegexOptions.Multiline);
                    if (mm.Success)
                    {
                        declares.Add(Regex.Replace(StripAttachments(mm.Value), @"\bptr\b", Pointer(Device)));
                        break;
                    }
                }
            }
        }
        output.AddRange(declares.Order(StringComparer.Ordinal));
        output.Add("attributes #0 = { nounwind \"frame-pointer\"=\"all\" }");
        output.Add("attributes #1 = { convergent nounwind willreturn }");
        output.Add("attributes #2 = { nounwind willreturn }\n");

        var meta = new List<string>();
        var refs = new List<string>();
        int n = 0;
        string Node(string text)
        {
            meta.Add($"!{n} = {text}");
            n++;
            return $"!{n - 1}";
        }

        string empty = Node("!{}");
        foreach (Kernel kernel in kernels)
        {
            string arglist = Node("!{" + string.Join(", ", kernel.Meta.Select(Node)) + "}");
            refs.Add(Node($"!{{void ({kernel.Signature})* @{kernel.Short}, {empty}, {arglist}}}"));
        }
        var flags = new[]
        {
            "!{i32 7, !\"air.max_device_buffers\", i32 31}",
            "!{i32 7, !\"air.max_constant_buffers\", i32 31}",
            "!{i32 7, !\"air.max_threadgroup_buffers\", i32 31}",
            "!{i32 7, !\"air.max_textures\", i32 128}",
            "!{i32 7, !\"air.max_read_write_textures\", i32 8}",
            "!{i32 7, !\"air.max_samplers\", i32 16}",
        }.Select(Node).ToList();
        string version = Node("!{i32 2, i32 9, i32 0}");
        string language = Node("!{!\"Metal\", i32 4, i32 1, i32 0}");
        output.Add("!air.kernel = !{" + string.Join(", ", refs) + "}");
        output.Add("!llvm.module.flags = !{" + string.Join(", ", flags) + "}");
        output.Add($"!air.version = !{{{version}}}");
        output.Add($"!air.language_version = !{{{language}}}");
        output.AddRange(meta);
        return string.Join("\n", output) + "\n";
    }
}
