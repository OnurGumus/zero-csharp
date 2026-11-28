// The native half of zerocore on WebAssembly (WASI).
//
// NativeAOT-LLVM normally links a full runtime: GC, exception handling, type
// loader. Here zerocore (C#) is the runtime: allocation, casts, interface
// calls and static fields all come from it, as on Linux. This file holds what
// can't be written in C#: the entry point, and a few functions whose calling
// convention differs from a C# method's. It's linked into every module by
// ./build.sh --wasm, together with WASI's C library.
//
// The detail that shapes it: managed code keeps some locals and arguments on a
// "shadow stack" in linear memory, because WASM's own stack can't be
// addressed. Native code entering managed code (main -> __managed__Main) hands
// it the shadow stack, and every C# method takes it as a hidden first argument.

#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <wasi/api.h>

#undef readdir64 // a macro for readdir here; defined below with glibc's layout

// ---- entry ----------------------------------------------------------------

int __managed__Main(int argc, char **argv);

int main(int argc, char **argv) { return __managed__Main(argc, argv); }

// ---- the clock ------------------------------------------------------------

// lib/Clock.cs and the samples call clock_gettime with Linux's clock numbers,
// 0 for the wall clock and 1 for the monotonic one, and expect two 64-bit
// numbers back. WASI's C library takes a pointer for the clock and writes a
// 32-bit tv_nsec, so this definition, linked before it, answers instead. WASI
// numbers its clocks as Linux does.
int clock_gettime(int clock, long long *timespec)
{
    __wasi_timestamp_t now;
    if (__wasi_clock_time_get(clock == 0 ? __WASI_CLOCKID_REALTIME : __WASI_CLOCKID_MONOTONIC, 1, &now) != 0)
        return -1;
    timespec[0] = (long long)(now / 1000000000);
    timespec[1] = (long long)(now % 1000000000);
    return 0;
}

// ---- the terminal --------------------------------------------------------

// Console.ReadKey (lib/InKeys.cs) asks for raw mode with these. WASI has no
// terminal to set (its C library has isatty and poll, not these), so they
// fail, and ReadKey reads the next character of the input instead.
int tcgetattr(int fd, void *termios)
{
    (void)fd;
    (void)termios;
    return -1;
}

int tcsetattr(int fd, int when, const void *termios)
{
    (void)fd;
    (void)when;
    (void)termios;
    return -1;
}

void cfmakeraw(void *termios) { (void)termios; }

// zerocore's startup (Runtime/Startup.cs) needs the module header to set up
// static fields of reference types. C# can't take a data symbol's address on
// WASM, so it asks for it here.
extern char __ReadyToRunHeader[];

void *ZeroGetModuleHeader(void *shadowStack)
{
    (void)shadowStack;
    return __ReadyToRunHeader;
}

// Catch.Run (zerocore/Runtime/Catch.cs). WebAssembly can't jump back up
// the stack without exception handling or JavaScript's help, so here the
// callback just runs, and ZeroCatchJump returns: RhpThrowEx goes on to print
// the exception and end the program, as with no Catch.Run at all.
void ZeroCatchCall(void *shadowStack, void **saved, void (*body)(void *, void *), void *argument)
{
    (void)saved;
    body(shadowStack, argument);
}

void ZeroCatchJump(void *shadowStack, void *saved) { (void)shadowStack; (void)saved; }

// ---- transitions between native and managed code --------------------------

static char shadow_stack[1 << 20] __attribute__((aligned(16)));

// Entering managed code from native code: hand out the shadow stack. Callbacks
// from native code back into managed code (nested reverse P/Invokes) aren't
// supported; zero-mode samples don't use them.
void *RhpReversePInvokeAndPushSparseVirtualUnwindFrame(void *frame, void *a, void *b)
{
    (void)frame;
    (void)a;
    (void)b;
    return shadow_stack;
}

void RhpReversePInvokeReturnAndPopSparseVirtualUnwindFrame(void *frame) { (void)frame; }

// A method with a catch or fault block registers itself here, so the
// exception dispatcher could find its handlers. `throw` never unwinds in zero
// mode (it prints and aborts, in zerocore's RhpThrowEx), so nothing reads it.
void RhpPushSparseVirtualUnwindFrame(void *frame, void *table, unsigned index)
{
    (void)frame;
    (void)table;
    (void)index;
}

void RhpPopSparseVirtualUnwindFrame(void) {}

// Managed code calling native code: nothing to do without a GC.
int RhpTrapThreads; // 0: never stop for a GC
void RhpPInvoke(void *frame) { (void)frame; }
void RhpPInvokeReturn(void *frame) { (void)frame; }

// ---- exceptions: zero mode has none, so these only report and stop ----------

int RhpExceptionThrown; // the flag NativeAOT-LLVM's emulated exceptions check after calls

int RhpHandleExceptionWasmCatch(void *a, void *b)
{
    (void)a;
    (void)b;
    fputs("zero mode: exception thrown\n", stderr);
    abort();
}

void RhpHandleUnhandledException(void *a, void *b)
{
    (void)a;
    (void)b;
    fputs("zero mode: unhandled exception\n", stderr);
    abort();
}

// ---- object references -------------------------------------------------------

// Storing an object reference: no GC, so a plain store. (On Linux these are
// zerocore/native/*.S.)
void RhpAssignRef(void **destination, void *reference) { *destination = reference; }
void RhpCheckedAssignRef(void **destination, void *reference) { *destination = reference; }

// Call sites of interface methods hold this function's address until their
// first call. Nothing calls it: compiled code asks zerocore for the method
// (RhpResolveInterfaceDispatch) instead.
void RhpInitialDynamicInterfaceDispatch(void) { abort(); }

// ---- files ----------------------------------------------------------------

// Which system this is, for lib/FileSystem.cs: 6, WebAssembly.
int ZeroSystem(void) { return 6; }

// lib/ArenaGuard.cs (Arena.GuardPages) asks for pages with mmap and for
// faults with sigaction. WebAssembly's memory is one block without
// protection, so mmap fails, and GuardPages stays off.
void *mmap(void *address, size_t length, int protection, int flags, int file, long long offset)
{
    (void)address; (void)length; (void)protection; (void)flags; (void)file; (void)offset;
    return (void *)-1;
}

int sigaction(int signal, const void *action, void *previous)
{
    (void)signal; (void)action; (void)previous;
    return -1;
}

// ZeroRootCertificate, for lib/CryptoX509Chain.cs: the system's trusted root
// certificates one at a time. Only Windows keeps them where this can ask
// (win/runtime.c); elsewhere there are none here, and X509Chain reads a PEM
// bundle instead.
const void *ZeroRootCertificate(const void *previous, const unsigned char **data, int *length)
{
    (void)previous; (void)data; (void)length;
    return 0;
}

// lib/FileSystem.cs calls glibc's names, with glibc's layouts: these give
// WASI's answers in them. glibc's struct stat as on arm64: the mode at 16,
// the size at 48, the modification time at 88 (seconds) and 96 (nanoseconds),
// with Linux's numbers for the file types. (errno keeps WASI's numbers, which
// lib/FileSystem.cs knows; WASI's C library has __errno_location.)
static int to_glibc_stat(int result, const struct stat *st, unsigned char *out)
{
    if (result != 0)
        return result;
    unsigned mode = S_ISDIR(st->st_mode) ? 0x4000 : S_ISLNK(st->st_mode) ? 0xA000
                  : S_ISREG(st->st_mode) ? 0x8000 : 0;
    long long size = st->st_size, seconds = st->st_mtim.tv_sec, nanoseconds = st->st_mtim.tv_nsec;
    memset(out, 0, 128);
    memcpy(out + 16, &mode, 4);
    memcpy(out + 48, &size, 8);
    memcpy(out + 88, &seconds, 8);
    memcpy(out + 96, &nanoseconds, 8);
    return 0;
}

int __xstat64(int version, const char *path, unsigned char *out)
{
    (void)version;
    struct stat st;
    return to_glibc_stat(stat(path, &st), &st, out);
}

int __lxstat64(int version, const char *path, unsigned char *out)
{
    (void)version;
    struct stat st;
    return to_glibc_stat(lstat(path, &st), &st, out);
}

// glibc's struct dirent64: d_type at 18, with Linux's numbers, and d_name
// from 19. Good until the next readdir64, as readdir's entry is.
struct glibc_dirent64 {
    unsigned long long d_ino;
    long long d_off;
    unsigned short d_reclen;
    unsigned char d_type;
    char d_name[1024];
};

void *readdir64(DIR *dir)
{
    static struct glibc_dirent64 out;
    struct dirent *entry = readdir(dir);
    if (!entry)
        return 0;
    out.d_type = entry->d_type == DT_DIR ? 4 : entry->d_type == DT_REG ? 8
               : entry->d_type == DT_LNK ? 10 : 0;
    size_t length = strlen(entry->d_name);
    if (length > 1023)
        length = 1023;
    memcpy(out.d_name, entry->d_name, length);
    out.d_name[length] = 0;
    return &out;
}

// No pipes (lib/FileAsync.cs then reads and writes files without its I/O
// thread, blocking, as there are no threads either).
int pipe(int fds[2])
{
    (void)fds;
    return -1;
}
