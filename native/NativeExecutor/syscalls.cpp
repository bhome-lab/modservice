#include "syscalls.h"
#include "win_offsets.h"

#include <cstring>

// ── Function pointer table ────────────────────────────────────────────────────
// Resolved once from the in-memory ntdll via manual export walk.

namespace {

enum class SyscallId : uint32_t {
    NtOpenProcess = 0, NtClose, NtAllocateVirtualMemory, NtFreeVirtualMemory,
    NtReadVirtualMemory, NtWriteVirtualMemory, NtProtectVirtualMemory,
    NtCreateThreadEx, NtWaitForSingleObject, NtQueryInformationProcess,
    NtQuerySystemInformation, NtOpenThread, NtSuspendThread, NtResumeThread,
    NtGetContextThread, NtSetContextThread, NtDelayExecution, Count
};

void* g_fn_table[static_cast<uint32_t>(SyscallId::Count)] = {};

constexpr const char* kSyscallNames[] = {
    "NtOpenProcess",
    "NtClose",
    "NtAllocateVirtualMemory",
    "NtFreeVirtualMemory",
    "NtReadVirtualMemory",
    "NtWriteVirtualMemory",
    "NtProtectVirtualMemory",
    "NtCreateThreadEx",
    "NtWaitForSingleObject",
    "NtQueryInformationProcess",
    "NtQuerySystemInformation",
    "NtOpenThread",
    "NtSuspendThread",
    "NtResumeThread",
    "NtGetContextThread",
    "NtSetContextThread",
    "NtDelayExecution",
};
static_assert(_countof(kSyscallNames) == static_cast<uint32_t>(SyscallId::Count));

// ── Manual export walk ────────────────────────────────────────────────────────

void* find_local_export_impl(HMODULE mod, const char* name) {
    const auto base = reinterpret_cast<uintptr_t>(mod);
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(mod);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;

    const auto& exp_dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (exp_dir.VirtualAddress == 0 || exp_dir.Size == 0) return nullptr;

    const auto* exp = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + exp_dir.VirtualAddress);
    const auto* names = reinterpret_cast<const uint32_t*>(base + exp->AddressOfNames);
    const auto* ords  = reinterpret_cast<const uint16_t*>(base + exp->AddressOfNameOrdinals);
    const auto* funcs = reinterpret_cast<const uint32_t*>(base + exp->AddressOfFunctions);

    for (DWORD i = 0; i < exp->NumberOfNames; ++i) {
        const auto* n = reinterpret_cast<const char*>(base + names[i]);
        if (strcmp(n, name) == 0) {
            uint32_t rva = funcs[ords[i]];
            if (rva >= exp_dir.VirtualAddress && rva < exp_dir.VirtualAddress + exp_dir.Size)
                return nullptr;  // forwarded
            return reinterpret_cast<void*>(base + rva);
        }
    }
    return nullptr;
}

}  // namespace

// ── Public API ────────────────────────────────────────────────────────────────

void* syscall_find_local_export(HMODULE mod, const char* name) {
    return find_local_export_impl(mod, name);
}

bool syscall_init(std::wstring& error) {
    // Resolve every NT function from the in-memory ntdll.
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) {
        error = L"ntdll.dll not loaded.";
        return false;
    }

    for (uint32_t i = 0; i < static_cast<uint32_t>(SyscallId::Count); ++i) {
        g_fn_table[i] = find_local_export_impl(ntdll, kSyscallNames[i]);
        if (!g_fn_table[i]) {
            error = L"Failed to resolve: ";
            const auto* n = kSyscallNames[i];
            error.append(n, n + strlen(n));
            return false;
        }
    }

    // Discover OS-specific offsets.
    if (!win_offsets_init(error)) return false;

    return true;
}

// ── Wrapper implementations ──────────────────────────────────────────────────
// Each wrapper calls the resolved ntdll function pointer directly.

#define NT_FN(id, type) reinterpret_cast<type>(g_fn_table[static_cast<uint32_t>(id)])

NTSTATUS syscall::NtOpenProcess(PHANDLE ph, ACCESS_MASK access,
                                NT_OBJECT_ATTRIBUTES* oa, NT_CLIENT_ID* cid) {
    using fn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, NT_OBJECT_ATTRIBUTES*, NT_CLIENT_ID*);
    return NT_FN(SyscallId::NtOpenProcess, fn)(ph, access, oa, cid);
}

NTSTATUS syscall::NtClose(HANDLE h) {
    using fn = NTSTATUS(NTAPI*)(HANDLE);
    return NT_FN(SyscallId::NtClose, fn)(h);
}

NTSTATUS syscall::NtAllocateVirtualMemory(HANDLE ph, PVOID* base, ULONG_PTR zero,
                                           PSIZE_T size, ULONG type, ULONG prot) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PVOID*, ULONG_PTR, PSIZE_T, ULONG, ULONG);
    return NT_FN(SyscallId::NtAllocateVirtualMemory, fn)(ph, base, zero, size, type, prot);
}

NTSTATUS syscall::NtFreeVirtualMemory(HANDLE ph, PVOID* base, PSIZE_T size, ULONG type) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PVOID*, PSIZE_T, ULONG);
    return NT_FN(SyscallId::NtFreeVirtualMemory, fn)(ph, base, size, type);
}

NTSTATUS syscall::NtReadVirtualMemory(HANDLE ph, PVOID base, PVOID buf,
                                       SIZE_T size, PSIZE_T bytes_read) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PVOID, PVOID, SIZE_T, PSIZE_T);
    return NT_FN(SyscallId::NtReadVirtualMemory, fn)(ph, base, buf, size, bytes_read);
}

NTSTATUS syscall::NtWriteVirtualMemory(HANDLE ph, PVOID base, const void* buf,
                                        SIZE_T size, PSIZE_T bytes_written) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PVOID, const void*, SIZE_T, PSIZE_T);
    return NT_FN(SyscallId::NtWriteVirtualMemory, fn)(ph, base, buf, size, bytes_written);
}

NTSTATUS syscall::NtProtectVirtualMemory(HANDLE ph, PVOID* base, PSIZE_T size,
                                          ULONG new_prot, PULONG old_prot) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PVOID*, PSIZE_T, ULONG, PULONG);
    return NT_FN(SyscallId::NtProtectVirtualMemory, fn)(ph, base, size, new_prot, old_prot);
}

NTSTATUS syscall::NtCreateThreadEx(PHANDLE th, ACCESS_MASK access, PVOID oa,
                                    HANDLE ph, PVOID start, PVOID arg,
                                    ULONG flags, SIZE_T zero_bits,
                                    SIZE_T stack, SIZE_T max_stack, PVOID attr) {
    using fn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, PVOID, HANDLE,
                                 PVOID, PVOID, ULONG, SIZE_T, SIZE_T, SIZE_T, PVOID);
    return NT_FN(SyscallId::NtCreateThreadEx, fn)(th, access, oa, ph, start, arg,
                                                   flags, zero_bits, stack, max_stack, attr);
}

NTSTATUS syscall::NtWaitForSingleObject(HANDLE h, BOOLEAN alertable, PLARGE_INTEGER timeout) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, BOOLEAN, PLARGE_INTEGER);
    return NT_FN(SyscallId::NtWaitForSingleObject, fn)(h, alertable, timeout);
}

NTSTATUS syscall::NtQueryInformationProcess(HANDLE ph, ULONG cls, PVOID info,
                                             ULONG len, PULONG ret_len) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
    return NT_FN(SyscallId::NtQueryInformationProcess, fn)(ph, cls, info, len, ret_len);
}

NTSTATUS syscall::NtQuerySystemInformation(ULONG cls, PVOID info,
                                            ULONG len, PULONG ret_len) {
    using fn = NTSTATUS(NTAPI*)(ULONG, PVOID, ULONG, PULONG);
    return NT_FN(SyscallId::NtQuerySystemInformation, fn)(cls, info, len, ret_len);
}

NTSTATUS syscall::NtOpenThread(PHANDLE th, ACCESS_MASK access,
                                NT_OBJECT_ATTRIBUTES* oa, NT_CLIENT_ID* cid) {
    using fn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, NT_OBJECT_ATTRIBUTES*, NT_CLIENT_ID*);
    return NT_FN(SyscallId::NtOpenThread, fn)(th, access, oa, cid);
}

NTSTATUS syscall::NtSuspendThread(HANDLE th, PULONG prev) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PULONG);
    return NT_FN(SyscallId::NtSuspendThread, fn)(th, prev);
}

NTSTATUS syscall::NtResumeThread(HANDLE th, PULONG prev) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PULONG);
    return NT_FN(SyscallId::NtResumeThread, fn)(th, prev);
}

NTSTATUS syscall::NtGetContextThread(HANDLE th, PCONTEXT ctx) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PCONTEXT);
    return NT_FN(SyscallId::NtGetContextThread, fn)(th, ctx);
}

NTSTATUS syscall::NtSetContextThread(HANDLE th, PCONTEXT ctx) {
    using fn = NTSTATUS(NTAPI*)(HANDLE, PCONTEXT);
    return NT_FN(SyscallId::NtSetContextThread, fn)(th, ctx);
}

NTSTATUS syscall::NtDelayExecution(BOOLEAN alertable, PLARGE_INTEGER interval) {
    using fn = NTSTATUS(NTAPI*)(BOOLEAN, PLARGE_INTEGER);
    return NT_FN(SyscallId::NtDelayExecution, fn)(alertable, interval);
}
