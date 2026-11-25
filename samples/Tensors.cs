// Test: TensorPrimitives (System.Numerics.Tensors), operations on whole
// spans of floats. Element-wise arithmetic, sums and the fast functions use
// NEON on arm64, and on x64 SSE2, or AVX when built with -m x86-64-v3:
// build.sh builds this file twice there, as Tensors (SSE2) and TensorsAvx.
// The other targets run the scalar loops. Lengths like 37 cover both a
// vector loop and its scalar tail. scripts/tensor-fuzz compares the same
// code with .NET's.
// Exit code = number of failed checks.
// Build: ./build.sh Tensors

using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;

namespace TensorsSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void End(string section)
    {
        Console.Write(s_sectionFailures == 0 ? "PASS " : "FAIL ");
        Console.Write(section);
        Console.Write(" (");
        Console.Write(s_sectionChecks);
        Console.WriteLine(" checks)");
        s_failures += s_sectionFailures;
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_sectionFailures++;
            Console.Write("  failed: ");
            Console.WriteLine(name);
        }
    }

    // Hide a value from the optimizer. (Always false: see SmallIntegers.cs.)
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static float F(float value) => s_never ? 0 : value;

    static bool Near(float actual, float expected, float tolerance = 1e-6f) =>
        MathF.Abs(actual - expected) <= tolerance * MathF.Max(1, MathF.Abs(expected));

    // Bit for bit: -0 differs from 0, and NaN equals NaN.
    static bool Same(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b) || (a != a && b != b);

    // n values, not all simple: i * 0.37 - 5, with a NaN-free spread of signs.
    static float[] Values(int n, float scale)
    {
        var values = new float[n];
        for (int i = 0; i < n; i++)
            values[i] = F((i * 0.37f - 5) * scale);
        return values;
    }

    public static int Main()
    {
        s_never = false;
        Elementwise();
        Reductions();
        Functions();
        FastFunctions();

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
        {
            Console.Write(s_failures);
            Console.WriteLine(" checks FAILED.");
        }
        return s_failures;
    }

    static void Elementwise()
    {
        const int N = 37;
        float[] x = Values(N, 1), y = Values(N, -0.5f), z = Values(N, 2);
        var d = new float[N];

        // Each element is one IEEE operation, vectors or not: the same bits
        // as the plain loop.
        bool add = true, sub = true, mul = true, div = true, scalar = true;
        TensorPrimitives.Add(x, y, d);
        for (int i = 0; i < N; i++) add &= Same(d[i], x[i] + y[i]);
        TensorPrimitives.Subtract(x, y, d);
        for (int i = 0; i < N; i++) sub &= Same(d[i], x[i] - y[i]);
        TensorPrimitives.Multiply(x, y, d);
        for (int i = 0; i < N; i++) mul &= Same(d[i], x[i] * y[i]);
        TensorPrimitives.Divide(x, y, d);
        for (int i = 0; i < N; i++) div &= Same(d[i], x[i] / y[i]);
        TensorPrimitives.Multiply(x, F(3), d);
        for (int i = 0; i < N; i++) scalar &= Same(d[i], x[i] * 3);
        TensorPrimitives.Subtract(F(1), y, d);
        for (int i = 0; i < N; i++) scalar &= Same(d[i], 1 - y[i]);
        Check("Add", add);
        Check("Subtract", sub);
        Check("Multiply", mul);
        Check("Divide", div);
        Check("with a scalar", scalar);

        bool fma = true, am = true;
        TensorPrimitives.MultiplyAdd(x, y, z, d);
        for (int i = 0; i < N; i++) fma &= Same(d[i], x[i] * y[i] + z[i]);
        TensorPrimitives.AddMultiply(x, F(2), z, d);
        for (int i = 0; i < N; i++) am &= Same(d[i], (x[i] + 2) * z[i]);
        Check("MultiplyAdd, rounded twice", fma);
        Check("AddMultiply", am);

        // docs:inplace
        // The destination may be an input: this scales x in place.
        float[] v = { 1, 2, 3, 4, 5 };
        TensorPrimitives.Multiply(v, 10, v);
        Check("in place", v[0] == 10 && v[4] == 50);
        // docs:end

        float[] neg = { F(0), 1, -2 };
        TensorPrimitives.Negate(neg, neg);
        Check("Negate, -0 included", Same(neg[0], -0f) && neg[1] == -1 && neg[2] == 2);

        float[] a = { F(1), -0f, 0, float.NaN }, b = { 2, 0, -0f, 1 };
        var m = new float[4];
        TensorPrimitives.Max(a, b, m);
        Check("Max: NaN wins, +0 over -0", m[0] == 2 && Same(m[1], 0) && Same(m[2], 0) && float.IsNaN(m[3]));
        TensorPrimitives.MinMagnitude(new float[] { F(-3), 2 }, new float[] { 3, -1 }, m);
        Check("MinMagnitude: the negative on a tie", m[0] == -3 && m[1] == -1);

        Half[] halves = new Half[3];
        float[] back = new float[3];
        TensorPrimitives.ConvertToHalf(new float[] { F(1.5f), 65504, 0.1f }, halves);
        TensorPrimitives.ConvertToSingle(halves, back);
        Check("ConvertToHalf, ConvertToSingle", back[0] == 1.5f && back[1] == 65504 && back[2] == 0.099975586f);
        End("element-wise");
    }

    static void Reductions()
    {
        var oneToHundred = new float[100];
        for (int i = 0; i < 100; i++)
            oneToHundred[i] = F(i + 1);
        Check("Sum", TensorPrimitives.Sum(oneToHundred) == 5050 && TensorPrimitives.Sum(ReadOnlySpan<float>.Empty) == 0);
        Check("SumOfSquares", TensorPrimitives.SumOfSquares(oneToHundred) == 338350);
        Check("SumOfMagnitudes", TensorPrimitives.SumOfMagnitudes(new float[] { F(-1), 2, -3 }) == 6);

        // docs:similarity
        // How alike two embeddings are: 1 for the same direction, 0 for
        // unrelated, -1 for opposite.
        float[] cat = { 0.9f, 0.1f, 0.4f }, kitten = { 0.8f, 0.2f, 0.5f };
        float[] car = { -0.1f, 0.9f, -0.3f };
        float close = TensorPrimitives.CosineSimilarity(cat, kitten);
        float far = TensorPrimitives.CosineSimilarity(cat, car);
        Check("CosineSimilarity", close > 0.98f && far < 0);
        // docs:end

        float[] three = { F(3), 4 }, zero = { 0, 0 };
        Check("Dot, Norm, Distance", TensorPrimitives.Dot(three, three) == 25 && TensorPrimitives.Norm(three) == 5
            && TensorPrimitives.Distance(three, zero) == 5);
        Check("Product, ProductOfSums, ProductOfDifferences", TensorPrimitives.Product(three) == 12
            && TensorPrimitives.ProductOfSums(three, three) == 48 && TensorPrimitives.ProductOfDifferences(three, zero) == 12);

        float[] values = { F(3), -7, 7, 0, -0f };
        Check("Max, Min, MaxMagnitude, MinMagnitude", TensorPrimitives.Max(values) == 7 && TensorPrimitives.Min(values) == -7
            && TensorPrimitives.MaxMagnitude(values) == 7 && Same(TensorPrimitives.MinMagnitude(values), -0f));
        Check("IndexOfMax, IndexOfMin: the first", TensorPrimitives.IndexOfMax(values) == 2
            && TensorPrimitives.IndexOfMin(values) == 1 && TensorPrimitives.IndexOfMaxMagnitude(values) == 2
            && TensorPrimitives.IndexOfMinMagnitude(values) == 4 && TensorPrimitives.IndexOfMax(ReadOnlySpan<float>.Empty) == -1);
        float[] withNaN = { F(1), float.NaN, 5 };
        Check("NaN wins", float.IsNaN(TensorPrimitives.Max(withNaN)) && TensorPrimitives.IndexOfMax(withNaN) == 1);
        End("reductions");
    }

    // Units in the last place between two floats of the same sign.
    static int Ulps(float a, float b)
    {
        int d = BitConverter.SingleToInt32Bits(a) - BitConverter.SingleToInt32Bits(b);
        return d < 0 ? -d : d;
    }

    // The vector forms (NEON, AVX) against the scalar function, on 37
    // values that fill whole vectors and a tail: tiny ones, where Tanh must
    // stay exact, ordinary ones, and a NaN, which sends its block to the
    // scalar function.
    static void FastFunctions()
    {
        const int N = 37;
        var x = new float[N];
        for (int i = 0; i < N; i++)
            x[i] = F(i < 8 ? (i - 4) * 1e-20f : (i - 22) * 0.61f);
        x[30] = F(float.NaN);
        var d = new float[N];
        bool exp = true, sigmoid = true, tanh = true;
        TensorPrimitives.Exp(x, d);
        for (int i = 0; i < N; i++) exp &= i == 30 ? float.IsNaN(d[i]) : Ulps(d[i], MathF.Exp(x[i])) <= 4;
        TensorPrimitives.Sigmoid(x, d);
        for (int i = 0; i < N; i++)
            sigmoid &= i == 30 ? float.IsNaN(d[i]) : Ulps(d[i], (float)(1 / (1 + Math.Exp(-x[i])))) <= 4;
        TensorPrimitives.Tanh(x, d);
        for (int i = 0; i < N; i++) tanh &= i == 30 ? float.IsNaN(d[i]) : Ulps(d[i], MathF.Tanh(x[i])) <= 4;
        Check("Exp, vectors", exp);
        Check("Sigmoid, vectors", sigmoid);
        Check("Tanh, vectors, exact near 0", tanh && d[0] == -4e-20f && Same(d[4], 0));

        // SoftMax against the same computed in double.
        var p = new float[N];
        float[] logits = Values(N, 3);
        TensorPrimitives.SoftMax(logits, p);
        double max = double.MinValue, total = 0;
        foreach (float v in logits) max = Math.Max(max, v);
        foreach (float v in logits) total += Math.Exp(v - max);
        bool softmax = true;
        for (int i = 0; i < N; i++)
            softmax &= Ulps(p[i], (float)(Math.Exp(logits[i] - max) / total)) <= 6;
        Check("SoftMax, vectors", softmax);
        End("fast functions");
    }

    static void Functions()
    {
        // docs:softmax
        // Scores to probabilities. Large scores are fine: the largest is
        // subtracted first, so e^x doesn't overflow.
        float[] scores = { 1000, 1001, 1002 };
        var p = new float[3];
        TensorPrimitives.SoftMax(scores, p);
        Check("SoftMax", Near(p[0], 0.09003057f) && Near(p[2], 0.66524096f)
            && Near(TensorPrimitives.Sum(p), 1));
        // docs:end

        var d = new float[3];
        TensorPrimitives.Sigmoid(new float[] { F(0), 2, -100 }, d);
        // Sigmoid(-100) is 3.72e-44, a subnormal 27 times the smallest float.
        int tiny = BitConverter.SingleToInt32Bits(d[2]);
        Check("Sigmoid", d[0] == 0.5f && Near(d[1], 0.880797f) && tiny >= 26 && tiny <= 28);
        TensorPrimitives.Exp(new float[] { F(0), 1, -1 }, d);
        Check("Exp", d[0] == 1 && Near(d[1], MathF.E) && Near(d[2], 0.36787944f));
        // Exact at 0, where .NET's vectorized versions are off by 6e-8.
        TensorPrimitives.Sinh(new float[] { F(-0f), 1, 1e-30f }, d);
        Check("Sinh", Same(d[0], -0f) && Near(d[1], 1.1752012f) && d[2] == 1e-30f);
        TensorPrimitives.Tanh(new float[] { F(0.5f), -1e-30f, 100 }, d);
        Check("Tanh", Near(d[0], 0.46211716f) && d[1] == -1e-30f && d[2] == 1);
        TensorPrimitives.Round(new float[] { F(0.5f), 1.5f, -2.5f }, d);
        Check("Round: halves to even", d[0] == 0 && d[1] == 2 && d[2] == -2);
        TensorPrimitives.Pow(new float[] { F(2), 9, -8 }, new float[] { 10, 0.5f, 1f / 3 }, d);
        Check("Pow", d[0] == 1024 && Near(d[1], 3) && float.IsNaN(d[2]));
        TensorPrimitives.Hypot(new float[] { F(3), 1e30f, 0 }, new float[] { 4, 1e30f, -2 }, d);
        Check("Hypot", d[0] == 5 && Near(d[1], 1.4142135e30f) && d[2] == 2);
        TensorPrimitives.Atan2(new float[] { F(1), 0, -1 }, new float[] { 1, -1, 0 }, d);
        Check("Atan2", Near(d[0], MathF.PI / 4) && Near(d[1], MathF.PI) && Near(d[2], -MathF.PI / 2));
        End("functions");
    }
}
