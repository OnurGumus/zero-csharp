// The native half of dotnet/ZeroCompat on macOS: what zerocore/native's
// assembly gives a zero-mode program, for lib/ under real .NET. Built into
// libzerocompat.dylib with mac/runtime.c (glibc's names and layouts on
// macOS), by dotnet/native/build.sh. ZeroCompat's resolver sends lib/'s
// DllImport("*"), ("libc") and ("libm.so.6") here; what isn't here, dlsym
// finds in libSystem, which this library links.
#include <fcntl.h>

// lib/FileSystem.cs's SystemKind.MacOS.
int ZeroSystem(void) { return 3; }

// fcntl is variadic: C# can't call it directly on arm64 macOS (a variadic
// function's extra arguments go on the stack there).
int ZeroFcntl(int fd, int command, long argument) { return fcntl(fd, command, argument); }
