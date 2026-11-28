using System.Runtime;
using Internal.Runtime;
using System.Runtime.CompilerServices;

namespace System
{
    public interface IEquatable<T>
    {
        bool Equals(T other);
    }

    // Negative if this goes before other, positive if after, zero if either will do.
    public interface IComparable<in T>
    {
        int CompareTo(T other);
    }

    // The same, for any object: F# takes a type that implements it as one that
    // supports comparison (the `comparison` constraint).
    public interface IComparable
    {
        int CompareTo(object obj);
    }

    public delegate int Comparison<in T>(T x, T y);

    // Cultures don't exist here: every provider means the invariant culture.
    public interface IFormatProvider { }

    // A value that takes a format string, as string.Format's "{0:F2}" and
    // $"{x:F2}" pass it.
    public interface IFormattable
    {
        string ToString(string format, IFormatProvider formatProvider);
    }

    // The same, written into a span instead of a new string.
    public interface ISpanFormattable : IFormattable
    {
        bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider);
    }
}

namespace System.Collections.Generic
{
    public interface IEqualityComparer<in T>
    {
        bool Equals(T x, T y);
        int GetHashCode(T obj);
    }

    public interface IComparer<in T>
    {
        int Compare(T x, T y);
    }

    // EqualityComparer<T>.Default and Comparer<T>.Default, without reflection.
    //
    // .NET's NativeAOT compiler replaces the body of an [Intrinsic] Create() in
    // these two classes, for each T the program uses, with `new` of the class
    // that fits T. It looks the classes up by name in System.Private.CoreLib,
    // which is what bflat takes zerocore for:
    //
    //   T is string (EqualityComparer only)          StringEqualityComparer
    //   T implements IEquatable<T> / IComparable<T>  Generic{Equality}Comparer<T>
    //   T is an enum                                 Enum{Equality}Comparer<T>
    //   T is a Nullable<U>                           Nullable{Equality}Comparer<U>
    //   anything else                                Object{Equality}Comparer<T>
    //
    // so a struct that implements IEquatable<T> is compared by a direct call to
    // its Equals(T), with no boxing (which in zero mode would allocate memory
    // that is never freed). The compiler also knows which class Default returns,
    // so EqualityComparer<T>.Default.Equals(a, b) becomes that direct call.
    // Create's own body runs only where the compiler can't tell T (shared code).
    public abstract class EqualityComparer<T> : IEqualityComparer<T>
    {
        // Made by a static constructor, so it is allocated outside any object scope
        // open when Default is first used (lib/Arena.cs), where a cache filled
        // lazily would be freed with the scope.
        private static class DefaultHolder
        {
            internal static readonly EqualityComparer<T> Value = Create();
        }

        public static EqualityComparer<T> Default
        {
            [Intrinsic]
            get => DefaultHolder.Value;
        }

        [Intrinsic]
        private static EqualityComparer<T> Create() => new ObjectEqualityComparer<T>();

        public abstract bool Equals(T x, T y);

        public abstract int GetHashCode(T obj);
    }

    public sealed class GenericEqualityComparer<T> : EqualityComparer<T> where T : IEquatable<T>
    {
        public override bool Equals(T x, T y)
        {
            if (x != null)
                return y != null && x.Equals(y);
            return y == null;
        }

        public override int GetHashCode(T obj) => obj == null ? 0 : obj.GetHashCode();
    }

    public sealed class StringEqualityComparer : EqualityComparer<string>
    {
        public override bool Equals(string x, string y) => string.Equals(x, y);

        public override int GetHashCode(string obj) => obj == null ? 0 : obj.GetHashCode();
    }

    public sealed class NullableEqualityComparer<T> : EqualityComparer<T?> where T : struct
    {
        public override bool Equals(T? x, T? y)
        {
            if (x.HasValue)
                return y.HasValue && EqualityComparer<T>.Default.Equals(x.GetValueOrDefault(), y.GetValueOrDefault());
            return !y.HasValue;
        }

        public override int GetHashCode(T? obj) => obj.HasValue ? EqualityComparer<T>.Default.GetHashCode(obj.GetValueOrDefault()) : 0;
    }

    // Compares an enum's value, without the boxing that Enum.Equals(object) needs.
    public sealed class EnumEqualityComparer<T> : EqualityComparer<T> where T : struct, Enum
    {
        public override bool Equals(T x, T y) => EnumValue.Bits(ref x) == EnumValue.Bits(ref y);

        public override int GetHashCode(T obj)
        {
            ulong bits = EnumValue.Bits(ref obj);
            return (int)bits ^ (int)(bits >> 32);
        }
    }

    // For a T that implements neither IEquatable<T> nor anything else the
    // compiler picks a comparer for. A class is compared by its Equals(object).
    // A struct is compared and hashed by its bytes, as zerocore's
    // ValueType.Equals and GetHashCode do, but here without boxing it (which
    // would allocate memory that is never freed). So a struct that overrides
    // Equals(object) without implementing IEquatable<T> is still compared by
    // its bytes: implement IEquatable<T> to have yours used.
    public sealed unsafe class ObjectEqualityComparer<T> : EqualityComparer<T>
    {
        public override bool Equals(T x, T y)
        {
            if (default(T) != null) // a struct: the compiler keeps one branch
            {
                uint size = MethodTable.Of<T>()->ValueTypeSize;
                ref byte a = ref Unsafe.As<T, byte>(ref x);
                ref byte b = ref Unsafe.As<T, byte>(ref y);
                for (uint i = 0; i < size; i++)
                {
                    if (Unsafe.Add(ref a, (nint)i) != Unsafe.Add(ref b, (nint)i))
                        return false;
                }
                return true;
            }
            if (x != null)
                return y != null && x.Equals(y);
            return y == null;
        }

        public override int GetHashCode(T obj)
        {
            if (default(T) != null)
                return Number.HashBytes((byte*)Unsafe.AsPointer(ref obj), (int)MethodTable.Of<T>()->ValueTypeSize);
            return obj == null ? 0 : obj.GetHashCode();
        }
    }

    // Null goes before everything else, as in .NET.
    public abstract class Comparer<T> : IComparer<T>
    {
        // Made by a static constructor, so it is allocated outside any object scope
        // open when Default is first used (lib/Arena.cs), where a cache filled
        // lazily would be freed with the scope.
        private static class DefaultHolder
        {
            internal static readonly Comparer<T> Value = Create();
        }

        public static Comparer<T> Default
        {
            [Intrinsic]
            get => DefaultHolder.Value;
        }

        [Intrinsic]
        private static Comparer<T> Create() => new ObjectComparer<T>();

        // A comparer that calls comparison. Calling it goes through a delegate;
        // a struct that implements IComparer<T> is faster where a method takes one.
        public static Comparer<T> Create(Comparison<T> comparison)
        {
            if (comparison == null)
                Environment.FailFast("Comparer.Create: comparison is null");
            return new ComparisonComparer<T>(comparison);
        }

        public abstract int Compare(T x, T y);
    }

    public sealed class GenericComparer<T> : Comparer<T> where T : IComparable<T>
    {
        public override int Compare(T x, T y)
        {
            if (x != null)
                return y != null ? x.CompareTo(y) : 1;
            return y != null ? -1 : 0;
        }
    }

    public sealed class NullableComparer<T> : Comparer<T?> where T : struct
    {
        public override int Compare(T? x, T? y)
        {
            if (x.HasValue)
                return y.HasValue ? Comparer<T>.Default.Compare(x.GetValueOrDefault(), y.GetValueOrDefault()) : 1;
            return y.HasValue ? -1 : 0;
        }
    }

    // By the underlying value, as a signed or an unsigned number as the enum's type is.
    public sealed unsafe class EnumComparer<T> : Comparer<T> where T : struct, Enum
    {
        public override int Compare(T x, T y)
        {
            ulong a = EnumValue.Bits(ref x), b = EnumValue.Bits(ref y);
            if (a == b)
                return 0;
            if (EnumValue.IsSigned<T>())
                return EnumValue.SignExtend<T>(a) < EnumValue.SignExtend<T>(b) ? -1 : 1;
            return a < b ? -1 : 1;
        }
    }

    // T's IComparable<T>, if it has one; the compiler only picks this class for
    // a T that doesn't, or in shared code it can't see T in.
    public sealed class ObjectComparer<T> : Comparer<T>
    {
        public override int Compare(T x, T y)
        {
            if (x == null)
                return y == null ? 0 : -1;
            if (y == null)
                return 1;
            if (x is IComparable<T> comparable)
                return comparable.CompareTo(y);
            Environment.FailFast("Comparer<T>.Default: T doesn't implement IComparable<T>");
            return 0;
        }
    }

    internal sealed class ComparisonComparer<T> : Comparer<T>
    {
        private readonly Comparison<T> _comparison;

        public ComparisonComparer(Comparison<T> comparison) => _comparison = comparison;

        public override int Compare(T x, T y) => _comparison(x, y);
    }

    internal static unsafe class EnumValue
    {
        // The value's bits, zero-extended to 64.
        public static ulong Bits<T>(ref T value)
        {
            switch (Unsafe.SizeOf<T>())
            {
                case 1: return Unsafe.As<T, byte>(ref value);
                case 2: return Unsafe.As<T, ushort>(ref value);
                case 4: return Unsafe.As<T, uint>(ref value);
                default: return Unsafe.As<T, ulong>(ref value);
            }
        }

        public static bool IsSigned<T>()
        {
            uint kind = MethodTable.Of<T>()->ElementTypeKind;
            return kind == 4 || kind == 6 || kind == 8 || kind == 10 || kind == 12;
        }

        public static long SignExtend<T>(ulong bits)
        {
            switch (Unsafe.SizeOf<T>())
            {
                case 1: return (sbyte)bits;
                case 2: return (short)bits;
                case 4: return (int)bits;
                default: return (long)bits;
            }
        }
    }
}
