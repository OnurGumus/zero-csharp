// Paths: the parts of a path, joining paths, and tidying one up. All of it
// works on the text alone, on every system, without touching the disk (except
// GetFullPath, which asks for the current directory).
//
//   Path.GetFileName("logs/today.txt")          "today.txt"
//   Path.GetExtension("logs/today.txt")         ".txt"
//   Path.GetDirectoryName("logs/today.txt")     "logs"
//   Path.Combine("logs", "today.txt")           "logs/today.txt" ("logs\today.txt" on Windows)
//   Path.Normalize("logs/./old/../today.txt")   "logs/today.txt"
//
// '/' separates names everywhere; on Windows, DOS and UEFI so does '\', which
// is also what Combine and Normalize write there. The functions that pick out
// a part of a path return a slice of it and allocate nothing; the ones that
// make a new path return a string, and have a Try form that writes into a
// span instead.

using System;
using System.Runtime.InteropServices;

/// <summary>Functions on paths, as text: their parts, joining them, and tidying them up.</summary>
public static class Path
{
    /// <summary>The separator Combine and Normalize write: '\' on Windows, DOS and UEFI, '/' elsewhere.</summary>
    public static char Separator => FileSystem.Backslashes ? '\\' : '/';

    /// <summary>.NET's name for <see cref="Separator"/>.</summary>
    public static char DirectorySeparatorChar => Separator;

    /// <summary>The other separator accepted: '/' on Windows, DOS and UEFI, and '/' elsewhere too, as in .NET.</summary>
    public static char AltDirectorySeparatorChar => '/';

    /// <summary>Whether <paramref name="c"/> separates names: '/', and on Windows, DOS and UEFI also '\'.</summary>
    public static bool IsSeparator(char c) => c == '/' || (c == '\\' && FileSystem.Backslashes);

    /// <summary>
    /// Whether the path starts from a root rather than the current directory:
    /// "/x", and on Windows, DOS and UEFI also "\x", "C:x", "C:\x" and "\\server\share".
    /// </summary>
    public static bool IsRooted(ReadOnlySpan<char> path) => RootLength(path) > 0;

    /// <summary>
    /// The root the path starts with ("/", "C:\", "\\server\share\"), or an
    /// empty span for a relative path.
    /// </summary>
    public static ReadOnlySpan<char> GetRoot(ReadOnlySpan<char> path) => path.Slice(0, RootLength(path));

    /// <summary>The last name in the path: "c.txt" in "a/b/c.txt". Empty if the path ends with a separator.</summary>
    public static ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path)
    {
        int start = RootLength(path);
        for (int i = path.Length - 1; i >= start; i--)
            if (IsSeparator(path[i]))
                return path.Slice(i + 1);
        return path.Slice(start);
    }

    /// <summary>
    /// The file name without its extension: "c" in "a/b/c.txt", "archive.tar"
    /// in "archive.tar.gz", and "" in ".profile".
    /// </summary>
    public static ReadOnlySpan<char> GetFileNameWithoutExtension(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<char> name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Slice(0, dot);
    }

    /// <summary>
    /// The extension, with its dot: ".txt" in "a/b/c.txt", ".gz" in
    /// "archive.tar.gz". Empty when the file name has no dot, or ends with one.
    /// </summary>
    public static ReadOnlySpan<char> GetExtension(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<char> name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? ReadOnlySpan<char>.Empty : name.Slice(dot);
    }

    /// <summary>Whether the file name has an extension, as GetExtension finds it.</summary>
    public static bool HasExtension(ReadOnlySpan<char> path) => !GetExtension(path).IsEmpty;

    /// <summary>
    /// The path without its last name: "a/b" for "a/b/c.txt", "/" for "/a".
    /// Empty for a name on its own ("c.txt") and for a root ("/", "C:\").
    /// </summary>
    public static ReadOnlySpan<char> GetDirectoryName(ReadOnlySpan<char> path)
    {
        int root = RootLength(path);
        int i = path.Length - 1;
        while (i >= root && !IsSeparator(path[i]))
            i--;
        if (i < root)
            return path.Length > root ? path.Slice(0, root) : ReadOnlySpan<char>.Empty;
        // Drop the separators before the last name, but not the root's.
        while (i > root && IsSeparator(path[i - 1]))
            i--;
        return path.Slice(0, i);
    }

    /// <summary>
    /// <paramref name="second"/> after <paramref name="first"/>, with a
    /// separator between them if <paramref name="first"/> doesn't end with one.
    /// If <paramref name="second"/> is rooted it's the answer on its own, and an
    /// empty path adds nothing.
    /// </summary>
    public static string Combine(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        int length = CombinedLength(first, second);
        string result = string.Allocate(length);
        Span<char> chars;
        unsafe { chars = MemoryMarshal.CreateSpan(ref result.FirstChar, length); } // SAFETY: the string is new, nothing else has it yet, and the span covers its length chars.
        TryCombine(first, second, chars, out _);
        return result;
    }

    /// <summary>The three paths combined, as Combine(Combine(first, second), third).</summary>
    public static string Combine(ReadOnlySpan<char> first, ReadOnlySpan<char> second, ReadOnlySpan<char> third)
    {
        int length = CombinedLength(first, second);
        Span<char> buffer = stackalloc char[512];
        scoped Span<char> both;
        if (length <= buffer.Length)
            both = buffer.Slice(0, length);
        else
            unsafe { both = MemoryMarshal.CreateSpan(ref string.Allocate(length).FirstChar, length); } // SAFETY: a new string, used only as a buffer here.
        TryCombine(first, second, both, out _);
        return Combine(both, third);
    }

    /// <summary>
    /// Combine, into <paramref name="destination"/>. False, with nothing
    /// written, if it doesn't fit.
    /// </summary>
    public static bool TryCombine(ReadOnlySpan<char> first, ReadOnlySpan<char> second, Span<char> destination, out int written)
    {
        written = 0;
        int length = CombinedLength(first, second);
        if (length > destination.Length)
            return false;
        if (second.IsEmpty || IsRooted(second))
        {
            (second.IsEmpty ? first : second).CopyTo(destination);
        }
        else if (first.IsEmpty)
        {
            second.CopyTo(destination);
        }
        else
        {
            first.CopyTo(destination);
            int at = first.Length;
            if (NeedsSeparator(first))
                destination[at++] = Separator;
            second.CopyTo(destination.Slice(at));
        }
        written = length;
        return true;
    }

    /// <summary>
    /// The path with its extension replaced: ChangeExtension("a/b.txt", ".md")
    /// is "a/b.md". The dot is added if <paramref name="extension"/> has none,
    /// and an empty <paramref name="extension"/> removes the extension.
    /// </summary>
    public static string ChangeExtension(ReadOnlySpan<char> path, ReadOnlySpan<char> extension)
    {
        ReadOnlySpan<char> name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        int keep = dot < 0 ? path.Length : path.Length - name.Length + dot;
        bool addDot = !extension.IsEmpty && extension[0] != '.';
        int length = keep + (addDot ? 1 : 0) + extension.Length;
        string result = string.Allocate(length);
        Span<char> chars;
        unsafe { chars = MemoryMarshal.CreateSpan(ref result.FirstChar, length); } // SAFETY: the string is new, nothing else has it yet, and the span covers its length chars.
        path.Slice(0, keep).CopyTo(chars);
        if (addDot)
            chars[keep] = '.';
        extension.CopyTo(chars.Slice(keep + (addDot ? 1 : 0)));
        return result;
    }

    /// <summary>
    /// The path tidied up, from the text alone: separators become Separator,
    /// repeated ones become one, "." names go, and each ".." takes away the name
    /// before it. A relative path keeps the ".." it starts with; a rooted one
    /// can't go above its root. A trailing separator goes, and a path that ends
    /// up empty is ".". Symbolic links aren't looked at, so "link/.." may not be
    /// where the system would go.
    /// </summary>
    public static string Normalize(ReadOnlySpan<char> path)
    {
        // Never longer than the path, except that "" becomes ".".
        int capacity = path.Length + 1;
        Span<char> buffer = stackalloc char[512];
        scoped Span<char> chars;
        if (capacity <= buffer.Length)
            chars = buffer.Slice(0, capacity);
        else
            unsafe { chars = MemoryMarshal.CreateSpan(ref string.Allocate(capacity).FirstChar, capacity); } // SAFETY: a new string, used only as a buffer here.
        TryNormalize(path, chars, out int written);
        return string.FromChars(chars.Slice(0, written));
    }

    /// <summary>
    /// Normalize, into <paramref name="destination"/>. False, with nothing
    /// written, if it doesn't fit (path.Length + 1 chars always do).
    /// </summary>
    public static bool TryNormalize(ReadOnlySpan<char> path, Span<char> destination, out int written)
    {
        written = 0;
        int root = RootLength(path);
        if (destination.Length < Math.Max(path.Length, 1))
            return false;
        char separator = Separator;
        int at = 0;
        for (int i = 0; i < root; i++)
            destination[at++] = IsSeparator(path[i]) ? separator : path[i];
        int start = at; // what ".." can't remove
        int i0 = root;
        while (i0 < path.Length)
        {
            int end = i0;
            while (end < path.Length && !IsSeparator(path[end]))
                end++;
            ReadOnlySpan<char> name = path.Slice(i0, end - i0);
            i0 = end + 1;
            if (name.IsEmpty || (name.Length == 1 && name[0] == '.'))
                continue;
            if (name.Length == 2 && name[0] == '.' && name[1] == '.')
            {
                // Remove the last name written, unless there is none or it's "..".
                int last = at;
                while (last > start && destination[last - 1] != separator)
                    last--;
                bool lastIsDotDot = at - last == 2 && destination[last] == '.' && destination[last + 1] == '.';
                if (at > start && !lastIsDotDot)
                {
                    at = last > start ? last - 1 : start;
                    continue;
                }
                if (root > 0)
                    continue; // above the root is the root
            }
            if (at > start)
                destination[at++] = separator;
            name.CopyTo(destination.Slice(at));
            at += name.Length;
        }
        if (at == 0)
            destination[at++] = '.';
        written = at;
        return true;
    }

    /// <summary>
    /// The path made absolute: combined with the current directory unless it's
    /// rooted, then normalized. Linux, macOS, Windows and WebAssembly; on
    /// Windows a path rooted only by its drive ("C:x") or a '\' ("\x") is
    /// normalized as it is.
    /// </summary>
    public static string GetFullPath(ReadOnlySpan<char> path)
    {
        if (IsRooted(path))
            return Normalize(path);
        return Normalize(Combine(Directory.GetCurrentDirectory(), path));
    }

    // ---- helpers --------------------------------------------------------------

    static int CombinedLength(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        if (second.IsEmpty)
            return first.Length;
        if (first.IsEmpty || IsRooted(second))
            return second.Length;
        return first.Length + (NeedsSeparator(first) ? 1 : 0) + second.Length;
    }

    // Whether a separator goes between first and what follows: not after one,
    // and not after a drive ("C:" + "x" is "C:x", on that drive's current directory).
    static bool NeedsSeparator(ReadOnlySpan<char> first)
    {
        char last = first[first.Length - 1];
        if (IsSeparator(last))
            return false;
        return !(last == ':' && FileSystem.Backslashes && first.Length == 2);
    }

    // How many chars at the start of the path make its root: "/" on Unix;
    // "C:", "C:\", "\" or "\\server\share\" on Windows, DOS and UEFI.
    static int RootLength(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
            return 0;
        if (!FileSystem.Backslashes)
            return path[0] == '/' ? 1 : 0;
        if (path.Length >= 2 && path[1] == ':' && IsLetter(path[0]))
            return path.Length >= 3 && IsSeparator(path[2]) ? 3 : 2;
        if (!IsSeparator(path[0]))
            return 0;
        if (path.Length < 2 || !IsSeparator(path[1]))
            return 1;
        // \\server\share\: through the separator after the share's name.
        int i = 2;
        for (int names = 0; names < 2 && i < path.Length; names++)
        {
            while (i < path.Length && !IsSeparator(path[i]))
                i++;
            if (i < path.Length)
                i++;
        }
        return i;
    }

    static bool IsLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
}
