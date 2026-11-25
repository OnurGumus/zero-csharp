// Example: LLM math kernels (lib/Kernels.cs), checked against scalar references
// and timed. Exit code = number of failed checks.
// Build: ./build.sh Kernels   (x64: -m x86-64-v3; arm64: -m dotprod for DotI8)

using System;
using System.Runtime.InteropServices;

namespace KernelsSample;

public static class Program
{
    [DllImport("libm.so.6")] static safe extern float expf(float x);
    [DllImport("libm.so.6")] static safe extern double sqrt(double x);
    [DllImport("libc")] static unsafe extern int clock_gettime(int clock, long* timespec);

    static int s_failures;
    static uint s_rng = 2463534242u;

    static float Random(float lo, float hi)
    {
        s_rng = s_rng * 1664525u + 1013904223u;
        return lo + (hi - lo) * ((s_rng >> 8) * (1.0f / 16777216.0f));
    }

    static double Abs(double v) => v < 0 ? -v : v;

    static long NowUs()
    {
        long* ts = stackalloc long[2];
        unsafe
        {
            clock_gettime(1, ts); // SAFETY: ts holds a timespec.
            return ts[0] * 1_000_000 + ts[1] / 1000;
        }
    }

    static void Check(string name, bool ok, string detail, double value)
    {
        Console.Write(ok ? "PASS " : "FAIL ");
        Console.Write(name);
        Console.Write(" (");
        Console.Write(detail);
        WriteSci(value);
        Console.WriteLine(")");
        if (!ok)
            s_failures++;
    }

    // Prints a small positive number as m.mmm e-x.
    static void WriteSci(double v)
    {
        if (v == 0) { Console.Write("0"); return; }
        int e = 0;
        while (v < 1) { v *= 10; e--; }
        while (v >= 10) { v /= 10; e++; }
        Console.Write((long)v);
        Console.Write(".");
        long frac = (long)((v - (long)v) * 1000);
        if (frac < 100) Console.Write("0");
        if (frac < 10) Console.Write("0");
        Console.Write(frac);
        Console.Write("e");
        Console.Write(e);
    }

    public static int Main()
    {
        Console.Write("=== LLM kernels, path: ");
        Console.Write(Kernels.Path);
        Console.WriteLine(" ===");
        Arena arena = Arena.Create(1 << 20);
        if (!arena.IsCreated)
            return 1;

        // docs:buffers
        const int N = 1003; // odd on purpose: exercises the scalar tails
        float* x = arena.AllocArray<float>(N, align: 64);
        float* y = arena.AllocArray<float>(N, align: 64);
        float* w = arena.AllocArray<float>(N, align: 64);
        float* refv = arena.AllocArray<float>(N, align: 64);
        // docs:end

        unsafe
        {
            // ---- Exp: max relative error vs libm over the whole range ----
            double maxRel = 0;
            for (int block = 0; block < 2000; block++)
            {
                for (int i = 0; i < N; i++) x[i] = Random(-87f, 88f);
                Kernels.Exp(x, y, N);
                for (int i = 0; i < N; i++)
                {
                    double want = expf(x[i]);
                    double rel = Abs(y[i] - want) / want;
                    if (rel > maxRel) maxRel = rel;
                }
            }
            Check("Exp vs libm, 2M values in [-87, 88]", maxRel < 4e-7, "max rel err ", maxRel);

            // docs:softmax
            // ---- Softmax ----
            for (int i = 0; i < N; i++) { x[i] = Random(-20f, 20f); refv[i] = x[i]; }
            Kernels.Softmax(x, N);
            // docs:end
            float max = refv[0];
            for (int i = 1; i < N; i++) max = refv[i] > max ? refv[i] : max;
            double sum = 0, total = 0, maxAbs = 0;
            for (int i = 0; i < N; i++) sum += expf(refv[i] - max);
            for (int i = 0; i < N; i++)
            {
                double want = expf(refv[i] - max) / sum;
                double d = Abs(x[i] - want);
                if (d > maxAbs) maxAbs = d;
                total += x[i];
            }
            Check("Softmax vs scalar", maxAbs < 1e-6, "max abs err ", maxAbs);
            Check("Softmax sums to 1", Abs(total - 1) < 1e-5, "|sum - 1| ", Abs(total - 1));

            // ---- SiLU ----
            for (int i = 0; i < N; i++) { x[i] = Random(-30f, 30f); refv[i] = x[i]; }
            Kernels.Silu(x, N);
            maxAbs = 0;
            for (int i = 0; i < N; i++)
            {
                double want = refv[i] / (1.0 + expf(-refv[i]));
                double d = Abs(x[i] - want) / (Abs(want) + 1e-3);
                if (d > maxAbs) maxAbs = d;
            }
            Check("SiLU vs scalar", maxAbs < 1e-6, "max rel err ", maxAbs);

            // ---- RMSNorm ----
            for (int i = 0; i < N; i++) { x[i] = Random(-3f, 3f); w[i] = Random(0.5f, 1.5f); }
            Kernels.RmsNorm(y, x, w, N, 1e-5f);
            double ss = 0;
            for (int i = 0; i < N; i++) ss += (double)x[i] * x[i];
            double scale = 1.0 / sqrt(ss / N + 1e-5);
            maxAbs = 0;
            for (int i = 0; i < N; i++)
            {
                double want = x[i] * scale * w[i];
                double d = Abs(y[i] - want) / (Abs(want) + 1e-3);
                if (d > maxAbs) maxAbs = d;
            }
            Check("RMSNorm vs double", maxAbs < 1e-5, "max rel err ", maxAbs);

            // ---- MatVec ----
            const int Rows = 64;
            float* m = arena.AllocArray<float>(Rows * N, align: 64);
            float* out64 = arena.AllocArray<float>(Rows, align: 64);
            for (int i = 0; i < Rows * N; i++) m[i] = Random(-1f, 1f);
            for (int i = 0; i < N; i++) x[i] = Random(-1f, 1f);
            Kernels.MatVec(out64, m, x, Rows, N);
            maxAbs = 0;
            for (int r = 0; r < Rows; r++)
            {
                double want = 0;
                for (int c = 0; c < N; c++) want += (double)m[r * N + c] * x[c];
                double d = Abs(out64[r] - want);
                if (d > maxAbs) maxAbs = d;
            }
            Check("MatVec 64x1003 vs double", maxAbs < 1e-3, "max abs err ", maxAbs);

            // ---- DotI8 ----
            const int NI8 = 4096 + 7;
            sbyte* a8 = (sbyte*)arena.Alloc(NI8, 64);
            sbyte* b8 = (sbyte*)arena.Alloc(NI8, 64);
            for (int i = 0; i < NI8; i++) { a8[i] = (sbyte)(int)Random(-127.9f, 127.9f); b8[i] = (sbyte)(int)Random(-127.9f, 127.9f); }
            int want8 = 0;
            for (int i = 0; i < NI8; i++) want8 += a8[i] * b8[i];
            int got8 = Kernels.DotI8(a8, b8, NI8);
            Check("DotI8 exact, 4103 values", got8 == want8, "abs diff ", got8 > want8 ? got8 - want8 : want8 - got8);

            // ---- Timing: 4096-float softmax and dot ----
            const int T = 4096, Reps = 20000;
            float* t = arena.AllocArray<float>(T, align: 64);
            for (int i = 0; i < T; i++) t[i] = Random(-10f, 10f);

            long t0 = NowUs();
            for (int r = 0; r < Reps; r++) Kernels.Softmax(t, T);
            long t1 = NowUs();
            for (int r = 0; r < Reps; r++)
            {
                float mx = t[0];
                for (int i = 1; i < T; i++) mx = t[i] > mx ? t[i] : mx;
                float s = 0;
                for (int i = 0; i < T; i++) { t[i] = expf(t[i] - mx); s += t[i]; }
                float inv = 1.0f / s;
                for (int i = 0; i < T; i++) t[i] *= inv;
            }
            long t2 = NowUs();
            Console.WriteLine();
            Console.WriteLine($"Softmax 4096 x 20000: kernel {(t1 - t0) / 1000} ms");
            Console.WriteLine($"                      scalar libm {(t2 - t1) / 1000} ms");
            if (t1 > t0)
                Console.WriteLine($"                      speedup {(t2 - t1) / (t1 - t0)}x");
        }

        unsafe { arena.Destroy(); } // SAFETY: nothing uses the buffers after this.
        Console.WriteLine();
        Console.WriteLine($"failures: {s_failures}");
        return s_failures;
    }
}
