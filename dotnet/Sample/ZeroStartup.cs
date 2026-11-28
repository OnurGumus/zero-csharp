// Compiled into every program dotnet/ builds: what a zero-mode program starts
// with, where real .NET starts otherwise.
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
        StartInvariant();
        StressGC();
    }

    // zerocore starts in the invariant culture, whatever the system's locale
    // (CultureInfo's own cultures, ~80 of them from ICU's data, are there to
    // switch to): .NET would start in the user's, and format numbers and
    // dates in it. On threads too, which lib/'s fibers are here.
    static void StartInvariant()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = invariant;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = invariant;
        System.Globalization.CultureInfo.CurrentCulture = invariant;
        System.Globalization.CultureInfo.CurrentUICulture = invariant;
    }

    // dotnet/test.sh --gc-stress: .NET's GC collecting and compacting every
    // couple of milliseconds, on a thread of its own, so objects move under
    // the program all the time. Code that kept a raw address of one (zero
    // mode's objects never move) shows itself. (Without the pause the
    // program hardly gets to run.)
    static void StressGC()
    {
        if (System.Environment.GetEnvironmentVariable("ZERO_GC_STRESS") != "1")
            return;
        var thread = new System.Threading.Thread(static () =>
        {
            while (true)
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
                System.Threading.Thread.Sleep(2);
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }
}
