// GPU compute from C#, on a Mac with Apple silicon (Metal): buffers the CPU
// and GPU share, launching kernels, and what kernels call (built-ins, barriers,
// shared memory, atomics).
//
// A kernel is a static method written in ordinary C# and marked
// [RuntimeExport("Name")] (System.Runtime), Name being what Gpu.Run calls it:
// pointer parameters are buffers, other parameters values passed with the
// launch. The GPU build (./build.sh --gpu, or the SDK's ZeroGpu) compiles those
// methods a second time, for the GPU (zero-bitcode ll2air,
// sdk/ZeroCSharp.Bitcode/Air.cs: what a kernel may do), and links the program
// with gpu/metal.m. Inside a kernel the Gpu.* calls below are the GPU's
// built-ins; on the CPU, gpu/metal.m stands in for them one thread at a time.
//
//     [RuntimeExport("Scale")]
//     static unsafe void Scale(float* x, float factor)
//     {
//         unsafe { x[Gpu.ThreadId()] *= factor; }
//     }
//
//     var x = GpuBuffer<float>.Allocate(n);
//     Gpu.Run("Scale", (uint)n, x, 2.5f);
//
// Only a GPU build links the functions these call; other targets can compile
// against this file but not use it.

using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// A buffer the CPU and the GPU share (on Apple silicon they share memory).
public readonly struct GpuBuffer<T> where T : unmanaged
{
    public readonly nint Handle;
    public readonly T* Pointer;
    public readonly int Length;

    GpuBuffer(nint handle, T* pointer, int length) { Handle = handle; Pointer = pointer; Length = length; }

    public static GpuBuffer<T> Allocate(int length)
    {
        void* contents;
        nint handle;
        unsafe { handle = Gpu.gpu_buffer_new((long)length * sizeof(T), &contents); }
        if (handle == 0) Environment.FailFast("GpuBuffer: out of GPU memory");
        return new GpuBuffer<T>(handle, (T*)contents, length);
    }

    public Span<T> Span
    {
        get { unsafe { return new Span<T>(Pointer, Length); } } // SAFETY: the buffer holds Length elements.
    }

    // The buffer as a tensor of these lengths (row-major: the last index
    // moves fastest), to fill or read with the Tensor methods. It is a view,
    // not a copy: the GPU sees what the tensor writes, and the tensor what
    // the GPU writes, once Gpu.Run returns. The lengths must multiply to at
    // most Length.
    public TensorSpan<T> AsTensorSpan(params scoped ReadOnlySpan<nint> lengths) => new TensorSpan<T>(Span, lengths);

    public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<nint> lengths) =>
        new ReadOnlyTensorSpan<T>(Span, lengths);

    public void Free() => Gpu.gpu_buffer_free(Handle);

    public static implicit operator GpuArg(GpuBuffer<T> buffer) => GpuArg.Buffer(buffer.Handle);
}

// One argument of a launch: a buffer or a value. Laid out as gpu/metal.m's gpu_arg.
[StructLayout(LayoutKind.Sequential)]
public readonly struct GpuArg
{
    readonly int _kind; // 0 buffer, 1 value
    readonly int _size;
    readonly nint _handle;
    readonly long _bytes;

    GpuArg(int kind, int size, nint handle, long bytes) { _kind = kind; _size = size; _handle = handle; _bytes = bytes; }

    public static GpuArg Buffer(nint handle) => new(0, 0, handle, 0);

    public static implicit operator GpuArg(int value) => new(1, 4, 0, (uint)value);
    public static implicit operator GpuArg(uint value) => new(1, 4, 0, value);
    public static implicit operator GpuArg(long value) => new(1, 8, 0, value);
    public static implicit operator GpuArg(float value)
    {
        unsafe { return new(1, 4, 0, (uint)*(int*)&value); } // SAFETY: reads the float's 4 bytes.
    }
}

// How to launch a kernel: a grid of threads (width x height; 1D: height 1),
// in threadgroups of groupWidth x groupHeight (0: as many as the kernel
// allows), with SharedBytes of threadgroup memory per threadgroup.
public readonly struct GpuLaunch
{
    public readonly uint Width, Height, GroupWidth, GroupHeight;
    public readonly int SharedBytes;

    public GpuLaunch(uint threads, uint groupSize = 0, int sharedBytes = 0)
        : this(threads, 1, groupSize, groupSize == 0 ? 0u : 1u, sharedBytes) { }

    GpuLaunch(uint width, uint height, uint groupWidth, uint groupHeight, int sharedBytes)
    {
        Width = width; Height = height; GroupWidth = groupWidth; GroupHeight = groupHeight; SharedBytes = sharedBytes;
    }

    public static GpuLaunch Grid(uint width, uint height, uint groupWidth = 0, uint groupHeight = 0, int sharedBytes = 0) =>
        new(width, height, groupWidth, groupHeight, sharedBytes);
}

public static class Gpu
{
    // In a kernel, these are the GPU's built-ins (gpu/ll2air.py replaces the calls):
    // the thread's position in the grid, in its threadgroup, the threadgroup's
    // position, and the sizes. ThreadId and friends are the X of a 1D launch.
    [DllImport("libc", EntryPoint = "gpu_thread_x"), SuppressGCTransition] public static safe extern uint ThreadId();
    [DllImport("libc", EntryPoint = "gpu_thread_x"), SuppressGCTransition] public static safe extern uint ThreadX();
    [DllImport("libc", EntryPoint = "gpu_thread_y"), SuppressGCTransition] public static safe extern uint ThreadY();
    [DllImport("libc", EntryPoint = "gpu_local_x"), SuppressGCTransition] public static safe extern uint LocalId();
    [DllImport("libc", EntryPoint = "gpu_local_x"), SuppressGCTransition] public static safe extern uint LocalX();
    [DllImport("libc", EntryPoint = "gpu_local_y"), SuppressGCTransition] public static safe extern uint LocalY();
    [DllImport("libc", EntryPoint = "gpu_group_x"), SuppressGCTransition] public static safe extern uint GroupId();
    [DllImport("libc", EntryPoint = "gpu_group_x"), SuppressGCTransition] public static safe extern uint GroupX();
    [DllImport("libc", EntryPoint = "gpu_group_y"), SuppressGCTransition] public static safe extern uint GroupY();
    [DllImport("libc", EntryPoint = "gpu_group_w"), SuppressGCTransition] public static safe extern uint GroupSize();
    [DllImport("libc", EntryPoint = "gpu_group_w"), SuppressGCTransition] public static safe extern uint GroupWidth();
    [DllImport("libc", EntryPoint = "gpu_group_h"), SuppressGCTransition] public static safe extern uint GroupHeight();
    [DllImport("libc", EntryPoint = "gpu_grid_w"), SuppressGCTransition] public static safe extern uint GridWidth();
    [DllImport("libc", EntryPoint = "gpu_grid_h"), SuppressGCTransition] public static safe extern uint GridHeight();
    [DllImport("libc", EntryPoint = "gpu_barrier"), SuppressGCTransition] public static safe extern void Barrier();
    [DllImport("libc", EntryPoint = "gpu_shared"), SuppressGCTransition] public static unsafe extern void* Shared();

    // Atomics on device or threadgroup memory (relaxed; the scope follows the
    // memory). Each returns the value the memory held before.
    [DllImport("libc", EntryPoint = "gpu_atomic_add_i32"), SuppressGCTransition] public static unsafe extern int AtomicAdd(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_add_u32"), SuppressGCTransition] public static unsafe extern uint AtomicAdd(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_add_f32"), SuppressGCTransition] public static unsafe extern float AtomicAdd(float* location, float value);
    [DllImport("libc", EntryPoint = "gpu_atomic_sub_i32"), SuppressGCTransition] public static unsafe extern int AtomicSub(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_sub_u32"), SuppressGCTransition] public static unsafe extern uint AtomicSub(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_min_i32"), SuppressGCTransition] public static unsafe extern int AtomicMin(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_min_u32"), SuppressGCTransition] public static unsafe extern uint AtomicMin(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_max_i32"), SuppressGCTransition] public static unsafe extern int AtomicMax(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_max_u32"), SuppressGCTransition] public static unsafe extern uint AtomicMax(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_and_i32"), SuppressGCTransition] public static unsafe extern int AtomicAnd(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_and_u32"), SuppressGCTransition] public static unsafe extern uint AtomicAnd(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_or_i32"), SuppressGCTransition] public static unsafe extern int AtomicOr(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_or_u32"), SuppressGCTransition] public static unsafe extern uint AtomicOr(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_xor_i32"), SuppressGCTransition] public static unsafe extern int AtomicXor(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_xor_u32"), SuppressGCTransition] public static unsafe extern uint AtomicXor(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_xchg_i32"), SuppressGCTransition] public static unsafe extern int Exchange(int* location, int value);
    [DllImport("libc", EntryPoint = "gpu_atomic_xchg_u32"), SuppressGCTransition] public static unsafe extern uint Exchange(uint* location, uint value);
    [DllImport("libc", EntryPoint = "gpu_atomic_xchg_f32"), SuppressGCTransition] public static unsafe extern float Exchange(float* location, float value);
    // Stores value if the memory holds comparand, like Interlocked.CompareExchange.
    [DllImport("libc", EntryPoint = "gpu_atomic_cas_i32"), SuppressGCTransition] public static unsafe extern int CompareExchange(int* location, int value, int comparand);
    [DllImport("libc", EntryPoint = "gpu_atomic_cas_u32"), SuppressGCTransition] public static unsafe extern uint CompareExchange(uint* location, uint value, uint comparand);

    [DllImport("libc"), SuppressGCTransition] static safe extern void gpu_set_thread_id(uint x, uint y, uint width, uint height);
    [DllImport("libc")] static safe extern int gpu_open();
    [DllImport("libc")] static unsafe extern int gpu_device_name(char* buffer, int capacity);
    [DllImport("libc")] internal static unsafe extern nint gpu_buffer_new(long bytes, void** contents);
    [DllImport("libc")] internal static safe extern void gpu_buffer_free(nint handle);
    [DllImport("libc")]
    static unsafe extern int gpu_run(char* name, int length, GpuArg* args, int count,
        uint width, uint height, uint groupWidth, uint groupHeight, int sharedBytes, double* milliseconds);

    public static bool Open() => gpu_open() == 0;

    public static ReadOnlySpan<char> DeviceName(Span<char> buffer)
    {
        int length;
        unsafe { fixed (char* p = buffer) length = gpu_device_name(p, buffer.Length); }
        return buffer.Slice(0, length);
    }

    // Runs the kernel (a method of Kernels, by name) and waits for it; argument i
    // goes to the kernel's parameter i. Returns the GPU's time in milliseconds.
    public static double Run(string kernel, uint threads, params ReadOnlySpan<GpuArg> args) =>
        Run(kernel, new GpuLaunch(threads), args);

    public static double Run(string kernel, GpuLaunch launch, params ReadOnlySpan<GpuArg> args)
    {
        double milliseconds = 0;
        int status;
        unsafe
        {
            fixed (char* name = kernel)
            fixed (GpuArg* p = args)
                status = gpu_run(name, kernel.Length, p, args.Length, launch.Width, launch.Height,
                    launch.GroupWidth, launch.GroupHeight, launch.SharedBytes, &milliseconds);
        }
        if (status != 0) Environment.FailFast("Gpu.Run failed");
        return milliseconds;
    }

    // For running a kernel on the CPU: what the built-ins return next.
    public static void SetThreadId(uint x) => gpu_set_thread_id(x, 0, 0, 1);
    public static void SetThreadId(uint x, uint y, uint width, uint height) => gpu_set_thread_id(x, y, width, height);
}
