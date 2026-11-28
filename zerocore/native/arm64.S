// Storing an object reference, as arm64 code compiled by bflat does it.
//
// A garbage collector needs to know about every such store, so the compiler
// routes them through these functions and passes arguments in registers of
// its own choosing. That calling convention is why they're in assembly.
// Without a garbage collector they only have to do the store.

    .text

// x14: address to store to (advanced by 8 on return)
// x15: the object reference
    .global RhpAssignRefArm64
    .global RhpCheckedAssignRefArm64
    .type RhpAssignRefArm64, %function
    .type RhpCheckedAssignRefArm64, %function
RhpAssignRefArm64:
RhpCheckedAssignRefArm64:
    str     x15, [x14], #8
    ret

// Copying a reference from one place to another (struct copies).
// x13: address to load from (advanced by 8 on return)
// x14: address to store to (advanced by 8 on return)
// x15: scratch
    .global RhpByRefAssignRefArm64
    .type RhpByRefAssignRefArm64, %function
RhpByRefAssignRefArm64:
    ldr     x15, [x13], #8
    str     x15, [x14], #8
    ret

// Calling an interface method on an object whose type isn't known at build
// time (see Runtime/InterfaceDispatch.cs). The call arrives here with the
// method's own arguments in place, plus:
// x0:  the object
// x11: the call site's dispatch cell
//
// Fast path: after its first call, the cell's second word points at a cache
// of four entries, each a class and the method it resolved to. If the object's
// class is among them, jump. x9, x10 and x12 are free to use here.
    .global RhpInitialDynamicInterfaceDispatch
    .type RhpInitialDynamicInterfaceDispatch, %function
RhpInitialDynamicInterfaceDispatch:
    cbz     x0, 1f                  // null: let the slow path report it
    ldr     x9, [x11, #8]
    tst     x9, #3
    b.ne    1f                      // not a cache pointer yet
    ldr     x10, [x0]               // the object's class
    ldr     x12, [x9]
    cmp     x10, x12
    b.eq    2f
    ldr     x12, [x9, #16]!         // next entry
    cmp     x10, x12
    b.eq    2f
    ldr     x12, [x9, #16]!
    cmp     x10, x12
    b.eq    2f
    ldr     x12, [x9, #16]!
    cmp     x10, x12
    b.ne    1f
2:
    ldr     x9, [x9, #8]
    cbz     x9, 1f                  // entry claimed but not filled in yet
    br      x9

// Slow path: find the method, put every argument register back, and jump to it.
1:
    stp     x29, x30, [sp, #-224]!
    mov     x29, sp
    stp     x0, x1, [sp, #16]
    stp     x2, x3, [sp, #32]
    stp     x4, x5, [sp, #48]
    stp     x6, x7, [sp, #64]
    str     x8, [sp, #80]           // where a large struct result goes
    stp     q0, q1, [sp, #96]
    stp     q2, q3, [sp, #128]
    stp     q4, q5, [sp, #160]
    stp     q6, q7, [sp, #192]
    mov     x1, x11
    bl      RhpResolveInterfaceMethod
    mov     x9, x0
    ldp     x0, x1, [sp, #16]
    ldp     x2, x3, [sp, #32]
    ldp     x4, x5, [sp, #48]
    ldp     x6, x7, [sp, #64]
    ldr     x8, [sp, #80]
    ldp     q0, q1, [sp, #96]
    ldp     q2, q3, [sp, #128]
    ldp     q4, q5, [sp, #160]
    ldp     q6, q7, [sp, #192]
    ldp     x29, x30, [sp], #224
    br      x9

// Fibers (../lib/Fibers.cs): switching from one stack to another.
//
// ZeroFiberSwitch saves what a function must keep for its caller (x19-x30
// and the low halves of v8-v15) on the current stack, stores the stack
// pointer in *x0, and resumes the stack x1 points at, which an earlier switch,
// or ZeroFiberInit, left the same way. x18 is left alone: it is the
// platform's on macOS and Windows.
// x0: where to store this stack's pointer
// x1: the stack pointer to resume
    .globl ZeroFiberSwitch
    .type ZeroFiberSwitch, %function
ZeroFiberSwitch:
    sub     sp, sp, #160
    stp     x19, x20, [sp]
    stp     x21, x22, [sp, #16]
    stp     x23, x24, [sp, #32]
    stp     x25, x26, [sp, #48]
    stp     x27, x28, [sp, #64]
    stp     x29, x30, [sp, #80]
    stp     d8, d9, [sp, #96]
    stp     d10, d11, [sp, #112]
    stp     d12, d13, [sp, #128]
    stp     d14, d15, [sp, #144]
    mov     x9, sp
    str     x9, [x0]
    mov     sp, x1
    ldp     x19, x20, [sp]
    ldp     x21, x22, [sp, #16]
    ldp     x23, x24, [sp, #32]
    ldp     x25, x26, [sp, #48]
    ldp     x27, x28, [sp, #64]
    ldp     x29, x30, [sp, #80]
    ldp     d8, d9, [sp, #96]
    ldp     d10, d11, [sp, #112]
    ldp     d12, d13, [sp, #128]
    ldp     d14, d15, [sp, #144]
    add     sp, sp, #160
    ret

// Prepares a new stack so that switching to it calls entry(argument) there:
// the frame ZeroFiberSwitch would have left, returning to ZeroFiberStart.
// x0: the stack's top; x1: its bottom (unused here); x2: entry; x3: argument
// x0 on return: the stack pointer to give ZeroFiberSwitch
    .globl ZeroFiberInit
    .type ZeroFiberInit, %function
ZeroFiberInit:
    and     x0, x0, #0xfffffffffffffff0
    sub     x0, x0, #160
    mov     x9, x0
    add     x10, x0, #160
1:
    stp     xzr, xzr, [x9], #16
    cmp     x9, x10
    b.ne    1b
    stp     x3, x2, [x0]            // x19: argument, x20: entry
    adr     x9, ZeroFiberStart
    str     x9, [x0, #88]           // x30: where the switch returns to
    ret

// sp is the stack's top here, 16-byte aligned.
// The entry never returns: it switches away for good.
ZeroFiberStart:
    mov     x0, x19
    blr     x20
    brk     #1

// Catch.Run (../Runtime/Catch.cs): ZeroCatchCall saves the same frame as
// ZeroFiberSwitch, stores its stack pointer in *x0 and calls x1(x2). When the
// callback returns, or a throw inside it calls ZeroCatchJump with that stack
// pointer, the frame comes back and ZeroCatchCall returns.
// x0: where to store the frame's stack pointer; x1: callback; x2: its argument
    .globl ZeroCatchCall
    .type ZeroCatchCall, %function
ZeroCatchCall:
    sub     sp, sp, #160
    stp     x19, x20, [sp]
    stp     x21, x22, [sp, #16]
    stp     x23, x24, [sp, #32]
    stp     x25, x26, [sp, #48]
    stp     x27, x28, [sp, #64]
    stp     x29, x30, [sp, #80]
    stp     d8, d9, [sp, #96]
    stp     d10, d11, [sp, #112]
    stp     d12, d13, [sp, #128]
    stp     d14, d15, [sp, #144]
    mov     x9, sp
    str     x9, [x0]
    mov     x0, x2
    blr     x1
    mov     x0, sp

// x0: the stack pointer ZeroCatchCall stored
    .globl ZeroCatchJump
    .type ZeroCatchJump, %function
ZeroCatchJump:
    mov     sp, x0
    ldp     x19, x20, [sp]
    ldp     x21, x22, [sp, #16]
    ldp     x23, x24, [sp, #32]
    ldp     x25, x26, [sp, #48]
    ldp     x27, x28, [sp, #64]
    ldp     x29, x30, [sp, #80]
    ldp     d8, d9, [sp, #96]
    ldp     d10, d11, [sp, #112]
    ldp     d12, d13, [sp, #128]
    ldp     d14, d15, [sp, #144]
    add     sp, sp, #160
    ret


// Which system this is, for lib/FileSystem.cs: 2, Linux on arm64. (1 Linux x64,
// 2 Linux arm64, 3 macOS, 4 Windows, 5 UEFI, 6 WebAssembly, 7 DOS,
// 8 a microcontroller, 9 the Commodore 64, 10 Android.) Weak: Android
// programs link android/runtime.c, whose ZeroSystem says 10.
    .text
    .weak ZeroSystem
    .type ZeroSystem, %function
ZeroSystem:
    mov     w0, #2
    ret

// ZeroRootCertificate(previous, data, length), for lib/CryptoX509Chain.cs:
// the system's trusted roots one at a time. Linux and Android keep them in
// files, which X509Chain reads itself, so here there are none: null.
    .global ZeroRootCertificate
    .type ZeroRootCertificate, %function
ZeroRootCertificate:
    mov     x0, #0
    ret

// fcntl(fd, command, argument), for lib/Sockets.cs. fcntl is variadic; on
// Linux a variadic call passes its arguments as any other does.
    .text
    .globl ZeroFcntl
    .type ZeroFcntl, %function
ZeroFcntl:
    b       fcntl

    .section .note.GNU-stack, "", %progbits
