// Example: files (lib/File.cs): whole files in one call, and an open file
// read, written and moved through piece by piece. Writes files.tmp in the
// current directory and deletes it again. "Files --keep <name>" writes that
// file and leaves it.
// Exit code = number of failed checks.
// Build: ./build.sh Files

using System;

namespace FilesSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    const string Path = "files.tmp"; // 8.3, for DOS

    static void Section(string section)
    {
        Console.WriteLine($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
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
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    public static int Main(string[] args)
    {
        // "Files --keep <name>": writes the file and leaves it, so test.sh can
        // check from outside that it has exactly that name (a name the system
        // got wrong would read back fine from in here).
        if (args.Length == 2 && args[0] == "--keep")
            return File.WriteAllText(args[1], "kept\n") ? 0 : 1;

        Arena arena = Arena.Create(64 * 1024);
        WholeFiles(arena);
        OpenFiles(arena);
        Missing(arena);
        return s_failures;
    }

    static void WholeFiles(Arena arena)
    {
        // docs:whole
        File.WriteAllText(Path, "naïve café\n");
        File.AppendAllText(Path, "snow ☃ and a clef 𝄞\n");
        if (File.TryReadAllText(arena, Path, out string text))
        {
            foreach (ReadOnlySpan<char> line in text.Split('\n'))
                if (line.Length > 0)
                    Console.WriteLine(line);
        }
        // docs:end
        Check("read back what was written", text == "naïve café\nsnow ☃ and a clef 𝄞\n");
        Check("Exists", File.Exists(Path));

        Span<byte> bytes;
        bool read;
        unsafe { read = File.TryReadAllBytes(arena, Path, out bytes); } // SAFETY: arena outlives bytes.
        // 38 bytes: ï and é are 2 bytes each in UTF-8, ☃ is 3 and 𝄞 is 4.
        Check("TryReadAllBytes: UTF-8 on disk", read && bytes.Length == 38 && bytes[2] == 0xC3 && bytes[3] == 0xAF);

        Span<byte> raw = stackalloc byte[5];
        raw[0] = 0xFF; // never part of UTF-8
        " ok\n"u8.CopyTo(raw.Slice(1));
        Check("WriteAllBytes", File.WriteAllBytes(Path, raw));
        Check("bytes that aren't UTF-8 read as U+FFFD", File.TryReadAllText(arena, Path, out string bad) && bad == "� ok\n");

        Check("an empty file", File.WriteAllText(Path, "") && File.TryReadAllText(arena, Path, out string empty) && empty.Length == 0);
        Section("whole files");
    }

    static void OpenFiles(Arena arena)
    {
        // docs:open
        File file = File.Create(Path);
        if (!file.IsOpen)
            return;
        file.Write("0123456789");
        file.Write("abcdef"u8);
        Console.WriteLine($"Wrote {file.Length} bytes");

        file.Seek(4);
        file.Write("--");
        unsafe { file.Close(); } // SAFETY: file isn't used after this.

        File input = File.OpenRead(Path);
        Span<byte> chunk = stackalloc byte[6];
        int count;
        while ((count = input.Read(chunk)) > 0)
            Console.WriteLine($"{count} bytes at {input.Position - count}");
        unsafe { input.Close(); } // SAFETY: input isn't used after this.
        // docs:end

        Check("Seek and Write overwrite in place", File.TryReadAllText(arena, Path, out string text) && text == "0123--6789abcdef");

        File again = File.OpenRead(Path);
        Check("Position starts at 0", again.Position == 0);
        Check("Seek past the start of the text", again.Seek(10) && again.Read(chunk) == 6 && chunk[0] == (byte)'a');
        Check("Read at the end gives 0", again.Read(chunk) == 0);
        Check("Length doesn't move the position", again.Length == 16 && again.Position == 16);
        unsafe { again.Close(); } // SAFETY: again isn't used after this.

        File appended = File.OpenAppend(Path);
        Check("OpenAppend writes at the end", appended.Write("!") && appended.Length == 17);
        unsafe { appended.Close(); } // SAFETY: appended isn't used after this.
        Section("open files");
    }

    static void Missing(Arena arena)
    {
        Check("Delete", File.Delete(Path));
        Check("deleted: Exists is false", !File.Exists(Path));
        Check("deleted: can't be deleted again", !File.Delete(Path));
        File missing = File.OpenRead(Path);
        Check("OpenRead of a missing file isn't open", !missing.IsOpen);
        Check("reading a file that isn't open", missing.Read(stackalloc byte[4]) == 0 && missing.Position == -1 && missing.Length == -1);
        Check("TryReadAllText of a missing file", !File.TryReadAllText(arena, Path, out string none) && none == null);
        Check("an empty path", !File.OpenRead("").IsOpen && !File.Exists(""));
        Section("missing files");
    }
}
