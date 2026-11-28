// Compiled into every program dotnet/Faithful builds (see dotnet/Sample/ZeroStartup.cs).
static class ZeroStartup
{
    // lib/'s DllImports are this assembly's: NativeResolver registers for it.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Start() => StressGC();

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
