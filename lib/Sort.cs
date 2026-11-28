// Sorting and binary search on spans, with .NET's MemoryExtensions names:
//
//   Span<int> numbers = ...;
//   numbers.Sort();                       // by IComparable<T>, here int's
//   numbers.Sort(new Descending());       // by a struct that implements IComparer<T>
//   int at = numbers.BinarySearch(42);    // in a sorted span; ~insertion point if absent
//
// A comparer passed as a struct is the fast way: the compiler makes a copy of
// the sort for that struct, and its Compare is a direct call it can inline.
// Sort() uses Comparer<T>.Default, which the compiler also resolves for each T
// (zerocore/System/Equality.cs). A Comparison<T> lambda works too, through a
// delegate call per comparison.
//
// The sort is a heapsort, as List<T>.Sort has always been: no extra memory,
// n log n comparisons however the items start out, and not stable (items that
// compare equal may end up in either order).

using System.Collections.Generic;

// Real .NET (dotnet/ZeroCompat) has these MemoryExtensions already.
#if !ZERO_DOTNET
namespace System
{
    public static partial class MemoryExtensions
    {
        /// <summary>Sorts the items by <see cref="Comparer{T}.Default"/>: T's IComparable&lt;T&gt;.</summary>
        public static void Sort<T>(this Span<T> span) => Sorting.HeapSort(span, new DefaultComparer<T>());

        /// <summary>Sorts the items by <paramref name="comparer"/>. Pass a struct for speed.</summary>
        public static void Sort<T, TComparer>(this Span<T> span, TComparer comparer) where TComparer : IComparer<T>
        {
            if (comparer == null)
                Environment.FailFast("Sort: comparer is null");
            Sorting.HeapSort(span, comparer);
        }

        /// <summary>Sorts the items by <paramref name="comparison"/>.</summary>
        public static void Sort<T>(this Span<T> span, Comparison<T> comparison)
        {
            if (comparison == null)
                Environment.FailFast("Sort: comparison is null");
            Sorting.HeapSort(span, new DelegateComparer<T>(comparison));
        }

        /// <summary>
        /// Where <paramref name="comparable"/> is in the sorted span: its index if
        /// some item compares equal to it, or else the bitwise complement (~) of
        /// the index it would be inserted at. <c>span.BinarySearch(42)</c> works for
        /// any T that implements IComparable&lt;T&gt;.
        /// </summary>
        public static int BinarySearch<T, TComparable>(this ReadOnlySpan<T> span, TComparable comparable)
            where TComparable : IComparable<T>
        {
            if (comparable == null)
                Environment.FailFast("BinarySearch: comparable is null");
            int low = 0, high = span.Length - 1;
            while (low <= high)
            {
                int middle = (int)(((uint)low + (uint)high) >> 1);
                // CompareTo says where the item is relative to the value searched
                // for, so its sign is the opposite of the usual comparison's.
                int order = comparable.CompareTo(span[middle]);
                if (order == 0)
                    return middle;
                if (order > 0)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return ~low;
        }

        /// <summary>The same, comparing with <paramref name="comparer"/>, by which the span must be sorted.</summary>
        public static int BinarySearch<T, TComparer>(this ReadOnlySpan<T> span, T value, TComparer comparer)
            where TComparer : IComparer<T>
        {
            if (comparer == null)
                Environment.FailFast("BinarySearch: comparer is null");
            return BinarySearch(span, new ComparerComparable<T, TComparer>(value, comparer));
        }

        public static int BinarySearch<T, TComparable>(this Span<T> span, TComparable comparable)
            where TComparable : IComparable<T>
            => BinarySearch((ReadOnlySpan<T>)span, comparable);

        public static int BinarySearch<T, TComparer>(this Span<T> span, T value, TComparer comparer)
            where TComparer : IComparer<T>
            => BinarySearch((ReadOnlySpan<T>)span, value, comparer);

        /// <summary>Reverses the order of the items, in place.</summary>
        public static void Reverse<T>(this Span<T> span)
        {
            for (int i = 0, j = span.Length - 1; i < j; i++, j--)
            {
                T item = span[i];
                span[i] = span[j];
                span[j] = item;
            }
        }
    }
}
#endif

namespace System.Collections.Generic
{
    // Comparer<T>.Default as a struct, so a sort over it is compiled for T and
    // the call to Compare is direct.
    internal struct DefaultComparer<T> : IComparer<T>
    {
        public int Compare(T x, T y) => Comparer<T>.Default.Compare(x, y);
    }

    internal struct FunctionPointerComparer<T> : IComparer<T>
    {
        private readonly delegate*<T, T, int> _compare;

        public FunctionPointerComparer(delegate*<T, T, int> compare) => _compare = compare;

        public int Compare(T x, T y)
        {
            // SAFETY: _compare is the caller's comparison (List.Sort and
            // RefList.Sort check it isn't null), called with two of its items.
            unsafe { return _compare(x, y); }
        }
    }

    internal struct DelegateComparer<T> : IComparer<T>
    {
        private readonly Comparison<T> _comparison;

        public DelegateComparer(Comparison<T> comparison) => _comparison = comparison;

        public int Compare(T x, T y) => _comparison(x, y);
    }

    // A value and a comparer, as the IComparable<T> BinarySearch searches with.
    internal struct ComparerComparable<T, TComparer> : IComparable<T> where TComparer : IComparer<T>
    {
        private readonly T _value;
        private readonly TComparer _comparer;

        public ComparerComparable(T value, TComparer comparer)
        {
            _value = value;
            _comparer = comparer;
        }

        public int CompareTo(T other) => _comparer.Compare(_value, other);
    }

    internal static class Sorting
    {
        // Arranges the items as a heap with the largest first, then moves the
        // largest to the end and repairs the heap, one item at a time.
        public static void HeapSort<T, TComparer>(Span<T> items, TComparer comparer) where TComparer : IComparer<T>
        {
            int count = items.Length;
            for (int start = count / 2 - 1; start >= 0; start--)
                SiftDown(items, start, count, ref comparer);
            for (int end = count - 1; end > 0; end--)
            {
                T largest = items[0];
                items[0] = items[end];
                items[end] = largest;
                SiftDown(items, 0, end, ref comparer);
            }
        }

        // Moves items[root] down until neither child is larger, within [0, count).
        private static void SiftDown<T, TComparer>(Span<T> items, int root, int count, ref TComparer comparer)
            where TComparer : IComparer<T>
        {
            while (true)
            {
                int child = 2 * root + 1;
                if (child >= count)
                    return;
                if (child + 1 < count && comparer.Compare(items[child], items[child + 1]) < 0)
                    child++;
                if (comparer.Compare(items[root], items[child]) >= 0)
                    return;
                T item = items[root];
                items[root] = items[child];
                items[child] = item;
                root = child;
            }
        }
    }
}
