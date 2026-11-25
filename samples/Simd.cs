// Example: SIMD in zero mode, using System.Runtime.Intrinsics from the core
// library (zerocore/Intrinsics). A float dot product, the core of every matrix multiply:
//   arm64: NEON (AdvSimd), 4 lanes per instruction
//   x64:   AVX + FMA, 8 lanes per instruction (needs -m x86-64-v3, which build.sh passes)
// Build: ./build.sh Simd

using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SimdSample;

public static class Program
{
    const int N = 4096;      // multiple of 32 (the x64 loop's step)
    const int Reps = 200_000;

    [DllImport("libc")]
    static unsafe extern int clock_gettime(int clock, long* timespec);

    static long NowMs()
    {
        long* ts = stackalloc long[2];
        unsafe
        {
            clock_gettime(1 /* CLOCK_MONOTONIC */, ts); // SAFETY: ts has room for a timespec.
            return ts[0] * 1000 + ts[1] / 1_000_000;
        }
    }

    /// <safety>a and b must each point to n readable floats.</safety>
    static unsafe float DotScalar(float* a, float* b, int n)
    {
        float sum = 0;
        unsafe
        {
            for (int i = 0; i < n; i++)
                sum += a[i] * b[i];
        }
        return sum;
    }

    /// <safety>a and b must each point to n readable floats; n must be a multiple of 32.</safety>
    static unsafe float DotSimd(float* a, float* b, int n)
    {
        unsafe
        {
            if (AdvSimd.Arm64.IsSupported)
            {
                // Four accumulators hide the latency of the fused multiply-add.
                Vector128<float> s0 = default, s1 = default, s2 = default, s3 = default;
                for (int i = 0; i < n; i += 16)
                {
                    s0 = AdvSimd.FusedMultiplyAdd(s0, AdvSimd.LoadVector128(a + i), AdvSimd.LoadVector128(b + i));
                    s1 = AdvSimd.FusedMultiplyAdd(s1, AdvSimd.LoadVector128(a + i + 4), AdvSimd.LoadVector128(b + i + 4));
                    s2 = AdvSimd.FusedMultiplyAdd(s2, AdvSimd.LoadVector128(a + i + 8), AdvSimd.LoadVector128(b + i + 8));
                    s3 = AdvSimd.FusedMultiplyAdd(s3, AdvSimd.LoadVector128(a + i + 12), AdvSimd.LoadVector128(b + i + 12));
                }
                Vector128<float> s = AdvSimd.Add(AdvSimd.Add(s0, s1), AdvSimd.Add(s2, s3));
                s = AdvSimd.Arm64.AddPairwise(s, s);
                s = AdvSimd.Arm64.AddPairwise(s, s); // every lane now holds the total
                float* lanes = stackalloc float[4];
                AdvSimd.Store(lanes, s);
                return lanes[0];
            }

            if (Fma.IsSupported)
            {
                Vector256<float> s0 = default, s1 = default, s2 = default, s3 = default;
                for (int i = 0; i < n; i += 32)
                {
                    s0 = Fma.MultiplyAdd(Avx.LoadVector256(a + i), Avx.LoadVector256(b + i), s0);
                    s1 = Fma.MultiplyAdd(Avx.LoadVector256(a + i + 8), Avx.LoadVector256(b + i + 8), s1);
                    s2 = Fma.MultiplyAdd(Avx.LoadVector256(a + i + 16), Avx.LoadVector256(b + i + 16), s2);
                    s3 = Fma.MultiplyAdd(Avx.LoadVector256(a + i + 24), Avx.LoadVector256(b + i + 24), s3);
                }
                Vector256<float> s = Avx.Add(Avx.Add(s0, s1), Avx.Add(s2, s3));
                float* lanes = stackalloc float[8];
                Avx.Store(lanes, s);
                return lanes[0] + lanes[1] + lanes[2] + lanes[3] + lanes[4] + lanes[5] + lanes[6] + lanes[7];
            }

            return DotScalar(a, b, n);
        }
    }

    public static int Main()
    {
        Console.WriteLine("=== SIMD dot product in --stdlib:zero ===");
        Console.Write("Path: ");
        // docs:path
        Console.WriteLine(AdvSimd.Arm64.IsSupported
            ? "NEON (AdvSimd), 4 floats per instruction"
            : Fma.IsSupported ? "AVX + FMA, 8 floats per instruction"
            : "scalar (no SIMD ISA enabled)");
        // docs:end

        Arena arena = Arena.Create(64 * 1024);
        if (!arena.IsCreated)
            return 1;

        // docs:aligned
        // 64-byte alignment: a full cache line, and aligned for any vector width.
        float* a = arena.AllocArray<float>(N, align: 64);
        float* b = arena.AllocArray<float>(N, align: 64);
        if (a == null || b == null)
            return 1;
        // docs:end

        float scalar, simd;
        long scalarMs, simdMs;
        unsafe
        {
            // Multiples of 1/8, so float sums are exact and both orders agree.
            for (int i = 0; i < N; i++)
            {
                a[i] = (i % 7) * 0.5f;
                b[i] = (i % 5) * 0.25f;
            }

            // SAFETY: a and b hold N floats, and N is a multiple of 32.
            scalar = DotScalar(a, b, N);
            simd = DotSimd(a, b, N);

            float sink = 0;
            long t0 = NowMs();
            for (int r = 0; r < Reps; r++)
                sink += DotScalar(a, b, N);
            long t1 = NowMs();
            for (int r = 0; r < Reps; r++)
                sink += DotSimd(a, b, N);
            long t2 = NowMs();
            scalarMs = t1 - t0;
            simdMs = t2 - t1;
            if (sink == 0)
                return 1; // keeps the loops from being optimized away
        }

        Console.WriteLine($"Scalar result: {(long)scalar}");
        Console.WriteLine($"SIMD result:   {(long)simd}");
        Console.Write("Results match: ");
        Console.Write(scalar == simd);
        Console.WriteLine();
        Console.WriteLine($"Scalar: {scalarMs} ms");
        Console.WriteLine($"SIMD:   {simdMs} ms");
        if (simdMs > 0)
            Console.WriteLine($"Speedup: {scalarMs / simdMs}x");

        unsafe { arena.Destroy(); } // SAFETY: a and b are not used after this.
        return scalar == simd ? 0 : 1;
    }
}
