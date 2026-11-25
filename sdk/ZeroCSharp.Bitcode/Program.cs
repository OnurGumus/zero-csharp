// zero-bitcode: the tools that turn NativeAOT-LLVM's output into sources for
// a processor it doesn't know.
//
//   zero-bitcode ll2mos OUTDIR IN.ll...         LLVM IR for the 6502 (Ll2Mos.cs)
//   zero-bitcode wasm2mos IN.o OUT MODULE.ll... a WebAssembly object file's data
//                                               and functions, for the 6502
//   zero-bitcode wasm2i386 IN.o OUT             the same, for the 386 (DOS)
//   zero-bitcode wasm2arm IN.o OUT              the same, for Cortex-M
//   zero-bitcode elf2uf2 IN.elf OUT.uf2         a Pico 2 program as a UF2 file (Uf2.cs)
//   zero-bitcode ll2air OUT.ll ASSEMBLY IN.ll... an assembly's [RuntimeExport] methods
//                                               as Metal kernels (Air.cs)
//   zero-bitcode ll2ptx OUT.ll ASSEMBLY IN.ll... the same as CUDA kernels, LLVM IR
//                                               for the NVPTX backend (Ptx.cs)
//   zero-bitcode gpu-bitcode, gpu-kernels       a program's GPU kernels (Gpu.cs)
//
// They began as Python scripts (c64/ll2mos.py, c64/wasm2mos.py,
// dos/wasm2i386.py, removed after the port was checked to write the same
// output, byte for byte, for every C64 and DOS program).
using System.Text;

namespace ZeroBitcode;

/// <summary>A tool's failure: printed as "tool: message", exit code 1.</summary>
sealed class ToolFailure(string tool, string message) : Exception(message)
{
    public string Tool { get; } = tool;
}

static class Files
{
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A text file, with any \r\n or \r read as \n.</summary>
    public static string ReadText(string path) =>
        File.ReadAllText(path, Utf8).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Writes text as it is: UTF-8 without a byte order mark, \n line ends.</summary>
    public static void WriteText(string path, string text) => File.WriteAllText(path, text, Utf8);
}

static class Program
{
    const string Usage = """
        Usage: zero-bitcode ll2mos OUTDIR IN.ll...
               zero-bitcode wasm2mos IN.o OUT MODULE.ll...
               zero-bitcode wasm2i386 IN.o OUT
               zero-bitcode wasm2arm IN.o OUT
               zero-bitcode elf2uf2 IN.elf OUT.uf2
               zero-bitcode c64-bitcode IN.dll OUTDIR CORE.dll [REF.dll...]
               zero-bitcode c64-link OBJDIR NAME RUNTIME.c OUT.prg [--llvm-mos DIR]
               zero-bitcode arm-bitcode IN.dll OUTDIR CORE.dll [REF.dll...]
               zero-bitcode arm-link OBJDIR NAME RUNTIMEDIR pico2|r4 QEMU.elf [BOARD.elf BOARD.uf2] [--arm DIR]
               zero-bitcode tools ilc|llvm-mos|arm
               zero-bitcode ll2air OUT.ll ASSEMBLY IN.ll...
               zero-bitcode ll2ptx OUT.ll ASSEMBLY IN.ll...
               zero-bitcode gpu-bitcode IN.dll OUTDIR CORE.dll [REF.dll...]
               zero-bitcode gpu-kernels BITCODEDIR NAME METAL.m OUTDIR
        """;

    static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "ll2mos" when args.Length >= 3:
                    Ll2Mos.Run(args[1], args[2..]);
                    return 0;
                case "wasm2mos" when args.Length >= 4:
                    Wasm2Mos.Run(args[1], args[2], args[3..]);
                    return 0;
                case "wasm2i386" when args.Length == 3:
                    Wasm.Convert(args[1], args[2], new Target());
                    return 0;
                case "wasm2arm" when args.Length == 3:
                    Wasm.Convert(args[1], args[2], new Arm());
                    return 0;
                case "elf2uf2" when args.Length == 3:
                    Uf2.Run(args[1], args[2]);
                    return 0;
                case "tools":
                    Tools.Command(args[1..]);
                    return 0;
                case "c64-bitcode":
                    C64.Bitcode(args[1..]);
                    return 0;
                case "c64-link":
                    C64.Link(args[1..]);
                    return 0;
                case "arm-bitcode":
                    ArmTarget.Bitcode(args[1..]);
                    return 0;
                case "arm-link":
                    ArmTarget.Link(args[1..]);
                    return 0;
                case "ll2air" when args.Length >= 4:
                    Air.Run(args[1], args[2], args[3..]);
                    return 0;
                case "ll2ptx" when args.Length >= 4:
                    Ptx.Run(args[1], args[2], args[3..]);
                    return 0;
                case "gpu-bitcode":
                    Gpu.Bitcode(args[1..]);
                    return 0;
                case "gpu-kernels":
                    Gpu.Kernels(args[1..]);
                    return 0;
                default:
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }
        catch (ToolFailure failure)
        {
            Console.Error.WriteLine($"{failure.Tool}: {failure.Message}");
            return 1;
        }
    }
}
