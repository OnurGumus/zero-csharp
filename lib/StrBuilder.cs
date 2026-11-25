// StrBuilder: growable string buffer backed by an Arena, with $"..." support.
//
//   StrBuilder sb = StrBuilder.Create(arena);
//   sb.Append("total: ");
//   sb.Append($"{count} items, {price:F2} each");
//   Console.WriteLine(sb.AsSpan());
//
// Like List<T>, a StrBuilder is a handle to a header in the arena, so copies
// share the same contents, and default(StrBuilder) is "not created": it reads
// as empty and Append returns false. Append also returns false when the arena
// is full. Growing extends the buffer in place while it is still the arena's
// most recent allocation; otherwise it copies into a fresh buffer and the old
// one stays allocated until the arena is reset.
//
// The buffer is laid out like a string object, [MethodTable*][length][chars],
// so Str.Format(arena, $"...") turns a finished buffer into a string without copying.

using System;
using System.Runtime.CompilerServices;

public struct StrBuilder
{
    const int MinCapacity = 16;

    struct Header
    {
        public Arena Arena;    // where the builder lives and grows
        public byte* Object;   // string-object-shaped buffer (see Str.MakeString)
        public int Length;
        public int Capacity;   // in chars, terminator not included
    }

    Header* _header;

    /// <summary>Returns a new builder in <paramref name="arena"/>, or a not-created one if it is out of memory.</summary>
    public static StrBuilder Create(Arena arena, int initialCapacity = 64)
    {
        if (initialCapacity < MinCapacity)
            initialCapacity = MinCapacity;

        Header* header = arena.Alloc<Header>();
        byte* obj = Str.AllocStringObject(arena, initialCapacity);
        if (header == null || obj == null)
            return default;

        unsafe
        {
            // SAFETY: header was just allocated and zeroed.
            header->Arena = arena;
            header->Object = obj;
            header->Capacity = initialCapacity;
        }
        return new StrBuilder { _header = header };
    }

    public bool IsCreated => _header != null;

    public int Length
    {
        get
        {
            if (_header == null)
                return 0;
            unsafe { return _header->Length; } // SAFETY: a live header (see List<T>).
        }
    }

    /// <summary>The text so far. Valid until the next Append or until the arena is reset.</summary>
    public ReadOnlySpan<char> AsSpan()
    {
        if (_header == null)
            return default;
        DebugCheck();
        unsafe { return new ReadOnlySpan<char>(Chars(), _header->Length); } // SAFETY: Length chars are initialized.
    }

    public bool Append(ReadOnlySpan<char> text)
    {
        if (_header == null)
            return false;
        DebugCheck();

        unsafe
        {
            // SAFETY: a live header; after Grow there is room for text at Chars() + Length.
            int length = _header->Length;
            if (text.Length > _header->Capacity - length && !Grow(text.Length))
                return false;
            text.CopyTo(new Span<char>(Chars() + length, text.Length));
            _header->Length = length + text.Length;
        }
        return true;
    }

    public bool Append(char value)
    {
        Span<char> one = stackalloc char[1];
        one[0] = value;
        return Append(one);
    }

    public bool Append(long value)
    {
        Span<char> buffer = stackalloc char[Fmt.MaxChars];
        return Append(buffer.Slice(0, Fmt.Int(value, buffer)));
    }

    // For F#, where a whole number would fit both the long and the double
    // overload: C# widens an int to long by itself.
    public bool Append(int value) => Append((long)value);

    /// <summary>Appends a number as $"{value}" writes it.</summary>
    public bool Append(double value)
    {
        Span<char> buffer = stackalloc char[Fmt.MaxChars];
        int length = Fmt.Format(value, null, buffer);
        return length >= 0 ? Append(buffer.Slice(0, length)) : Append(value.ToString());
    }

    /// <summary>Appends True or False.</summary>
    public bool Append(bool value) => Append(Fmt.Bool(value));

    /// <summary>Appends $"..." directly, without an intermediate string.</summary>
    public bool Append([InterpolatedStringHandlerArgument("")] ref InterpolatedHandler handler) => handler.Ok;

    public void Clear()
    {
        if (_header != null)
            unsafe { _header->Length = 0; } // SAFETY: a live header.
    }

    /// <summary>The character at <paramref name="index"/>; stops the program if it is out of range.</summary>
    public char this[int index]
    {
        get
        {
            unsafe { return Chars()[CheckIndex(index)]; } // SAFETY: CheckIndex keeps it below Length.
        }
        set
        {
            unsafe { Chars()[CheckIndex(index)] = value; } // SAFETY: CheckIndex keeps it below Length.
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> before the character at <paramref name="index"/>
    /// (at the end when it is Length). Returns false if the builder isn't created
    /// or the arena is full, leaving the text as it was; stops the program if
    /// the index is out of range.
    /// </summary>
    public bool Insert(int index, ReadOnlySpan<char> text) => Splice(index, 0, text);

    public bool Insert(int index, char value)
    {
        Span<char> one = stackalloc char[1];
        one[0] = value;
        return Splice(index, 0, one);
    }

    public bool Insert(int index, long value)
    {
        Span<char> buffer = stackalloc char[Fmt.MaxChars];
        return Splice(index, 0, buffer.Slice(0, Fmt.Int(value, buffer)));
    }

    public bool Insert(int index, int value) => Insert(index, (long)value);

    /// <summary>Removes <paramref name="length"/> characters from <paramref name="startIndex"/>; stops the program if they aren't all there.</summary>
    public void Remove(int startIndex, int length)
    {
        CheckRange(startIndex, length);
        if (_header != null)
            Splice(startIndex, length, default); // shrinking never allocates
    }

    /// <summary>Replaces every <paramref name="oldChar"/> with <paramref name="newChar"/>.</summary>
    public void Replace(char oldChar, char newChar) => Replace(oldChar, newChar, 0, Length);

    /// <summary>Replaces <paramref name="oldChar"/> with <paramref name="newChar"/> in the <paramref name="count"/> characters from <paramref name="startIndex"/>.</summary>
    public void Replace(char oldChar, char newChar, int startIndex, int count)
    {
        CheckRange(startIndex, count);
        if (_header == null)
            return;
        unsafe
        {
            // SAFETY: CheckRange keeps startIndex + count within Length.
            char* chars = Chars();
            for (int i = startIndex; i < startIndex + count; i++)
                if (chars[i] == oldChar)
                    chars[i] = newChar;
        }
    }

    /// <summary>
    /// Replaces every <paramref name="oldValue"/> with <paramref name="newValue"/>,
    /// left to right, as .NET's StringBuilder.Replace. Returns false if the
    /// builder isn't created or the arena is full, leaving the text as it was;
    /// stops the program if <paramref name="oldValue"/> is empty.
    /// </summary>
    public bool Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue)
        => Replace(oldValue, newValue, 0, Length);

    /// <summary>Replaces the <paramref name="oldValue"/>s that lie wholly within the <paramref name="count"/> characters from <paramref name="startIndex"/>.</summary>
    public bool Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue, int startIndex, int count)
    {
        if (oldValue.Length == 0)
            Environment.FailFast("StrBuilder: Replace of an empty string");
        CheckRange(startIndex, count);
        if (_header == null)
            return false;
        DebugCheck();

        int length;
        int capacity;
        char* chars;
        unsafe
        {
            length = _header->Length; // SAFETY: a live header.
            capacity = _header->Capacity;
            chars = Chars();
        }

        // Count the matches first, so the result's length is known before
        // anything moves.
        int end = startIndex + count;
        int matches = 0;
        int scan = startIndex;
        ReadOnlySpan<char> window;
        unsafe { window = new ReadOnlySpan<char>(chars, end); } // SAFETY: end <= Length.
        while (true)
        {
            int found = window.Slice(scan).IndexOf(oldValue);
            if (found < 0)
                break;
            matches++;
            scan += found + oldValue.Length;
        }
        if (matches == 0)
            return true;

        long resultLength = length + (long)matches * (newValue.Length - oldValue.Length);
        if (resultLength > int.MaxValue / 2)
            return false;
        int newLength = (int)resultLength;

        char* source;
        char* target;
        if (!Overlaps(oldValue) && !Overlaps(newValue)
            && (newLength <= length || EnsureCapacityInPlace(newLength)))
        {
            // In place. When the text gets longer, it moves to the end of the
            // buffer first; the result is then written from the start, and
            // never catches up with what is still to be read.
            int shift = newLength > length ? newLength - length : 0;
            unsafe
            {
                // SAFETY: the buffer has room for newLength >= length + shift chars.
                chars = Chars();
                if (shift > 0)
                    new ReadOnlySpan<char>(chars, length).CopyTo(new Span<char>(chars + shift, length));
                source = chars + shift;
                target = chars;
            }
        }
        else
        {
            // A new buffer: the arguments point into this builder's text, or
            // there's no room where it is. The old one stays allocated until the
            // arena is reset, as when Append grows.
            int newCapacity = newLength > capacity ? Math.Max(newLength, (int)Math.Min((long)capacity * 2, int.MaxValue / 2)) : capacity;
            byte* obj;
            unsafe { obj = Str.AllocStringObject(_header->Arena, newCapacity); } // SAFETY: a live header.
            if (obj == null)
                return false;
            unsafe
            {
                // SAFETY: the old buffer is still allocated; the new one has room for newCapacity chars.
                source = chars;
                _header->Object = obj;
                _header->Capacity = newCapacity;
                target = Chars();
            }
        }

        unsafe
        {
            // SAFETY: source holds length chars, target has room for newLength.
            // Each copy is a memmove, so source and target may overlap.
            int read = 0;
            int written = 0;
            scan = startIndex;
            for (int i = 0; i < matches; i++)
            {
                int at = scan + new ReadOnlySpan<char>(source + scan, end - scan).IndexOf(oldValue);
                new ReadOnlySpan<char>(source + read, at - read).CopyTo(new Span<char>(target + written, at - read));
                written += at - read;
                newValue.CopyTo(new Span<char>(target + written, newValue.Length));
                written += newValue.Length;
                read = scan = at + oldValue.Length;
            }
            new ReadOnlySpan<char>(source + read, length - read).CopyTo(new Span<char>(target + written, length - read));
            _header->Length = newLength;
        }
        return true;
    }

    /// <summary>Copies the text into a new string in the builder's own arena. Returns null if it is out of memory.</summary>
    public string ToArenaString()
    {
        if (_header == null)
            return null;
        unsafe { return Str.ToArenaString(_header->Arena, AsSpan()); } // SAFETY: a live header.
    }

    /// <summary>
    /// Copies the text into a new string in <paramref name="target"/>: build in a
    /// scratch arena, keep the result in a longer-lived one.
    /// </summary>
    public string ToArenaString(Arena target) => Str.ToArenaString(target, AsSpan());

    /// <summary>Turns the buffer itself into a string. The builder must not be used afterwards.</summary>
    internal string TakeString()
    {
        if (_header == null)
            return null;
        unsafe { return Str.MakeString(_header->Object, _header->Length); } // SAFETY: Object has room for Capacity >= Length chars.
    }

    // Under Arena.DebugChecks, stops the program if the builder's memory was freed.
    void DebugCheck()
    {
        if (Arena.DebugChecks)
            unsafe { Arena.CheckLive(_header->Arena, _header->Object, Str.StringObjectSize(_header->Capacity)); } // SAFETY: callers checked _header != null.
    }

    // Replaces removeCount chars at index with text. Like Replace, it works in
    // place unless text points into this builder or there's no room, and then
    // builds the result in a new buffer.
    bool Splice(int index, int removeCount, ReadOnlySpan<char> text)
    {
        if (index < 0 || index > Length)
            Environment.FailFast("StrBuilder: index out of range");
        if (_header == null)
            return false;
        DebugCheck();

        int length;
        int capacity;
        char* chars;
        unsafe
        {
            length = _header->Length; // SAFETY: a live header.
            capacity = _header->Capacity;
            chars = Chars();
        }
        long resultLength = (long)length - removeCount + text.Length;
        if (resultLength > int.MaxValue / 2)
            return false;
        int newLength = (int)resultLength;
        int tail = length - index - removeCount;

        if (!Overlaps(text) && (newLength <= length || EnsureCapacityInPlace(newLength)))
        {
            unsafe
            {
                // SAFETY: the buffer has room for newLength chars; CopyTo is a memmove.
                chars = Chars();
                new ReadOnlySpan<char>(chars + index + removeCount, tail).CopyTo(new Span<char>(chars + index + text.Length, tail));
                text.CopyTo(new Span<char>(chars + index, text.Length));
                _header->Length = newLength;
            }
            return true;
        }

        int newCapacity = newLength > capacity ? Math.Max(newLength, (int)Math.Min((long)capacity * 2, int.MaxValue / 2)) : capacity;
        byte* obj;
        unsafe { obj = Str.AllocStringObject(_header->Arena, newCapacity); } // SAFETY: a live header.
        if (obj == null)
            return false;
        unsafe
        {
            // SAFETY: the old buffer is still allocated; the new one has room for newLength chars.
            char* target = (char*)(obj + RuntimeHelpers.OffsetToStringData);
            new ReadOnlySpan<char>(chars, index).CopyTo(new Span<char>(target, index));
            text.CopyTo(new Span<char>(target + index, text.Length));
            new ReadOnlySpan<char>(chars + index + removeCount, tail).CopyTo(new Span<char>(target + index + text.Length, tail));
            _header->Object = obj;
            _header->Capacity = newCapacity;
            _header->Length = newLength;
        }
        return true;
    }

    // Makes room for `needed` chars without moving the buffer: true if there
    // already is room, or the buffer is the arena's newest allocation.
    bool EnsureCapacityInPlace(int needed)
    {
        unsafe
        {
            // SAFETY: callers checked _header != null.
            int capacity = _header->Capacity;
            if (needed <= capacity)
                return true;
            long grown = Math.Max((long)needed, Math.Min((long)capacity * 2, int.MaxValue / 2));
            if (_header->Arena.TryExtend(_header->Object, Str.StringObjectSize(capacity), Str.StringObjectSize((int)grown)))
            {
                _header->Capacity = (int)grown;
                return true;
            }
            return false;
        }
    }

    // Whether text lies in this builder's buffer (as sb.AsSpan() does).
    bool Overlaps(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return false;
        unsafe
        {
            // SAFETY: only compares addresses; callers checked _header != null.
            fixed (char* p = text)
            {
                char* start = Chars();
                return p < start + _header->Capacity && p + text.Length > start;
            }
        }
    }

    int CheckIndex(int index)
    {
        if ((uint)index >= (uint)Length)
            Environment.FailFast("StrBuilder: index out of range");
        DebugCheck();
        return index;
    }

    void CheckRange(int startIndex, int count)
    {
        if (startIndex < 0 || count < 0 || startIndex > Length - count)
            Environment.FailFast("StrBuilder: index out of range");
    }

    char* Chars()
    {
        unsafe { return (char*)(_header->Object + RuntimeHelpers.OffsetToStringData); } // SAFETY: callers checked _header != null.
    }

    bool Grow(int extra)
    {
        int length;
        int capacity;
        Arena arena;
        byte* oldObj;
        unsafe
        {
            length = _header->Length; // SAFETY: callers checked _header != null.
            capacity = _header->Capacity;
            arena = _header->Arena;
            oldObj = _header->Object;
        }

        long needed = (long)length + extra;
        long newCapacity = (long)capacity * 2;
        if (newCapacity < needed)
            newCapacity = needed;
        if (newCapacity > int.MaxValue / 2)
            newCapacity = needed;
        if (newCapacity > int.MaxValue / 2)
            return false;

        // Nothing was allocated after the buffer: just make it longer.
        if (arena.TryExtend(oldObj, Str.StringObjectSize(capacity), Str.StringObjectSize((int)newCapacity)))
        {
            unsafe { _header->Capacity = (int)newCapacity; } // SAFETY: a live header.
            return true;
        }

        byte* obj = Str.AllocStringObject(arena, (int)newCapacity);
        if (obj == null)
            return false;

        unsafe
        {
            // SAFETY: both buffers hold at least length chars.
            AsSpan().CopyTo(new Span<char>(obj + RuntimeHelpers.OffsetToStringData, length));
            _header->Object = obj;
            _header->Capacity = (int)newCapacity;
        }
        return true;
    }

    /// <summary>
    /// Lets StrBuilder.Append and Str.Format take $"..." and format each hole
    /// straight into the arena. Supports {x,alignment} and {x:format}, with
    /// .NET's format strings.
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct InterpolatedHandler
    {
        StrBuilder _builder;
        bool _ok;

        /// <summary>Used by StrBuilder.Append: appends to that builder.</summary>
        public InterpolatedHandler(int literalLength, int formattedCount, StrBuilder builder)
        {
            _builder = builder;
            _ok = builder.IsCreated;
        }

        /// <summary>Used by ScratchText.Append: appends to that text.</summary>
        public InterpolatedHandler(int literalLength, int formattedCount, ScratchText text)
        {
            _builder = text.BuilderToAppendTo();
            _ok = _builder.IsCreated;
        }

        /// <summary>Used by Str.Format: starts a new builder in that arena.</summary>
        public InterpolatedHandler(int literalLength, int formattedCount, Arena arena)
        {
            _builder = Create(arena, literalLength + formattedCount * 8);
            _ok = _builder.IsCreated;
        }

        internal bool Ok => _ok;

        internal string TakeString() => _ok ? _builder.TakeString() : null;

        public void AppendLiteral(string value) => Put(value, 0);

        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null) => Put(value, alignment);

        public void AppendFormatted(char value, int alignment = 0)
        {
            Span<char> one = stackalloc char[1];
            one[0] = value;
            Put(one, alignment);
        }

        // Integers without a format: the small path (see Fmt). Overloads for
        // each type and without optional parameters, as .NET's handler has
        // them, so the compiler prefers these to the generic ones below.
        public void AppendFormatted(sbyte value) => Whole(value, 0);
        public void AppendFormatted(sbyte value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(byte value) => Whole(value, 0);
        public void AppendFormatted(byte value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(short value) => Whole(value, 0);
        public void AppendFormatted(short value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(ushort value) => Whole(value, 0);
        public void AppendFormatted(ushort value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(int value) => Whole(value, 0);
        public void AppendFormatted(int value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(uint value) => Whole(value, 0);
        public void AppendFormatted(uint value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(long value) => Whole(value, 0);
        public void AppendFormatted(long value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(ulong value) => Whole(value, 0);
        public void AppendFormatted(ulong value, int alignment) => Whole(value, alignment);

        // Hex.Of(x, 4): hex without .NET's formatting (see lib/Hex.cs).
        public void AppendFormatted(Hex value) => AppendFormatted(value, 0);

        public void AppendFormatted(Hex value, int alignment)
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            Put(buffer.Slice(0, value.Format(buffer)), alignment);
        }

        // Any number with a format, {x:X4} or {x,8:F2}, and double, float,
        // Half and decimal with or without one: .NET's format strings.
        public void AppendFormatted<T>(T value) where T : ISpanFormattable => Formatted(value, 0, null);

        public void AppendFormatted<T>(T value, int alignment) where T : ISpanFormattable => Formatted(value, alignment, null);

        public void AppendFormatted<T>(T value, string format) where T : ISpanFormattable => Formatted(value, 0, format);

        public void AppendFormatted<T>(T value, int alignment, string format) where T : ISpanFormattable => Formatted(value, alignment, format);

        void Formatted<T>(T value, int alignment, string format) where T : ISpanFormattable
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            int length = Fmt.Format(value, format, buffer);
            if (length >= 0)
                Put(buffer.Slice(0, length), alignment);
            else
                Put(value.ToString(format, null), alignment); // longer than the buffer: a string, never freed
        }

        void Whole(long value, int alignment)
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            Put(buffer.Slice(0, Fmt.Int(value, buffer)), alignment);
        }

        void Whole(ulong value, int alignment)
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            Put(buffer.Slice(0, Fmt.UInt(value, buffer)), alignment);
        }

        public void AppendFormatted(bool value, int alignment = 0) => Put(Fmt.Bool(value), alignment);

        // alignment > 0 pads on the left (right-aligns), < 0 pads on the right.
        void Put(scoped ReadOnlySpan<char> text, int alignment)
        {
            int padding = (alignment < 0 ? -alignment : alignment) - text.Length;
            if (alignment > 0)
                Pad(padding);
            if (_ok)
                _ok = _builder.Append(text);
            if (alignment < 0)
                Pad(padding);
        }

        void Pad(int count)
        {
            for (; count > 0 && _ok; count--)
                _ok = _builder.Append(' ');
        }
    }
}
