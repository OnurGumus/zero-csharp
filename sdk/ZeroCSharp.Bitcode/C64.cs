// A C64 program from IL, in two steps:
//
//   zero-bitcode c64-bitcode IN.dll OUTDIR CORE.dll [REF.dll...]
//       NativeAOT-LLVM: IN.dll -> OUTDIR/<name>.bc (the data, a WebAssembly
//       object file), OUTDIR/<name>.<n>.bc (the code) and maybe
//       OUTDIR/<name>.external.o. CORE.dll is zerocore built for bitcode
//       targets (-d WASM). On a Mac it runs in a Linux container.
//
//   zero-bitcode c64-link OBJDIR NAME RUNTIME.c OUT.prg [--llvm-mos DIR]
//       llvm-mos: the bitcode in OBJDIR -> OUT.prg, through ll2mos and
//       wasm2mos, linked with RUNTIME.c (c64/runtime.c, zerocore's native
//       half) and llvm-mos's C library.
//
// Tools come from the cache (Tools.cs) unless given.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroBitcode;

static class C64
{
    static ToolFailure Fail(string message) => new("zero-bitcode", message);

    // ---- NativeAOT-LLVM -----------------------------------------------------------

    // optimize: --Os for the C64 (50 KB for everything), -O for the
    // microcontrollers (Arm.cs), whose flash has room for speed, and for the
    // GPU; entryPoints: also compile IN.dll's [RuntimeExport] methods, called
    // or not (the GPU's kernels).
    public static void Bitcode(string[] args, string command = "c64-bitcode", string optimize = "--Os", bool entryPoints = false)
    {
        if (args.Length < 3)
            throw Fail($"usage: zero-bitcode {command} IN.dll OUTDIR CORE.dll [REF.dll...]");
        string input = Path.GetFullPath(args[0]);
        string outDir = Path.GetFullPath(args[1]);
        string core = Path.GetFullPath(args[2]);
        string[] references = args[3..].Select(Path.GetFullPath).ToArray();
        string name = Path.GetFileNameWithoutExtension(input);
        string ilcFolder = Tools.Ilc();

        // Emptied rather than deleted: Docker Desktop can briefly hold a folder
        // it mounted for the last build, and then refuses to let it go.
        Directory.CreateDirectory(outDir);
        foreach (string file in Directory.GetFiles(outDir))
            File.Delete(file);
        foreach (string folder in Directory.GetDirectories(outDir))
            Directory.Delete(folder, recursive: true);

        // As ./build.sh compiles for the C64 and the microcontrollers
        // (compile_bitcode); [DllImport("libc")] and ("libm.so.6") bound at
        // link time.
        var ilcArgs = new List<string> { input };
        foreach (string reference in references)
            ilcArgs.AddRange(["-r", reference]);
        ilcArgs.AddRange(["-r", core, "--reflectiondata:none",
            "--generateunmanagedentrypoints:System.Private.CoreLib",
            "--targetos:wasi", "--targetarch:wasm", optimize, "--wasm-global-base:1024",
            "--codegenopt:Target=wasm32-unknown-wasip2", "--codegenopt:LlvmExceptionHandlingModel=emulated",
            "--directpinvoke:libc", "--directpinvoke:libm.so.6"]);
        if (entryPoints)
            ilcArgs.Add($"--generateunmanagedentrypoints:{name}");
        ilcArgs.Add($"-o:{Path.Combine(outDir, name + ".bc")}");

        if (OperatingSystem.IsMacOS())
        {
            // Microsoft publishes no NativeAOT-LLVM for Apple Silicon: Linux
            // arm64's runs in a container, with every folder it reads or
            // writes mounted at the same path, so paths need no translating.
            CheckDocker();
            var docker = new List<string> { "run", "--rm" };
            var folders = new[] { ilcFolder, outDir, Path.GetDirectoryName(input)!, Path.GetDirectoryName(core)! }
                .Concat(references.Select(r => Path.GetDirectoryName(r)!))
                .Distinct(StringComparer.Ordinal);
            foreach (string folder in folders)
                docker.AddRange(["-v", $"{folder}:{folder}"]);
            docker.Add("ubuntu:24.04");
            docker.Add(Path.Combine(ilcFolder, "tools", "ilc"));
            docker.AddRange(ilcArgs);
            RunLogged("docker", docker, Path.Combine(outDir, "ilc.log"), "NativeAOT-LLVM (in Docker)");
        }
        else
        {
            string ilc = Path.Combine(ilcFolder, "tools", OperatingSystem.IsWindows() ? "ilc.exe" : "ilc");
            RunLogged(ilc, ilcArgs, Path.Combine(outDir, "ilc.log"), "NativeAOT-LLVM");
        }
    }

    public static void CheckDocker()
    {
        bool running;
        try
        {
            var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("info");
            using Process process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            running = process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw Fail("building for the C64, a microcontroller or the GPU on a Mac needs Docker, to run NativeAOT-LLVM's Linux build. "
                       + "Install Docker Desktop, or a free alternative such as Colima (brew install colima docker) or Podman, and start it.");
        }
        if (!running)
            throw Fail("Docker is installed but not running: start Docker Desktop (or colima start, or podman machine start) and build again.");
    }

    public static void RunLogged(string program, IEnumerable<string> arguments, string log, string what)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw Fail($"couldn't start {program}");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        File.WriteAllText(log, output.Result + errors);
        if (process.ExitCode != 0)
            throw Fail($"{what} failed:\n{output.Result}{errors}");
    }

    // ---- llvm-mos -----------------------------------------------------------------

    public static void Link(string[] args)
    {
        string? llvmMos = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--llvm-mos" && i + 1 < args.Length)
                llvmMos = args[++i];
            else
                positional.Add(args[i]);
        }
        if (positional.Count != 4)
            throw Fail("usage: zero-bitcode c64-link OBJDIR NAME RUNTIME.c OUT.prg [--llvm-mos DIR]");
        string obj = positional[0], name = positional[1], runtime = positional[2], output = positional[3];
        llvmMos ??= Tools.LlvmMosFolder();
        string clang = Path.Combine(llvmMos, "bin", OperatingSystem.IsWindows() ? "clang.exe" : "clang");

        string outDir = Path.Combine(obj, "mos");
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(Path.Combine(outDir, "ir"));

        // Bitcode -> text (any target will do for reading it) -> rewritten text.
        var bitcode = Directory.GetFiles(obj, name + ".*.bc")
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"\.\d+\.bc$"))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (bitcode.Count == 0)
            throw Fail($"no bitcode for {name} in {obj}");
        var ir = new List<string>();
        foreach (string bc in bitcode)
        {
            string ll = Path.Combine(outDir, "ir", Path.GetFileNameWithoutExtension(bc) + ".ll");
            Tools.Run(clang, ["--target=wasm32", "-Wno-override-module", "-S", "-emit-llvm", bc, "-o", ll], $"reading {Path.GetFileName(bc)}");
            ir.Add(ll);
        }
        string[] irFiles = ir.ToArray();
        CheckFatPointers(name, irFiles);
        Ll2Mos.Run(outDir, irFiles);
        Wasm.Convert(Path.Combine(obj, name + ".bc"), Path.Combine(outDir, "data"), new Mos(irFiles));
        string external = Path.Combine(obj, name + ".external.o");
        if (File.Exists(external))
            Wasm.Convert(external, Path.Combine(outDir, "external"), new Mos(irFiles));

        // One compilation of everything: llvm-mos optimizes the whole program
        // at link time, which is where it decides what can live at fixed
        // addresses instead of on a stack, and makes it a fifth smaller.
        // -Os: there are 50 KB for it all.
        //
        // The 6502 has three registers, and now and then a method made larger
        // by that optimization (others get folded into it) is more than
        // llvm-mos can fit into them: "ran out of registers". The module with
        // that method is then compiled on its own, where the method stays as
        // it was written, and the link is tried again. Other failures of the
        // 6502 backend ("Scavenger spill for register not yet implemented")
        // don't name the method: the link is then run again with the
        // compiler saying which method each step works on, and the last one
        // named is where it stopped.
        var whole = Directory.GetFiles(outDir, "*.ll").Order(StringComparer.Ordinal).ToList();
        var separate = new List<string>();
        var extra = Directory.GetFiles(outDir, "*.s").Order(StringComparer.Ordinal)
            .Concat(new[] { "data.c", "external.c" }.Select(f => Path.Combine(outDir, f)).Where(File.Exists))
            .ToList();
        string[] mos = ["--config", "mos-c64.cfg", "-Os", "-Wall", "-Wno-override-module", "-Wno-unused-command-line-argument"];
        // save-basic.o: by default an llvm-mos program never returns (it has
        // overwritten memory BASIC uses). This saves that memory at the start
        // and puts it back at the end, so the program ends at BASIC's READY.
        string saveBasic = Path.Combine(llvmMos, "mos-platform", "commodore", "lib", "save-basic.o");

        while (true)
        {
            var (code, log) = Capture(clang, [.. mos, .. whole, .. separate, .. extra, runtime, saveBasic, "-o", output]);
            File.WriteAllText(Path.Combine(outDir, "link.log"), log);
            if (code == 0)
                return;

            Match registers = Regex.Match(log, @"ran out of registers during register allocation in function '(.*?)'");
            string? method = registers.Success ? registers.Groups[1].Value : null;
            if (method == null && log.Contains("LLVM ERROR", StringComparison.Ordinal))
            {
                var (_, trace) = Capture(clang, [.. mos, .. whole, .. separate, .. extra, runtime, saveBasic, "-o", output,
                    "-Wl,-mllvm,-debug-pass=Executions"]);
                MatchCollection steps = Regex.Matches(trace, @"Executing Pass '.*?' on Function '(.*?)'");
                if (steps.Count > 0)
                    method = steps[^1].Groups[1].Value;
            }
            string? module = null;
            if (method != null)
            {
                foreach (string candidate in whole)
                {
                    if (File.ReadLines(candidate).Any(line => line.StartsWith("define ", StringComparison.Ordinal)
                            && (line.Contains($"@{method}(", StringComparison.Ordinal) || line.Contains($"@\"{method}\"(", StringComparison.Ordinal))))
                        module = candidate;
                }
            }
            if (module != null)
            {
                Console.Error.WriteLine($"[c64]   {name}: {method} is too much for the 6502's registers once optimized with the rest; compiling its module on its own");
                Tools.Run(clang, [.. mos, "-fno-lto", "-c", module, "-o", module + ".o"], $"compiling {Path.GetFileName(module)}");
                separate.Add(module + ".o");
                whole.Remove(module);
                continue;
            }

            Match tooBig = Regex.Match(log, @"will not fit in region.*?overflowed by (.*)");
            if (tooBig.Success)
                throw Fail($"{name} doesn't fit in a C64's memory:\n  too big by {tooBig.Groups[1].Value.Trim()}");
            var missing = Regex.Matches(log, @"undefined symbol: cs_([A-Za-z0-9_]*)").Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                throw Fail($"{name} calls functions the C64's runtime (c64/runtime.c) doesn't have:\n  {string.Join(" ", missing)}");
            if (method != null)
                throw Fail($"{method} is too complex for llvm-mos (the 6502 has three registers). Split it into smaller methods.\n{log}");
            throw Fail($"linking {name} failed:\n{log}");
        }
    }

    // A pointer to a generic method over a class (&Method<SomeClass>) is
    // "fat": it points at the shared code and which class it's for, and is
    // told apart from a plain pointer by the top bit of 32, which a C64's
    // 16-bit pointer can't keep. A call through one jumps into data, so a
    // program that makes one doesn't build.
    static void CheckFatPointers(string name, string[] irFiles)
    {
        var use = new Regex(@"@""?__fatpointer_([^""\s,)]+)");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in irFiles)
            foreach (string line in File.ReadLines(file))
                if (!line.StartsWith(';') && !line.StartsWith("@\"__fatpointer_") && !line.StartsWith("@__fatpointer_"))
                    foreach (Match m in use.Matches(line))
                        found.Add(m.Groups[1].Value);
        if (found.Count > 0)
            throw Fail($"{name} takes pointers to generic methods over classes (&Method<SomeClass>), which a C64's 16-bit pointers can't hold: call through a generic class's virtual method instead:\n  {string.Join("\n  ", found)}");
    }

    public static (int Code, string Log) Capture(string program, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw Fail($"couldn't start {program}");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + errors);
    }
}
