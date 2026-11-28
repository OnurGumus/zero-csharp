// The native half of zerocore on Cortex-M microcontrollers (bare metal).
//
// The C# code is NativeAOT-LLVM's WebAssembly bitcode, compiled by clang for
// a Cortex-M, as for DOS (dos/runtime.c): 32-bit little-endian on both sides,
// so the bitcode needs no rewriting. Managed code passes its "shadow stack"
// as a hidden first argument and expects the same few native functions as on
// WebAssembly (wasm/runtime.c).
//
// The C library is Arm Toolchain for Embedded's picolibc: its startup code
// copies .data from flash, clears .bss, calls main and passes main's result
// to exit. This file is what every Cortex-M program has. The rest depends on
// where it runs, and is linked in from one of:
//
//   arm/semihost.c  in QEMU (or with a debugger attached): input, output,
//                   files, the clock and the exit code go to the host
//   arm/rp2350.c    on a real Raspberry Pi Pico 2: its clocks, a UART
//                   console and a timer
//
// Each defines zero_start, which main calls first.

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <unistd.h>

// picolibc has no roundeven. rint rounds the same way: to nearest, ties to
// even, the mode a program starts in and never leaves.
double roundeven(double x) { return __builtin_rint(x); }
float roundevenf(float x) { return __builtin_rintf(x); }

static void fatal(const char *message)
{
    write(2, message, strlen(message));
    abort();
}

// ---- the terminal ---------------------------------------------------------------
//
// Console.ReadKey and KeyAvailable (lib/InKeys.cs) ask for raw mode with
// these. A board's standard input is a semihosting file or console, not a
// terminal, so ReadKey reads the next character of it. Weak, in case the C
// library has some of them.

struct pollfd {
    int fd;
    short events, revents;
};

__attribute__((weak)) int isatty(int fd)
{
    (void)fd;
    return 0;
}

__attribute__((weak)) int tcgetattr(int fd, void *termios)
{
    (void)fd;
    (void)termios;
    return -1;
}

__attribute__((weak)) int tcsetattr(int fd, int when, const void *termios)
{
    (void)fd;
    (void)when;
    (void)termios;
    return -1;
}

__attribute__((weak)) void cfmakeraw(void *termios) { (void)termios; }

// Standard input can always be read: there is a next character, or the end.
__attribute__((weak)) int poll(struct pollfd *fds, unsigned long count, int timeout)
{
    (void)timeout;
    for (unsigned long i = 0; i < count; i++)
        fds[i].revents = fds[i].events & 1;
    return (int)count;
}

// ---- threads: there are none (see dos/runtime.c) --------------------------------

#define MAX_KEYS 8
static void *key_values[MAX_KEYS];
static unsigned key_count;

int pthread_create(void *thread, void *attributes, void *(*start)(void *), void *argument)
{
    (void)thread; (void)attributes; (void)start; (void)argument;
    return 38; // ENOSYS
}

// No pipes (lib/FileAsync.cs then reads and writes files without its I/O
// thread, blocking, as there are no threads either).
int pipe(int fds[2])
{
    (void)fds;
    return -1;
}

int sched_yield(void) { return 0; }

int pthread_join(void *thread, void **result)
{
    (void)thread; (void)result;
    return 3; // ESRCH
}

int pthread_key_create(unsigned *key, void (*destructor)(void *))
{
    (void)destructor;
    if (key_count == MAX_KEYS)
        return 11; // EAGAIN
    *key = key_count++;
    return 0;
}

void *pthread_getspecific(unsigned key) { return key < key_count ? key_values[key] : 0; }

int pthread_setspecific(unsigned key, void *value)
{
    if (key >= key_count)
        return 22; // EINVAL
    key_values[key] = value;
    return 0;
}

int get_nprocs(void) { return 1; }

// ---- random bytes: none to give (lib/CryptoRandom.cs throws) -------------------

int getentropy(void *buffer, size_t length) { (void)buffer; (void)length; return -1; }

// ZeroRootCertificate, for lib/CryptoX509Chain.cs: the system's trusted root
// certificates one at a time. Only Windows keeps them where this can ask
// (win/runtime.c); elsewhere there are none here, and X509Chain reads a PEM
// bundle instead.
const void *ZeroRootCertificate(const void *previous, const unsigned char **data, int *length)
{
    (void)previous; (void)data; (void)length;
    return 0;
}

// Directories, for lib/Directory.cs: there are none to list here, so opendir
// finds nothing. (X509Chain's search for the system's root certificates looks
// for Android's directory of them and links these in.)
void *opendir(const char *path) { (void)path; return 0; }
void *readdir64(void *dir) { (void)dir; return 0; }
int closedir(void *dir) { (void)dir; return -1; }

// ---- flash is read-only ----------------------------------------------------------
//
// String literals stay in flash (zero-bitcode wasm2arm puts them in .rodata).
// Nothing may write to them, and on a real chip a write to flash is lost or
// faults. In QEMU the "flash" is RAM, though, so a write would go unnoticed.
// The memory protection unit makes it fault in both places: one region, the
// whole of flash, read-only; everything else keeps its default rights.

extern char __boot_flash[], __flash[], __flash_size[];

#define MPU_TYPE (*(volatile uint32_t *)0xE000ED90)
#define MPU_CTRL (*(volatile uint32_t *)0xE000ED94)
#define MPU_RNR (*(volatile uint32_t *)0xE000ED98)
#define MPU_RBAR (*(volatile uint32_t *)0xE000ED9C)
#define MPU_CTRL_ENABLE 1u
#define MPU_CTRL_PRIVDEFENA 4u // the default memory map where no region is

static void protect_flash(void)
{
    uint32_t start = (uint32_t)__boot_flash;
    uint32_t end = (uint32_t)__flash + (uint32_t)__flash_size; // just past it
    if (((MPU_TYPE >> 8) & 0xFF) == 0)
        return; // no MPU
    MPU_CTRL = 0;
    MPU_RNR = 0;
#if defined(__ARM_ARCH_8M_MAIN__) || defined(__ARM_ARCH_8_1M_MAIN__)
    // ARMv8-M (Cortex-M33): a region is a start and a last address, 32 bytes apart.
    // Attribute 0: normal memory, write-through, as flash is.
    *(volatile uint32_t *)0xE000EDC0 = 0xAA;                  // MPU_MAIR0
    MPU_RBAR = (start & ~31u) | (3u << 1);                      // AP = read-only
    *(volatile uint32_t *)0xE000EDA0 = ((end - 1) & ~31u) | 1u; // MPU_RLAR: attribute 0, enabled
#else
    // ARMv7-M (Cortex-M4): a region is a power of two in size, aligned to it.
    uint32_t log2 = 5;
    while (log2 < 32 && (1u << log2) < end - start)
        log2++;
    if (start & ((1u << log2) - 1))
        return;
    MPU_RBAR = start;
    // AP = 110: read-only; C: write-through; SIZE = log2 - 1; enabled.
    *(volatile uint32_t *)0xE000EDA0 = (6u << 24) | (1u << 17) | ((log2 - 1) << 1) | 1u; // MPU_RASR
#endif
    MPU_CTRL = MPU_CTRL_ENABLE | MPU_CTRL_PRIVDEFENA;
    __asm__ volatile("dsb\n\tisb" ::: "memory");
}

// ---- the hardware, for lib/Board.cs ---------------------------------------------
//
// C# reaches the chip's registers through these, not through its own pointer
// reads and writes: the compiler may combine, reorder or hoist those out of a
// loop (on the C64 a polling loop stopped reading), and here each access is
// exactly one load or store, where it's written.

uint32_t zero_read32(uintptr_t address) { return *(volatile uint32_t *)address; }
void zero_write32(uintptr_t address, uint32_t value) { *(volatile uint32_t *)address = value; }

void zero_wait_for_interrupt(void) { __asm__ volatile("wfi" ::: "memory"); }

// SysTick, the timer every Cortex-M has: an interrupt hz times a second,
// counted from the processor's clock (zero_cpu_hz, in semihost.c and
// rp2350.c), which calls a C# method ([UnmanagedCallersOnly]).
#define SYST_CSR (*(volatile uint32_t *)0xE000E010)
#define SYST_RVR (*(volatile uint32_t *)0xE000E014)
#define SYST_CVR (*(volatile uint32_t *)0xE000E018)

uint32_t zero_cpu_hz(void);
static void (*volatile tick_handler)(void);

void arm_systick_isr(void)
{
    void (*handler)(void) = tick_handler;
    if (handler)
        handler();
}

// 0, or -1 if hz is too slow or too fast for the timer's 24 bits.
int zero_tick_start(int32_t hz, void (*handler)(void))
{
    if (hz <= 0)
        return -1;
    uint32_t reload = zero_cpu_hz() / (uint32_t)hz;
    if (reload < 2 || reload > (1u << 24))
        return -1;
    SYST_CSR = 0;
    tick_handler = handler;
    SYST_RVR = reload - 1;
    SYST_CVR = 0;
    SYST_CSR = 7; // enabled, interrupting, from the processor's clock
    return 0;
}

void zero_tick_stop(void)
{
    SYST_CSR = 0;
    tick_handler = 0;
}

// ---- entry -------------------------------------------------------------------

int __managed__Main(int argc, char **argv);

// Sets up the machine and may change the arguments (semihost.c, rp2350.c).
void zero_start(int *argc, char ***argv);

// picolibc's vector table: where the processor finds each interrupt's
// handler (arm_systick_isr among them).
extern char __interrupt_vector[];
#define SCB_VTOR (*(volatile uint32_t *)0xE000ED08)

int main(int argc, char **argv)
{
    // The processor looks for interrupt handlers where VTOR points, which
    // isn't necessarily here: the RP2350's boot ROM leaves it at the start of
    // RAM, and an interrupt would then jump to an address read from data.
    SCB_VTOR = (uint32_t)__interrupt_vector;
    __asm__ volatile("dsb\n\tisb" ::: "memory");
    zero_start(&argc, &argv);
    protect_flash();
    return __managed__Main(argc, argv);
}

extern char __ReadyToRunHeader[];

void *ZeroGetModuleHeader(void *shadowStack)
{
    (void)shadowStack;
    return __ReadyToRunHeader;
}

// ---- transitions between native and managed code (see wasm/runtime.c) --------

// Managed code's locals that hold references live here, not on the machine
// stack. 16 KB of the RP2350's 520 KB by default; build.sh sets less for
// smaller chips.
#ifndef SHADOW_STACK_SIZE
#define SHADOW_STACK_SIZE (16 << 10)
#endif
static char shadow_stack[SHADOW_STACK_SIZE] __attribute__((aligned(16)));

// An interrupt handler written in C# needs one of its own: it can interrupt
// managed code anywhere, and must leave that code's frames alone. Handlers
// don't interrupt each other (they all have the same priority), so one is
// enough. A quarter of the other.
static char interrupt_shadow_stack[SHADOW_STACK_SIZE / 4] __attribute__((aligned(16)));

// Where C calls into C#: from main, or from an interrupt (IPSR holds the
// number of the exception being handled, 0 for none). A fiber starts
// differently: ZeroFiberStart below passes its entry its shadow stack.
static void *entry_shadow_stack(void)
{
    uint32_t exception;
    __asm__ volatile("mrs %0, ipsr" : "=r"(exception));
    return exception ? interrupt_shadow_stack : shadow_stack;
}

// The program's entry point, which has a catch or fault block to register.
void *RhpReversePInvokeAndPushSparseVirtualUnwindFrame(void *frame, void *a, void *b)
{
    (void)frame; (void)a; (void)b;
    return entry_shadow_stack();
}

void RhpReversePInvokeReturnAndPopSparseVirtualUnwindFrame(void *frame) { (void)frame; }

// Any other [UnmanagedCallersOnly] method, such as an interrupt handler.
void *RhpReversePInvoke(int32_t unused)
{
    (void)unused;
    return entry_shadow_stack();
}

void RhpReversePInvokeReturn(void *frame) { (void)frame; }

void RhpPushSparseVirtualUnwindFrame(void *frame, void *table, unsigned index)
{
    (void)frame; (void)table; (void)index;
}

void RhpPopSparseVirtualUnwindFrame(void) {}

int RhpTrapThreads;
void RhpPInvoke(void *frame) { (void)frame; }
void RhpPInvokeReturn(void *frame) { (void)frame; }

int RhpExceptionThrown;

int RhpHandleExceptionWasmCatch(void *a, void *b)
{
    (void)a; (void)b;
    fatal("zero mode: exception thrown\n");
    return 0;
}

void RhpHandleUnhandledException(void *a, void *b)
{
    (void)a; (void)b;
    fatal("zero mode: unhandled exception\n");
}

void RhpAssignRef(void **destination, void *reference) { *destination = reference; }
void RhpCheckedAssignRef(void **destination, void *reference) { *destination = reference; }
void RhpInitialDynamicInterfaceDispatch(void) { abort(); }

// ---- fibers (lib/Fibers.cs) ------------------------------------------------------
//
// Switching from one stack to another: save the registers a function must
// keep (r4-r11, s16-s31), change stacks, restore them. On ARMv8-M (the Pico
// 2's Cortex-M33) the switch also swaps MSPLIM, the lowest address sp may
// reach, so the hardware stops a fiber that runs past its stack (a UsageFault)
// as it would the main stack.
//
// A fiber's memory is [guard][pattern][shadow stack ->   <- stack][record]:
// managed code keeps its references on a shadow stack (see above), so each
// fiber needs one of its own, and gets the bottom quarter of its stack. It
// grows up, the machine stack down. Fibers.cs writes a pattern below both and
// checks it on every switch back; MSPLIM stops the machine stack at the top
// of the shadow stack. Nothing stops the shadow stack growing into the
// machine stack.
//
// There's no memory protection to make a guard page with (an MPU has too few
// regions for one per fiber), so getpagesize reports 1 KB: Fibers.cs reserves
// that much below each stack, which an overflow lands in before anything
// else, and sizes stacks for a small machine. mprotect does nothing.
//
// The frame a switch leaves, from the saved sp up (104 bytes):
//   0   s16-s31
//   64  the stack limit (MSPLIM; unused on ARMv7-M)
//   68  r4-r11
//   100 lr: where the switch returns to
// An interrupt arriving while a fiber runs uses the fiber's stack for its
// own frame and its handler's: leave room for that.

#if !defined(__ARM_FP)
#error "the fiber switch saves s16-s31: build with a hardware FPU"
#endif

int getpagesize(void) { return 1024; }

int mprotect(void *address, size_t length, int protection)
{
    (void)address; (void)length; (void)protection;
    return 0;
}

int usleep(unsigned microseconds)
{
    struct { int64_t seconds, nanoseconds; } now;
    extern int clock_gettime(int clock, void *timespec);
    clock_gettime(1, &now);
    int64_t until = now.seconds * 1000000 + now.nanoseconds / 1000 + microseconds;
    do
        clock_gettime(1, &now);
    while (now.seconds * 1000000 + now.nanoseconds / 1000 < until);
    return 0;
}

// Catch.Run (zerocore/Runtime/Catch.cs): a throw inside body jumps back
// here through ZeroCatchJump, and ZeroCatchCall returns as if body had.
// picolibc's setjmp saves what a function must keep for its caller. Managed
// code passes its shadow stack first (see wasm/runtime.c); body gets the
// caller's.
//
// An interrupt handler written in C# runs on top of the code it interrupted
// and finds that code's marks. A throw in the handler mustn't jump to one:
// the processor would be left inside the interrupt, with later ones never
// served. So a mark remembers which interrupt it was made in (IPSR: 0 for
// none), and ZeroCatchJump returns when it's called from another: the throw
// then ends the program with its message, as it does with no Catch.Run. A
// handler's own Catch.Run works.
#include <setjmp.h>

struct catch_mark
{
    jmp_buf buffer;
    uint32_t exception;
};

static uint32_t current_exception(void)
{
    uint32_t exception;
    __asm__ volatile("mrs %0, ipsr" : "=r"(exception));
    return exception;
}

void ZeroCatchCall(void *shadowStack, void **saved, void (*body)(void *, void *), void *argument)
{
    struct catch_mark mark;
    mark.exception = current_exception();
    *saved = &mark;
    if (setjmp(mark.buffer) == 0)
        body(shadowStack, argument);
}

void ZeroCatchJump(void *shadowStack, void *saved)
{
    struct catch_mark *mark = saved;
    (void)shadowStack;
    if (mark->exception == current_exception())
        longjmp(mark->buffer, 1);
}

// r0: where to store this stack's pointer; r1: the stack pointer to resume.
__attribute__((naked)) void ZeroFiberSwitch(void **save, void *resume)
{
    __asm__ volatile(
        "push    {r4-r11, lr}\n"
#if __ARM_ARCH_8M_MAIN__
        "mrs     r2, msplim\n"
#endif
        "push    {r2}\n"
        "vpush   {s16-s31}\n"
        "mov     r2, sp\n"
        "str     r2, [r0]\n"
#if __ARM_ARCH_8M_MAIN__
        // No limit while sp moves: the new stack may be below the old one's.
        "movs    r2, #0\n"
        "msr     msplim, r2\n"
#endif
        "mov     sp, r1\n"
        "vpop    {s16-s31}\n"
        "pop     {r2}\n"
#if __ARM_ARCH_8M_MAIN__
        "msr     msplim, r2\n"
#endif
        "pop     {r4-r11, pc}\n");
}

// Where a new fiber's first switch returns to: r4 its argument, r5 its entry,
// r6 its shadow stack. The entry is an ordinary C# method, which takes its
// shadow stack as a hidden first argument. It never returns: it switches
// away for good.
__attribute__((naked)) static void ZeroFiberStart(void)
{
    __asm__ volatile(
        "mov     r0, r6\n"
        "mov     r1, r4\n"
        "blx     r5\n"
        "udf     #0\n");
}

// top: the stack's top; bottom: its lowest byte, where Fibers.cs's pattern
// (64 bytes) starts. Returns the stack pointer to give ZeroFiberSwitch.
void *ZeroFiberInit(char *top, char *bottom, void (*entry)(void *), void *argument)
{
    uintptr_t stack_top = (uintptr_t)top & ~(uintptr_t)7;
    uint32_t *frame = (uint32_t *)(stack_top - 104);
    char *shadow = bottom + 64;
    uintptr_t shadow_size = ((stack_top - (uintptr_t)shadow) / 4) & ~(uintptr_t)7;
    memset(frame, 0, 104);
    frame[16] = (uint32_t)(uintptr_t)(shadow + shadow_size); // the stack limit
    frame[17] = (uint32_t)(uintptr_t)argument;               // r4
    frame[18] = (uint32_t)(uintptr_t)entry;                  // r5
    frame[19] = (uint32_t)(uintptr_t)shadow;                 // r6
    frame[25] = (uint32_t)(uintptr_t)ZeroFiberStart;         // lr (a Thumb address)
    return frame;
}

// ---- lib/FileSystem.cs ----------------------------------------------------

// Which system this is: 8, a microcontroller.
int ZeroSystem(void) { return 8; }

// File.Exists calls stat only on Linux, macOS, Windows and WebAssembly; here
// it opens the file instead. These are there for the link, and always fail.
static int file_system_errno;
int *__errno_location(void) { return &file_system_errno; }
int __xstat64(int version, const char *path, void *out) { (void)version; (void)path; (void)out; file_system_errno = 38; return -1; }
int __lxstat64(int version, const char *path, void *out) { (void)version; (void)path; (void)out; file_system_errno = 38; return -1; }
