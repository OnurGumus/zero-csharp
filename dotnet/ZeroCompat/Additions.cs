// What lib/ adds to .NET's own types, on real .NET's: C# 14 extension
// members, since these types are the BCL's here.
using System;
using System.IO;

public static class ZeroMemoryExtensions
{
    // lib/Str.cs: zerocore's string has these as extensions.
    public static bool SequenceEqual(this string text, ReadOnlySpan<char> other) => text.AsSpan().SequenceEqual(other);
    public static bool Contains(this ReadOnlySpan<char> span, ReadOnlySpan<char> value) => span.IndexOf(value) >= 0;
}

#if ZERO_DOTNET_BCL
// In lib/ these are members of the types themselves, found without a using:
// so these are in the global namespace.
public static class ZeroStreamExtensions
{
    extension(MemoryStream stream)
    {
        /// <summary>lib/Streams.cs: the stream's bytes, without a copy.</summary>
        public ReadOnlySpan<byte> AsSpan()
            => stream.TryGetBuffer(out ArraySegment<byte> buffer) ? buffer.AsSpan(0, (int)stream.Length) : stream.ToArray();
    }

    extension(FileStream)
    {
        /// <summary>lib/Streams.cs: opens the file, or says why it couldn't.</summary>
        public static bool TryOpen(string path, FileMode mode, FileAccess access, out FileStream stream, out FileError error, int bufferSize = 4096)
        {
            try
            {
                stream = new FileStream(path, mode, access, FileShare.ReadWrite, bufferSize);
                error = FileError.None;
                return true;
            }
            catch (Exception e)
            {
                stream = null;
                error = e switch
                {
                    FileNotFoundException or DirectoryNotFoundException => FileError.NotFound,
                    UnauthorizedAccessException when System.IO.Directory.Exists(path) => FileError.IsADirectory,
                    UnauthorizedAccessException => FileError.AccessDenied,
                    IOException when mode == FileMode.CreateNew && System.IO.File.Exists(path) => FileError.AlreadyExists,
                    PathTooLongException or ArgumentException => FileError.InvalidPath,
                    _ => FileError.Other,
                };
                return false;
            }
        }
    }
}
#endif

namespace System.Security.Cryptography
{
    static class ZeroCryptoExtensions
    {
        extension(ChaCha20Poly1305 cipher)
        {
            // lib/CryptoChaCha.cs: Decrypt that says false for a wrong tag,
            // for lib/EncryptedStream.cs.
            internal bool TryDecrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
                Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
            {
                try
                {
                    cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
                    return true;
                }
                catch (AuthenticationTagMismatchException)
                {
                    return false;
                }
            }
        }
    }
}


#if ZERO_DOTNET_BCL
// lib/Catch.cs calls these on lib/'s own FileStream, Socket and Task.
static class ZeroCatchExtensions
{
    // Catch.Keep: lib/'s stream has a record with Catch.Run to take off its
    // list. The BCL's has none: nothing closes it after a throw but its own
    // Dispose (or the GC).
    internal static void KeepOpen(this System.IO.FileStream stream) { }
    internal static void KeepOpen(this System.Net.Sockets.Socket socket) { }

    extension(System.Threading.Tasks.Task task)
    {
        // lib/Tasks.cs: what a faulted or canceled task ended with, or null.
        internal Exception Failure => task.IsFaulted ? task.Exception.InnerException
            : task.IsCanceled ? new System.Threading.Tasks.TaskCanceledException(task) : null;
    }
}
#endif
