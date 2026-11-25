// The C side of a 64-bit Windows program or native library: the POSIX
// functions zerocore and lib/ call that Windows doesn't have under those names
// (write, read, clock_gettime, fseeko and ftello, the pthreads lib/Threads.cs
// uses), and where the compiler's module list is. The rest of the C library
// (calloc, malloc, puts, fopen, abort, the math functions) comes from Windows'
// own, the Universal CRT;
// memset and memmove from ntdll. The entry point is start.c's (programs) or
// library.c's (native libraries).
//
// Compiled with clang for x86_64-pc-windows-msvc.

#include "declarations.h"

// ---- where the compiler's module list is ------------------------------------
//
// The compiler puts a pointer to each module's header in section
// ".modules$I". The linker sorts sections by the part after "$", so these two
// land just before and just after it, which is what the ELF linker's
// __start___modules and __stop___modules give zerocore on Linux. Startup.cs
// skips the null entries (this first one, and any padding).

#pragma section(".modules$A", read)
#pragma section(".modules$Z", read)
__declspec(allocate(".modules$A")) void *__start___modules[1] = { 0 };
__declspec(allocate(".modules$Z")) void *__stop___modules[1] = { 0 };

// ---- POSIX functions --------------------------------------------------------

static Handle handle_for(int fd)
{
    switch (fd) {
    case 0: return GetStdHandle(STD_INPUT_HANDLE);
    case 1: return GetStdHandle(STD_OUTPUT_HANDLE);
    case 2: return GetStdHandle(STD_ERROR_HANDLE);
    default: return 0;
    }
}

i64 write(int fd, const void *buffer, u64 count)
{
    Handle file = handle_for(fd);
    const char *next = buffer;
    u64 left = count;
    while (left > 0) {
        u32 chunk = left > 0x40000000 ? 0x40000000 : (u32)left;
        u32 written = 0;
        if (!WriteFile(file, next, chunk, &written, 0))
            return -1;
        next += written;
        left -= written;
    }
    return (i64)count;
}

#define KEYS_READ ReadConsoleInputW
#define KEYS_PEEK PeekConsoleInputW
#include "console-keys.h"

// A console hands over typed lines ending in "\r\n", so carriage returns from
// a console are dropped. In raw mode (Console.ReadKey) it hands over keys, as
// console-keys.h has them. A file or a pipe is passed through as it is: its
// bytes may not be text (lib/In's Read), and In's ReadLine handles "\r\n".
i64 read(int fd, void *buffer, u64 count)
{
    Handle file = handle_for(fd);
    char *bytes = buffer;
    u32 chunk = count > 0x40000000 ? 0x40000000 : (u32)count;
    if (fd == 0 && keys_raw)
        return keys_read(buffer, chunk);
    u32 mode;
    int console = GetConsoleMode(file, &mode);
    for (;;) {
        u32 got = 0;
        if (!ReadFile(file, bytes, chunk, &got, 0))
            return GetLastError() == ERROR_BROKEN_PIPE ? 0 : -1;
        if (got == 0)
            return 0;
        if (!console)
            return got;
        u32 kept = 0;
        for (u32 i = 0; i < got; i++)
            if (bytes[i] != '\r')
                bytes[kept++] = bytes[i];
        if (kept > 0)
            return kept;
    }
}

// ---- threads, as pthreads ---------------------------------------------------
//
// lib/Threads.cs and zerocore's ObjectAllocator use the POSIX names; these are
// the few they need, on Windows threads and thread-local storage.

typedef struct { void *(*start)(void *); void *argument; } ThreadStart;

static u32 run_thread(void *parameter)
{
    ThreadStart start = *(ThreadStart *)parameter;
    HeapFree(GetProcessHeap(), 0, parameter);
    start.start(start.argument);
    return 0;
}

int pthread_create(Handle *thread, void *attributes, void *(*start)(void *), void *argument)
{
    (void)attributes;
    ThreadStart *parameter = HeapAlloc(GetProcessHeap(), 0, sizeof(ThreadStart));
    if (!parameter)
        return 11; // EAGAIN
    parameter->start = start;
    parameter->argument = argument;
    *thread = CreateThread(0, 0, run_thread, parameter, 0, 0);
    if (!*thread) {
        HeapFree(GetProcessHeap(), 0, parameter);
        return 11;
    }
    return 0;
}

// What a thread waiting for another's static constructor calls between checks.
int sched_yield(void)
{
    SwitchToThread();
    return 0;
}

int pthread_join(Handle thread, void **result)
{
    WaitForSingleObject(thread, 0xFFFFFFFF);
    CloseHandle(thread);
    if (result)
        *result = 0;
    return 0;
}

int pthread_key_create(u64 *key, void (*destructor)(void *))
{
    (void)destructor;
    u32 index = TlsAlloc();
    if (index == 0xFFFFFFFF)
        return 11;
    *key = index;
    return 0;
}

void *pthread_getspecific(u64 key) { return TlsGetValue((u32)key); }

int pthread_setspecific(u64 key, void *value) { return TlsSetValue((u32)key, value) ? 0 : 22; }

int get_nprocs(void) { return (int)GetActiveProcessorCount(0xFFFF); }

// lib/File.cs passes paths as UTF-8, as every other target takes them; the
// Universal CRT's fopen and remove read them in the ANSI code page, which
// garbles or refuses any other character. These take UTF-8 and call the
// UTF-16 functions. Like getenv below, they are linked with /wrap:fopen and
// /wrap:remove, which send calls here, since the Universal CRT is read first.
enum { MaxPath = 4096 }; // in UTF-16 units; lib/File.cs's limit is 1024 bytes

void *__wrap_fopen(const char *path, const char *mode)
{
    wchar wide_path[MaxPath], wide_mode[16];
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, wide_path, MaxPath)
        || !MultiByteToWideChar(CP_UTF8, 0, mode, -1, wide_mode, 16))
        return 0;
    return _wfopen(wide_path, wide_mode);
}

int __wrap_remove(const char *path)
{
    wchar wide_path[MaxPath];
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, wide_path, MaxPath))
        return -1;
    return _wremove(wide_path);
}

// Environment.GetEnvironmentVariable passes the name as UTF-8 and expects the
// value back as UTF-8; the Universal CRT's getenv speaks the ANSI code page.
// This one goes through the UTF-16 environment instead. The linker reads the
// Universal CRT before this file, so a getenv here would clash with its; the
// link passes /wrap:getenv instead, which sends calls to getenv here.
// (Environment.Exit uses the Universal CRT's exit, which flushes its files.)
char *__wrap_getenv(const char *name)
{
    enum { MaxValue = 32767 }; // the longest a variable can be, in UTF-16
    static wchar wide_name[256], wide_value[MaxValue];
    static char value[MaxValue * 3 + 1];
    if (!MultiByteToWideChar(65001, 0, name, -1, wide_name, 256))
        return 0;
    u32 length = GetEnvironmentVariableW(wide_name, wide_value, MaxValue);
    if (length == 0 || length >= MaxValue)
        return 0;
    return WideCharToMultiByte(65001, 0, wide_value, -1, value, sizeof value, 0, 0) ? value : 0;
}

// Linux's clock numbers, as lib/Clock.cs passes them: 0 is the wall clock
// (CLOCK_REALTIME), anything else the monotonic clock.
int clock_gettime(int clock, i64 *timespec)
{
    if (clock == 0) {
        // 100-nanosecond intervals since 1601-01-01, which was 11644473600
        // seconds before 1970-01-01.
        u64 now;
        GetSystemTimePreciseAsFileTime(&now);
        now -= 116444736000000000ull;
        timespec[0] = (i64)(now / 10000000);
        timespec[1] = (i64)(now % 10000000) * 100;
        return 0;
    }
    i64 count, frequency;
    QueryPerformanceCounter(&count);
    QueryPerformanceFrequency(&frequency);
    timespec[0] = count / frequency;
    timespec[1] = (count % frequency) * 1000000000 / frequency;
    return 0;
}

// 64-bit file positions for lib/File.cs. POSIX calls these fseeko and ftello;
// the Universal CRT has them as _fseeki64 and _ftelli64.
int fseeko(void *stream, i64 offset, int origin) { return _fseeki64(stream, offset, origin); }

i64 ftello(void *stream) { return _ftelli64(stream); }

// ---- fiber stacks -----------------------------------------------------------
//
// lib/Fibers.cs puts a guard page under each fiber's stack, with the POSIX
// calls: mprotect with Linux's flags (0: no access, 3: read and write).

int getpagesize(void) { return 4096; }

int mprotect(void *address, u64 length, int protection)
{
    u32 old;
    return VirtualProtect(address, length, protection == 0 ? PAGE_NOACCESS : PAGE_READWRITE, &old) ? 0 : -1;
}

int usleep(u32 microseconds)
{
    Sleep((microseconds + 999) / 1000);
    return 0;
}
