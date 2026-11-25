// zero-bitcode ll2ptx OUT.ll ASSEMBLY MODULE.ll...
//
// C# methods, compiled by NativeAOT-LLVM to (wasm32) LLVM IR, as CUDA kernels:
// LLVM IR for the NVPTX backend (nvptx64-nvidia-cuda), which `llc -march=nvptx64`
// turns into PTX, the text NVIDIA's driver compiles for its GPUs.
//
// The kernels are found as for Metal (Air.cs): ASSEMBLY's [RuntimeExport("Name")]
// methods, named after the export. A kernel's parameters stay in order: a
// pointer is a device pointer (a CUdeviceptr from the host), any other value
// is passed by value. lib/Gpu.cs's built-ins become:
//
//   gpu_thread_x/y   ctaid * ntid + tid        (the thread's position in the grid)
//   gpu_local_x/y    tid
//   gpu_group_x/y    ctaid
//   gpu_group_w/h    ntid
//   gpu_grid_w/h     the grid's width and height, two parameters added after
//                    the kernel's own
//   gpu_barrier      llvm.nvvm.barrier0 (__syncthreads)
//   gpu_shared       the dynamic shared memory, `extern __shared__`; the host sets its size
//
// and the atomics become LLVM's atomicrmw and cmpxchg (relaxed), which NVPTX
// turns into atom.* on whatever memory the pointer points to.
//
// Metal runs exactly width x height threads; CUDA runs whole blocks, so the
// last ones can reach past the grid. Each kernel starts with a check that
// ends the threads outside it, as Metal never starts them. (They have exited
// before any barrier, which CUDA counts as arrived.)
//
// The other rewrites are Metal's: the shadow stack parameter goes, null checks
// are false, GC polls are dead, and RyuJIT's pointer-to-i32-and-back round
// trips become the pointer. NVPTX takes opaque pointers, so unlike AIR nothing
// else changes; generic pointers reach global and shared memory alike.
using System.Text.RegularExpressions;

namespace ZeroBitcode;

static class Ptx
{
    const string DataLayout = "e-i64:64-i128:128-v16:16-v32:32-n16:32:64";
    const string Name = @"(%[\w.]+)";
    static readonly HashSet<string> Scalars = ["i8", "i16", "i32", "i64", "float", "double"];

    static ToolFailure Fail(string message) => new("ll2ptx", message);

    static Match At(string input, string pattern) => Air.At(input, pattern);

    public static void Run(string output, string assembly, string[] inputs)
    {
        string[] sources = inputs.Select(Files.ReadText).ToArray();
        var found = Air.KernelsOf(sources, assembly);
        if (found.Count == 0)
            throw Fail($"{assembly} has no kernels: mark them [RuntimeExport(\"Name\")]");
        var declares = new SortedSet<string>(StringComparer.Ordinal);
        bool shared = false;
        var functions = new List<string>();
        foreach (var (export, function) in found)
        {
            functions.Add(Convert(function, Air.FindFunction(sources, function), export, sources, declares, out bool usesShared));
            shared |= usesShared;
        }

        var text = new List<string>
        {
            $"target datalayout = \"{DataLayout}\"",
            "target triple = \"nvptx64-nvidia-cuda\"",
            "",
        };
        if (shared)
            text.Add("@gpu_shared = external addrspace(3) global [0 x i8], align 16\n");
        text.AddRange(functions);
        text.AddRange(declares);
        text.Add("");
        text.Add("attributes #0 = { nounwind }");
        text.Add("attributes #1 = { convergent nounwind }");
        text.Add("");
        // Each kernel is marked as one (an entry point the host can launch).
        var annotations = new List<string>();
        for (int i = 0; i < found.Count; i++)
        {
            text.Add($"!{i} = !{{ptr @{found[i].Export}, !\"kernel\", i32 1}}");
            annotations.Add($"!{i}");
        }
        text.Add("!nvvm.annotations = !{" + string.Join(", ", annotations) + "}");
        Files.WriteText(output, string.Join("\n", text) + "\n");
    }

    static string Convert(string name, List<string> body, string shortName, string[] sources,
        SortedSet<string> declares, out bool usesShared)
    {
        usesShared = false;
        string header = body[0];
        List<string> lines = Air.PointerRoundTrips(name, body.Skip(1).Take(body.Count - 2).Select(Air.StripAttachments).ToList());
        Match m = At(header, @"define (\w+) @[\w.$]+\((.*)\)");
        if (!m.Success || m.Groups[1].Value != "void")
            throw Fail($"{name}: a kernel must return void");
        string[][] parameters = m.Groups[2].Value.Split(',').Where(p => p.Trim() != "")
            .Select(p => p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        string shadowStack = parameters[0][^1];
        var args = new List<string>();
        foreach (string[] p in parameters.Skip(1))
        {
            string ptype = p[0], pname = p[^1];
            if (ptype != "ptr" && !Scalars.Contains(ptype))
                throw Fail($"{name}: parameter {pname} is {ptype}, which a GPU kernel can't take");
            args.Add($"{ptype} {pname}");
        }

        var cells = new Dictionary<string, string>();
        foreach (string s in lines)
        {
            Match mm = At(s, @"\s*" + Name + @" = load ptr, ptr @cell\.(gpu_\w+)");
            if (mm.Success)
                cells[mm.Groups[1].Value] = mm.Groups[2].Value;
        }

        var output = new List<string>();
        int temp = 0;
        string Register(string sreg)
        {
            string fn = $"@llvm.nvvm.read.ptx.sreg.{sreg}";
            declares.Add($"declare i32 {fn}() #0");
            string r = $"%ptx.{++temp}";
            output.Add($"  {r} = call i32 {fn}()");
            return r;
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
                string result = mm.Groups[1].Value;
                Match b = Regex.Match(fn, @"^gpu_(thread|local|group|grid)_(x|y|w|h)$");
                if (b.Success)
                {
                    string kind = b.Groups[1].Value, axis = b.Groups[2].Value is "x" or "w" ? "x" : "y";
                    string value = (kind, b.Groups[2].Value) switch
                    {
                        ("thread", _) => Mad(Register($"ctaid.{axis}"), Register($"ntid.{axis}"), Register($"tid.{axis}")),
                        ("local", _) => Register($"tid.{axis}"),
                        ("group", "x" or "y") => Register($"ctaid.{axis}"),
                        ("group", _) => Register($"ntid.{axis}"),
                        ("grid", _) => axis == "x" ? "%gpu.width" : "%gpu.height",
                        _ => throw Fail($"{name}: {fn} is not a GPU built-in"),
                    };
                    output.Add($"  {result} = add i32 {value}, 0");
                }
                else if (fn == "gpu_barrier")
                {
                    declares.Add("declare void @llvm.nvvm.barrier0() #1");
                    output.Add("  call void @llvm.nvvm.barrier0()");
                }
                else if (fn == "gpu_shared")
                {
                    usesShared = true;
                    output.Add($"  {result} = addrspacecast ptr addrspace(3) @gpu_shared to ptr");
                }
                else
                    throw Fail($"{name}: {fn} is not a GPU built-in");
                continue;
            }
            mm = At(s, @"(?:" + Name + @" = )?(?:tail )?call (\w+) " + Name + @"\((.+)\)$");
            if (mm.Success && cells.TryGetValue(mm.Groups[3].Value, out fn))
            {
                var callArgs = mm.Groups[4].Value.Split(',').Select(a => Air.Last(a.Trim())).ToList();
                output.AddRange(Atomic(name, fn, mm.Groups[1].Value, callArgs, ref temp));
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
                declares.Add(Declaration(mm.Groups[1].Value, sources));
            else if (mm.Success)
                throw Fail($"{name}: calls {mm.Groups[1].Value}, which can't run on the GPU");
            if (Regex.IsMatch(s, @"\b(ptrtoint|inttoptr)\b"))
                throw Fail($"{name}: converts between pointers and integers: {s}");
            output.Add("  " + s);
        }

        string text = string.Join("\n", output);
        if (Regex.IsMatch(text, Regex.Escape(shadowStack) + @"\b"))
            throw Fail($"{name}: still uses the shadow stack after the rewrite");
        // Dropping parameter %0 breaks the numbering of unnamed values: name them all.
        text = Regex.Replace(text, @"%(\d+)\b", "%v$1");
        text = Regex.Replace(text, @"^(\d+):", "v$1:", RegexOptions.Multiline);
        string signature = string.Join(", ", args.Select(a => Regex.Replace(a, @"%(\d+)$", "%v$1"))
            .Append("i32 %gpu.width").Append("i32 %gpu.height"));

        // The entry check: threads outside the width x height grid return at once.
        string firstLine = text.Split('\n').FirstOrDefault(l => l.Trim() != "") ?? "";
        string bodyLabel = firstLine.TrimEnd().EndsWith(':') ? firstLine.Trim().TrimEnd(':') : "gpu.body";
        declares.Add("declare i32 @llvm.nvvm.read.ptx.sreg.ctaid.x() #0");
        declares.Add("declare i32 @llvm.nvvm.read.ptx.sreg.ntid.x() #0");
        declares.Add("declare i32 @llvm.nvvm.read.ptx.sreg.tid.x() #0");
        declares.Add("declare i32 @llvm.nvvm.read.ptx.sreg.ctaid.y() #0");
        declares.Add("declare i32 @llvm.nvvm.read.ptx.sreg.ntid.y() #0");
        declares.Add("declare i32 @llvm.nvvm.read.ptx.sreg.tid.y() #0");
        string guard = string.Join("\n",
            "gpu.entry:",
            "  %gpu.cx = call i32 @llvm.nvvm.read.ptx.sreg.ctaid.x()",
            "  %gpu.nx = call i32 @llvm.nvvm.read.ptx.sreg.ntid.x()",
            "  %gpu.tx = call i32 @llvm.nvvm.read.ptx.sreg.tid.x()",
            "  %gpu.mx = mul i32 %gpu.cx, %gpu.nx",
            "  %gpu.x = add i32 %gpu.mx, %gpu.tx",
            "  %gpu.cy = call i32 @llvm.nvvm.read.ptx.sreg.ctaid.y()",
            "  %gpu.ny = call i32 @llvm.nvvm.read.ptx.sreg.ntid.y()",
            "  %gpu.ty = call i32 @llvm.nvvm.read.ptx.sreg.tid.y()",
            "  %gpu.my = mul i32 %gpu.cy, %gpu.ny",
            "  %gpu.y = add i32 %gpu.my, %gpu.ty",
            "  %gpu.outx = icmp uge i32 %gpu.x, %gpu.width",
            "  %gpu.outy = icmp uge i32 %gpu.y, %gpu.height",
            "  %gpu.out = or i1 %gpu.outx, %gpu.outy",
            $"  br i1 %gpu.out, label %gpu.exit, label %{bodyLabel}",
            "gpu.exit:",
            "  ret void");
        if (bodyLabel == "gpu.body")
            text = "gpu.body:\n" + text;
        return $"define void @{shortName}({signature}) #0 {{\n{guard}\n{text}\n}}\n";

        string Mad(string a, string b2, string c)
        {
            string product = $"%ptx.{++temp}";
            output.Add($"  {product} = mul i32 {a}, {b2}");
            string sum = $"%ptx.{++temp}";
            output.Add($"  {sum} = add i32 {product}, {c}");
            return sum;
        }

    }

    // gpu_atomic_<op>_<type>(p, v[, comparand]) -> atomicrmw / cmpxchg, monotonic.
    static List<string> Atomic(string name, string fn, string result, List<string> args, ref int temp)
    {
        Match am = Regex.Match(fn, @"^gpu_atomic_(\w+)_(i32|u32|f32)$");
        if (!am.Success)
            throw Fail($"{name}: {fn} is not a GPU built-in");
        string op = am.Groups[1].Value, kind = am.Groups[2].Value;
        string t = kind == "f32" ? "float" : "i32";
        string p = args[0], v = args[1];
        string rmw = (op, kind) switch
        {
            ("add", "f32") => "fadd",
            ("add", _) => "add",
            ("sub", "i32" or "u32") => "sub",
            ("min", "i32") => "min",
            ("min", "u32") => "umin",
            ("max", "i32") => "max",
            ("max", "u32") => "umax",
            ("and" or "or" or "xor", "i32" or "u32") => op,
            ("xchg", _) => "xchg",
            ("cas", "i32" or "u32") => "cas",
            _ => throw Fail($"{name}: no atomic {op} on {kind}"),
        };
        string target = result == "" ? $"%ptx.{++temp}" : result;
        if (rmw == "cas")
        {
            string pair = $"%ptx.{++temp}";
            return
            [
                $"  {pair} = cmpxchg ptr {p}, i32 {args[2]}, i32 {v} monotonic monotonic",
                $"  {target} = extractvalue {{ i32, i1 }} {pair}, 0",
            ];
        }
        return [$"  {target} = atomicrmw {rmw} ptr {p}, {t} {v} monotonic"];
    }

    // An @llvm.* intrinsic's declaration, from the input.
    static string Declaration(string intrinsic, string[] sources)
    {
        foreach (string text in sources)
        {
            Match mm = Regex.Match(text, @"^declare [^\n]*" + Regex.Escape(intrinsic) + @"\(.*$", RegexOptions.Multiline);
            if (mm.Success)
                return Air.StripAttachments(mm.Value);
        }
        throw Fail($"{intrinsic}: no declaration in the IR");
    }
}
