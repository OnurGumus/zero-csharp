// C# kernels on the GPU (Metal, on a Mac with Apple silicon; lib/Gpu.cs): each
// kernel runs on the GPU and is checked against the CPU, by running the same
// method there or a plain C# equivalent, and timed against a plain C# loop.
// Build: ./build.sh --gpu Gpu   Run: ./run.sh --gpu Gpu   Test: ./test.sh --gpu

using System;
using System.Buffers;
using System.Numerics.Tensors;
using System.Runtime;
using System.Runtime.InteropServices;

namespace GpuSample;

// The kernels: each [RuntimeExport] method is compiled for the GPU too, as a
// kernel of that name (./build.sh --gpu). Pointers are buffers; other
// parameters are values the host passes with the launch.
public static class Kernels
{
    [RuntimeExport("Add")]
    public static unsafe void Add(float* a, float* b, float* c)
    {
        uint i = Gpu.ThreadId();
        unsafe { c[i] = a[i] + b[i]; } // SAFETY: Gpu.Run starts one thread per element.
    }

    // A loop, a branch and 64-bit integer math, to see that control flow survives.
    [RuntimeExport("Collatz")]
    public static unsafe void Collatz(int* steps)
    {
        uint i = Gpu.ThreadId();
        long n = i + 1;
        int count = 0;
        while (n != 1)
        {
            n = (n & 1) == 0 ? n / 2 : 3 * n + 1;
            count++;
        }
        unsafe { steps[i] = count; } // SAFETY: as above.
    }

    // Scalar parameters: y = a * x + y for the first n elements; the threads
    // past n (the grid is rounded up) do nothing.
    [RuntimeExport("Saxpy")]
    public static unsafe void Saxpy(float* x, float* y, float a, int n)
    {
        uint i = Gpu.ThreadId();
        if (i < (uint)n)
            unsafe { y[i] = a * x[i] + y[i]; } // SAFETY: i < n, the buffers' length.
    }

    // Shared memory and barriers: each threadgroup adds up its part of x (one
    // element per thread) in threadgroup memory, halving the active threads each
    // step, and writes the total to partial[group]. The group size must be a
    // power of two, and the launch must give GroupSize floats of shared memory.
    [RuntimeExport("Sum")]
    public static unsafe void Sum(float* x, float* partial)
    {
        float* tile;
        unsafe { tile = (float*)Gpu.Shared(); } // SAFETY: the launch gives GroupSize floats.
        uint local = Gpu.LocalId();
        unsafe { tile[local] = x[Gpu.ThreadId()]; } // SAFETY: one thread per element; local < GroupSize.
        Gpu.Barrier();
        for (uint half = Gpu.GroupSize() / 2; half > 0; half /= 2)
        {
            if (local < half)
                unsafe { tile[local] += tile[local + half]; } // SAFETY: local + half < GroupSize.
            Gpu.Barrier();
        }
        if (local == 0)
            unsafe { partial[Gpu.GroupId()] = tile[0]; } // SAFETY: one slot per threadgroup.
    }

    // Matrix multiply, c = a x b, n x n floats, row-major, on an n x n grid:
    // one thread per element of c (x the column, y the row).

    // Straight from device memory: each thread reads a row of a and a column of b.
    [RuntimeExport("MatMulNaive")]
    public static unsafe void MatMulNaive(float* a, float* b, float* c, int n)
    {
        int col = (int)Gpu.ThreadX(), row = (int)Gpu.ThreadY();
        if (col >= n || row >= n) return;
        float sum = 0;
        for (int k = 0; k < n; k++)
            unsafe { sum += a[row * n + k] * b[k * n + col]; } // SAFETY: row, col, k < n.
        unsafe { c[row * n + col] = sum; } // SAFETY: as above.
    }

    public const int Tile = 16;

    // docs:matmul
    // Tiled: a Tile x Tile threadgroup loads one tile of a and one of b into
    // threadgroup memory together (one element per thread), waits, and works on
    // them from there, so each element of a and b is read from device memory
    // n / Tile times instead of n. Needs n a multiple of Tile, threadgroups of
    // Tile x Tile, and 2 * Tile * Tile floats of shared memory.
    [RuntimeExport("MatMul")]
    public static unsafe void MatMul(float* a, float* b, float* c, int n)
    {
        float* tileA, tileB;
        // SAFETY (every block): the launch gives 2 tiles of shared memory;
        // x, y < Tile; row, col, t + x, t + y < n.
        unsafe { tileA = (float*)Gpu.Shared(); tileB = tileA + Tile * Tile; }
        int x = (int)Gpu.LocalX(), y = (int)Gpu.LocalY();
        int col = (int)Gpu.ThreadX(), row = (int)Gpu.ThreadY();
        float sum = 0;
        for (int t = 0; t < n; t += Tile)
        {
            unsafe
            {
                tileA[y * Tile + x] = a[row * n + t + x];
                tileB[y * Tile + x] = b[(t + y) * n + col];
            }
            Gpu.Barrier();
            for (int k = 0; k < Tile; k++)
                unsafe { sum += tileA[y * Tile + k] * tileB[k * Tile + x]; }
            Gpu.Barrier();
        }
        unsafe { c[row * n + col] = sum; }
    }
    // docs:end

    // Atomics. A histogram of bytes, one thread per byte: every thread adds to
    // one of the same 256 counters in device memory.
    [RuntimeExport("Histogram")]
    public static unsafe void Histogram(byte* data, uint* bins)
    {
        unsafe { Gpu.AtomicAdd(bins + data[Gpu.ThreadId()], 1u); } // SAFETY: one thread per byte; 256 bins.
    }

    // The same without atomics: threads that hit the same bin at the same time
    // lose counts. Only here to show that the atomics matter.
    [RuntimeExport("HistogramRacy")]
    public static unsafe void HistogramRacy(byte* data, uint* bins)
    {
        unsafe { bins[data[Gpu.ThreadId()]]++; } // SAFETY: as above.
    }

    // docs:histogram
    // Threadgroup atomics first: each threadgroup of 256 counts into its own
    // 256 bins in threadgroup memory, then adds them to the device bins, so
    // device memory sees 256 atomics per threadgroup instead of one per byte.
    // Needs threadgroups of 256 and 256 uints of shared memory.
    [RuntimeExport("HistogramShared")]
    public static unsafe void HistogramShared(byte* data, uint* bins)
    {
        uint* local;
        // SAFETY (every block): the launch gives 256 uints of shared memory,
        // l < 256, and one thread per byte.
        unsafe { local = (uint*)Gpu.Shared(); }
        uint l = Gpu.LocalId();
        unsafe { local[l] = 0; }
        Gpu.Barrier();
        unsafe { Gpu.AtomicAdd(local + data[Gpu.ThreadId()], 1u); }
        Gpu.Barrier();
        uint count;
        unsafe { count = local[l]; }
        if (count != 0)
            unsafe { Gpu.AtomicAdd(bins + l, count); }
    }
    // docs:end

    // Float atomic add: every element into one total.
    [RuntimeExport("FloatTotal")]
    public static unsafe void FloatTotal(float* x, float* total)
    {
        unsafe { Gpu.AtomicAdd(total, x[Gpu.ThreadId()]); } // SAFETY: one thread per element.
    }

    // docs:float-max
    // Compare-exchange: the largest element. Metal has no atomic max on
    // floats, so each thread retries until its value isn't larger, or it
    // wrote it with no other thread changing the maximum in between.
    [RuntimeExport("FloatMax")]
    public static unsafe void FloatMax(float* x, float* max)
    {
        float value;
        int old;
        // SAFETY (both blocks): one thread per element; max is one float.
        unsafe { value = x[Gpu.ThreadId()]; old = *(int*)max; }
        int bits = BitConverter.SingleToInt32Bits(value);
        while (BitConverter.Int32BitsToSingle(old) < value)
        {
            int seen;
            unsafe { seen = Gpu.CompareExchange((int*)max, bits, old); }
            if (seen == old) break;
            old = seen;
        }
    }
    // docs:end
}

public static class Program
{
    const int N = 1 << 20;

    public static int Main()
    {
        if (!Gpu.Open()) return 1;
        Span<char> name = stackalloc char[64];
        Console.WriteLine($"GPU: {Gpu.DeviceName(name)}");

        int failures = CheckAdd() + CheckCollatz() + CheckSaxpy() + CheckSum() + CheckMatMul() + CheckTensors() + CheckAtomics();
        Console.WriteLine(failures == 0 ? "OK: GPU matches CPU" : $"FAILED: {failures} differences");
        return failures == 0 ? 0 : 1;
    }

    // Times, in microseconds: the GPU running the kernel (Metal's GPU start and
    // end), the whole Gpu.Run call by the wall clock (encoding, submitting,
    // waiting; the second launch, so the one-time pipeline compile isn't in
    // it), and a plain C# loop doing the same work on one CPU core.
    static void Report(string kernel, double gpuMs, double runMs, double loopMs) =>
        Console.WriteLine($"{kernel}: GPU {Us(gpuMs)} us, Gpu.Run {Us(runMs)} us, C# loop {Us(loopMs)} us (1 core)");

    static long Us(double ms) => (long)(ms * 1000);

    static double Since(long start) => (Clock.Monotonic - start) / 1e6;

    static int CheckAdd()
    {
        var a = GpuBuffer<float>.Allocate(N);
        var b = GpuBuffer<float>.Allocate(N);
        var gpu = GpuBuffer<float>.Allocate(N);
        var cpu = GpuBuffer<float>.Allocate(N);
        for (int i = 0; i < N; i++) { a.Span[i] = i * 0.5f; b.Span[i] = N - i; }

        Gpu.Run(nameof(Kernels.Add), N, a, b, gpu);
        long start = Clock.Monotonic;
        double gpuMs = Gpu.Run(nameof(Kernels.Add), N, a, b, gpu);
        double runMs = Since(start);
        start = Clock.Monotonic;
        Span<float> sa = a.Span, sb = b.Span, sc = cpu.Span;
        for (int i = 0; i < sc.Length; i++) sc[i] = sa[i] + sb[i];
        Report("Add    ", gpuMs, runMs, Since(start));

        // The check: the kernel itself on the CPU, one thread at a time.
        for (uint i = 0; i < N; i++)
        {
            Gpu.SetThreadId(i);
            unsafe { Kernels.Add(a.Pointer, b.Pointer, cpu.Pointer); } // SAFETY: i < N, the buffers' length.
        }

        int failures = Compare("Add", gpu.Span, cpu.Span);
        a.Free(); b.Free(); gpu.Free(); cpu.Free();
        return failures;
    }

    static int CheckCollatz()
    {
        var gpu = GpuBuffer<int>.Allocate(N);
        var cpu = GpuBuffer<int>.Allocate(N);

        Gpu.Run(nameof(Kernels.Collatz), N, gpu);
        long start = Clock.Monotonic;
        double gpuMs = Gpu.Run(nameof(Kernels.Collatz), N, gpu);
        double runMs = Since(start);
        start = Clock.Monotonic;
        Span<int> steps = cpu.Span;
        for (int i = 0; i < steps.Length; i++)
        {
            long n = i + 1;
            int count = 0;
            while (n != 1) { n = (n & 1) == 0 ? n / 2 : 3 * n + 1; count++; }
            steps[i] = count;
        }
        Report("Collatz", gpuMs, runMs, Since(start));

        for (uint i = 0; i < N; i++)
        {
            Gpu.SetThreadId(i);
            unsafe { Kernels.Collatz(cpu.Pointer); } // SAFETY: as above.
        }

        int failures = Compare("Collatz", gpu.Span, cpu.Span);
        int longest = 0;
        for (int i = 1; i < N; i++) if (gpu.Span[i] > gpu.Span[longest]) longest = i;
        Console.WriteLine($"         longest chain starts at {longest + 1} ({gpu.Span[longest]} steps)");
        gpu.Free(); cpu.Free();
        return failures;
    }

    static int CheckSaxpy()
    {
        const int n = N - 3; // not a multiple of the threadgroup size
        var x = GpuBuffer<float>.Allocate(N);
        var gpu = GpuBuffer<float>.Allocate(N);
        var cpu = GpuBuffer<float>.Allocate(N);
        for (int i = 0; i < N; i++) { x.Span[i] = i % 1000; gpu.Span[i] = cpu.Span[i] = -i; }

        // Saxpy updates y in place: time it on a scratch buffer, check on gpu.
        var scratch = GpuBuffer<float>.Allocate(N);
        Gpu.Run(nameof(Kernels.Saxpy), N, x, scratch, 2.5f, n);
        long start = Clock.Monotonic;
        double gpuMs = Gpu.Run(nameof(Kernels.Saxpy), N, x, scratch, 2.5f, n);
        double runMs = Since(start);
        start = Clock.Monotonic;
        Span<float> sx = x.Span, sy = scratch.Span;
        for (int i = 0; i < n; i++) sy[i] = 2.5f * sx[i] + sy[i];
        Report("Saxpy  ", gpuMs, runMs, Since(start));
        scratch.Free();

        Gpu.Run(nameof(Kernels.Saxpy), N, x, gpu, 2.5f, n);
        for (uint i = 0; i < N; i++)
        {
            Gpu.SetThreadId(i);
            unsafe { Kernels.Saxpy(x.Pointer, cpu.Pointer, 2.5f, n); } // SAFETY: as above.
        }

        int failures = Compare("Saxpy", gpu.Span, cpu.Span);
        if (gpu.Span[N - 1] != -(N - 1)) { Console.WriteLine("Saxpy wrote past n"); failures++; }
        x.Free(); gpu.Free(); cpu.Free();
        return failures;
    }

    // Sum needs real threadgroups (barriers), so the CPU side is plain C#.
    // The values are small integers, so every order of adding them is exact.
    static int CheckSum()
    {
        const uint group = 256;
        const int groups = N / (int)group;
        var x = GpuBuffer<float>.Allocate(N);
        var partial = GpuBuffer<float>.Allocate(groups);
        for (int i = 0; i < N; i++) x.Span[i] = i % 7;

        var launch = new GpuLaunch(N, group, (int)group * sizeof(float));
        Gpu.Run(nameof(Kernels.Sum), launch, x, partial);
        long start = Clock.Monotonic;
        double gpuMs = Gpu.Run(nameof(Kernels.Sum), launch, x, partial);
        double runMs = Since(start);
        var cpu = GpuBuffer<float>.Allocate(groups);
        start = Clock.Monotonic;
        for (int g = 0; g < groups; g++)
        {
            float total = 0;
            for (int i = 0; i < group; i++) total += x.Span[g * (int)group + i];
            cpu.Span[g] = total;
        }
        Report("Sum    ", gpuMs, runMs, Since(start));

        int failures = Compare("Sum", partial.Span, cpu.Span);
        long sum = 0;
        for (int g = 0; g < groups; g++) sum += (long)partial.Span[g];
        Console.WriteLine($"         {groups} threadgroups of {group}, total {sum}");
        x.Free(); partial.Free(); cpu.Free();
        return failures;
    }

    // Both matrix kernels against a plain C# multiply. The entries are small
    // integers, so every order of adding the products gives the same floats.
    static int CheckMatMul()
    {
        const int n = 1024;
        const uint tile = Kernels.Tile;
        var a = GpuBuffer<float>.Allocate(n * n);
        var b = GpuBuffer<float>.Allocate(n * n);
        var naive = GpuBuffer<float>.Allocate(n * n);
        var tiled = GpuBuffer<float>.Allocate(n * n);
        var cpu = GpuBuffer<float>.Allocate(n * n);
        for (int i = 0; i < n * n; i++) { a.Span[i] = i % 5 - 2; b.Span[i] = i % 3 - 1; }

        // docs:matmul-launch
        // An n x n grid of threads in Tile x Tile threadgroups; the tiled
        // kernel also gets two tiles of floats of threadgroup memory.
        var naiveLaunch = GpuLaunch.Grid(n, n, tile, tile);
        int tileBytes = (int)(tile * tile) * sizeof(float);
        var tiledLaunch = GpuLaunch.Grid(n, n, tile, tile, 2 * tileBytes);
        Gpu.Run(nameof(Kernels.MatMul), tiledLaunch, a, b, tiled, n);
        // docs:end
        Gpu.Run(nameof(Kernels.MatMulNaive), naiveLaunch, a, b, naive, n);
        long start = Clock.Monotonic;
        double naiveGpuMs = Gpu.Run(nameof(Kernels.MatMulNaive), naiveLaunch, a, b, naive, n);
        double naiveRunMs = Since(start);
        start = Clock.Monotonic;
        double tiledGpuMs = Gpu.Run(nameof(Kernels.MatMul), tiledLaunch, a, b, tiled, n);
        double tiledRunMs = Since(start);

        // i-k-j order: the inner loop walks rows of b and c, which caches like.
        start = Clock.Monotonic;
        Span<float> sa = a.Span, sb = b.Span, sc = cpu.Span;
        sc.Clear();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                float aik = sa[i * n + k];
                Span<float> brow = sb.Slice(k * n, n), crow = sc.Slice(i * n, n);
                for (int j = 0; j < n; j++) crow[j] += aik * brow[j];
            }
        double loopMs = Since(start);

        double gflop = 2.0 * n * n * n / 1e9;
        Report("MatMulNaive", naiveGpuMs, naiveRunMs, loopMs);
        Report("MatMul     ", tiledGpuMs, tiledRunMs, loopMs);
        Console.WriteLine($"         {n} x {n}: naive {(long)(gflop / naiveRunMs * 1000)} GFLOP/s, tiled {(long)(gflop / tiledRunMs * 1000)} GFLOP/s, C# loop {(long)(gflop / loopMs * 1000 * 10) / 10.0} GFLOP/s");

        int failures = Compare("MatMulNaive", naive.Span, cpu.Span) + Compare("MatMul", tiled.Span, cpu.Span);
        a.Free(); b.Free(); naive.Free(); tiled.Free(); cpu.Free();
        return failures;
    }

    // docs:tensors
    // Matrices as tensors over GPU buffers: the CPU fills a and b with the
    // Tensor methods, writing straight into the memory the GPU reads (no
    // copies), the tiled kernel multiplies them, and Tensor.Dot checks c.
    static int CheckTensors()
    {
        const int n = 256;
        var a = GpuBuffer<float>.Allocate(n * n);
        var b = GpuBuffer<float>.Allocate(n * n);
        var c = GpuBuffer<float>.Allocate(n * n);
        TensorSpan<float> ta = a.AsTensorSpan(n, n), tb = b.AsTensorSpan(n, n);

        // A column [n, 1] and a row [1, n], broadcast into the n x n buffers:
        // a[i, j] = column[i] + row[j], b[i, j] = column[i] * row[j].
        Tensor<float> column = Tensor.CreateFromShape<float>([n, 1]);
        Tensor<float> row = Tensor.CreateFromShape<float>([1, n]);
        for (int i = 0; i < n; i++)
        {
            column[i, 0] = i % 5 - 2;
            row[0, i] = i % 3 - 1;
        }
        Tensor.Add(column, row, ta);
        Tensor.Multiply(column, row, tb);

        const uint tile = Kernels.Tile;
        int sharedBytes = 2 * (int)(tile * tile) * sizeof(float);
        var launch = GpuLaunch.Grid(n, n, tile, tile, sharedBytes);
        Gpu.Run(nameof(Kernels.MatMul), launch, a, b, c, n);

        // c[i, j] is row i of a times column j of b; a column is a view of b
        // with stride n.
        ReadOnlyTensorSpan<float> tc = c.AsReadOnlyTensorSpan(n, n);
        int failures = 0;
        for (nint i = 0; i < n; i++)
        {
            var rowSlice = ta.Slice([new NRange(i, i + 1), NRange.All]);
            ReadOnlyTensorSpan<float> ai = Tensor.Reshape(rowSlice, [n]);
            for (nint j = 0; j < n; j++)
            {
                var columnSlice = tb.Slice([NRange.All, new NRange(j, j + 1)]);
                ReadOnlyTensorSpan<float> bj = Tensor.Reshape(columnSlice, [n]);
                if (Tensor.Dot(ai, bj) != tc[i, j] && failures++ < 5)
                    Console.WriteLine($"Tensors: c[{i}, {j}] differs");
            }
        }
        Console.WriteLine(
            $"Tensors: {n} x {n} on the GPU, checked with Tensor.Dot");
        a.Free(); b.Free(); c.Free();
        return failures;
    }
    // docs:end

    static int CheckAtomics()
    {
        const int bytes = 1 << 24;
        var data = GpuBuffer<byte>.Allocate(bytes);
        var bins = GpuBuffer<uint>.Allocate(256);
        var cpu = GpuBuffer<uint>.Allocate(256);
        uint state = 2463534242;
        for (int i = 0; i < bytes; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            data.Span[i] = (byte)(state >> 24);
        }
        long start = Clock.Monotonic;
        Span<byte> sd = data.Span;
        Span<uint> sc = cpu.Span;
        sc.Clear();
        for (int i = 0; i < sd.Length; i++) sc[sd[i]]++;
        double loopMs = Since(start);

        int failures = 0;
        var shared = new GpuLaunch(bytes, 256, 256 * sizeof(uint));
        foreach (string kernel in new[] { nameof(Kernels.Histogram), nameof(Kernels.HistogramShared) })
        {
            var launch = kernel == nameof(Kernels.Histogram) ? new GpuLaunch(bytes) : shared;
            bins.Span.Clear();
            Gpu.Run(kernel, launch, data, bins);
            failures += Compare(kernel, bins.Span, cpu.Span);
            bins.Span.Clear();
            start = Clock.Monotonic;
            double gpuMs = Gpu.Run(kernel, launch, data, bins);
            Report(kernel == nameof(Kernels.Histogram) ? "Histogram      " : "HistogramShared", gpuMs, Since(start), loopMs);
        }

        bins.Span.Clear();
        Gpu.Run(nameof(Kernels.HistogramRacy), bytes, data, bins);
        int wrong = 0;
        long lost = 0;
        for (int b = 0; b < 256; b++)
            if (bins.Span[b] != cpu.Span[b]) { wrong++; lost += cpu.Span[b] - bins.Span[b]; }
        Console.WriteLine($"         {bytes} bytes; without atomics {wrong} of 256 bins are wrong ({lost} counts lost)");
        if (wrong == 0) { Console.WriteLine("HistogramRacy: no lost counts, so the histogram test doesn't show the atomics work"); failures++; }

        // Floats: small integers whose total stays below 2^24, so every partial
        // sum is exact and the order the GPU adds them in doesn't matter.
        var x = GpuBuffer<float>.Allocate(N);
        var result = GpuBuffer<float>.Allocate(1);
        float want = 0, wantMax = -1e30f;
        for (int i = 0; i < N; i++)
        {
            x.Span[i] = (uint)i * 7919u % 16;
            want += x.Span[i];
        }
        x.Span[123457] = 5000; // the maximum, somewhere in the middle
        want += 5000 - 123457u * 7919u % 16;
        for (int i = 0; i < N; i++) if (x.Span[i] > wantMax) wantMax = x.Span[i];

        result.Span[0] = 0;
        Gpu.Run(nameof(Kernels.FloatTotal), N, x, result);
        float total = result.Span[0];
        result.Span[0] = -1e30f;
        Gpu.Run(nameof(Kernels.FloatMax), N, x, result);
        float max = result.Span[0];
        Console.WriteLine($"Floats : total {(long)total} (want {(long)want}), max {(long)max} (want {(long)wantMax})");
        if (total != want) { Console.WriteLine("FloatTotal differs"); failures++; }
        if (max != wantMax) { Console.WriteLine("FloatMax differs"); failures++; }

        data.Free(); bins.Free(); cpu.Free(); x.Free(); result.Free();
        return failures;
    }

    static int Compare<T>(string kernel, Span<T> gpu, Span<T> cpu) where T : unmanaged, IEquatable<T>
    {
        int failures = 0;
        for (int i = 0; i < gpu.Length; i++)
            if (!gpu[i].Equals(cpu[i]))
                if (failures++ < 5) Console.WriteLine($"{kernel}[{i}] differs");
        return failures;
    }
}
