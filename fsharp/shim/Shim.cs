// BCL types the F# compiler and FSharp.Core expect that zerocore doesn't define.
namespace System
{
    using System.Collections;

    // The tuples' structural equality, hash and order, item by item through
    // the comparer given, as .NET's tuples have: F#'s = on a list of tuples,
    // and a Set or Map with tuple keys, compare boxed tuples this way.
    internal static class TupleComparers
    {
        internal static readonly IEqualityComparer Equality = new DefaultEquality();
        internal static readonly IComparer Comparison = new DefaultComparison();

        internal static int Combine(int h1, int h2) => ((h1 << 5) + h1) ^ h2;

        private sealed class DefaultEquality : IEqualityComparer
        {
            public new bool Equals(object x, object y) => x == null ? y == null : x.Equals(y);
            public int GetHashCode(object obj) => obj == null ? 0 : obj.GetHashCode();
        }

        private sealed class DefaultComparison : IComparer
        {
            public int Compare(object x, object y)
                => x == null ? (y == null ? 0 : -1) : y == null ? 1 : ((IComparable)x).CompareTo(y);
        }
    }

    public class Tuple<T1> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1> t && comparer.Equals(Item1, t.Item1);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => comparer.GetHashCode(Item1);
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1>)other;
            return comparer.Compare(Item1, t.Item1);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1) { Item1 = item1; }
        public T1 Item1 { get; }
        public override string ToString() => "(" + Show(Item1) + ")";
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
    public class Tuple<T1, T2> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1, T2> t && comparer.Equals(Item1, t.Item1) && comparer.Equals(Item2, t.Item2);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => TupleComparers.Combine(comparer.GetHashCode(Item1), comparer.GetHashCode(Item2));
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1, T2>)other;
            int c;
            if ((c = comparer.Compare(Item1, t.Item1)) != 0) return c;
            return comparer.Compare(Item2, t.Item2);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1, T2 item2) { Item1 = item1; Item2 = item2; }
        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public override string ToString() => "(" + Show(Item1) + ", " + Show(Item2) + ")";
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
    public class Tuple<T1, T2, T3> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1, T2, T3> t && comparer.Equals(Item1, t.Item1) && comparer.Equals(Item2, t.Item2) && comparer.Equals(Item3, t.Item3);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => TupleComparers.Combine(TupleComparers.Combine(comparer.GetHashCode(Item1), comparer.GetHashCode(Item2)), comparer.GetHashCode(Item3));
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1, T2, T3>)other;
            int c;
            if ((c = comparer.Compare(Item1, t.Item1)) != 0) return c;
            if ((c = comparer.Compare(Item2, t.Item2)) != 0) return c;
            return comparer.Compare(Item3, t.Item3);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1, T2 item2, T3 item3) { Item1 = item1; Item2 = item2; Item3 = item3; }
        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public T3 Item3 { get; }
        public override string ToString() => "(" + Show(Item1) + ", " + Show(Item2) + ", " + Show(Item3) + ")";
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
    public class Tuple<T1, T2, T3, T4> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1, T2, T3, T4> t && comparer.Equals(Item1, t.Item1) && comparer.Equals(Item2, t.Item2) && comparer.Equals(Item3, t.Item3) && comparer.Equals(Item4, t.Item4);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(comparer.GetHashCode(Item1), comparer.GetHashCode(Item2)), comparer.GetHashCode(Item3)), comparer.GetHashCode(Item4));
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1, T2, T3, T4>)other;
            int c;
            if ((c = comparer.Compare(Item1, t.Item1)) != 0) return c;
            if ((c = comparer.Compare(Item2, t.Item2)) != 0) return c;
            if ((c = comparer.Compare(Item3, t.Item3)) != 0) return c;
            return comparer.Compare(Item4, t.Item4);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1, T2 item2, T3 item3, T4 item4) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; }
        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public T3 Item3 { get; }
        public T4 Item4 { get; }
        public override string ToString() => "(" + Show(Item1) + ", " + Show(Item2) + ", " + Show(Item3) + ", " + Show(Item4) + ")";
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
    public class Tuple<T1, T2, T3, T4, T5> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1, T2, T3, T4, T5> t && comparer.Equals(Item1, t.Item1) && comparer.Equals(Item2, t.Item2) && comparer.Equals(Item3, t.Item3) && comparer.Equals(Item4, t.Item4) && comparer.Equals(Item5, t.Item5);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(comparer.GetHashCode(Item1), comparer.GetHashCode(Item2)), comparer.GetHashCode(Item3)), comparer.GetHashCode(Item4)), comparer.GetHashCode(Item5));
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1, T2, T3, T4, T5>)other;
            int c;
            if ((c = comparer.Compare(Item1, t.Item1)) != 0) return c;
            if ((c = comparer.Compare(Item2, t.Item2)) != 0) return c;
            if ((c = comparer.Compare(Item3, t.Item3)) != 0) return c;
            if ((c = comparer.Compare(Item4, t.Item4)) != 0) return c;
            return comparer.Compare(Item5, t.Item5);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; }
        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public T3 Item3 { get; }
        public T4 Item4 { get; }
        public T5 Item5 { get; }
        public override string ToString() => "(" + Show(Item1) + ", " + Show(Item2) + ", " + Show(Item3) + ", " + Show(Item4) + ", " + Show(Item5) + ")";
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
    public class Tuple<T1, T2, T3, T4, T5, T6> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1, T2, T3, T4, T5, T6> t && comparer.Equals(Item1, t.Item1) && comparer.Equals(Item2, t.Item2) && comparer.Equals(Item3, t.Item3) && comparer.Equals(Item4, t.Item4) && comparer.Equals(Item5, t.Item5) && comparer.Equals(Item6, t.Item6);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(comparer.GetHashCode(Item1), comparer.GetHashCode(Item2)), comparer.GetHashCode(Item3)), comparer.GetHashCode(Item4)), comparer.GetHashCode(Item5)), comparer.GetHashCode(Item6));
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1, T2, T3, T4, T5, T6>)other;
            int c;
            if ((c = comparer.Compare(Item1, t.Item1)) != 0) return c;
            if ((c = comparer.Compare(Item2, t.Item2)) != 0) return c;
            if ((c = comparer.Compare(Item3, t.Item3)) != 0) return c;
            if ((c = comparer.Compare(Item4, t.Item4)) != 0) return c;
            if ((c = comparer.Compare(Item5, t.Item5)) != 0) return c;
            return comparer.Compare(Item6, t.Item6);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; Item6 = item6; }
        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public T3 Item3 { get; }
        public T4 Item4 { get; }
        public T5 Item5 { get; }
        public T6 Item6 { get; }
        public override string ToString() => "(" + Show(Item1) + ", " + Show(Item2) + ", " + Show(Item3) + ", " + Show(Item4) + ", " + Show(Item5) + ", " + Show(Item6) + ")";
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
    public class Tuple<T1, T2, T3, T4, T5, T6, T7> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1, T2, T3, T4, T5, T6, T7> t && comparer.Equals(Item1, t.Item1) && comparer.Equals(Item2, t.Item2) && comparer.Equals(Item3, t.Item3) && comparer.Equals(Item4, t.Item4) && comparer.Equals(Item5, t.Item5) && comparer.Equals(Item6, t.Item6) && comparer.Equals(Item7, t.Item7);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(comparer.GetHashCode(Item1), comparer.GetHashCode(Item2)), comparer.GetHashCode(Item3)), comparer.GetHashCode(Item4)), comparer.GetHashCode(Item5)), comparer.GetHashCode(Item6)), comparer.GetHashCode(Item7));
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1, T2, T3, T4, T5, T6, T7>)other;
            int c;
            if ((c = comparer.Compare(Item1, t.Item1)) != 0) return c;
            if ((c = comparer.Compare(Item2, t.Item2)) != 0) return c;
            if ((c = comparer.Compare(Item3, t.Item3)) != 0) return c;
            if ((c = comparer.Compare(Item4, t.Item4)) != 0) return c;
            if ((c = comparer.Compare(Item5, t.Item5)) != 0) return c;
            if ((c = comparer.Compare(Item6, t.Item6)) != 0) return c;
            return comparer.Compare(Item7, t.Item7);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; Item6 = item6; Item7 = item7; }
        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public T3 Item3 { get; }
        public T4 Item4 { get; }
        public T5 Item5 { get; }
        public T6 Item6 { get; }
        public T7 Item7 { get; }
        public override string ToString() => "(" + Show(Item1) + ", " + Show(Item2) + ", " + Show(Item3) + ", " + Show(Item4) + ", " + Show(Item5) + ", " + Show(Item6) + ", " + Show(Item7) + ")";
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
    public class Tuple<T1, T2, T3, T4, T5, T6, T7, TRest> : IStructuralEquatable, IStructuralComparable, IComparable
    {
        public override bool Equals(object obj) => ((IStructuralEquatable)this).Equals(obj, TupleComparers.Equality);
        public override int GetHashCode() => ((IStructuralEquatable)this).GetHashCode(TupleComparers.Equality);
        bool IStructuralEquatable.Equals(object other, IEqualityComparer comparer)
            => other is Tuple<T1, T2, T3, T4, T5, T6, T7, TRest> t && comparer.Equals(Item1, t.Item1) && comparer.Equals(Item2, t.Item2) && comparer.Equals(Item3, t.Item3) && comparer.Equals(Item4, t.Item4) && comparer.Equals(Item5, t.Item5) && comparer.Equals(Item6, t.Item6) && comparer.Equals(Item7, t.Item7) && comparer.Equals(Rest, t.Rest);
        int IStructuralEquatable.GetHashCode(IEqualityComparer comparer) => TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(TupleComparers.Combine(comparer.GetHashCode(Item1), comparer.GetHashCode(Item2)), comparer.GetHashCode(Item3)), comparer.GetHashCode(Item4)), comparer.GetHashCode(Item5)), comparer.GetHashCode(Item6)), comparer.GetHashCode(Item7)), comparer.GetHashCode(Rest));
        int IStructuralComparable.CompareTo(object other, IComparer comparer)
        {
            if (other == null) return 1;
            var t = (Tuple<T1, T2, T3, T4, T5, T6, T7, TRest>)other;
            int c;
            if ((c = comparer.Compare(Item1, t.Item1)) != 0) return c;
            if ((c = comparer.Compare(Item2, t.Item2)) != 0) return c;
            if ((c = comparer.Compare(Item3, t.Item3)) != 0) return c;
            if ((c = comparer.Compare(Item4, t.Item4)) != 0) return c;
            if ((c = comparer.Compare(Item5, t.Item5)) != 0) return c;
            if ((c = comparer.Compare(Item6, t.Item6)) != 0) return c;
            if ((c = comparer.Compare(Item7, t.Item7)) != 0) return c;
            return comparer.Compare(Rest, t.Rest);
        }
        int IComparable.CompareTo(object obj) => ((IStructuralComparable)this).CompareTo(obj, TupleComparers.Comparison);
        public Tuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, TRest rest) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; Item6 = item6; Item7 = item7; Rest = rest; }
        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public T3 Item3 { get; }
        public T4 Item4 { get; }
        public T5 Item5 { get; }
        public T6 Item6 { get; }
        public T7 Item7 { get; }
        public TRest Rest { get; }
        public override string ToString()
        {
            string rest = Show(Rest);
            return "(" + Show(Item1) + ", " + Show(Item2) + ", " + Show(Item3) + ", " + Show(Item4) + ", " + Show(Item5) + ", " + Show(Item6) + ", " + Show(Item7) + ", " + rest.Substring(1);
        }
        private static string Show(object value) => value == null ? "null" : value.ToString();
    }
}

namespace System.Collections
{
    public interface ICollection : IEnumerable { int Count { get; } }
    public interface IList : ICollection { object this[int index] { get; set; } }
    public interface IComparer { int Compare(object x, object y); }
    public interface IEqualityComparer { bool Equals(object x, object y); int GetHashCode(object obj); }
    public interface IStructuralEquatable { bool Equals(object other, IEqualityComparer comparer); int GetHashCode(IEqualityComparer comparer); }
    public interface IStructuralComparable { int CompareTo(object other, IComparer comparer); }
}

namespace System.Collections.Generic
{
    public interface ICollection<T> : IEnumerable<T>
    {
        int Count { get; }
        bool IsReadOnly { get; }
        void Add(T item);
        void Clear();
        bool Contains(T item);
        void CopyTo(T[] array, int arrayIndex);
        bool Remove(T item);
    }
    public interface IList<T> : ICollection<T>
    {
        T this[int index] { get; set; }
        int IndexOf(T item);
        void Insert(int index, T item);
        void RemoveAt(int index);
    }
    public interface IReadOnlyCollection<out T> : IEnumerable<T> { int Count { get; } }
    public interface IReadOnlyList<out T> : IReadOnlyCollection<T> { T this[int index] { get; } }
    public class KeyNotFoundException : Exception
    {
        public KeyNotFoundException() : base("The given key was not present in the dictionary.") { }
        public KeyNotFoundException(string message) : base(message) { }
    }
    public readonly struct KeyValuePair<TKey, TValue>
    {
        public KeyValuePair(TKey key, TValue value) { Key = key; Value = value; }
        public TKey Key { get; }
        public TValue Value { get; }
        public void Deconstruct(out TKey key, out TValue value) { key = Key; value = Value; }
        public override string ToString()
        {
            object key = Key, value = Value;
            return "[" + (key == null ? "" : key.ToString()) + ", " + (value == null ? "" : value.ToString()) + "]";
        }
    }
}

namespace System.ComponentModel
{
    public enum EditorBrowsableState { Always, Never, Advanced }
    [AttributeUsage(AttributeTargets.All)]
    public sealed class EditorBrowsableAttribute : Attribute
    {
        public EditorBrowsableAttribute(EditorBrowsableState state) { State = state; }
        public EditorBrowsableState State { get; }
    }
}

namespace System.Diagnostics
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class DebuggerNonUserCodeAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)]
    public sealed class DebuggerHiddenAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)]
    public sealed class DebuggerStepThroughAttribute : Attribute { }
    public enum DebuggerBrowsableState { Never = 0, Collapsed = 2, RootHidden = 3 }
    [AttributeUsage(AttributeTargets.All)]
    public sealed class DebuggerBrowsableAttribute : Attribute
    {
        public DebuggerBrowsableAttribute(DebuggerBrowsableState state) { State = state; }
        public DebuggerBrowsableState State { get; }
    }
    [AttributeUsage(AttributeTargets.All)]
    public sealed class DebuggerDisplayAttribute : Attribute
    {
        public DebuggerDisplayAttribute(string value) { Value = value; }
        public string Value { get; }
        public string Type { get; set; }
    }
    public sealed class DebuggableAttribute : Attribute
    {
        [Flags] public enum DebuggingModes { None = 0, Default = 1, DisableOptimizations = 256, IgnoreSymbolStoreSequencePoints = 2, EnableEditAndContinue = 4 }
        public DebuggableAttribute(DebuggingModes modes) { }
        public DebuggableAttribute(bool isJITTrackingEnabled, bool isJITOptimizerDisabled) { }
    }
}

namespace System.Diagnostics
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class DebuggerTypeProxyAttribute : Attribute
    {
        public DebuggerTypeProxyAttribute(string typeName) { }
        public DebuggerTypeProxyAttribute(Type type) { }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [Flags]
    public enum DynamicallyAccessedMemberTypes
    {
        None = 0, PublicParameterlessConstructor = 0x0001, PublicConstructors = 0x0003, NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008, NonPublicMethods = 0x0010, PublicFields = 0x0020, NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080, NonPublicNestedTypes = 0x0100, PublicProperties = 0x0200, NonPublicProperties = 0x0400,
        PublicEvents = 0x0800, NonPublicEvents = 0x1000, Interfaces = 0x2000, All = -1
    }
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes) { MemberTypes = memberTypes; }
        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public sealed class RequiresDynamicCodeAttribute : Attribute
    {
        public RequiresDynamicCodeAttribute(string message) { Message = message; }
        public string Message { get; }
    }
}
