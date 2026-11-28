// Streams: .NET's System.IO.Stream, and the streams most programs use, so
// code written against them compiles here.
//
//   using var file = new FileStream("log.txt", FileMode.Append, FileAccess.Write);
//   file.Write("started\n"u8);
//
//   var memory = new MemoryStream();
//   file.CopyTo(memory);
//
// What differs from .NET:
// - A throw ends the program (as everywhere in zero mode) unless a Catch.Run
//   is waiting for it (Catch.cs), so what .NET would throw for (reading a
//   stream that can't read, a failed write, a file that won't open) stops
//   the program with the message, or comes back to that Catch.Run. To open
//   a file that may not be there, FileStream.TryOpen returns a FileError
//   instead.
// - The async methods (ReadAsync, WriteAsync, CopyToAsync...) of Stream
//   itself call the blocking ones, so a MemoryStream's finish at once, and in
//   a fiber Pipe.AsStream's (lib/Fibers.cs) let the other fibers run while
//   they wait. FileStream's run on another thread (lib/FileAsync.cs), and
//   NetworkStream's wait with no fiber at all (lib/SocketsAsync.cs).
// - Memory is bounded and comes from where objects come from: a FileStream's
//   buffer is a byte[] of the size you give it (4096 by default; the C
//   library's own buffering is off), and a MemoryStream's bytes are a byte[].
//   Inside an arena's AllocateObjectsHere scope both come from the arena, and
//   a MemoryStream made over a byte[] you pass can never grow past it.
// - Dispose a FileStream (using): its buffer is written out then. One that
//   isn't disposed loses what's still in its buffer, as with any buffer
//   that's never flushed.
// - FileStream.SetLength isn't there yet.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO;

/// <summary>Where Seek counts from.</summary>
public enum SeekOrigin
{
    /// <summary>The start of the stream.</summary>
    Begin = 0,
    /// <summary>The current position.</summary>
    Current = 1,
    /// <summary>The end of the stream.</summary>
    End = 2,
}

/// <summary>How FileStream opens a file: whether it must exist, may be created, or is emptied.</summary>
public enum FileMode
{
    /// <summary>Create a new file; fail if one exists.</summary>
    CreateNew = 1,
    /// <summary>Create the file, emptying it if it exists.</summary>
    Create = 2,
    /// <summary>Open a file that must exist.</summary>
    Open = 3,
    /// <summary>Open the file, creating it if it doesn't exist.</summary>
    OpenOrCreate = 4,
    /// <summary>Open a file that must exist, and empty it.</summary>
    Truncate = 5,
    /// <summary>Open or create the file, and write at its end. Only with FileAccess.Write.</summary>
    Append = 6,
}

/// <summary>Whether a FileStream reads, writes, or both.</summary>
[Flags]
public enum FileAccess
{
    /// <summary>Read only.</summary>
    Read = 1,
    /// <summary>Write only.</summary>
    Write = 2,
    /// <summary>Read and write.</summary>
    ReadWrite = 3,
}

/// <summary>What other programs may do with an open file. Accepted for .NET's sake, and not enforced.</summary>
[Flags]
public enum FileShare
{
    /// <summary>Nothing.</summary>
    None = 0,
    /// <summary>Read it.</summary>
    Read = 1,
    /// <summary>Write it.</summary>
    Write = 2,
    /// <summary>Read and write it.</summary>
    ReadWrite = 3,
    /// <summary>Delete it.</summary>
    Delete = 4,
}

/// <summary>An input or output failure. Thrown, it ends the program with its message, unless a Catch.Run is waiting for it.</summary>
public class IOException : Exception
{
    /// <summary>An IOException with the default message.</summary>
    public IOException() : base("IOException") { }
    /// <summary>An IOException with this message.</summary>
    public IOException(string message) : base(message) { }
}

/// <summary>Reading past the end of a stream that had to have more.</summary>
public class EndOfStreamException : IOException
{
    /// <summary>An EndOfStreamException with the default message.</summary>
    public EndOfStreamException() : base("Unable to read beyond the end of the stream.") { }
    /// <summary>An EndOfStreamException with this message.</summary>
    public EndOfStreamException(string message) : base(message) { }
}

/// <summary>
/// A sequence of bytes to read, write or move through, as in .NET. A
/// subclass overrides the byte[] Read and Write at least; the span ones go
/// through them unless overridden too.
/// </summary>
public abstract class Stream : IDisposable, IAsyncDisposable
{
    byte[] _chunk; // for the span Read and Write of a subclass that has only the byte[] ones

    /// <summary>A stream that reads nothing and throws writes away.</summary>
    public static Stream Null => NullHolder.Value;

    // Made by a static constructor, so it's allocated outside any object scope (lib/Arena.cs).
    static class NullHolder { internal static readonly Stream Value = new NullStream(); }

    /// <summary>Whether Read works.</summary>
    public abstract bool CanRead { get; }
    /// <summary>Whether Seek, Position and Length work.</summary>
    public abstract bool CanSeek { get; }
    /// <summary>Whether Write works.</summary>
    public abstract bool CanWrite { get; }
    /// <summary>The stream's length in bytes.</summary>
    public abstract long Length { get; }
    /// <summary>Where the next read or write happens.</summary>
    public abstract long Position { get; set; }

    /// <summary>Writes out whatever is buffered.</summary>
    public abstract void Flush();
    /// <summary>Reads up to <paramref name="count"/> bytes into buffer from offset; returns how many, 0 at the end.</summary>
    public abstract int Read(byte[] buffer, int offset, int count);
    /// <summary>Moves the position, and returns it.</summary>
    public abstract long Seek(long offset, SeekOrigin origin);
    /// <summary>Makes the stream this long.</summary>
    public abstract void SetLength(long value);
    /// <summary>Writes <paramref name="count"/> bytes of buffer from offset.</summary>
    public abstract void Write(byte[] buffer, int offset, int count);

    /// <summary>Reads up to buffer.Length bytes; returns how many, 0 at the end.</summary>
    public virtual int Read(Span<byte> buffer)
    {
        byte[] chunk = Chunk();
        int n = Read(chunk, 0, Math.Min(buffer.Length, chunk.Length));
        new ReadOnlySpan<byte>(chunk, 0, n).CopyTo(buffer);
        return n;
    }

    /// <summary>Writes all of buffer.</summary>
    public virtual void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] chunk = Chunk();
        while (!buffer.IsEmpty)
        {
            int n = Math.Min(buffer.Length, chunk.Length);
            buffer.Slice(0, n).CopyTo(chunk);
            Write(chunk, 0, n);
            buffer = buffer.Slice(n);
        }
    }

    /// <summary>Reads one byte; -1 at the end.</summary>
    public virtual int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    /// <summary>Writes one byte.</summary>
    public virtual void WriteByte(byte value)
    {
        Span<byte> one = stackalloc byte[1];
        one[0] = value;
        Write(one);
    }

    /// <summary>Reads until buffer is full; reaching the end first ends the program (EndOfStreamException).</summary>
    public void ReadExactly(Span<byte> buffer) => ReadAtLeast(buffer, buffer.Length, true);

    /// <summary>Reads exactly <paramref name="count"/> bytes into buffer from offset.</summary>
    public void ReadExactly(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ReadAtLeast(new Span<byte>(buffer, offset, count), count, true);
    }

    /// <summary>
    /// Reads until at least <paramref name="minimumBytes"/> have come, and
    /// returns how many did (up to buffer.Length). At the end before that:
    /// with <paramref name="throwOnEndOfStream"/>, ends the program;
    /// without, returns what came.
    /// </summary>
    public int ReadAtLeast(Span<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true)
    {
        if (minimumBytes < 0 || minimumBytes > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(minimumBytes));
        int total = 0;
        while (total < minimumBytes)
        {
            int n = Read(buffer.Slice(total));
            if (n == 0)
            {
                if (throwOnEndOfStream)
                    throw new EndOfStreamException();
                break;
            }
            total += n;
        }
        return total;
    }

    /// <summary>Reads the rest of this stream and writes it to <paramref name="destination"/>.</summary>
    public void CopyTo(Stream destination) => CopyTo(destination, 512);

    /// <summary>
    /// Reads the rest of this stream and writes it to
    /// <paramref name="destination"/>, in pieces of up to
    /// <paramref name="bufferSize"/> bytes (at most 512, on the stack, so
    /// it fits a fiber's stack on a microcontroller).
    /// </summary>
    public virtual void CopyTo(Stream destination, int bufferSize)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        Span<byte> chunk = stackalloc byte[512];
        chunk = chunk.Slice(0, Math.Min(bufferSize, chunk.Length));
        int n;
        while ((n = Read(chunk)) > 0)
            destination.Write(chunk.Slice(0, n));
    }

    // ---- timeouts ---------------------------------------------------------

    /// <summary>Whether ReadTimeout and WriteTimeout work: false unless a subclass says so (NetworkStream).</summary>
    public virtual bool CanTimeout => false;

    /// <summary>Milliseconds a read waits before giving up. Ends the program on a stream that can't time out.</summary>
    public virtual int ReadTimeout
    {
        get => throw new InvalidOperationException("Timeouts are not supported on this stream.");
        set => throw new InvalidOperationException("Timeouts are not supported on this stream.");
    }

    /// <summary>Milliseconds a write waits before giving up. Ends the program on a stream that can't time out.</summary>
    public virtual int WriteTimeout
    {
        get => throw new InvalidOperationException("Timeouts are not supported on this stream.");
        set => throw new InvalidOperationException("Timeouts are not supported on this stream.");
    }

    // ---- async ------------------------------------------------------------
    // The base methods call the blocking ones and return a completed task,
    // as .NET's would after its thread pool ran them: a file read blocks the
    // thread, as Read does. In a fiber a blocking call lets the others run
    // (Pipe, sockets), so these are safe, if not free, in an async method.
    // NetworkStream overrides them with waits that need no fiber.

    /// <summary>Reads up to buffer.Length bytes; the task's result is how many, 0 at the end.</summary>
    public virtual ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<int>(cancellationToken);
        return new ValueTask<int>(Read(buffer.Span));
    }

    /// <summary>Reads up to <paramref name="count"/> bytes into buffer from offset.</summary>
    public Task<int> ReadAsync(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default);

    /// <summary>Reads up to <paramref name="count"/> bytes into buffer from offset.</summary>
    public virtual Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    /// <summary>Writes all of buffer.</summary>
    public virtual ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        Write(buffer.Span);
        return default;
    }

    /// <summary>Writes <paramref name="count"/> bytes of buffer from offset.</summary>
    public Task WriteAsync(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default);

    /// <summary>Writes <paramref name="count"/> bytes of buffer from offset.</summary>
    public virtual Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    /// <summary>Flush, as a task.</summary>
    public Task FlushAsync() => FlushAsync(default);

    /// <summary>Flush, as a task.</summary>
    public virtual Task FlushAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        Flush();
        return Task.CompletedTask;
    }

    /// <summary>Reads until buffer is full; reaching the end first ends the program (EndOfStreamException).</summary>
    public async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => await ReadAtLeastAsync(buffer, buffer.Length, true, cancellationToken);

    /// <summary>Reads exactly <paramref name="count"/> bytes into buffer from offset.</summary>
    public ValueTask ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadExactlyAsync(new Memory<byte>(buffer, offset, count), cancellationToken);
    }

    /// <summary>ReadAtLeast, without blocking.</summary>
    public async ValueTask<int> ReadAtLeastAsync(Memory<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true, CancellationToken cancellationToken = default)
    {
        if (minimumBytes < 0 || minimumBytes > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(minimumBytes));
        int total = 0;
        while (total < minimumBytes)
        {
            int n = await ReadAsync(buffer.Slice(total), cancellationToken);
            if (n == 0)
            {
                if (throwOnEndOfStream)
                    throw new EndOfStreamException();
                break;
            }
            total += n;
        }
        return total;
    }

    /// <summary>Reads the rest of this stream and writes it to <paramref name="destination"/>.</summary>
    public Task CopyToAsync(Stream destination) => CopyToAsync(destination, 4096, default);

    /// <summary>CopyToAsync, in pieces of up to <paramref name="bufferSize"/> bytes.</summary>
    public Task CopyToAsync(Stream destination, int bufferSize) => CopyToAsync(destination, bufferSize, default);

    /// <summary>CopyToAsync, until <paramref name="cancellationToken"/> is canceled.</summary>
    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken) => CopyToAsync(destination, 4096, cancellationToken);

    /// <summary>
    /// Reads the rest of this stream and writes it to
    /// <paramref name="destination"/>, in pieces of up to
    /// <paramref name="bufferSize"/> bytes, through a buffer on the heap (an
    /// async method can't keep one on the stack).
    /// </summary>
    public virtual async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        byte[] buffer = new byte[bufferSize];
        int n;
        while ((n = await ReadAsync(buffer, cancellationToken)) > 0)
            await destination.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, n), cancellationToken);
    }

    /// <summary>Dispose, as a task.</summary>
    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    /// <summary>Flushes and closes the stream (Dispose does the same).</summary>
    public virtual void Close() => Dispose(true);

    /// <summary>Flushes and closes the stream.</summary>
    public void Dispose() => Close();

    /// <summary>What closing does; a subclass overrides it to release what it holds.</summary>
    protected virtual void Dispose(bool disposing) { }

    /// <summary>Ends the program if buffer, offset and count don't describe bytes inside buffer.</summary>
    protected static void ValidateBufferArguments(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if ((uint)count > (uint)(buffer.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(count));
    }

    byte[] Chunk() => _chunk ??= new byte[1024];

    sealed class NullStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override int Read(Span<byte> buffer) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Write(ReadOnlySpan<byte> buffer) { }
    }
}

/// <summary>
/// A stream over bytes in memory: a byte[] it grows as you write, or one you
/// give it, which it never grows past.
/// </summary>
public class MemoryStream : Stream
{
    byte[] _buffer;
    readonly int _origin;  // where the stream starts in _buffer
    int _position;         // in _buffer
    int _length;           // the end, in _buffer
    int _capacity;         // the end of the room, in _buffer
    readonly bool _expandable, _writable, _exposable;
    bool _open = true;

    /// <summary>An empty stream that grows as it's written.</summary>
    public MemoryStream() : this(0) { }

    /// <summary>An empty stream with room for <paramref name="capacity"/> bytes, which grows past it as needed.</summary>
    public MemoryStream(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = capacity == 0 ? [] : new byte[capacity];
        _capacity = capacity;
        _expandable = _writable = _exposable = true;
    }

    /// <summary>A stream over all of buffer, which can't grow: writing past its end ends the program.</summary>
    public MemoryStream(byte[] buffer) : this(buffer, true) { }

    /// <summary>A stream over all of buffer, writable or not.</summary>
    public MemoryStream(byte[] buffer, bool writable)
        : this(buffer, 0, buffer == null ? 0 : buffer.Length, writable) { }

    /// <summary>A stream over <paramref name="count"/> bytes of buffer from index.</summary>
    public MemoryStream(byte[] buffer, int index, int count, bool writable = true, bool publiclyVisible = false)
    {
        ValidateBufferArguments(buffer, index, count);
        _buffer = buffer;
        _origin = _position = index;
        _length = _capacity = index + count;
        _writable = writable;
        _exposable = publiclyVisible;
    }

    /// <inheritdoc/>
    public override bool CanRead => _open;
    /// <inheritdoc/>
    public override bool CanSeek => _open;
    /// <inheritdoc/>
    public override bool CanWrite => _open && _writable;
    /// <inheritdoc/>
    public override long Length { get { EnsureOpen(); return _length - _origin; } }

    /// <inheritdoc/>
    public override long Position
    {
        get { EnsureOpen(); return _position - _origin; }
        set
        {
            EnsureOpen();
            if (value < 0 || value > int.MaxValue - _origin)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = _origin + (int)value;
        }
    }

    /// <summary>How many bytes the stream has room for before it must grow.</summary>
    public virtual int Capacity
    {
        get { EnsureOpen(); return _capacity - _origin; }
        set
        {
            EnsureOpen();
            if (value < Length)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (value == _capacity - _origin)
                return;
            if (!_expandable)
                throw new NotSupportedException("MemoryStream: this stream's buffer can't grow");
            byte[] bigger = value == 0 ? [] : new byte[value];
            new ReadOnlySpan<byte>(_buffer, 0, _length).CopyTo(bigger);
            _buffer = bigger;
            _capacity = value;
        }
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(new Span<byte>(buffer, offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        EnsureOpen();
        int n = Math.Min(buffer.Length, _length - _position);
        if (n <= 0)
            return 0;
        new ReadOnlySpan<byte>(_buffer, _position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        EnsureOpen();
        return _position < _length ? _buffer[_position++] : -1;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        long end = (long)_position + buffer.Length;
        if (end > int.MaxValue)
            throw new IOException("MemoryStream: longer than 2 GB");
        Grow((int)end);
        buffer.CopyTo(new Span<byte>(_buffer, _position, buffer.Length));
        _position = (int)end;
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        EnsureWritable();
        Grow(_position + 1);
        _buffer[_position++] = value;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureOpen();
        long from = origin switch
        {
            SeekOrigin.Begin => _origin,
            SeekOrigin.Current => _position,
            SeekOrigin.End => _length,
            _ => throw new ArgumentException(nameof(origin)),
        };
        long target = from + offset;
        if (target < _origin || target > int.MaxValue)
            throw new IOException("MemoryStream: seek before the start");
        _position = (int)target;
        return target - _origin;
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        EnsureWritable();
        if (value < 0 || value > int.MaxValue - _origin)
            throw new ArgumentOutOfRangeException(nameof(value));
        int end = _origin + (int)value;
        if (end > _length)
            Grow(end);
        else
            _length = end;
        if (_position > end)
            _position = end;
    }

    /// <summary>A copy of the stream's bytes, in a new byte[].</summary>
    public virtual byte[] ToArray()
    {
        int length = _length - _origin;
        byte[] copy = length == 0 ? [] : new byte[length];
        new ReadOnlySpan<byte>(_buffer, _origin, length).CopyTo(copy);
        return copy;
    }

    /// <summary>
    /// The byte[] underneath, which may be longer than the stream: only for
    /// a stream that made its own buffer, or was given one with publiclyVisible.
    /// </summary>
    public virtual byte[] GetBuffer()
    {
        if (!_exposable)
            throw new InvalidOperationException("MemoryStream: this stream's buffer isn't publicly visible");
        return _buffer;
    }

    /// <summary>The stream's bytes, without copying them. Good until the stream next grows.</summary>
    public ReadOnlySpan<byte> AsSpan() => new ReadOnlySpan<byte>(_buffer, _origin, _length - _origin);

    /// <summary>Writes the stream's bytes, all of them, to <paramref name="stream"/>.</summary>
    public virtual void WriteTo(Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));
        EnsureOpen();
        stream.Write(AsSpan());
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing) => _open = false;

    // Makes the stream at least `end` long, growing the buffer if it may,
    // and zeroing any gap left by a seek past the end.
    void Grow(int end)
    {
        if (end <= _length)
            return;
        if (end > _capacity)
        {
            if (!_expandable)
                throw new NotSupportedException("MemoryStream: this stream's buffer can't grow");
            int capacity = Math.Max(end, Math.Max(256, _capacity * 2 > 0 ? Math.Min(_capacity * 2, int.MaxValue / 2) : 256));
            Capacity = capacity;
        }
        if (_position > _length)
            new Span<byte>(_buffer, _length, _position - _length).Clear();
        _length = end;
    }

    void EnsureOpen()
    {
        if (!_open)
            throw new ObjectDisposedException("MemoryStream");
    }

    void EnsureWritable()
    {
        EnsureOpen();
        if (!_writable)
            throw new NotSupportedException("MemoryStream: this stream can't be written");
    }
}

/// <summary>
/// A stream over a file, with a buffer of a size you choose (4096 bytes by
/// default; 0 or 1 for none). Dispose it: what's still in the buffer is
/// written out then, and not otherwise (nor when a Catch.Run closes the
/// file after a throw).
/// </summary>
// The buffer is this stream's own, and the C library's buffering is turned
// off: no C code ever points into memory that may be an arena's, so freeing
// the arena before disposing the stream loses the unwritten bytes but
// corrupts nothing (the C library flushes open files at exit, and would
// otherwise write from freed memory). Catch.Run keeps to that too: after a
// throw it closes the file, not this object (see Catch.cs), and the File
// handle in here, and so IsOpen below, finds out.
public partial class FileStream : Stream
{
    global::File _file;
    readonly byte[] _buffer; // null: unbuffered
    int _readPos, _readLen;  // bytes read ahead into _buffer, and how many are used
    int _writeLen;           // bytes in _buffer waiting to be written
    bool _read;              // the file was last read, so a write must seek first (C's rule)
    bool _busy;              // an async operation is running (lib/FileAsync.cs)
    readonly bool _canRead, _canWrite;
    readonly long _appendStart = -1; // FileMode.Append: where the file ended when opened

    /// <summary>The path the file was opened with.</summary>
    public virtual string Name { get; }

    /// <summary>Opens the file; read and write, or write only with FileMode.Append. A failure throws an IOException.</summary>
    public FileStream(string path, FileMode mode)
        : this(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite) { }

    /// <summary>Opens the file. A failure throws an IOException; TryOpen returns it instead.</summary>
    public FileStream(string path, FileMode mode, FileAccess access) : this(path, mode, access, FileShare.Read, 4096) { }

    /// <summary>Opens the file. <paramref name="share"/> isn't enforced.</summary>
    public FileStream(string path, FileMode mode, FileAccess access, FileShare share) : this(path, mode, access, share, 4096) { }

    /// <summary>
    /// Opens the file with a buffer of <paramref name="bufferSize"/> bytes
    /// (0 or 1 for none). A failure throws an IOException.
    /// </summary>
    public FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize)
        : this(path, mode, access, bufferSize, out FileError error)
    {
        if (error != FileError.None)
            throw new IOException(string.Concat("FileStream: couldn't open '", path, "': ", ErrorName(error)));
    }

    FileStream(string path, FileMode mode, FileAccess access, int bufferSize, out FileError error)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));
        if (access < FileAccess.Read || access > FileAccess.ReadWrite)
            throw new ArgumentOutOfRangeException(nameof(access));
        if (bufferSize < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        bool write = (access & FileAccess.Write) != 0;
        if (!write && mode != FileMode.Open && mode != FileMode.OpenOrCreate)
            throw new ArgumentException("FileStream: this FileMode needs FileAccess.Write");
        if (mode == FileMode.Append && access != FileAccess.Write)
            throw new ArgumentException("FileStream: FileMode.Append is for FileAccess.Write only");
        Name = path;
        _canRead = (access & FileAccess.Read) != 0;
        _canWrite = write;
        _file = OpenFile(path, mode, write, out error);
        if (error != FileError.None)
            return;
        if (mode == FileMode.Append)
        {
            // As .NET: the stream starts at the end and can seek, though not
            // back over what the file held. The file is still opened with
            // fopen's "a", so every write goes to the end wherever Seek
            // went (two programs can append to one log), where .NET's would
            // overwrite what this stream itself appended.
            _appendStart = _file.Length;
            if (!_file.Seek(_appendStart))
            {
                unsafe { _file.Close(); } // SAFETY: not used after this
                error = FileError.Other;
            }
        }
        _file.Unbuffer();
        if (bufferSize > 1)
            _buffer = new byte[bufferSize];
    }

    /// <summary>
    /// Opens the file, or says why it couldn't: NotFound, AccessDenied,
    /// AlreadyExists (FileMode.CreateNew), IsADirectory... On DOS, UEFI and
    /// microcontrollers the reason is Other.
    /// </summary>
    public static bool TryOpen(string path, FileMode mode, FileAccess access, out FileStream stream, out FileError error, int bufferSize = 4096)
    {
        var opened = new FileStream(path, mode, access, bufferSize, out error);
        stream = error == FileError.None ? opened : null;
        return stream != null;
    }

    // fopen's mode for each FileMode: r, w or a; + to read and write both;
    // x to fail if the file exists.
    static global::File OpenFile(string path, FileMode mode, bool write, out FileError error)
    {
        switch (mode)
        {
            case FileMode.Open:
                return global::File.Open(path, 'r', false, out error, plus: write);
            case FileMode.Create:
                return global::File.Open(path, 'w', false, out error, plus: true);
            case FileMode.CreateNew:
                return global::File.Open(path, 'w', true, out error, plus: true);
            case FileMode.Truncate:
                if (!global::File.Exists(path))
                {
                    error = FileError.NotFound;
                    return default;
                }
                return global::File.Open(path, 'w', false, out error, plus: true);
            case FileMode.Append:
                return global::File.Open(path, 'a', false, out error);
            case FileMode.OpenOrCreate:
            {
                global::File file = global::File.Open(path, 'r', false, out error, plus: write);
                if (file.IsOpen)
                    return file;
                if (error != FileError.NotFound && global::File.Exists(path))
                    return file;
                // Not there: create it, unless another program just did.
                file = global::File.Open(path, 'w', true, out error, plus: true);
                if (error != FileError.AlreadyExists)
                    return file;
                return global::File.Open(path, 'r', false, out error, plus: write);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => _canRead && IsOpen;
    /// <inheritdoc/>
    public override bool CanWrite => _canWrite && IsOpen;
    /// <inheritdoc/>
    public override bool CanSeek => IsOpen;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            EnsureOpen();
            FlushWrites();
            return _file.Length;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get { EnsureOpen(); return _file.Position + _writeLen - (_readLen - _readPos); }
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(new Span<byte>(buffer, offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        EnsureOpen();
        if (!_canRead)
            throw new NotSupportedException("FileStream: opened without FileAccess.Read");
        if (buffer.IsEmpty)
            return 0;
        if (_writeLen > 0)
        {
            FlushWrites();
            _file.Flush(); // C's rule: a write, then a flush or seek, then a read
        }
        _read = true;
        if (_readPos == _readLen)
        {
            // Nothing read ahead: a big read goes straight to the file.
            if (_buffer == null || buffer.Length >= _buffer.Length)
                return _file.Read(buffer);
            _readPos = 0;
            _readLen = _file.Read(_buffer);
            if (_readLen == 0)
                return 0;
        }
        return TakeReadAhead(buffer);
    }

    // Copies what was read ahead into buffer, as much as fits.
    int TakeReadAhead(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _readLen - _readPos);
        new ReadOnlySpan<byte>(_buffer, _readPos, n).CopyTo(buffer);
        _readPos += n;
        return n;
    }

    // Before a write after reads: the file is ahead of the stream by what was
    // read ahead and not used, so go back to the stream's position, which is
    // also the seek C requires between a read and a write.
    void LeaveReading()
    {
        long position = _file.Position - (_readLen - _readPos);
        _readPos = _readLen = 0;
        _read = false;
        if (!_file.Seek(position))
        {
            _busy = false; // an async write ends here (see FileAsync.cs's WriteOutAsync)
            throw new IOException(string.Concat("FileStream: couldn't seek in '", Name, "'"));
        }
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        if (_readPos < _readLen && IsOpen && !_busy)
            return _buffer[_readPos++];
        return base.ReadByte();
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureOpen();
        if (!_canWrite)
            throw new NotSupportedException("FileStream: opened without FileAccess.Write");
        if (_read)
            LeaveReading();
        if (_buffer == null)
        {
            WriteToFile(buffer);
            return;
        }
        if (_writeLen + buffer.Length > _buffer.Length)
            FlushWrites();
        if (buffer.Length >= _buffer.Length)
        {
            WriteToFile(buffer); // too big to be worth buffering
            return;
        }
        buffer.CopyTo(new Span<byte>(_buffer, _writeLen, buffer.Length));
        _writeLen += buffer.Length;
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        if (_buffer != null && !_read && _writeLen < _buffer.Length && _canWrite && IsOpen && !_busy)
        {
            _buffer[_writeLen++] = value;
            return;
        }
        base.WriteByte(value);
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureOpen();
        long from = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => Position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentException(nameof(origin)),
        };
        long target = from + offset;
        if (target < _appendStart)
            throw new IOException("FileStream: FileMode.Append can't seek back over what the file held when opened");
        FlushWrites();
        _readPos = _readLen = 0;
        _read = false;
        if (target < 0 || !_file.Seek(target))
            throw new IOException(string.Concat("FileStream: couldn't seek in '", Name, "'"));
        return target;
    }

    /// <summary>Not there yet: ends the program.</summary>
    public override void SetLength(long value) => throw new NotSupportedException("FileStream.SetLength");

    /// <summary>Writes out what's buffered.</summary>
    public override void Flush()
    {
        EnsureOpen();
        FlushWrites();
        _file.Flush();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!IsOpen)
            return;
        EnsureOpen(); // not while an async operation runs on the file
        // Closed even if the write fails: the exception may come back to
        // a Catch.Run, and the program go on.
        int length = _writeLen;
        _writeLen = 0;
        bool written = length == 0 || _file.Write(new ReadOnlySpan<byte>(_buffer, 0, length)) == length;
        unsafe { _file.Close(); } // SAFETY: _file is this stream's only handle, and is forgotten here
        _file = default;
        if (!written)
            throw new IOException(string.Concat("FileStream: couldn't write to '", Name, "'"));
    }

    void FlushWrites()
    {
        if (_writeLen == 0)
            return;
        int length = _writeLen;
        _writeLen = 0;
        WriteToFile(new ReadOnlySpan<byte>(_buffer, 0, length));
    }

    void WriteToFile(ReadOnlySpan<byte> bytes)
    {
        if (_file.Write(bytes) != bytes.Length)
            throw new IOException(string.Concat("FileStream: couldn't write to '", Name, "'"));
    }

    // Whether the file is open: not disposed, and not closed by a Catch.Run
    // after a throw, which the File handle keeps track of (File.cs) and the
    // stream learns here, at its next use. What it had buffered went with
    // the file.
    bool IsOpen
    {
        get
        {
            if (_file.IsOpen)
                return true;
            _writeLen = _readPos = _readLen = 0;
            return false;
        }
    }

    // Catch.Keep: no Catch.Run closes the file from here on.
    internal void KeepOpen() => Catch.Kept(_file.Ticket);

    void EnsureOpen()
    {
        if (!IsOpen)
            throw new ObjectDisposedException("FileStream");
        if (_busy)
            throw new InvalidOperationException("FileStream: an async operation on it is still running (await it first)");
    }

    static string ErrorName(FileError error) => error switch
    {
        FileError.NotFound => "not found",
        FileError.AccessDenied => "access denied",
        FileError.AlreadyExists => "it already exists",
        FileError.IsADirectory => "it's a directory",
        FileError.NotADirectory => "a part of the path isn't a directory",
        FileError.InvalidPath => "the path isn't valid",
        FileError.NoSpace => "the disk is full",
        _ => "an error",
    };
}
