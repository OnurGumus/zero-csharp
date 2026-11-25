// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    public readonly struct NRange : IEquatable<NRange>
    {
        public NIndex Start { get; }

        public NIndex End { get; }

        public NRange(NIndex start, NIndex end)
        {
            Start = start;
            End = end;
        }

        public NRange(Range range)
        {
            Start = range.Start;
            End = range.End;
        }

        public override bool Equals(object? value) =>
            value is NRange r &&
            r.Start.Equals(Start) &&
            r.End.Equals(End);

        public bool Equals(NRange other) => other.Start.Equals(Start) && other.End.Equals(End);

        public override int GetHashCode()
        {
            return HashCode.Combine(Start.GetHashCode(), End.GetHashCode());
        }

        public override string ToString()
        {
            Span<char> span = stackalloc char[2 + 2 * 21]; // 2 for "..", then for each NIndex 1 for '^' and 20 for longest possible nuint
            int pos = 0;

            if (Start.IsFromEnd)
            {
                span[0] = '^';
                pos = 1;
            }
            bool formatted = ((nuint)Start.Value).TryFormat(span.Slice(pos), out int charsWritten);
            Debug.Assert(formatted);
            pos += charsWritten;

            span[pos++] = '.';
            span[pos++] = '.';

            if (End.IsFromEnd)
            {
                span[pos++] = '^';
            }
            formatted = ((nuint)End.Value).TryFormat(span.Slice(pos), out charsWritten);
            Debug.Assert(formatted);
            pos += charsWritten;

            return string.FromChars(span.Slice(0, pos));
        }

        public static NRange StartAt(NIndex start) => new NRange(start, NIndex.End);

        public static NRange EndAt(NIndex end) => new NRange(NIndex.Start, end);

        public static NRange All => new NRange(NIndex.Start, NIndex.End);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (nint Offset, nint Length) GetOffsetAndLength(nint length)
        {
            nint start = Start.GetOffset(length);
            nint end = End.GetOffset(length);

            if ((nuint)end > (nuint)length || (nuint)start > (nuint)end)
            {
                ThrowArgumentOutOfRangeException();
            }

            return (start, end - start);
        }

        private static void ThrowArgumentOutOfRangeException() => throw new ArgumentOutOfRangeException("length");

        public static implicit operator NRange(Range range) => new NRange(range.Start, range.End);

        public static explicit operator Range(NRange value) => new Range((Index)value.Start, (Index)value.End);

        public static explicit operator checked Range(NRange value) => new Range(checked((Index)value.Start), checked((Index)value.End));

        public Range ToRange() => new Range(checked((Index)Start), checked((Index)End));

        public Range ToRangeUnchecked() => new Range((Index)Start, (Index)End);
    }
}
