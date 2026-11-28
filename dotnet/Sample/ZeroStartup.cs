// Compiled into every program dotnet/ builds: what a zero-mode program starts
// with, where real .NET starts otherwise. (zerocore's cultures are .NET's
// invariant globalization mode: InvariantGlobalization in Sample.csproj.)
using ZeroCompatNative = NativeResolver;

static class ZeroStartup
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Start()
    {
        // The program's DllImport("libc") and ("libm.so.6"), as lib/'s.
        ZeroCompatNative.Register(typeof(ZeroStartup).Assembly);

        // Async methods' continuations run on the main thread, one at a time,
        // as in zero mode (dotnet/ZeroCompat/Async.cs).
        Fiber.UseThisThreadForContinuations();
    }
}
