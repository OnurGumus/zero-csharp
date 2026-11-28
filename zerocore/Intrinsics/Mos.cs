// The 6502 of the Commodore 64: zero-csharp's own name, in the style of the
// processors .NET knows (X86, Arm, Wasm).
//
// Only IsSupported, true in zerocore's build for the C64 (./build.sh --c64
// builds it with -d C64). Code in assembly for the 6502 is in c64/runtime.c,
// and a lib/ method that has a 6502 version calls it when IsSupported says
// so. It's a constant, so the compiler drops the branches that don't apply,
// and with them the calls to functions only the C64's runtime has.
namespace System.Runtime.Intrinsics.Mos
{
    public abstract class Mos6502
    {
#if C64
        public static bool IsSupported => true;
#else
        public static bool IsSupported => false;
#endif
    }
}
