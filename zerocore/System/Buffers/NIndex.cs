// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    public readonly struct NIndex : IEquatable<NIndex>
    {
        private readonly nint _value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NIndex(nint value, bool fromEnd = false)
        {
            if (value < 0)
            {
                ThrowHelper.ThrowValueArgumentOutOfRange_NeedNonNegNumException();
            }

            if (fromEnd)
                _value = ~value;
            else
                _value = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NIndex(Index index)
        {
            if (index.IsFromEnd)
                _value = ~index.Value;
            else
                _value = index.Value;
        }

        // The following private constructor exists to skip the checks in the public ctor
        private NIndex(nint value)
        {
            _value = value;
        }

        public static NIndex Start => new NIndex((nint)0);

        public static NIndex End => new NIndex((nint)~0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NIndex FromStart(nint value)
        {
            if (value < 0)
            {
                ThrowHelper.ThrowValueArgumentOutOfRange_NeedNonNegNumException();
            }

            return new NIndex(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NIndex FromEnd(nint value)
        {
            if (value < 0)
            {
                ThrowHelper.ThrowValueArgumentOutOfRange_NeedNonNegNumException();
            }

            return new NIndex(~value);
        }

        public Index ToIndex() => checked((Index)this);

        public Index ToIndexUnchecked() => (Index)this;

        public nint Value
        {
            get
            {
                if (_value < 0)
                    return ~_value;
                else
                    return _value;
            }
        }

        public bool IsFromEnd => _value < 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetOffset(nint length)
        {
            nint offset = _value;
            if (IsFromEnd)
            {
                // offset = length - (~value)
                // offset = length + (~(~value) + 1)
                // offset = length + value + 1

                offset += length + 1;
            }
            return offset;
        }

        public override bool Equals(object? value) => value is NIndex other && _value == other._value;

        public bool Equals(NIndex other) => _value == other._value;

        public override int GetHashCode() => _value.GetHashCode();

        public static implicit operator NIndex(nint value) => FromStart(value);

        public static implicit operator NIndex(Index value) => new NIndex(value);

        public static explicit operator Index(NIndex value) => new Index((int)value.Value, value.IsFromEnd);

        public static explicit operator checked Index(NIndex value) => new Index(checked((int)value.Value), value.IsFromEnd);

        public override string ToString()
        {
            if (IsFromEnd)
                return ToStringFromEnd();

            return Value.ToString();
        }

        private string ToStringFromEnd()
        {
            Span<char> span = stackalloc char[21]; // 1 for ^ and 20 for longest possible nuint value
            bool formatted = ((nuint)Value).TryFormat(span.Slice(1), out int charsWritten);
            Debug.Assert(formatted);
            span[0] = '^';
            return string.FromChars(span.Slice(0, charsWritten + 1));
        }
    }
}
