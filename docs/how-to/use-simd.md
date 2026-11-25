---
title: Use SIMD
index: 8
---

# Use SIMD

zerocore declares `Vector64<T>`, `Vector128<T>` and `Vector256<T>` and a subset of .NET's hardware
intrinsics, under the names .NET gives them: `AdvSimd` and `Dp` (SDOT) on arm64, and `Avx`, `Avx2` and
`Fma` on x64. The compiler replaces each call with its instruction. What differs from .NET is when
`IsSupported` is decided: zero mode has no run-time CPU detection, so it is decided at build time,
from the `-m` flags.

## Know what `IsSupported` means here

When bflat compiles for a target, it replaces `IsSupported` with `true` or `false` according to the
instruction sets that target's baseline and the `-m` flags include:

| Target | Without `-m` | With `-m` |
|---|---|---|
| arm64 | `AdvSimd` (NEON) is true; `Dp` is false | `-m dotprod`: `Dp` is true (ARMv8.2+, such as Apple M1 or Cortex-A76) |
| x64 | `Sse`, `Sse2` are true; `Avx`, `Avx2`, `Fma` are false | `-m x86-64-v3`: all five are true (Haswell, 2013, or newer) |
| WebAssembly, DOS | all false | not applicable |

Each check is a constant in the compiled program, not a test at run time. The other side of that: a
binary built with `-m x86-64-v3` assumes AVX2 and FMA. Zero mode doesn't detect
the CPU, so run it only on machines that have them.

## Pass the `-m` flag

With the .NET SDK, the project's `ZeroInstructionSet` property is passed as `-m`. The instruction set
depends on the target's architecture, so set it per target:

```xml
<PropertyGroup>
  <ZeroInstructionSet Condition="'$(ZeroTarget)' == 'linux-x64' or '$(ZeroTarget)' == 'win-x64'">x86-64-v3</ZeroInstructionSet>
  <ZeroInstructionSet Condition="'$(ZeroTarget)' == 'linux-arm64' or '$(ZeroTarget)' == 'osx-arm64'">dotprod</ZeroInstructionSet>
</PropertyGroup>
```

In the repository, `build.sh` passes the flag only to Simd and Kernels, in its `bflat_flags` function:
`-m x86-64-v3` on x64 and `-m dotprod` on arm64. To give your own sample SIMD, add its name to that
line:

```bash
Simd | Kernels)
    if [ "$BFLAT_ARCH" = x64 ]; then echo -m x86-64-v3; else echo -m dotprod; fi ;;
```

`TensorPrimitives` (`System.Numerics.Tensors`) follows the same flags: its arithmetic, sums, `Exp`,
`Sigmoid`, `Tanh` and `SoftMax` use NEON on arm64, and on x64 SSE2, or AVX with `-m x86-64-v3`
(see [What works](../concepts/limits.html#tensorprimitives)). `build.sh` builds its sample twice on
x64, as `Tensors` (SSE2) and `TensorsAvx`, so both paths are tested.

`lib/` itself is built once as IL. Each program's build compiles the parts it uses for its own `-m`
flags, so one `BflatLib.dll` gives NEON code on arm64 and AVX2 code on x64.

## Write the paths

Test the instruction set you need, from the most specific to none, and keep a scalar path at the end.
`samples/Simd.cs` reports which path it compiled:

<!-- sample: samples/Simd.cs path -->
```csharp
Console.WriteLine(AdvSimd.Arm64.IsSupported
    ? "NEON (AdvSimd), 4 floats per instruction"
    : Fma.IsSupported ? "AVX + FMA, 8 floats per instruction"
    : "scalar (no SIMD ISA enabled)");
```

The vector loops themselves load through pointers (`AdvSimd.LoadVector128(a + i)`,
`Avx.LoadVector256(a + i)`), so data usually lives in an arena. Allocate it aligned:

<!-- sample: samples/Simd.cs aligned -->
```csharp
// 64-byte alignment: a full cache line, and aligned for any vector width.
float* a = arena.AllocArray<float>(N, align: 64);
float* b = arena.AllocArray<float>(N, align: 64);
if (a == null || b == null)
    return 1;
```

Alignment is per allocation in an arena, any power of two. 64 bytes is a full cache line, and aligned
for any vector width.

`Simd.cs` does its loads inside `unsafe` blocks, marks the functions that take raw pointers `unsafe`,
and states in a `<safety>` comment how many floats each pointer must cover.

## Use the ready-made kernels

`lib/Kernels.cs` has SIMD math for LLM inference, each with NEON, AVX2+FMA and scalar paths. Every
kernel handles any length: vector loops cover the bulk and a scalar loop the tail.

| Kernel | Does |
|---|---|
| `Dot(a, b, n)` | sum of `a[i] * b[i]` |
| `MatVec(y, W, x, rows, cols)` | `y[r] = dot(W[r, :], x)` for a row-major matrix |
| `DotI8(a, b, n)` | exact int8 dot product; values must be in [-127, 127] |
| `Exp(x, y, n)` | `y[i] = exp(x[i])`; `x` and `y` may be the same buffer |
| `Softmax(x, n)` | in place: `exp(x - max) / sum` |
| `Silu(x, n)` | in place: `x / (1 + exp(-x))` |
| `RmsNorm(y, x, weight, n, eps)` | `x / sqrt(mean(x^2) + eps) * weight` |
| `Kernels.Path` | `"NEON"`, `"AVX2+FMA"` or `"scalar"` |

They take raw pointers and are `unsafe`: the caller guarantees each pointer covers `n` elements. The
Kernels sample uses an odd length on purpose, to exercise the scalar tails:

<!-- sample: samples/Kernels.cs buffers -->
```csharp
const int N = 1003; // odd on purpose: exercises the scalar tails
float* x = arena.AllocArray<float>(N, align: 64);
float* y = arena.AllocArray<float>(N, align: 64);
float* w = arena.AllocArray<float>(N, align: 64);
float* refv = arena.AllocArray<float>(N, align: 64);
```

<!-- sample: samples/Kernels.cs softmax -->
```csharp
// ---- Softmax ----
for (int i = 0; i < N; i++) { x[i] = Random(-20f, 20f); refv[i] = x[i]; }
Kernels.Softmax(x, N);
```

`DotI8` uses SDOT on arm64, which needs `-m dotprod`, and AVX2 on x64. `Exp` is Cephes' `expf`, and
clamps its input to [-87.33, 88.72], so results below 2^-126 come out as about 1.2e-38 instead of a
subnormal or 0. The Kernels sample checks it against libm over 2 million values and requires a
relative error below 4e-7.

## Build and run

Put [`samples/Simd.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/Simd.cs) in a zero project's `Program.cs`, with the property above,
then `dotnet build -c Release` and run `bin/Release/zero03/<project>`: a Debug build isn't optimized,
which leaves its timings meaningless. In the repository:

```bash
./build.sh Simd Kernels
./run.sh Simd
./run.sh Kernels
```

Simd checks its SIMD dot product against the scalar one and times both; the README reports it 12x
faster on Apple Silicon. Kernels checks every kernel against a scalar or double-precision reference,
exits with the number of failed checks, and times softmax against scalar libm (7x faster on Apple
Silicon, per the README). `test.sh` checks only their exit codes, since timings vary.

## Watch out for

- **Only the declared operations exist.** zerocore declares the operations `lib/Kernels.cs` uses. To
  add one, declare it in `zerocore/Intrinsics/` with the name and signature it has in .NET; the
  compiler does the rest.
- **No SIMD on WebAssembly or DOS.** WebAssembly has 128-bit SIMD, but `Kernels.cs` has no path for it
  yet, so the scalar paths run. DOS doesn't build Simd or Kernels: a 386 has no SIMD instructions.
- **UEFI works like the native targets.** The table above applies to `uefi-x64` and `uefi-arm64`.
  The firmware leaves AVX off, so `efi/start.c` turns it on before `Main`; without that, a
  `-m x86-64-v3` program would stop at its first AVX instruction. In QEMU, `run.sh --uefi` passes
  `-cpu max` or `-cpu host` so the emulated processor has AVX2 and FMA.
- **Time it with `Clock.Monotonic`.** It counts nanoseconds on a clock that only moves forward, on
  every target (see [Use the clock and random numbers](use-the-clock-and-random-numbers.html)).

See also: the [`Kernels` reference](../reference/kernels.html),
[What zero mode is](../concepts/zero-mode.html), and [Limits](../concepts/limits.html).
