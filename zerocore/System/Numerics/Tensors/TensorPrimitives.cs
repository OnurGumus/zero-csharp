using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

// The same check, for x != x (NaN) in the Max and Min operators.
#pragma warning disable CS1718

namespace System.Numerics.Tensors
{
    // .NET's TensorPrimitives (the System.Numerics.Tensors package) for
    // float: operations on whole spans, written to a destination span or
    // reduced to one value. The names and argument checks are .NET's, and so
    // are the results of the arithmetic. Exp, Sigmoid, Tanh and SoftMax have
    // vector forms a few ulps from exact, as .NET's do (but exact near 0,
    // where .NET's Tanh is off by 6e-8); the other functions are MathF's,
    // one element at a time. SoftMax subtracts the largest value first, so
    // large inputs don't overflow to NaN. Sums may round
    // differently, since they add in another order (as .NET's do from one
    // processor to another).
    //
    // Element-wise arithmetic, the sums and the four fast functions use the
    // processor's vectors: NEON on arm64; on x64 AVX when built with
    // -m x86-64-v3 (AVX2 and FMA for the functions), SSE2 otherwise, since
    // every x64 processor has it. WebAssembly, DOS and the rest run scalar
    // loops. The arithmetic gives the same bits as the scalar loop, since each
    // element is still one IEEE operation; sums add in another order.
    //
    // A destination may be the same span as an input (the operation is then
    // done in place) but not overlap one partly; that, a destination shorter
    // than the input, and inputs of different lengths end the program with
    // .NET's ArgumentException message.
    public static unsafe class TensorPrimitives
    {
        // ---- checks ----------------------------------------------------------

        private static void CheckDestination(int length, Span<float> destination)
        {
            if (destination.Length < length)
                throw new ArgumentException("Destination is too short.");
        }

        private static void CheckSameLength(int x, int y)
        {
            if (x != y)
                throw new ArgumentException("Input span arguments must all have the same length.");
        }

        private static void CheckNotEmpty(int length)
        {
            if (length == 0)
                throw new ArgumentException("Input span arguments must not be empty.");
        }

        private static void CheckOverlap(ReadOnlySpan<float> input, Span<float> destination)
        {
            fixed (float* i = input)
            fixed (float* d = destination)
            {
                if (i != d && i < d + destination.Length && d < i + input.Length)
                    throw new ArgumentException("The destination span may only overlap with an input span if the two spans start at the same memory location.");
            }
        }

        // ---- operators ---------------------------------------------------------

        // An operation on one element, and on 4 (NEON, SSE) or 8 (AVX) at once.
        // The vector forms are only called when that instruction set is there.
        private interface IBinary
        {
            static abstract bool Vectorizable { get; }
            static abstract float Invoke(float x, float y);
            static abstract Vector128<float> Invoke(Vector128<float> x, Vector128<float> y);
            static abstract Vector256<float> Invoke(Vector256<float> x, Vector256<float> y);
        }

        private struct AddOperator : IBinary
        {
            public static bool Vectorizable => true;
            public static float Invoke(float x, float y) => x + y;
            public static Vector128<float> Invoke(Vector128<float> x, Vector128<float> y) => V.Add(x, y);
            public static Vector256<float> Invoke(Vector256<float> x, Vector256<float> y) => Avx.Add(x, y);
        }

        private struct SubtractOperator : IBinary
        {
            public static bool Vectorizable => true;
            public static float Invoke(float x, float y) => x - y;
            public static Vector128<float> Invoke(Vector128<float> x, Vector128<float> y) => V.Subtract(x, y);
            public static Vector256<float> Invoke(Vector256<float> x, Vector256<float> y) => Avx.Subtract(x, y);
        }

        private struct MultiplyOperator : IBinary
        {
            public static bool Vectorizable => true;
            public static float Invoke(float x, float y) => x * y;
            public static Vector128<float> Invoke(Vector128<float> x, Vector128<float> y) => V.Multiply(x, y);
            public static Vector256<float> Invoke(Vector256<float> x, Vector256<float> y) => Avx.Multiply(x, y);
        }

        private struct DivideOperator : IBinary
        {
            public static bool Vectorizable => true;
            public static float Invoke(float x, float y) => x / y;
            public static Vector128<float> Invoke(Vector128<float> x, Vector128<float> y) => V.Divide(x, y);
            public static Vector256<float> Invoke(Vector256<float> x, Vector256<float> y) => Avx.Divide(x, y);
        }

        // The rest have no vector form here; they run the scalar loop.
        private abstract class ScalarOnly
        {
            public static bool Vectorizable => false;
            public static Vector128<float> Invoke(Vector128<float> x, Vector128<float> y) => x;
            public static Vector256<float> Invoke(Vector256<float> x, Vector256<float> y) => x;
        }

        // IEEE maximum and minimum, as .NET's float.Max and Min: NaN wins,
        // and +0 is greater than -0.
        private sealed class MaxOperator : ScalarOnly, IBinary
        {
            public static float Invoke(float x, float y)
            {
                if (x != y)
                    return x != x ? x : y < x ? x : y;
                return float.IsNegative(y) ? x : y;
            }
        }

        private sealed class MinOperator : ScalarOnly, IBinary
        {
            public static float Invoke(float x, float y)
            {
                if (x != y)
                    return x != x ? x : x < y ? x : y;
                return float.IsNegative(x) ? x : y;
            }
        }

        // The one of larger magnitude; for equal magnitudes the positive one.
        private sealed class MaxMagnitudeOperator : ScalarOnly, IBinary
        {
            public static float Invoke(float x, float y)
            {
                float ax = MathF.Abs(x), ay = MathF.Abs(y);
                if (ax > ay || ax != ax)
                    return x;
                if (ax == ay)
                    return float.IsNegative(x) ? y : x;
                return y;
            }
        }

        // The one of smaller magnitude; for equal magnitudes the negative one.
        private sealed class MinMagnitudeOperator : ScalarOnly, IBinary
        {
            public static float Invoke(float x, float y)
            {
                float ax = MathF.Abs(x), ay = MathF.Abs(y);
                if (ax < ay || ax != ax)
                    return x;
                if (ax == ay)
                    return float.IsNegative(x) ? x : y;
                return y;
            }
        }

        private sealed class PowOperator : ScalarOnly, IBinary
        {
            public static float Invoke(float x, float y) => MathF.Pow(x, y);
        }

        private sealed class Atan2Operator : ScalarOnly, IBinary
        {
            public static float Invoke(float y, float x) => MathF.Atan2(y, x);
        }

        private sealed class CopySignOperator : ScalarOnly, IBinary
        {
            public static float Invoke(float x, float y) => MathF.CopySign(x, y);
        }

        // In double, where the squares can't overflow: correctly rounded
        // nearly always, as .NET's float.Hypot.
        private sealed class HypotOperator : ScalarOnly, IBinary
        {
            public static float Invoke(float x, float y)
            {
                if (float.IsInfinity(x) || float.IsInfinity(y))
                    return float.PositiveInfinity;
                double dx = x, dy = y;
                return (float)Math.Sqrt(dx * dx + dy * dy);
            }
        }

        private interface IUnary
        {
            static abstract float Invoke(float x);
        }

        private struct AbsOperator : IUnary { public static float Invoke(float x) => MathF.Abs(x); }
        private struct NegateOperator : IUnary { public static float Invoke(float x) => -x; }
        private struct LogOperator : IUnary { public static float Invoke(float x) => MathF.Log(x); }
        private struct Log2Operator : IUnary { public static float Invoke(float x) => MathF.Log2(x); }
        private struct Log10Operator : IUnary { public static float Invoke(float x) => MathF.Log10(x); }
        private struct SqrtOperator : IUnary { public static float Invoke(float x) => MathF.Sqrt(x); }
        private struct CbrtOperator : IUnary { public static float Invoke(float x) => MathF.Cbrt(x); }
        private struct SinOperator : IUnary { public static float Invoke(float x) => MathF.Sin(x); }
        private struct CosOperator : IUnary { public static float Invoke(float x) => MathF.Cos(x); }
        private struct TanOperator : IUnary { public static float Invoke(float x) => MathF.Tan(x); }
        private struct AsinOperator : IUnary { public static float Invoke(float x) => MathF.Asin(x); }
        private struct AcosOperator : IUnary { public static float Invoke(float x) => MathF.Acos(x); }
        private struct AtanOperator : IUnary { public static float Invoke(float x) => MathF.Atan(x); }
        private struct SinhOperator : IUnary { public static float Invoke(float x) => MathF.Sinh(x); }
        private struct CoshOperator : IUnary { public static float Invoke(float x) => MathF.Cosh(x); }
        private struct AsinhOperator : IUnary { public static float Invoke(float x) => MathF.Asinh(x); }
        private struct AcoshOperator : IUnary { public static float Invoke(float x) => MathF.Acosh(x); }
        private struct AtanhOperator : IUnary { public static float Invoke(float x) => MathF.Atanh(x); }
        private struct CeilingOperator : IUnary { public static float Invoke(float x) => MathF.Ceiling(x); }
        private struct FloorOperator : IUnary { public static float Invoke(float x) => MathF.Floor(x); }
        private struct TruncateOperator : IUnary { public static float Invoke(float x) => MathF.Truncate(x); }
        private struct RoundOperator : IUnary { public static float Invoke(float x) => MathF.Round(x); }
        private struct ReciprocalOperator : IUnary { public static float Invoke(float x) => 1 / x; }

        // ---- loops -------------------------------------------------------------

        private static void Unary<TOp>(ReadOnlySpan<float> x, Span<float> destination) where TOp : IUnary
        {
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            for (int i = 0; i < x.Length; i++)
                destination[i] = TOp.Invoke(x[i]);
        }

        private static void Binary<TOp>(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) where TOp : IBinary
        {
            CheckSameLength(x.Length, y.Length);
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            CheckOverlap(y, destination);
            fixed (float* px = x)
            fixed (float* py = y)
            fixed (float* pd = destination)
                Binary<TOp>(px, 1, py, 1, pd, x.Length);
        }

        private static void Binary<TOp>(ReadOnlySpan<float> x, float y, Span<float> destination) where TOp : IBinary
        {
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            fixed (float* px = x)
            fixed (float* pd = destination)
                Binary<TOp>(px, 1, &y, 0, pd, x.Length);
        }

        private static void Binary<TOp>(float x, ReadOnlySpan<float> y, Span<float> destination) where TOp : IBinary
        {
            CheckDestination(y.Length, destination);
            CheckOverlap(y, destination);
            fixed (float* py = y)
            fixed (float* pd = destination)
                Binary<TOp>(&x, 0, py, 1, pd, y.Length);
        }

        // A step of 0 makes an argument a scalar, used for every element.
        private static void Binary<TOp>(float* x, int xStep, float* y, int yStep, float* d, int n) where TOp : IBinary
        {
            int i = 0;
            if (TOp.Vectorizable && Avx.IsSupported)
            {
                for (; i + 8 <= n; i += 8)
                {
                    Vector256<float> vx = xStep == 0 ? Vector256.Create(*x) : Avx.LoadVector256(x + i);
                    Vector256<float> vy = yStep == 0 ? Vector256.Create(*y) : Avx.LoadVector256(y + i);
                    Avx.Store(d + i, TOp.Invoke(vx, vy));
                }
            }
            else if (TOp.Vectorizable && V.IsSupported)
            {
                for (; i + 4 <= n; i += 4)
                {
                    Vector128<float> vx = xStep == 0 ? V.Create(*x) : V.Load(x + i);
                    Vector128<float> vy = yStep == 0 ? V.Create(*y) : V.Load(y + i);
                    V.Store(d + i, TOp.Invoke(vx, vy));
                }
            }
            for (; i < n; i++)
                d[i] = TOp.Invoke(x[i * xStep], y[i * yStep]);
        }

        // first(second(x, y), z): (x * y) + z for MultiplyAdd, (x + y) * z
        // for AddMultiply. Two roundings, as in .NET (FusedMultiplyAdd has one).
        private static void Ternary<TFirst, TSecond>(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, Span<float> destination)
            where TFirst : IBinary where TSecond : IBinary
        {
            CheckSameLength(x.Length, y.Length);
            CheckSameLength(x.Length, z.Length);
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            CheckOverlap(y, destination);
            CheckOverlap(z, destination);
            fixed (float* px = x)
            fixed (float* py = y)
            fixed (float* pz = z)
            fixed (float* pd = destination)
                Ternary<TFirst, TSecond>(px, 1, py, 1, pz, 1, pd, x.Length);
        }

        private static void Ternary<TFirst, TSecond>(ReadOnlySpan<float> x, ReadOnlySpan<float> y, float z, Span<float> destination)
            where TFirst : IBinary where TSecond : IBinary
        {
            CheckSameLength(x.Length, y.Length);
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            CheckOverlap(y, destination);
            fixed (float* px = x)
            fixed (float* py = y)
            fixed (float* pd = destination)
                Ternary<TFirst, TSecond>(px, 1, py, 1, &z, 0, pd, x.Length);
        }

        private static void Ternary<TFirst, TSecond>(ReadOnlySpan<float> x, float y, ReadOnlySpan<float> z, Span<float> destination)
            where TFirst : IBinary where TSecond : IBinary
        {
            CheckSameLength(x.Length, z.Length);
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            CheckOverlap(z, destination);
            fixed (float* px = x)
            fixed (float* pz = z)
            fixed (float* pd = destination)
                Ternary<TFirst, TSecond>(px, 1, &y, 0, pz, 1, pd, x.Length);
        }

        private static void Ternary<TFirst, TSecond>(float* x, int xStep, float* y, int yStep, float* z, int zStep, float* d, int n)
            where TFirst : IBinary where TSecond : IBinary
        {
            int i = 0;
            if (Avx.IsSupported)
            {
                for (; i + 8 <= n; i += 8)
                {
                    Vector256<float> vx = xStep == 0 ? Vector256.Create(*x) : Avx.LoadVector256(x + i);
                    Vector256<float> vy = yStep == 0 ? Vector256.Create(*y) : Avx.LoadVector256(y + i);
                    Vector256<float> vz = zStep == 0 ? Vector256.Create(*z) : Avx.LoadVector256(z + i);
                    Avx.Store(d + i, TSecond.Invoke(TFirst.Invoke(vx, vy), vz));
                }
            }
            else if (V.IsSupported)
            {
                for (; i + 4 <= n; i += 4)
                {
                    Vector128<float> vx = xStep == 0 ? V.Create(*x) : V.Load(x + i);
                    Vector128<float> vy = yStep == 0 ? V.Create(*y) : V.Load(y + i);
                    Vector128<float> vz = zStep == 0 ? V.Create(*z) : V.Load(z + i);
                    V.Store(d + i, TSecond.Invoke(TFirst.Invoke(vx, vy), vz));
                }
            }
            for (; i < n; i++)
                d[i] = TSecond.Invoke(TFirst.Invoke(x[i * xStep], y[i * yStep]), z[i * zStep]);
        }

        // ---- element-wise ------------------------------------------------------

        public static void Add(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<AddOperator>(x, y, destination);
        public static void Add(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<AddOperator>(x, y, destination);
        public static void Subtract(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<SubtractOperator>(x, y, destination);
        public static void Subtract(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<SubtractOperator>(x, y, destination);
        public static void Subtract(float x, ReadOnlySpan<float> y, Span<float> destination) => Binary<SubtractOperator>(x, y, destination);
        public static void Multiply(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MultiplyOperator>(x, y, destination);
        public static void Multiply(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MultiplyOperator>(x, y, destination);
        public static void Divide(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<DivideOperator>(x, y, destination);
        public static void Divide(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<DivideOperator>(x, y, destination);
        public static void Divide(float x, ReadOnlySpan<float> y, Span<float> destination) => Binary<DivideOperator>(x, y, destination);

        // -x is x * -1 exactly, -0 and NaN included.
        public static void Negate(ReadOnlySpan<float> x, Span<float> destination) => Binary<MultiplyOperator>(x, -1f, destination);

        /// <summary>(x * y) + addend, rounded twice.</summary>
        public static void MultiplyAdd(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> addend, Span<float> destination) =>
            Ternary<MultiplyOperator, AddOperator>(x, y, addend, destination);
        public static void MultiplyAdd(ReadOnlySpan<float> x, ReadOnlySpan<float> y, float addend, Span<float> destination) =>
            Ternary<MultiplyOperator, AddOperator>(x, y, addend, destination);
        public static void MultiplyAdd(ReadOnlySpan<float> x, float y, ReadOnlySpan<float> addend, Span<float> destination) =>
            Ternary<MultiplyOperator, AddOperator>(x, y, addend, destination);

        /// <summary>(x + y) * multiplier.</summary>
        public static void AddMultiply(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> multiplier, Span<float> destination) =>
            Ternary<AddOperator, MultiplyOperator>(x, y, multiplier, destination);
        public static void AddMultiply(ReadOnlySpan<float> x, ReadOnlySpan<float> y, float multiplier, Span<float> destination) =>
            Ternary<AddOperator, MultiplyOperator>(x, y, multiplier, destination);
        public static void AddMultiply(ReadOnlySpan<float> x, float y, ReadOnlySpan<float> multiplier, Span<float> destination) =>
            Ternary<AddOperator, MultiplyOperator>(x, y, multiplier, destination);

        public static void Max(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MaxOperator>(x, y, destination);
        public static void Min(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MinOperator>(x, y, destination);
        public static void MaxMagnitude(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MaxMagnitudeOperator>(x, y, destination);
        public static void MinMagnitude(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MinMagnitudeOperator>(x, y, destination);
        public static void Pow(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<PowOperator>(x, y, destination);
        public static void Pow(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<PowOperator>(x, y, destination);
        public static void Atan2(ReadOnlySpan<float> y, ReadOnlySpan<float> x, Span<float> destination) => Binary<Atan2Operator>(y, x, destination);
        public static void CopySign(ReadOnlySpan<float> x, ReadOnlySpan<float> sign, Span<float> destination) => Binary<CopySignOperator>(x, sign, destination);
        public static void Hypot(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<HypotOperator>(x, y, destination);

        public static void Abs(ReadOnlySpan<float> x, Span<float> destination) => Unary<AbsOperator>(x, destination);
        public static void Exp(ReadOnlySpan<float> x, Span<float> destination) => VectorUnary<FastExp>(x, destination);
        public static void Log(ReadOnlySpan<float> x, Span<float> destination) => Unary<LogOperator>(x, destination);
        public static void Log2(ReadOnlySpan<float> x, Span<float> destination) => Unary<Log2Operator>(x, destination);
        public static void Log10(ReadOnlySpan<float> x, Span<float> destination) => Unary<Log10Operator>(x, destination);
        public static void Sqrt(ReadOnlySpan<float> x, Span<float> destination) => Unary<SqrtOperator>(x, destination);
        public static void Cbrt(ReadOnlySpan<float> x, Span<float> destination) => Unary<CbrtOperator>(x, destination);
        public static void Sin(ReadOnlySpan<float> x, Span<float> destination) => Unary<SinOperator>(x, destination);
        public static void Cos(ReadOnlySpan<float> x, Span<float> destination) => Unary<CosOperator>(x, destination);
        public static void Tan(ReadOnlySpan<float> x, Span<float> destination) => Unary<TanOperator>(x, destination);
        public static void Asin(ReadOnlySpan<float> x, Span<float> destination) => Unary<AsinOperator>(x, destination);
        public static void Acos(ReadOnlySpan<float> x, Span<float> destination) => Unary<AcosOperator>(x, destination);
        public static void Atan(ReadOnlySpan<float> x, Span<float> destination) => Unary<AtanOperator>(x, destination);
        public static void Sinh(ReadOnlySpan<float> x, Span<float> destination) => Unary<SinhOperator>(x, destination);
        public static void Cosh(ReadOnlySpan<float> x, Span<float> destination) => Unary<CoshOperator>(x, destination);
        public static void Tanh(ReadOnlySpan<float> x, Span<float> destination) => VectorUnary<FastTanh>(x, destination);
        public static void Asinh(ReadOnlySpan<float> x, Span<float> destination) => Unary<AsinhOperator>(x, destination);
        public static void Acosh(ReadOnlySpan<float> x, Span<float> destination) => Unary<AcoshOperator>(x, destination);
        public static void Atanh(ReadOnlySpan<float> x, Span<float> destination) => Unary<AtanhOperator>(x, destination);
        public static void Ceiling(ReadOnlySpan<float> x, Span<float> destination) => Unary<CeilingOperator>(x, destination);
        public static void Floor(ReadOnlySpan<float> x, Span<float> destination) => Unary<FloorOperator>(x, destination);
        public static void Truncate(ReadOnlySpan<float> x, Span<float> destination) => Unary<TruncateOperator>(x, destination);

        /// <summary>Rounds to the nearest whole number; halfway cases go to the even one.</summary>
        public static void Round(ReadOnlySpan<float> x, Span<float> destination) => Unary<RoundOperator>(x, destination);

        public static void Reciprocal(ReadOnlySpan<float> x, Span<float> destination) => Unary<ReciprocalOperator>(x, destination);

        /// <summary>1 / (1 + e^-x) for each element.</summary>
        public static void Sigmoid(ReadOnlySpan<float> x, Span<float> destination)
        {
            CheckNotEmpty(x.Length);
            VectorUnary<FastSigmoid>(x, destination);
        }

        /// <summary>
        /// e^x / Σ e^x over the span: probabilities that add up to 1. Computed
        /// as e^(x - max) / Σ e^(x - max), which is the same but doesn't
        /// overflow for x above 88 (.NET's gives NaN there). x - max is rounded
        /// in float; its rounding error, recovered exactly (TwoSum), corrects
        /// the result, which would otherwise lose up to 66 ulps on the small
        /// probabilities.
        /// </summary>
        public static void SoftMax(ReadOnlySpan<float> x, Span<float> destination)
        {
            CheckNotEmpty(x.Length);
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            int n = x.Length;
            fixed (float* px = x)
            fixed (float* pd = destination)
            {
                if (!FastExpAvailable || !MaxIfFinite(px, n, out float max))
                {
                    SoftMaxScalar(px, pd, n);
                    return;
                }
                float sum = 0;
                int i = 0;
                if (!(Avx2.IsSupported && Fma.IsSupported))
                {
                    Vector128<float> vmax = V.Create(max), negMax = V.Create(-max);
                    Vector128<float> vsum = default;
                    for (; i + 4 <= n; i += 4)
                    {
                        if (!AllAtLeast(px + i, 4, max + ExpLo))
                        {
                            for (int j = i; j < i + 4; j++)
                                sum += pd[j] = (float)Math.Exp((double)px[j] - max);
                            continue;
                        }
                        // d + error = x - max exactly (TwoSum), and
                        // e^(d + error) = e^d (1 + error) to float precision.
                        Vector128<float> v = V.Load(px + i);
                        Vector128<float> d = V.Subtract(v, vmax);
                        Vector128<float> bb = V.Subtract(d, v);
                        Vector128<float> error = V.Add(V.Subtract(v, V.Subtract(d, bb)), V.Subtract(negMax, bb));
                        Vector128<float> e = V.Exp(d);
                        e = V.MultiplyAdd(e, error, e);
                        V.Store(pd + i, e);
                        vsum = V.Add(vsum, e);
                    }
                    sum += V.Sum(vsum);
                }
                else
                {
                    Vector256<float> vmax = Vector256.Create(max), negMax = Vector256.Create(-max);
                    Vector256<float> vsum = default;
                    for (; i + 8 <= n; i += 8)
                    {
                        if (!AllAtLeast(px + i, 8, max + ExpLo))
                        {
                            for (int j = i; j < i + 8; j++)
                                sum += pd[j] = (float)Math.Exp((double)px[j] - max);
                            continue;
                        }
                        Vector256<float> v = Avx.LoadVector256(px + i);
                        Vector256<float> d = Avx.Subtract(v, vmax);
                        Vector256<float> bb = Avx.Subtract(d, v);
                        Vector256<float> error = Avx.Add(Avx.Subtract(v, Avx.Subtract(d, bb)), Avx.Subtract(negMax, bb));
                        Vector256<float> e = Exp256(d);
                        e = Fma.MultiplyAdd(e, error, e);
                        Avx.Store(pd + i, e);
                        vsum = Avx.Add(vsum, e);
                    }
                    sum += HorizontalSum(vsum);
                }
                for (; i < n; i++)
                    sum += pd[i] = (float)Math.Exp((double)px[i] - max);
                Binary<DivideOperator>(pd, 1, &sum, 0, pd, n);
            }
        }

        // Without the vector exp, or with an infinity or NaN in x: in double,
        // where x - max is exact.
        private static void SoftMaxScalar(float* x, float* d, int n)
        {
            double max = x[0];
            for (int i = 1; i < n; i++)
                max = MaxOperator.Invoke((float)max, x[i]);
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += Math.Exp(x[i] - max);
            for (int i = 0; i < n; i++)
                d[i] = (float)(Math.Exp(x[i] - max) / sum);
        }

        // The largest value, when every value is finite (vector Max doesn't
        // order NaN the way MaxOperator does, and SoftMaxScalar handles those).
        private static bool MaxIfFinite(float* x, int n, out float max)
        {
            int i = 0;
            float finite = 0; // stays 0 while x * 0 does: NaN for an infinity or a NaN
            max = x[0];
            if (Avx.IsSupported)
            {
                Vector256<float> vmax = Vector256.Create(x[0]), check = default;
                for (; i + 8 <= n; i += 8)
                {
                    Vector256<float> v = Avx.LoadVector256(x + i);
                    vmax = Avx.Max(vmax, v);
                    check = Avx.Add(check, Avx.Multiply(v, default));
                }
                float* lanes = stackalloc float[8];
                Avx.Store(lanes, vmax);
                for (int j = 0; j < 8; j++)
                    max = lanes[j] > max ? lanes[j] : max;
                finite = HorizontalSum(check);
            }
            else if (V.IsSupported)
            {
                Vector128<float> vmax = V.Create(x[0]), check = default;
                for (; i + 4 <= n; i += 4)
                {
                    Vector128<float> v = V.Load(x + i);
                    vmax = V.Max(vmax, v);
                    check = V.Add(check, V.Multiply(v, default));
                }
                max = V.MaxAcross(vmax);
                finite = V.Sum(check);
            }
            for (; i < n; i++)
            {
                finite += x[i] * 0;
                max = x[i] > max ? x[i] : max;
            }
            return finite == 0;
        }

        public static void ConvertToHalf(ReadOnlySpan<float> source, Span<Half> destination)
        {
            if (destination.Length < source.Length)
                throw new ArgumentException("Destination is too short.");
            for (int i = 0; i < source.Length; i++)
                destination[i] = (Half)source[i];
        }

        public static void ConvertToSingle(ReadOnlySpan<Half> source, Span<float> destination)
        {
            CheckDestination(source.Length, destination);
            for (int i = 0; i < source.Length; i++)
                destination[i] = (float)source[i];
        }

        // ---- reductions ----------------------------------------------------------

        public static float Sum(ReadOnlySpan<float> x)
        {
            fixed (float* px = x)
                return Sum(px, x.Length);
        }

        // Four running sums of 4 (or 8) lanes each, added at the end.
        private static float Sum(float* x, int n)
        {
            int i = 0;
            float sum = 0;
            if (Avx.IsSupported)
            {
                Vector256<float> s0 = default, s1 = default;
                for (; i + 16 <= n; i += 16)
                {
                    s0 = Avx.Add(s0, Avx.LoadVector256(x + i));
                    s1 = Avx.Add(s1, Avx.LoadVector256(x + i + 8));
                }
                sum = HorizontalSum(Avx.Add(s0, s1));
            }
            else if (V.IsSupported)
            {
                Vector128<float> s0 = default, s1 = default, s2 = default, s3 = default;
                for (; i + 16 <= n; i += 16)
                {
                    s0 = V.Add(s0, V.Load(x + i));
                    s1 = V.Add(s1, V.Load(x + i + 4));
                    s2 = V.Add(s2, V.Load(x + i + 8));
                    s3 = V.Add(s3, V.Load(x + i + 12));
                }
                sum = V.Sum(V.Add(V.Add(s0, s1), V.Add(s2, s3)));
            }
            for (; i < n; i++)
                sum += x[i];
            return sum;
        }

        /// <summary>Σ x[i] * y[i]; 0 for empty spans.</summary>
        public static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        {
            CheckSameLength(x.Length, y.Length);
            fixed (float* px = x)
            fixed (float* py = y)
                return Dot(px, py, x.Length);
        }

        private static float Dot(float* x, float* y, int n)
        {
            int i = 0;
            float sum = 0;
            if (Avx.IsSupported)
            {
                Vector256<float> s0 = default, s1 = default;
                for (; i + 16 <= n; i += 16)
                {
                    s0 = Avx.Add(s0, Avx.Multiply(Avx.LoadVector256(x + i), Avx.LoadVector256(y + i)));
                    s1 = Avx.Add(s1, Avx.Multiply(Avx.LoadVector256(x + i + 8), Avx.LoadVector256(y + i + 8)));
                }
                sum = HorizontalSum(Avx.Add(s0, s1));
            }
            else if (V.IsSupported)
            {
                Vector128<float> s0 = default, s1 = default, s2 = default, s3 = default;
                for (; i + 16 <= n; i += 16)
                {
                    s0 = V.Add(s0, V.Multiply(V.Load(x + i), V.Load(y + i)));
                    s1 = V.Add(s1, V.Multiply(V.Load(x + i + 4), V.Load(y + i + 4)));
                    s2 = V.Add(s2, V.Multiply(V.Load(x + i + 8), V.Load(y + i + 8)));
                    s3 = V.Add(s3, V.Multiply(V.Load(x + i + 12), V.Load(y + i + 12)));
                }
                sum = V.Sum(V.Add(V.Add(s0, s1), V.Add(s2, s3)));
            }
            for (; i < n; i++)
                sum += x[i] * y[i];
            return sum;
        }

        public static float SumOfSquares(ReadOnlySpan<float> x)
        {
            fixed (float* px = x)
                return Dot(px, px, x.Length);
        }

        public static float SumOfMagnitudes(ReadOnlySpan<float> x)
        {
            float sum = 0;
            for (int i = 0; i < x.Length; i++)
                sum += MathF.Abs(x[i]);
            return sum;
        }

        /// <summary>The Euclidean length, √Σ x².</summary>
        public static float Norm(ReadOnlySpan<float> x) => MathF.Sqrt(SumOfSquares(x));

        /// <summary>√Σ (x - y)²; the spans must not be empty.</summary>
        public static float Distance(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        {
            CheckNotEmpty(x.Length);
            CheckSameLength(x.Length, y.Length);
            float sum = 0;
            for (int i = 0; i < x.Length; i++)
            {
                float d = x[i] - y[i];
                sum += d * d;
            }
            return MathF.Sqrt(sum);
        }

        /// <summary>Dot(x, y) / (Norm(x) * Norm(y)); the spans must not be empty.</summary>
        public static float CosineSimilarity(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        {
            CheckNotEmpty(x.Length);
            CheckSameLength(x.Length, y.Length);
            fixed (float* px = x)
            fixed (float* py = y)
                return Dot(px, py, x.Length) / (MathF.Sqrt(Dot(px, px, x.Length)) * MathF.Sqrt(Dot(py, py, x.Length)));
        }

        public static float Product(ReadOnlySpan<float> x)
        {
            CheckNotEmpty(x.Length);
            float product = 1;
            for (int i = 0; i < x.Length; i++)
                product *= x[i];
            return product;
        }

        /// <summary>Π (x - y).</summary>
        public static float ProductOfDifferences(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        {
            CheckNotEmpty(x.Length);
            CheckSameLength(x.Length, y.Length);
            float product = 1;
            for (int i = 0; i < x.Length; i++)
                product *= x[i] - y[i];
            return product;
        }

        /// <summary>Π (x + y).</summary>
        public static float ProductOfSums(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        {
            CheckNotEmpty(x.Length);
            CheckSameLength(x.Length, y.Length);
            float product = 1;
            for (int i = 0; i < x.Length; i++)
                product *= x[i] + y[i];
            return product;
        }

        // The largest value, NaN if there is one, +0 over -0; the span must
        // not be empty. Min, MaxMagnitude and MinMagnitude likewise.
        public static float Max(ReadOnlySpan<float> x) => Fold<MaxOperator>(x);
        public static float Min(ReadOnlySpan<float> x) => Fold<MinOperator>(x);
        public static float MaxMagnitude(ReadOnlySpan<float> x) => Fold<MaxMagnitudeOperator>(x);
        public static float MinMagnitude(ReadOnlySpan<float> x) => Fold<MinMagnitudeOperator>(x);

        private static float Fold<TOp>(ReadOnlySpan<float> x) where TOp : IBinary
        {
            CheckNotEmpty(x.Length);
            float result = x[0];
            for (int i = 1; i < x.Length && result == result; i++)
                result = TOp.Invoke(result, x[i]);
            return result;
        }

        // The index of the value Max (Min, ...) would return: the first NaN,
        // else the first of equal values (+0 counting as larger than -0);
        // -1 for an empty span.
        public static int IndexOfMax(ReadOnlySpan<float> x) => IndexOf<MaxOperator>(x);
        public static int IndexOfMin(ReadOnlySpan<float> x) => IndexOf<MinOperator>(x);
        public static int IndexOfMaxMagnitude(ReadOnlySpan<float> x) => IndexOf<MaxMagnitudeOperator>(x);
        public static int IndexOfMinMagnitude(ReadOnlySpan<float> x) => IndexOf<MinMagnitudeOperator>(x);

        // IndexOfMax and the others on a tensor (Tensor.IndexOfMax): the same
        // search over its elements in order, so a strided tensor gives the
        // index a dense copy of it would. which: 0 Max, 1 MaxMagnitude, 2 Min,
        // 3 MinMagnitude.
        internal static nint IndexOf(in ReadOnlyTensorSpan<float> x, int which)
        {
            if (x.IsDense && x.FlattenedLength <= int.MaxValue)
            {
                ReadOnlySpan<float> span = MemoryMarshal.CreateReadOnlySpan(in x._reference, (int)x.FlattenedLength);
                return which == 0 ? IndexOfMax(span) : which == 1 ? IndexOfMaxMagnitude(span) : which == 2 ? IndexOfMin(span) : IndexOfMinMagnitude(span);
            }
            return which == 0 ? IndexOf<MaxOperator>(x) : which == 1 ? IndexOf<MaxMagnitudeOperator>(x)
                : which == 2 ? IndexOf<MinOperator>(x) : IndexOf<MinMagnitudeOperator>(x);
        }

        private static nint IndexOf<TOp>(in ReadOnlyTensorSpan<float> x) where TOp : IBinary
        {
            ReadOnlyTensorSpan<float>.Enumerator e = x.GetEnumerator();
            if (!e.MoveNext())
                return -1;
            float result = e.Current;
            nint index = 0;
            for (nint i = 1; result == result && e.MoveNext(); i++)
            {
                float next = TOp.Invoke(result, e.Current);
                if (!next.Equals(result) || (next == 0 && float.IsNegative(next) != float.IsNegative(result)))
                {
                    result = next;
                    index = i;
                }
            }
            return index;
        }

        private static int IndexOf<TOp>(ReadOnlySpan<float> x) where TOp : IBinary
        {
            if (x.Length == 0)
                return -1;
            float result = x[0];
            int index = 0;
            for (int i = 1; i < x.Length && result == result; i++)
            {
                float next = TOp.Invoke(result, x[i]);
                // A different value, or the same one with another sign
                // (bit comparison: -0 and +0 differ, NaNs are all one).
                if (!next.Equals(result) || (next == 0 && float.IsNegative(next) != float.IsNegative(result)))
                {
                    result = next;
                    index = i;
                }
            }
            return index;
        }

        // ---- the rest of .NET's float functions ------------------------------------
        //
        // One element at a time, with float's own formulas (ScalarF.cs): AcosPi is
        // Acos(x) / pi, Exp2M1 is Pow(2, x) - 1, and so on, as .NET computes them.

        private struct AcosPiOperator : IUnary { public static float Invoke(float x) => ScalarF.AcosPi(x); }
        private struct AsinPiOperator : IUnary { public static float Invoke(float x) => ScalarF.AsinPi(x); }
        private struct AtanPiOperator : IUnary { public static float Invoke(float x) => ScalarF.AtanPi(x); }
        private struct CosPiOperator : IUnary { public static float Invoke(float x) => ScalarF.CosPi(x); }
        private struct SinPiOperator : IUnary { public static float Invoke(float x) => ScalarF.SinPi(x); }
        private struct TanPiOperator : IUnary { public static float Invoke(float x) => ScalarF.TanPi(x); }
        private struct DegreesToRadiansOperator : IUnary { public static float Invoke(float x) => ScalarF.DegreesToRadians(x); }
        private struct RadiansToDegreesOperator : IUnary { public static float Invoke(float x) => ScalarF.RadiansToDegrees(x); }
        private struct Exp10Operator : IUnary { public static float Invoke(float x) => ScalarF.Exp10(x); }
        private struct Exp10M1Operator : IUnary { public static float Invoke(float x) => ScalarF.Exp10M1(x); }
        private struct Exp2Operator : IUnary { public static float Invoke(float x) => ScalarF.Exp2(x); }
        private struct Exp2M1Operator : IUnary { public static float Invoke(float x) => ScalarF.Exp2M1(x); }
        private struct ExpM1Operator : IUnary { public static float Invoke(float x) => ScalarF.ExpM1(x); }
        private struct Log10P1Operator : IUnary { public static float Invoke(float x) => ScalarF.Log10P1(x); }
        private struct Log2P1Operator : IUnary { public static float Invoke(float x) => ScalarF.Log2P1(x); }
        private struct LogP1Operator : IUnary { public static float Invoke(float x) => ScalarF.LogP1(x); }
        private struct IncrementOperator : IUnary { public static float Invoke(float x) => x + 1; }
        private struct DecrementOperator : IUnary { public static float Invoke(float x) => x - 1; }
        private sealed class Atan2PiOperator : ScalarOnly, IBinary { public static float Invoke(float x, float y) => ScalarF.Atan2Pi(x, y); }
        private sealed class Ieee754RemainderOperator : ScalarOnly, IBinary { public static float Invoke(float x, float y) => ScalarF.Ieee754Remainder(x, y); }
        private sealed class MaxNumberOperator : ScalarOnly, IBinary { public static float Invoke(float x, float y) => ScalarF.MaxNumber(x, y); }
        private sealed class MinNumberOperator : ScalarOnly, IBinary { public static float Invoke(float x, float y) => ScalarF.MinNumber(x, y); }
        private sealed class MaxMagnitudeNumberOperator : ScalarOnly, IBinary { public static float Invoke(float x, float y) => ScalarF.MaxMagnitudeNumber(x, y); }
        private sealed class MinMagnitudeNumberOperator : ScalarOnly, IBinary { public static float Invoke(float x, float y) => ScalarF.MinMagnitudeNumber(x, y); }
        private sealed class LogBaseOperator : ScalarOnly, IBinary { public static float Invoke(float x, float y) => ScalarF.Log(x, y); }

        public static void AcosPi(ReadOnlySpan<float> x, Span<float> destination) => Unary<AcosPiOperator>(x, destination);
        public static void AsinPi(ReadOnlySpan<float> x, Span<float> destination) => Unary<AsinPiOperator>(x, destination);
        public static void AtanPi(ReadOnlySpan<float> x, Span<float> destination) => Unary<AtanPiOperator>(x, destination);
        public static void CosPi(ReadOnlySpan<float> x, Span<float> destination) => Unary<CosPiOperator>(x, destination);
        public static void SinPi(ReadOnlySpan<float> x, Span<float> destination) => Unary<SinPiOperator>(x, destination);
        public static void TanPi(ReadOnlySpan<float> x, Span<float> destination) => Unary<TanPiOperator>(x, destination);
        public static void DegreesToRadians(ReadOnlySpan<float> x, Span<float> destination) => Unary<DegreesToRadiansOperator>(x, destination);
        public static void RadiansToDegrees(ReadOnlySpan<float> x, Span<float> destination) => Unary<RadiansToDegreesOperator>(x, destination);
        public static void Exp10(ReadOnlySpan<float> x, Span<float> destination) => Unary<Exp10Operator>(x, destination);
        public static void Exp10M1(ReadOnlySpan<float> x, Span<float> destination) => Unary<Exp10M1Operator>(x, destination);
        public static void Exp2(ReadOnlySpan<float> x, Span<float> destination) => Unary<Exp2Operator>(x, destination);
        public static void Exp2M1(ReadOnlySpan<float> x, Span<float> destination) => Unary<Exp2M1Operator>(x, destination);
        public static void ExpM1(ReadOnlySpan<float> x, Span<float> destination) => Unary<ExpM1Operator>(x, destination);
        public static void Log10P1(ReadOnlySpan<float> x, Span<float> destination) => Unary<Log10P1Operator>(x, destination);
        public static void Log2P1(ReadOnlySpan<float> x, Span<float> destination) => Unary<Log2P1Operator>(x, destination);
        public static void LogP1(ReadOnlySpan<float> x, Span<float> destination) => Unary<LogP1Operator>(x, destination);
        public static void Increment(ReadOnlySpan<float> x, Span<float> destination) => Unary<IncrementOperator>(x, destination);
        public static void Decrement(ReadOnlySpan<float> x, Span<float> destination) => Unary<DecrementOperator>(x, destination);

        public static void Atan2Pi(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<Atan2PiOperator>(x, y, destination);
        public static void Atan2Pi(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<Atan2PiOperator>(x, y, destination);
        public static void Atan2Pi(float x, ReadOnlySpan<float> y, Span<float> destination) => Binary<Atan2PiOperator>(x, y, destination);
        public static void Ieee754Remainder(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<Ieee754RemainderOperator>(x, y, destination);
        public static void Ieee754Remainder(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<Ieee754RemainderOperator>(x, y, destination);
        public static void Ieee754Remainder(float x, ReadOnlySpan<float> y, Span<float> destination) => Binary<Ieee754RemainderOperator>(x, y, destination);
        public static void Atan2(ReadOnlySpan<float> y, float x, Span<float> destination) => Binary<Atan2Operator>(y, x, destination);
        public static void Atan2(float y, ReadOnlySpan<float> x, Span<float> destination) => Binary<Atan2Operator>(y, x, destination);
        public static void Pow(float x, ReadOnlySpan<float> y, Span<float> destination) => Binary<PowOperator>(x, y, destination);
        public static void CopySign(ReadOnlySpan<float> x, float sign, Span<float> destination) => Binary<CopySignOperator>(x, sign, destination);
        public static void Log(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<LogBaseOperator>(x, y, destination);
        public static void Log(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<LogBaseOperator>(x, y, destination);
        public static void Max(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MaxOperator>(x, y, destination);
        public static void Min(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MinOperator>(x, y, destination);
        public static void MaxMagnitude(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MaxMagnitudeOperator>(x, y, destination);
        public static void MinMagnitude(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MinMagnitudeOperator>(x, y, destination);
        public static void MaxNumber(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MaxNumberOperator>(x, y, destination);
        public static void MaxNumber(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MaxNumberOperator>(x, y, destination);
        public static void MinNumber(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MinNumberOperator>(x, y, destination);
        public static void MinNumber(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MinNumberOperator>(x, y, destination);
        public static void MaxMagnitudeNumber(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MaxMagnitudeNumberOperator>(x, y, destination);
        public static void MaxMagnitudeNumber(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MaxMagnitudeNumberOperator>(x, y, destination);
        public static void MinMagnitudeNumber(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination) => Binary<MinMagnitudeNumberOperator>(x, y, destination);
        public static void MinMagnitudeNumber(ReadOnlySpan<float> x, float y, Span<float> destination) => Binary<MinMagnitudeNumberOperator>(x, y, destination);

        // The largest number, ignoring NaNs (NaN only if all are); +0 over -0.
        // The other three likewise. The span must not be empty.
        public static float MaxNumber(ReadOnlySpan<float> x)
        {
            CheckNotEmpty(x.Length);
            float result = x[0];
            for (int i = 1; i < x.Length; i++)
                result = ScalarF.MaxNumber(result, x[i]);
            return result;
        }

        public static float MinNumber(ReadOnlySpan<float> x)
        {
            CheckNotEmpty(x.Length);
            float result = x[0];
            for (int i = 1; i < x.Length; i++)
                result = ScalarF.MinNumber(result, x[i]);
            return result;
        }

        public static float MaxMagnitudeNumber(ReadOnlySpan<float> x)
        {
            CheckNotEmpty(x.Length);
            float result = x[0];
            for (int i = 1; i < x.Length; i++)
                result = ScalarF.MaxMagnitudeNumber(result, x[i]);
            return result;
        }

        public static float MinMagnitudeNumber(ReadOnlySpan<float> x)
        {
            CheckNotEmpty(x.Length);
            float result = x[0];
            for (int i = 1; i < x.Length; i++)
                result = ScalarF.MinMagnitudeNumber(result, x[i]);
            return result;
        }

        // x^(1/n) for each element, as .NET's float.RootN.
        public static void RootN(ReadOnlySpan<float> x, int n, Span<float> destination)
        {
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            for (int i = 0; i < x.Length; i++)
                destination[i] = ScalarF.RootN(x[i], n);
        }

        // The exponent of each element (floor(log2 |x|)): int.MinValue for 0,
        // int.MaxValue for infinities and NaN.
        public static void ILogB(ReadOnlySpan<float> x, Span<int> destination)
        {
            if (destination.Length < x.Length)
                throw new ArgumentException("Destination is too short.");
            for (int i = 0; i < x.Length; i++)
                destination[i] = ScalarF.ILogB(x[i]);
        }

        // The mean; the span must not be empty.
        public static float Average(ReadOnlySpan<float> x)
        {
            CheckNotEmpty(x.Length);
            return Sum(x) / x.Length;
        }

        // ---- the fast functions ----------------------------------------------------
        //
        // Exp, Sigmoid, Tanh and SoftMax are the ones neural networks spend
        // their time in, so they have vector forms, built on Cephes' expf
        // (as lib/Kernels.cs): reduce by ln 2, a degree-6 polynomial, scale by
        // 2^n through the exponent bits. A few ulps, like .NET's. SSE2 has no
        // fused multiply-add and no rounding instruction: there the polynomial
        // rounds after each step and fx is rounded through a conversion to
        // int (to nearest, ties to even); ln 2's high part has 9 bits, so
        // fx * ln2Hi is exact either way. A block of 4
        // or 8 with a value outside the range where that works (a NaN, or a
        // result that overflows or is subnormal) is done by the scalar
        // function instead, so special values come out exactly as MathF's.

        private const float ExpLo = -87f, ExpHi = 88f;
        private const float Log2E = 1.44269504088896341f;
        private const float NegLn2Hi = -0.693359375f, NegLn2Lo = 2.12194440e-4f; // -ln 2 in two parts
        private const float P0 = 1.9875691500e-4f, P1 = 1.3981999507e-3f, P2 = 8.3334519073e-3f;
        private const float P3 = 4.1665795894e-2f, P4 = 1.6666665459e-1f, P5 = 5.0000001201e-1f;

        private static bool FastExpAvailable => V.IsSupported || (Avx2.IsSupported && Fma.IsSupported);

        private interface IVectorUnary
        {
            static abstract float Invoke(float x);
            static abstract bool InRange(float x);
            static abstract Vector128<float> Invoke(Vector128<float> x);
            static abstract Vector256<float> Invoke(Vector256<float> x);
        }

        private struct FastExp : IVectorUnary
        {
            public static float Invoke(float x) => MathF.Exp(x);
            public static bool InRange(float x) => x >= ExpLo && x <= ExpHi;
            public static Vector128<float> Invoke(Vector128<float> x) => V.Exp(x);
            public static Vector256<float> Invoke(Vector256<float> x) => Exp256(x);
        }

        // 1 / (1 + e^-x). The scalar form is in double, where e^-x doesn't
        // overflow before the result underflows.
        private struct FastSigmoid : IVectorUnary
        {
            public static float Invoke(float x) => (float)(1 / (1 + Math.Exp(-x)));
            public static bool InRange(float x) => x >= -ExpHi && x <= -ExpLo;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Invoke(Vector128<float> x)
            {
                Vector128<float> one = V.Create(1f);
                return V.Divide(one, V.Add(one, V.Exp(V.Subtract(default, x))));
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<float> Invoke(Vector256<float> x)
            {
                Vector256<float> one = Vector256.Create(1f);
                return Avx.Divide(one, Avx.Add(one, Exp256(Avx.Subtract(default, x))));
            }
        }

        // Below |x| = 0.625 Cephes' odd polynomial, x + x^3 P(x^2), exact for
        // tiny x (where .NET's is off by 6e-8); above it 1 - 2 / (e^2|x| + 1),
        // with |x| capped at 40, where tanh is 1 in float. The sign is x's.
        private struct FastTanh : IVectorUnary
        {
            private const float T0 = -5.70498872745e-3f, T1 = 2.06390887954e-2f, T2 = -5.37397155531e-2f;
            private const float T3 = 1.33314422036e-1f, T4 = -3.33332819422e-1f;

            public static float Invoke(float x) => MathF.Tanh(x);
            public static bool InRange(float x) => x == x;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Invoke(Vector128<float> x)
            {
                Vector128<float> a = V.Abs(x);
                Vector128<float> z = V.Multiply(a, a);
                Vector128<float> p = V.Create(T0);
                p = V.MultiplyAdd(p, z, V.Create(T1));
                p = V.MultiplyAdd(p, z, V.Create(T2));
                p = V.MultiplyAdd(p, z, V.Create(T3));
                p = V.MultiplyAdd(p, z, V.Create(T4));
                Vector128<float> small = V.MultiplyAdd(V.Multiply(p, z), a, a);

                Vector128<float> one = V.Create(1f);
                Vector128<float> capped = V.Min(a, V.Create(40f));
                Vector128<float> e = V.Exp(V.Add(capped, capped));
                Vector128<float> large = V.Subtract(one, V.Divide(V.Create(2f), V.Add(e, one)));

                Vector128<float> t = V.Select(V.LessThan(a, V.Create(0.625f)), small, large);
                return V.Select(V.Create(-0f), x, t); // x's sign bit, t's others
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<float> Invoke(Vector256<float> x)
            {
                Vector256<float> signBit = Vector256.Create(-0f);
                Vector256<float> a = Avx.AndNot(signBit, x);
                Vector256<float> z = Avx.Multiply(a, a);
                Vector256<float> p = Vector256.Create(T0);
                p = Fma.MultiplyAdd(p, z, Vector256.Create(T1));
                p = Fma.MultiplyAdd(p, z, Vector256.Create(T2));
                p = Fma.MultiplyAdd(p, z, Vector256.Create(T3));
                p = Fma.MultiplyAdd(p, z, Vector256.Create(T4));
                Vector256<float> small = Fma.MultiplyAdd(Avx.Multiply(p, z), a, a);

                Vector256<float> one = Vector256.Create(1f);
                Vector256<float> capped = Avx.Min(a, Vector256.Create(40f));
                Vector256<float> e = Exp256(Avx.Add(capped, capped));
                Vector256<float> large = Avx.Subtract(one, Avx.Divide(Vector256.Create(2f), Avx.Add(e, one)));

                Vector256<float> t = Avx.BlendVariable(large, small, Avx.CompareLessThan(a, Vector256.Create(0.625f)));
                return Avx.Or(Avx.And(signBit, x), t);
            }
        }

        private static void VectorUnary<TOp>(ReadOnlySpan<float> x, Span<float> destination) where TOp : IVectorUnary
        {
            CheckDestination(x.Length, destination);
            CheckOverlap(x, destination);
            int n = x.Length, i = 0;
            fixed (float* px = x)
            fixed (float* pd = destination)
            {
                if (Avx2.IsSupported && Fma.IsSupported)
                {
                    for (; i + 8 <= n; i += 8)
                    {
                        if (AllInRange<TOp>(px + i, 8))
                            Avx.Store(pd + i, TOp.Invoke(Avx.LoadVector256(px + i)));
                        else
                            for (int j = i; j < i + 8; j++)
                                pd[j] = TOp.Invoke(px[j]);
                    }
                }
                else if (V.IsSupported)
                {
                    for (; i + 4 <= n; i += 4)
                    {
                        if (AllInRange<TOp>(px + i, 4))
                            V.Store(pd + i, TOp.Invoke(V.Load(px + i)));
                        else
                            for (int j = i; j < i + 4; j++)
                                pd[j] = TOp.Invoke(px[j]);
                    }
                }
                for (; i < n; i++)
                    pd[i] = TOp.Invoke(px[i]);
            }
        }

        private static bool AllInRange<TOp>(float* x, int count) where TOp : IVectorUnary
        {
            bool all = true;
            for (int j = 0; j < count; j++)
                all &= TOp.InRange(x[j]);
            return all;
        }

        private static bool AllAtLeast(float* x, int count, float low)
        {
            bool all = true;
            for (int j = 0; j < count; j++)
                all &= x[j] >= low;
            return all;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // e^x for x in [ExpLo, ExpHi], 8 at once.
        private static Vector256<float> Exp256(Vector256<float> x)
        {
            Vector256<float> fx = Avx.RoundToNearestInteger(Avx.Multiply(x, Vector256.Create(Log2E)));
            Vector256<float> r = Fma.MultiplyAdd(fx, Vector256.Create(NegLn2Hi), x);
            r = Fma.MultiplyAdd(fx, Vector256.Create(NegLn2Lo), r);
            Vector256<float> p = Vector256.Create(P0);
            p = Fma.MultiplyAdd(p, r, Vector256.Create(P1));
            p = Fma.MultiplyAdd(p, r, Vector256.Create(P2));
            p = Fma.MultiplyAdd(p, r, Vector256.Create(P3));
            p = Fma.MultiplyAdd(p, r, Vector256.Create(P4));
            p = Fma.MultiplyAdd(p, r, Vector256.Create(P5));
            Vector256<float> y = Fma.MultiplyAdd(p, Avx.Multiply(r, r), Avx.Add(r, Vector256.Create(1f)));
            Vector256<int> n = Avx.ConvertToVector256Int32(Avx.Add(fx, Vector256.Create(127f)));
            return Avx.Multiply(y, Avx2.ShiftLeftLogical(n, 23).AsSingle());
        }

        // ---- helpers -------------------------------------------------------------

        private static float HorizontalSum(Vector256<float> v)
        {
            float* lanes = stackalloc float[8];
            Avx.Store(lanes, v);
            return ((lanes[0] + lanes[4]) + (lanes[1] + lanes[5])) + ((lanes[2] + lanes[6]) + (lanes[3] + lanes[7]));
        }

        // 4 floats at once, with NEON on arm64 and SSE/SSE2 on x64: each
        // method is one instruction (or a few), picked at compile time, since
        // IsSupported is a constant there.
        private static class V
        {
            public static bool IsSupported => AdvSimd.Arm64.IsSupported || Sse2.IsSupported;
            private static bool Neon => AdvSimd.Arm64.IsSupported;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Load(float* p) => Neon ? AdvSimd.LoadVector128(p) : Sse.LoadVector128(p);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Store(float* p, Vector128<float> v)
            {
                if (Neon)
                    AdvSimd.Store(p, v);
                else
                    Sse.Store(p, v);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Create(float value) => Neon ? AdvSimd.DuplicateToVector128(value) : Vector128.Create(value);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Add(Vector128<float> x, Vector128<float> y) => Neon ? AdvSimd.Add(x, y) : Sse.Add(x, y);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Subtract(Vector128<float> x, Vector128<float> y) => Neon ? AdvSimd.Subtract(x, y) : Sse.Subtract(x, y);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Multiply(Vector128<float> x, Vector128<float> y) => Neon ? AdvSimd.Multiply(x, y) : Sse.Multiply(x, y);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Divide(Vector128<float> x, Vector128<float> y) => Neon ? AdvSimd.Arm64.Divide(x, y) : Sse.Divide(x, y);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Max(Vector128<float> x, Vector128<float> y) => Neon ? AdvSimd.Max(x, y) : Sse.Max(x, y);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Min(Vector128<float> x, Vector128<float> y) => Neon ? AdvSimd.Min(x, y) : Sse.Min(x, y);

            // a * b + c: fused (one rounding) on NEON, two roundings on SSE.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> MultiplyAdd(Vector128<float> a, Vector128<float> b, Vector128<float> c) =>
                Neon ? AdvSimd.FusedMultiplyAdd(c, a, b) : Sse.Add(Sse.Multiply(a, b), c);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Abs(Vector128<float> x) => Neon ? AdvSimd.Abs(x) : Sse.AndNot(Create(-0f), x);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> LessThan(Vector128<float> x, Vector128<float> y) =>
                Neon ? AdvSimd.CompareLessThan(x, y) : Sse.CompareLessThan(x, y);

            // The bits of a where mask has 1s, of b elsewhere.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Select(Vector128<float> mask, Vector128<float> a, Vector128<float> b) =>
                Neon ? AdvSimd.BitwiseSelect(mask, a, b) : Sse.Or(Sse.And(mask, a), Sse.AndNot(mask, b));

            public static float Sum(Vector128<float> v)
            {
                if (Neon)
                {
                    v = AdvSimd.Arm64.AddPairwise(v, v);
                    v = AdvSimd.Arm64.AddPairwise(v, v); // every lane now holds the total
                    return *(float*)&v;
                }
                float* lanes = (float*)&v;
                return (lanes[0] + lanes[2]) + (lanes[1] + lanes[3]);
            }

            // The largest lane, for values without NaN.
            public static float MaxAcross(Vector128<float> v)
            {
                if (Neon)
                    return AdvSimd.Arm64.MaxAcross(v).ToScalar();
                float* lanes = (float*)&v;
                float a = lanes[0] > lanes[1] ? lanes[0] : lanes[1];
                float b = lanes[2] > lanes[3] ? lanes[2] : lanes[3];
                return a > b ? a : b;
            }

            // e^x for x in [ExpLo, ExpHi].
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> Exp(Vector128<float> x)
            {
                Vector128<float> scaled = Multiply(x, Create(Log2E));
                Vector128<float> fx = Neon
                    ? AdvSimd.RoundToNearest(scaled)
                    : Sse2.ConvertToVector128Single(Sse2.ConvertToVector128Int32(scaled));
                Vector128<float> r = MultiplyAdd(fx, Create(NegLn2Hi), x);
                r = MultiplyAdd(fx, Create(NegLn2Lo), r);
                Vector128<float> p = Create(P0);
                p = MultiplyAdd(p, r, Create(P1));
                p = MultiplyAdd(p, r, Create(P2));
                p = MultiplyAdd(p, r, Create(P3));
                p = MultiplyAdd(p, r, Create(P4));
                p = MultiplyAdd(p, r, Create(P5));
                Vector128<float> y = MultiplyAdd(p, Multiply(r, r), Add(r, Create(1f)));
                // 2^n: fx is a whole number, so fx + 127 is the exponent field.
                Vector128<float> biased = Add(fx, Create(127f));
                Vector128<float> pow2 = Neon
                    ? AdvSimd.ShiftLeftLogical(AdvSimd.ConvertToInt32RoundToEven(biased).AsUInt32(), 23).AsSingle()
                    : Sse2.ShiftLeftLogical(Sse2.ConvertToVector128Int32(biased), 23).AsSingle();
                return Multiply(y, pow2);
            }
        }
    }
}
