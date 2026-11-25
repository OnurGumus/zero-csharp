// Runs zerocore's Tensor<T>, TensorSpan<T> and Tensor class (namespace
// ZeroTest) and .NET's (the System.Numerics.Tensors package) on the same
// random tensors and compares what comes out: lengths, strides, density and
// every element (in units in the last place). The tensors are dense, sliced
// (strided), permuted, or both, of rank 1 to 4 with lengths 0 to 4, so the
// operations' dense, sliced and element-by-element paths and broadcasting
// all run. Argument errors must agree: both throw the same exception type
// with the same message, or neither throws.
//
// The math methods are found by reflection: every public float method of
// zerocore's Tensor class with one of the shapes below is run against .NET's
// generic method of the same name. Exit code = number of checks over their
// limit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using N = System.Numerics.Tensors;
using Z = ZeroTest;

delegate N.Tensor<float> NU(in N.ReadOnlyTensorSpan<float> x);
delegate Z.Tensor<float> ZU(in Z.ReadOnlyTensorSpan<float> x);
delegate N.Tensor<float> NB(in N.ReadOnlyTensorSpan<float> x, in N.ReadOnlyTensorSpan<float> y);
delegate Z.Tensor<float> ZB(in Z.ReadOnlyTensorSpan<float> x, in Z.ReadOnlyTensorSpan<float> y);
delegate N.Tensor<float> NBS(in N.ReadOnlyTensorSpan<float> x, float y);
delegate Z.Tensor<float> ZBS(in Z.ReadOnlyTensorSpan<float> x, float y);
delegate N.Tensor<float> NSB(float x, in N.ReadOnlyTensorSpan<float> y);
delegate Z.Tensor<float> ZSB(float x, in Z.ReadOnlyTensorSpan<float> y);
delegate float NR(in N.ReadOnlyTensorSpan<float> x);
delegate float ZR(in Z.ReadOnlyTensorSpan<float> x);
delegate float NR2(in N.ReadOnlyTensorSpan<float> x, in N.ReadOnlyTensorSpan<float> y);
delegate float ZR2(in Z.ReadOnlyTensorSpan<float> x, in Z.ReadOnlyTensorSpan<float> y);
delegate nint NI(in N.ReadOnlyTensorSpan<float> x);
delegate nint ZI(in Z.ReadOnlyTensorSpan<float> x);
delegate N.Tensor<bool> NC(in N.ReadOnlyTensorSpan<float> x, in N.ReadOnlyTensorSpan<float> y);
delegate Z.Tensor<bool> ZC(in Z.ReadOnlyTensorSpan<float> x, in Z.ReadOnlyTensorSpan<float> y);
delegate N.Tensor<bool> NCS(in N.ReadOnlyTensorSpan<float> x, float y);
delegate Z.Tensor<bool> ZCS(in Z.ReadOnlyTensorSpan<float> x, float y);
delegate bool NCB(in N.ReadOnlyTensorSpan<float> x, in N.ReadOnlyTensorSpan<float> y);
delegate bool ZCB(in Z.ReadOnlyTensorSpan<float> x, in Z.ReadOnlyTensorSpan<float> y);
delegate bool NCBS(in N.ReadOnlyTensorSpan<float> x, float y);
delegate bool ZCBS(in Z.ReadOnlyTensorSpan<float> x, float y);

static class Program
{
    static Random s_random;
    static readonly SortedDictionary<string, (double Worst, string Input, double Limit, int Runs)> s_results = new();

    static void Record(string name, double error, string input, double limit = 0)
    {
        s_results.TryGetValue(name, out var r);
        if (r.Runs == 0)
            r = (-1, "", limit, 0);
        r.Runs++;
        if (error > r.Worst)
        {
            r.Worst = error;
            r.Input = input;
        }
        s_results[name] = r;
    }

    // ---- random tensors ------------------------------------------------------------

    static readonly float[] s_edges =
    {
        0, -0f, 1, -1, 0.5f, -0.5f, 2, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        1e-30f, -1e-30f, 1e30f, 1.4e-45f, 88.5f, -88.5f, 100, MathF.PI,
    };

    static float AnyFloat()
    {
        if (s_random.Next(20) == 0)
            return s_edges[s_random.Next(s_edges.Length)];
        switch (s_random.Next(4))
        {
            case 0: return (float)(s_random.NextDouble() * 2 - 1);
            case 1: return (float)((s_random.NextDouble() * 2 - 1) * 10);
            case 2: return s_random.Next(-4, 5);
            default: return (float)((s_random.NextDouble() * 2 - 1) * 100);
        }
    }

    // How a tensor was made, so the same one can be made on both sides.
    sealed class Spec
    {
        public float[] Data;
        public nint[] Lengths;
        public (int Start, int End)[] Slice; // null: not sliced
        public int[] Permutation;            // null: not permuted

        public override string ToString()
        {
            string s = $"lengths [{string.Join(", ", Lengths)}]";
            if (Slice != null)
                s += " slice [" + string.Join(", ", Slice.Select(r => $"{r.Start}..{r.End}")) + "]";
            if (Permutation != null)
                s += " permute [" + string.Join(", ", Permutation) + "]";
            return s;
        }
    }

    static Spec AnySpec(nint[] lengths = null, bool views = true)
    {
        if (lengths == null)
        {
            int rank = s_random.Next(1, 5);
            lengths = new nint[rank];
            for (int i = 0; i < rank; i++)
                lengths[i] = s_random.Next(10) == 0 ? 0 : s_random.Next(1, 5);
        }
        var spec = new Spec { Lengths = lengths };
        // A slice is taken from a larger tensor, so the result has these lengths.
        nint[] full = (nint[])lengths.Clone();
        if (views && s_random.Next(3) == 0)
        {
            spec.Slice = new (int, int)[lengths.Length];
            for (int i = 0; i < lengths.Length; i++)
            {
                int extra = s_random.Next(3);
                int start = s_random.Next(extra + 1);
                full[i] = lengths[i] + extra;
                spec.Slice[i] = (start, start + (int)lengths[i]);
            }
        }
        if (views && lengths.Length > 1 && s_random.Next(3) == 0)
        {
            spec.Permutation = Enumerable.Range(0, lengths.Length).OrderBy(_ => s_random.Next()).ToArray();
            // Permuting reorders the lengths: make the base so the result has `lengths`.
            nint[] before = new nint[full.Length];
            var slice = spec.Slice;
            (int, int)[] sliceBefore = slice == null ? null : new (int, int)[slice.Length];
            for (int i = 0; i < full.Length; i++)
            {
                before[spec.Permutation[i]] = full[i];
                if (slice != null)
                    sliceBefore[spec.Permutation[i]] = slice[i];
            }
            full = before;
            spec.Slice = sliceBefore;
            spec.Lengths = spec.Slice == null ? (nint[])full.Clone() : spec.Slice.Select(r => (nint)(r.Item2 - r.Item1)).ToArray();
        }
        long count = 1;
        foreach (nint l in full)
            count *= l;
        spec.Data = new float[count];
        for (int i = 0; i < count; i++)
            spec.Data[i] = AnyFloat();
        spec.Lengths = full;
        return spec;
    }

    static Z.Tensor<float> MakeZ(Spec s)
    {
        Z.Tensor<float> t = Z.Tensor.Create((float[])s.Data.Clone(), s.Lengths);
        if (s.Slice != null)
            t = t.Slice(s.Slice.Select(r => new Z.NRange(r.Start, r.End)).ToArray());
        if (s.Permutation != null)
            t = Z.Tensor.PermuteDimensions(t, s.Permutation);
        return t;
    }

    static N.Tensor<float> MakeN(Spec s)
    {
        N.Tensor<float> t = N.Tensor.Create((float[])s.Data.Clone(), s.Lengths);
        if (s.Slice != null)
            t = t.Slice(s.Slice.Select(r => new System.Buffers.NRange(r.Start, r.End)).ToArray());
        if (s.Permutation != null)
            t = N.Tensor.PermuteDimensions(t, s.Permutation);
        return t;
    }

    // Lengths y can be broadcast with: a suffix of x's, some of them 1;
    // sometimes x's own, sometimes (to test the errors) something else.
    static nint[] Compatible(nint[] x)
    {
        int r = s_random.Next(5);
        if (r == 0)
            return (nint[])x.Clone();
        if (r == 1)
        {
            int rank = s_random.Next(1, 5);
            var bad = new nint[rank];
            for (int i = 0; i < rank; i++)
                bad[i] = s_random.Next(1, 5);
            return bad;
        }
        int keep = s_random.Next(1, x.Length + 1);
        var y = x.Skip(x.Length - keep).ToArray();
        for (int i = 0; i < y.Length; i++)
            if (s_random.Next(3) == 0)
                y[i] = 1;
        if (s_random.Next(4) == 0)
            y = new nint[] { 1 }.Concat(y).ToArray(); // a higher rank than x
        return y;
    }

    // ---- comparing -----------------------------------------------------------------

    static double Ulps(float actual, float expected)
    {
        if (float.IsNaN(actual) || float.IsNaN(expected))
            return float.IsNaN(actual) && float.IsNaN(expected) ? 0 : double.PositiveInfinity;
        if (actual == expected)
            return BitConverter.SingleToInt32Bits(actual) == BitConverter.SingleToInt32Bits(expected) ? 0 : 1; // ±0
        long a = BitConverter.SingleToInt32Bits(actual), e = BitConverter.SingleToInt32Bits(expected);
        a = a < 0 ? int.MinValue - a : a;
        e = e < 0 ? int.MinValue - e : e;
        return Math.Abs(a - e);
    }

    static string Shape(ReadOnlySpan<nint> lengths, ReadOnlySpan<nint> strides) =>
        $"[{string.Join(", ", lengths.ToArray())}] strides [{string.Join(", ", strides.ToArray())}]";

    // Returns the error: infinite for a different shape, else the worst ulps.
    static double Compare(Z.Tensor<float> z, N.Tensor<float> n, out string detail)
    {
        detail = "";
        string zs = Shape(z.Lengths, z.Strides), ns = Shape(n.Lengths, n.Strides);
        if (zs != ns || z.IsDense != n.IsDense)
        {
            detail = $"shape: ours {zs} dense={z.IsDense}, .NET's {ns} dense={n.IsDense}";
            return double.PositiveInfinity;
        }
        float[] zf = new float[z.FlattenedLength], nf = new float[n.FlattenedLength];
        z.FlattenTo(zf);
        n.FlattenTo(nf);
        double worst = 0;
        for (int i = 0; i < zf.Length; i++)
        {
            double u = Ulps(zf[i], nf[i]);
            if (u > worst)
            {
                worst = u;
                detail = $"[{i}] ours {zf[i]:R}, .NET's {nf[i]:R}";
            }
        }
        return worst;
    }

    static double CompareBool(Z.Tensor<bool> z, N.Tensor<bool> n, out string detail)
    {
        detail = "";
        string zs = Shape(z.Lengths, z.Strides), ns = Shape(n.Lengths, n.Strides);
        if (zs != ns)
        {
            detail = $"shape: ours {zs}, .NET's {ns}";
            return double.PositiveInfinity;
        }
        bool[] zf = new bool[z.FlattenedLength], nf = new bool[n.FlattenedLength];
        z.FlattenTo(zf);
        n.FlattenTo(nf);
        for (int i = 0; i < zf.Length; i++)
            if (zf[i] != nf[i])
            {
                detail = $"[{i}] ours {zf[i]}, .NET's {nf[i]}";
                return 1;
            }
        return 0;
    }

    // Runs both; if either throws, both must throw the same type and message.
    static void Run<TZ, TN>(string name, string input, Func<TZ> zero, Func<TN> net, Func<TZ, TN, (double, string)> compare, double limit = 0)
    {
        TZ z = default;
        TN n = default;
        Exception ze = null, ne = null;
        try { z = zero(); } catch (Exception e) { ze = e; }
        try { n = net(); } catch (Exception e) { ne = e; }
        if (ze != null || ne != null)
        {
            bool same = ze != null && ne != null && ze.GetType() == ne.GetType() && ze.Message == ne.Message;
            Record(name + " (errors)", same ? 0 : 1,
                $"{input}: ours {(ze == null ? "no exception" : ze.GetType().Name + ": " + ze.Message)}; .NET's {(ne == null ? "no exception" : ne.GetType().Name + ": " + ne.Message)}");
            return;
        }
        var (error, detail) = compare(z, n);
        Record(name, error, input + ": " + detail, limit);
    }

    static (double, string) Tensors(Z.Tensor<float> z, N.Tensor<float> n) => (Compare(z, n, out string d), d);
    static (double, string) Bools(Z.Tensor<bool> z, N.Tensor<bool> n) => (CompareBool(z, n, out string d), d);
    static (double, string) Floats(float z, float n) => (Ulps(z, n), $"ours {z:R}, .NET's {n:R}");
    static (double, string) Same<T>(T z, T n) => (EqualityComparer<T>.Default.Equals(z, n) ? 0 : 1, $"ours {z}, .NET's {n}");

    // ---- the methods, found by reflection ----------------------------------------

    static bool IsIn(ParameterInfo p, Type t) => p.ParameterType.IsByRef && p.ParameterType.GetElementType() == t;

    static IEnumerable<(string Name, TZ Zero, TN Net)> Find<TZ, TN>(Func<ParameterInfo[], bool> zShape, Type zReturn, Func<ParameterInfo[], bool> nShape, Type nReturn)
        where TZ : Delegate where TN : Delegate
    {
        foreach (MethodInfo zm in typeof(Z.Tensor).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (zm.IsGenericMethodDefinition || zm.ReturnType != zReturn || !zShape(zm.GetParameters()))
                continue;
            MethodInfo nm = typeof(N.Tensor).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == zm.Name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1)
                .Select(m => { try { return m.MakeGenericMethod(typeof(float)); } catch (ArgumentException) { return null; } })
                .FirstOrDefault(m => m != null && m.ReturnType == nReturn && nShape(m.GetParameters()));
            if (nm == null)
            {
                Record("missing in .NET: " + zm.Name, 1, "");
                continue;
            }
            yield return (zm.Name, zm.CreateDelegate<TZ>(), nm.CreateDelegate<TN>());
        }
    }

    static readonly Type ZRo = typeof(Z.ReadOnlyTensorSpan<float>), NRo = typeof(N.ReadOnlyTensorSpan<float>);

    static bool One(ParameterInfo[] p, Type ro) => p.Length == 1 && IsIn(p[0], ro);
    static bool Two(ParameterInfo[] p, Type ro) => p.Length == 2 && IsIn(p[0], ro) && IsIn(p[1], ro);
    static bool WithScalar(ParameterInfo[] p, Type ro) => p.Length == 2 && IsIn(p[0], ro) && p[1].ParameterType == typeof(float);
    static bool ScalarFirst(ParameterInfo[] p, Type ro) => p.Length == 2 && p[0].ParameterType == typeof(float) && IsIn(p[1], ro);

    // Functions whose float results may differ from .NET's in the last bits:
    // TensorPrimitives' vector forms (zerocore's and .NET's are different
    // code) and sums, which add in a different order.
    static double LimitFor(string name) => name switch
    {
        "Exp" or "Sigmoid" or "Tanh" or "SoftMax" => 8,
        "Sum" or "Average" or "Dot" or "Norm" or "StdDev" or "Distance" or "CosineSimilarity" or "Product" or "SumOfSquares" => 64,
        _ => 0,
    };

    // The float functions, against the function in double. zerocore's come
    // from MathF and float's own formulas (AcosPi is Acos(x) / pi, ...), .NET's
    // TensorPrimitives from its vector code, so the last bits differ. An
    // element passes when the two are within 4 ulps of each other, or ours is
    // at least as close to the function in double as .NET's.
    static double ExpM1(double x) => Math.Abs(x) < 1e-5 ? x + x * x / 2 : Math.Exp(x) - 1;
    static double LogP1(double x) => Math.Abs(x) < 1e-5 ? x - x * x / 2 : Math.Log(1 + x);
    static readonly Dictionary<string, Func<double, double>> s_reference = new()
    {
        ["Sin"] = Math.Sin, ["Cos"] = Math.Cos, ["Tan"] = Math.Tan,
        ["SinPi"] = x => Math.Sin(Math.PI * x), ["CosPi"] = x => Math.Cos(Math.PI * x), ["TanPi"] = x => Math.Tan(Math.PI * x),
        ["Asin"] = Math.Asin, ["Acos"] = Math.Acos, ["Atan"] = Math.Atan,
        ["AsinPi"] = x => Math.Asin(x) / Math.PI, ["AcosPi"] = x => Math.Acos(x) / Math.PI, ["AtanPi"] = x => Math.Atan(x) / Math.PI,
        ["Sinh"] = Math.Sinh, ["Cosh"] = Math.Cosh, ["Tanh"] = Math.Tanh,
        ["Asinh"] = Math.Asinh, ["Acosh"] = Math.Acosh, ["Atanh"] = Math.Atanh,
        ["Exp"] = Math.Exp, ["Exp2"] = x => Math.Pow(2, x), ["Exp10"] = x => Math.Pow(10, x),
        ["ExpM1"] = ExpM1, ["Exp2M1"] = x => ExpM1(x * Math.Log(2)), ["Exp10M1"] = x => ExpM1(x * Math.Log(10)),
        ["Log"] = Math.Log, ["Log2"] = Math.Log2, ["Log10"] = Math.Log10,
        ["LogP1"] = LogP1, ["Log2P1"] = x => LogP1(x) / Math.Log(2), ["Log10P1"] = x => LogP1(x) / Math.Log(10),
        ["Cbrt"] = Math.Cbrt, ["Sigmoid"] = x => 1 / (1 + Math.Exp(-x)),
        ["DegreesToRadians"] = x => x * (Math.PI / 180), ["RadiansToDegrees"] = x => x * (180 / Math.PI),
    };

    static (double, string) Function(Z.Tensor<float> z, N.Tensor<float> n, float[] xs, Func<double, double> f)
    {
        double shape = Compare(z, n, out string detail);
        if (double.IsInfinity(shape) || shape == 0)
            return (shape, detail);
        float[] zf = new float[z.FlattenedLength], nf = new float[n.FlattenedLength];
        z.FlattenTo(zf);
        n.FlattenTo(nf);
        double worst = 0;
        for (int i = 0; i < zf.Length; i++)
        {
            double u = Ulps(zf[i], nf[i]);
            if (u <= 4)
                continue;
            double exact = f(xs[i]);
            if (Math.Abs(zf[i] - exact) <= Math.Abs(nf[i] - exact) || (double.IsNaN(exact) && float.IsNaN(zf[i])))
                continue;
            if (u > worst)
            {
                worst = u;
                detail = $"[{i}] x={xs[i]:R}: ours {zf[i]:R}, .NET's {nf[i]:R}, exact {exact:R}";
            }
        }
        return (worst, detail);
    }

    static int Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 2000;
        s_random = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);

        var unary = Find<ZU, NU>(p => One(p, ZRo), typeof(Z.Tensor<float>), p => One(p, NRo), typeof(N.Tensor<float>)).ToList();
        var binary = Find<ZB, NB>(p => Two(p, ZRo), typeof(Z.Tensor<float>), p => Two(p, NRo), typeof(N.Tensor<float>)).ToList();
        var withScalar = Find<ZBS, NBS>(p => WithScalar(p, ZRo), typeof(Z.Tensor<float>), p => WithScalar(p, NRo), typeof(N.Tensor<float>)).ToList();
        var scalarFirst = Find<ZSB, NSB>(p => ScalarFirst(p, ZRo), typeof(Z.Tensor<float>), p => ScalarFirst(p, NRo), typeof(N.Tensor<float>)).ToList();
        var reduce = Find<ZR, NR>(p => One(p, ZRo), typeof(float), p => One(p, NRo), typeof(float)).ToList();
        var reduce2 = Find<ZR2, NR2>(p => Two(p, ZRo), typeof(float), p => Two(p, NRo), typeof(float)).ToList();
        var index = Find<ZI, NI>(p => One(p, ZRo), typeof(nint), p => One(p, NRo), typeof(nint)).ToList();
        var compare = Find<ZC, NC>(p => Two(p, ZRo), typeof(Z.Tensor<bool>), p => Two(p, NRo), typeof(N.Tensor<bool>)).ToList();
        var compareScalar = Find<ZCS, NCS>(p => WithScalar(p, ZRo), typeof(Z.Tensor<bool>), p => WithScalar(p, NRo), typeof(N.Tensor<bool>)).ToList();
        var all = Find<ZCB, NCB>(p => Two(p, ZRo), typeof(bool), p => Two(p, NRo), typeof(bool)).ToList();
        var allScalar = Find<ZCBS, NCBS>(p => WithScalar(p, ZRo), typeof(bool), p => WithScalar(p, NRo), typeof(bool)).ToList();
        Console.WriteLine($"Methods: {unary.Count} unary, {binary.Count} binary, {withScalar.Count} + {scalarFirst.Count} with a scalar, " +
            $"{reduce.Count + reduce2.Count + index.Count} reductions, {compare.Count + compareScalar.Count + all.Count + allScalar.Count} comparisons");

        for (int it = 0; it < iterations; it++)
        {
            Spec sx = AnySpec();
            Spec sy = AnySpec(Compatible(sx.Lengths.Length == 0 ? new nint[] { 1 } : MakeN(sx).Lengths.ToArray()));
            Z.Tensor<float> zx = MakeZ(sx), zy = MakeZ(sy);
            N.Tensor<float> nx = MakeN(sx), ny = MakeN(sy);
            float s = AnyFloat();
            string ix = "x " + sx, ixy = ix + ", y " + sy;

            float[] xs = new float[zx.FlattenedLength];
            zx.FlattenTo(xs);
            foreach (var (name, z, n) in unary)
            {
                if (s_reference.TryGetValue(name, out var f))
                    Run(name, ix, () => z(zx), () => n(nx), (a, b) => Function(a, b, xs, f));
                else
                    Run(name, ix, () => z(zx), () => n(nx), Tensors, LimitFor(name));
            }
            foreach (var (name, z, n) in binary)
                Run(name, ixy, () => z(zx, zy), () => n(nx, ny), Tensors, LimitFor(name));
            foreach (var (name, z, n) in withScalar)
                Run(name + "(x, s)", ix + $", s {s:R}", () => z(zx, s), () => n(nx, s), Tensors, LimitFor(name));
            foreach (var (name, z, n) in scalarFirst)
                Run(name + "(s, x)", ix + $", s {s:R}", () => z(s, zx), () => n(s, nx), Tensors, LimitFor(name));
            foreach (var (name, z, n) in reduce)
                Run(name, ix, () => z(zx), () => n(nx), Floats, LimitFor(name));
            foreach (var (name, z, n) in reduce2)
            {
                // Same lengths: Dot and Distance don't broadcast.
                Spec sz = AnySpec(MakeN(sx).Lengths.ToArray());
                Z.Tensor<float> zz = MakeZ(sz);
                N.Tensor<float> nz = MakeN(sz);
                Run(name, ix + ", y " + sz, () => z(zx, zz), () => n(nx, nz), Floats, LimitFor(name));
            }
            foreach (var (name, z, n) in index)
                Run(name, ix, () => z(zx), () => n(nx), Same);
            foreach (var (name, z, n) in compare)
                Run(name, ixy, () => z(zx, zy), () => n(nx, ny), Bools);
            foreach (var (name, z, n) in compareScalar)
                Run(name + "(x, s)", ix + $", s {s:R}", () => z(zx, s), () => n(nx, s), Bools);
            foreach (var (name, z, n) in all)
                Run(name, ixy, () => z(zx, zy), () => n(nx, ny), Same);
            foreach (var (name, z, n) in allScalar)
                Run(name + "(x, s)", ix + $", s {s:R}", () => z(zx, s), () => n(nx, s), Same);

            ShapeOperations(sx, sy, zx, nx, zy, ny);
        }

        int failed = 0;
        foreach (var (name, r) in s_results)
        {
            bool over = r.Worst > r.Limit;
            if (over)
                failed++;
            if (over || r.Worst > 0)
                Console.WriteLine($"{(over ? "FAIL" : "ok  ")} {name}: worst {r.Worst} (limit {r.Limit}, {r.Runs} runs) {r.Input}");
        }
        int errors = s_results.Count(r => r.Key.EndsWith(" (errors)"));
        Console.WriteLine($"{s_results.Count} checks ({errors} of them argument errors), {failed} over their limit.");
        return failed;
    }

    // ---- shape operations ------------------------------------------------------------

    static void ShapeOperations(Spec sx, Spec sy, Z.Tensor<float> zx, N.Tensor<float> nx, Z.Tensor<float> zy, N.Tensor<float> ny)
    {
        string ix = "x " + sx;
        int rank = zx.Rank;

        Run("ToString", ix, () => zx.ToString(Enumerable.Repeat((nint)3, rank).ToArray()), () => nx.ToString(Enumerable.Repeat((nint)3, rank).ToArray()), Same);
        Run("Transpose", ix, () => Z.Tensor.Transpose(zx), () => N.Tensor.Transpose(nx), Tensors);
        int[] perm = Enumerable.Range(0, rank).OrderBy(_ => s_random.Next()).ToArray();
        Run("PermuteDimensions", ix, () => Z.Tensor.PermuteDimensions(zx, perm), () => N.Tensor.PermuteDimensions(nx, perm), Tensors);
        Run("Reverse", ix, () => Z.Tensor.Reverse<float>(zx), () => N.Tensor.Reverse<float>(nx), Tensors);
        int dim = s_random.Next(rank);
        Run("ReverseDimension", ix, () => Z.Tensor.ReverseDimension<float>(zx, dim), () => N.Tensor.ReverseDimension<float>(nx, dim), Tensors);
        Run("Squeeze", ix, () => Z.Tensor.Squeeze(zx), () => N.Tensor.Squeeze(nx), Tensors);
        Run("SqueezeDimension", ix, () => Z.Tensor.SqueezeDimension(zx, dim), () => N.Tensor.SqueezeDimension(nx, dim), Tensors);
        int at = s_random.Next(rank + 1);
        Run("Unsqueeze", ix, () => Z.Tensor.Unsqueeze(zx, at), () => N.Tensor.Unsqueeze(nx, at), Tensors);

        // Reshape to the same count of elements, in another order of factors.
        nint count = zx.FlattenedLength;
        nint[] shape = s_random.Next(3) == 0 ? new nint[] { count } : s_random.Next(2) == 0 ? new nint[] { -1, 1 } : new nint[] { 1, count };
        Run("Reshape", ix + $" to [{string.Join(", ", shape)}]", () => Z.Tensor.Reshape(zx, shape), () => N.Tensor.Reshape(nx, shape), Tensors);

        nint[] bigger = new nint[] { 2 }.Concat(zx.Lengths.ToArray()).ToArray();
        Run("Broadcast", ix, () => Z.Tensor.Broadcast<float>(zx, bigger), () => N.Tensor.Broadcast<float>(nx, bigger), Tensors);
        Run("Broadcast(y)", ix + ", y " + sy, () => Z.Tensor.Broadcast<float>(zx, zy), () => N.Tensor.Broadcast<float>(nx, ny), Tensors);

        nint[] resized = zx.Lengths.ToArray().Select(l => (nint)s_random.Next(0, 5)).ToArray();
        Run("Resize", ix, () => Z.Tensor.Resize(zx, resized), () => N.Tensor.Resize(nx, resized), Tensors);

        Run("Concatenate", ix + ", y " + sy, () => Z.Tensor.Concatenate<float>([zx, zy]), () => N.Tensor.Concatenate<float>([nx, ny]), Tensors);
        Run("ConcatenateOnDimension", ix + ", y " + sy, () => Z.Tensor.ConcatenateOnDimension<float>(dim, [zx, zy]), () => N.Tensor.ConcatenateOnDimension<float>(dim, [nx, ny]), Tensors);
        Run("ConcatenateOnDimension(x, x)", ix, () => Z.Tensor.ConcatenateOnDimension<float>(dim, [zx, zx]), () => N.Tensor.ConcatenateOnDimension<float>(dim, [nx, nx]), Tensors);
        Run("Stack", ix, () => Z.Tensor.Stack<float>([zx, zx]), () => N.Tensor.Stack<float>([nx, nx]), Tensors);
        Run("StackAlongDimension", ix, () => Z.Tensor.StackAlongDimension<float>(at, [zx, zx]), () => N.Tensor.StackAlongDimension<float>(at, [nx, nx]), Tensors);
        int parts = s_random.Next(1, 4);
        Run("Split", ix, () => Z.Tensor.Split<float>(zx, parts, dim), () => N.Tensor.Split<float>(nx, parts, dim),
            (z, n) =>
            {
                if (z.Length != n.Length)
                    return (double.PositiveInfinity, $"{z.Length} parts, .NET's {n.Length}");
                double worst = 0;
                string d = "";
                for (int i = 0; i < z.Length; i++)
                {
                    double e = Compare(z[i], n[i], out string di);
                    if (e > worst) { worst = e; d = $"part {i}: {di}"; }
                }
                return (worst, d);
            });

        Run("SequenceEqual", ix + ", y " + sy, () => Z.Tensor.SequenceEqual<float>(zx, zy), () => N.Tensor.SequenceEqual<float>(nx, ny), Same);
        Run("SequenceEqual(x, x)", ix, () => Z.Tensor.SequenceEqual<float>(zx, zx), () => N.Tensor.SequenceEqual<float>(nx, nx), Same);

        // Writes through views: fill a slice of a copy, then compare the copy.
        Run("Slice + Fill", ix, () =>
        {
            Z.Tensor<float> c = MakeZ(sx);
            var ranges = c.Lengths.ToArray().Select(l => new Z.NRange(0, (int)(l / 2))).ToArray();
            c.AsTensorSpan().Slice(ranges).Fill(42);
            return c;
        }, () =>
        {
            N.Tensor<float> c = MakeN(sx);
            var ranges = c.Lengths.ToArray().Select(l => new System.Buffers.NRange(0, (int)(l / 2))).ToArray();
            c.AsTensorSpan().Slice(ranges).Fill(42);
            return c;
        }, Tensors);
        Run("SetSlice", ix + ", y " + sy, () => { Z.Tensor<float> c = MakeZ(sx); Z.Tensor.SetSlice(c, zy, Z.NRange.All); return c; },
            () => { N.Tensor<float> c = MakeN(sx); N.Tensor.SetSlice(c, ny, System.Buffers.NRange.All); return c; }, Tensors);
        Run("BroadcastTo", ix + ", y " + sy, () => { Z.Tensor<float> c = MakeZ(sx); Z.Tensor.BroadcastTo(zy, c); return c; },
            () => { N.Tensor<float> c = MakeN(sx); N.Tensor.BroadcastTo(ny, c); return c; }, Tensors);
        Run("CopyTo", ix + ", y " + sy, () => { Z.Tensor<float> c = MakeZ(sy); zx.CopyTo(c); return c; },
            () => { N.Tensor<float> c = MakeN(sy); nx.CopyTo(c); return c; }, Tensors);
        Run("FilteredUpdate", ix, () =>
        {
            Z.Tensor<float> c = MakeZ(sx);
            Z.Tensor<bool> mask = Z.Tensor.GreaterThan(c, 0f);
            Z.Tensor.FilteredUpdate<float>(c, mask, -1f);
            return c;
        }, () =>
        {
            N.Tensor<float> c = MakeN(sx);
            N.Tensor<bool> mask = N.Tensor.GreaterThan<float>(c, 0f);
            N.Tensor.FilteredUpdate<float>(c, mask, -1f);
            return c;
        }, Tensors);

        // Indexing and enumeration.
        if (count > 0)
        {
            nint[] idx = zx.Lengths.ToArray().Select(l => (nint)s_random.Next((int)l)).ToArray();
            Run("indexer", ix, () => zx[idx], () => nx[idx], Floats);
            float total = 0, totalN = 0;
            foreach (float v in zx) total += v;
            foreach (float v in nx) totalN += v;
            Record("enumerator", Ulps(total, totalN), ix);
            var zd = zx.AsReadOnlyTensorSpan().GetDimensionSpan(dim);
            var nd = nx.AsReadOnlyTensorSpan().GetDimensionSpan(dim);
            double w = zd.Length == nd.Length ? 0 : double.PositiveInfinity;
            for (nint i = 0; i < zd.Length && w == 0; i++)
            {
                float[] a = new float[zd[i].FlattenedLength], b = new float[nd[i].FlattenedLength];
                zd[i].FlattenTo(a);
                nd[i].FlattenTo(b);
                for (int j = 0; j < a.Length; j++)
                    w = Math.Max(w, Ulps(a[j], b[j]));
            }
            Record("GetDimensionSpan", w, ix + $" dimension {dim}");
        }
    }
}
