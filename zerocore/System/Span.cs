using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    public readonly ref struct Span<T>
    {
        internal readonly ref T _reference;
        private readonly int _length;

        public Span(T[] array)
        {
            if (array == null)
            {
                this = default;
                return;
            }
            _reference = ref MemoryMarshal.GetArrayDataReference(array);
            _length = array.Length;
        }

        public Span(T[] array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0)
                    Environment.FailFast("Span: range out of bounds");
                this = default;
                return;
            }
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start))
                Environment.FailFast("Span: range out of bounds");
            _reference = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), start);
            _length = length;
        }

        public unsafe Span(void* pointer, int length)
        {
            if (length < 0)
                Environment.FailFast("Span: negative length");
            _reference = ref Unsafe.As<byte, T>(ref *(byte*)pointer);
            _length = length;
        }

        public Span(ref T reference, int length)
        {
            _reference = ref reference;
            _length = length;
        }

        public ref T this[int index]
        {
            [Intrinsic]
            get
            {
                if ((uint)index >= (uint)_length)
                    Environment.FailFast("Span: index out of range");
                return ref Unsafe.Add(ref _reference, index);
            }
        }

        public int Length => _length;

        // A span of chars is its text, as in .NET (a new string, never freed);
        // any other is "System.Span[n]" (.NET names T too: zero mode has no type names). F# calls
        // this for span.ToString(), and without it boxed the span: invalid.
        public override string ToString()
        {
            if (typeof(T) == typeof(char))
                return string.FromChars(new ReadOnlySpan<char>(ref Unsafe.As<T, char>(ref _reference), _length));
            return string.Concat("System.Span[", _length.ToString(), "]");
        }

        // As in .NET, a span has no value equality or hash: these throw. Here
        // so that F#'s span.Equals(x) and span.GetHashCode() are calls, not a
        // boxed span (invalid code the compiler can't compile).
        [Obsolete("Equals() on Span will always throw an exception.")]
        public override bool Equals(object obj) =>
            throw new NotSupportedException("Equals() on Span isn't supported.");

        [Obsolete("GetHashCode() on Span will always throw an exception.")]
        public override int GetHashCode() =>
            throw new NotSupportedException("GetHashCode() on Span isn't supported.");

        public bool IsEmpty => _length == 0;

        public static Span<T> Empty => default;

        public Span<T> Slice(int start)
        {
            if ((uint)start > (uint)_length)
                Environment.FailFast("Span: slice out of range");
            return new Span<T>(ref Unsafe.Add(ref _reference, start), _length - start);
        }

        public Span<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
                Environment.FailFast("Span: slice out of range");
            return new Span<T>(ref Unsafe.Add(ref _reference, start), length);
        }

        public void Clear()
            => SpanHelpers.Fill(ref Unsafe.As<T, byte>(ref _reference), 0, (nuint)_length * (nuint)Unsafe.SizeOf<T>());

        public void Fill(T value) => SpanHelpers.Fill(ref _reference, value, (nuint)_length);

        public void CopyTo(Span<T> destination) => ((ReadOnlySpan<T>)this).CopyTo(destination);

        /// <summary>A new array holding a copy of the elements. Zero mode never frees it.</summary>
        public T[] ToArray() => ((ReadOnlySpan<T>)this).ToArray();

        // For `fixed (T* p = span)`.
        public ref T GetPinnableReference() => ref _reference;

        public Enumerator GetEnumerator() => new Enumerator(this);

        public static implicit operator Span<T>(T[] array) => new Span<T>(array);

        public static implicit operator ReadOnlySpan<T>(Span<T> span) => new ReadOnlySpan<T>(ref span._reference, span._length);

        public ref struct Enumerator
        {
            private readonly Span<T> _span;
            private int _index;

            internal Enumerator(Span<T> span)
            {
                _span = span;
                _index = -1;
            }

            public bool MoveNext() => ++_index < _span.Length;

            public ref T Current => ref _span[_index];
        }
    }

    public readonly ref struct ReadOnlySpan<T>
    {
        internal readonly ref T _reference;
        private readonly int _length;

        public ReadOnlySpan(T[] array)
        {
            if (array == null)
            {
                this = default;
                return;
            }
            _reference = ref MemoryMarshal.GetArrayDataReference(array);
            _length = array.Length;
        }

        public ReadOnlySpan(T[] array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0)
                    Environment.FailFast("ReadOnlySpan: range out of bounds");
                this = default;
                return;
            }
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start))
                Environment.FailFast("ReadOnlySpan: range out of bounds");
            _reference = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), start);
            _length = length;
        }

        public unsafe ReadOnlySpan(void* pointer, int length)
        {
            if (length < 0)
                Environment.FailFast("ReadOnlySpan: negative length");
            _reference = ref Unsafe.As<byte, T>(ref *(byte*)pointer);
            _length = length;
        }

        public ReadOnlySpan(ref T reference, int length)
        {
            _reference = ref reference;
            _length = length;
        }

        public ref readonly T this[int index]
        {
            [Intrinsic]
            get
            {
                if ((uint)index >= (uint)_length)
                    Environment.FailFast("ReadOnlySpan: index out of range");
                return ref Unsafe.Add(ref _reference, index);
            }
        }

        public int Length => _length;

        // A span of chars is its text, as in .NET (a new string, never freed);
        // any other is "System.ReadOnlySpan[n]" (.NET names T too: zero mode has no type names). F# calls
        // this for span.ToString(), and without it boxed the span: invalid.
        public override string ToString()
        {
            if (typeof(T) == typeof(char))
                return string.FromChars(new ReadOnlySpan<char>(ref Unsafe.As<T, char>(ref _reference), _length));
            return string.Concat("System.ReadOnlySpan[", _length.ToString(), "]");
        }

        // As in .NET, a span has no value equality or hash: these throw. Here
        // so that F#'s span.Equals(x) and span.GetHashCode() are calls, not a
        // boxed span (invalid code the compiler can't compile).
        [Obsolete("Equals() on ReadOnlySpan will always throw an exception.")]
        public override bool Equals(object obj) =>
            throw new NotSupportedException("Equals() on ReadOnlySpan isn't supported.");

        [Obsolete("GetHashCode() on ReadOnlySpan will always throw an exception.")]
        public override int GetHashCode() =>
            throw new NotSupportedException("GetHashCode() on ReadOnlySpan isn't supported.");

        public bool IsEmpty => _length == 0;

        public static ReadOnlySpan<T> Empty => default;

        public ReadOnlySpan<T> Slice(int start)
        {
            if ((uint)start > (uint)_length)
                Environment.FailFast("ReadOnlySpan: slice out of range");
            return new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, start), _length - start);
        }

        public ReadOnlySpan<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
                Environment.FailFast("ReadOnlySpan: slice out of range");
            return new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, start), length);
        }

        // The ranges may overlap.
        public void CopyTo(Span<T> destination)
        {
            if ((uint)_length > (uint)destination.Length)
                Environment.FailFast("ReadOnlySpan: destination too short");
            SpanHelpers.Memmove(
                ref Unsafe.As<T, byte>(ref destination._reference),
                ref Unsafe.As<T, byte>(ref _reference),
                (nuint)_length * (nuint)Unsafe.SizeOf<T>());
        }

        /// <summary>A new array holding a copy of the elements (an empty span gives Array.Empty). Zero mode never frees it.</summary>
        public T[] ToArray()
        {
            if (_length == 0)
                return Array.Empty<T>();
            T[] array = new T[_length];
            CopyTo(array);
            return array;
        }

        // For `fixed (T* p = span)`.
        public ref readonly T GetPinnableReference() => ref _reference;

        public Enumerator GetEnumerator() => new Enumerator(this);

        public static implicit operator ReadOnlySpan<T>(T[] array) => new ReadOnlySpan<T>(array);

        public ref struct Enumerator
        {
            private readonly ReadOnlySpan<T> _span;
            private int _index;

            internal Enumerator(ReadOnlySpan<T> span)
            {
                _span = span;
                _index = -1;
            }

            public bool MoveNext() => ++_index < _span.Length;

            public ref readonly T Current => ref _span[_index];
        }
    }
}
