using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime;

namespace System
{
    // Every object starts with a pointer to its type's method table. The
    // compiler finds the field by this name, .NET's: the thunk it makes to call
    // a shared generic struct's method on a boxed one reads it.
    public unsafe class Object
    {
        internal MethodTable* m_pEEType;

        public Object() { }

        // What records, anonymous types and EqualityComparer<T> build on.
        // An object is equal only to itself, its hash is its address (objects
        // never move), and with no type names there is nothing better to
        // print than "object".
        public virtual bool Equals(object obj) => (object)this == obj;

        public virtual int GetHashCode()
        {
            object self = this;
            nint address = Unsafe.As<object, nint>(ref self);
            return unchecked((int)(address >> 4) ^ (int)((long)address >> 36));
        }

        public virtual string ToString() => "object";

        public Type GetType() => Type.Of(m_pEEType);

        public static bool ReferenceEquals(object a, object b) => a == b;

        public static bool Equals(object a, object b) => a == b || (a != null && b != null && a.Equals(b));
    }

    // A struct is equal to another of the same type with the same bytes. That
    // is exact for numbers, bools and nested structs of them; a reference
    // field compares by reference, where .NET would call its Equals (two
    // equal strings at different addresses differ here). Records and
    // IEquatable<T> implementations compare field by field instead.
    public abstract unsafe class ValueType
    {
        public override bool Equals(object obj)
        {
            if (obj == null || obj.m_pEEType != m_pEEType)
                return false;
            uint size = m_pEEType->ValueTypeSize;
            ref byte mine = ref Unsafe.As<RawData>(this).Data;
            ref byte theirs = ref Unsafe.As<RawData>(obj).Data;
            for (uint i = 0; i < size; i++)
            {
                if (Unsafe.Add(ref mine, (nint)i) != Unsafe.Add(ref theirs, (nint)i))
                    return false;
            }
            return true;
        }

        public override int GetHashCode()
            => Number.HashBytes((byte*)Unsafe.AsPointer(ref Unsafe.As<RawData>(this).Data), (int)m_pEEType->ValueTypeSize);
    }

    // With no names to look up, an enum prints its number.
    public abstract unsafe class Enum : ValueType
    {
        public override string ToString()
        {
            ref byte data = ref Unsafe.As<RawData>(this).Data;
            switch (m_pEEType->ValueTypeSize)
            {
                case 1: return Number.FormatInt64(Unsafe.As<byte, sbyte>(ref data));
                case 2: return Number.FormatInt64(Unsafe.As<byte, short>(ref data));
                case 8: return Number.FormatInt64(Unsafe.As<byte, long>(ref data));
                default: return Number.FormatInt64(Unsafe.As<byte, int>(ref data));
            }
        }
    }

    // The fields have .NET's names: the compiler looks up "value" to lay out
    // a boxed Nullable<T>'s method table.
    public struct Nullable<T> where T : struct
    {
        private readonly bool hasValue;
        private T value;

        public Nullable(T value)
        {
            hasValue = true;
            this.value = value;
        }

        public readonly bool HasValue => hasValue;

        public readonly T Value
        {
            get
            {
                if (!hasValue)
                    Environment.FailFast("Nullable object must have a value");
                return value;
            }
        }

        public readonly T GetValueOrDefault() => value;

        public readonly T GetValueOrDefault(T defaultValue) => hasValue ? value : defaultValue;

        public static implicit operator T?(T value) => new T?(value);

        public static explicit operator T(T? value) => value.Value;
    }

    public struct RuntimeTypeHandle
    {
        internal nint _value; // the type's MethodTable*
    }

    public struct RuntimeMethodHandle
    {
        private nint _value;
    }

    public struct RuntimeFieldHandle
    {
        private nint _value;
    }

    public interface IDisposable
    {
        void Dispose();
    }

    public struct ValueTuple<T1, T2>
    {
        public T1 Item1;
        public T2 Item2;

        public ValueTuple(T1 item1, T2 item2)
        {
            Item1 = item1;
            Item2 = item2;
        }
    }

    public struct ValueTuple<T1, T2, T3>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;

        public ValueTuple(T1 item1, T2 item2, T3 item3)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
        }
    }

    public struct ValueTuple<T1, T2, T3, T4>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
        }
    }

    public struct ValueTuple<T1, T2, T3, T4, T5>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
        }
    }

    public struct ValueTuple<T1, T2, T3, T4, T5, T6>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
        }
    }

    public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
            Item7 = item7;
        }
    }

    // The fields and the Initialize methods are the compiler's contract for
    // creating and invoking delegates.
    public abstract class Delegate
    {
        private object _firstParameter;
        private object _helperObject;
        private nint _extraFunctionPointerOrData;
        private nint _functionPointer;

        // A delegate to an instance method: call _functionPointer with _firstParameter as `this`.
        private void InitializeClosedInstance(object firstParameter, nint functionPointer)
        {
            if (firstParameter == null)
                Environment.FailFast("Delegate to an instance method needs a non-null target");
            _functionPointer = functionPointer;
            _firstParameter = firstParameter;
        }

        // A delegate to a static method: the thunk receives the delegate as `this`
        // and calls the real method, stored in _extraFunctionPointerOrData.
        private void InitializeOpenStaticThunk(object firstParameter, nint functionPointer, nint functionPointerThunk)
        {
            _firstParameter = this;
            _functionPointer = functionPointerThunk;
            _extraFunctionPointerOrData = functionPointer;
        }

        // A delegate to a static method with its first argument bound (F#'s
        // `fun () -> f x` as an Action, C#'s `obj.ExtensionMethod` as a
        // delegate): the thunk receives the delegate as `this`, and calls the
        // method, in _extraFunctionPointerOrData, with _helperObject first.
        private void InitializeClosedStaticThunk(object firstParameter, nint functionPointer, nint functionPointerThunk)
        {
            _extraFunctionPointerOrData = functionPointer;
            _helperObject = firstParameter;
            _functionPointer = functionPointerThunk;
            _firstParameter = this;
        }
    }

    public abstract class MulticastDelegate : Delegate { }

    public delegate void Action();
    public delegate void Action<in T>(T arg);
    public delegate void Action<in T1, in T2>(T1 arg1, T2 arg2);
    public delegate TResult Func<out TResult>();
    public delegate TResult Func<in T, out TResult>(T arg);
    public delegate TResult Func<in T1, in T2, out TResult>(T1 arg1, T2 arg2);
}
