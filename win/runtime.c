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

static int is_wake_socket(int fd);
static i64 wake_send(int fd, const void *buffer, u64 count);
static i64 wake_recv(int fd, void *buffer, u64 count);

i64 write(int fd, const void *buffer, u64 count)
{
    if (is_wake_socket(fd))
        return wake_send(fd, buffer, count);
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
#define KEYS_POLL keys_poll
#include "console-keys.h"

// A console hands over typed lines ending in "\r\n", so carriage returns from
// a console are dropped. In raw mode (Console.ReadKey) it hands over keys, as
// console-keys.h has them. A file or a pipe is passed through as it is: its
// bytes may not be text (lib/In's Read), and In's ReadLine handles "\r\n".
i64 read(int fd, void *buffer, u64 count)
{
    if (is_wake_socket(fd))
        return wake_recv(fd, buffer, count);
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

// ---- files and directories, for lib/FileSystem.cs -------------------------
//
// lib/FileSystem.cs calls glibc's names with glibc's layouts and UTF-8 paths;
// these answer through the UTF-16 Win32 functions. Failures set the Universal
// CRT's errno, with its numbers (which lib/FileSystem.cs knows). mkdir, rmdir,
// rename, getcwd and chdir are linked with /wrap:, like fopen; the others
// aren't in the Universal CRT.

// Which system this is: zerocore/native/win-x64.S says (4, or 5 on UEFI).

int *__errno_location(void) { return _errno(); }

// The Universal CRT's errno for the thread's last Win32 error.
static int set_errno_from_win32(void)
{
    int e;
    switch (GetLastError()) {
    case 2: case 3: case 15: case 53: case 67: e = 2; break;   // not found: ENOENT
    case 5: case 19: case 32: case 33: e = 13; break;          // EACCES
    case 80: case 183: e = 17; break;                          // EEXIST
    case 145: e = 41; break;                                   // ENOTEMPTY
    case 267: e = 20; break;                                   // ENOTDIR
    case 17: e = 18; break;                                    // EXDEV
    case 206: e = 38; break;                                   // ENAMETOOLONG
    case 123: case 161: e = 22; break;                         // EINVAL
    case 39: case 112: e = 28; break;                          // ENOSPC
    default: e = 5; break;                                     // EIO
    }
    *_errno() = e;
    return -1;
}

static int widen(const char *path, wchar *wide)
{
    if (MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, MaxPath))
        return 1;
    *_errno() = 38; // ENAMETOOLONG, or not UTF-8
    return 0;
}

// glibc's struct stat as on arm64: the mode at 16, the size at 48, the
// modification time at 88 (seconds) and 96 (nanoseconds). The attributes are
// the path's own, so a symbolic link to a directory is a directory to stat
// and a link to lstat.
static int stat_windows(const char *path, unsigned char *out, int follow)
{
    wchar wide[MaxPath];
    FileAttributeData data;
    if (!widen(path, wide))
        return -1;
    if (!GetFileAttributesExW(wide, 0, &data))
        return set_errno_from_win32();
    for (int i = 0; i < 128; i++)
        out[i] = 0;
    u32 mode = !follow && (data.attributes & FILE_ATTRIBUTE_REPARSE_POINT) ? 0xA000
             : (data.attributes & FILE_ATTRIBUTE_DIRECTORY) ? 0x4000 : 0x8000;
    *(u32 *)(out + 16) = mode;
    *(i64 *)(out + 48) = (i64)(((u64)data.size_high << 32) | data.size_low);
    // 100-nanosecond intervals since 1601 to seconds and nanoseconds since 1970.
    u64 ticks = ((u64)data.written.high << 32) | data.written.low;
    u64 since1970 = ticks - 116444736000000000ull;
    *(i64 *)(out + 88) = (i64)(since1970 / 10000000);
    *(i64 *)(out + 96) = (i64)(since1970 % 10000000 * 100);
    return 0;
}

int __xstat64(int version, const char *path, unsigned char *out)
{
    (void)version;
    return stat_windows(path, out, 1);
}

int __lxstat64(int version, const char *path, unsigned char *out)
{
    (void)version;
    return stat_windows(path, out, 0);
}

// A directory being read: FindFirstFileW's handle, the entry it found first,
// and glibc's struct dirent64 to answer in (d_type at 18, d_name from 19).
typedef struct {
    unsigned long long d_ino;
    long long d_off;
    unsigned short d_reclen;
    unsigned char d_type;
    char d_name[1024];
} Dirent64;

typedef struct {
    Handle find;
    int pending; // FindFirstFileW's entry hasn't been returned yet
    FindData data;
    Dirent64 entry;
} Dir;

void *opendir(const char *path)
{
    wchar pattern[MaxPath];
    if (!widen(path, pattern))
        return 0;
    int n = 0;
    while (pattern[n])
        n++;
    if (n + 3 > MaxPath) {
        *_errno() = 38;
        return 0;
    }
    if (n > 0 && pattern[n - 1] != '\\' && pattern[n - 1] != '/')
        pattern[n++] = '\\';
    pattern[n++] = '*';
    pattern[n] = 0;
    Dir *dir = HeapAlloc(GetProcessHeap(), 0, sizeof(Dir));
    if (!dir) {
        *_errno() = 12; // ENOMEM
        return 0;
    }
    dir->find = FindFirstFileW(pattern, &dir->data);
    dir->pending = 1;
    if (dir->find == INVALID_HANDLE_VALUE) {
        if (GetLastError() == 2) {
            dir->pending = 0; // a drive's root with nothing in it
        } else {
            set_errno_from_win32();
            HeapFree(GetProcessHeap(), 0, dir);
            return 0;
        }
    }
    return dir;
}

void *readdir64(Dir *dir)
{
    if (dir->find == INVALID_HANDLE_VALUE)
        return 0;
    if (!dir->pending) {
        if (!FindNextFileW(dir->find, &dir->data)) {
            if (GetLastError() != 18) // ERROR_NO_MORE_FILES: the end, not an error
                set_errno_from_win32();
            return 0;
        }
    }
    dir->pending = 0;
    u32 attributes = dir->data.attributes;
    u32 tag = dir->data.reparse_tag;
    dir->entry.d_type = (attributes & FILE_ATTRIBUTE_REPARSE_POINT)
            && (tag == IO_REPARSE_TAG_SYMLINK || tag == IO_REPARSE_TAG_MOUNT_POINT) ? 10
        : (attributes & FILE_ATTRIBUTE_DIRECTORY) ? 4 : 8;
    if (!WideCharToMultiByte(CP_UTF8, 0, dir->data.name, -1, dir->entry.d_name,
                             sizeof dir->entry.d_name, 0, 0))
        dir->entry.d_name[0] = 0;
    return &dir->entry;
}

int closedir(Dir *dir)
{
    if (dir->find != INVALID_HANDLE_VALUE)
        FindClose(dir->find);
    HeapFree(GetProcessHeap(), 0, dir);
    return 0;
}

int __wrap_mkdir(const char *path, int mode)
{
    (void)mode;
    wchar wide[MaxPath];
    if (!widen(path, wide))
        return -1;
    return CreateDirectoryW(wide, 0) ? 0 : set_errno_from_win32();
}

int __wrap_rmdir(const char *path)
{
    wchar wide[MaxPath];
    if (!widen(path, wide))
        return -1;
    return RemoveDirectoryW(wide) ? 0 : set_errno_from_win32();
}

// As POSIX's rename: replaces a file already at `to`. Between drives a file
// is copied (directories can't be).
int __wrap_rename(const char *from, const char *to)
{
    wchar wide_from[MaxPath], wide_to[MaxPath];
    if (!widen(from, wide_from) || !widen(to, wide_to))
        return -1;
    return MoveFileExW(wide_from, wide_to, MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED)
        ? 0 : set_errno_from_win32();
}

char *__wrap_getcwd(char *buffer, unsigned long long size)
{
    wchar wide[MaxPath];
    u32 length = GetCurrentDirectoryW(MaxPath, wide);
    if (length == 0 || length >= MaxPath) {
        set_errno_from_win32();
        return 0;
    }
    if (!WideCharToMultiByte(CP_UTF8, 0, wide, -1, buffer, (int)size, 0, 0)) {
        *_errno() = 34; // ERANGE
        return 0;
    }
    return buffer;
}

int __wrap_chdir(const char *path)
{
    wchar wide[MaxPath];
    if (!widen(path, wide))
        return -1;
    return SetCurrentDirectoryW(wide) ? 0 : set_errno_from_win32();
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

// ---- random bytes, for lib/CryptoRandom.cs ----------------------------------
//
// getentropy from BCryptGenRandom with the system's preferred generator
// (BCRYPT_USE_SYSTEM_PREFERRED_RNG, 2), looked up at run time so programs
// that never ask for random bytes don't import bcrypt.dll.

int getentropy(void *buffer, u64 length)
{
    typedef long (*GenRandom)(void *, void *, u32, u32);
    static GenRandom gen_random;
    if (length > 256)
        return -1;
    if (!gen_random) {
        Handle module = LoadLibraryA("bcrypt.dll");
        if (module)
            gen_random = (GenRandom)GetProcAddress(module, "BCryptGenRandom");
        if (!gen_random)
            return -1;
    }
    return gen_random(0, buffer, (u32)length, 2) == 0 ? 0 : -1;
}

// ---- sockets, for lib/Sockets.cs -------------------------------------------
//
// lib/Sockets.cs calls the C library's socket functions with Linux's numbers
// and layouts; these answer through Winsock. The names Winsock also has are
// linked with /wrap:, like getenv, and find Winsock's own at run time
// (GetProcAddress): naming them here would make every program link them,
// and lld can't resolve a wrapped name's original that only this file calls.
// poll and ZeroFcntl Winsock doesn't have. Failures set errno to Linux's
// number for what went wrong. SOCKETs go to lib/ as ints: Windows hands out
// handles that fit in 32 bits.

// Winsock's function `name`, looked up once into *slot.
static void *winsock_function(void **slot, const char *name)
{
    static Handle module;
    if (!*slot) {
        if (!module)
            module = LoadLibraryA("ws2_32.dll");
        *slot = GetProcAddress(module, name);
    }
    return *slot;
}

#define WINSOCK(result, name, parameters) \
    static void *name##_slot; \
    result (*name) parameters = (result (*) parameters)winsock_function(&name##_slot, #name)

static void start_winsock(void)
{
    static int started;
    if (!started) {
        u64 data[64]; // WSADATA: 408 bytes on x64
        WINSOCK(int, WSAStartup, (unsigned short, void *));
        WSAStartup(0x0202, data);
        started = 1;
    }
}

// errno, with Linux's number, for the thread's last Winsock error.
static int set_errno_from_winsock(void)
{
    WINSOCK(int, WSAGetLastError, (void));
    int e;
    switch (WSAGetLastError()) {
    case 10004: e = 4; break;   case 10009: e = 9; break;   case 10013: e = 13; break;
    case 10022: e = 22; break;  case 10024: e = 24; break;  case 10035: e = 11; break;
    case 10036: e = 115; break; case 10037: e = 114; break; case 10038: e = 88; break;
    case 10039: e = 89; break;  case 10040: e = 90; break;  case 10043: e = 93; break;
    case 10045: e = 95; break;  case 10047: e = 97; break;  case 10048: e = 98; break;
    case 10049: e = 99; break;  case 10050: e = 100; break; case 10051: e = 101; break;
    case 10052: e = 102; break; case 10053: e = 103; break; case 10054: e = 104; break;
    case 10055: e = 105; break; case 10056: e = 106; break; case 10057: e = 107; break;
    case 10058: e = 108; break; case 10060: e = 110; break; case 10061: e = 111; break;
    case 10064: e = 112; break; case 10065: e = 113; break;
    default: e = 5; break; // EIO
    }
    *_errno() = e;
    return -1;
}

// Linux's address family numbers to Winsock's and back: only IPv6 differs.
static int to_windows_family(int family) { return family == 10 ? 23 : family; }
static int to_linux_family(int family) { return family == 23 ? 10 : family; }

// A sockaddr (sockaddr_in or sockaddr_in6, which Winsock lays out as Linux
// does) with its 16-bit family changed.
static const void *windows_address(const void *address, int length, unsigned char *copy)
{
    if (!address || length < 2 || length > 28)
        return address;
    for (int i = 0; i < length; i++)
        copy[i] = ((const unsigned char *)address)[i];
    *(unsigned short *)copy = (unsigned short)to_windows_family(*(unsigned short *)copy);
    return copy;
}

static void linux_address(void *address, int *length)
{
    if (address && length && *length >= 2)
        *(unsigned short *)address = (unsigned short)to_linux_family(*(unsigned short *)address);
}

int __wrap_socket(int family, int type, int protocol)
{
    start_winsock();
    WINSOCK(Socket, socket, (int, int, int));
    Socket s = socket(to_windows_family(family), type, protocol);
    return s == ~(Socket)0 ? set_errno_from_winsock() : (int)s;
}

int __wrap_bind(int s, const void *address, unsigned length)
{
    unsigned char copy[28];
    WINSOCK(int, bind, (Socket, const void *, int));
    return bind((Socket)(unsigned)s, windows_address(address, (int)length, copy), (int)length) == 0 ? 0 : set_errno_from_winsock();
}

int __wrap_listen(int s, int backlog)
{
    WINSOCK(int, listen, (Socket, int));
    return listen((Socket)(unsigned)s, backlog) == 0 ? 0 : set_errno_from_winsock();
}

int __wrap_accept(int s, void *address, int *length)
{
    WINSOCK(Socket, accept, (Socket, void *, int *));
    Socket accepted = accept((Socket)(unsigned)s, address, length);
    if (accepted == ~(Socket)0)
        return set_errno_from_winsock();
    linux_address(address, length);
    return (int)accepted;
}

int __wrap_connect(int s, const void *address, unsigned length)
{
    unsigned char copy[28];
    WINSOCK(int, connect, (Socket, const void *, int));
    return connect((Socket)(unsigned)s, windows_address(address, (int)length, copy), (int)length) == 0 ? 0 : set_errno_from_winsock();
}

// MSG_NOSIGNAL (0x4000) means nothing here: Windows has no SIGPIPE.
long long __wrap_sendto(int s, const void *buffer, unsigned long long length, int flags, const void *to, unsigned tolength)
{
    unsigned char copy[28];
    WINSOCK(int, sendto, (Socket, const void *, int, int, const void *, int));
    int n = sendto((Socket)(unsigned)s, buffer, (int)(length > 0x7FFFFFFF ? 0x7FFFFFFF : length), flags & ~0x4000,
                          windows_address(to, (int)tolength, copy), (int)tolength);
    return n < 0 ? set_errno_from_winsock() : n;
}

long long __wrap_recvfrom(int s, void *buffer, unsigned long long length, int flags, void *from, int *fromlength)
{
    WINSOCK(int, recvfrom, (Socket, void *, int, int, void *, int *));
    WINSOCK(int, WSAGetLastError, (void));
    int n = recvfrom((Socket)(unsigned)s, buffer, (int)(length > 0x7FFFFFFF ? 0x7FFFFFFF : length), flags, from, fromlength);
    if (n < 0) {
        // A datagram bigger than the buffer: Linux gives what fits.
        if (WSAGetLastError() == 10040)
            return (long long)length;
        return set_errno_from_winsock();
    }
    linux_address(from, fromlength);
    return n;
}

int __wrap_shutdown(int s, int how)
{
    WINSOCK(int, shutdown, (Socket, int));
    return shutdown((Socket)(unsigned)s, how) == 0 ? 0 : set_errno_from_winsock();
}

int __wrap_close(int s)
{
    WINSOCK(int, closesocket, (Socket));
    return closesocket((Socket)(unsigned)s) == 0 ? 0 : set_errno_from_winsock();
}

// Linux's option numbers to Winsock's. SO_REUSEADDR is left out: on Windows
// it lets two sockets listen on one port, not what lib/ asks it for.
static int windows_option(int *level, int *name)
{
    if (*level == 1) { // SOL_SOCKET
        *level = 0xFFFF;
        switch (*name) {
        case 2: return 0;                   // SO_REUSEADDR: ignored
        case 4: *name = 0x1007; break;      // SO_ERROR
        case 6: *name = 0x20; break;        // SO_BROADCAST
        }
    } else if (*level == 41 && *name == 26) { // IPPROTO_IPV6, IPV6_V6ONLY
        *name = 27;
    }
    return 1;
}

int __wrap_setsockopt(int s, int level, int name, const void *value, unsigned length)
{
    if (level == 1 && name == 13 && length >= 8) {
        // SO_LINGER: Linux's struct linger is two ints, Winsock's two u_shorts.
        unsigned short linger[2] = { (unsigned short)((const int *)value)[0], (unsigned short)((const int *)value)[1] };
        WINSOCK(int, setsockopt, (Socket, int, int, const void *, int));
        return setsockopt((Socket)(unsigned)s, 0xFFFF, 0x80, linger, 4) == 0 ? 0 : set_errno_from_winsock();
    }
    if (!windows_option(&level, &name))
        return 0;
    WINSOCK(int, setsockopt, (Socket, int, int, const void *, int));
    return setsockopt((Socket)(unsigned)s, level, name, value, (int)length) == 0 ? 0 : set_errno_from_winsock();
}

int __wrap_getsockopt(int s, int level, int name, void *value, int *length)
{
    if (level == 1 && name == 13 && *length >= 8) {
        // SO_LINGER, as in __wrap_setsockopt.
        unsigned short linger[2] = { 0, 0 };
        int size = 4;
        WINSOCK(int, getsockopt, (Socket, int, int, void *, int *));
        if (getsockopt((Socket)(unsigned)s, 0xFFFF, 0x80, linger, &size) != 0)
            return set_errno_from_winsock();
        ((int *)value)[0] = linger[0];
        ((int *)value)[1] = linger[1];
        *length = 8;
        return 0;
    }
    if (!windows_option(&level, &name)) {
        *(int *)value = 0;
        return 0;
    }
    WINSOCK(int, getsockopt, (Socket, int, int, void *, int *));
    if (getsockopt((Socket)(unsigned)s, level, name, value, length) != 0)
        return set_errno_from_winsock();
    // SO_ERROR holds a Winsock error: Linux's number for it.
    if (level == 0xFFFF && name == 0x1007 && *(int *)value != 0) {
        int e = *(int *)value;
        *(int *)value = e == 10061 ? 111 : e == 10060 ? 110 : e == 10065 ? 113 : e == 10051 ? 101
                      : e == 10054 ? 104 : e == 10048 ? 98 : e == 10049 ? 99 : 5;
    }
    return 0;
}

int __wrap_getsockname(int s, void *address, int *length)
{
    WINSOCK(int, getsockname, (Socket, void *, int *));
    if (getsockname((Socket)(unsigned)s, address, length) != 0)
        return set_errno_from_winsock();
    linux_address(address, length);
    return 0;
}

int __wrap_getpeername(int s, void *address, int *length)
{
    WINSOCK(int, getpeername, (Socket, void *, int *));
    if (getpeername((Socket)(unsigned)s, address, length) != 0)
        return set_errno_from_winsock();
    linux_address(address, length);
    return 0;
}

// poll over WSAPoll, whose entries are 16 bytes (a 64-bit SOCKET) and whose
// event bits differ: POLLIN is POLLRDNORM, POLLOUT is POLLWRNORM.
int poll(void *fds, unsigned long long count, int timeout)
{
    typedef struct { Socket fd; short events, revents; } WinPoll;
    typedef struct { int fd; short events, revents; } LinuxPoll;
    LinuxPoll *in = fds;
    // Standard input (Console.KeyAvailable) is the console's, not a socket.
    if (count == 1 && in[0].fd == 0)
        return keys_poll(fds, (unsigned long)count, timeout);
    start_winsock();
    WinPoll *out = HeapAlloc(GetProcessHeap(), 0, count * sizeof(WinPoll));
    if (!out) {
        *_errno() = 12; // ENOMEM
        return -1;
    }
    for (unsigned long long i = 0; i < count; i++) {
        out[i].fd = (Socket)(unsigned)in[i].fd;
        out[i].events = (short)(((in[i].events & 1) ? 0x100 : 0) | ((in[i].events & 4) ? 0x10 : 0));
        out[i].revents = 0;
    }
    WINSOCK(int, WSAPoll, (void *, u32, int));
    int ready = WSAPoll(out, (u32)count, timeout);
    if (ready < 0) {
        HeapFree(GetProcessHeap(), 0, out);
        return set_errno_from_winsock();
    }
    for (unsigned long long i = 0; i < count; i++) {
        short r = out[i].revents;
        in[i].revents = (short)(((r & 0x300) ? 1 : 0) | ((r & 0x10) ? 4 : 0) | ((r & 1) ? 8 : 0)
                              | ((r & 2) ? 0x10 : 0) | ((r & 4) ? 0x20 : 0));
    }
    HeapFree(GetProcessHeap(), 0, out);
    return ready;
}

// pipe, for lib/FileAsync.cs: its I/O thread and Fiber.Run wake each other
// through two of them, and Fiber.Run waits on one in poll with the sockets.
// WSAPoll takes only sockets, so a pipe here is a connected pair of loopback
// sockets (blocking ones: the I/O thread waits in read, and Fiber.Run reads
// only once poll says there's something). read and write above send and
// receive on them.
//
// The wake sockets are listed in chunks of 64, added as needed and never
// freed: every thread's read and write looks here, while any thread may make
// a pipe (lib/ThreadTasks.cs takes one per worker, lib/ThreadCompletion.cs
// one per thread that makes sources). Adding is under a spin lock; the count
// is published after the entries, so a reader sees only filled ones.
#define WAKE_CHUNK 64
#define WAKE_CHUNKS 256
static int *wake_chunks[WAKE_CHUNKS];
static int wake_count;
static int wake_lock;

static int is_wake_socket(int fd)
{
    if (fd <= 2)
        return 0;
    int count = __atomic_load_n(&wake_count, __ATOMIC_ACQUIRE);
    for (int i = 0; i < count; i++)
        if (wake_chunks[i / WAKE_CHUNK][i % WAKE_CHUNK] == fd)
            return 1;
    return 0;
}

// Lists a pipe's two sockets; 0 when out of room or memory.
static int add_wake_sockets(int reader, int writer)
{
    while (__atomic_exchange_n(&wake_lock, 1, __ATOMIC_ACQUIRE))
        Sleep(0);
    int count = wake_count, added = 0;
    if (count + 2 <= WAKE_CHUNK * WAKE_CHUNKS) {
        added = 1;
        // count is even and WAKE_CHUNK too: both land in the same chunk.
        int *chunk = wake_chunks[count / WAKE_CHUNK];
        if (!chunk) {
            chunk = HeapAlloc(GetProcessHeap(), 0, WAKE_CHUNK * sizeof(int));
            wake_chunks[count / WAKE_CHUNK] = chunk;
            added = chunk != 0;
        }
        if (added) {
            chunk[count % WAKE_CHUNK] = reader;
            chunk[count % WAKE_CHUNK + 1] = writer;
            __atomic_store_n(&wake_count, count + 2, __ATOMIC_RELEASE);
        }
    }
    __atomic_store_n(&wake_lock, 0, __ATOMIC_RELEASE);
    return added;
}

static i64 wake_send(int fd, const void *buffer, u64 count)
{
    WINSOCK(int, send, (Socket, const void *, int, int));
    int sent = send((Socket)(unsigned)fd, buffer, (int)count, 0);
    return sent < 0 ? set_errno_from_winsock() : sent;
}

static i64 wake_recv(int fd, void *buffer, u64 count)
{
    WINSOCK(int, recv, (Socket, void *, int, int));
    int got = recv((Socket)(unsigned)fd, buffer, (int)count, 0);
    return got < 0 ? set_errno_from_winsock() : got;
}

int pipe(int fds[2])
{
    start_winsock();
    WINSOCK(Socket, socket, (int, int, int));
    WINSOCK(int, bind, (Socket, const void *, int));
    WINSOCK(int, listen, (Socket, int));
    WINSOCK(int, getsockname, (Socket, void *, int *));
    WINSOCK(int, connect, (Socket, const void *, int));
    WINSOCK(Socket, accept, (Socket, void *, int *));
    WINSOCK(int, closesocket, (Socket));
    // sockaddr_in for 127.0.0.1, port 0 (the system chooses).
    unsigned char address[16] = { 2, 0, 0, 0, 127, 0, 0, 1 };
    int length = sizeof address;
    Socket none = ~(Socket)0;
    Socket listener = socket(2, 1, 6), writer = none, reader = none;
    if (listener == none)
        return set_errno_from_winsock();
    if (bind(listener, address, length) != 0 || listen(listener, 1) != 0
        || getsockname(listener, address, &length) != 0)
        goto failed;
    writer = socket(2, 1, 6);
    if (writer == none || connect(writer, address, length) != 0)
        goto failed;
    reader = accept(listener, 0, 0);
    if (reader == none)
        goto failed;
    closesocket(listener);
    if (!add_wake_sockets((int)reader, (int)writer)) {
        closesocket(reader);
        closesocket(writer);
        *_errno() = 24; // EMFILE
        return -1;
    }
    fds[0] = (int)reader;
    fds[1] = (int)writer;
    return 0;
failed:
    set_errno_from_winsock();
    closesocket(listener);
    if (writer != none)
        closesocket(writer);
    return -1;
}

// fcntl's F_GETFL (3) and F_SETFL (4) with O_NONBLOCK (0x800), which is all
// lib/Sockets.cs asks of it, over ioctlsocket's FIONBIO.
int ZeroFcntl(int s, int command, int argument)
{
    if (command == 3)
        return 0;
    if (command == 4) {
        u32 on = (argument & 0x800) ? 1 : 0;
        WINSOCK(int, ioctlsocket, (Socket, long, u32 *));
        return ioctlsocket((Socket)(unsigned)s, (long)0x8004667E, &on) == 0 ? 0 : set_errno_from_winsock();
    }
    *_errno() = 22;
    return -1;
}

// getaddrinfo, answering in Linux's struct addrinfo: ai_addr at 24 and
// ai_canonname at 32, where Winsock has them the other way round (and a
// 64-bit ai_addrlen). Each entry is copied, with its address after it.
typedef struct LinuxAddrinfo {
    int flags, family, socktype, protocol;
    unsigned addrlen;
    void *addr;
    char *canonname;
    struct LinuxAddrinfo *next;
    unsigned char address[28];
} LinuxAddrinfo;

int __wrap_getaddrinfo(const char *node, const char *service, const int *hints, LinuxAddrinfo **result)
{
    start_winsock();
    int windows_hints[12] = { 0 }; // ADDRINFOA: 48 bytes
    if (hints) {
        windows_hints[0] = hints[0];
        windows_hints[1] = to_windows_family(hints[1]);
        windows_hints[2] = hints[2];
        windows_hints[3] = hints[3];
    }
    unsigned char *list = 0;
    WINSOCK(int, getaddrinfo, (const char *, const char *, const void *, void **));
    WINSOCK(void, freeaddrinfo, (void *));
    int e = getaddrinfo(node, service, hints ? windows_hints : 0, (void **)&list);
    if (e != 0) {
        // Linux's EAI_ numbers.
        return e == 11001 ? -2 : e == 11002 ? -3 : e == 11003 ? -4 : e == 11004 ? -5 : -11;
    }
    LinuxAddrinfo *first = 0, **link = &first;
    for (unsigned char *entry = list; entry; entry = *(unsigned char **)(entry + 40)) {
        LinuxAddrinfo *copy = HeapAlloc(GetProcessHeap(), 8, sizeof(LinuxAddrinfo)); // zeroed
        if (!copy)
            break;
        copy->flags = ((int *)entry)[0];
        copy->family = to_linux_family(((int *)entry)[1]);
        copy->socktype = ((int *)entry)[2];
        copy->protocol = ((int *)entry)[3];
        unsigned long long length = *(unsigned long long *)(entry + 16);
        const unsigned char *address = *(unsigned char **)(entry + 32);
        if (address && length <= sizeof copy->address) {
            for (unsigned i = 0; i < length; i++)
                copy->address[i] = address[i];
            *(unsigned short *)copy->address = (unsigned short)to_linux_family(*(unsigned short *)copy->address);
            copy->addr = copy->address;
            copy->addrlen = (unsigned)length;
        }
        *link = copy;
        link = &copy->next;
    }
    freeaddrinfo(list);
    *result = first;
    return 0;
}

void __wrap_freeaddrinfo(LinuxAddrinfo *list)
{
    while (list) {
        LinuxAddrinfo *next = list->next;
        HeapFree(GetProcessHeap(), 0, list);
        list = next;
    }
}

int __wrap_gethostname(char *name, unsigned long long length)
{
    start_winsock();
    WINSOCK(int, gethostname, (char *, int));
    return gethostname(name, (int)length) == 0 ? 0 : set_errno_from_winsock();
}
