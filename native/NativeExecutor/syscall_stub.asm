;; indirect_syscall_stub — x64 indirect syscall trampoline
;;
;; Before calling this, the C++ wrapper sets:
;;   g_syscall_ssn    = the syscall service number
;;   g_syscall_gadget = address of a  syscall ; ret  gadget inside ntdll's .text
;;
;; This function is called with the *same* register/stack layout as the target
;; NT function (rcx=arg1, rdx=arg2, r8=arg3, r9=arg4, stack=arg5+).
;; It moves rcx → r10 (NT convention), loads the SSN into eax, and jumps into
;; ntdll.  The  ret  inside the gadget returns directly to the C++ caller.

.data
EXTERN g_syscall_ssn:    DWORD
EXTERN g_syscall_gadget: QWORD

.code

indirect_syscall_stub PROC
    mov     r10, rcx                        ; first arg in r10 (NT ABI)
    mov     eax, DWORD PTR [g_syscall_ssn]  ; syscall number
    jmp     QWORD PTR [g_syscall_gadget]    ; → ntdll  syscall ; ret
indirect_syscall_stub ENDP

END
