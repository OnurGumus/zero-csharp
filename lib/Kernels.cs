// SIMD math kernels for LLM inference, in --stdlib:zero.
//
// Each kernel has four paths, picked at compile time:
//   arm64: NEON (AdvSimd), 4 floats per vector; DotI8 uses SDOT with -m dotprod
//   x64:   AVX2 + FMA, 8 floats per vector (needs -m x86-64-v3)
//   wasm:  simd128, 4 floats per vector, in C (wasm/simd.c): NativeAOT-LLVM
//          can't compile vector operations for WebAssembly
//   c64:   DotI8 only, in 6502 assembly (c64/runtime.c), with a table of
//          quarter squares instead of a multiply instruction
//   other: scalar
// Any length works: vector loops cover the bulk, a scalar loop the tail.
//
// Exp is Cephes' expf: reduce by ln 2, degree-6 polynomial, scale by 2^n through
// the exponent bits. Max relative error is a few ulp (samples/Kernels.cs measures
// it). Inputs are clamped to [-87.33, 88.72], so results below 2^-126 come out as
// ~1.2e-38 instead of a subnormal or 0, which doesn't matter for softmax or SiLU.
//
// All kernels take raw pointers and are marked unsafe: callers guarantee each
// pointer covers n elements (rows * cols for the matrix).

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.Mos;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

public static class Kernels
{
    [DllImport("libm.so.6")] static safe extern float expf(float x);
    [DllImport("libm.so.6")] static safe extern float sqrtf(float x);

    // wasm/simd.c, linked only into WebAssembly modules; called only when
    // PackedSimd.IsSupported, so no other target needs them.
    [DllImport("*"), SuppressGCTransition] static unsafe extern float ZeroSimdKernelDot(float* a, float* b, int n);
    [DllImport("*"), SuppressGCTransition] static unsafe extern void ZeroSimdKernelMatVec(float* y, float* w, float* x, int rows, int cols);
    [DllImport("*"), SuppressGCTransition] static unsafe extern int ZeroSimdKernelDotI8(sbyte* a, sbyte* b, int n);

    // c64/runtime.c, called only when Mos6502.IsSupported.
    [DllImport("libc"), SuppressGCTransition] static unsafe extern int c64_dot_i8(sbyte* a, sbyte* b, int n);
    [DllImport("*"), SuppressGCTransition] static unsafe extern void ZeroSimdKernelExp(float* x, float* y, int n);
    [DllImport("*"), SuppressGCTransition] static unsafe extern void ZeroSimdKernelSoftmax(float* x, int n);
    [DllImport("*"), SuppressGCTransition] static unsafe extern void ZeroSimdKernelSilu(float* x, int n);
    [DllImport("*"), SuppressGCTransition] static unsafe extern void ZeroSimdKernelScaleMultiply(float* y, float* x, float* weight, int n, float scale);

    const float ExpHi = 88.72283935546875f;
    const float ExpLo = -87.33654022216797f;
    const float Log2E = 1.44269504088896341f;
    const float NegLn2Hi = -0.693359375f;      // -ln 2, split in two for an exact reduction
    const float NegLn2Lo = 2.12194440e-4f;
    const float P0 = 1.9875691500e-4f, P1 = 1.3981999507e-3f, P2 = 8.3334519073e-3f;
    const float P3 = 4.1665795894e-2f, P4 = 1.6666665459e-1f, P5 = 5.0000001201e-1f;

    public static string Path =>
        AdvSimd.Arm64.IsSupported ? "NEON" : Fma.IsSupported && Avx2.IsSupported ? "AVX2+FMA"
        : PackedSimd.IsSupported ? "wasm simd128" : "scalar";

    // ------------------------------------------------------------------ dot

    /// <summary>Sum of a[i] * b[i].</summary>
    /// <safety>a and b must each cover n floats.</safety>
    public static unsafe float Dot(float* a, float* b, int n)
    {
        unsafe
        {
            if (PackedSimd.IsSupported)
                return ZeroSimdKernelDot(a, b, n);
            int i = 0;
            float sum = 0;
            if (AdvSimd.Arm64.IsSupported)
            {
                Vector128<float> s0 = default, s1 = default, s2 = default, s3 = default;
                for (; i + 16 <= n; i += 16)
                {
                    s0 = AdvSimd.FusedMultiplyAdd(s0, AdvSimd.LoadVector128(a + i), AdvSimd.LoadVector128(b + i));
                    s1 = AdvSimd.FusedMultiplyAdd(s1, AdvSimd.LoadVector128(a + i + 4), AdvSimd.LoadVector128(b + i + 4));
                    s2 = AdvSimd.FusedMultiplyAdd(s2, AdvSimd.LoadVector128(a + i + 8), AdvSimd.LoadVector128(b + i + 8));
                    s3 = AdvSimd.FusedMultiplyAdd(s3, AdvSimd.LoadVector128(a + i + 12), AdvSimd.LoadVector128(b + i + 12));
                }
                sum = HorizontalSum(AdvSimd.Add(AdvSimd.Add(s0, s1), AdvSimd.Add(s2, s3)));
            }
            else if (Fma.IsSupported && Avx2.IsSupported)
            {
                Vector256<float> s0 = default, s1 = default, s2 = default, s3 = default;
                for (; i + 32 <= n; i += 32)
                {
                    s0 = Fma.MultiplyAdd(Avx.LoadVector256(a + i), Avx.LoadVector256(b + i), s0);
                    s1 = Fma.MultiplyAdd(Avx.LoadVector256(a + i + 8), Avx.LoadVector256(b + i + 8), s1);
                    s2 = Fma.MultiplyAdd(Avx.LoadVector256(a + i + 16), Avx.LoadVector256(b + i + 16), s2);
                    s3 = Fma.MultiplyAdd(Avx.LoadVector256(a + i + 24), Avx.LoadVector256(b + i + 24), s3);
                }
                sum = HorizontalSum(Avx.Add(Avx.Add(s0, s1), Avx.Add(s2, s3)));
            }
            for (; i < n; i++)
                sum += a[i] * b[i];
            return sum;
        }
    }

    /// <summary>y[r] = dot(W[r, :], x) for a row-major rows x cols matrix W.</summary>
    /// <safety>y covers rows floats, W covers rows * cols floats, x covers cols floats.</safety>
    public static unsafe void MatVec(float* y, float* W, float* x, int rows, int cols)
    {
        unsafe
        {
            if (PackedSimd.IsSupported)
            {
                ZeroSimdKernelMatVec(y, W, x, rows, cols);
                return;
            }
            for (int r = 0; r < rows; r++)
                y[r] = Dot(W + (nint)r * cols, x, cols);
        }
    }

    /// <summary>Exact int8 dot product. Values must be in [-127, 127] (the x64 and C64 paths need it). On a C64 it takes about 100 processor cycles a product, ten times faster than a loop in C#, and a 1 KB table of RAM, made at the first call.</summary>
    /// <safety>a and b must each cover n bytes.</safety>
    public static unsafe int DotI8(sbyte* a, sbyte* b, int n)
    {
        unsafe
        {
            if (PackedSimd.IsSupported)
                return ZeroSimdKernelDotI8(a, b, n);
            if (Mos6502.IsSupported)
                return c64_dot_i8(a, b, n);
            int i = 0;
            int sum = 0;
            if (Dp.IsSupported)
            {
                Vector128<int> acc0 = default, acc1 = default;
                for (; i + 32 <= n; i += 32)
                {
                    acc0 = Dp.DotProduct(acc0, AdvSimd.LoadVector128(a + i), AdvSimd.LoadVector128(b + i));
                    acc1 = Dp.DotProduct(acc1, AdvSimd.LoadVector128(a + i + 16), AdvSimd.LoadVector128(b + i + 16));
                }
                Vector64<int> total = AdvSimd.Arm64.AddAcross(AdvSimd.Add(acc0, acc1));
                sum = *(int*)&total;
            }
            else if (Avx2.IsSupported)
            {
                // llama.cpp's trick: |a| (unsigned) times b with a's sign, pairs into int16, then int32.
                Vector256<short> ones = default;
                short* o = (short*)&ones;
                for (int k = 0; k < 16; k++)
                    o[k] = 1;
                Vector256<int> acc = default;
                for (; i + 32 <= n; i += 32)
                {
                    Vector256<sbyte> va = Avx.LoadVector256(a + i);
                    Vector256<sbyte> vb = Avx.LoadVector256(b + i);
                    Vector256<short> pairs = Avx2.MultiplyAddAdjacent(Avx2.Abs(va), Avx2.Sign(vb, va));
                    acc = Avx2.Add(acc, Avx2.MultiplyAddAdjacent(pairs, ones));
                }
                int* lanes = stackalloc int[8];
                Avx.Store(lanes, acc);
                for (int k = 0; k < 8; k++)
                    sum += lanes[k];
            }
            for (; i < n; i++)
                sum += a[i] * b[i];
            return sum;
        }
    }

    // ------------------------------------------------------------------ exp and friends

    /// <summary>y[i] = exp(x[i]). x and y may be the same buffer.</summary>
    /// <safety>x and y must each cover n floats.</safety>
    public static unsafe void Exp(float* x, float* y, int n)
    {
        unsafe
        {
            if (PackedSimd.IsSupported)
            {
                ZeroSimdKernelExp(x, y, n);
                return;
            }
            int i = 0;
            if (AdvSimd.Arm64.IsSupported)
            {
                for (; i + 4 <= n; i += 4)
                    AdvSimd.Store(y + i, Exp(AdvSimd.LoadVector128(x + i)));
            }
            else if (Fma.IsSupported && Avx2.IsSupported)
            {
                for (; i + 8 <= n; i += 8)
                    Avx.Store(y + i, Exp(Avx.LoadVector256(x + i)));
            }
            for (; i < n; i++)
                y[i] = expf(x[i]);
        }
    }

    /// <summary>In place: x = exp(x - max) / sum.</summary>
    /// <safety>x must cover n floats, n &gt; 0.</safety>
    public static unsafe void Softmax(float* x, int n)
    {
        unsafe
        {
            if (PackedSimd.IsSupported)
            {
                ZeroSimdKernelSoftmax(x, n);
                return;
            }
            float max = Max(x, n);
            int i = 0;
            float sum = 0;
            if (AdvSimd.Arm64.IsSupported)
            {
                Vector128<float> vmax = AdvSimd.DuplicateToVector128(max);
                Vector128<float> vsum = default;
                for (; i + 4 <= n; i += 4)
                {
                    Vector128<float> e = Exp(AdvSimd.Subtract(AdvSimd.LoadVector128(x + i), vmax));
                    AdvSimd.Store(x + i, e);
                    vsum = AdvSimd.Add(vsum, e);
                }
                sum = HorizontalSum(vsum);
            }
            else if (Fma.IsSupported && Avx2.IsSupported)
            {
                Vector256<float> vmax = Vector256.Create(max);
                Vector256<float> vsum = default;
                for (; i + 8 <= n; i += 8)
                {
                    Vector256<float> e = Exp(Avx.Subtract(Avx.LoadVector256(x + i), vmax));
                    Avx.Store(x + i, e);
                    vsum = Avx.Add(vsum, e);
                }
                sum = HorizontalSum(vsum);
            }
            for (; i < n; i++)
            {
                x[i] = expf(x[i] - max);
                sum += x[i];
            }
            Scale(x, n, 1.0f / sum);
        }
    }

    /// <summary>In place: x = x * sigmoid(x) = x / (1 + exp(-x)).</summary>
    /// <safety>x must cover n floats.</safety>
    public static unsafe void Silu(float* x, int n)
    {
        unsafe
        {
            if (PackedSimd.IsSupported)
            {
                ZeroSimdKernelSilu(x, n);
                return;
            }
            int i = 0;
            if (AdvSimd.Arm64.IsSupported)
            {
                Vector128<float> one = AdvSimd.DuplicateToVector128(1.0f);
                Vector128<float> zero = default;
                for (; i + 4 <= n; i += 4)
                {
                    Vector128<float> v = AdvSimd.LoadVector128(x + i);
                    Vector128<float> e = Exp(AdvSimd.Subtract(zero, v));
                    AdvSimd.Store(x + i, AdvSimd.Arm64.Divide(v, AdvSimd.Add(one, e)));
                }
            }
            else if (Fma.IsSupported && Avx2.IsSupported)
            {
                Vector256<float> one = Vector256.Create(1.0f);
                Vector256<float> zero = default;
                for (; i + 8 <= n; i += 8)
                {
                    Vector256<float> v = Avx.LoadVector256(x + i);
                    Vector256<float> e = Exp(Avx.Subtract(zero, v));
                    Avx.Store(x + i, Avx.Divide(v, Avx.Add(one, e)));
                }
            }
            for (; i < n; i++)
                x[i] = x[i] / (1.0f + expf(-x[i]));
        }
    }

    /// <summary>y = x / sqrt(mean(x^2) + eps) * weight.</summary>
    /// <safety>y, x and weight must each cover n floats; y may equal x.</safety>
    public static unsafe void RmsNorm(float* y, float* x, float* weight, int n, float eps)
    {
        unsafe
        {
            float scale = 1.0f / sqrtf(Dot(x, x, n) / n + eps);
            if (PackedSimd.IsSupported)
            {
                ZeroSimdKernelScaleMultiply(y, x, weight, n, scale);
                return;
            }
            int i = 0;
            if (AdvSimd.Arm64.IsSupported)
            {
                Vector128<float> s = AdvSimd.DuplicateToVector128(scale);
                for (; i + 4 <= n; i += 4)
                    AdvSimd.Store(y + i, AdvSimd.Multiply(AdvSimd.Multiply(AdvSimd.LoadVector128(x + i), s), AdvSimd.LoadVector128(weight + i)));
            }
            else if (Fma.IsSupported && Avx2.IsSupported)
            {
                Vector256<float> s = Vector256.Create(scale);
                for (; i + 8 <= n; i += 8)
                    Avx.Store(y + i, Avx.Multiply(Avx.Multiply(Avx.LoadVector256(x + i), s), Avx.LoadVector256(weight + i)));
            }
            for (; i < n; i++)
                y[i] = x[i] * scale * weight[i];
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <safety>x must cover n floats, n &gt; 0.</safety>
    static unsafe float Max(float* x, int n)
    {
        unsafe
        {
            int i = 0;
            float max = x[0];
            if (AdvSimd.Arm64.IsSupported && n >= 4)
            {
                Vector128<float> m = AdvSimd.LoadVector128(x);
                for (i = 4; i + 4 <= n; i += 4)
                    m = AdvSimd.Max(m, AdvSimd.LoadVector128(x + i));
                Vector64<float> across = AdvSimd.Arm64.MaxAcross(m);
                max = *(float*)&across;
            }
            else if (Fma.IsSupported && Avx2.IsSupported && n >= 8)
            {
                Vector256<float> m = Avx.LoadVector256(x);
                for (i = 8; i + 8 <= n; i += 8)
                    m = Avx.Max(m, Avx.LoadVector256(x + i));
                float* lanes = stackalloc float[8];
                Avx.Store(lanes, m);
                max = lanes[0];
                for (int k = 1; k < 8; k++)
                    max = lanes[k] > max ? lanes[k] : max;
            }
            for (; i < n; i++)
                max = x[i] > max ? x[i] : max;
            return max;
        }
    }

    /// <safety>x must cover n floats.</safety>
    static unsafe void Scale(float* x, int n, float factor)
    {
        unsafe
        {
            int i = 0;
            if (AdvSimd.Arm64.IsSupported)
            {
                Vector128<float> f = AdvSimd.DuplicateToVector128(factor);
                for (; i + 4 <= n; i += 4)
                    AdvSimd.Store(x + i, AdvSimd.Multiply(AdvSimd.LoadVector128(x + i), f));
            }
            else if (Fma.IsSupported && Avx2.IsSupported)
            {
                Vector256<float> f = Vector256.Create(factor);
                for (; i + 8 <= n; i += 8)
                    Avx.Store(x + i, Avx.Multiply(Avx.LoadVector256(x + i), f));
            }
            for (; i < n; i++)
                x[i] *= factor;
        }
    }

    static float HorizontalSum(Vector128<float> v)
    {
        v = AdvSimd.Arm64.AddPairwise(v, v);
        v = AdvSimd.Arm64.AddPairwise(v, v); // every lane now holds the total
        float* lanes = (float*)&v;
        unsafe { return lanes[0]; }
    }

    static float HorizontalSum(Vector256<float> v)
    {
        float* lanes = stackalloc float[8];
        unsafe
        {
            Avx.Store(lanes, v);
            return (lanes[0] + lanes[4]) + (lanes[1] + lanes[5]) + (lanes[2] + lanes[6]) + (lanes[3] + lanes[7]);
        }
    }

    static Vector128<float> Exp(Vector128<float> x)
    {
        x = AdvSimd.Min(AdvSimd.Max(x, AdvSimd.DuplicateToVector128(ExpLo)), AdvSimd.DuplicateToVector128(ExpHi));
        Vector128<float> fx = AdvSimd.RoundToNearest(AdvSimd.Multiply(x, AdvSimd.DuplicateToVector128(Log2E)));
        Vector128<float> r = AdvSimd.FusedMultiplyAdd(x, fx, AdvSimd.DuplicateToVector128(NegLn2Hi));
        r = AdvSimd.FusedMultiplyAdd(r, fx, AdvSimd.DuplicateToVector128(NegLn2Lo));

        Vector128<float> p = AdvSimd.DuplicateToVector128(P0);
        p = AdvSimd.FusedMultiplyAdd(AdvSimd.DuplicateToVector128(P1), p, r);
        p = AdvSimd.FusedMultiplyAdd(AdvSimd.DuplicateToVector128(P2), p, r);
        p = AdvSimd.FusedMultiplyAdd(AdvSimd.DuplicateToVector128(P3), p, r);
        p = AdvSimd.FusedMultiplyAdd(AdvSimd.DuplicateToVector128(P4), p, r);
        p = AdvSimd.FusedMultiplyAdd(AdvSimd.DuplicateToVector128(P5), p, r);
        Vector128<float> y = AdvSimd.FusedMultiplyAdd(AdvSimd.Add(r, AdvSimd.DuplicateToVector128(1.0f)), p, AdvSimd.Multiply(r, r));

        // 2^n: fx is an exact integer, so fx + 127 is too; put it into the exponent field.
        Vector128<int> n = AdvSimd.ConvertToInt32RoundToEven(AdvSimd.Add(fx, AdvSimd.DuplicateToVector128(127.0f)));
        Vector128<float> pow2 = AdvSimd.ShiftLeftLogical(n.AsUInt32(), 23).AsSingle();
        return AdvSimd.Multiply(y, pow2);
    }

    static Vector256<float> Exp(Vector256<float> x)
    {
        x = Avx.Min(Avx.Max(x, Vector256.Create(ExpLo)), Vector256.Create(ExpHi));
        Vector256<float> fx = Avx.RoundToNearestInteger(Avx.Multiply(x, Vector256.Create(Log2E)));
        Vector256<float> r = Fma.MultiplyAdd(fx, Vector256.Create(NegLn2Hi), x);
        r = Fma.MultiplyAdd(fx, Vector256.Create(NegLn2Lo), r);

        Vector256<float> p = Vector256.Create(P0);
        p = Fma.MultiplyAdd(p, r, Vector256.Create(P1));
        p = Fma.MultiplyAdd(p, r, Vector256.Create(P2));
        p = Fma.MultiplyAdd(p, r, Vector256.Create(P3));
        p = Fma.MultiplyAdd(p, r, Vector256.Create(P4));
        p = Fma.MultiplyAdd(p, r, Vector256.Create(P5));
        Vector256<float> y = Fma.MultiplyAdd(p, Avx.Multiply(r, r), Avx.Add(r, Vector256.Create(1.0f)));

        // 2^n: fx is an exact integer, so fx + 127 is too; put it into the exponent field.
        Vector256<int> n = Avx.ConvertToVector256Int32(Avx.Add(fx, Vector256.Create(127.0f)));
        Vector256<float> pow2 = Avx2.ShiftLeftLogical(n, 23).AsSingle();
        return Avx.Multiply(y, pow2);
    }
}
