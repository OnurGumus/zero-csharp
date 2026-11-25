// x64.S for Windows: the same routines, for the Windows x64 calling
// convention (arguments in rcx, rdx, r8, r9; 32 bytes of "shadow space" the
// caller reserves for the callee; xmm6-xmm15 kept by the callee), plus the
// stack probe Windows code needs. Assembled by clang as a COFF object.
//
// (Comments after an instruction start with # here.)

    .intel_syntax noprefix
    .text

// Storing an object reference (see x64.S).
// rcx: address to store to
// rdx: the object reference
    .globl RhpAssignRef
    .globl RhpCheckedAssignRef
RhpAssignRef:
RhpCheckedAssignRef:
    mov     [rcx], rdx
    ret

// Copying a reference from one place to another (struct copies). The compiler
// uses rdi and rsi for this one on Windows too.
// rdi: address to store to (advanced by 8 on return)
// rsi: address to load from (advanced by 8 on return)
// rcx: scratch
    .globl RhpByRefAssignRef
RhpByRefAssignRef:
    mov     rcx, [rsi]
    mov     [rdi], rcx
    add     rdi, 8
    add     rsi, 8
    ret

// Calling an interface method on an object whose type isn't known at build
// time (see x64.S and Runtime/InterfaceDispatch.cs).
// rcx: the object
// r11: the call site's dispatch cell
    .globl RhpInitialDynamicInterfaceDispatch
RhpInitialDynamicInterfaceDispatch:
    test    rcx, rcx
    jz      1f                      # null: let the slow path report it
    mov     r10, [r11 + 8]
    test    r10b, 3
    jnz     1f                      # not a cache pointer yet
    mov     rax, [rcx]              # the object's class
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
// it. 152 bytes: 32 of shadow space for the call, 6 integer registers, 4
// vector registers, and 8 to keep the stack 16-byte aligned. (The callee
// keeps xmm6-xmm15 itself.)
1:
    sub     rsp, 152
    mov     [rsp + 32], rcx
    mov     [rsp + 40], rdx
    mov     [rsp + 48], r8
    mov     [rsp + 56], r9
    mov     [rsp + 64], rax
    mov     [rsp + 72], r10
    movups  [rsp + 80], xmm0
    movups  [rsp + 96], xmm1
    movups  [rsp + 112], xmm2
    movups  [rsp + 128], xmm3
    mov     rdx, r11                # RhpResolveInterfaceMethod(object, cell)
    call    RhpResolveInterfaceMethod
    mov     r11, rax
    mov     rcx, [rsp + 32]
    mov     rdx, [rsp + 40]
    mov     r8, [rsp + 48]
    mov     r9, [rsp + 56]
    mov     rax, [rsp + 64]
    mov     r10, [rsp + 72]
    movups  xmm0, [rsp + 80]
    movups  xmm1, [rsp + 96]
    movups  xmm2, [rsp + 112]
    movups  xmm3, [rsp + 128]
    add     rsp, 152
    jmp     r11

// Windows grows a thread's stack one guard page at a time, so a function
// whose frame is larger than a page must touch each page on the way down, in
// order. The compiler calls this from such a function's prologue.
// r11: the lowest address of the frame being allocated (kept)
// rsp: somewhere on the last page already touched
// rax: scratch
    .globl RhpStackProbe
RhpStackProbe:
    mov     rax, rsp
    and     rax, -4096              # start of the last page touched
3:
    sub     rax, 4096
    test    dword ptr [rax], eax    # touch the next page down
    cmp     rax, r11
    jg      3b
    ret


// Fibers (../lib/Fibers.cs): switching from one stack to another (see x64.S).
// Windows' calling convention has the callee keep more: rdi, rsi and
// xmm6-xmm15 as well. And Windows keeps each thread's stack bounds in its
// thread information block (gs:[8] the top, gs:[16] the lowest committed
// address, gs:[0x1478] the bottom of the allocation), which code compiled for
// Windows checks: the C library's stack probe, for one, would walk from the
// thread's stack limit down to a fiber's stack, through memory that isn't a
// stack. So the switch swaps them with the stack. UEFI has no such block (gs
// isn't set up), and efi/'s build defines ZERO_UEFI to leave them out.
//
// The frame, from the saved stack pointer up (272 bytes, 16-byte aligned):
//   0    xmm6-xmm15
//   160  mxcsr, x87 control word
//   168  stack top, 176 stack limit, 184 allocation bottom (Windows only)
//   192  (padding)
//   200  r15, r14, r13, r12, rsi, rdi, rbx, rbp
//   264  return address
// rcx: where to store this stack's pointer
// rdx: the stack pointer to resume
    .globl ZeroFiberSwitch
ZeroFiberSwitch:
    push    rbp
    push    rbx
    push    rdi
    push    rsi
    push    r12
    push    r13
    push    r14
    push    r15
    sub     rsp, 200
    movaps  [rsp], xmm6
    movaps  [rsp + 16], xmm7
    movaps  [rsp + 32], xmm8
    movaps  [rsp + 48], xmm9
    movaps  [rsp + 64], xmm10
    movaps  [rsp + 80], xmm11
    movaps  [rsp + 96], xmm12
    movaps  [rsp + 112], xmm13
    movaps  [rsp + 128], xmm14
    movaps  [rsp + 144], xmm15
    stmxcsr [rsp + 160]
    fnstcw  [rsp + 164]
#ifndef ZERO_UEFI
    mov     rax, gs:[8]
    mov     [rsp + 168], rax
    mov     rax, gs:[16]
    mov     [rsp + 176], rax
    mov     rax, gs:[0x1478]
    mov     [rsp + 184], rax
#endif
    mov     [rcx], rsp
    mov     rsp, rdx
#ifndef ZERO_UEFI
    mov     rax, [rsp + 168]
    mov     gs:[8], rax
    mov     rax, [rsp + 176]
    mov     gs:[16], rax
    mov     rax, [rsp + 184]
    mov     gs:[0x1478], rax
#endif
    ldmxcsr [rsp + 160]
    fldcw   [rsp + 164]
    movaps  xmm6, [rsp]
    movaps  xmm7, [rsp + 16]
    movaps  xmm8, [rsp + 32]
    movaps  xmm9, [rsp + 48]
    movaps  xmm10, [rsp + 64]
    movaps  xmm11, [rsp + 80]
    movaps  xmm12, [rsp + 96]
    movaps  xmm13, [rsp + 112]
    movaps  xmm14, [rsp + 128]
    movaps  xmm15, [rsp + 144]
    add     rsp, 200
    pop     r15
    pop     r14
    pop     r13
    pop     r12
    pop     rsi
    pop     rdi
    pop     rbx
    pop     rbp
    ret

// Prepares a new stack so that switching to it calls entry(argument) there.
// rcx: the stack's top; rdx: its bottom (above the guard page); r8: entry;
// r9: argument
// rax: the stack pointer to give ZeroFiberSwitch
    .globl ZeroFiberInit
ZeroFiberInit:
    and     rcx, -16
    lea     rax, [rcx - 272]
    xor     r10, r10
    mov     r11, rax
1:
    mov     [r11], r10              # zero the frame: registers start at 0
    add     r11, 8
    cmp     r11, rcx
    jne     1b
    mov     dword ptr [rax + 160], 0x1F80 # mxcsr: the default
    mov     dword ptr [rax + 164], 0x027F # x87 control word: Windows' default
    mov     [rax + 168], rcx        # stack top
    mov     [rax + 176], rdx        # stack limit: all of it is committed
    mov     [rax + 184], rdx        # allocation bottom
    mov     [rax + 216], r8         # r13: entry
    mov     [rax + 224], r9         # r12: argument
    lea     r10, [rip + ZeroFiberStart]
    mov     [rax + 264], r10        # where the switch returns to
    ret

// rsp is the stack's top here, 16-byte aligned. The call gets its 32 bytes of
// shadow space. The entry never returns: it switches away for good.
ZeroFiberStart:
    sub     rsp, 32
    mov     rcx, r12
    call    r13
    int3
