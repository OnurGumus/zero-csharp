// What the check scripts (scripts/*-check) need besides the zerocore files
// they compile into an ordinary .NET program: zerocore's object scopes,
// which have nothing to do under .NET's collector.
namespace System.Runtime
{
    internal static class ObjectAllocator
    {
        internal static Scope Lasting() => default;

        internal struct Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
