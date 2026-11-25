// Storing an object reference, as x64 code compiled by bflat does it.
//
// A garbage collector needs to know about every such store, so the compiler
// routes them through these functions, which must leave every register alone
// except the ones noted. That contract is why they're in assembly. Without a
// garbage collector they only have to do the store.
//
// (Comments after an instruction start with # here: this assembler only takes
// // at the start of a line.)

    .intel_syntax noprefix
    .text

// rdi: address to store to
// rsi: the object reference
    .global RhpAssignRef
    .global RhpCheckedAssignRef
    .type RhpAssignRef, @function
    .type RhpCheckedAssignRef, @function
RhpAssignRef:
RhpCheckedAssignRef:
    mov     [rdi], rsi
    ret

// Copying a reference from one place to another (struct copies).
// rdi: address to store to (advanced by 8 on return)
// rsi: address to load from (advanced by 8 on return)
// rcx: scratch
    .global RhpByRefAssignRef
    .type RhpByRefAssignRef, @function
RhpByRefAssignRef:
    mov     rcx, [rsi]
    mov     [rdi], rcx
    add     rdi, 8
    add     rsi, 8
    ret

// Calling an interface method on an object whose type isn't known at build
// time (see Runtime/InterfaceDispatch.cs). The call arrives here with the
// method's own arguments in place, plus:
// rdi: the object
// r11: the call site's dispatch cell
//
// Fast path: after its first call, the cell's second word points at a cache
// of four entries, each a class and the method it resolved to. If the object's
// class is among them, jump. rax and r10 are free to use here.
    .global RhpInitialDynamicInterfaceDispatch
    .type RhpInitialDynamicInterfaceDispatch, @function
RhpInitialDynamicInterfaceDispatch:
    test    rdi, rdi
    jz      1f                      # null: let the slow path report it
    mov     r10, [r11 + 8]
    test    r10b, 3
    jnz     1f                      # not a cache pointer yet
    mov     rax, [rdi]              # the object's class
    cmp     rax, [r10]
    je      2f
    add     r10, 16                 # next entry
    cmp     rax, [r10]
    je      2f
    add     r10, 16
    cmp     rax, [r10]
    je      2f
    add     r10, 16
    cmp     rax, [r10]
    jne     1f
2:
    mov     r10, [r10 + 8]
    test    r10, r10
    jz      1f                      # entry claimed but not filled in yet
    jmp     r10

// Slow path: find the method, put every argument register back, and jump to
// it. 200 bytes: 8 integer registers, 8 vector registers, and 8 to keep the
// stack 16-byte aligned for the call.
1:
    sub     rsp, 200
    mov     [rsp], rdi
    mov     [rsp + 8], rsi
    mov     [rsp + 16], rdx
    mov     [rsp + 24], rcx
    mov     [rsp + 32], r8
    mov     [rsp + 40], r9
    mov     [rsp + 48], rax
    mov     [rsp + 56], r10
    movups  [rsp + 64], xmm0
    movups  [rsp + 80], xmm1
    movups  [rsp + 96], xmm2
    movups  [rsp + 112], xmm3
    movups  [rsp + 128], xmm4
    movups  [rsp + 144], xmm5
    movups  [rsp + 160], xmm6
    movups  [rsp + 176], xmm7
    mov     rsi, r11
    call    RhpResolveInterfaceMethod
    mov     r11, rax
    mov     rdi, [rsp]
    mov     rsi, [rsp + 8]
    mov     rdx, [rsp + 16]
    mov     rcx, [rsp + 24]
    mov     r8, [rsp + 32]
    mov     r9, [rsp + 40]
    mov     rax, [rsp + 48]
    mov     r10, [rsp + 56]
    movups  xmm0, [rsp + 64]
    movups  xmm1, [rsp + 80]
    movups  xmm2, [rsp + 96]
    movups  xmm3, [rsp + 112]
    movups  xmm4, [rsp + 128]
    movups  xmm5, [rsp + 144]
    movups  xmm6, [rsp + 160]
    movups  xmm7, [rsp + 176]
    add     rsp, 200
    jmp     r11

// RhpStackProbe: the compiler calls it in the prolog of a function whose
// frame is larger than a few pages, to touch each page in turn so the stack
// grows into them in order (the guard page below the stack is hit before
// anything past it). As in .NET's runtime for Linux, it moves rsp down as it
// goes: Linux refuses to grow the stack for an access far below rsp.
// r11: the lowest address of the frame being allocated (kept)
// rsp: somewhere on the last page already touched
    .globl RhpStackProbe
RhpStackProbe:
    push    rbp
    mov     rbp, rsp
    and     rsp, -4096              # the start of the last page touched
4:
    sub     rsp, 4096
    test    dword ptr [rsp], eax    # touch the next page down
    cmp     rsp, r11
    jg      4b
    mov     rsp, rbp
    pop     rbp
    ret

// Fibers (../lib/Fibers.cs): switching from one stack to another.
//
// ZeroFiberSwitch saves what a function must keep for its caller (the
// callee-saved registers, and the SSE and x87 control words) on the current
// stack, stores the stack pointer in *rdi, and resumes the stack rsi points
// at, which an earlier switch, or ZeroFiberInit, left the same way.
// rdi: where to store this stack's pointer
// rsi: the stack pointer to resume
    .global ZeroFiberSwitch
    .type ZeroFiberSwitch, @function
ZeroFiberSwitch:
    push    rbp
    push    rbx
    push    r12
    push    r13
    push    r14
    push    r15
    sub     rsp, 8
    stmxcsr [rsp]
    fnstcw  [rsp + 4]
    mov     [rdi], rsp
    mov     rsp, rsi
    ldmxcsr [rsp]
    fldcw   [rsp + 4]
    add     rsp, 8
    pop     r15
    pop     r14
    pop     r13
    pop     r12
    pop     rbx
    pop     rbp
    ret

// Prepares a new stack so that switching to it calls entry(argument) there:
// the frame ZeroFiberSwitch would have left, returning to ZeroFiberStart.
// rdi: the stack's top; rsi: its bottom (unused here: see win-x64.S);
// rdx: entry; rcx: argument
// rax: the stack pointer to give ZeroFiberSwitch
    .global ZeroFiberInit
    .type ZeroFiberInit, @function
ZeroFiberInit:
    and     rdi, -16
    lea     rax, [rdi - 64]
    mov     dword ptr [rax], 0x1F80     # mxcsr: the default
    mov     dword ptr [rax + 4], 0x037F # x87 control word: the default
    mov     qword ptr [rax + 8], 0      # r15
    mov     qword ptr [rax + 16], 0     # r14
    mov     [rax + 24], rdx             # r13: entry
    mov     [rax + 32], rcx             # r12: argument
    mov     qword ptr [rax + 40], 0     # rbx
    mov     qword ptr [rax + 48], 0     # rbp
    lea     rdx, [rip + ZeroFiberStart]
    mov     [rax + 56], rdx             # where the switch returns to
    ret

// rsp is the stack's top here, 16-byte aligned, as a call needs.
// The entry never returns: it switches away for good.
ZeroFiberStart:
    mov     rdi, r12
    call    r13
    ud2

    .section .note.GNU-stack, "", @progbits
