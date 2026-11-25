// What lib/Gpu.cs calls on Linux with an NVIDIA GPU: CUDA's driver API, behind
// the same small C API as gpu/metal.m. The kernels are PTX (zero-bitcode
// ll2ptx, then llc), embedded in the program as gpu_ptx (gpu/cuda-kernels.s);
// the driver compiles them for the GPU it finds when gpu_open loads them.
//
// libcuda is opened at run time (dlopen), so building needs no CUDA headers or
// libraries, and a program run where there is no NVIDIA driver says so and
// Gpu.Open returns false.
//
// Buffers are CUDA managed memory: one address for the CPU and the GPU, which
// the driver moves to whichever touches it. gpu_run waits for the kernel, so
// the CPU may read a buffer as soon as Gpu.Run returns, as with Metal.
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

extern const char gpu_ptx[];

typedef int CUresult;
typedef int CUdevice;
typedef struct CUctx* CUcontext;
typedef struct CUmod* CUmodule;
typedef struct CUfunc* CUfunction;
typedef struct CUevt* CUevent;
typedef struct CUstrm* CUstream;
typedef unsigned long long CUdeviceptr;

#define CU_MEM_ATTACH_GLOBAL 1
#define CU_FUNC_ATTRIBUTE_MAX_THREADS_PER_BLOCK 0

static struct {
    CUresult (*Init)(unsigned);
    CUresult (*DeviceGet)(CUdevice*, int);
    CUresult (*DeviceGetName)(char*, int, CUdevice);
    CUresult (*DevicePrimaryCtxRetain)(CUcontext*, CUdevice);
    CUresult (*CtxSetCurrent)(CUcontext);
    CUresult (*ModuleLoadData)(CUmodule*, const void*);
    CUresult (*ModuleGetFunction)(CUfunction*, CUmodule, const char*);
    CUresult (*FuncGetAttribute)(int*, int, CUfunction);
    CUresult (*MemAllocManaged)(CUdeviceptr*, size_t, unsigned);
    CUresult (*MemFree)(CUdeviceptr);
    CUresult (*LaunchKernel)(CUfunction, unsigned, unsigned, unsigned, unsigned, unsigned, unsigned,
                             unsigned, CUstream, void**, void**);
    CUresult (*CtxSynchronize)(void);
    CUresult (*EventCreate)(CUevent*, unsigned);
    CUresult (*EventRecord)(CUevent, CUstream);
    CUresult (*EventSynchronize)(CUevent);
    CUresult (*EventElapsedTime)(float*, CUevent, CUevent);
    CUresult (*GetErrorString)(CUresult, const char**);
} cu;

static CUdevice s_device;
static CUmodule s_module;
static CUevent s_start, s_end;

// A failed call: its name and CUDA's message on stderr, and 1.
static int failed(const char* call, CUresult result) {
    const char* message = "unknown error";
    if (cu.GetErrorString) cu.GetErrorString(result, &message);
    fprintf(stderr, "gpu: %s: %s (%d)\n", call, message, result);
    return 1;
}

#define CHECK(call) do { CUresult r_ = (call); if (r_ != 0) return failed(#call, r_); } while (0)

static int load(void) {
    void* lib = dlopen("libcuda.so.1", RTLD_NOW);
    if (!lib) {
        fprintf(stderr, "gpu: no NVIDIA driver (libcuda.so.1): %s\n", dlerror());
        return 1;
    }
#define SYM(field, name) if (!(*(void**)&cu.field = dlsym(lib, name))) { fprintf(stderr, "gpu: libcuda has no %s\n", name); return 1; }
    SYM(Init, "cuInit") SYM(DeviceGet, "cuDeviceGet") SYM(DeviceGetName, "cuDeviceGetName")
    SYM(DevicePrimaryCtxRetain, "cuDevicePrimaryCtxRetain") SYM(CtxSetCurrent, "cuCtxSetCurrent")
    SYM(ModuleLoadData, "cuModuleLoadData") SYM(ModuleGetFunction, "cuModuleGetFunction")
    SYM(FuncGetAttribute, "cuFuncGetAttribute") SYM(MemAllocManaged, "cuMemAllocManaged")
    SYM(MemFree, "cuMemFree_v2") SYM(LaunchKernel, "cuLaunchKernel") SYM(CtxSynchronize, "cuCtxSynchronize")
    SYM(EventCreate, "cuEventCreate") SYM(EventRecord, "cuEventRecord") SYM(EventSynchronize, "cuEventSynchronize")
    SYM(EventElapsedTime, "cuEventElapsedTime") SYM(GetErrorString, "cuGetErrorString")
#undef SYM
    return 0;
}

// 0 on success; otherwise the error is on stderr.
int32_t gpu_open(void) {
    if (load()) return 1;
    CUcontext context;
    CHECK(cu.Init(0));
    CHECK(cu.DeviceGet(&s_device, 0));
    CHECK(cu.DevicePrimaryCtxRetain(&context, s_device));
    CHECK(cu.CtxSetCurrent(context));
    CHECK(cu.ModuleLoadData(&s_module, gpu_ptx));
    CHECK(cu.EventCreate(&s_start, 0));
    CHECK(cu.EventCreate(&s_end, 0));
    return 0;
}

// The device's name as UTF-16; returns its length (at most capacity).
int32_t gpu_device_name(uint16_t* out, int32_t capacity) {
    char name[256] = "";
    if (cu.DeviceGetName) cu.DeviceGetName(name, (int)sizeof name, s_device);
    int32_t n = 0;
    for (; name[n] && n < capacity; n++) out[n] = (uint8_t)name[n];
    return n;
}

// A buffer the CPU and GPU share: managed memory, the handle is its address.
void* gpu_buffer_new(int64_t bytes, void** contents) {
    CUdeviceptr p = 0;
    CUresult r = cu.MemAllocManaged(&p, (size_t)(bytes > 0 ? bytes : 1), CU_MEM_ATTACH_GLOBAL);
    if (r != 0) { failed("cuMemAllocManaged", r); return 0; }
    *contents = (void*)(uintptr_t)p;
    return (void*)(uintptr_t)p;
}

void gpu_buffer_free(void* handle) {
    if (handle) cu.MemFree((CUdeviceptr)(uintptr_t)handle);
}

// One kernel argument, as lib/Gpu.cs's GpuArg lays it out: a buffer (kind 0,
// handle from gpu_buffer_new) or a scalar (kind 1, `size` bytes).
typedef struct {
    int32_t kind;
    int32_t size;
    void* handle;
    uint8_t bytes[8];
} gpu_arg;

#define MAX_KERNELS 64
static struct { char name[128]; CUfunction function; } s_kernels[MAX_KERNELS];

static CUfunction kernel(const char* name) {
    for (int i = 0; i < MAX_KERNELS && s_kernels[i].function; i++)
        if (strcmp(s_kernels[i].name, name) == 0) return s_kernels[i].function;
    CUfunction f;
    CUresult r = cu.ModuleGetFunction(&f, s_module, name);
    if (r != 0) { fprintf(stderr, "gpu: no kernel named %s\n", name); return 0; }
    for (int i = 0; i < MAX_KERNELS; i++)
        if (!s_kernels[i].function) { strcpy(s_kernels[i].name, name); s_kernels[i].function = f; break; }
    return f;
}

// Runs kernel `name` (UTF-16) on a width x height grid of threads, in blocks of
// group_width x group_height (0 x 0: as many as the kernel allows, in one row
// for a 1D grid, else 32 wide), argument i as the kernel's parameter i, with
// `shared_bytes` of dynamic shared memory, and waits for it. Returns 0 and the
// GPU's time in *milliseconds, or 1.
int32_t gpu_run(const uint16_t* name16, int32_t name_length, const gpu_arg* args, int32_t count,
                uint32_t width, uint32_t height, uint32_t group_width, uint32_t group_height,
                int32_t shared_bytes, double* milliseconds) {
    char name[128];
    if (name_length >= (int32_t)sizeof name) { fprintf(stderr, "gpu: kernel name too long\n"); return 1; }
    for (int32_t i = 0; i < name_length; i++) name[i] = (char)name16[i];
    name[name_length] = 0;
    CUfunction f = kernel(name);
    if (!f) return 1;
    if (height == 0) height = 1;
    int max = 1024;
    cu.FuncGetAttribute(&max, CU_FUNC_ATTRIBUTE_MAX_THREADS_PER_BLOCK, f);
    uint32_t bw = group_width, bh = group_height ? group_height : 1;
    if (group_width == 0 && height <= 1) { bw = width < (uint32_t)max ? width : (uint32_t)max; bh = 1; }
    else if (group_width == 0) { bw = 32; bh = (uint32_t)max / 32; }
    if (bw == 0) bw = 1;
    if ((int)(bw * bh) > max) {
        fprintf(stderr, "gpu: %s: blocks of %u x %u, at most %d threads\n", name, bw, bh, max);
        return 1;
    }
    // Parameters: the kernel's own, then the grid's width and height (Ptx.cs).
    void* params[34];
    CUdeviceptr pointers[32];
    if (count > 32) { fprintf(stderr, "gpu: %s: more than 32 arguments\n", name); return 1; }
    for (int32_t i = 0; i < count; i++) {
        if (args[i].kind == 0) {
            pointers[i] = (CUdeviceptr)(uintptr_t)args[i].handle;
            params[i] = &pointers[i];
        } else
            params[i] = (void*)args[i].bytes;
    }
    params[count] = &width;
    params[count + 1] = &height;
    CHECK(cu.EventRecord(s_start, 0));
    CHECK(cu.LaunchKernel(f, (width + bw - 1) / bw, (height + bh - 1) / bh, 1, bw, bh, 1,
                          (unsigned)(shared_bytes > 0 ? shared_bytes : 0), 0, params, 0));
    CHECK(cu.EventRecord(s_end, 0));
    CHECK(cu.EventSynchronize(s_end));
    CHECK(cu.CtxSynchronize());
    float ms = 0;
    CHECK(cu.EventElapsedTime(&ms, s_start, s_end));
    *milliseconds = ms;
    return 0;
}

// The built-ins when a kernel runs on the CPU, one thread at a time: the
// thread's position is whatever Gpu.SetThreadId last set, and each thread is a
// block of one (as in gpu/metal.m).
static _Thread_local uint32_t s_x, s_y, s_width = 1, s_height = 1;
static _Thread_local uint8_t s_shared[32768];
uint32_t gpu_thread_x(void) { return s_x; }
uint32_t gpu_thread_y(void) { return s_y; }
uint32_t gpu_local_x(void) { return 0; }
uint32_t gpu_local_y(void) { return 0; }
uint32_t gpu_group_x(void) { return s_x; }
uint32_t gpu_group_y(void) { return s_y; }
uint32_t gpu_group_w(void) { return 1; }
uint32_t gpu_group_h(void) { return 1; }
uint32_t gpu_grid_w(void) { return s_width; }
uint32_t gpu_grid_h(void) { return s_height; }
void gpu_barrier(void) {}
void* gpu_shared(void) { return s_shared; }
void gpu_set_thread_id(uint32_t x, uint32_t y, uint32_t width, uint32_t height) {
    s_x = x; s_y = y; s_width = width; s_height = height;
}

// The atomics on the CPU. Each returns the value the memory held before.
#define GPU_ATOMIC(op, builtin, name, T) \
    T gpu_atomic_##op##_##name(T* p, T v) { return builtin(p, v, __ATOMIC_RELAXED); }
#define GPU_ATOMIC_INTS(op, builtin) GPU_ATOMIC(op, builtin, i32, int32_t) GPU_ATOMIC(op, builtin, u32, uint32_t)
GPU_ATOMIC_INTS(add, __atomic_fetch_add)
GPU_ATOMIC_INTS(sub, __atomic_fetch_sub)
GPU_ATOMIC_INTS(and, __atomic_fetch_and)
GPU_ATOMIC_INTS(or, __atomic_fetch_or)
GPU_ATOMIC_INTS(xor, __atomic_fetch_xor)
GPU_ATOMIC_INTS(xchg, __atomic_exchange_n)
#define GPU_MINMAX(op, cmp, name, T) \
    T gpu_atomic_##op##_##name(T* p, T v) { \
        T old = __atomic_load_n(p, __ATOMIC_RELAXED); \
        while ((v cmp old) && !__atomic_compare_exchange_n(p, &old, v, 1, __ATOMIC_RELAXED, __ATOMIC_RELAXED)) {} \
        return old; \
    }
GPU_MINMAX(min, <, i32, int32_t) GPU_MINMAX(min, <, u32, uint32_t)
GPU_MINMAX(max, >, i32, int32_t) GPU_MINMAX(max, >, u32, uint32_t)
float gpu_atomic_xchg_f32(float* p, float v) { float old; __atomic_exchange(p, &v, &old, __ATOMIC_RELAXED); return old; }
float gpu_atomic_add_f32(float* p, float v) {
    float old, sum;
    __atomic_load(p, &old, __ATOMIC_RELAXED);
    do sum = old + v;
    while (!__atomic_compare_exchange(p, &old, &sum, 1, __ATOMIC_RELAXED, __ATOMIC_RELAXED));
    return old;
}
#define GPU_CAS(name, T) \
    T gpu_atomic_cas_##name(T* p, T value, T comparand) { \
        __atomic_compare_exchange_n(p, &comparand, value, 0, __ATOMIC_RELAXED, __ATOMIC_RELAXED); \
        return comparand; \
    }
GPU_CAS(i32, int32_t)
GPU_CAS(u32, uint32_t)
