// Two kernels on the GPU of an Apple Silicon Mac, checked against the CPU.
//
// A kernel is a static method marked [RuntimeExport("Name")], written in
// ordinary C#: pointers are buffers (GpuBuffer), other parameters are values
// passed with the launch, and Gpu.* gives the thread's position, threadgroup
// memory, barriers and atomics. Gpu.Run("Name", threads, arguments...) runs
// it. A kernel can't allocate, use strings or double, or call other methods.
//
// Guide: https://onurgumus.github.io/zero-csharp/how-to/run-code-on-the-gpu.html

using System;
using System.Runtime;

public static class Program
{
    const int N = 1 << 20;

    // docs:saxpy
    // y = a * x + y for the first n elements; threads past n do nothing.
    [RuntimeExport("Saxpy")]
    static unsafe void Saxpy(float* x, float* y, float a, int n)
    {
        uint i = Gpu.ThreadId();
        if (i < (uint)n)
            unsafe { y[i] = a * x[i] + y[i]; } // SAFETY: i < n
    }
    // docs:end

    // docs:sum
    // Each threadgroup adds up its part of x in threadgroup memory, halving
    // the threads that work at each step, then adds its total to *total.
    [RuntimeExport("Sum")]
    static unsafe void Sum(int* x, int* total)
    {
        int* tile;
        // SAFETY (all four blocks): the launch gives one thread per element
        // and one int of threadgroup memory per thread; total is one int.
        unsafe { tile = (int*)Gpu.Shared(); }
        uint local = Gpu.LocalId();
        unsafe { tile[local] = x[Gpu.ThreadId()]; }
        Gpu.Barrier();
        for (uint half = Gpu.GroupSize() / 2; half > 0; half /= 2)
        {
            if (local < half)
                unsafe { tile[local] += tile[local + half]; }
            Gpu.Barrier();
        }
        if (local == 0)
            unsafe { Gpu.AtomicAdd(total, tile[0]); }
    }
    // docs:end

    public static int Main()
    {
        if (!Gpu.Open())
        {
            Console.WriteLine("No Metal GPU.");
            return 1;
        }
        Span<char> name = stackalloc char[64];
        Console.WriteLine($"GPU: {Gpu.DeviceName(name)}");

        // docs:run
        var x = GpuBuffer<float>.Allocate(N);
        var y = GpuBuffer<float>.Allocate(N);
        for (int i = 0; i < N; i++) { x.Span[i] = i % 100; y.Span[i] = 1; }
        double ms = Gpu.Run("Saxpy", N, x, y, 2f, N);
        int wrong = 0;
        for (int i = 0; i < N; i++)
            if (y.Span[i] != 2f * (i % 100) + 1) wrong++;
        long us = (long)(ms * 1000);
        Console.WriteLine($"Saxpy: {N} elements, {us} us on the GPU, {wrong} wrong");
        // docs:end

        // docs:launch
        // Threadgroups of 256 threads, each with 256 ints of shared memory.
        const uint group = 256;
        var values = GpuBuffer<int>.Allocate(N);
        var total = GpuBuffer<int>.Allocate(1);
        int want = 0;
        for (int i = 0; i < N; i++) { values.Span[i] = i % 10; want += i % 10; }
        total.Span[0] = 0;
        var launch = new GpuLaunch(N, group, (int)group * sizeof(int));
        ms = Gpu.Run("Sum", launch, values, total);
        int got = total.Span[0];
        // docs:end
        us = (long)(ms * 1000);
        Console.WriteLine($"Sum:   {got} (the CPU says {want}), {us} us on the GPU");

        x.Free(); y.Free(); values.Free(); total.Free();
        return wrong == 0 && got == want ? 0 : 1;
    }
}
