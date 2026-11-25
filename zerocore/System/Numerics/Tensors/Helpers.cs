using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System.Numerics.Tensors
{
    // The few MemoryExtensions methods and the ArrayPool the tensor code
    // (ported from the System.Numerics.Tensors package) calls, kept internal
    // so they don't compete with lib/'s span extensions.
    internal static class TensorSpanExtensions
    {
        public static Span<T> AsSpan<T>(this T[] array) => new Span<T>(array);

        public static Span<T> AsSpan<T>(this T[] array, int start) => new Span<T>(array).Slice(start);

        public static Span<T> AsSpan<T>(this T[] array, int start, int length) => new Span<T>(array).Slice(start, length);

        public static bool Contains(this ReadOnlySpan<nint> span, nint value)
        {
            for (int i = 0; i < span.Length; i++)
                if (span[i] == value)
                    return true;
            return false;
        }

        public static bool Contains(this Span<nint> span, nint value) => ((ReadOnlySpan<nint>)span).Contains(value);

        public static bool ContainsAnyExcept(this ReadOnlySpan<nint> span, nint value)
        {
            for (int i = 0; i < span.Length; i++)
                if (span[i] != value)
                    return true;
            return false;
        }

        public static bool ContainsAnyExcept(this Span<nint> span, nint value) => ((ReadOnlySpan<nint>)span).ContainsAnyExcept(value);

        public static bool SequenceEqual(this ReadOnlySpan<nint> span, ReadOnlySpan<nint> other)
        {
            if (span.Length != other.Length)
                return false;
            for (int i = 0; i < span.Length; i++)
                if (span[i] != other[i])
                    return false;
            return true;
        }

        public static bool SequenceEqual(this Span<nint> span, ReadOnlySpan<nint> other) => ((ReadOnlySpan<nint>)span).SequenceEqual(other);

        public static bool SequenceEqual(this ReadOnlySpan<int> span, ReadOnlySpan<int> other)
        {
            if (span.Length != other.Length)
                return false;
            for (int i = 0; i < span.Length; i++)
                if (span[i] != other[i])
                    return false;
            return true;
        }

        // .NET's span SequenceEqual for IEquatable<T> (so NaN equals NaN).
        public static bool ElementsEqual<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> other) where T : IEquatable<T>
        {
            if (span.Length != other.Length)
                return false;
            for (int i = 0; i < span.Length; i++)
                if (!span[i].Equals(other[i]))
                    return false;
            return true;
        }

        public static void Reverse<T>(this Span<T> span)
        {
            for (int i = 0, j = span.Length - 1; i < j; i++, j--)
            {
                T t = span[i];
                span[i] = span[j];
                span[j] = t;
            }
        }
    }

    internal static class MemoryExtensions
    {
        // Sorts keys, moving items with them (stable: an insertion sort; the
        // tensor code sorts one stride per dimension).
        public static void Sort(Span<nint> keys, Span<int> items)
        {
            for (int i = 1; i < keys.Length; i++)
            {
                nint key = keys[i];
                int item = items[i];
                int j = i - 1;
                for (; j >= 0 && keys[j] > key; j--)
                {
                    keys[j + 1] = keys[j];
                    items[j + 1] = items[j];
                }
                keys[j + 1] = key;
                items[j + 1] = item;
            }
        }
    }

    // .NET's ArrayPool<T>.Shared, as far as the tensor code uses it. With no
    // collector there is nothing to pool into: Rent allocates, for good. The
    // tensor code rents only for more than 5 dimensions (shapes keep up to 5
    // inline) and in a few strided fallbacks.
    internal sealed class ArrayPool<T>
    {
        public static readonly ArrayPool<T> Shared = new ArrayPool<T>();

        public T[] Rent(int minimumLength) => new T[minimumLength];

        public void Return(T[] array, bool clearArray = false) { }
    }
}
