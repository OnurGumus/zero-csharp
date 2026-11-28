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
// c64/run.sh relies on it. The KERNAL ROM and its interrupt go back on
// first, in case the program switched them off (C64.KernalRom,
// C64.KernalInterrupt): without them BASIC would have no cursor and read no
// keys.
static void leave(int code)
{
    // $36 at the processor port: the KERNAL ROM in, the RAM at $A000 kept.
    *(volatile unsigned char *)1 = (*(volatile unsigned char *)1 & 0xF8) | 0x06;
    *(volatile unsigned char *)0xDC0D = 0x81;
    *(volatile unsigned char *)0xD7FF = (unsigned char)code;
    exit(code);
}

// BASIC's own bookkeeping, mended before llvm-mos's save-basic.o copies its
// zero page (.init.005) to put it back at the end. LOAD sets BASIC's end of
// program (VARTAB, $2D/$2E) to the end of the file; a program bigger than
// BASIC's 38,911 bytes ends past BASIC's top of memory (MEMSIZ, $37/$38),
// and BASIC, given that back, says ?OUT OF MEMORY ERROR to every line typed
// after the program ends (or at once, to what it finds after SYS). Such a
// program BASIC can't hold anyway, so here it gets what NEW leaves: an
// empty program at the start of BASIC's memory (TXTTAB, $2B/$2C), and the
// variables after it. A smaller program is left as it was, and RUN runs it
// again.
//
// And the program ends through BASIC's warm start ($A002, what RUN/STOP and
// RESTORE do), last of all the exit code (.fini.999, after save-basic.o has
// put BASIC's zero page back and the processor port has the ROMs in again):
// it empties the 6502's stack, closes the channels and prints READY. A
// return into the SYS line that started the program would have BASIC go on
// reading that line, which a program bigger than BASIC's memory may have
// overwritten.
__asm__(
    "  .section .fini.999,\"ax\",@progbits\n"
    "  jmp ($a002)\n"
    "  .text\n");
__asm__(
    "  .section .init.004,\"ax\",@progbits\n"
    "  lda $37\n"                     // MEMSIZ >= VARTAB: it fits
    "  cmp $2d\n"
    "  lda $38\n"
    "  sbc $2e\n"
    "  bcs 1f\n"
    "  ldy #0\n"                      // an empty program: its link 0
    "  tya\n"
    "  sta ($2b),y\n"
    "  iny\n"
    "  sta ($2b),y\n"
    "  clc\n"                         // VARTAB, ARYTAB, STREND after it
    "  lda $2b\n"
    "  adc #2\n"
    "  sta $2d\n"
    "  sta $2f\n"
    "  sta $31\n"
    "  lda $2c\n"
    "  adc #0\n"
    "  sta $2e\n"
    "  sta $30\n"
    "  sta $32\n"
    "1:\n"
    "  .text\n");

int main(void)
{
    leave((int)__managed__Main(0, 0));
    return 0;
}

void cs_exit(i32 code) { leave((int)code); }

void zero_out(char character);
static void trace(void);

// What a Unix shell reports for abort(). Environment.FailFast ends here,
// after printing its message (a NullReferenceException, a failed check).
void cs_abort(void)
{
    trace();
    leave(134);
}

// Where the program was when it stopped: one line, "at $0DE4 $1DD9 ...",
// the addresses of the JSR instructions whose calls haven't returned yet,
// innermost first (FailFast's own and the helper that called it come
// first). c64/run.sh turns them into method names.
//
// They are found on the processor's stack, page 1, from the stack pointer
// up: a JSR pushes the address of its own last byte. Functions also save
// registers there, so a pair of bytes counts as a return address only if
// it points just past a JSR into the program's code. A saved value that
// happens to look like one would show up as an extra line.
extern char _start[];
static void hex(unsigned value)
{
    static const char digits[] = "0123456789ABCDEF";
    zero_out('$');
    for (int shift = 12; shift >= 0; shift -= 4)
        zero_out(digits[(value >> shift) & 15]);
}

static void trace(void)
{
    unsigned char sp;
    __asm__ volatile("tsx" : "=x"(sp));
    const volatile unsigned char *stack = (const volatile unsigned char *)0x100;
    unsigned found = 0;
    zero_out('a');
    zero_out('t');
    for (unsigned i = sp + 1u; i < 0xFF && found < 12; i++) {
        unsigned last = stack[i] | (unsigned)stack[i + 1] << 8;   // the JSR's last byte
        unsigned jsr = last - 2;
        if (jsr >= (unsigned)_start && last < 0xD000 && *(const volatile unsigned char *)jsr == 0x20) {
            zero_out(' ');
            hex(jsr);
            found++;
            i++;
        }
    }
    zero_out('\n');
}

// zerocore's startup (Runtime/Startup.cs) needs the module header to set up
// static fields of reference types; C# can't take a data symbol's address.
extern char __ReadyToRunHeader[];
void *ZeroGetModuleHeader(void *shadowStack) { (void)shadowStack; return __ReadyToRunHeader; }

// ---- transitions between native and managed code ---------------------------

// Compiled C# keeps the locals that hold object references on a stack of its
// own, the "shadow stack", which every method gets as a hidden first
// argument. It isn't checked for overflow; deep recursion needs more. A
// project sets its size with ZeroC64ShadowStack (zero-bitcode passes it as
// ZERO_SHADOW_STACK): a program that keeps few objects in locals can give
// memory back, one that recurses deeply can have more.
#ifndef ZERO_SHADOW_STACK
#define ZERO_SHADOW_STACK 2048
#endif
char zero_shadow_stack[ZERO_SHADOW_STACK];
#define shadow_stack zero_shadow_stack

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
void *cs_realloc(void *pointer, u32 size) { return size > 0xFFF0 ? 0 : realloc(pointer, (size_t)size); }

void *cs_memset(void *destination, i32 value, u32 count) { return memset(destination, (int)value, (size_t)count); }
void *cs_memmove(void *destination, const void *source, u32 count) { return memmove(destination, source, (size_t)count); }

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
// is C64.Ticks. It stands still whenever interrupts are off: while a program
// has switched the KERNAL's interrupt off (C64.KernalInterrupt), has the ROMs
// switched out, or during disk access.
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

// clock_gettime and usleep (Clock, Stopwatch, Environment.TickCount64,
// Fiber.Sleep, the timers) count those sixtieths too. A program that turns
// the KERNAL's interrupt off (C64.KernalInterrupt) turns on keep_time
// below, and then they go on counting while the jiffies stand still: CIA
// 2's time-of-day clock, which counts the mains' cycles into tenths of a
// second and needs no interrupt, says how much time went by, and the clock
// moves a tenth at a time. Only such a program has that code: it is
// reached through a pointer, which only c64_keep_time sets.
static u32 clock_extra;                 // sixtieths counted while the jiffies stood still
static void (*clock_check)(void);       // keep_time, once c64_keep_time has run

static u32 clock_ticks(void)
{
    if (clock_check)
        clock_check();
    return jiffies() + clock_extra;
}

i32 cs_clock_gettime(i32 clock, i64 *timespec)
{
    u32 ticks = clock_ticks();
    (void)clock;
    timespec[0] = ticks / 60;
    timespec[1] = (i64)(ticks % 60) * 16666667;
    return 0;
}

// keep_time counts on the time-of-day clock only when the jiffies stood
// still across two tenths, which never happens while the interrupt runs
// (it comes every sixtieth), so no stretch of time is counted by both.
// c64_keep_time starts the time-of-day clock at 0, set to the mains' 50 Hz
// on a PAL machine (the KERNAL's flag at $02A6), 60 on NTSC: a program that
// keeps time in CIA 2's TOD registers itself shares them. CIA 2's timers
// are left alone (RS-232 and NMI routines use them).
#define TOD2 ((volatile unsigned char *)0xDD08)

static unsigned char tod_stalled, tod_still; // interrupts off; tenths with no jiffy
static unsigned char tod_hour;               // the time-of-day clock at the last check: the hour (0-23)
static unsigned short tod_in_hour;           // and tenths into it (below 36,000)
static u32 tod_jiffies;                      // the KERNAL's count at the last check

static unsigned char bcd(unsigned char v) { return (unsigned char)((v >> 4) * 10 + (v & 15)); }

// Tenths of a second on the time-of-day clock since the last check. The
// hours are 12-hour BCD with bit 7 for PM, starting at 0; reading them
// holds the other registers until the tenths are read.
static u32 tod_passed(void)
{
    unsigned char hours = TOD2[3], minutes = TOD2[2], seconds = TOD2[1], tenths = TOD2[0];
    unsigned char hour = (unsigned char)(bcd(hours & 0x1F) % 12 + (hours & 0x80 ? 12 : 0));
    unsigned short in_hour = (unsigned short)((bcd(minutes) * 60 + bcd(seconds)) * 10 + tenths);
    // Whole hours, then the rest; modulo 2^32 the sum is right when the
    // rest is negative.
    u32 passed = (u32)(unsigned char)((hour + 24 - tod_hour) % 24) * 36000 + in_hour - tod_in_hour;
    tod_hour = hour;
    tod_in_hour = in_hour;
    return passed;
}

static void keep_time(void)
{
    u32 now = jiffies(), tenths = tod_passed();
    if (now != tod_jiffies) {
        tod_jiffies = now;
        tod_still = 0;
        tod_stalled = 0;
        return;
    }
    // Two tenths and not a jiffy: interrupts are off, since the last jiffy,
    // and all those tenths count; from then on, each one.
    u32 still = tod_still + tenths;
    if (tod_stalled || still >= 2) {
        clock_extra += still * 6;
        tod_stalled = 1;
        tod_still = 0;
    } else {
        tod_still = (unsigned char)still;
    }
}

void cs_c64_keep_time(void)
{
    if (clock_check)
        return;
    volatile unsigned char *control = (volatile unsigned char *)0xDD0E;
    *control = (*control & 0x7F) | (*(volatile unsigned char *)0x02A6 ? 0x80 : 0);
    *(volatile unsigned char *)0xDD0F &= 0x7F; // TOD writes set the time, not the alarm
    TOD2[3] = 0; // writing the hours stops it, the tenths start it again
    TOD2[2] = 0;
    TOD2[1] = 0;
    TOD2[0] = 0;
    tod_jiffies = jiffies();
    clock_check = keep_time;
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

// ---- integer division and multiplication ---------------------------------------------
//
// The 6502 can't multiply or divide. For `/`, `%` and `*` it can't do with
// shifts, llvm-mos calls __udivsi3, __mulsi3 and the like from its library,
// written in C: dividing C#'s 32-bit numbers there costs 1,100-5,300 cycles
// of a frame's 19,700, multiplying 1,250-2,500, and printing a number
// (lib/Str.cs, Fmt.UInt: ulong % 10 and / 10) up to 16,000 a digit. The
// library keeps them in three pieces (twenty divisions from 8 to 64 bits;
// the four that give quotient and remainder together; four
// multiplications), and replacing part of a piece means defining all of
// it, here.
//
// The 16-, 32- and 64-bit divisions and the 32-bit multiplication are in
// assembly: 2-5 times faster than the library's for the numbers programs
// use, 1.3-7 for multiplying. The rest are C around them, or the library's
// own method in C, as fast as before (on 8 bits it is hard to beat). The
// library is faster only where its divisor and dividend are within a bit
// or two of each other, by a few hundred cycles.
//
// Arguments and results follow llvm-mos's calling convention: the first
// number's bytes in A, X, __rc2, __rc3, the second's after them, low byte
// first; the result in the first's place. __rc2-__rc19, A, X and Y are the
// caller's to save.

typedef signed char i8;
typedef unsigned char u8;
typedef short i16;
typedef unsigned short u16;

// Unsigned division in assembly. Long division, a bit at a time: the
// dividend shifts left into the remainder, and wherever the divisor fits it
// is subtracted and a 1 shifts into the quotient (where the dividend's bit
// was). Two shortcuts first: a dividend below the divisor is the remainder,
// and the dividend's top bytes go into the remainder whole while it stays
// below the divisor (a zero byte always does). The remainder is then kept
// as wide as the divisor, in A when that is a byte (about 35 cycles a bit),
// with the carry as its extra top bit.
//
// zero_udivmod32: n in A, X, __rc2, __rc3 and d (not 0) in __rc4-__rc7;
// the quotient comes out in __rc8-__rc11, the remainder in __rc12-__rc15.
__asm__(
    "  .section .text.zero_udivmod32,\"ax\",@progbits\n"
    "zero_udivmod32:\n"
    "  sta __rc8\n"
    "  stx __rc9\n"
    "  lda __rc2\n"
    "  sta __rc10\n"
    "  lda __rc3\n"
    "  sta __rc11\n"
    "  lda __rc8\n"                   // n < d: the quotient is 0
    "  cmp __rc4\n"
    "  lda __rc9\n"
    "  sbc __rc5\n"
    "  lda __rc10\n"
    "  sbc __rc6\n"
    "  lda __rc11\n"
    "  sbc __rc7\n"
    "  bcs .Ldiv32\n"
    "  lda __rc8\n"
    "  sta __rc12\n"
    "  lda __rc9\n"
    "  sta __rc13\n"
    "  lda __rc10\n"
    "  sta __rc14\n"
    "  lda __rc11\n"
    "  sta __rc15\n"
    "  lda #0\n"
    "  sta __rc8\n"
    "  sta __rc9\n"
    "  sta __rc10\n"
    "  sta __rc11\n"
    "  rts\n"
    ".Ldiv32:\n"
    "  lda __rc10\n"                  // n under 65,536 (and so d): 16 bits
    "  ora __rc11\n"
    "  bne .Ldiv32_wide\n"
    "  sta __rc14\n"
    "  sta __rc15\n"
    "  jmp zero_udivmod16_from32\n"
    ".Ldiv32_wide:\n"
    "  lda #0\n"
    "  sta __rc12\n"
    "  sta __rc13\n"
    "  sta __rc14\n"
    "  sta __rc15\n"
    "  ldy #32\n"                     // bits left
    ".Ldiv32_zero:\n"                 // a zero top byte (n >= d: not all)
    "  lda __rc11\n"
    "  bne .Ldiv32_byte_in\n"
    "  lda __rc10\n"
    "  sta __rc11\n"
    "  lda __rc9\n"
    "  sta __rc10\n"
    "  lda __rc8\n"
    "  sta __rc9\n"
    "  lda #0\n"
    "  sta __rc8\n"
    "  tya\n"
    "  sec\n"
    "  sbc #8\n"
    "  tay\n"
    "  jmp .Ldiv32_zero\n"
    ".Ldiv32_byte_in:\n"              // remainder * 256 + top byte < d?
    "  lda __rc15\n"
    "  bne .Ldiv32_bits\n"
    "  lda __rc11\n"
    "  cmp __rc4\n"
    "  lda __rc12\n"
    "  sbc __rc5\n"
    "  lda __rc13\n"
    "  sbc __rc6\n"
    "  lda __rc14\n"
    "  sbc __rc7\n"
    "  bcs .Ldiv32_bits\n"
    "  lda __rc14\n"                  // yes: it's the remainder, 8 zero bits
    "  sta __rc15\n"                  // of the quotient (n >= d: not all 32)
    "  lda __rc13\n"
    "  sta __rc14\n"
    "  lda __rc12\n"
    "  sta __rc13\n"
    "  lda __rc11\n"
    "  sta __rc12\n"
    "  lda __rc10\n"
    "  sta __rc11\n"
    "  lda __rc9\n"
    "  sta __rc10\n"
    "  lda __rc8\n"
    "  sta __rc9\n"
    "  lda #0\n"
    "  sta __rc8\n"
    "  tya\n"
    "  sec\n"
    "  sbc #8\n"
    "  tay\n"
    "  jmp .Ldiv32_byte_in\n"
    ".Ldiv32_bits:\n"
    "  lda __rc6\n"
    "  ora __rc7\n"
    "  bne .Ldiv32_long\n"
    "  lda __rc5\n"
    "  bne .Ldiv32_short\n"
    "  lda __rc12\n"                  // a byte: the remainder in A
    ".Ldiv32_byte:\n"
    "  asl __rc8\n"
    "  rol __rc9\n"
    "  rol __rc10\n"
    "  rol __rc11\n"
    "  rol a\n"
    "  bcs .Ldiv32_byte_fits\n"       // a ninth bit: more than d for sure
    "  cmp __rc4\n"
    "  bcc .Ldiv32_byte_next\n"
    ".Ldiv32_byte_fits:\n"
    "  sbc __rc4\n"                   // the carry is set both ways here
    "  inc __rc8\n"
    ".Ldiv32_byte_next:\n"
    "  dey\n"
    "  bne .Ldiv32_byte\n"
    "  sta __rc12\n"
    "  rts\n"
    ".Ldiv32_short:\n"                // 16 bits
    "  asl __rc8\n"
    "  rol __rc9\n"
    "  rol __rc10\n"
    "  rol __rc11\n"
    "  rol __rc12\n"
    "  rol __rc13\n"
    "  bcs .Ldiv32_short_fits\n"
    "  lda __rc12\n"
    "  cmp __rc4\n"
    "  lda __rc13\n"
    "  sbc __rc5\n"
    "  bcc .Ldiv32_short_next\n"
    ".Ldiv32_short_fits:\n"
    "  lda __rc12\n"
    "  sbc __rc4\n"
    "  sta __rc12\n"
    "  lda __rc13\n"
    "  sbc __rc5\n"
    "  sta __rc13\n"
    "  inc __rc8\n"
    ".Ldiv32_short_next:\n"
    "  dey\n"
    "  bne .Ldiv32_short\n"
    "  rts\n"
    ".Ldiv32_long:\n"                 // 32 bits
    "  asl __rc8\n"
    "  rol __rc9\n"
    "  rol __rc10\n"
    "  rol __rc11\n"
    "  rol __rc12\n"
    "  rol __rc13\n"
    "  rol __rc14\n"
    "  rol __rc15\n"
    "  bcs .Ldiv32_long_fits\n"
    "  lda __rc12\n"
    "  cmp __rc4\n"
    "  lda __rc13\n"
    "  sbc __rc5\n"
    "  lda __rc14\n"
    "  sbc __rc6\n"
    "  lda __rc15\n"
    "  sbc __rc7\n"
    "  bcc .Ldiv32_long_next\n"
    ".Ldiv32_long_fits:\n"
    "  lda __rc12\n"
    "  sbc __rc4\n"
    "  sta __rc12\n"
    "  lda __rc13\n"
    "  sbc __rc5\n"
    "  sta __rc13\n"
    "  lda __rc14\n"
    "  sbc __rc6\n"
    "  sta __rc14\n"
    "  lda __rc15\n"
    "  sbc __rc7\n"
    "  sta __rc15\n"
    "  inc __rc8\n"
    ".Ldiv32_long_next:\n"
    "  dey\n"
    "  bne .Ldiv32_long\n"
    "  rts\n"
    "  .section .text.__udivsi3,\"ax\",@progbits\n"
    "  .globl __udivsi3\n"
    "__udivsi3:\n"
    "  jsr zero_udivmod32\n"
    ".Ludivsi3_quotient:\n"
    "  lda __rc10\n"
    "  sta __rc2\n"
    "  lda __rc11\n"
    "  sta __rc3\n"
    "  lda __rc8\n"
    "  ldx __rc9\n"
    "  rts\n"
    "  .section .text.__umodsi3,\"ax\",@progbits\n"
    "  .globl __umodsi3\n"
    "__umodsi3:\n"
    "  jsr zero_udivmod32\n"
    "  lda __rc14\n"
    "  sta __rc2\n"
    "  lda __rc15\n"
    "  sta __rc3\n"
    "  lda __rc12\n"
    "  ldx __rc13\n"
    "  rts\n"
    "  .section .text.__udivmodsi4,\"ax\",@progbits\n"
    "  .globl __udivmodsi4\n"
    "__udivmodsi4:\n"                 // both: the remainder through a pointer,
    "  ldy __rc8\n"                   // in __rc8, __rc9
    "  sty __rc16\n"
    "  ldy __rc9\n"
    "  sty __rc17\n"
    "  jsr zero_udivmod32\n"
    "  ldy #0\n"
    "  lda __rc12\n"
    "  sta (__rc16),y\n"
    "  iny\n"
    "  lda __rc13\n"
    "  sta (__rc16),y\n"
    "  iny\n"
    "  lda __rc14\n"
    "  sta (__rc16),y\n"
    "  iny\n"
    "  lda __rc15\n"
    "  sta (__rc16),y\n"
    "  jmp .Ludivsi3_quotient\n"

    // zero_udivmod16, the same on 16 bits: n in A, X and d (not 0) in
    // __rc2, __rc3; the quotient comes out in __rc8, __rc9 and the
    // remainder in __rc12, __rc13. The 32-bit division comes here for a
    // dividend under 65,536. A dividend under 256, or whose top byte is
    // below d, takes 8 steps on a byte.
    "  .section .text.zero_udivmod16,\"ax\",@progbits\n"
    "zero_udivmod16:\n"
    "  ldy __rc2\n"
    "  sty __rc4\n"
    "  ldy __rc3\n"
    "  sty __rc5\n"
    "  sta __rc8\n"
    "  stx __rc9\n"
    "  cmp __rc4\n"                   // n < d: the quotient is 0
    "  txa\n"
    "  sbc __rc5\n"
    "  bcs zero_udivmod16_from32\n"
    "  lda __rc8\n"
    "  sta __rc12\n"
    "  stx __rc13\n"
    "  lda #0\n"
    "  sta __rc8\n"
    "  sta __rc9\n"
    "  rts\n"
    "zero_udivmod16_from32:\n"        // n >= d in __rc8, __rc9 and d in __rc4, __rc5
    "  lda #0\n"
    "  sta __rc13\n"
    "  ldy #8\n"
    "  ldx __rc9\n"
    "  bne .Ldiv16_two\n"
    ".Ldiv8:\n"                       // a byte left in __rc8, the remainder in A
    "  asl __rc8\n"
    "  rol a\n"
    "  bcs .Ldiv8_fits\n"
    "  cmp __rc4\n"
    "  bcc .Ldiv8_next\n"
    ".Ldiv8_fits:\n"
    "  sbc __rc4\n"
    "  inc __rc8\n"
    ".Ldiv8_next:\n"
    "  dey\n"
    "  bne .Ldiv8\n"
    "  sta __rc12\n"
    "  rts\n"
    ".Ldiv16_two:\n"
    "  lda __rc5\n"
    "  bne .Ldiv16_wide\n"
    "  cpx __rc4\n"                   // the top byte below d: it's the
    "  bcs .Ldiv16_bytes\n"           // remainder, and a byte is left
    "  txa\n"
    "  ldx #0\n"
    "  stx __rc9\n"
    "  beq .Ldiv8\n"
    ".Ldiv16_bytes:\n"                // 16 bits, the remainder in A
    "  lda #0\n"
    "  ldy #16\n"
    ".Ldiv16_byte:\n"
    "  asl __rc8\n"
    "  rol __rc9\n"
    "  rol a\n"
    "  bcs .Ldiv16_byte_fits\n"
    "  cmp __rc4\n"
    "  bcc .Ldiv16_byte_next\n"
    ".Ldiv16_byte_fits:\n"
    "  sbc __rc4\n"
    "  inc __rc8\n"
    ".Ldiv16_byte_next:\n"
    "  dey\n"
    "  bne .Ldiv16_byte\n"
    "  sta __rc12\n"
    "  rts\n"
    ".Ldiv16_wide:\n"                 // d above a byte: the top byte is the
    "  stx __rc12\n"                  // remainder, a byte is left
    "  lda #0\n"
    "  sta __rc9\n"
    ".Ldiv16_short:\n"
    "  asl __rc8\n"
    "  rol __rc12\n"
    "  rol __rc13\n"
    "  bcs .Ldiv16_short_fits\n"
    "  lda __rc12\n"
    "  cmp __rc4\n"
    "  lda __rc13\n"
    "  sbc __rc5\n"
    "  bcc .Ldiv16_short_next\n"
    ".Ldiv16_short_fits:\n"
    "  lda __rc12\n"
    "  sbc __rc4\n"
    "  sta __rc12\n"
    "  lda __rc13\n"
    "  sbc __rc5\n"
    "  sta __rc13\n"
    "  inc __rc8\n"
    ".Ldiv16_short_next:\n"
    "  dey\n"
    "  bne .Ldiv16_short\n"
    "  rts\n"
    "  .section .text.__udivhi3,\"ax\",@progbits\n"
    "  .globl __udivhi3\n"
    "__udivhi3:\n"
    "  jsr zero_udivmod16\n"
    "  lda __rc8\n"
    "  ldx __rc9\n"
    "  rts\n"
    "  .section .text.__umodhi3,\"ax\",@progbits\n"
    "  .globl __umodhi3\n"
    "__umodhi3:\n"
    "  jsr zero_udivmod16\n"
    "  lda __rc12\n"
    "  ldx __rc13\n"
    "  rts\n"
    "  .text\n");

u32 __udivsi3(u32 n, u32 d);
u32 __umodsi3(u32 n, u32 d);
u32 __udivmodsi4(u32 n, u32 d, u32 *rem);
u16 __udivhi3(u16 n, u16 d);
u16 __umodhi3(u16 n, u16 d);

// Unsigned division in C, the library's way: the divisor shifts left up to
// the dividend's top bit, and back down a bit at a time, subtracted
// wherever it fits.
#define ZERO_UDIV(T, udiv, umod)                                                \
    T udiv(T n, T d)                                                            \
    {                                                                           \
        if (d == 0 || d > n)                                                    \
            return 0;                                                           \
        u8 shift = zero_align_##T(n, &d);                                       \
        T q = 1;                                                                \
        n -= d;                                                                 \
        for (; shift != 0; shift--) {                                           \
            d >>= 1;                                                            \
            q <<= 1;                                                            \
            if (d <= n) {                                                       \
                n -= d;                                                         \
                q |= 1;                                                         \
            }                                                                   \
        }                                                                       \
        return q;                                                               \
    }                                                                           \
    T umod(T n, T d)                                                            \
    {                                                                           \
        if (d == 0 || d > n)                                                    \
            return n;                                                           \
        u8 shift = zero_align_##T(n, &d);                                       \
        n -= d;                                                                 \
        for (; shift != 0; shift--) {                                           \
            d >>= 1;                                                            \
            if (d <= n)                                                         \
                n -= d;                                                         \
        }                                                                       \
        return n;                                                               \
    }
// d, at most n, shifted left as far as it stays so; returns how far.
#define ZERO_ALIGN(T)                                                           \
    static inline u8 zero_align_##T(T n, T *dp)                                 \
    {                                                                           \
        T d = *dp;                                                              \
        u8 shift = 0;                                                           \
        while (!(d >> (sizeof(T) * 8 - 1)) && (T)(d << 1) <= n) {              \
            d <<= 1;                                                            \
            shift++;                                                            \
        }                                                                       \
        *dp = d;                                                                \
        return shift;                                                           \
    }
ZERO_ALIGN(u8)
ZERO_UDIV(u8, __udivqi3, __umodqi3)

// zero_udivmod64_short: n in A, X, __rc2-__rc7, d under 65,536 (not 0) in
// __rc8, __rc9 and a pointer for the remainder in __rc10, __rc11; the
// quotient comes back in n's place. The dividend shifts in __rc12-__rc19,
// the remainder in __rc2, __rc3.
u64 zero_udivmod64_short(u64 n, u16 d, u64 *rem);
__asm__(
    "  .section .text.zero_udivmod64_short,\"ax\",@progbits\n"
    "zero_udivmod64_short:\n"
    "  sta __rc12\n"
    "  stx __rc13\n"
    "  ldx #5\n"
    ".Ldiv64_copy:\n"
    "  lda __rc2,x\n"
    "  sta __rc14,x\n"
    "  dex\n"
    "  bpl .Ldiv64_copy\n"
    "  lda #0\n"
    "  sta __rc2\n"
    "  sta __rc3\n"
    "  ldy #64\n"
    ".Ldiv64_skip:\n"                 // the dividend's zero top bytes
    "  lda __rc19\n"
    "  bne .Ldiv64_bits\n"
    "  ldx #6\n"
    ".Ldiv64_move:\n"
    "  lda __rc12,x\n"
    "  sta __rc13,x\n"
    "  dex\n"
    "  bpl .Ldiv64_move\n"
    "  lda #0\n"
    "  sta __rc12\n"
    "  tya\n"
    "  sec\n"
    "  sbc #8\n"
    "  tay\n"
    "  bne .Ldiv64_skip\n"
    "  beq .Ldiv64_out\n"             // n was 0
    ".Ldiv64_bits:\n"
    "  lda __rc9\n"
    "  bne .Ldiv64_short\n"
    "  lda #0\n"                      // a byte: the remainder in A
    ".Ldiv64_byte:\n"
    "  asl __rc12\n"
    "  rol __rc13\n"
    "  rol __rc14\n"
    "  rol __rc15\n"
    "  rol __rc16\n"
    "  rol __rc17\n"
    "  rol __rc18\n"
    "  rol __rc19\n"
    "  rol a\n"
    "  bcs .Ldiv64_byte_fits\n"
    "  cmp __rc8\n"
    "  bcc .Ldiv64_byte_next\n"
    ".Ldiv64_byte_fits:\n"
    "  sbc __rc8\n"
    "  inc __rc12\n"
    ".Ldiv64_byte_next:\n"
    "  dey\n"
    "  bne .Ldiv64_byte\n"
    "  sta __rc2\n"
    "  beq .Ldiv64_out\n"             // Y is 0
    ".Ldiv64_short:\n"
    "  asl __rc12\n"
    "  rol __rc13\n"
    "  rol __rc14\n"
    "  rol __rc15\n"
    "  rol __rc16\n"
    "  rol __rc17\n"
    "  rol __rc18\n"
    "  rol __rc19\n"
    "  rol __rc2\n"
    "  rol __rc3\n"
    "  bcs .Ldiv64_short_fits\n"
    "  lda __rc2\n"
    "  cmp __rc8\n"
    "  lda __rc3\n"
    "  sbc __rc9\n"
    "  bcc .Ldiv64_short_next\n"
    ".Ldiv64_short_fits:\n"
    "  lda __rc2\n"
    "  sbc __rc8\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc __rc9\n"
    "  sta __rc3\n"
    "  inc __rc12\n"
    ".Ldiv64_short_next:\n"
    "  dey\n"
    "  bne .Ldiv64_short\n"
    ".Ldiv64_out:\n"
    "  lda __rc10\n"                  // the remainder, if wanted
    "  ora __rc11\n"
    "  beq .Ldiv64_quotient\n"
    "  ldy #0\n"
    "  lda __rc2\n"
    "  sta (__rc10),y\n"
    "  iny\n"
    "  lda __rc3\n"
    "  sta (__rc10),y\n"
    "  lda #0\n"
    ".Ldiv64_rest:\n"
    "  iny\n"
    "  sta (__rc10),y\n"
    "  cpy #7\n"
    "  bne .Ldiv64_rest\n"
    ".Ldiv64_quotient:\n"
    "  ldx #5\n"
    ".Ldiv64_back:\n"
    "  lda __rc14,x\n"
    "  sta __rc2,x\n"
    "  dex\n"
    "  bpl .Ldiv64_back\n"
    "  lda __rc12\n"
    "  ldx __rc13\n"
    "  rts\n"
    "  .text\n");

// 64 bits (C#'s long and ulong, and the decimal and double formatting
// that works in them). zero_udivmod64: n in A, X, __rc2-__rc7 and d (not
// 0) in __rc8-__rc15; the quotient comes back in n's place and the
// remainder in zero_rem64. A dividend below the divisor is the remainder,
// one that fits in 32 bits goes to the 32-bit division, and a divisor
// under 65,536 to zero_udivmod64_short. Others (rare) go the library's
// way: the divisor shifts left up to the dividend's top bit, and back down
// a bit at a time, subtracted wherever it fits.
__asm__(
    "  .section .noinit.zero_rem64,\"aw\",@nobits\n"
    "  .globl zero_rem64\n"
    "zero_rem64:\n"
    "  .zero 8\n"
    "  .section .text.zero_udivmod64,\"ax\",@progbits\n"
    "zero_udivmod64:\n"
    "  sta __rc18\n"
    "  stx __rc19\n"
    "  cmp __rc8\n"                   // n < d: the quotient is 0
    "  txa\n"
    "  sbc __rc9\n"
    "  lda __rc2\n"
    "  sbc __rc10\n"
    "  lda __rc3\n"
    "  sbc __rc11\n"
    "  lda __rc4\n"
    "  sbc __rc12\n"
    "  lda __rc5\n"
    "  sbc __rc13\n"
    "  lda __rc6\n"
    "  sbc __rc14\n"
    "  lda __rc7\n"
    "  sbc __rc15\n"
    "  bcs .Ldiv64_ge\n"
    "  lda __rc18\n"                  // the remainder is n
    "  sta zero_rem64\n"
    "  lda __rc19\n"
    "  sta zero_rem64+1\n"
    "  ldx #5\n"
    ".Ldiv64_n:\n"
    "  lda __rc2,x\n"
    "  sta zero_rem64+2,x\n"
    "  lda #0\n"
    "  sta __rc2,x\n"
    "  dex\n"
    "  bpl .Ldiv64_n\n"
    "  tax\n"
    "  rts\n"
    ".Ldiv64_ge:\n"
    "  lda __rc4\n"                   // n in 32 bits (and so d)?
    "  ora __rc5\n"
    "  ora __rc6\n"
    "  ora __rc7\n"
    "  bne .Ldiv64_wide\n"
    "  ldx #3\n"
    ".Ldiv64_d32:\n"
    "  lda __rc8,x\n"
    "  sta __rc4,x\n"
    "  dex\n"
    "  bpl .Ldiv64_d32\n"
    "  lda __rc18\n"
    "  ldx __rc19\n"
    "  jsr zero_udivmod32\n"
    "  ldx #3\n"
    ".Ldiv64_r32:\n"
    "  lda __rc12,x\n"
    "  sta zero_rem64,x\n"
    "  lda #0\n"
    "  sta zero_rem64+4,x\n"
    "  sta __rc4,x\n"
    "  dex\n"
    "  bpl .Ldiv64_r32\n"
    "  lda __rc10\n"
    "  sta __rc2\n"
    "  lda __rc11\n"
    "  sta __rc3\n"
    "  lda __rc8\n"
    "  ldx __rc9\n"
    "  rts\n"
    ".Ldiv64_wide:\n"
    "  lda __rc10\n"                  // d under 65,536?
    "  ora __rc11\n"
    "  ora __rc12\n"
    "  ora __rc13\n"
    "  ora __rc14\n"
    "  ora __rc15\n"
    "  bne .Ldiv64_long\n"
    "  lda #mos16lo(zero_rem64)\n"
    "  sta __rc10\n"
    "  lda #mos16hi(zero_rem64)\n"
    "  sta __rc11\n"
    "  lda __rc18\n"
    "  ldx __rc19\n"
    "  jmp zero_udivmod64_short\n"
    ".Ldiv64_long:\n"   // n in __rc18, __rc19, __rc2-__rc7; d shifts
    "  ldy #0\n"   // up to n's top bit, a byte then a bit at a time
    ".Ldiv64_long_bytes:\n"
    "  lda __rc15\n"
    "  bne .Ldiv64_long_bits\n"
    "  lda __rc19\n"
    "  cmp __rc8\n"
    "  lda __rc2\n"
    "  sbc __rc9\n"
    "  lda __rc3\n"
    "  sbc __rc10\n"
    "  lda __rc4\n"
    "  sbc __rc11\n"
    "  lda __rc5\n"
    "  sbc __rc12\n"
    "  lda __rc6\n"
    "  sbc __rc13\n"
    "  lda __rc7\n"
    "  sbc __rc14\n"
    "  bcc .Ldiv64_long_bits\n"
    "  lda __rc14\n"
    "  sta __rc15\n"
    "  lda __rc13\n"
    "  sta __rc14\n"
    "  lda __rc12\n"
    "  sta __rc13\n"
    "  lda __rc11\n"
    "  sta __rc12\n"
    "  lda __rc10\n"
    "  sta __rc11\n"
    "  lda __rc9\n"
    "  sta __rc10\n"
    "  lda __rc8\n"
    "  sta __rc9\n"
    "  lda #0\n"
    "  sta __rc8\n"
    "  tya\n"
    "  clc\n"
    "  adc #8\n"
    "  tay\n"
    "  jmp .Ldiv64_long_bytes\n"
    ".Ldiv64_long_bits:\n"
    "  lda __rc15\n"
    "  bmi .Ldiv64_long_aligned\n"
    "  asl __rc8\n"
    "  rol __rc9\n"
    "  rol __rc10\n"
    "  rol __rc11\n"
    "  rol __rc12\n"
    "  rol __rc13\n"
    "  rol __rc14\n"
    "  rol __rc15\n"
    "  lda __rc18\n"
    "  cmp __rc8\n"
    "  lda __rc19\n"
    "  sbc __rc9\n"
    "  lda __rc2\n"
    "  sbc __rc10\n"
    "  lda __rc3\n"
    "  sbc __rc11\n"
    "  lda __rc4\n"
    "  sbc __rc12\n"
    "  lda __rc5\n"
    "  sbc __rc13\n"
    "  lda __rc6\n"
    "  sbc __rc14\n"
    "  lda __rc7\n"
    "  sbc __rc15\n"
    "  bcc .Ldiv64_long_back\n"
    "  iny\n"
    "  jmp .Ldiv64_long_bits\n"
    ".Ldiv64_long_back:\n"
    "  lsr __rc15\n"
    "  ror __rc14\n"
    "  ror __rc13\n"
    "  ror __rc12\n"
    "  ror __rc11\n"
    "  ror __rc10\n"
    "  ror __rc9\n"
    "  ror __rc8\n"
    ".Ldiv64_long_aligned:\n"   // the quotient, in zero_rem64 for now: 1
    "  lda #1\n"
    "  sta zero_rem64\n"
    "  lda #0\n"
    "  ldx #6\n"
    ".Ldiv64_long_q:\n"
    "  sta zero_rem64+1,x\n"
    "  dex\n"
    "  bpl .Ldiv64_long_q\n"
    "  sec\n"
    "  lda __rc18\n"
    "  sbc __rc8\n"
    "  sta __rc18\n"
    "  lda __rc19\n"
    "  sbc __rc9\n"
    "  sta __rc19\n"
    "  lda __rc2\n"
    "  sbc __rc10\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc __rc11\n"
    "  sta __rc3\n"
    "  lda __rc4\n"
    "  sbc __rc12\n"
    "  sta __rc4\n"
    "  lda __rc5\n"
    "  sbc __rc13\n"
    "  sta __rc5\n"
    "  lda __rc6\n"
    "  sbc __rc14\n"
    "  sta __rc6\n"
    "  lda __rc7\n"
    "  sbc __rc15\n"
    "  sta __rc7\n"
    ".Ldiv64_long_step:\n"   // back down a bit at a time
    "  tya\n"
    "  bne .Ldiv64_long_more\n"
    "  jmp .Ldiv64_long_done\n"
    ".Ldiv64_long_more:\n"
    "  dey\n"
    "  lsr __rc15\n"
    "  ror __rc14\n"
    "  ror __rc13\n"
    "  ror __rc12\n"
    "  ror __rc11\n"
    "  ror __rc10\n"
    "  ror __rc9\n"
    "  ror __rc8\n"
    "  asl zero_rem64\n"
    "  rol zero_rem64+1\n"
    "  rol zero_rem64+2\n"
    "  rol zero_rem64+3\n"
    "  rol zero_rem64+4\n"
    "  rol zero_rem64+5\n"
    "  rol zero_rem64+6\n"
    "  rol zero_rem64+7\n"
    "  lda __rc18\n"
    "  cmp __rc8\n"
    "  lda __rc19\n"
    "  sbc __rc9\n"
    "  lda __rc2\n"
    "  sbc __rc10\n"
    "  lda __rc3\n"
    "  sbc __rc11\n"
    "  lda __rc4\n"
    "  sbc __rc12\n"
    "  lda __rc5\n"
    "  sbc __rc13\n"
    "  lda __rc6\n"
    "  sbc __rc14\n"
    "  lda __rc7\n"
    "  sbc __rc15\n"
    "  bcs .Ldiv64_long_fits\n"
    "  jmp .Ldiv64_long_step\n"
    ".Ldiv64_long_fits:\n"
    "  lda __rc18\n"
    "  sbc __rc8\n"
    "  sta __rc18\n"
    "  lda __rc19\n"
    "  sbc __rc9\n"
    "  sta __rc19\n"
    "  lda __rc2\n"
    "  sbc __rc10\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc __rc11\n"
    "  sta __rc3\n"
    "  lda __rc4\n"
    "  sbc __rc12\n"
    "  sta __rc4\n"
    "  lda __rc5\n"
    "  sbc __rc13\n"
    "  sta __rc5\n"
    "  lda __rc6\n"
    "  sbc __rc14\n"
    "  sta __rc6\n"
    "  lda __rc7\n"
    "  sbc __rc15\n"
    "  sta __rc7\n"
    "  inc zero_rem64\n"
    "  jmp .Ldiv64_long_step\n"
    ".Ldiv64_long_done:\n"   // what's left of n is the remainder: swap
    "  lda zero_rem64+2\n"
    "  ldx __rc2\n"
    "  stx zero_rem64+2\n"
    "  sta __rc2\n"
    "  lda zero_rem64+3\n"
    "  ldx __rc3\n"
    "  stx zero_rem64+3\n"
    "  sta __rc3\n"
    "  lda zero_rem64+4\n"
    "  ldx __rc4\n"
    "  stx zero_rem64+4\n"
    "  sta __rc4\n"
    "  lda zero_rem64+5\n"
    "  ldx __rc5\n"
    "  stx zero_rem64+5\n"
    "  sta __rc5\n"
    "  lda zero_rem64+6\n"
    "  ldx __rc6\n"
    "  stx zero_rem64+6\n"
    "  sta __rc6\n"
    "  lda zero_rem64+7\n"
    "  ldx __rc7\n"
    "  stx zero_rem64+7\n"
    "  sta __rc7\n"
    "  lda zero_rem64\n"
    "  ldx __rc18\n"
    "  stx zero_rem64\n"
    "  sta __rc18\n"
    "  lda zero_rem64+1\n"
    "  ldx __rc19\n"
    "  stx zero_rem64+1\n"
    "  sta __rc19\n"
    "  lda __rc18\n"
    "  ldx __rc19\n"
    "  rts\n"

    // The library's three, on top of it. __udivmoddi4's pointer comes on
    // the stack: the numbers fill the registers.
    "  .section .text.__udivdi3,\"ax\",@progbits\n"
    "  .globl __udivdi3\n"
    "__udivdi3:\n"
    "  jmp zero_udivmod64\n"
    "  .section .text.__umoddi3,\"ax\",@progbits\n"
    "  .globl __umoddi3\n"
    "__umoddi3:\n"
    "  jsr zero_udivmod64\n"
    "  ldx #5\n"
    ".Lumoddi3_copy:\n"
    "  lda zero_rem64+2,x\n"
    "  sta __rc2,x\n"
    "  dex\n"
    "  bpl .Lumoddi3_copy\n"
    "  lda zero_rem64\n"
    "  ldx zero_rem64+1\n"
    "  rts\n"
    "  .section .text.__udivmoddi4,\"ax\",@progbits\n"
    "  .globl __udivmoddi4\n"
    "__udivmoddi4:\n"
    "  jsr zero_udivmod64\n"
    "  sta __rc18\n"
    "  ldy #0\n"
    "  lda (__rc0),y\n"
    "  sta __rc16\n"
    "  iny\n"
    "  lda (__rc0),y\n"
    "  sta __rc17\n"
    "  ora __rc16\n"
    "  beq .Ludivmoddi4_done\n"
    "  ldy #7\n"
    ".Ludivmoddi4_copy:\n"
    "  lda zero_rem64,y\n"
    "  sta (__rc16),y\n"
    "  dey\n"
    "  bpl .Ludivmoddi4_copy\n"
    ".Ludivmoddi4_done:\n"
    "  lda __rc18\n"
    "  rts\n"
    "  .text\n");

u64 __udivdi3(u64 n, u64 d);
u64 __umoddi3(u64 n, u64 d);
u64 __udivmoddi4(u64 n, u64 d, u64 *rem);

// Signed: rounded towards zero, and the remainder has the dividend's sign,
// as in C and C#. The minimum value divided by -1 overflows to itself (C#
// throws before it gets here).
#define ZERO_DIVMOD(S, U, div, mod, udiv, umod)                                 \
    S div(S n, S d)                                                             \
    {                                                                           \
        U q = udiv(n < 0 ? (U)(0 - (U)n) : (U)n, d < 0 ? (U)(0 - (U)d) : (U)d);  \
        return (S)((n < 0) != (d < 0) ? (U)(0 - q) : q);                        \
    }                                                                           \
    S mod(S n, S d)                                                             \
    {                                                                           \
        U r = umod(n < 0 ? (U)(0 - (U)n) : (U)n, d < 0 ? (U)(0 - (U)d) : (U)d);  \
        return (S)(n < 0 ? (U)(0 - r) : r);                                     \
    }
ZERO_DIVMOD(i8, u8, __divqi3, __modqi3, __udivqi3, __umodqi3)
ZERO_DIVMOD(i16, u16, __divhi3, __modhi3, __udivhi3, __umodhi3)
ZERO_DIVMOD(i32, u32, __divsi3, __modsi3, __udivsi3, __umodsi3)
ZERO_DIVMOD(i64, u64, __divdi3, __moddi3, __udivdi3, __umoddi3)

// Both at once, the remainder through a pointer.
#define ZERO_SDIVMOD(S, U, divmod, udivmod)                                     \
    S divmod(S n, S d, S *rem)                                                  \
    {                                                                           \
        U r;                                                                    \
        U q = udivmod(n < 0 ? 0 - (U)n : (U)n, d < 0 ? 0 - (U)d : (U)d, &r);    \
        *rem = (S)(n < 0 ? 0 - r : r);                                          \
        return (S)((n < 0) != (d < 0) ? 0 - q : q);                             \
    }
ZERO_SDIVMOD(i32, u32, __divmodsi4, __udivmodsi4)
ZERO_SDIVMOD(i64, u64, __divmoddi4, __udivmoddi4)
u8 __udivmodqi4(u8 n, u8 d, u8 *rem) { *rem = __umodqi3(n, d); return __udivqi3(n, d); }
u16 __udivmodhi4(u16 n, u16 d, u16 *rem) { *rem = __umodhi3(n, d); return __udivhi3(n, d); }
i8 __divmodqi4(i8 n, i8 d, i8 *rem) { *rem = __modqi3(n, d); return __divqi3(n, d); }
i16 __divmodhi4(i16 n, i16 d, i16 *rem) { *rem = __modhi3(n, d); return __divhi3(n, d); }

#ifdef ZERO_FAST_MULTIPLY
// The low 32 bits of a * b, with a table of quarter squares, for a project
// that asks for it (ZeroC64FastMultiply): 1 KB of RAM for a multiplication
// two to four times faster. f(m) = floor(m^2 / 4) for m = 0..510, low bytes
// then high bytes, filled at the start; a product of two bytes is then
// f(x + y) - f(|x - y|), exactly. A 32-bit product is the bytes' products,
// each added at its place, those that would land above the low 32 bits left
// out and zero bytes skipped. A negative factor (its top bit set) is
// negated first and the product at the end, so -5 costs what 5 does: the
// low 32 bits come out the same.
unsigned char zero_mul_lo[512] __attribute__((aligned(256)));
unsigned char zero_mul_hi[512] __attribute__((aligned(256)));
unsigned char zero_mul_r[8]; // the product, with room for carries past it

__attribute__((constructor)) static void zero_mul_init(void)
{
    for (unsigned m = 0; m < 511; m++) {
        unsigned f = (m >> 1) * (m - (m >> 1)); // floor(m^2 / 4) < 65536
        zero_mul_lo[m] = (unsigned char)f;
        zero_mul_hi[m] = (unsigned char)(f >> 8);
    }
}

// a in A, X, __rc2, __rc3 (copied to __rc8-__rc11) and b in __rc4-__rc7;
// the product in A, X, __rc2, __rc3 (built in __rc12-__rc15). When one
// factor has 16 bits and the other 24, the usual case, its six products of
// bytes are written out one after the other (about 50 cycles each, none for
// a zero byte); f(x + y) is read through a pointer whose low byte is x,
// with Y = y, so that the 6502 adds them. Wider numbers go through a loop
// over the bytes. (The source of the unrolled part is generated: each
// product is the same eighteen instructions.)
__asm__(
    "  .section .text.__mulsi3,\"ax\",@progbits\n"
    "  .globl __mulsi3\n"
    "__mulsi3:\n"
    "  sta __rc8\n"
    "  stx __rc9\n"
    "  lda __rc2\n"
    "  sta __rc10\n"
    "  lda __rc3\n"
    "  sta __rc11\n"
    "  eor __rc7\n"  // the product's sign, kept on the stack
    "  pha\n"
    "  bit __rc11\n"  // |a|
    "  bpl .Lfm1\n"
    "  ldx #0\n"
    "  ldy #4\n"
    "  sec\n"
    ".Lfm2:\n"
    "  lda #0\n"
    "  sbc __rc8,x\n"
    "  sta __rc8,x\n"
    "  inx\n"
    "  dey\n"
    "  bne .Lfm2\n"
    ".Lfm1:\n"
    "  bit __rc7\n"  // |b|
    "  bpl .Lfm3\n"
    "  ldx #0\n"
    "  ldy #4\n"
    "  sec\n"
    ".Lfm4:\n"
    "  lda #0\n"
    "  sbc __rc4,x\n"
    "  sta __rc4,x\n"
    "  inx\n"
    "  dey\n"
    "  bne .Lfm4\n"
    ".Lfm3:\n"
    "  lda #0\n"
    "  sta __rc12\n"
    "  sta __rc13\n"
    "  sta __rc14\n"
    "  sta __rc15\n"
    "  lda #mos16hi(zero_mul_lo)\n"  // pointers into the table's two halves
    "  sta __rc17\n"
    "  lda #mos16hi(zero_mul_hi)\n"
    "  sta __rc19\n"
    "  lda __rc6\n"  // b in 16 bits and a in 24: unrolled
    "  ora __rc7\n"
    "  bne .Lfm6\n"
    "  lda __rc11\n"
    "  beq .Lfm5\n"
    ".Lfm6:\n"  // or a in 16 and b in 24: the same, swapped
    "  lda __rc10\n"
    "  ora __rc11\n"
    "  bne .Lfm_wide\n"
    "  lda __rc7\n"
    "  beq .Lfm9\n"
    ".Lfm_wide:\n"
    "  jmp .Lfmul_general\n"
    ".Lfm9:\n"
    "  ldx #3\n"
    ".Lfm10:\n"
    "  lda __rc4,x\n"
    "  ldy __rc8,x\n"
    "  sta __rc8,x\n"
    "  sty __rc4,x\n"
    "  dex\n"
    "  bpl .Lfm10\n"
    ".Lfm5:\n"
    "  lda __rc8\n"  // a0 * b0, at byte 0
    "  beq .Lfm11\n"
    "  sta __rc16\n"
    "  sta __rc18\n"
    "  ldy __rc4\n"
    "  beq .Lfm11\n"
    "  lda (__rc16),y\n"
    "  sta __rc2\n"
    "  lda (__rc18),y\n"
    "  sta __rc3\n"
    "  lda __rc8\n"
    "  sec\n"
    "  sbc __rc4\n"
    "  bcs .Lfm12\n"
    "  eor #$ff\n"
    "  adc #1\n"
    ".Lfm12:\n"
    "  tax\n"
    "  sec\n"
    "  lda __rc2\n"
    "  sbc zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  clc\n"
    "  lda __rc2\n"
    "  adc __rc12\n"
    "  sta __rc12\n"
    "  lda __rc13\n"
    "  adc __rc3\n"
    "  sta __rc13\n"
    "  lda __rc14\n"
    "  adc #0\n"
    "  sta __rc14\n"
    "  lda __rc15\n"
    "  adc #0\n"
    "  sta __rc15\n"
    ".Lfm11:\n"
    "  lda __rc8\n"  // a0 * b1, at byte 1
    "  beq .Lfm13\n"
    "  sta __rc16\n"
    "  sta __rc18\n"
    "  ldy __rc5\n"
    "  beq .Lfm13\n"
    "  lda (__rc16),y\n"
    "  sta __rc2\n"
    "  lda (__rc18),y\n"
    "  sta __rc3\n"
    "  lda __rc8\n"
    "  sec\n"
    "  sbc __rc5\n"
    "  bcs .Lfm14\n"
    "  eor #$ff\n"
    "  adc #1\n"
    ".Lfm14:\n"
    "  tax\n"
    "  sec\n"
    "  lda __rc2\n"
    "  sbc zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  clc\n"
    "  lda __rc2\n"
    "  adc __rc13\n"
    "  sta __rc13\n"
    "  lda __rc14\n"
    "  adc __rc3\n"
    "  sta __rc14\n"
    "  lda __rc15\n"
    "  adc #0\n"
    "  sta __rc15\n"
    ".Lfm13:\n"
    "  lda __rc9\n"  // a1 * b0, at byte 1
    "  beq .Lfm15\n"
    "  sta __rc16\n"
    "  sta __rc18\n"
    "  ldy __rc4\n"
    "  beq .Lfm15\n"
    "  lda (__rc16),y\n"
    "  sta __rc2\n"
    "  lda (__rc18),y\n"
    "  sta __rc3\n"
    "  lda __rc9\n"
    "  sec\n"
    "  sbc __rc4\n"
    "  bcs .Lfm16\n"
    "  eor #$ff\n"
    "  adc #1\n"
    ".Lfm16:\n"
    "  tax\n"
    "  sec\n"
    "  lda __rc2\n"
    "  sbc zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  clc\n"
    "  lda __rc2\n"
    "  adc __rc13\n"
    "  sta __rc13\n"
    "  lda __rc14\n"
    "  adc __rc3\n"
    "  sta __rc14\n"
    "  lda __rc15\n"
    "  adc #0\n"
    "  sta __rc15\n"
    ".Lfm15:\n"
    "  lda __rc9\n"  // a1 * b1, at byte 2
    "  beq .Lfm17\n"
    "  sta __rc16\n"
    "  sta __rc18\n"
    "  ldy __rc5\n"
    "  beq .Lfm17\n"
    "  lda (__rc16),y\n"
    "  sta __rc2\n"
    "  lda (__rc18),y\n"
    "  sta __rc3\n"
    "  lda __rc9\n"
    "  sec\n"
    "  sbc __rc5\n"
    "  bcs .Lfm18\n"
    "  eor #$ff\n"
    "  adc #1\n"
    ".Lfm18:\n"
    "  tax\n"
    "  sec\n"
    "  lda __rc2\n"
    "  sbc zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  clc\n"
    "  lda __rc2\n"
    "  adc __rc14\n"
    "  sta __rc14\n"
    "  lda __rc15\n"
    "  adc __rc3\n"
    "  sta __rc15\n"
    ".Lfm17:\n"
    "  lda __rc10\n"  // a2 * b0, at byte 2
    "  beq .Lfm19\n"
    "  sta __rc16\n"
    "  sta __rc18\n"
    "  ldy __rc4\n"
    "  beq .Lfm19\n"
    "  lda (__rc16),y\n"
    "  sta __rc2\n"
    "  lda (__rc18),y\n"
    "  sta __rc3\n"
    "  lda __rc10\n"
    "  sec\n"
    "  sbc __rc4\n"
    "  bcs .Lfm20\n"
    "  eor #$ff\n"
    "  adc #1\n"
    ".Lfm20:\n"
    "  tax\n"
    "  sec\n"
    "  lda __rc2\n"
    "  sbc zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  clc\n"
    "  lda __rc2\n"
    "  adc __rc14\n"
    "  sta __rc14\n"
    "  lda __rc15\n"
    "  adc __rc3\n"
    "  sta __rc15\n"
    ".Lfm19:\n"
    "  lda __rc10\n"  // a2 * b1, at byte 3
    "  beq .Lfm21\n"
    "  sta __rc16\n"
    "  sta __rc18\n"
    "  ldy __rc5\n"
    "  beq .Lfm21\n"
    "  lda (__rc16),y\n"
    "  sta __rc2\n"
    "  lda (__rc18),y\n"
    "  sta __rc3\n"
    "  lda __rc10\n"
    "  sec\n"
    "  sbc __rc5\n"
    "  bcs .Lfm22\n"
    "  eor #$ff\n"
    "  adc #1\n"
    ".Lfm22:\n"
    "  tax\n"
    "  sec\n"
    "  lda __rc2\n"
    "  sbc zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  clc\n"
    "  lda __rc2\n"
    "  adc __rc15\n"
    "  sta __rc15\n"
    ".Lfm21:\n"
    "  jmp .Lfmul_sign\n"
    // Wider: every pair of bytes whose product lands in the low 32 bits.
    // __rc18/19 the factors, __rc17 j, Y i; the product in zero_mul_r.
    ".Lfmul_general:\n"
    "  lda #0\n"
    "  ldx #7\n"
    "2:\n"
    "  sta zero_mul_r,x\n"
    "  dex\n"
    "  bpl 2b\n"
    "  ldy #0\n"
    ".Lfmul_i:\n"
    "  lda __rc8,y\n"
    "  bne 6f\n"
    "  jmp .Lfmul_next_i\n"
    "6:\n"
    "  sta __rc18\n"
    "  lda #0\n"
    "  sta __rc17\n"
    ".Lfmul_j:\n"
    "  ldx __rc17\n"
    "  lda __rc4,x\n"
    "  bne 6f\n"
    "  jmp .Lfmul_next_j\n"
    "6:\n"
    "  sta __rc19\n"
    "  clc\n"
    "  lda __rc18\n"
    "  adc __rc19\n"
    "  tax\n"
    "  bcs 3f\n"
    "  lda zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  jmp 4f\n"
    "3:\n"
    "  lda zero_mul_lo+256,x\n"
    "  sta __rc2\n"
    "  lda zero_mul_hi+256,x\n"
    "  sta __rc3\n"
    "4:\n"
    "  lda __rc18\n"
    "  sec\n"
    "  sbc __rc19\n"
    "  bcs 5f\n"
    "  eor #$ff\n"
    "  adc #1\n"
    "5:\n"
    "  tax\n"
    "  sec\n"
    "  lda __rc2\n"
    "  sbc zero_mul_lo,x\n"
    "  sta __rc2\n"
    "  lda __rc3\n"
    "  sbc zero_mul_hi,x\n"
    "  sta __rc3\n"
    "  tya\n"
    "  clc\n"
    "  adc __rc17\n"
    "  tax\n"
    "  clc\n"
    "  lda __rc2\n"
    "  adc zero_mul_r,x\n"
    "  sta zero_mul_r,x\n"
    "  lda __rc3\n"
    "  adc zero_mul_r+1,x\n"
    "  sta zero_mul_r+1,x\n"
    "  lda zero_mul_r+2,x\n"
    "  adc #0\n"
    "  sta zero_mul_r+2,x\n"
    "  lda zero_mul_r+3,x\n"
    "  adc #0\n"
    "  sta zero_mul_r+3,x\n"
    ".Lfmul_next_j:\n"
    "  inc __rc17\n"
    "  tya\n"
    "  clc\n"
    "  adc __rc17\n"
    "  cmp #4\n"
    "  bcs 7f\n"
    "  jmp .Lfmul_j\n"
    "7:\n"
    ".Lfmul_next_i:\n"
    "  iny\n"
    "  cpy #4\n"
    "  bcs 7f\n"
    "  jmp .Lfmul_i\n"
    "7:\n"
    "  ldx #3\n"
    "8:\n"
    "  lda zero_mul_r,x\n"
    "  sta __rc12,x\n"
    "  dex\n"
    "  bpl 8b\n"
    ".Lfmul_sign:\n"                  // the sign back
    "  pla\n"
    "  bpl 1f\n"
    "  ldx #0\n"
    "  ldy #4\n"
    "  sec\n"
    "2:\n"
    "  lda #0\n"
    "  sbc __rc12,x\n"
    "  sta __rc12,x\n"
    "  inx\n"
    "  dey\n"
    "  bne 2b\n"
    "1:\n"
    "  lda __rc14\n"
    "  sta __rc2\n"
    "  lda __rc15\n"
    "  sta __rc3\n"
    "  lda __rc12\n"
    "  ldx __rc13\n"
    "  rts\n"
    "  .text\n");
#else
// The low 32 bits of a * b (the same for signed and unsigned numbers), in
// assembly: a in A, X, __rc2, __rc3 and b in __rc4-__rc7. The factor with
// fewer bytes is the multiplier, taken from the bottom a byte at a time:
// for each 1 bit the multiplicand is added, and it shifts left a bit for
// the next. A byte ends with its last 1 bit, a zero byte costs a byte
// move, and the work ends with the multiplier's last nonzero byte.
__asm__(
    "  .section .text.__mulsi3,\"ax\",@progbits\n"
    "  .globl __mulsi3\n"
    "__mulsi3:\n"
    "  sta __rc8\n"                   // the multiplicand: __rc8-__rc11
    "  stx __rc9\n"
    "  lda __rc2\n"
    "  sta __rc10\n"
    "  lda __rc3\n"
    "  sta __rc11\n"
    "  lda __rc6\n"                   // b above 16 bits: a is shorter?
    "  ora __rc7\n"
    "  beq .Lmul_b_short\n"
    "  lda __rc10\n"
    "  ora __rc11\n"
    "  beq .Lmul_swap\n"
    "  bne .Lmul_start\n"
    ".Lmul_b_short:\n"                // b above 8 bits: is a a byte?
    "  lda __rc5\n"
    "  beq .Lmul_start\n"
    "  lda __rc9\n"
    "  ora __rc10\n"
    "  ora __rc11\n"
    "  bne .Lmul_start\n"
    ".Lmul_swap:\n"
    "  ldx #3\n"
    ".Lmul_swap_byte:\n"
    "  lda __rc4,x\n"
    "  ldy __rc8,x\n"
    "  sta __rc8,x\n"
    "  sty __rc4,x\n"
    "  dex\n"
    "  bpl .Lmul_swap_byte\n"
    ".Lmul_start:\n"
    "  lda #0\n"                      // the product: __rc12-__rc15
    "  sta __rc12\n"
    "  sta __rc13\n"
    "  sta __rc14\n"
    "  sta __rc15\n"
    ".Lmul_byte:\n"
    "  lda __rc4\n"
    "  beq .Lmul_zero_byte\n"
    "  ldy #8\n"                      // shifts of the multiplicand left
    ".Lmul_bit:\n"
    "  lsr __rc4\n"
    "  bcc .Lmul_shift\n"
    "  clc\n"
    "  lda __rc12\n"
    "  adc __rc8\n"
    "  sta __rc12\n"
    "  lda __rc13\n"
    "  adc __rc9\n"
    "  sta __rc13\n"
    "  lda __rc14\n"
    "  adc __rc10\n"
    "  sta __rc14\n"
    "  lda __rc15\n"
    "  adc __rc11\n"
    "  sta __rc15\n"
    ".Lmul_shift:\n"
    "  asl __rc8\n"
    "  rol __rc9\n"
    "  rol __rc10\n"
    "  rol __rc11\n"
    "  dey\n"
    "  lda __rc4\n"
    "  bne .Lmul_bit\n"
    "  lda __rc5\n"                   // the byte's last 1 bit: more bytes?
    "  ora __rc6\n"
    "  ora __rc7\n"
    "  beq .Lmul_done\n"
    "  tya\n"
    "  beq .Lmul_next\n"
    ".Lmul_rest:\n"                   // the byte's remaining shifts
    "  asl __rc8\n"
    "  rol __rc9\n"
    "  rol __rc10\n"
    "  rol __rc11\n"
    "  dey\n"
    "  bne .Lmul_rest\n"
    "  beq .Lmul_next\n"
    ".Lmul_zero_byte:\n"
    "  lda __rc5\n"
    "  ora __rc6\n"
    "  ora __rc7\n"
    "  beq .Lmul_done\n"
    "  lda __rc10\n"                  // shift the multiplicand a byte
    "  sta __rc11\n"
    "  lda __rc9\n"
    "  sta __rc10\n"
    "  lda __rc8\n"
    "  sta __rc9\n"
    "  lda #0\n"
    "  sta __rc8\n"
    ".Lmul_next:\n"                   // and the multiplier the other way
    "  lda __rc5\n"
    "  sta __rc4\n"
    "  lda __rc6\n"
    "  sta __rc5\n"
    "  lda __rc7\n"
    "  sta __rc6\n"
    "  lda #0\n"
    "  sta __rc7\n"
    "  beq .Lmul_byte\n"
    ".Lmul_done:\n"
    "  lda __rc14\n"
    "  sta __rc2\n"
    "  lda __rc15\n"
    "  sta __rc3\n"
    "  lda __rc12\n"
    "  ldx __rc13\n"
    "  rts\n"
    "  .text\n");
#endif

// The other widths in C, the library's way: add a, shifting left, for each
// 1 bit of b, until b has none left.
#define ZERO_MUL(T, name)                                                       \
    T name(T a, T b)                                                            \
    {                                                                           \
        T p = 0;                                                                \
        while (b != 0) {                                                        \
            if (b & 1)                                                          \
                p += a;                                                         \
            a <<= 1;                                                            \
            b >>= 1;                                                            \
        }                                                                       \
        return p;                                                               \
    }
ZERO_MUL(u8, __mulqi3)
ZERO_MUL(u16, __mulhi3)
ZERO_MUL(u64, __muldi3)

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
    // The next time the beam reaches the line: if it is on it already, the
    // line ends first (63 cycles), so that two calls never return in the
    // same frame.
    while (*raster == wanted) {}
    while (*raster != wanted) {}
}

// The key pressed since the last call, as the KERNAL's keyboard buffer has
// it (PETSCII), or 0.
i32 cs_c64_key(void)
{
    return cbm_k_getin();
}

// The keys held down in some of the keyboard's 8 columns: a column is
// selected by a 0 in its bit of $DC00, and $DC01 then has a 0 for each key
// of it that is down. The KERNAL's interrupt scans the keyboard through the
// same register, so interrupts wait for the few instructions this takes,
// and $DC00 is put back as it was. Returns 1 bits for keys down. llvm-mos
// passes the 32-bit argument in A, X, __rc2 and __rc3, and takes the result
// back in the same four.
i32 cs_c64_matrix(i32 columns);
__asm__(
    "  .section .text.cs_c64_matrix,\"ax\",@progbits\n"
    "  .globl cs_c64_matrix\n"
    "cs_c64_matrix:\n"
    "  eor #$ff\n"
    "  php\n"
    "  sei\n"
    "  ldx $dc00\n"
    "  sta $dc00\n"
    "  lda $dc01\n"
    "  stx $dc00\n"
    "  plp\n"
    "  eor #$ff\n"
    "  ldx #0\n"
    "  stx __rc2\n"
    "  stx __rc3\n"
    "  rts\n"
    "  .text\n");

u32 cs_c64_ticks(void) { return jiffies(); }

// ---- the RAM Expansion Unit (C64.Reu) --------------------------------------------------
//
// The 1700, 1764 and 1750, and the C64 Ultimate's: memory the 6502 can't
// address, copied to and from the C64's by DMA, while the processor waits,
// at a byte a cycle. The registers: $DF01 the command, $DF02-03 the C64
// address, $DF04-06 the REU address, $DF07-08 the length, $DF0A whether
// the addresses count up (0: both do).
#define REU ((volatile unsigned char *)0xDF00)

// command: $90, from the C64 to the REU; $91, the other way (bit 7: now;
// bit 4: without waiting for a write to $FF00). A length of 0 would copy
// 64 KB, so count is never 0 here.
static void reu_copy(unsigned char command, u32 reu, unsigned char *c64, unsigned count)
{
    unsigned address = (unsigned)c64;
    REU[2] = (unsigned char)address;
    REU[3] = (unsigned char)(address >> 8);
    REU[4] = (unsigned char)reu;
    REU[5] = (unsigned char)(reu >> 8);
    REU[6] = (unsigned char)(reu >> 16);
    REU[7] = (unsigned char)count;
    REU[8] = (unsigned char)(count >> 8);
    REU[0x0A] = 0;
    REU[1] = command;
}

void cs_c64_reu_copy(i32 toReu, i32 reu, unsigned char *c64, i32 count)
{
    if (count > 0)
        reu_copy(toReu ? 0x90 : 0x91, (u32)reu, c64, (unsigned)count);
}

// The REU's size: 0 without one. Without an REU nothing answers at $DF02,
// and a byte written there doesn't read back. With one, a byte is written
// at 0 and at each power of two from 64 KB to 8 MB; an REU smaller than an
// address wraps it to 0, so the smallest address that reads back what 0
// does is the size. The bytes there are put back, highest address first,
// so that the one at 0 is put back last.
i32 cs_c64_reu_size(void)
{
    REU[2] = 0x55;
    if (REU[2] != 0x55)
        return 0;
    REU[2] = 0xAA;
    if (REU[2] != 0xAA)
        return 0;
    unsigned char saved[9], seen[9];
    u32 probe[9];
    probe[0] = 0;
    for (unsigned char i = 1; i < 9; i++)
        probe[i] = (u32)1 << (15 + i);
    for (unsigned char i = 0; i < 9; i++)
        reu_copy(0x91, probe[i], &saved[i], 1);
    for (unsigned char i = 0; i < 9; i++) {
        seen[i] = i;
        reu_copy(0x90, probe[i], &seen[i], 1);
    }
    for (unsigned char i = 0; i < 9; i++)
        reu_copy(0x91, probe[i], &seen[i], 1);
    i32 size = (i32)1 << 24;
    if (seen[0] != 0)
        for (unsigned char i = 1; i < 9; i++)
            if (seen[i] == seen[0]) {
                size = (i32)probe[i];
                break;
            }
    for (unsigned char i = 9; i-- > 0;)
        reu_copy(0x90, probe[i], &saved[i], 1);
    return size;
}

// ---- an int8 dot product (Kernels.DotI8) -----------------------------------------------
//
// The 6502 has no multiply instruction (__mulsi3 above takes about 1,000
// cycles for two 16-bit numbers), so each product comes from a table of
// quarter squares, f(m) = floor(m^2 / 4):
//   a * b = f(a + b) - f(b - a)
// exactly, for whole numbers. With A = a + 128 and B = b + 128 (a sign flip
// of the top bit, so -127..127 become 1..255), entry k of the table holds
// f(|k - 256|), so that f(a + b) is entry A + B and f(b - a) entry 256 - A
// + B. Those sums cost nothing: a pointer's low byte holds A (or 256 - A)
// and the Y register B, and (pointer),Y adds them.
//
// The table is 1 KB of RAM, built at the first call: its low bytes in two
// pages, then its high bytes in two more. It is only in programs that call
// DotI8. The values must be -127..127 (a = -128 would need entry 0 - 128).
unsigned char zero_dot_table[1024] __attribute__((aligned(256)));
unsigned char zero_dot_ready;
unsigned zero_dot_left;     // products still to go
u32 zero_dot_sum;           // the sum of the chunks so far

void zero_dot_init(void)
{
    for (unsigned k = 0; k < 512; k++) {
        unsigned m = k < 256 ? 256 - k : k - 256;
        unsigned f = (m >> 1) * (m - (m >> 1)); // floor(m^2 / 4), in 16 bits
        zero_dot_table[k] = (unsigned char)f;
        zero_dot_table[512 + k] = (unsigned char)(f >> 8);
    }
    zero_dot_ready = 1;
}

// cs_c64_dot_i8(a, b, n), all in assembly: llvm-mos's calling convention
// brings a in __rc2/3, b in __rc4/5 and n in A, X, __rc6, __rc7, and takes
// the result in A, X, __rc2, __rc3; __rc2-__rc19 are free to use. (In C,
// the 32-bit bookkeeping around the loop took 900 cycles a call: a C64
// program's C has no zero page for its variables.)
//
// The products go in chunks of up to 255. The loop reads a and b with
// absolute,X addressing, their addresses written into the instructions at
// each chunk. Each product is added as an unsigned number, plus 32768 (its
// high byte's top bit flipped), into a 24-bit sum (255 * 65535 < 2^24), and
// count * 32768 comes off at the chunk's end: no sign to extend for each
// one. About 100 cycles a product, and 150 a call.
//   __rc6/7 and __rc8/9: the low bytes' pointers for f(a + b) and f(b - a);
//   __rc10/11 and __rc12/13 the high bytes'; __rc14 the chunk's count;
//   __rc15 a product's low byte; __rc16-18 the chunk's sum.
__asm__(
    "  .section .text.cs_c64_dot_i8,\"ax\",@progbits\n"
    "  .globl cs_c64_dot_i8\n"
    "cs_c64_dot_i8:\n"
    "  ldy __rc7\n"
    "  bpl .Ldot_some\n"
    "  jmp .Ldot_none\n"                 // n < 0: nothing
    ".Ldot_some:\n"
    "  sta zero_dot_left\n"              // n's low 16 bits: no memory holds more
    "  stx zero_dot_left+1\n"
    "  lda zero_dot_ready\n"
    "  bne .Ldot_ready\n"
    "  lda __rc2\n"                      // build the table, keeping a and b
    "  pha\n"
    "  lda __rc3\n"
    "  pha\n"
    "  lda __rc4\n"
    "  pha\n"
    "  lda __rc5\n"
    "  pha\n"
    "  jsr zero_dot_init\n"
    "  pla\n"
    "  sta __rc5\n"
    "  pla\n"
    "  sta __rc4\n"
    "  pla\n"
    "  sta __rc3\n"
    "  pla\n"
    "  sta __rc2\n"
    ".Ldot_ready:\n"
    "  lda #0\n"
    "  sta zero_dot_sum\n"
    "  sta zero_dot_sum+1\n"
    "  sta zero_dot_sum+2\n"
    "  sta zero_dot_sum+3\n"
    "  lda #mos16hi(zero_dot_table)\n"   // the low bytes' pages
    "  sta __rc7\n"
    "  sta __rc9\n"
    "  clc\n"
    "  adc #2\n"                         // the high bytes'
    "  sta __rc11\n"
    "  sta __rc13\n"
    ".Ldot_chunk:\n"
    "  lda zero_dot_left+1\n"
    "  bne .Ldot_full\n"
    "  lda zero_dot_left\n"
    "  bne .Ldot_count\n"
    "  jmp .Ldot_done\n"
    ".Ldot_full:\n"
    "  lda #255\n"
    ".Ldot_count:\n"
    "  sta __rc14\n"
    "  sec\n"
    "  lda zero_dot_left\n"
    "  sbc __rc14\n"
    "  sta zero_dot_left\n"
    "  lda zero_dot_left+1\n"
    "  sbc #0\n"
    "  sta zero_dot_left+1\n"
    "  lda __rc2\n"                      // a and b into the loop's loads
    "  sta .Ldot_a+1\n"
    "  lda __rc3\n"
    "  sta .Ldot_a+2\n"
    "  lda __rc4\n"
    "  sta .Ldot_b+1\n"
    "  lda __rc5\n"
    "  sta .Ldot_b+2\n"
    "  lda #0\n"
    "  sta __rc16\n"
    "  sta __rc17\n"
    "  sta __rc18\n"
    "  tax\n"
    ".Ldot_loop:\n"
    ".Ldot_a:\n"
    "  lda $ffff,x\n"                    // A = a + 128
    "  eor #$80\n"
    "  sta __rc6\n"
    "  sta __rc10\n"
    "  eor #$ff\n"                       // 256 - A: A is 1-255, so no carry
    "  clc\n"
    "  adc #1\n"
    "  sta __rc8\n"
    "  sta __rc12\n"
    ".Ldot_b:\n"
    "  lda $ffff,x\n"                    // Y = B = b + 128
    "  eor #$80\n"
    "  tay\n"
    "  sec\n"                            // f(a + b) - f(b - a) = a * b, 16 bits
    "  lda (__rc6),y\n"
    "  sbc (__rc8),y\n"
    "  sta __rc15\n"
    "  lda (__rc10),y\n"
    "  sbc (__rc12),y\n"
    "  eor #$80\n"                       // + 32768: unsigned
    "  tay\n"
    "  clc\n"
    "  lda __rc15\n"
    "  adc __rc16\n"
    "  sta __rc16\n"
    "  tya\n"
    "  adc __rc17\n"
    "  sta __rc17\n"
    "  bcc .Ldot_next\n"
    "  inc __rc18\n"
    ".Ldot_next:\n"
    "  inx\n"
    "  cpx __rc14\n"
    "  bne .Ldot_loop\n"
    "  lda __rc14\n"                     // - count * 32768: count / 2 off the top
    "  lsr a\n"                          // byte, count's low bit off bit 7 of
    "  sta __rc19\n"                     // the middle one
    "  lda #0\n"
    "  ror a\n"
    "  sta __rc15\n"
    "  sec\n"
    "  lda __rc17\n"
    "  sbc __rc15\n"
    "  sta __rc17\n"
    "  lda __rc18\n"
    "  sbc __rc19\n"
    "  sta __rc18\n"
    "  asl a\n"                          // the chunk's sign, as a fourth byte
    "  lda #0\n"
    "  adc #$ff\n"
    "  eor #$ff\n"
    "  sta __rc19\n"
    "  clc\n"                            // the sum += the chunk's
    "  lda __rc16\n"
    "  adc zero_dot_sum\n"
    "  sta zero_dot_sum\n"
    "  lda __rc17\n"
    "  adc zero_dot_sum+1\n"
    "  sta zero_dot_sum+1\n"
    "  lda __rc18\n"
    "  adc zero_dot_sum+2\n"
    "  sta zero_dot_sum+2\n"
    "  lda __rc19\n"
    "  adc zero_dot_sum+3\n"
    "  sta zero_dot_sum+3\n"
    "  clc\n"                            // a and b move on by the count
    "  lda __rc2\n"
    "  adc __rc14\n"
    "  sta __rc2\n"
    "  bcc 1f\n"
    "  inc __rc3\n"
    "1:\n"
    "  clc\n"
    "  lda __rc4\n"
    "  adc __rc14\n"
    "  sta __rc4\n"
    "  bcc 2f\n"
    "  inc __rc5\n"
    "2:\n"
    "  jmp .Ldot_chunk\n"
    ".Ldot_done:\n"
    "  lda zero_dot_sum+2\n"
    "  sta __rc2\n"
    "  lda zero_dot_sum+3\n"
    "  sta __rc3\n"
    "  lda zero_dot_sum\n"
    "  ldx zero_dot_sum+1\n"
    "  rts\n"
    ".Ldot_none:\n"
    "  lda #0\n"
    "  sta __rc2\n"
    "  sta __rc3\n"
    "  tax\n"
    "  rts\n"
    "  .text\n");

// ---- the RAM under the KERNAL ROM (C64.KernalRom) ---------------------------------------
//
// The processor port at $01 says what the 6502 sees at $A000 and $E000. A
// program runs with $36: RAM at $A000 (BASIC's ROM is out), the KERNAL ROM at
// $E000. With $35 the RAM under the KERNAL is there instead, 8 KB, and the
// KERNAL isn't: C64.KernalRom = false. Writes reach that RAM either way.
//
// Without the KERNAL, two things would break, and both are mended in the RAM
// at the KERNAL's own addresses, which the 6502 reads exactly when the ROM is
// out:
//
// - Its jump table, $FF81-$FFF3, 39 entries of 3 bytes that programs call
//   (CHROUT at $FFD2 prints a character: putchar, Console, the runtime's
//   output and input all end there). Each entry in RAM is JSR
//   zero_kernal_gate: the gate switches the ROM in, calls the same entry in
//   it with the caller's A, X, Y and flags, and switches it out again,
//   keeping the routine's A, X, Y and flags. So any code that calls the
//   KERNAL works, the C library's and a program's own C as well.
// - The interrupt vectors, $FFFA-$FFFF. The IRQ (the KERNAL's 60 a second:
//   the keyboard, the jiffy clock, the cursor) and the NMI (the RESTORE key,
//   RS-232) go to handlers in the program's RAM, which switch the ROM in
//   and enter the KERNAL's own, with a return made to come back to them;
//   they switch it out again and return to the program.
//
// What is left for the program is $E000-$FF80, 8,065 bytes. The program's
// end switches the ROM back in.
#define PORT (*(volatile unsigned char *)1)

unsigned char zero_gate_a, zero_gate_x, zero_gate_p; // the gate's registers in transit

void zero_kernal_gate(void);
void zero_irq_ram(void);
void zero_nmi_ram(void);
__asm__(
    "  .section .text.zero_kernal_gate,\"ax\",@progbits\n"
    "  .globl zero_kernal_gate\n"
    "zero_kernal_gate:\n"
    "  sta zero_gate_a\n"
    "  stx zero_gate_x\n"
    "  php\n"
    "  pla\n"
    "  sta zero_gate_p\n"              // the caller's flags (some entries take C)
    "  pla\n"                          // where the JSR at $FFxx returns: $FFxx + 2
    "  sec\n"
    "  sbc #2\n"
    "  sta .Lgate_call+1\n"            // the entry, in the ROM ($FF81 and up: no borrow)
    "  pla\n"                          // its high byte, $FF
    "  lda $01\n"
    "  pha\n"                          // the program's port, for the way back
    "  and #$f8\n"
    "  ora #$06\n"                     // $36: the KERNAL in, the RAM at $A000 kept
    "  sta $01\n"
    "  lda zero_gate_p\n"
    "  pha\n"
    "  lda zero_gate_a\n"
    "  ldx zero_gate_x\n"
    "  plp\n"
    ".Lgate_call:\n"
    "  jsr $ff00\n"
    "  php\n"
    "  sta zero_gate_a\n"
    "  pla\n"
    "  sta zero_gate_p\n"              // the routine's flags (C: an error)
    "  pla\n"
    "  sta $01\n"
    "  lda zero_gate_p\n"
    "  pha\n"
    "  lda zero_gate_a\n"
    "  plp\n"
    "  rts\n"                          // to whoever called $FFxx
    // An interrupt: the KERNAL in, then its handler, which ends with RTI to
    // a frame pushed here (flags $04: interrupts off, not a BRK).
    "  .globl zero_irq_ram\n"
    "zero_irq_ram:\n"
    "  pha\n"
    "  lda $01\n"
    "  pha\n"
    "  and #$f8\n"
    "  ora #$06\n"
    "  sta $01\n"
    "  lda #mos16hi(.Lirq_back)\n"
    "  pha\n"
    "  lda #mos16lo(.Lirq_back)\n"
    "  pha\n"
    "  lda #$04\n"
    "  pha\n"
    "  jmp $ff48\n"                    // the KERNAL's IRQ entry
    ".Lirq_back:\n"
    "  pla\n"
    "  sta $01\n"
    "  pla\n"
    "  rti\n"
    "  .globl zero_nmi_ram\n"
    "zero_nmi_ram:\n"
    "  pha\n"
    "  lda $01\n"
    "  pha\n"
    "  and #$f8\n"
    "  ora #$06\n"
    "  sta $01\n"
    "  lda #mos16hi(.Lirq_back)\n"
    "  pha\n"
    "  lda #mos16lo(.Lirq_back)\n"
    "  pha\n"
    "  lda #$04\n"
    "  pha\n"
    "  jmp $fe43\n"                    // the KERNAL's NMI entry
    "  .text\n");

void cs_c64_kernal_rom(i32 on)
{
    if (on) {
        PORT = (PORT & 0xF8) | 0x06;
        return;
    }
    if ((PORT & 0x07) == 0x05)
        return;
    volatile unsigned char *under = (volatile unsigned char *)0xFF81;
    for (unsigned char i = 0; i < 39; i++, under += 3) {
        under[0] = 0x20; // JSR
        under[1] = (unsigned char)(unsigned)zero_kernal_gate;
        under[2] = (unsigned char)((unsigned)zero_kernal_gate >> 8);
    }
    volatile unsigned char *vectors = (volatile unsigned char *)0xFFFA;
    vectors[0] = (unsigned char)(unsigned)zero_nmi_ram;
    vectors[1] = (unsigned char)((unsigned)zero_nmi_ram >> 8);
    vectors[4] = (unsigned char)(unsigned)zero_irq_ram;
    vectors[5] = (unsigned char)((unsigned)zero_irq_ram >> 8);
    PORT = (PORT & 0xF8) | 0x05;
}

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

// Catch.Run (zerocore/Runtime/Catch.cs): a throw inside body jumps back
// here through ZeroCatchJump, and ZeroCatchCall returns as if body had.
// llvm-mos's setjmp saves the hardware and soft stack pointers and the
// registers a function must keep. Runtime imports keep their names (no cs_)
// and take the shadow stack first (see wasm/runtime.c); body gets the
// caller's.
#include <setjmp.h>

void ZeroCatchCall(void *shadowStack, void **saved, void (*body)(void *, void *), void *argument)
{
    jmp_buf buffer;
    *saved = buffer;
    if (setjmp(buffer) == 0)
        body(shadowStack, argument);
}

void ZeroCatchJump(void *shadowStack, void *saved)
{
    (void)shadowStack;
    longjmp(*(jmp_buf *)saved, 1);
}

i32 cs_getpagesize(void) { return 256; }

i32 cs_mprotect(void *address, u32 length, i32 protection)
{
    (void)address; (void)length; (void)protection;
    return 0;
}

// lib/ArenaGuard.cs (Arena.GuardPages) asks for pages with mmap and for
// faults with sigaction. A 6502 has no pages to protect, so mmap fails,
// and GuardPages stays off.
void *cs_mmap(void *address, u32 length, i32 protection, i32 flags, i32 file, i64 offset)
{
    (void)address; (void)length; (void)protection; (void)flags; (void)file; (void)offset;
    return (void *)-1;
}

i32 cs_sigaction(i32 signal, const void *action, void *previous)
{
    (void)signal; (void)action; (void)previous;
    return -1;
}

// Waits on the clock above, so with keep_time on it returns with
// interrupts off too.
i32 cs_usleep(u32 microseconds)
{
    u32 start = clock_ticks(), wait = (microseconds + 16666) / 16667;
    while (clock_ticks() - start < wait) { }
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

// Which system this is, for lib/FileSystem.cs (Path asks): 9, the Commodore 64.
i32 cs_ZeroSystem(void) { return 9; }
