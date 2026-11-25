// What the Windows C files (runtime.c, start.c, library.c) declare: there
// are no Windows headers here, so the few types, functions and constants they
// use are written out.

#pragma once

typedef unsigned short wchar;
typedef unsigned int u32;
typedef long long i64;
typedef unsigned long long u64;
typedef void *Handle;

#define IMPORT __declspec(dllimport)

IMPORT wchar *GetCommandLineW(void);
IMPORT wchar **CommandLineToArgvW(const wchar *line, int *count);
IMPORT int WideCharToMultiByte(u32 page, u32 flags, const wchar *text, int length,
                               char *out, int size, const char *fallback, int *used);
IMPORT Handle GetProcessHeap(void);
IMPORT void *HeapAlloc(Handle heap, u32 flags, u64 size);
IMPORT Handle GetStdHandle(u32 which);
IMPORT int WriteFile(Handle file, const void *buffer, u32 count, u32 *written, void *overlapped);
IMPORT int ReadFile(Handle file, void *buffer, u32 count, u32 *read, void *overlapped);
IMPORT u32 GetLastError(void);
IMPORT int GetConsoleMode(Handle console, u32 *mode);
IMPORT u32 GetFileType(Handle file);
IMPORT int ReadConsoleInputW(Handle console, void *records, u32 capacity, u32 *count);
IMPORT int PeekConsoleInputW(Handle console, void *records, u32 capacity, u32 *count);
IMPORT int GetNumberOfConsoleInputEvents(Handle console, u32 *count);
IMPORT u32 GetEnvironmentVariableW(const wchar *name, wchar *buffer, u32 size);
IMPORT int MultiByteToWideChar(u32 page, u32 flags, const char *text, int length, wchar *out, int size);
IMPORT int SetConsoleOutputCP(u32 page);
IMPORT int QueryPerformanceCounter(i64 *count);
IMPORT int QueryPerformanceFrequency(i64 *frequency);
IMPORT void GetSystemTimePreciseAsFileTime(u64 *time);
IMPORT int _fseeki64(void *stream, i64 offset, int origin);
IMPORT void *_wfopen(const wchar *path, const wchar *mode);
IMPORT int _wremove(const wchar *path);
IMPORT i64 _ftelli64(void *stream);
IMPORT __declspec(noreturn) void ExitProcess(u32 code);
IMPORT unsigned int _set_abort_behavior(unsigned int flags, unsigned int mask);
IMPORT Handle CreateThread(void *security, u64 stackSize, u32 (*start)(void *), void *argument,
                           u32 flags, u32 *id);
IMPORT u32 WaitForSingleObject(Handle handle, u32 milliseconds);
IMPORT int CloseHandle(Handle handle);
IMPORT int HeapFree(Handle heap, u32 flags, void *memory);
IMPORT u32 TlsAlloc(void);
IMPORT void *TlsGetValue(u32 index);
IMPORT int TlsSetValue(u32 index, void *value);
IMPORT u32 GetActiveProcessorCount(unsigned short group);
IMPORT int SwitchToThread(void);
IMPORT int VirtualProtect(void *address, u64 size, u32 protection, u32 *old);
IMPORT void Sleep(u32 milliseconds);

#define PAGE_NOACCESS 0x01
#define PAGE_READWRITE 0x04
#define CP_UTF8 65001
#define STD_INPUT_HANDLE ((u32)-10)
#define STD_OUTPUT_HANDLE ((u32)-11)
#define STD_ERROR_HANDLE ((u32)-12)
#define ERROR_BROKEN_PIPE 109
#define _WRITE_ABORT_MSG 1
#define _CALL_REPORTFAULT 2
