// The enumeration interfaces. The C# compiler wants them declared before it
// compiles any foreach, even one over an array (which it turns into an
// indexed loop). Arrays implement IEnumerable<T> through Array<T> (Array.cs).

namespace System.Collections
{
    public interface IEnumerable
    {
        IEnumerator GetEnumerator();
    }

    public interface IEnumerator
    {
        object Current { get; }
        bool MoveNext();
        void Reset();
    }
}

namespace System.Collections.Generic
{
    // `allows ref struct`, as in .NET 9 and later: the tensor dimension spans
    // enumerate TensorSpan<T>s.
    public interface IEnumerable<out T> : IEnumerable
        where T : allows ref struct
    {
        new IEnumerator<T> GetEnumerator();
    }

    public interface IEnumerator<out T> : IEnumerator, IDisposable
        where T : allows ref struct
    {
        new T Current { get; }
    }
}
