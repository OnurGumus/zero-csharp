// The native half of zerocore on the Commodore 64.
//
// The program is the same LLVM bitcode as on WebAssembly and DOS, compiled
// for the 6502 by llvm-mos after zero-bitcode's ll2mos has rewritten it. zerocore
// (C#) is the runtime, as everywhere; this file holds the entry point, the
// functions the compiled code expects from a runtime, and what C# imports
// from "libc", on top of llvm-mos's small C library and the C64's KERNAL.
//
// Two things shape it:
//
// C's int is 16 bits here and C#'s is 32. Everything compiled C# calls takes
// and returns `long` where C# has int or nint. The imports get the prefix
// cs_ (zero-bitcode's wasm2mos), so that C#'s calloc(nuint, nuint) and the C
// library's calloc(size_t, size_t) are two functions.
//
// An object reference in memory is a 4-byte slot (the layout is the 32-bit
// one, see sdk/ZeroCSharp.Bitcode/Ll2Mos.cs): the address and two zero bytes.

#include <cbm.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef long i32;
typedef unsigned long u32;
typedef long long i64;
typedef unsigned long long u64;

// ---- entry and exit ---------------------------------------------------------

i32 __managed__Main(i32 argc, char **argv);

// Leaves the program: back to BASIC's READY (the build links llvm-mos's
// save-basic.o, which puts back the memory BASIC needs). The exit code has
// nowhere to go on a C64, so it's written to an address the machine does
// nothing with: $D7FF, among the repeats of the sound chip's registers, where
// it falls on a register the chip doesn't have. VICE's "debug cartridge"
// (-debugcart) watches that address and ends the emulator with the code;
// c64/run.sh relies on it.
static void leave(int code)
{
    *(volatile unsigned char *)0xD7FF = (unsigned char)code;
    exit(code);
}

int main(void)
{
    leave((int)__managed__Main(0, 0));
    return 0;
}

void cs_exit(i32 code) { leave((int)code); }
void cs_abort(void) { leave(134); } // what a Unix shell reports for abort()

// zerocore's startup (Runtime/Startup.cs) needs the module header to set up
// static fields of reference types; C# can't take a data symbol's address.
extern char __ReadyToRunHeader[];
void *ZeroGetModuleHeader(void *shadowStack) { (void)shadowStack; return __ReadyToRunHeader; }

// ---- transitions between native and managed code ---------------------------

// Compiled C# keeps the locals that hold object references on a stack of its
// own, the "shadow stack", which every method gets as a hidden first
// argument. It isn't checked for overflow; deep recursion needs more.
static char shadow_stack[2048];

void *RhpReversePInvokeAndPushSparseVirtualUnwindFrame(i32 frame, void *a, i32 b)
{
    (void)frame; (void)a; (void)b;
    return shadow_stack;
}
void RhpReversePInvokeReturnAndPopSparseVirtualUnwindFrame(void *frame) { (void)frame; }
void RhpPushSparseVirtualUnwindFrame(void *frame, void *table, i32 index) { (void)frame; (void)table; (void)index; }
void RhpPopSparseVirtualUnwindFrame(void) {}

// Managed code calling native code: nothing to do without a GC.
i32 RhpTrapThreads; // 0: never stop for a GC
void RhpPInvoke(void *frame) { (void)frame; }
void RhpPInvokeReturn(void *frame) { (void)frame; }

// ---- exceptions: zero mode has none ------------------------------------------

// RhpExceptionThrown, the flag compiled code checks after calls, is missing
// on purpose: ll2mos replaces every read of it with zero, and if a write
// survived the optimizer the program wouldn't link. These two are only called
// from the code those checks guard, so they normally aren't linked in either.
void *RhpHandleExceptionWasmCatch(void *a, i32 b)
{
    (void)a; (void)b;
    fputs("zero mode: exception thrown\n", stdout);
    leave(134);
    return 0;
}

void RhpHandleUnhandledException(void *a, void *b)
{
    (void)a; (void)b;
    fputs("zero mode: unhandled exception\n", stdout);
    leave(134);
}

// ---- object references ---------------------------------------------------------

// Storing an object reference: no GC, so a plain store, of all four bytes.
void RhpAssignRef(u32 *destination, void *reference) { *destination = (unsigned)reference; }
void RhpCheckedAssignRef(u32 *destination, void *reference) { *destination = (unsigned)reference; }

// Call sites of interface methods hold this function's address until their
// first call. Nothing calls it: compiled code asks zerocore for the method.
void RhpInitialDynamicInterfaceDispatch(void) { leave(134); }

// ---- memory --------------------------------------------------------------------

// The heap: the memory between the program and the stack, handed out in
// 4-byte units. zerocore keeps flags in the two low bits of pointers to what
// it allocates (an interface call's cache, for one), as every 32-bit system
// lets it, so every block starts on a multiple of 4.
//
// llvm-mos's own malloc, with the wrapper it needed (it aligns to nothing),
// came to 2.1 to 2.7 KB; this one is about half that. Each block has a 4-byte
// header: its size in units, header included, and, while it is free, the
// next free block. Free blocks are kept in address order, and one freed next
// to another becomes part of it, so an arena that grows, is freed and grows
// again reuses the same memory. A request is served by the first free block
// big enough, else from memory never used yet.
typedef struct chunk {
    struct chunk *next;
    unsigned units;
} chunk;

// The heap runs from the end of the program to 1 KB below where the stack
// is when the first block is asked for, as llvm-mos's own malloc does.
// __rc0 and __rc1 are llvm-mos's stack pointer, in the zero page.
extern char __heap_start[], __stack_reserve_size[];
extern volatile unsigned char __rc0, __rc1;
static chunk *free_list;
static chunk *unused, *heap_end;

// The C library's names: anything in it that allocates gets this heap too.
// llvm-mos's malloc then never comes into the program, which is the point:
// two allocators would hand out the same memory. (If something ever needs
// another function from its malloc.cc, such as aligned_alloc, the link
// fails on a second definition of malloc instead.)
void *malloc(size_t size)
{
    unsigned units;
    chunk **link, *block;
    if (size > 0xFFF0)
        return 0;
    units = (size + 3) / 4 + 1;
    for (link = &free_list; (block = *link) != 0; link = &block->next) {
        if (block->units >= units) {
            if (block->units == units) {
                *link = block->next;
            } else { // the block's end is the new block
                block->units -= units;
                block += block->units;
                block->units = units;
            }
            return block + 1;
        }
    }
    if (!unused) { // the first request
        unsigned stack = __rc0 | (unsigned)__rc1 << 8;
        unused = (chunk *)(((unsigned)__heap_start + 3) & ~3u);
        heap_end = (chunk *)((stack - (unsigned)__stack_reserve_size) & ~3u);
        if (heap_end < unused)
            heap_end = unused;
    }
    if ((unsigned)(heap_end - unused) < units)
        return 0;
    block = unused;
    unused += units;
    block->units = units;
    return block + 1;
}

void free(void *pointer)
{
    chunk *block, *before = 0, *after = free_list;
    if (!pointer)
        return;
    block = (chunk *)pointer - 1;
    while (after && after < block) {
        before = after;
        after = after->next;
    }
    if (after && block + block->units == after) { // joins the one after it
        block->units += after->units;
        block->next = after->next;
    } else {
        block->next = after;
    }
    if (!before)
        free_list = block;
    else if (before + before->units == block) { // and the one before it
        before->units += block->units;
        before->next = block->next;
    } else {
        before->next = block;
    }
}

void *calloc(size_t count, size_t size)
{
    unsigned long total = (unsigned long)count * size;
    void *block = total > 0xFFF0 ? 0 : malloc((size_t)total);
    if (block)
        memset(block, 0, (size_t)total);
    return block;
}

void *realloc(void *pointer, size_t size)
{
    void *block;
    size_t had;
    if (!pointer)
        return malloc(size);
    block = malloc(size);
    if (block) {
        had = (((chunk *)pointer - 1)->units - 1) * 4;
        memcpy(block, pointer, had < size ? had : size);
        free(pointer);
    }
    return block;
}

// What C# imports, with its 32-bit sizes.
void *cs_calloc(u32 count, u32 size) { return (count | size) > 0xFFF0 ? 0 : calloc((size_t)count, (size_t)size); }
void *cs_malloc(u32 size) { return size > 0xFFF0 ? 0 : malloc((size_t)size); }
void cs_free(void *pointer) { free(pointer); }

void *cs_memset(void *destination, i32 value, u32 count) { return memset(destination, (int)value, (size_t)count); }

// ---- the screen and the keyboard as standard output and input -----------------

// Every character the program prints goes through here, as ASCII: putchar
// turns it into the C64's character set (PETSCII) and calls the KERNAL.
// c64/run.sh finds this function's address and has VICE log its argument,
// which is how a program's output reaches the terminal. Keep it a real
// function with the character as its only argument.
__attribute__((noinline)) void zero_out(char character) { putchar(character); }

i32 cs_write(i32 file, const char *buffer, u32 count)
{
    (void)file; // standard output and standard error are both the screen
    for (u32 i = 0; i < count; i++)
        zero_out(buffer[i]);
    return (i32)count;
}

i32 cs_puts(const char *text)
{
    while (*text)
        zero_out(*text++);
    zero_out('\n');
    return 0;
}

// Reads from the keyboard, a line at a time: the KERNAL's own line editor,
// with a cursor, hands a line over when RETURN is pressed (getchar turns it
// into ASCII). Like a terminal, a read returns what is left of the current
// line, the newline included, or as much of it as fits.
//
// A keyboard has no end of input, and programs wait for one ("until read
// returns 0"). So, as Ctrl-Z does on DOS, a line that holds only the C64's
// left-arrow key (top left; '_' in ASCII) ends the input for good.
static char line[82]; // the editor's longest line is 80 characters
static unsigned char line_length, line_at;
static char input_ended;

// ---- keys one at a time, for Console.ReadKey (lib/InKeys.cs) -----------------
//
// The POSIX calls ReadKey and KeyAvailable make. A "termios" is one byte, 1
// for raw; tcsetattr remembers it, and in raw mode cs_read returns the next
// key press, from the KERNAL's keyboard buffer (GETIN), as a Unix terminal
// would send it: letters, digits and signs in ASCII, RETURN as '\r', DEL as
// backspace, RUN/STOP as Escape, and the cursor and function keys as escape
// sequences (ESC [ A for cursor up, ESC O P for F1).

struct pollfd {
    i32 fd;
    short events, revents;
};

static char keys_raw;

i32 cs_isatty(i32 fd) { return fd == 0; }

i32 cs_tcgetattr(i32 fd, unsigned char *termios)
{
    if (fd != 0)
        return -1;
    termios[0] = 0;
    return 0;
}

void cs_cfmakeraw(unsigned char *termios) { termios[0] = 1; }

i32 cs_tcsetattr(i32 fd, i32 when, const unsigned char *termios)
{
    (void)when;
    if (fd != 0)
        return -1;
    keys_raw = (char)termios[0];
    return 0;
}

// A key from GETIN (PETSCII) as a terminal's bytes, or 0 bytes for one that
// has no meaning here (colors, CLR, the graphics signs).
static unsigned char keys_bytes(unsigned char key, char *out)
{
    static const char *const sequences[] = {
        "\x1b[B", "\x1b[A", "\x1b[C", "\x1b[D", "\x1b[H", "\x1b[2~",
        "\x1bOP", "\x1bOR", "\x1b[15~", "\x1b[18~", "\x1bOQ", "\x1bOS", "\x1b[17~", "\x1b[19~",
    };
    static const unsigned char codes[] = { 17, 145, 29, 157, 19, 148, 133, 134, 135, 136, 137, 138, 139, 140 };
    for (unsigned char i = 0; i < sizeof codes; i++) {
        if (codes[i] == key) {
            const char *sequence = sequences[i];
            unsigned char n = 0;
            while (sequence[n]) {
                out[n] = sequence[n];
                n++;
            }
            return n;
        }
    }
    char c;
    if (key == 13)
        c = '\r';
    else if (key == 20)
        c = 8; // DEL: backspace
    else if (key == 3)
        c = 0x1B; // RUN/STOP: Escape
    else if (key >= 0x41 && key <= 0x5A)
        c = (char)(key + 0x20); // unshifted letters are lower case here
    else if (key >= 0xC1 && key <= 0xDA)
        c = (char)(key - 0x80); // shifted ones upper case
    else if (key >= 0x20 && key <= 0x3F)
        c = (char)key;
    else if (key == 0x5F)
        c = '_'; // the left-arrow key
    else
        return 0;
    out[0] = c;
    return 1;
}

static i32 keys_read(char *buffer, u32 count)
{
    for (;;) {
        unsigned char key = cbm_k_getin();
        if (key == 0)
            continue;
        char bytes[8];
        unsigned char n = keys_bytes(key, bytes);
        if (n == 0)
            continue;
        if (n > count)
            n = (unsigned char)count;
        memcpy(buffer, bytes, n);
        return n;
    }
}

// Only standard input is asked about: whether the keyboard buffer has a key.
i32 cs_poll(struct pollfd *fds, u32 count, i32 timeout)
{
    i32 ready = 0;
    (void)timeout;
    for (u32 i = 0; i < count; i++) {
        fds[i].revents = 0;
        if (fds[i].fd == 0 && (fds[i].events & 1) && *(volatile unsigned char *)0xC6 > 0) {
            fds[i].revents = 1;
            ready++;
        }
    }
    return ready;
}

i32 cs_read(i32 file, char *buffer, u32 count)
{
    unsigned char available;
    (void)file;
    if (keys_raw)
        return keys_read(buffer, count);
    if (line_at == line_length) {
        if (input_ended)
            return 0;
        line_length = line_at = 0;
        for (;;) {
            int character = getchar();
            if (character < 0)
                break;
            if (line_length < sizeof line)
                line[line_length++] = (char)character;
            if (character == '\n')
                break;
        }
        if (line_length == 0 || (line_length == 2 && line[0] == '_' && line[1] == '\n')) {
            input_ended = 1;
            line_length = 0;
            return 0;
        }
    }
    available = line_length - line_at;
    if (count < available)
        available = (unsigned char)count;
    memcpy(buffer, line + line_at, available);
    line_at += available;
    return available;
}

// ---- the clock --------------------------------------------------------------------

// The KERNAL counts sixtieths of a second since the machine was switched on,
// in three bytes at $A0 (the highest first), from its timer interrupt. That
// is all the clock there is: both of clock_gettime's clocks start at power-on.
static u32 jiffies(void)
{
    volatile unsigned char *clock = (volatile unsigned char *)0xA0;
    unsigned char high, middle, low;
    do { // the interrupt can strike between two bytes: read until stable
        high = clock[0];
        middle = clock[1];
        low = clock[2];
    } while (high != clock[0] || middle != clock[1]);
    return ((u32)high << 16) | ((u32)middle << 8) | low;
}

i32 cs_clock_gettime(i32 clock, i64 *timespec)
{
    u32 ticks = jiffies();
    (void)clock;
    timespec[0] = ticks / 60;
    timespec[1] = (i64)(ticks % 60) * 16666667;
    return 0;
}

// ---- the rest of what zerocore and lib/ import -------------------------------------

// One thread: thread-local keys are plain slots.
#define KEYS 4
static void *key_values[KEYS];
static unsigned char key_count;

i32 cs_pthread_key_create(u32 *key, void *destructor)
{
    (void)destructor;
    if (key_count == KEYS)
        return 11; // EAGAIN
    *key = key_count++;
    return 0;
}

void *cs_pthread_getspecific(u32 key) { return key < key_count ? key_values[key] : 0; }

i32 cs_pthread_setspecific(u32 key, void *value)
{
    if (key >= key_count)
        return 22; // EINVAL
    key_values[key] = value;
    return 0;
}

char *cs_getenv(const char *name) { (void)name; return 0; } // no environment
i32 cs_sched_yield(void) { return 0; }                      // no other thread to yield to
i32 cs_get_nprocs(void) { return 1; }

// ---- floating point ------------------------------------------------------------------
//
// The 6502 has none; llvm-mos computes float and double in software, slowly
// and at a few KB per program that uses them. Its C library has no math
// functions, so the ones compiled code calls by itself are here: rounding,
// remainder and square root. Sin, Pow, Log and the like (Math.*, MathF.*)
// are not: a program that uses them doesn't link (undefined symbol cs_pow).

// C#'s conversions to integers: NaN is 0, out of range is the nearest limit.
#define SATURATING(name, T, F, LOW, HIGH) \
    T name(F x) { if (x != x) return 0; if (x <= (F)(LOW)) return (LOW); if (x >= (F)(HIGH)) return (HIGH); return (T)x; }
SATURATING(zero_fptosi_sat_i32_f64, i32, double, (-2147483647L - 1), 2147483647L)
SATURATING(zero_fptosi_sat_i32_f32, i32, float, (-2147483647L - 1), 2147483647L)
SATURATING(zero_fptosi_sat_i64_f64, i64, double, (-9223372036854775807LL - 1), 9223372036854775807LL)
SATURATING(zero_fptosi_sat_i64_f32, i64, float, (-9223372036854775807LL - 1), 9223372036854775807LL)
SATURATING(zero_fptoui_sat_i32_f64, u32, double, 0UL, 4294967295UL)
SATURATING(zero_fptoui_sat_i32_f32, u32, float, 0UL, 4294967295UL)
SATURATING(zero_fptoui_sat_i64_f64, u64, double, 0ULL, 18446744073709551615ULL)
SATURATING(zero_fptoui_sat_i64_f32, u64, float, 0ULL, 18446744073709551615ULL)

double trunc(double x)
{
    union { double value; u64 bits; } u = { x };
    int exponent = (int)((u.bits >> 52) & 0x7FF) - 1023;
    if (exponent < 0)
        return x < 0 ? -0.0 : 0.0;
    if (exponent >= 52)
        return x; // no fraction bits left (or infinity, or NaN)
    u.bits &= ~((1ULL << (52 - exponent)) - 1);
    return u.value;
}

double floor(double x) { double whole = trunc(x); return whole > x ? whole - 1 : whole; }
double ceil(double x) { double whole = trunc(x); return whole < x ? whole + 1 : whole; }

// To the nearest whole number, halves to the even one.
double roundeven(double x)
{
    double below = floor(x), fraction = x - below;
    if (fraction < 0.5)
        return below;
    if (fraction > 0.5)
        return below + 1;
    return trunc(below / 2) * 2 == below ? below : below + 1;
}

double fabs(double x) { return x < 0 ? -x : x; }

// Exact while x / y fits in 53 bits, which covers the % of ordinary numbers.
double fmod(double x, double y) { return x - trunc(x / y) * y; }

// The square root, correctly rounded: the root of the 53-bit mantissa, found
// a bit at a time the way long division finds a quotient, to 54 bits (one
// more than fits, to round by).
double sqrt(double x)
{
    union { double value; u64 bits; } u = { x };
    int exponent = (int)((u.bits >> 52) & 0x7FF);
    u64 mantissa = u.bits & 0x000FFFFFFFFFFFFFULL, root = 0, rest = 0, result;
    if (x != x || x == 0 || u.bits == 0x7FF0000000000000ULL)
        return x; // NaN, either zero, infinity
    if (x < 0) {
        u.bits = 0x7FF8000000000000ULL; // NaN
        return u.value;
    }
    if (exponent == 0) { // a number too small for the usual form
        exponent = 1;
        while (!(mantissa & (1ULL << 52))) {
            mantissa <<= 1;
            exponent--;
        }
    } else {
        mantissa |= 1ULL << 52;
    }
    exponent -= 1023;
    if (exponent & 1) { // an even exponent halves exactly
        mantissa <<= 1;
        exponent--;
    }
    // The root of mantissa * 2^54, two bits of it per step from the top.
    for (int i = 0; i < 54; i++) {
        u64 trial = (root << 2) | 1;
        rest <<= 2;
        if (i <= 26)
            rest |= (mantissa >> (52 - 2 * i)) & 3;
        root <<= 1;
        if (rest >= trial) {
            rest -= trial;
            root |= 1;
        }
    }
    result = root >> 1;
    if ((root & 1) && (rest != 0 || (result & 1)))
        result++;
    u.bits = ((u64)(exponent / 2 + 1023) << 52) + (result - (1ULL << 52));
    return u.value;
}

float truncf(float x) { return (float)trunc(x); }
float sqrtf(float x) { return (float)sqrt(x); }
float floorf(float x) { return (float)floor(x); }
float ceilf(float x) { return (float)ceil(x); }
float roundevenf(float x) { return (float)roundeven(x); }
float fabsf(float x) { return x < 0 ? -x : x; }
float fmodf(float x, float y) { return (float)fmod(x, y); }

// ---- the machine, for lib/C64.cs -----------------------------------------------------

i32 cs_c64_peek(i32 address) { return *(volatile unsigned char *)(unsigned)address; }
void cs_c64_poke(i32 address, i32 value) { *(volatile unsigned char *)(unsigned)address = (unsigned char)value; }

// The KERNAL's own "clear screen" character: empties the screen and moves
// the cursor that Out writes at to the top left corner.
void cs_c64_clear(void) { cbm_k_chrout(147); }
void cs_c64_copy(i32 address, const void *source, i32 count) { memcpy((void *)(unsigned)address, source, (size_t)count); }

// The code of a character in screen memory, in the character set with both
// cases (which llvm-mos's startup code selects): not ASCII, and not PETSCII.
static unsigned char screen_code(unsigned short character)
{
    if (character >= 'a' && character <= 'z')
        return (unsigned char)(character - 'a' + 1);
    if ((character >= ' ' && character <= '?') || (character >= 'A' && character <= 'Z'))
        return (unsigned char)character;
    switch (character) {
    case '@': return 0;
    case '[': return 27;
    case ']': return 29;
    case '_': return 100;
    default:  return '?';
    }
}

// Writes UTF-16 text into screen memory from a cell (row * 40 + column) on,
// in one color. The caller has checked that it fits.
void cs_c64_print(i32 cell, const unsigned short *text, i32 count, i32 color)
{
    unsigned char *screen = (unsigned char *)0x0400 + (unsigned)cell;
    unsigned char *colors = (unsigned char *)0xD800 + (unsigned)cell;
    for (unsigned i = 0; i < (unsigned)count; i++) {
        screen[i] = screen_code(text[i]);
        colors[i] = (unsigned char)color;
    }
}

// Returns when the video chip starts drawing the given line of the picture
// (0-255; the lines beyond, in the lower border, can't be asked for).
void cs_c64_wait_line(i32 line)
{
    volatile unsigned char *raster = (volatile unsigned char *)0xD012;
    unsigned char wanted = (unsigned char)line;
    while (*raster != wanted) {}
}

// The key pressed since the last call, as the KERNAL's keyboard buffer has
// it (PETSCII), or 0.
i32 cs_c64_key(void)
{
    return cbm_k_getin();
}

u32 cs_c64_ticks(void) { return jiffies(); }

// ---- fibers (lib/Fibers.cs) ---------------------------------------------------------
//
// A 6502 program has two stacks. llvm-mos keeps locals on a stack of its own
// in memory, whose pointer is __rc0/__rc1 in the zero page, and a fiber gets
// one of those like on any machine: its own memory. But JSR, RTS and the
// registers functions save go on the processor's stack, which is always page
// 1 ($0100-$01FF), 256 bytes for everyone. So a switch copies the part of
// page 1 in use (from the stack pointer S up to $01FF) onto the fiber's own
// stack, next to the registers a function must keep (__rc20 to __rc31), and
// copies the next fiber's part back. Interrupts are off while page 1 is
// being rewritten.
//
// The frame a switch leaves, from the saved stack pointer up:
//   0   __rc20 to __rc31
//   12  n, the number of bytes of page 1 in use
//   13  those bytes, from S+1 to $01FF; the first two are where the switch
//       returns to
//
// A fiber's memory is [guard][pattern][shadow stack ->  <- stack][record]:
// managed code keeps its references on a shadow stack, which grows up from
// above the pattern Fibers.cs checks; the stack above grows down. Nothing
// stops either growing into the other.
//
// No memory protection, so no guard page: getpagesize reports the 6502's
// page, 256 bytes, which Fibers.cs reserves below each stack and takes for a
// small machine (stacks of 2 KB, at least 512 bytes). mprotect does nothing.
//
// ZeroFiberSwitch is assembly at the top level of this file, so the link-time
// optimizer can't look into it. That matters: llvm-mos gives a function that
// can't be running twice at once static memory for its locals instead of
// stack. To the optimizer, a function that might reach the switch might call
// back into any fiber's entry, so its locals stay on the stack, and two
// fibers waiting inside it each keep their own.

i32 cs_getpagesize(void) { return 256; }

i32 cs_mprotect(void *address, u32 length, i32 protection)
{
    (void)address; (void)length; (void)protection;
    return 0;
}

i32 cs_usleep(u32 microseconds)
{
    u32 start = jiffies(), wait = (microseconds + 16666) / 16667;
    while (jiffies() - start < wait) { }
    return 0;
}

void cs_ZeroFiberSwitch(void **save, void *resume);
void ZeroFiberStart(void);

// top: the stack's top; bottom: its lowest byte, where Fibers.cs's pattern
// (64 bytes) starts. Returns the stack pointer to give ZeroFiberSwitch, a
// frame whose page 1 holds only the return into ZeroFiberStart, with __rc20
// the entry, __rc22 its argument and __rc24 the shadow stack.
void *cs_ZeroFiberInit(char *top, char *bottom, void (*entry)(void *), void *argument)
{
    unsigned char *frame = (unsigned char *)top - 15;
    unsigned start = (unsigned)ZeroFiberStart - 1; // RTS adds 1
    memset(frame, 0, 15);
    *(void **)&frame[0] = (void *)entry;
    *(void **)&frame[2] = argument;
    *(void **)&frame[4] = bottom + 64;
    frame[12] = 2;
    frame[13] = (unsigned char)start;
    frame[14] = (unsigned char)(start >> 8);
    return frame;
}

__asm__(
    ".section .text.cs_ZeroFiberSwitch,\"ax\",@progbits\n"
    ".globl cs_ZeroFiberSwitch\n"
    "cs_ZeroFiberSwitch:\n"
    // __rc2/__rc3: where to store this stack's pointer; __rc4/__rc5: the
    // one to resume. __rc16 to __rc19 are free to use (the caller's).
    "    php\n"
    "    pla\n"
    "    sta __rc16\n"            // the flags, interrupts on or off
    "    sei\n"
    "    tsx\n"
    "    txa\n"
    "    eor #$ff\n"
    "    sta __rc18\n"            // n = $FF - S
    "    sec\n"                   // make room: 13 + n bytes
    "    lda __rc0\n"
    "    sbc #13\n"
    "    sta __rc0\n"
    "    bcs 1f\n"
    "    dec __rc1\n"
    "1:  sec\n"
    "    lda __rc0\n"
    "    sbc __rc18\n"
    "    sta __rc0\n"
    "    bcs 2f\n"
    "    dec __rc1\n"
    "2:  ldy #0\n"
    "3:  lda mos16(__rc20),y\n"
    "    sta (__rc0),y\n"
    "    iny\n"
    "    cpy #12\n"
    "    bne 3b\n"
    "    lda __rc18\n"
    "    sta (__rc0),y\n"
    "4:  cpx #$ff\n"              // page 1, from S+1 up
    "    beq 5f\n"
    "    inx\n"
    "    iny\n"
    "    lda $100,x\n"
    "    sta (__rc0),y\n"
    "    jmp 4b\n"
    "5:  ldy #0\n"                // *save = the stack pointer
    "    lda __rc0\n"
    "    sta (__rc2),y\n"
    "    iny\n"
    "    lda __rc1\n"
    "    sta (__rc2),y\n"
    "    lda __rc4\n"             // the other fiber's stack
    "    sta __rc0\n"
    "    lda __rc5\n"
    "    sta __rc1\n"
    "    ldy #0\n"
    "6:  lda (__rc0),y\n"
    "    sta mos16(__rc20),y\n"
    "    iny\n"
    "    cpy #12\n"
    "    bne 6b\n"
    "    lda (__rc0),y\n"
    "    sta __rc18\n"
    "    eor #$ff\n"
    "    tax\n"
    "    txs\n"                   // S = $FF - n
    "7:  cpx #$ff\n"
    "    beq 8f\n"
    "    inx\n"
    "    iny\n"
    "    lda (__rc0),y\n"
    "    sta $100,x\n"
    "    jmp 7b\n"
    "8:  clc\n"                   // drop the frame
    "    lda __rc0\n"
    "    adc #13\n"
    "    sta __rc0\n"
    "    bcc 9f\n"
    "    inc __rc1\n"
    "9:  clc\n"
    "    lda __rc0\n"
    "    adc __rc18\n"
    "    sta __rc0\n"
    "    bcc 10f\n"
    "    inc __rc1\n"
    "10: lda __rc16\n"
    "    pha\n"
    "    plp\n"
    "    rts\n"
    // Where a new fiber's first switch returns to: the entry is an ordinary
    // C# method, which takes its shadow stack as a hidden first argument, and
    // the argument after it. It never returns: it switches away for good.
    ".globl ZeroFiberStart\n"
    "ZeroFiberStart:\n"
    "    lda __rc24\n"
    "    sta __rc2\n"
    "    lda __rc25\n"
    "    sta __rc3\n"
    "    lda __rc22\n"
    "    sta __rc4\n"
    "    lda __rc23\n"
    "    sta __rc5\n"
    "    jmp (__rc20)\n");
