// The C side of a UEFI program: the C library functions zerocore and lib/
// call (memory, write, read, abort, the clock, the pthreads lib/Threads.cs
// uses), written over the firmware's boot services, since there is no C
// library underneath. libm.c has the math functions; start.c the entry point.
//
// Compiled with clang for x86_64-pc-windows-msvc or aarch64-pc-windows-msvc.

#include "efi.h"

Handle efi_image;
SystemTable *efi_system;
ShellParameters *efi_shell;

// The compiler references this from any C file that uses floating point on
// Windows targets; the C library would define it.
int _fltused = 0;

// ---- where the compiler's module list is ------------------------------------
//
// As in win/runtime.c: the compiler puts a pointer to each module's header in
// section ".modules$I", and the linker sorts these two around it.

#pragma section(".modules$A", read)
#pragma section(".modules$Z", read)
__declspec(allocate(".modules$A")) void *__start___modules[1] = { 0 };
__declspec(allocate(".modules$Z")) void *__stop___modules[1] = { 0 };

// ---- memory -----------------------------------------------------------------

void *memset(void *destination, int value, usize count)
{
    u8 *d = destination;
    while (count--)
        *d++ = (u8)value;
    return destination;
}

void *memcpy(void *destination, const void *source, usize count)
{
    u8 *d = destination;
    const u8 *s = source;
    while (count--)
        *d++ = *s++;
    return destination;
}

void *memmove(void *destination, const void *source, usize count)
{
    u8 *d = destination;
    const u8 *s = source;
    if (d < s) {
        while (count--)
            *d++ = *s++;
    } else {
        d += count;
        s += count;
        while (count--)
            *--d = *--s;
    }
    return destination;
}

int memcmp(const void *a, const void *b, usize count)
{
    const u8 *x = a, *y = b;
    for (; count; count--, x++, y++)
        if (*x != *y)
            return *x - *y;
    return 0;
}

// Pool memory is 8-byte aligned; C's malloc promises 16, which SIMD code may
// count on. Each block keeps the pool's own pointer just before it, and its
// size before that, for realloc.
void *malloc(usize size)
{
    u8 *pool = 0;
    if (size > (usize)-48 || EFI_ERROR(efi_system->boot_services->allocate_pool(EfiLoaderData, size + 24, (void **)&pool)))
        return 0;
    u8 *block = (u8 *)(((usize)pool + 16 + 15) & ~(usize)15);
    ((void **)block)[-1] = pool;
    ((usize *)block)[-2] = size;
    return block;
}

void *calloc(usize count, usize size)
{
    if (size != 0 && count > (usize)-1 / size)
        return 0;
    void *block = malloc(count * size);
    if (block)
        memset(block, 0, count * size);
    return block;
}

void free(void *block)
{
    if (block)
        efi_system->boot_services->free_pool(((void **)block)[-1]);
}

void *realloc(void *block, usize size)
{
    if (!block)
        return malloc(size);
    usize had = ((usize *)block)[-2];
    void *moved = malloc(size);
    if (moved) {
        memcpy(moved, block, had < size ? had : size);
        free(block);
    }
    return moved;
}

// ---- standard input and output ----------------------------------------------
//
// Run by the UEFI Shell with "> file" or "< file", the program's output or
// input is a file (the shell's parameters say which); bytes go to and from it
// unchanged. Otherwise the console is UTF-16: write converts from UTF-8 and
// turns "\n" into "\r\n", and read collects a line from the keyboard, showing
// it as it is typed.

static File *file_or_null(File *file)
{
    if (!file)
        return 0;
    static const Guid file_info = FILE_INFO_GUID;
    usize size = 0;
    // A real file answers that the buffer is too small; the shell's console
    // stand-ins don't support the question.
    return file->get_info(file, &file_info, &size, 0) == EFI_BUFFER_TOO_SMALL ? file : 0;
}

static File *output_file, *error_file, *input_file;

void efi_runtime_start(void)
{
    if (efi_shell) {
        output_file = file_or_null(efi_shell->std_out);
        error_file = file_or_null(efi_shell->std_err);
        input_file = file_or_null(efi_shell->std_in);
    }
}

typedef struct { u32 value; int needed; } Decoder;

static Decoder output_decoder, error_decoder;

static void console_write(TextOutput *console, Decoder *decoder, const u8 *bytes, usize count)
{
    char16 text[260];
    usize length = 0;
    for (usize i = 0; i < count; i++) {
        u32 b = bytes[i];
        u32 c;
        if (decoder->needed > 0) {
            if ((b & 0xC0) == 0x80) {
                decoder->value = (decoder->value << 6) | (b & 0x3F);
                if (--decoder->needed > 0)
                    continue;
                c = decoder->value;
            } else {
                decoder->needed = 0;
                c = 0xFFFD;
                i--; // this byte starts something new
            }
        } else if (b < 0x80) {
            c = b;
        } else if (b >= 0xC2 && b < 0xE0) {
            decoder->value = b & 0x1F;
            decoder->needed = 1;
            continue;
        } else if (b >= 0xE0 && b < 0xF0) {
            decoder->value = b & 0x0F;
            decoder->needed = 2;
            continue;
        } else if (b >= 0xF0 && b < 0xF5) {
            decoder->value = b & 0x07;
            decoder->needed = 3;
            continue;
        } else {
            c = 0xFFFD;
        }

        if (c == '\n') {
            text[length++] = '\r';
            text[length++] = '\n';
        } else if (c >= 0x10000 && c <= 0x10FFFF) {
            c -= 0x10000;
            text[length++] = (char16)(0xD800 + (c >> 10));
            text[length++] = (char16)(0xDC00 + (c & 0x3FF));
        } else if (c > 0x10FFFF || (c >= 0xD800 && c < 0xE000)) {
            text[length++] = 0xFFFD;
        } else {
            text[length++] = (char16)c;
        }
        if (length >= 256) {
            text[length] = 0;
            console->output_string(console, text);
            length = 0;
        }
    }
    if (length > 0) {
        text[length] = 0;
        console->output_string(console, text);
    }
}

i64 write(int fd, const void *buffer, usize count)
{
    File *file = fd == 1 ? output_file : fd == 2 ? error_file : 0;
    if (file) {
        usize size = count;
        return EFI_ERROR(file->write(file, &size, buffer)) ? -1 : (i64)size;
    }
    if (fd == 1)
        console_write(efi_system->con_out, &output_decoder, buffer, count);
    else if (fd == 2)
        console_write(efi_system->std_err ? efi_system->std_err : efi_system->con_out, &error_decoder, buffer, count);
    else
        return -1;
    return (i64)count;
}

int puts(const char *text)
{
    usize length = 0;
    while (text[length])
        length++;
    write(1, text, length);
    write(1, "\n", 1);
    return 0;
}

// A line typed at the keyboard, as UTF-8, and how much of it read has handed out.
static u8 line[1024];
static usize line_length, line_given;
static int input_ended;

static void echo(const char *text)
{
    char16 wide[8];
    usize i = 0;
    for (; text[i] && i < 7; i++)
        wide[i] = (u8)text[i];
    wide[i] = 0;
    efi_system->con_out->output_string(efi_system->con_out, wide);
}

static void read_line(void)
{
    TextInput *keyboard = efi_system->con_in;
    line_length = line_given = 0;
    for (;;) {
        InputKey key;
        Status status = keyboard->read_key_stroke(keyboard, &key);
        if (status == EFI_NOT_READY) {
            usize index;
            efi_system->boot_services->wait_for_event(1, &keyboard->wait_for_key, &index);
            continue;
        }
        if (EFI_ERROR(status)) {
            input_ended = 1;
            return;
        }
        u32 c = key.unicode_char;
        if (c == '\r' || c == '\n') {
            echo("\r\n");
            line[line_length++] = '\n';
            return;
        }
        if (c == 4 || c == 26) { // Ctrl+D or Ctrl+Z: the end of the input
            if (line_length == 0)
                input_ended = 1;
            return;
        }
        if (c == 8) { // backspace: remove the last character, all its bytes
            if (line_length > 0) {
                do
                    line_length--;
                while (line_length > 0 && (line[line_length] & 0xC0) == 0x80);
                echo("\b \b");
            }
            continue;
        }
        if (c < 0x20 || line_length + 4 >= sizeof line)
            continue;
        char16 shown[2] = { (char16)c, 0 };
        efi_system->con_out->output_string(efi_system->con_out, shown);
        if (c < 0x80) {
            line[line_length++] = (u8)c;
        } else if (c < 0x800) {
            line[line_length++] = (u8)(0xC0 | (c >> 6));
            line[line_length++] = (u8)(0x80 | (c & 0x3F));
        } else {
            line[line_length++] = (u8)(0xE0 | (c >> 12));
            line[line_length++] = (u8)(0x80 | ((c >> 6) & 0x3F));
            line[line_length++] = (u8)(0x80 | (c & 0x3F));
        }
    }
}

// ---- keys one at a time, for Console.ReadKey (lib/InKeys.cs) -----------------
//
// The POSIX calls ReadKey and KeyAvailable make, for the firmware's keyboard.
// A "termios" is one byte, 1 for raw; tcsetattr remembers it, and in raw mode
// read() returns the next key press as the bytes a Unix terminal sends: its
// character in UTF-8, or an escape sequence (ESC [ A for the up arrow) for a
// key without one. The keyboard protocol doesn't report Shift, Ctrl or Alt
// with those keys. Input from a file (the shell's "<") isn't a terminal.

struct pollfd {
    int fd;
    short events, revents;
};

static int keys_raw;

int isatty(int fd) { return fd == 0 && !input_file; }

int tcgetattr(int fd, u8 *termios)
{
    if (!isatty(fd))
        return -1;
    termios[0] = 0;
    return 0;
}

void cfmakeraw(u8 *termios) { termios[0] = 1; }

int tcsetattr(int fd, int when, const u8 *termios)
{
    (void)when;
    if (!isatty(fd))
        return -1;
    keys_raw = termios[0];
    return 0;
}

// A key press as a terminal's bytes (out has room for 8), or 0 bytes.
static usize key_bytes(InputKey key, u8 *out)
{
    // Scan codes 1-6: up, down, right, left, home, end.
    static const char finals[] = { 'A', 'B', 'C', 'D', 'H', 'F' };
    // 7-0x16: insert, delete, page up, page down, F1-F12.
    static const u8 numbers[] = { 2, 3, 5, 6, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 23, 24 };
    usize n = 0;
    if (key.scan_code >= 1 && key.scan_code <= 6) {
        out[n++] = 0x1B;
        out[n++] = '[';
        out[n++] = (u8)finals[key.scan_code - 1];
        return n;
    }
    if (key.scan_code >= 7 && key.scan_code <= 0x16) {
        u8 number = numbers[key.scan_code - 7];
        out[n++] = 0x1B;
        out[n++] = '[';
        if (number >= 10)
            out[n++] = (u8)('0' + number / 10);
        out[n++] = (u8)('0' + number % 10);
        out[n++] = '~';
        return n;
    }
    if (key.scan_code == 0x17) { // escape
        out[n++] = 0x1B;
        return n;
    }
    u32 c = key.unicode_char;
    if (c == 0)
        return 0;
    if (c < 0x80) {
        out[n++] = (u8)c;
    } else if (c < 0x800) {
        out[n++] = (u8)(0xC0 | (c >> 6));
        out[n++] = (u8)(0x80 | (c & 0x3F));
    } else {
        out[n++] = (u8)(0xE0 | (c >> 12));
        out[n++] = (u8)(0x80 | ((c >> 6) & 0x3F));
        out[n++] = (u8)(0x80 | (c & 0x3F));
    }
    return n;
}

static i64 keys_read(void *buffer, usize count)
{
    TextInput *keyboard = efi_system->con_in;
    for (;;) {
        InputKey key;
        Status status = keyboard->read_key_stroke(keyboard, &key);
        if (status == EFI_NOT_READY) {
            usize index;
            efi_system->boot_services->wait_for_event(1, &keyboard->wait_for_key, &index);
            continue;
        }
        if (EFI_ERROR(status))
            return -1;
        u8 bytes[8];
        usize n = key_bytes(key, bytes);
        if (n == 0)
            continue;
        if (n > count)
            n = count;
        memcpy(buffer, bytes, n);
        return (i64)n;
    }
}

// Only standard input is asked about, and only whether it can be read now.
int poll(struct pollfd *fds, unsigned long count, int timeout)
{
    (void)timeout;
    int ready = 0;
    for (unsigned long i = 0; i < count; i++) {
        fds[i].revents = 0;
        if (fds[i].fd != 0 || !(fds[i].events & 1))
            continue;
        if (!isatty(0) || efi_system->boot_services->check_event(efi_system->con_in->wait_for_key) == EFI_SUCCESS) {
            fds[i].revents = 1;
            ready++;
        }
    }
    return ready;
}

i64 read(int fd, void *buffer, usize count)
{
    if (fd != 0)
        return -1;
    if (keys_raw && !input_file)
        return keys_read(buffer, count);
    if (input_file) {
        usize size = count;
        return EFI_ERROR(input_file->read(input_file, &size, buffer)) ? -1 : (i64)size;
    }
    if (line_given == line_length) {
        if (input_ended)
            return 0;
        read_line();
        if (line_length == 0)
            return 0;
    }
    usize n = line_length - line_given;
    if (n > count)
        n = count;
    memcpy(buffer, line + line_given, n);
    line_given += n;
    return (i64)n;
}

// zerocore prints why before it calls this. 134 is what a shell reports for
// a program that aborted on Linux (128 + SIGABRT).
__declspec(noreturn) void abort(void)
{
    efi_system->boot_services->exit(efi_image, 134, 0, 0);
    for (;;) {
    }
}

// Environment.Exit: the code is the status, as start.c returns Main's.
__declspec(noreturn) void exit(int code)
{
    efi_system->boot_services->exit(efi_image, (Status)(u8)code, 0, 0);
    for (;;) {
    }
}

// Environment.GetEnvironmentVariable: the shell's variables (set, in the
// shell), converted from UTF-16 to UTF-8. Without a shell there are none.
char *getenv(const char *name)
{
    static const Guid shell_guid = SHELL_GUID;
    static char value[4096];
    char16 wide_name[256];
    Shell *shell;
    if (EFI_ERROR(efi_system->boot_services->locate_protocol(&shell_guid, 0, (void **)&shell)))
        return 0;

    // The name: UTF-8 to UTF-16, characters past U+FFFF aside (no name has one).
    usize n = 0;
    for (const u8 *p = (const u8 *)name; *p; n++) {
        if (n == 255)
            return 0;
        u32 c = *p++;
        if (c >= 0xE0 && p[0] && p[1]) {
            c = ((c & 0x0F) << 12) | ((p[0] & 0x3F) << 6) | (p[1] & 0x3F);
            p += 2;
        } else if (c >= 0xC0 && p[0]) {
            c = ((c & 0x1F) << 6) | (p[0] & 0x3F);
            p += 1;
        }
        wide_name[n] = (char16)c;
    }
    wide_name[n] = 0;

    const char16 *wide = shell->get_env(wide_name);
    if (!wide)
        return 0;
    usize length = 0;
    for (; *wide; wide++) {
        u32 c = *wide;
        if (c >= 0xD800 && c <= 0xDBFF && wide[1] >= 0xDC00 && wide[1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (*++wide - 0xDC00);
        } else if (c >= 0xD800 && c <= 0xDFFF) {
            c = 0xFFFD;
        }
        if (length + 5 > sizeof value)
            return 0;
        if (c < 0x80) {
            value[length++] = (char)c;
        } else if (c < 0x800) {
            value[length++] = (char)(0xC0 | (c >> 6));
            value[length++] = (char)(0x80 | (c & 0x3F));
        } else if (c < 0x10000) {
            value[length++] = (char)(0xE0 | (c >> 12));
            value[length++] = (char)(0x80 | ((c >> 6) & 0x3F));
            value[length++] = (char)(0x80 | (c & 0x3F));
        } else {
            value[length++] = (char)(0xF0 | (c >> 18));
            value[length++] = (char)(0x80 | ((c >> 12) & 0x3F));
            value[length++] = (char)(0x80 | ((c >> 6) & 0x3F));
            value[length++] = (char)(0x80 | (c & 0x3F));
        }
    }
    value[length] = 0;
    return value;
}

// ---- the clock --------------------------------------------------------------
//
// The firmware has a calendar clock (GetTime) but no monotonic one with good
// resolution. The processor's counter is one: on x64 the time-stamp counter,
// whose rate is measured once against the firmware's Stall; on arm64 the
// generic timer, whose rate the processor reports.

static u64 counter(void)
{
#if defined(__x86_64__)
    u32 low, high;
    __asm__ volatile("rdtsc" : "=a"(low), "=d"(high));
    return ((u64)high << 32) | low;
#else
    u64 value;
    __asm__ volatile("isb; mrs %0, cntvct_el0" : "=r"(value));
    return value;
#endif
}

static u64 counter_frequency(void)
{
    static u64 frequency;
    if (frequency == 0) {
#if defined(__x86_64__)
        u64 start = counter();
        efi_system->boot_services->stall(20000);
        frequency = (counter() - start) * 50;
#else
        __asm__ volatile("mrs %0, cntfrq_el0" : "=r"(frequency));
#endif
        if (frequency == 0)
            frequency = 1;
    }
    return frequency;
}

// Days from 1970-01-01 to a date (Howard Hinnant's days_from_civil).
static i64 days_from_civil(i64 year, i64 month, i64 day)
{
    year -= month <= 2;
    i64 era = (year >= 0 ? year : year - 399) / 400;
    i64 year_of_era = year - era * 400;
    i64 day_of_year = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
    i64 day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    return era * 146097 + day_of_era - 719468;
}

// Linux's clock numbers, as lib/Clock.cs passes them. 0 is the wall clock: the
// firmware's calendar clock, read as it is. Its time zone is usually
// unspecified, and PCs usually keep local time. Anything else is the
// monotonic clock, the processor's counter.
int clock_gettime(int clock, i64 *timespec)
{
    if (clock == 0) {
        Time t;
        if (EFI_ERROR(efi_system->runtime_services->get_time(&t, 0)))
            return -1;
        timespec[0] = days_from_civil(t.year, t.month, t.day) * 86400 + t.hour * 3600 + t.minute * 60 + t.second;
        timespec[1] = t.nanosecond;
        return 0;
    }
    u64 frequency = counter_frequency();
    u64 count = counter();
    timespec[0] = (i64)(count / frequency);
    timespec[1] = (i64)((count % frequency) * 1000000000 / frequency);
    return 0;
}

// struct tm's first nine fields: all zerocore's TimeZoneInfo reads.
typedef struct { int sec, min, hour, mday, mon, year, wday, yday, isdst; } zero_tm;

// A Unix time broken down as UTC, for any year (Howard Hinnant's
// civil_from_days).
static void civil_tm(i64 t, zero_tm *tm)
{
    i64 days = t / 86400, rest = t % 86400;
    if (rest < 0) {
        rest += 86400;
        days--;
    }
    tm->hour = (int)(rest / 3600);
    tm->min = (int)(rest / 60 % 60);
    tm->sec = (int)(rest % 60);
    tm->wday = (int)(((days % 7) + 11) % 7); // 1970-01-01 was a Thursday
    i64 z = days + 719468;
    i64 era = (z >= 0 ? z : z - 146096) / 146097;
    i64 day_of_era = z - era * 146097;
    i64 year_of_era = (day_of_era - day_of_era / 1460 + day_of_era / 36524 - day_of_era / 146096) / 365;
    i64 day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    i64 shifted = (5 * day_of_year + 2) / 153; // 0 is March
    i64 month = shifted < 10 ? shifted + 3 : shifted - 9;
    tm->mday = (int)(day_of_year - (153 * shifted + 2) / 5 + 1);
    tm->mon = (int)(month - 1);
    tm->year = (int)(year_of_era + era * 400 + (month <= 2) - 1900);
    tm->yday = 0;
    tm->isdst = 0;
}

// The firmware's clock has no time zone (see clock_gettime): local time is the
// clock's, so zerocore's TimeZoneInfo.Local finds no offset.
zero_tm *localtime_r(const i64 *time, zero_tm *result)
{
    civil_tm(*time, result);
    return result;
}

i64 readlink(const char *path, char *buffer, u64 size) { return -1; }

// ---- random bytes, for lib/CryptoRandom.cs ----------------------------------
//
// From the firmware's EFI_RNG_PROTOCOL, with its default algorithm. Firmware
// without one (OVMF has it only when the CPU has RDRAND or QEMU gives the
// machine a virtio-rng device) gets -1, and RandomNumberGenerator throws.

int getentropy(void *buffer, usize length)
{
    static const Guid rng_guid = RNG_GUID;
    static Rng *rng;
    if (length > 256)
        return -1;
    if (!rng && EFI_ERROR(efi_system->boot_services->locate_protocol(&rng_guid, 0, (void **)&rng)))
        return rng = 0, -1;
    return EFI_ERROR(rng->get_rng(rng, 0, length, buffer)) ? -1 : 0;
}

// Root certificates: none here, as for directories below.
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

// ---- threads ----------------------------------------------------------------
//
// One processor, no scheduler: as on DOS, a thread runs to its end when it is
// created, and joining it just reports that it ended.

// No pipes (lib/FileAsync.cs then reads and writes files without its I/O
// thread, blocking, as its threads run to their end when created).
int pipe(int fds[2])
{
    (void)fds;
    return -1;
}

int pthread_create(void **thread, void *attributes, void *(*start)(void *), void *argument)
{
    (void)attributes;
    *thread = 0;
    start(argument);
    return 0;
}

int pthread_join(void *thread, void **result)
{
    (void)thread;
    if (result)
        *result = 0;
    return 0;
}

int sched_yield(void) { return 0; }

int get_nprocs(void) { return 1; }

#define KEY_COUNT 64
static void *key_values[KEY_COUNT];
static u32 key_count;

int pthread_key_create(u64 *key, void (*destructor)(void *))
{
    (void)destructor;
    if (key_count >= KEY_COUNT)
        return 11; // EAGAIN
    *key = key_count++;
    return 0;
}

void *pthread_getspecific(u64 key) { return key < key_count ? key_values[key] : 0; }

int pthread_setspecific(u64 key, void *value)
{
    if (key >= key_count)
        return 22; // EINVAL
    key_values[key] = value;
    return 0;
}

// ---- fiber stacks -----------------------------------------------------------
//
// What lib/Fibers.cs calls. The firmware runs with its memory identity-mapped
// and no way for a program to protect a page, so a fiber's stack has no guard
// page here: mprotect succeeds and does nothing.

int getpagesize(void) { return 4096; }

int mprotect(void *address, usize length, int protection)
{
    (void)address;
    (void)length;
    (void)protection;
    return 0;
}

// lib/ArenaGuard.cs (Arena.GuardPages) asks for pages with mmap and for
// faults with sigaction. No page can be protected here, so mmap fails,
// and GuardPages stays off.
void *mmap(void *address, usize length, int protection, int flags, int file, i64 offset)
{
    (void)address; (void)length; (void)protection; (void)flags; (void)file; (void)offset;
    return (void *)-1;
}

int sigaction(int signal, const void *action, void *previous)
{
    (void)signal; (void)action; (void)previous;
    return -1;
}

int usleep(u32 microseconds)
{
    efi_system->boot_services->stall(microseconds);
    return 0;
}

// ---- lib/FileSystem.cs ----------------------------------------------------

// Which system this is: zerocore/native/win-<arch>.S says (5, UEFI).

// File.Exists calls stat only on Linux, macOS, Windows and WebAssembly; here
// it opens the file instead. These are there for the link, and always fail.
static int file_system_errno;
int *__errno_location(void) { return &file_system_errno; }
int __xstat64(int version, const char *path, void *out) { (void)version; (void)path; (void)out; file_system_errno = 38; return -1; }
int __lxstat64(int version, const char *path, void *out) { (void)version; (void)path; (void)out; file_system_errno = 38; return -1; }
