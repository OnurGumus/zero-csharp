---
title: Run code on the GPU
index: 25
---

# Run code on the GPU

On an Apple Silicon Mac, a zero program can run C# methods on the GPU, through Metal. A **kernel** is
an ordinary static method that the build compiles twice: once into the program, and once for the GPU.
The program then runs it on thousands of GPU threads at once, each working on its own element.

`ZeroGpu` and `dotnet new zerogpu` are in the SDK from 0.13.0 on. This is experimental. The kernels are compiled to AIR, the format Apple's Metal compiler reads, which
Apple doesn't document, so a new version of Xcode can break it.

The same kernels can also run on an NVIDIA GPU on Linux, through CUDA: see
[NVIDIA GPUs](#nvidia-gpus-cuda). That's newer still, from 0.14.0 on, and hasn't yet run on a real
NVIDIA GPU.

## What you need

- **An Apple Silicon Mac with Xcode**, from the App Store, and its Metal Toolchain, which Xcode
  downloads separately (about 840 MB):

  ```bash
  xcodebuild -downloadComponent MetalToolchain
  ```

- **Docker**, running: Docker Desktop, or a free alternative such as Colima or Podman. The kernels'
  first compiler, NativeAOT-LLVM, has no macOS build, so it runs in a Linux container, as it does for
  [the Commodore 64](build-for-c64.html). The first build downloads it.
- **The .NET SDK packages** ([Install with dotnet](../get-started/install-with-dotnet.html)).

## Make the program

```bash
dotnet new zerogpu -o Kernels
cd Kernels
dotnet run
```

The program runs two kernels on the GPU and checks their results on the CPU:

```text
GPU: Apple M2 Max
Saxpy: 1048576 elements, 43 us on the GPU, 0 wrong
Sum:   4718580 (the CPU says 4718580), 85 us on the GPU
```

The project is a program with one more property, `ZeroGpu`:

```xml
<Project Sdk="ZeroCSharp.Sdk/0.16.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <ZeroGpu>Metal</ZeroGpu>
  </PropertyGroup>
</Project>
```

## Write a kernel

A kernel is a static method marked `[RuntimeExport("Name")]` (from `System.Runtime`). The name is what
the program launches it by. Pointer parameters are buffers; any other parameter is a value that comes
with the launch:

<!-- sample: sdk/ZeroCSharp.Templates/content/zerogpu/Program.cs saxpy -->
```csharp
// y = a * x + y for the first n elements; threads past n do nothing.
[RuntimeExport("Saxpy")]
static unsafe void Saxpy(float* x, float* y, float a, int n)
{
    uint i = Gpu.ThreadId();
    if (i < (uint)n)
        unsafe { y[i] = a * x[i] + y[i]; } // SAFETY: i < n
}
```

Every thread runs the whole method. `Gpu.ThreadId()` says which thread this one is, and so which
element it works on. The number of threads is the launch's, not the kernel's, so a kernel that can
be launched with more threads than elements checks `i` against `n`.

Pointers are read and written in `unsafe` blocks, as anywhere in zero mode. The GPU checks no bounds,
so the `SAFETY` comment says why each access stays inside its buffer.

## Launch it

<!-- sample: sdk/ZeroCSharp.Templates/content/zerogpu/Program.cs run -->
```csharp
var x = GpuBuffer<float>.Allocate(N);
var y = GpuBuffer<float>.Allocate(N);
for (int i = 0; i < N; i++) { x.Span[i] = i % 100; y.Span[i] = 1; }
double ms = Gpu.Run("Saxpy", N, x, y, 2f, N);
int wrong = 0;
for (int i = 0; i < N; i++)
    if (y.Span[i] != 2f * (i % 100) + 1) wrong++;
long us = (long)(ms * 1000);
Console.WriteLine($"Saxpy: {N} elements, {us} us on the GPU, {wrong} wrong");
```

- `GpuBuffer<T>.Allocate(n)` makes a buffer of `n` elements that the CPU and the GPU share: on Apple
  silicon they use the same memory, so nothing is copied. `.Span` reads and writes it on the CPU,
  `.Pointer` is its address, and `Free()` gives it back.
- `Gpu.Run("Saxpy", N, x, y, 2f, N)` starts `N` threads and waits for them to finish. The arguments
  go to the kernel's parameters in order: a `GpuBuffer` for a pointer, and an `int`, `uint`, `long`
  or `float` for a value. It returns the time the GPU spent running the kernel, in milliseconds.
- `Gpu.Open()` finds the GPU, and `Gpu.DeviceName` writes its name into a span.

A wrong kernel name, or a launch the GPU refuses, stops the program with a message.

## Threadgroups, shared memory and barriers

The GPU runs threads in **threadgroups**. The threads of one threadgroup can share a block of fast
**threadgroup memory**, and wait for each other at a **barrier**. The template's second kernel uses all
three to add up a buffer: each threadgroup loads its part into threadgroup memory, then adds pairs,
halving the threads that work at each step, until one value is left:

<!-- sample: sdk/ZeroCSharp.Templates/content/zerogpu/Program.cs sum -->
```csharp
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
```

- `Gpu.Shared()` is the threadgroup's memory, as a `void*` to cast.
- `Gpu.LocalId()` is the thread's position in its threadgroup, and `Gpu.GroupSize()` the number of
  threads in it.
- `Gpu.Barrier()` waits until every thread of the threadgroup gets there, and makes what they wrote
  to threadgroup memory visible to each other. Without the barriers, threads read values that others
  haven't written yet.
- `Gpu.AtomicAdd` adds each threadgroup's total to one value in the buffer. Plain `+=` there would
  lose totals, when two threadgroups read the old value at the same time.

The launch sets the threadgroup size and how much threadgroup memory each one gets, with `GpuLaunch`:

<!-- sample: sdk/ZeroCSharp.Templates/content/zerogpu/Program.cs launch -->
```csharp
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
```

This kernel needs a power-of-two threadgroup size, and as many ints of memory as threads.
`new GpuLaunch(threads)` alone, like `Gpu.Run("Name", threads, ...)`, makes threadgroups as large as
the kernel allows, with no threadgroup memory.

## 2D grids: a tiled matrix multiply

A grid can have two dimensions: `Gpu.ThreadX()` and `Gpu.ThreadY()`, `Gpu.LocalX()` and `Gpu.LocalY()`.
The repository's `samples/Gpu.cs` multiplies two matrices with one thread per element of the result.
Each 16 x 16 threadgroup loads a tile of each matrix into threadgroup memory, waits, and multiplies
from there, so each element is read from the buffers 16 times less often:

<!-- sample: samples/Gpu.cs matmul -->
```csharp
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
```

The launch is a grid of `n` x `n` threads in 16 x 16 threadgroups, with room for two tiles:

<!-- sample: samples/Gpu.cs matmul-launch -->
```csharp
// An n x n grid of threads in Tile x Tile threadgroups; the tiled
// kernel also gets two tiles of floats of threadgroup memory.
var naiveLaunch = GpuLaunch.Grid(n, n, tile, tile);
int tileBytes = (int)(tile * tile) * sizeof(float);
var tiledLaunch = GpuLaunch.Grid(n, n, tile, tile, 2 * tileBytes);
Gpu.Run(nameof(Kernels.MatMul), tiledLaunch, a, b, tiled, n);
```

On an M2 Max, at 1024 x 1024, the tiled kernel takes about 1.2 ms of GPU time, and the same multiply
reading straight from the buffers 5.5 to 6 ms.

## Matrices as tensors

`GpuBuffer<T>.AsTensorSpan(rows, columns)` is the buffer as a [tensor](use-tensors.html): a view,
not a copy, so the [Tensor methods](use-tensors.html) fill and read the very memory the GPU uses.
Here broadcasting writes two matrices straight into the buffers, the tiled kernel multiplies them,
and `Tensor.Dot` of a row and a column checks each element of the result:

<!-- sample: samples/Gpu.cs tensors -->
```csharp
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
```

A kernel still takes pointers: the tensor is for the CPU side, the lengths and strides are the
kernel's to know.

## Atomics

An atomic changes one value in memory without another thread's change getting lost in between. It
works on buffers and on threadgroup memory, and returns the value from before. A histogram with
16 million bytes and 256 counters has many threads adding to the same counter at once; counting in
threadgroup memory first means the buffer sees 256 atomics per threadgroup instead of one per byte:

<!-- sample: samples/Gpu.cs histogram -->
```csharp
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
```

`Gpu.CompareExchange(location, value, comparand)` writes `value` only if the memory still holds
`comparand`, as `Interlocked.CompareExchange` does. With it, a kernel can make an atomic operation
Metal doesn't have, such as a maximum of floats:

<!-- sample: samples/Gpu.cs float-max -->
```csharp
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
```

| Call | On |
|---|---|
| `AtomicAdd`, `AtomicSub`, `AtomicMin`, `AtomicMax` | `int*`, `uint*` (`AtomicAdd` also `float*`) |
| `AtomicAnd`, `AtomicOr`, `AtomicXor` | `int*`, `uint*` |
| `Exchange` | `int*`, `uint*`, `float*` |
| `CompareExchange` | `int*`, `uint*` |

## What a kernel can use

| Call | What it is |
|---|---|
| `ThreadId()`, `ThreadX()`, `ThreadY()` | The thread's position in the grid (`ThreadId` is `ThreadX`) |
| `LocalId()`, `LocalX()`, `LocalY()` | Its position in its threadgroup |
| `GroupId()`, `GroupX()`, `GroupY()` | The threadgroup's position in the grid |
| `GroupSize()`, `GroupWidth()`, `GroupHeight()` | The threadgroup's size |
| `GridWidth()`, `GridHeight()` | The grid's size |
| `Shared()` | The threadgroup's memory |
| `Barrier()` | Wait for the threadgroup, and see what it wrote to threadgroup memory |

Besides those, a kernel can do arithmetic on integers and `float`, loops and branches, and loads and
stores through its buffers and threadgroup memory. The build rejects anything else, and names it:

```text
ll2air: Kernels_App_Program__Scale: calls @Kernels_App_Program__Twice, which can't run on the GPU
```

The name is the method's in the compiled code: the project's name, then the class's and the
method's.

- **No `double`.** Metal has no 64-bit floats.
- **No allocation, strings or objects.** A kernel has no runtime under it.
- **No calls to other methods**, except ones the compiler inlines into the kernel. One that isn't
  inlined is an error that names it.
- **No `stackalloc`**, or other locals the compiler keeps in memory rather than in registers: "uses
  the stack (alloca)".
- **Parameters are pointers or numbers**, not structs.

## Check a kernel on the CPU

A kernel is an ordinary C# method, so the program can call it on the CPU too. There, the `Gpu.*` calls
have stand-ins: `Gpu.SetThreadId(i)` sets what `ThreadId()` returns for the next call, and each thread
is a threadgroup of its own. `samples/Gpu.cs` checks its simple kernels this way:

```csharp no-sample
for (uint i = 0; i < N; i++)
{
    Gpu.SetThreadId(i);
    unsafe { Kernels.Add(a.Pointer, b.Pointer, cpu.Pointer); }
}
```

A kernel that needs real threadgroups (barriers, threadgroup memory that threads share) doesn't work
one thread at a time: check it against plain C# instead, as the template does.

## NVIDIA GPUs (CUDA)

`<ZeroGpu>Cuda</ZeroGpu>` builds the same program for Linux, with its kernels for NVIDIA GPUs. It is
experimental: the kernels are checked as PTX, the code NVIDIA's driver compiles, but no kernel has yet
run on an NVIDIA GPU. Reports from someone who has one are welcome.

```xml
<PropertyGroup>
  <ZeroGpu>Cuda</ZeroGpu>
  <ZeroTarget>linux-x64</ZeroTarget>   <!-- or linux-arm64 -->
</PropertyGroup>
```

It needs:

- **clang 18 or later with the NVPTX backend**, which compiles the kernels to PTX. Linux
  distributions' clang has it (Ubuntu 24.04: `apt install clang`), and so do LLVM's own builds and
  Homebrew's `llvm`; Apple's clang doesn't. The build looks for `clang`, `clang-22` down to `clang-18`
  and Homebrew's, or takes the one in `<ZeroCudaClang>`.
- **NativeAOT-LLVM**, as for Metal: downloaded on the first build, and run in Docker on a Mac.
- Nothing from NVIDIA to build. The program opens the CUDA driver (`libcuda.so.1`) when it starts, so
  it runs on any Linux machine with NVIDIA's driver; on one without, `Gpu.Open()` returns false and
  says why. If `ptxas`, from the CUDA toolkit, is on the `PATH`, the build checks the PTX with it.

The kernels are PTX for `sm_70` (Volta, 2017) and later; `<ZeroCudaArch>` picks another. CUDA runs a
grid in whole blocks, so each kernel starts by ending the threads that fall outside it, as Metal never
starts them. Buffers are CUDA managed memory, which the CPU and GPU share, as with Metal.

## In this repository

The repository builds GPU programs without the SDK packages:

```bash
./build.sh --gpu Gpu       # bin/gpu/Gpu
./run.sh --gpu Gpu
./test.sh --gpu
./test.sh --gpu-air        # any machine: the kernels up to AIR, no Metal
./build.sh --cuda-ptx Gpu  # the kernels as PTX, checked by ptxas
./build.sh --cuda Gpu      # bin/cuda/Gpu, for Linux with an NVIDIA GPU
```

CI runs `--gpu-air`, since its runners have no Metal: it catches a change that the rewriter to AIR
doesn't understand, but nothing after it. It also builds `--cuda`, which checks the PTX with `ptxas`,
but no runner has an NVIDIA GPU to run it. Run `./test.sh --gpu` on a Mac before merging changes to
zerocore, `lib/` or the compiler.

`samples/Gpu.cs` has eleven kernels: adding, the Collatz sequence, SAXPY, a reduction, a plain and a
tiled matrix multiply, histograms, and float atomics. Each is checked against the CPU and timed
against a plain C# loop. [Targets](../concepts/targets.html#the-gpu-metal) explains how the build
works.

## What to watch out for

- **Small work isn't worth a launch.** A launch costs about 0.3 ms on an M2 Max, starting the work and
  waiting for it. Adding two arrays of a million floats takes the GPU about 35 microseconds, but the
  whole `Gpu.Run` 250 to 700, while one CPU core's plain loop takes about 600: no clear win. The GPU
  wins when each element has real work, like the matrix multiply or the Collatz sequence.
- **Kernel names are symbols.** The program exports each kernel under its name, so a name that a C
  function has (`write`, `exp`) clashes with it. Pick names of your own.
- **Buffers up to 2 GB.** The kernels are compiled for a 32-bit target first, so their offsets into a
  buffer are 32-bit.
- **Threadgroup memory is small**: 32 KB per threadgroup on Apple's GPUs.
- **A new Xcode can break the build**, since AIR isn't documented. The error then comes from `metal`.
