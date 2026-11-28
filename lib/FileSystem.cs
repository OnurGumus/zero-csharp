// What File, Directory and Path share: the error codes they return, which
// system the program runs on, and the C functions underneath.
//
// Every system is called through one set of C functions, named as glibc names
// them, with glibc's struct layouts: __xstat64 and __lxstat64 (stat and
// lstat), opendir, readdir64 and closedir, mkdir, rmdir, rename, getcwd,
// chdir and __errno_location. Linux has them all. The others supply what
// their C library lacks or lays out differently, in their runtime files
// (mac/runtime.c, win/runtime.c, wasm/runtime.c, android/runtime.c). On Windows the ones the
// Universal CRT also has are linked with /wrap:, as fopen is, and take UTF-8.
//
// Directories, file information, moving and copying work on Linux, macOS,
// Windows, Android and WebAssembly (under wasmtime, inside the directories given with
// --dir). Path's functions work everywhere: they only look at text.
// ZeroSystem(), in each target's assembly or runtime file, says which system
// this is.

using System;
using System.Runtime.InteropServices;

/// <summary>Why a file or directory operation failed, or None.</summary>
public enum FileError
{
    /// <summary>It worked.</summary>
    None,
    /// <summary>The file or directory, or a directory on the way to it, doesn't exist.</summary>
    NotFound,
    /// <summary>The program isn't allowed to do this, or the file system is read-only.</summary>
    AccessDenied,
    /// <summary>Something already has that name.</summary>
    AlreadyExists,
    /// <summary>The directory still has entries in it.</summary>
    NotEmpty,
    /// <summary>A directory was expected, and this, or a part of the path, is a file.</summary>
    NotADirectory,
    /// <summary>A file was expected, and this is a directory.</summary>
    IsADirectory,
    /// <summary>The path is empty, too long, holds a '\0', or isn't valid UTF-16.</summary>
    InvalidPath,
    /// <summary>The disk is full.</summary>
    NoSpace,
    /// <summary>Anything else.</summary>
    Other,
}

// Which system the program runs on, as ZeroSystem() returns it.
enum SystemKind
{
    LinuxX64 = 1,
    LinuxArm64 = 2,
    MacOS = 3,
    Windows = 4,
    Uefi = 5,
    Wasi = 6,
    Dos = 7,
    Microcontroller = 8,
    C64 = 9,
    Android = 10,
}

static class FileSystem
{
#if ZERO_DOTNET
    // Real .NET knows which system it runs on (no native code needed for it).
    static int ZeroSystem() => (int)(OperatingSystem.IsMacOS() ? SystemKind.MacOS
        : OperatingSystem.IsWindows() ? SystemKind.Windows
        : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? SystemKind.LinuxArm64 : SystemKind.LinuxX64);
#else
    [DllImport("*")] static safe extern int ZeroSystem();
#endif

    [DllImport("libc")] internal static unsafe extern int* __errno_location();
    [DllImport("libc")] internal static unsafe extern int __xstat64(int version, byte* path, byte* stat);
    [DllImport("libc")] internal static unsafe extern int __lxstat64(int version, byte* path, byte* stat);
    [DllImport("libc")] internal static unsafe extern void* opendir(byte* path);
    [DllImport("libc")] internal static unsafe extern byte* readdir64(void* dir);
    [DllImport("libc")] internal static unsafe extern int closedir(void* dir);
    [DllImport("libc")] internal static unsafe extern int mkdir(byte* path, uint mode);
    [DllImport("libc")] internal static unsafe extern int rmdir(byte* path);
    [DllImport("libc")] internal static unsafe extern int rename(byte* from, byte* to);
    [DllImport("libc")] internal static unsafe extern byte* getcwd(byte* buffer, nuint size);
    [DllImport("libc")] internal static unsafe extern int chdir(byte* path);
    [DllImport("libc")] internal static unsafe extern int remove(byte* path);

    // The longest path, in UTF-8 bytes with its '\0', that these functions take.
    internal const int MaxPath = 4096;

    // Room for a path as a C string: its UTF-8 (at most 3 bytes a char) and
    // the 0, up to `max`; a longer one doesn't fit, and ToC says so. A fiber
    // on a microcontroller has 4 KB of stack in all, so a stackalloc of
    // MaxPath there would overflow it.
    internal static int PathRoom(ReadOnlySpan<char> path, int max = MaxPath)
        => path.Length < max / 3 ? path.Length * 3 + 1 : max;

    static int s_system;

    internal static SystemKind System
    {
        get
        {
            if (s_system == 0)
                s_system = ZeroSystem();
            return (SystemKind)s_system;
        }
    }

    // Windows, DOS and UEFI also take '\' between names, and write it.
    internal static bool Backslashes
    {
        get
        {
            SystemKind system = System;
            return system == SystemKind.Windows || system == SystemKind.Dos || system == SystemKind.Uefi;
        }
    }

    // Whether the system has stat, directories and rename: Linux, macOS,
    // Windows, WebAssembly, Android. Elsewhere those imports are placeholders that fail.
    internal static bool HasCalls
    {
        get
        {
            SystemKind system = System;
            return system <= SystemKind.Windows || system == SystemKind.Wasi || system == SystemKind.Android;
        }
    }

    // ---- errors ---------------------------------------------------------------

    internal static void ClearError()
    {
        unsafe { *__errno_location() = 0; } // SAFETY: the thread's errno, which the C library keeps
    }

    static int Errno
    {
        get { unsafe { return *__errno_location(); } } // SAFETY: as above
    }

    // The error the last failed call left, as a FileError. The numbers differ:
    // WASI numbers its own way, and macOS and the Universal CRT differ from
    // Linux in a few.
    internal static FileError LastError()
    {
        int e = Errno;
        switch (System)
        {
            case SystemKind.Wasi:
                return e switch
                {
                    0 => FileError.Other,
                    44 => FileError.NotFound,
                    2 or 63 or 69 or 76 => FileError.AccessDenied, // EACCES, EPERM, EROFS, ENOTCAPABLE
                    20 => FileError.AlreadyExists,
                    55 => FileError.NotEmpty,
                    54 => FileError.NotADirectory,
                    31 => FileError.IsADirectory,
                    37 or 28 => FileError.InvalidPath, // ENAMETOOLONG, EINVAL
                    51 => FileError.NoSpace,
                    _ => FileError.Other,
                };
            case SystemKind.MacOS:
                if (e == 66) return FileError.NotEmpty;
                if (e == 63) return FileError.InvalidPath; // ENAMETOOLONG
                if (e == 39 || e == 36) return FileError.Other;
                break;
            case SystemKind.Windows:
                if (e == 41) return FileError.NotEmpty;
                if (e == 38) return FileError.InvalidPath; // ENAMETOOLONG
                if (e == 39 || e == 36) return FileError.Other;
                break;
        }
        return e switch
        {
            2 => FileError.NotFound,
            1 or 13 or 30 => FileError.AccessDenied, // EPERM, EACCES, EROFS
            17 => FileError.AlreadyExists,
            39 => FileError.NotEmpty,
            20 => FileError.NotADirectory,
            21 => FileError.IsADirectory,
            36 or 22 => FileError.InvalidPath, // ENAMETOOLONG, EINVAL
            28 => FileError.NoSpace,
            _ => FileError.Other,
        };
    }

    // Whether the last failed call failed because source and destination are
    // on different devices (EXDEV), which rename can't cross.
    internal static bool LastErrorIsCrossDevice()
        => Errno == (System == SystemKind.Wasi ? 75 : 18);

    // Whether the last call left an error, for readdir64, which returns null
    // both at the end and on failure.
    internal static bool HasError => Errno != 0;

    // ---- paths ----------------------------------------------------------------

    // The path as UTF-8 with a '\0' after it, into dest. -1 if it's empty,
    // holds a '\0', or doesn't fit; else its length without the '\0'.
    internal static int ToC(ReadOnlySpan<char> path, Span<byte> dest)
    {
        if (path.Length == 0 || path.IndexOf('\0') >= 0)
            return -1;
        int next = 0;
        int length = Utf8.Encode(path, ref next, dest.Slice(0, dest.Length - 1));
        if (next < path.Length)
            return -1;
        dest[length] = 0;
        return length;
    }

    // How many bytes the text takes as UTF-8 (a lone surrogate becomes U+FFFD's 3).
    internal static int Utf8Length(ReadOnlySpan<char> text)
    {
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80)
                n += 1;
            else if (c < 0x800)
                n += 2;
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                n += 4;
                i++;
            }
            else
                n += 3;
        }
        return n;
    }

    // A string of UTF-8 bytes.
    internal static string ToString(ReadOnlySpan<byte> bytes)
    {
        string result = string.Allocate(bytes.Length);
        Span<char> chars;
        unsafe { chars = MemoryMarshal.CreateSpan(ref result.FirstChar, bytes.Length); } // SAFETY: the string is new, nothing else has it yet, and the span covers its length chars.
        int used = Utf8.Decode(bytes, chars);
        return used == bytes.Length ? result : string.FromChars(chars.Slice(0, used));
    }

    // The length of a '\0'-terminated string.
    internal static int Length(byte* text)
    {
        int n = 0;
        unsafe
        {
            // SAFETY: callers pass '\0'-terminated strings.
            while (text[n] != 0)
                n++;
        }
        return n;
    }

    // The length of a '\0'-terminated string in a span; the span's length if there's no '\0'.
    internal static int Length(ReadOnlySpan<byte> text)
    {
        int n = 0;
        while (n < text.Length && text[n] != 0)
            n++;
        return n;
    }

    // ---- stat -----------------------------------------------------------------

    // What stat says about a file: the parts lib/ uses.
    internal struct Status
    {
        public uint Mode;
        public long Size;
        public long UnixMilliseconds;
    }

    internal const uint TypeMask = 0xF000, TypeDirectory = 0x4000, TypeFile = 0x8000, TypeLink = 0xA000;

    // stat, or with link: false, lstat (a symbolic link itself, not what it
    // points to). glibc's struct stat puts the mode at 24 on x64 and at 16 on
    // arm64, and the size and modification time at 48 and 88 on both; the
    // other systems' runtime files write arm64's layout.
    internal static FileError Stat(byte* path, bool follow, out Status status)
    {
        Span<byte> buffer = stackalloc byte[256];
        ClearError();
        bool x64 = System == SystemKind.LinuxX64;
        int version = x64 ? 1 : 0; // _STAT_VER
        unsafe
        {
            // SAFETY: path is '\0'-terminated; buffer has room for any of the
            // layouts (144 bytes at most), and the reads are inside it.
            fixed (byte* b = buffer)
            {
                int result = follow ? __xstat64(version, path, b) : __lxstat64(version, path, b);
                if (result != 0)
                {
                    status = default;
                    return LastError();
                }
                status.Mode = *(uint*)(b + (x64 ? 24 : 16));
                status.Size = *(long*)(b + 48);
                status.UnixMilliseconds = *(long*)(b + 88) * 1000 + *(long*)(b + 96) / 1_000_000;
            }
        }
        return FileError.None;
    }

    internal static FileError Stat(ReadOnlySpan<char> path, bool follow, out Status status)
    {
        Span<byte> name = stackalloc byte[PathRoom(path)];
        if (ToC(path, name) < 0)
        {
            status = default;
            return FileError.InvalidPath;
        }
        unsafe
        {
            // SAFETY: ToC '\0'-terminated it.
            fixed (byte* n = name)
                return Stat(n, follow, out status);
        }
    }

    // ---- directory entries ----------------------------------------------------

    // readdir64's struct dirent64: d_type at 18, d_name from 19.
    internal const int EntryType = 18, EntryName = 19;
    internal const byte TypeUnknownEntry = 0, TypeDirectoryEntry = 4, TypeFileEntry = 8, TypeLinkEntry = 10;

    internal static bool IsDotOrDotDot(ReadOnlySpan<byte> name)
        => (name.Length == 1 && name[0] == '.') || (name.Length == 2 && name[0] == '.' && name[1] == '.');
}
