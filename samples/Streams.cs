// Example: streams (lib/Streams.cs): System.IO.Stream, MemoryStream and
// FileStream, as .NET has them, with their memory from an arena. Writes
// streams.tmp and append.tmp in the current directory and deletes them again.
// Exit code = number of failed checks.
// Build: ./build.sh Streams

using System;
using System.IO;

namespace StreamsSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    const string Name = "streams.tmp"; // 8.3, for DOS
    const string Log = "append.tmp";

    static void Section(string section)
    {
        Out.Line($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Out.Line($"  failed: {name}");
        }
    }

    static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    // docs:any-stream
    // Code that takes a Stream works with any of them.
    static long CountBytes(Stream stream)
    {
        Span<byte> chunk = stackalloc byte[64];
        long total = 0;
        int n;
        while ((n = stream.Read(chunk)) > 0)
            total += n;
        return total;
    }
    // docs:end

    // The file's bytes, read back with File.
    static bool FileHolds(string path, ReadOnlySpan<byte> expected)
    {
        Arena arena = Arena.Create(4096);
        bool same;
        unsafe
        {
            // SAFETY: bytes isn't used after the arena is destroyed.
            same = File.TryReadAllBytes(arena, path, out Span<byte> bytes) && Same(bytes, expected);
            arena.Destroy();
        }
        return same;
    }

    public static int Main()
    {
        File.Delete(Name);
        File.Delete(Log);

        // docs:memory
        // A MemoryStream grows as it's written; Position moves through it.
        var memory = new MemoryStream();
        memory.Write("hello, "u8);
        memory.Write("world"u8);
        memory.Position = 7;
        Span<byte> word = stackalloc byte[5];
        memory.ReadExactly(word); // "world"
        // docs:end
        Check("read back", Same(word, "world"u8));
        Check("length", memory.Length == 12 && memory.Position == 12);
        Check("ToArray", Same(memory.ToArray(), "hello, world"u8));
        Check("AsSpan", Same(memory.AsSpan(), "hello, world"u8));
        Check("at the end", memory.ReadByte() == -1 && memory.Read(word) == 0);
        memory.Seek(-5, SeekOrigin.End);
        memory.WriteByte((byte)'W');
        Check("WriteByte", Same(memory.AsSpan(), "hello, World"u8));
        memory.Position = 14;
        memory.WriteByte((byte)'!');
        Check("a gap is zeroed", memory.Length == 15 && memory.AsSpan()[12] == 0 && memory.AsSpan()[13] == 0);
        memory.SetLength(5);
        Check("SetLength", Same(memory.AsSpan(), "hello"u8) && memory.Position == 5);
        Check("capacity grew", memory.Capacity >= 15);
        var copy = new MemoryStream();
        memory.WriteTo(copy);
        Check("WriteTo", Same(copy.AsSpan(), "hello"u8));
        memory.Position = 0;
        Span<byte> more = stackalloc byte[8];
        Check("ReadAtLeast at the end", memory.ReadAtLeast(more, 8, throwOnEndOfStream: false) == 5);

        // docs:fixed
        // Over a byte[] you give it, a MemoryStream never grows: memory is
        // bounded, and writing past the end ends the program.
        byte[] room = new byte[16];
        var bounded = new MemoryStream(room);
        bounded.Write("sixteen bytes!!!"u8); // exactly full
        // docs:end
        Check("fixed: full", bounded.Length == 16 && Same(room, "sixteen bytes!!!"u8));
        Check("fixed: a read-only one", !new MemoryStream(room, writable: false).CanWrite);
        Check("fixed: a slice", Same(new MemoryStream(room, 8, 5).ToArray(), "bytes"u8));
        memory.Dispose();
        Check("disposed", !memory.CanRead && !memory.CanWrite);
        Section("MemoryStream");

        // docs:file-stream
        // A FileStream writes through its buffer, and seeks, reads and
        // writes in the same file.
        using (var file = new FileStream(Name, FileMode.Create))
        {
            file.Write("0123456789"u8);
            file.Position = 4;
            file.Write("--"u8);
            file.Seek(0, SeekOrigin.Begin);
            Span<byte> start = stackalloc byte[6];
            file.ReadExactly(start); // "0123--"
        }
        // docs:end
        Check("written", FileHolds(Name, "0123--6789"u8));
        using (var file = new FileStream(Name, FileMode.Open, FileAccess.ReadWrite))
        {
            Check("length", file.Length == 10 && file.Position == 0);
            Span<byte> three = stackalloc byte[3];
            file.ReadExactly(three);
            // A read buffers ahead; a write then goes where the reading got to.
            file.Write("ab"u8);
            Check("position after both", file.Position == 5);
            Check("read on", file.ReadByte() == '-');
            file.Seek(0, SeekOrigin.End);
            file.WriteByte((byte)'!');
            Check("length grew", file.Length == 11);
            Check("can all", file.CanRead && file.CanWrite && file.CanSeek);
        }
        Check("mixed", FileHolds(Name, "012ab-6789!"u8));
        using (var file = new FileStream(Name, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0))
        {
            Check("unbuffered", CountBytes(file) == 11 && !file.CanWrite);
        }
        // A write bigger than the buffer goes straight to the file.
        byte[] big = new byte[10_000];
        for (int i = 0; i < big.Length; i++)
            big[i] = (byte)i;
        using (var file = new FileStream(Name, FileMode.Truncate, FileAccess.Write, FileShare.None, bufferSize: 64))
        {
            file.Write("start"u8);
            file.Write(big);
            file.Write("end"u8);
        }
        using (var file = new FileStream(Name, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64))
        {
            Check("big", file.Length == 10_008);
            var back = new MemoryStream();
            file.CopyTo(back);
            Check("CopyTo", back.Length == 10_008 && Same(back.AsSpan().Slice(5, 10_000), big));
        }
        Section("FileStream");

        // docs:modes
        // Append writes at the end; CreateNew won't replace a file. TryOpen
        // says why a file wouldn't open, rather than ending the program.
        using (var log = new FileStream(Log, FileMode.Append))
            log.Write("one\n"u8);
        using (var log = new FileStream(Log, FileMode.Append))
            log.Write("two\n"u8);
        bool opened = FileStream.TryOpen(Log, FileMode.CreateNew,
            FileAccess.Write, out FileStream stream, out FileError error);
        // opened is false, and error is FileError.AlreadyExists
        // docs:end
        Check("appended", FileHolds(Log, "one\ntwo\n"u8));
        Check("CreateNew refused", !opened && stream == null && error != FileError.None);
        Check("missing", !FileStream.TryOpen("none.tmp", FileMode.Open, FileAccess.Read, out _, out error) && error != FileError.None);
        File.Delete(Log);
        using (var made = new FileStream(Log, FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            Check("OpenOrCreate made it", made.Length == 0);
            made.Write("x"u8);
        }
        using (var kept = new FileStream(Log, FileMode.OpenOrCreate, FileAccess.Read))
            Check("OpenOrCreate kept it", kept.Length == 1);
        using (var appending = new FileStream(Log, FileMode.Append))
            Check("append starts at the end, and can seek", appending.CanSeek && appending.Position == appending.Length && appending.Length == 1);
        Section("File modes");

        // docs:arena
        // Inside an arena's AllocateObjectsHere scope a stream, and its
        // buffer, come from the arena. Dispose the stream before the arena
        // goes: what's still buffered is written then.
        Arena arena = Arena.Create(16 * 1024);
        nuint before = arena.Used;
        unsafe
        {
            // SAFETY: the stream is disposed before the arena is destroyed.
            using (arena.AllocateObjectsHere())
            using (var file = new FileStream(Name, FileMode.Create,
                FileAccess.Write, FileShare.None, bufferSize: 8192))
            {
                file.Write("from the arena"u8);
            }
        }
        nuint used = arena.Used - before; // over 8192: the buffer
        unsafe { arena.Destroy(); } // SAFETY: nothing from it is used after this
        // docs:end
        Check("the buffer came from the arena", used >= 8192 && used < 9000);
        Check("written out on Dispose", FileHolds(Name, "from the arena"u8));
        Section("Arena");

        Check("Null reads nothing", CountBytes(Stream.Null) == 0);
        Stream.Null.Write("gone"u8);
        Check("any stream", CountBytes(new MemoryStream(room)) == 16);
        File.Delete(Name);
        File.Delete(Log);
        Check("cleaned up", !File.Exists(Name) && !File.Exists(Log));
        Section("Stream");

        return s_failures;
    }
}
