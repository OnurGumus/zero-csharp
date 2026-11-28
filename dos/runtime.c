// The native half of zerocore on DOS (32-bit protected mode, 386 and later).
//
// The program is a 32-bit Windows console executable, which the HX DOS
// extender loads and runs under DOS; HX supplies a small kernel32. There is no
// C library, so this file is one: the entry point, the handful of C functions
// zerocore and lib/ call (calloc, write, clock_gettime, fopen and the other
// file functions, the math functions, ...), and the
// routines clang's 386 code calls for 64-bit division. It also gives C# the
// PC itself: the VGA's 320x200 graphics mode, the keyboard, the mouse, the
// speaker, a clock and files.
//
// It also holds what wasm/runtime.c holds for WebAssembly. The C# code here is
// compiled by the same compiler (NativeAOT-LLVM) into the same bitcode; only
// the last step differs, clang compiling that bitcode for a 386. So managed
// code still passes its "shadow stack" as a hidden first argument, as on
// WebAssembly, and expects the same few native functions.
//
// Compiled with -march=i386: no instruction newer than the 386 and its 387
// math coprocessor (or a 486DX, which has one built in).

typedef unsigned int u32;
typedef unsigned long long u64;
typedef long long i64;
typedef u32 size_t;

#define WINAPI __stdcall
#define IMPORT __declspec(dllimport)

IMPORT void *WINAPI GetStdHandle(u32 which);
IMPORT int WINAPI WriteFile(void *file, const void *buffer, u32 count, u32 *written, void *overlapped);
IMPORT void WINAPI ExitProcess(u32 code);
IMPORT u32 WINAPI GetEnvironmentVariableA(const char *name, char *buffer, u32 size);
IMPORT void *WINAPI GetProcessHeap(void);
IMPORT void *WINAPI HeapAlloc(void *heap, u32 flags, u32 size);
IMPORT int WINAPI HeapFree(void *heap, u32 flags, void *block);
IMPORT void *WINAPI HeapReAlloc(void *heap, u32 flags, void *block, u32 size);
IMPORT char *WINAPI GetCommandLineA(void);
IMPORT void *WINAPI CreateFileA(const char *path, u32 access, u32 share, void *security, u32 disposition, u32 flags, void *template_file);
IMPORT int WINAPI ReadFile(void *file, void *buffer, u32 count, u32 *read, void *overlapped);
IMPORT int WINAPI CloseHandle(void *handle);
IMPORT u32 WINAPI SetFilePointer(void *file, int distance, int *distance_high, u32 origin);
IMPORT u32 WINAPI GetFileSize(void *file, u32 *size_high);
IMPORT int WINAPI DeleteFileA(const char *path);
IMPORT int WINAPI GetNumberOfConsoleInputEvents(void *console, u32 *count);
IMPORT int WINAPI ReadConsoleInputA(void *console, void *records, u32 capacity, u32 *count);
IMPORT int WINAPI PeekConsoleInputA(void *console, void *records, u32 capacity, u32 *count);
IMPORT int WINAPI GetConsoleMode(void *console, u32 *mode);
IMPORT u32 WINAPI GetFileType(void *file);

#define STD_INPUT_HANDLE ((u32)-10)
#define STD_OUTPUT_HANDLE ((u32)-11)
#define HEAP_ZERO_MEMORY 8

// ---- memory ------------------------------------------------------------------

// The string instructions do these, four bytes at a time and then the rest;
// on a 386 that is several times faster than any loop. Writing them this way
// also keeps the compiler from turning a loop back into a call to the same
// function.
void *memset(void *destination, int value, size_t count)
{
    void *d = destination;
    u32 pattern = (unsigned char)value * 0x01010101u;
    size_t words = count >> 2, rest = count & 3;
    __asm__ volatile("rep stosl" : "+D"(d), "+c"(words) : "a"(pattern) : "memory");
    __asm__ volatile("rep stosb" : "+D"(d), "+c"(rest) : "a"(pattern) : "memory");
    return destination;
}

void *memcpy(void *destination, const void *source, size_t count)
{
    void *d = destination;
    size_t words = count >> 2, rest = count & 3;
    __asm__ volatile("rep movsl" : "+D"(d), "+S"(source), "+c"(words) : : "memory");
    __asm__ volatile("rep movsb" : "+D"(d), "+S"(source), "+c"(rest) : : "memory");
    return destination;
}

void *memmove(void *destination, const void *source, size_t count)
{
    char *d = destination;
    const char *s = source;
    if (d <= s || d >= s + count)
        return memcpy(destination, source, count);
    // Overlapping, destination above source: copy backwards.
    d += count - 1;
    s += count - 1;
    __asm__ volatile("std; rep movsb; cld" : "+D"(d), "+S"(s), "+c"(count) : : "memory");
    return destination;
}

void *malloc(size_t size) { return HeapAlloc(GetProcessHeap(), 0, size); }

void *calloc(size_t count, size_t size)
{
    u64 bytes = (u64)count * size;
    if (bytes >> 32)
        return 0;
    return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (u32)bytes);
}

void free(void *block)
{
    if (block)
        HeapFree(GetProcessHeap(), 0, block);
}

// HeapReAlloc takes no null block, as realloc does.
void *realloc(void *block, size_t size)
{
    if (!block)
        return malloc(size);
    return HeapReAlloc(GetProcessHeap(), 0, block, size);
}

// ---- output and exit ---------------------------------------------------------

// File descriptors 1 and 2, and both go to standard output: DOS's command
// line can only redirect that one, so a failure message written to "stderr"
// would never reach a file or a pipe. Bytes are written as they are: on the
// screen DOS moves to the start of the next line at "\n", and a redirected
// file gets the same bytes the program writes on Linux.
int write(int fd, const void *buffer, size_t count)
{
    u32 written = 0;
    (void)fd;
    if (!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), buffer, count, &written, 0))
        return -1;
    return (int)written;
}

// Console.ReadKey's keys, from HX's console as from Windows' (see there).
#define KEYS_READ ReadConsoleInputA
#define KEYS_PEEK PeekConsoleInputA
#define KEYS_ANSI
#include "../win/console-keys.h"

// Standard input, whatever fd is: the keyboard through HX's console, or a
// file given with "<" (what test.sh uses). Returns 0 at the end of input.
// In raw mode (Console.ReadKey), a key at a time: see console-keys.h.
int read(int fd, void *buffer, size_t count)
{
    u32 got = 0;
    (void)fd;
    if (keys_raw)
        return keys_read(buffer, count);
    if (!ReadFile(GetStdHandle(STD_INPUT_HANDLE), buffer, count, &got, 0))
        return -1;
    return (int)got;
}

int puts(const char *text)
{
    size_t length = 0;
    while (text[length])
        length++;
    write(1, text, length);
    write(1, "\n", 1);
    return 0;
}

// 134 is what a shell reports for a program stopped by abort() on Linux
// (128 + SIGABRT), so scripts see the same exit code on every target.
void abort(void)
{
    ExitProcess(134);
    for (;;) { }
}

// Environment.Exit. Files need no flushing: fwrite here isn't buffered.
void exit(int code)
{
    ExitProcess((u32)code);
    for (;;) { }
}

// Environment.GetEnvironmentVariable: DOS's environment, through HX. DOS has
// no character set beyond the code page, so non-ASCII bytes come back as they
// are (and turn into U+FFFD unless they happen to be UTF-8).
char *getenv(const char *name)
{
    static char value[1024];
    u32 length = GetEnvironmentVariableA(name, value, sizeof value);
    return length == 0 || length >= sizeof value ? 0 : value;
}

static void fatal(const char *message, size_t length)
{
    write(2, message, length);
    abort();
}

// ---- threads: there are none --------------------------------------------------
//
// lib/Threads.cs starts no thread when pthread_create fails, and runs its loop
// itself. zerocore's ObjectAllocator keeps its per-thread setting in a key;
// with one thread, a key is a variable.

#define MAX_KEYS 8
static void *key_values[MAX_KEYS];
static unsigned key_count;

int pthread_create(void *thread, void *attributes, void *(*start)(void *), void *argument)
{
    (void)thread; (void)attributes; (void)start; (void)argument;
    return 38; // ENOSYS
}

// zerocore calls this while waiting for another thread's static constructor,
// which with one thread never happens.
int sched_yield(void) { return 0; }

// No pipes (lib/FileAsync.cs then reads and writes files without its I/O
// thread, blocking, as there are no threads either).
int pipe(int fds[2])
{
    (void)fds;
    return -1;
}


int pthread_join(void *thread, void **result)
{
    (void)thread; (void)result;
    return 3; // ESRCH: no thread was started
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

// ---- entry -------------------------------------------------------------------

int __managed__Main(int argc, char **argv);

#define MAX_ARGS 64
static char *arguments[MAX_ARGS + 1];
static char argument_text[1024];

// Splits the command line at spaces; "double quotes" keep spaces together.
static int split_command_line(const char *line)
{
    int count = 0;
    char *out = argument_text;
    char *limit = argument_text + sizeof(argument_text) - 1;
    while (*line && count < MAX_ARGS) {
        while (*line == ' ' || *line == '\t')
            line++;
        if (!*line)
            break;
        arguments[count++] = out;
        int quoted = 0;
        while (*line && (quoted || (*line != ' ' && *line != '\t'))) {
            if (*line == '"')
                quoted = !quoted;
            else if (out < limit)
                *out++ = *line;
            line++;
        }
        if (out < limit + 1)
            *out++ = 0;
    }
    arguments[count] = 0;
    return count;
}

void start(void)
{
    // 53-bit precision, the same as a Windows program gets: +, -, *, / and
    // sqrt on doubles then round as IEEE 754 says, instead of to the 387's
    // 64-bit default and then again when stored.
    unsigned short control = 0x027F;
    __asm__ volatile("fninit; fldcw %0" : : "m"(control));

    int argc = split_command_line(GetCommandLineA());
    ExitProcess((u32)__managed__Main(argc, arguments));
}

// zerocore's startup (Runtime/Startup.cs) needs the module header to set up
// static fields of reference types.
extern char __ReadyToRunHeader[];

void *ZeroGetModuleHeader(void *shadowStack)
{
    (void)shadowStack;
    return __ReadyToRunHeader;
}

// ---- transitions between native and managed code (see wasm/runtime.c) --------

// 256 KB, and as much again for the ordinary stack (build.sh, /stack): small
// enough to leave a 2 MB machine room for the program, roomy for recursion.
static char shadow_stack[256 << 10] __attribute__((aligned(16)));

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

int RhpTrapThreads; // 0: never stop for a GC
void RhpPInvoke(void *frame) { (void)frame; }
void RhpPInvokeReturn(void *frame) { (void)frame; }

int RhpExceptionThrown; // the flag the compiled code checks after calls

int RhpHandleExceptionWasmCatch(void *a, void *b)
{
    (void)a;
    (void)b;
    static const char message[] = "zero mode: exception thrown\n";
    fatal(message, sizeof(message) - 1);
    return 0;
}

void RhpHandleUnhandledException(void *a, void *b)
{
    (void)a;
    (void)b;
    static const char message[] = "zero mode: unhandled exception\n";
    fatal(message, sizeof(message) - 1);
}

void RhpAssignRef(void **destination, void *reference) { *destination = reference; }
void RhpCheckedAssignRef(void **destination, void *reference) { *destination = reference; }
void RhpInitialDynamicInterfaceDispatch(void) { abort(); }

// ---- the PC: screen, keyboard, clock (lib/Dos.cs is the C# side) --------------

// Sets a BIOS video mode: 0x13 is 320x200 with 256 colors, 0x03 the usual text
// mode. The DOS extender passes the interrupt on to the BIOS.
void dos_video_mode(int mode)
{
    __asm__ volatile("int $0x10" : "+a"(mode) : : "ebx", "ecx", "edx", "esi", "edi", "cc", "memory");
}

// Mode 0x13's screen: 64000 bytes, one per pixel, row after row. HX programs
// see the machine's first megabyte at its real addresses.
unsigned char *dos_framebuffer(void) { return (unsigned char *)0xA0000; }

// Copies a finished picture to the screen (at most the screen's 64000 bytes).
void dos_show(const unsigned char *picture, u32 count)
{
    memcpy((void *)0xA0000, picture, count < 64000 ? count : 64000);
}

static void out_byte(unsigned short port, unsigned char value)
{
    __asm__ volatile("outb %0, %1" : : "a"(value), "d"(port));
}

static unsigned char in_byte(unsigned short port)
{
    unsigned char value;
    __asm__ volatile("inb %1, %0" : "=a"(value) : "d"(port));
    return value;
}

// One of the 256 colors; red, green and blue are 0-63 (the VGA has 6 bits each).
void dos_set_color(int index, int red, int green, int blue)
{
    out_byte(0x3C8, (unsigned char)index);
    out_byte(0x3C9, (unsigned char)red);
    out_byte(0x3C9, (unsigned char)green);
    out_byte(0x3C9, (unsigned char)blue);
}

// The clock: milliseconds, from the two things every PC has. The BIOS counts
// the timer chip's interrupts, 18.2 a second, in a variable at 0040:006C, and
// the chip's counter tells how far the current one of those 54.9 ms periods
// has got. (HX's own GetTickCount isn't used: called in a loop it ran far
// ahead of real time under QEMU.)
static u32 bios_ticks(void)
{
    // The first page of memory isn't readable at its address under HX, but
    // DPMI provides selector 0x40 for exactly this BIOS data.
    u32 ticks;
    __asm__ volatile("pushl %%es\nmovw $0x40, %%ax\nmovw %%ax, %%es\nmovl %%es:0x6C, %%eax\npopl %%es" : "=a"(ticks));
    return ticks;
}

u32 dos_ticks(void)
{
    static u32 latest;
    u32 ticks, clocks; // clocks: timer-chip clocks (1,193,182 a second) into the period, 0-65535
    do {
        ticks = bios_ticks();
        // Read back counter 0's status and count together (an 8254 command;
        // every 386 has that chip).
        out_byte(0x43, 0xC2);
        unsigned char status = in_byte(0x40);
        u32 count = in_byte(0x40);
        count |= (u32)in_byte(0x40) << 8;
        if (((status >> 1) & 3) == 3)
            // Mode 3, the BIOS's setting: the counter runs down twice per
            // period, by 2 each clock; the output pin (bit 7) is high during the first run.
            clocks = (status & 0x80 ? 0 : 32768) + ((65536 - count) & 0xFFFF) / 2;
        else
            clocks = (65536 - count) & 0xFFFF;
    } while (ticks != bios_ticks());
    // The BIOS starts its count over at midnight, after 0x1800B0 ticks. Carry
    // on from there, so a program running through midnight sees no jump.
    static u32 previous_ticks, earlier_days;
    if (ticks < previous_ticks)
        earlier_days += 0x1800B0;
    previous_ticks = ticks;
    ticks += earlier_days;
    // 65536 clocks are 54.925 ms.
    u32 now = ticks * 54 + ticks * 925 / 1000 + clocks * 1000 / 1193182;
    // If the counter has wrapped but its interrupt hasn't been counted yet,
    // the result is one period early; never go backwards because of it.
    if ((int)(now - latest) < 0 && latest - now < 60)
        now = latest;
    latest = now;
    return now;
}

// getentropy for lib/CryptoRandom.cs: a PC under DOS has nothing a key could
// safely come from (the timer and the clock are guessable), so there are no
// random bytes, and RandomNumberGenerator throws.
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

// clock_gettime, with Linux's clock numbers as lib/Clock.cs passes them. 0 is
// the wall clock: DOS's date and time, which is usually local time. Anything
// else is the monotonic clock: dos_ticks, to the millisecond, carried past its
// 32-bit wrap (49.7 days).
typedef struct {
    unsigned short year, month, day_of_week, day, hour, minute, second, milliseconds;
} SystemTime;
IMPORT void WINAPI GetSystemTime(SystemTime *time);

// Days from 1970-01-01 to a date (Howard Hinnant's days_from_civil), in int
// arithmetic for a 386: good from 1970 to past 5000.
static int days_from_civil(int year, int month, int day)
{
    year -= month <= 2;
    int year_of_era = year % 400;
    int day_of_year = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
    int day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    return year / 400 * 146097 + day_of_era - 719468;
}

int clock_gettime(int clock, i64 *timespec)
{
    if (clock == 0) {
        SystemTime t;
        GetSystemTime(&t);
        int seconds_today = t.hour * 3600 + t.minute * 60 + t.second;
        timespec[0] = (i64)days_from_civil(t.year, t.month, t.day) * 86400 + seconds_today;
        timespec[1] = (i64)t.milliseconds * 1000000;
        return 0;
    }
    static u32 previous;
    static u64 wraps;
    u32 now = dos_ticks();
    if (now < previous)
        wraps += (u64)1 << 32;
    previous = now;
    u64 ms = wraps + now;
    timespec[0] = (i64)(ms / 1000);
    timespec[1] = (i64)(ms % 1000) * 1000000;
    return 0;
}

// Returns when the monitor's beam starts its way back to the top: the moment
// to change the screen without tearing, and a steady 70 times a second in mode
// 0x13. Bit 3 of the VGA's status port is set during that time.
//
// Some emulators don't imitate that timing (QEMU flips the bit on every
// read), and a game paced by this would run far too fast there. So a frame
// also never takes less than 13 ms by the clock; on a real VGA, where frames
// are 14.3 ms apart, that wait never happens.
void dos_wait_frame(void)
{
    static u32 last_frame;
    while (in_byte(0x3DA) & 8) { }
    while (!(in_byte(0x3DA) & 8)) { }
    while (dos_ticks() - last_frame < 13) { }
    last_frame = dos_ticks();
}

// The keyboard, from the key-down and key-up events HX's kernel32 collects in
// its keyboard interrupt handler. Keys are identified by scan code (the key's
// position: Esc is 1, the arrows 0x48, 0x50, 0x4B, 0x4D), 0-127.
static unsigned char key_is_down[128];
static unsigned short key_queue[32]; // presses not yet read: scan code << 8 | character
static u32 key_queue_head, key_queue_count;

struct key_event { // Windows' INPUT_RECORD for a key
    unsigned short type, padding;
    int down;
    unsigned short repeat, virtual_key, scan_code;
    unsigned char character, character_high;
    u32 control_keys;
};

// Takes in what happened on the keyboard since the last call. Returns the
// number of key presses waiting for dos_read_key.
int dos_poll_keys(void)
{
    void *console = GetStdHandle(STD_INPUT_HANDLE);
    struct key_event event;
    u32 count;
    while (GetNumberOfConsoleInputEvents(console, &count) && count > 0) {
        if (!ReadConsoleInputA(console, &event, 1, &count) || count == 0)
            break;
        if (event.type != 1 || event.scan_code > 127) // 1: a key event
            continue;
        key_is_down[event.scan_code] = event.down != 0;
        if (event.down && key_queue_count < 32) {
            key_queue[(key_queue_head + key_queue_count) % 32] =
                (unsigned short)(event.scan_code << 8 | event.character);
            key_queue_count++;
        }
    }
    return (int)key_queue_count;
}

// Whether a key is held down, as of the last dos_poll_keys.
int dos_key_down(int scan_code) { return scan_code >= 0 && scan_code < 128 && key_is_down[scan_code]; }

// The next key press as scan code << 8 | character, or 0 if there is none.
int dos_read_key(void)
{
    if (key_queue_count == 0 && dos_poll_keys() == 0)
        return 0;
    int key = key_queue[key_queue_head];
    key_queue_head = (key_queue_head + 1) % 32;
    key_queue_count--;
    return key;
}

// Waits by watching the clock above. (HX's Sleep never returned when midnight
// fell inside the wait. DOS runs one program at a time, so there is nobody to
// give the processor to meanwhile.)
void dos_sleep(u32 milliseconds)
{
    u32 start = dos_ticks();
    while (dos_ticks() - start < milliseconds) { }
}

// ---- fibers (lib/Fibers.cs) ----------------------------------------------------
//
// Switching from one stack to another: save the registers a function must
// keep (ebx, esi, edi, ebp) and the coprocessor's control word, change
// stacks, restore them.
//
// A fiber's memory is [guard][pattern][shadow stack ->   <- stack][record]:
// managed code keeps its references on a shadow stack (see above), so each
// fiber needs one of its own, and gets the bottom of its stack. It grows up,
// the machine stack down. Fibers.cs writes a pattern below both and checks
// it on every switch back. Nothing stops the shadow stack growing into the
// machine stack.
//
// HX gives a program no way to protect a page, so there's no guard page, and
// getpagesize reports 2 KB: Fibers.cs reserves that much below each stack,
// which an overflow lands in before anything else, and sizes stacks for a
// machine of a few MB (32 KB, at least 4 KB). mprotect does nothing.
//
// The frame a switch leaves, from the saved esp up (24 bytes):
//   0   the coprocessor's control word
//   4   edi, esi, ebx, ebp
//   20  where the switch returns to

int getpagesize(void) { return 2048; }

int mprotect(void *address, size_t length, int protection)
{
    (void)address; (void)length; (void)protection;
    return 0;
}

int usleep(u32 microseconds)
{
    dos_sleep((microseconds + 999) / 1000);
    return 0;
}

// Catch.Run (zerocore/Runtime/Catch.cs): a throw inside body jumps back
// here through ZeroCatchJump, and ZeroCatchCall returns as if body had.
// There's no C library, so it's the compiler's own setjmp, which saves only
// the frame and stack pointers and the place to come back to; the compiler
// makes the function that calls it save every other register itself. Managed
// code passes its shadow stack first (see wasm/runtime.c); body gets the
// caller's.
void ZeroCatchCall(void *shadowStack, void **saved, void (*body)(void *, void *), void *argument)
{
    void *buffer[5];
    *saved = buffer;
    if (__builtin_setjmp(buffer) == 0)
        body(shadowStack, argument);
}

void ZeroCatchJump(void *shadowStack, void *saved)
{
    (void)shadowStack;
    __builtin_longjmp((void **)saved, 1);
}

// [esp+4]: where to store this stack's pointer; [esp+8]: the one to resume.
__attribute__((naked)) void ZeroFiberSwitch(void **save, void *resume)
{
    __asm__ volatile(
        "movl    4(%esp), %eax\n"
        "movl    8(%esp), %edx\n"
        "pushl   %ebp\n"
        "pushl   %ebx\n"
        "pushl   %esi\n"
        "pushl   %edi\n"
        "subl    $4, %esp\n"
        "fnstcw  (%esp)\n"
        "movl    %esp, (%eax)\n"
        "movl    %edx, %esp\n"
        "fldcw   (%esp)\n"
        "addl    $4, %esp\n"
        "popl    %edi\n"
        "popl    %esi\n"
        "popl    %ebx\n"
        "popl    %ebp\n"
        "ret\n");
}

// Where a new fiber's first switch returns to: ebx its argument, esi its
// entry, edi its shadow stack. The entry is an ordinary C# method, which
// takes its shadow stack as a hidden first argument. It never returns: it
// switches away for good.
__attribute__((naked)) static void ZeroFiberStart(void)
{
    __asm__ volatile(
        "pushl   %ebx\n"
        "pushl   %edi\n"
        "call    *%esi\n"
        "int3\n");
}

// top: the stack's top; bottom: its lowest byte, where Fibers.cs's pattern
// (64 bytes) starts. Returns the stack pointer to give ZeroFiberSwitch.
void *ZeroFiberInit(char *top, char *bottom, void (*entry)(void *), void *argument)
{
    u32 stack_top = (u32)top & ~(u32)15;
    u32 *frame = (u32 *)(stack_top - 32);
    char *shadow = bottom + 64;
    memset(frame, 0, 32);
    frame[0] = 0x027F;                  // the control word start() sets
    frame[1] = (u32)shadow;             // edi
    frame[2] = (u32)entry;              // esi
    frame[3] = (u32)argument;           // ebx
    frame[5] = (u32)ZeroFiberStart;     // the return address
    return frame;
}

// A stackalloc whose size isn't a constant calls this, with the size in eax,
// to move esp down: a page at a time, touching each, as Windows expects.
__asm__(
    ".globl  __alloca\n"
    "__alloca:\n"
    "        pushl   %ecx\n"
    "        leal    8(%esp), %ecx\n"
    "1:      cmpl    $0x1000, %eax\n"
    "        jb      2f\n"
    "        subl    $0x1000, %ecx\n"
    "        orl     $0, (%ecx)\n"
    "        subl    $0x1000, %eax\n"
    "        jmp     1b\n"
    "2:      subl    %eax, %ecx\n"
    "        orl     $0, (%ecx)\n"
    "        movl    %esp, %eax\n"
    "        movl    %ecx, %esp\n"
    "        movl    (%eax), %ecx\n"
    "        pushl   4(%eax)\n"
    "        ret\n");

// Ports, for hardware this file has no function for.
int dos_in(int port) { return in_byte((unsigned short)port); }
void dos_out(int port, int value) { out_byte((unsigned short)port, (unsigned char)value); }

// The PC speaker: a square wave at the given pitch until told otherwise; 0
// turns it off. The timer chip's third counter makes the wave (the clock rate
// divided by the pitch), and two bits of port 0x61 connect it to the speaker.
void dos_sound(u32 hertz)
{
    if (hertz == 0) {
        out_byte(0x61, in_byte(0x61) & ~3);
        return;
    }
    u32 divisor = 1193182 / hertz;
    if (divisor > 65535)
        divisor = 65535; // 18 Hz, the lowest it goes
    if (divisor == 0)
        divisor = 1;
    out_byte(0x43, 0xB6);
    out_byte(0x42, (unsigned char)divisor);
    out_byte(0x42, (unsigned char)(divisor >> 8));
    out_byte(0x61, in_byte(0x61) | 3);
}

// The mouse, through the mouse driver (MOUSE.COM, CTMOUSE.EXE, ...), which
// answers on interrupt 0x33; the DOS extender passes these calls on to it.
// Returns the number of buttons, or 0 if no driver is loaded.
int dos_mouse_start(void)
{
    // Without a driver the interrupt may point nowhere, and calling it would
    // crash. DPMI function 0x200 returns its address in cx:dx.
    u32 function = 0x200, vector = 0x33, segment = 0, offset = 0;
    __asm__ volatile("int $0x31" : "+a"(function), "+b"(vector), "+c"(segment), "+d"(offset) : : "cc", "memory");
    if ((segment & 0xFFFF) == 0 && (offset & 0xFFFF) == 0)
        return 0;
    // Function 0: reset. ax comes back as 0xFFFF if a driver is there.
    u32 status = 0, buttons = 0;
    __asm__ volatile("int $0x33" : "+a"(status), "+b"(buttons) : : "ecx", "edx", "cc", "memory");
    if ((status & 0xFFFF) != 0xFFFF)
        return 0;
    buttons &= 0xFFFF;
    return buttons == 0 || buttons == 0xFFFF ? 2 : (int)buttons;
}

// Where the mouse is, in the driver's coordinates (in mode 0x13: 0-639 across,
// 0-199 down). Returns the buttons held: bit 0 left, bit 1 right, bit 2 middle.
int dos_mouse_read(int *x, int *y)
{
    u32 function = 3, buttons = 0, column = 0, row = 0;
    __asm__ volatile("int $0x33" : "+a"(function), "+b"(buttons), "+c"(column), "+d"(row) : : "cc", "memory");
    *x = (int)(column & 0xFFFF);
    *y = (int)(row & 0xFFFF);
    return (int)(buttons & 7);
}

// Files, through HX's kernel32, which uses DOS's own file functions. Names
// are DOS names: 8.3, backslashes, drive letters.
// mode 0: read an existing file; 1: write a new file, replacing any there;
// 2: append, creating the file if needed. Returns 0 if it can't be opened.
void *dos_file_open(const char *path, int mode)
{
    static const u32 access[] = { 0x80000000, 0x40000000, 0x40000000 }; // GENERIC_READ, GENERIC_WRITE
    static const u32 disposition[] = { 3, 2, 4 };                        // OPEN_EXISTING, CREATE_ALWAYS, OPEN_ALWAYS
    if (mode < 0 || mode > 2)
        return 0;
    void *file = CreateFileA(path, access[mode], 1, 0, disposition[mode], 0x80, 0);
    if (file == (void *)-1)
        return 0;
    if (mode == 2)
        SetFilePointer(file, 0, 0, 2);
    return file;
}

// Both return the number of bytes read or written (less than asked at the end
// of the file or when the disk is full), or -1.
int dos_file_read(void *file, void *buffer, u32 count)
{
    u32 done = 0;
    return ReadFile(file, buffer, count, &done, 0) ? (int)done : -1;
}

int dos_file_write(void *file, const void *buffer, u32 count)
{
    u32 done = 0;
    return WriteFile(file, buffer, count, &done, 0) ? (int)done : -1;
}

// origin 0: from the start; 1: from the current position; 2: from the end.
// Returns the new position, or -1.
int dos_file_seek(void *file, int offset, int origin)
{
    if (origin < 0 || origin > 2)
        return -1;
    return (int)SetFilePointer(file, offset, 0, (u32)origin);
}

int dos_file_size(void *file) { return (int)GetFileSize(file, 0); }
void dos_file_close(void *file) { CloseHandle(file); }
int dos_file_delete(const char *path) { return DeleteFileA(path) != 0; }

// The C library's file functions, which lib/File.cs calls on every target,
// over the same kernel32 functions. A FILE * is the kernel32 handle itself:
// there is no buffering, so each fread or fwrite is one call to DOS.
//
// The mode is C's: "r", "w" or "a", then "+" to read and write both, and "x"
// (with "w") to fail if the file exists. A "b" anywhere changes nothing.
void *fopen(const char *path, const char *mode)
{
    int plus = 0, exclusive = 0;
    for (const char *m = mode + 1; *m; m++) {
        plus |= *m == '+';
        exclusive |= *m == 'x';
    }
    u32 access = plus ? 0xC0000000 : mode[0] == 'r' ? 0x80000000 : 0x40000000; // GENERIC_READ, GENERIC_WRITE
    u32 disposition;
    switch (mode[0]) {
    case 'r': disposition = 3; break;                  // OPEN_EXISTING
    case 'w': disposition = exclusive ? 1 : 2; break;  // CREATE_NEW, CREATE_ALWAYS
    case 'a': disposition = 4; break;                  // OPEN_ALWAYS
    default: return 0;
    }
    void *file = CreateFileA(path, access, 1, 0, disposition, 0x80, 0);
    if (file == (void *)-1)
        return 0;
    if (mode[0] == 'a')
        SetFilePointer(file, 0, 0, 2);
    return file;
}

// Nothing is buffered here, so there's nothing to write out.
int fflush(void *file) { (void)file; return 0; }

// FileStream turns the C library's buffering off; there is none here.
int setvbuf(void *file, char *buffer, int how, size_t size)
{
    (void)file; (void)buffer; (void)how; (void)size;
    return 0;
}

size_t fread(void *buffer, size_t size, size_t count, void *file)
{
    if (size == 0)
        return 0;
    int done = dos_file_read(file, buffer, size * count);
    return done < 0 ? 0 : (u32)done / size;
}

size_t fwrite(const void *buffer, size_t size, size_t count, void *file)
{
    if (size == 0)
        return 0;
    int done = dos_file_write(file, buffer, size * count);
    return done < 0 ? 0 : (u32)done / size;
}

// Positions are 64-bit, as POSIX's fseeko and ftello take them, but FAT16
// files stop at 2 GB, so only the low 32 bits reach DOS.
int fseeko(void *file, i64 offset, int origin)
{
    if (origin < 0 || origin > 2 || offset != (int)offset)
        return -1;
    return dos_file_seek(file, (int)offset, origin) == -1 ? -1 : 0;
}

i64 ftello(void *file) { return dos_file_seek(file, 0, 1); }

int fclose(void *file) { return CloseHandle(file) ? 0 : -1; }
int remove(const char *path) { return DeleteFileA(path) ? 0 : -1; }

// ---- 64-bit division ----------------------------------------------------------

// A 386 divides 64 bits by 32 at most, so the compiler calls these for C#'s
// long and ulong division. Long division in binary, one bit per step, after a
// shortcut for values that fit in 32 bits.
static u64 divide(u64 n, u64 d, u64 *remainder)
{
    if (d == 0)
        fatal("DivideByZeroException\n", 22);
    if ((n >> 32) == 0 && (d >> 32) == 0) {
        *remainder = (u32)n % (u32)d;
        return (u32)n / (u32)d;
    }
    u64 quotient = 0, rest = 0;
    for (int bit = 63; bit >= 0; bit--) {
        rest = (rest << 1) | ((n >> bit) & 1);
        if (rest >= d) {
            rest -= d;
            quotient |= (u64)1 << bit;
        }
    }
    *remainder = rest;
    return quotient;
}

u64 __udivdi3(u64 n, u64 d)
{
    u64 r;
    return divide(n, d, &r);
}

u64 __umoddi3(u64 n, u64 d)
{
    u64 r;
    divide(n, d, &r);
    return r;
}

// The casts go through u64 so that negating the smallest long is well defined.
i64 __divdi3(i64 n, i64 d)
{
    u64 r;
    u64 q = divide(n < 0 ? -(u64)n : (u64)n, d < 0 ? -(u64)d : (u64)d, &r);
    return (i64)((n < 0) != (d < 0) ? -q : q);
}

i64 __moddi3(i64 n, i64 d)
{
    u64 r;
    divide(n < 0 ? -(u64)n : (u64)n, d < 0 ? -(u64)d : (u64)d, &r);
    return (i64)(n < 0 ? -r : r);
}

// ---- math ---------------------------------------------------------------------

// The 387 has an instruction for each of these. Values go through memory
// ("m" operands), which keeps the assembly independent of how the compiler
// uses the coprocessor's register stack around it.

#define X87_1(name, code)                                                   \
    double name(double x)                                                   \
    {                                                                       \
        __asm__ volatile("fldl %0\n" code "\nfstpl %0" : "+m"(x));          \
        return x;                                                           \
    }

X87_1(fabs, "fabs")
X87_1(sqrt, "fsqrt")
X87_1(sin, "fsin")
X87_1(cos, "fcos")
X87_1(tan, "fptan\nfstp %%st(0)") // fptan also pushes 1.0
X87_1(rint, "frndint")            // rounds to nearest, ties to even: the mode in effect

// log_b(x) = log_b(2) * log2(x): fyl2x computes st1 * log2(st0).
#define X87_LOG(name, constant)                                             \
    double name(double x)                                                   \
    {                                                                       \
        __asm__ volatile(constant "\nfldl %0\nfyl2x\nfstpl %0" : "+m"(x));  \
        return x;                                                           \
    }

X87_LOG(log, "fldln2")
X87_LOG(log2, "fld1")
X87_LOG(log10, "fldlg2")

double atan2(double y, double x)
{
    __asm__ volatile("fldl %0\nfldl %1\nfpatan\nfstpl %0" : "+m"(y) : "m"(x));
    return y;
}

// frndint rounds the way the control word says; bits 10-11 select the mode.
static double round_with(double x, unsigned short mode)
{
    unsigned short saved, changed;
    __asm__ volatile("fnstcw %0" : "=m"(saved));
    changed = (unsigned short)((saved & ~0x0C00) | mode);
    __asm__ volatile("fldcw %1\nfldl %0\nfrndint\nfstpl %0\nfldcw %2" : "+m"(x) : "m"(changed), "m"(saved));
    return x;
}

double floor(double x) { return round_with(x, 0x0400); }
double ceil(double x) { return round_with(x, 0x0800); }
double trunc(double x) { return round_with(x, 0x0C00); }
double roundeven(double x) { return round_with(x, 0x0000); }
double nearbyint(double x) { return rint(x); }

// Halves round away from zero (C's round, .NET's MidpointRounding.AwayFromZero).
double round(double x)
{
    double whole = trunc(x);
    double rest = x - whole;
    if (rest >= 0.5)
        return whole + 1;
    if (rest <= -0.5)
        return whole - 1;
    return whole;
}

// The remainder with the sign of x: C#'s % on doubles. fprem does it in steps
// when the two are far apart, and says so in a status flag (C2, which sahf
// moves to the parity flag).
double fmod(double x, double y)
{
    __asm__ volatile("fldl %1\nfldl %0\n1: fprem\nfnstsw %%ax\nsahf\njp 1b\nfstp %%st(1)\nfstpl %0"
                     : "+m"(x) : "m"(y) : "ax", "cc");
    return x;
}

// A NaN loses to a number, as in C and in the compiler's own min and max.
double fmin(double a, double b) { return a != a ? b : b != b ? a : a < b ? a : b; }
double fmax(double a, double b) { return a != a ? b : b != b ? a : a > b ? a : b; }

// With st0 = t (finite): replaces it with 2^t. f2xm1 only takes a fraction,
// so t is split into a whole part, applied with fscale, and the rest.
#define X87_EXP2                                                            \
    "fld %%st(0)\n"           /* t, t          */                           \
    "frndint\n"               /* i, t          */                           \
    "fxch %%st(1)\n"          /* t, i          */                           \
    "fsub %%st(1), %%st\n"    /* f = t - i, i  */                           \
    "f2xm1\n"                 /* 2^f - 1, i    */                           \
    "fld1\n"                                                                \
    "faddp %%st, %%st(1)\n"   /* 2^f, i        */                           \
    "fscale\n"                /* 2^f * 2^i, i  */                           \
    "fstp %%st(1)\n"

// The intermediate t is kept at the 387's full 64-bit precision (control word
// bits 8-9), since an error in t is multiplied in the result.
#define X87_FULL_PRECISION(saved, changed)                                  \
    __asm__ volatile("fnstcw %0" : "=m"(saved));                            \
    changed = (unsigned short)(saved | 0x0300);

static int is_nan(double x) { return x != x; }
static int is_infinite(double x) { return x - x != 0 && !is_nan(x); }

double exp(double x)
{
    unsigned short saved, changed;
    if (is_nan(x))
        return x;
    if (is_infinite(x))
        return x > 0 ? x : 0;
    X87_FULL_PRECISION(saved, changed)
    __asm__ volatile("fldcw %1\nfldl %0\nfldl2e\nfmulp %%st, %%st(1)\n" X87_EXP2 "fstpl %0\nfldcw %2"
                     : "+m"(x) : "m"(changed), "m"(saved));
    return x;
}

// x > 0, both finite.
static double pow_positive(double x, double y)
{
    unsigned short saved, changed;
    X87_FULL_PRECISION(saved, changed)
    __asm__ volatile("fldcw %2\nfldl %1\nfldl %0\nfyl2x\n" X87_EXP2 "fstpl %0\nfldcw %3"
                     : "+m"(x) : "m"(y), "m"(changed), "m"(saved));
    return x;
}

double pow(double x, double y)
{
    if (y == 0 || x == 1)
        return 1;
    if (is_nan(x) || is_nan(y))
        return x + y;

    // An odd whole y keeps the sign of a negative x.
    int whole = !is_infinite(y) && trunc(y) == y;
    int odd = whole && trunc(y / 2) * 2 != y;
    double infinity = 1e308 * 10;

    if (is_infinite(y)) {
        double magnitude = fabs(x);
        if (magnitude == 1)
            return 1;
        return (magnitude > 1) == (y > 0) ? infinity : 0;
    }
    if (x == 0 || is_infinite(x)) {
        double result = (x == 0) == (y > 0) ? 0 : infinity;
        return x < 0 && odd ? -result : result;
    }
    if (x < 0) {
        if (!whole)
            return (x - x) / (x - x); // NaN
        double result = pow_positive(-x, y);
        return odd ? -result : result;
    }
    return pow_positive(x, y);
}

float fabsf(float x) { return (float)fabs(x); }
float sqrtf(float x) { return (float)sqrt(x); }
float floorf(float x) { return (float)floor(x); }
float ceilf(float x) { return (float)ceil(x); }
float truncf(float x) { return (float)trunc(x); }
float rintf(float x) { return (float)rint(x); }
float nearbyintf(float x) { return (float)rint(x); }
float roundevenf(float x) { return (float)roundeven(x); }
float roundf(float x) { return (float)round(x); }
float fmodf(float x, float y) { return (float)fmod(x, y); }
float fminf(float a, float b) { return (float)fmin(a, b); }
float fmaxf(float a, float b) { return (float)fmax(a, b); }
float expf(float x) { return (float)exp(x); }
float logf(float x) { return (float)log(x); }
float sinf(float x) { return (float)sin(x); }
float cosf(float x) { return (float)cos(x); }
float powf(float x, float y) { return (float)pow(x, y); }

float tanhf(float x)
{
    if (x > 20)
        return 1;
    if (x < -20)
        return -1;
    // tanh x = x - x^3/3 + ..., and below 1e-4 x^3/3 is beyond a float's
    // precision; (e - 1) / (e + 1) would lose x to cancellation (0 for 1e-30).
    if (fabs(x) < 1e-4)
        return x;
    double e = exp(2.0 * x);
    return (float)((e - 1) / (e + 1));
}

// ---- lib/FileSystem.cs ----------------------------------------------------

// Which system this is: 7, DOS.
int ZeroSystem(void) { return 7; }

// File.Exists calls stat only on Linux, macOS, Windows and WebAssembly; here
// it opens the file instead. These are there for the link, and always fail.
static int file_system_errno;
int *__errno_location(void) { return &file_system_errno; }
int __xstat64(int version, const char *path, void *out) { (void)version; (void)path; (void)out; file_system_errno = 38; return -1; }
int __lxstat64(int version, const char *path, void *out) { (void)version; (void)path; (void)out; file_system_errno = 38; return -1; }
