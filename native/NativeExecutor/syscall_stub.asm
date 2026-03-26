;; ── ssn_dispatch — thread-safe indirect syscall with spoofed return addr ─────
;;
;; C++ call: ssn_dispatch(SSN, arg1, arg2, arg3, arg4, arg5, ...)
;;   RCX=SSN, RDX=a1, R8=a2, R9=a3, [RSP+28h]=a4, [RSP+30h]=a5, ...
;;
;; Rearranges to NT convention (R10=a1), loads SSN into EAX,
;; optionally sets a JMP[RBX] gadget in kernelbase as the return address
;; (if g_spoof_config is initialized), then jumps to syscall;ret in ntdll.

.data
EXTERN g_syscall_gadget: QWORD
EXTERN g_spoof_config:   BYTE    ; SpoofConfig struct, first QWORD = jmp_rbx_gadget

.code

;; ── spoof_fixup — restores state after: syscall;ret → JMP[RBX] → here ──────
spoof_fixup PROC
    add     rsp, 58h         ; undo sub 60h minus 8 (ret consumed 8)
    add     rsp, 20h         ; undo shadow
    pop     rbx
    pop     r13
    pop     r12
    ret
spoof_fixup ENDP

ssn_dispatch PROC
    ;; ── Prologue ────────────────────────────────────────────────────────────
    push    r12
    push    r13
    push    rbx
    sub     rsp, 20h

    ;; Stack layout (RSP-relative after prologue):
    ;; [rsp+00h..1Fh] shadow (20h)
    ;; [rsp+20h] saved rbx    [rsp+28h] saved r13    [rsp+30h] saved r12
    ;; [rsp+38h] return addr   [rsp+40h..58h] caller shadow
    ;; [rsp+60h] C++ arg5 = NT arg4
    ;; [rsp+68h] C++ arg6 = NT arg5     ...     [rsp+98h] C++ arg12 = NT arg11

    ;; ── Rearrange to NT convention ──────────────────────────────────────────
    mov     eax, ecx
    mov     r10, rdx
    mov     rdx, r8
    mov     r8,  r9
    mov     r9,  QWORD PTR [rsp + 60h]  ; NT arg4

    ;; ── Build syscall stack frame (60h bytes below) ─────────────────────────
    sub     rsp, 60h

    ;; ── Check if spoofing is available ──────────────────────────────────────
    mov     rcx, QWORD PTR [g_spoof_config]   ; jmp_rbx_gadget
    test    rcx, rcx
    jz      use_plain_return

    ;; ── Spoofed return: JMP [RBX] gadget in kernelbase ──────────────────────
    mov     QWORD PTR [rsp], rcx               ; return addr = JMP [RBX] gadget

    ;; Store fixup addr in caller shadow, point RBX to it.
    ;; Caller shadow[0] is at rsp + 60h + 40h = rsp + A0h.
    lea     rbx, [spoof_fixup]
    mov     QWORD PTR [rsp + 0A0h], rbx
    lea     rbx, [rsp + 0A0h]                  ; RBX → cell with fixup addr

    jmp     copy_args

use_plain_return:
    ;; ── Plain return: come back to our done label ───────────────────────────
    lea     rcx, [done]
    mov     QWORD PTR [rsp], rcx

copy_args:
    ;; Copy NT args 5-11 from original positions.
    ;; Before sub 60h, arg5 (NT) was at [rsp_before + 68h] = [rsp + 60h + 68h] = [rsp + C8h]
    mov     rcx, QWORD PTR [rsp + 0C8h]
    mov     QWORD PTR [rsp + 28h], rcx    ; NT arg5
    mov     rcx, QWORD PTR [rsp + 0D0h]
    mov     QWORD PTR [rsp + 30h], rcx    ; NT arg6
    mov     rcx, QWORD PTR [rsp + 0D8h]
    mov     QWORD PTR [rsp + 38h], rcx    ; NT arg7
    mov     rcx, QWORD PTR [rsp + 0E0h]
    mov     QWORD PTR [rsp + 40h], rcx    ; NT arg8
    mov     rcx, QWORD PTR [rsp + 0E8h]
    mov     QWORD PTR [rsp + 48h], rcx    ; NT arg9
    mov     rcx, QWORD PTR [rsp + 0F0h]
    mov     QWORD PTR [rsp + 50h], rcx    ; NT arg10
    mov     rcx, QWORD PTR [rsp + 0F8h]
    mov     QWORD PTR [rsp + 58h], rcx    ; NT arg11

    ;; ── Execute syscall ─────────────────────────────────────────────────────
    jmp     QWORD PTR [g_syscall_gadget]
    ;; Spoofed path: syscall;ret → JMP[RBX] → spoof_fixup (restores via add rsp)
    ;; Plain path:   syscall;ret → done

done:
    ;; Plain path epilogue (ret consumed 8 bytes from our sub 60h).
    add     rsp, 58h
    add     rsp, 20h
    pop     rbx
    pop     r13
    pop     r12
    ret

ssn_dispatch ENDP

END
