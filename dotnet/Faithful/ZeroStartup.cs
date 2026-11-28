// Compiled into every program dotnet/Faithful builds (see dotnet/Sample/ZeroStartup.cs).
static class ZeroStartup
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Start() { } // lib/'s DllImports are this assembly's: NativeResolver registers for it
}
