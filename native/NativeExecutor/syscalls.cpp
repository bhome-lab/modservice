#include "syscalls.h"
#include "win_offsets.h"

#include <algorithm>
#include <cstring>
#include <vector>

// ── Shared state (read by the ASM trampoline) ──────────────────────────────────

extern "C" void*       g_syscall_gadget = nullptr;

namespace {

// ── SSN table ──────────────────────────────────────────────────────────────────

uint32_t g_ssn_table[static_cast<uint32_t>(SyscallId::Count)] = {};

// NT function names, indexed by SyscallId.
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

// ── PE helpers (operate on a flat file buffer) ─────────────────────────────────

uint32_t rva_to_file_offset(const IMAGE_NT_HEADERS64* nt, uint32_t rva) {
    const auto* sec = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        if (rva >= sec[i].VirtualAddress &&
            rva <  sec[i].VirtualAddress + sec[i].SizeOfRawData) {
            return sec[i].PointerToRawData + (rva - sec[i].VirtualAddress);
        }
    }
    return 0;
}

// Returns the RVA of a named export, or 0 on failure.
uint32_t find_export_rva(const uint8_t* base, const IMAGE_NT_HEADERS64* nt,
                         const char* name) {
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (dir.VirtualAddress == 0 || dir.Size == 0) return 0;

    const auto exp_off = rva_to_file_offset(nt, dir.VirtualAddress);
    if (exp_off == 0) return 0;
    const auto* exp = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + exp_off);

    const auto names_off    = rva_to_file_offset(nt, exp->AddressOfNames);
    const auto ords_off     = rva_to_file_offset(nt, exp->AddressOfNameOrdinals);
    const auto funcs_off    = rva_to_file_offset(nt, exp->AddressOfFunctions);
    if (names_off == 0 || ords_off == 0 || funcs_off == 0) return 0;

    const auto* names_arr = reinterpret_cast<const uint32_t*>(base + names_off);
    const auto* ords_arr  = reinterpret_cast<const uint16_t*>(base + ords_off);
    const auto* funcs_arr = reinterpret_cast<const uint32_t*>(base + funcs_off);

    for (DWORD i = 0; i < exp->NumberOfNames; ++i) {
        const auto n_off = rva_to_file_offset(nt, names_arr[i]);
        if (n_off == 0) continue;
        if (strcmp(reinterpret_cast<const char*>(base + n_off), name) == 0) {
            return funcs_arr[ords_arr[i]];
        }
    }
    return 0;
}

// Extract the SSN from a clean ntdll stub on disk.
// Pattern: 4C 8B D1 B8 [SSN LE32] ...
bool extract_ssn(const uint8_t* base, const IMAGE_NT_HEADERS64* nt,
                 const char* func_name, uint32_t& ssn) {
    const auto rva = find_export_rva(base, nt, func_name);
    if (rva == 0) return false;

    const auto off = rva_to_file_offset(nt, rva);
    if (off == 0) return false;

    const uint8_t* stub = base + off;
    // Primary: mov r10, rcx (4C 8B D1) ; mov eax, <SSN> (B8 xx xx xx xx)
    if (stub[0] == 0x4C && stub[1] == 0x8B && stub[2] == 0xD1 &&
        stub[3] == 0xB8) {
        ssn = *reinterpret_cast<const uint32_t*>(stub + 4);
        return true;
    }
    // Alternative: mov r10, rcx (49 89 CA) ; mov eax, <SSN> (B8 xx xx xx xx)
    if (stub[0] == 0x49 && stub[1] == 0x89 && stub[2] == 0xCA &&
        stub[3] == 0xB8) {
        ssn = *reinterpret_cast<const uint32_t*>(stub + 4);
        return true;
    }
    return false;
}

// Scan the in-memory .text section of ntdll for a  syscall ; ret  gadget.
void* find_syscall_gadget() {
    const auto ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return nullptr;

    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(ntdll);
    const auto* nt  = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
        reinterpret_cast<const uint8_t*>(ntdll) + dos->e_lfanew);
    const auto* sec = IMAGE_FIRST_SECTION(nt);

    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        if (memcmp(sec[i].Name, ".text", 5) != 0) continue;

        auto* start = reinterpret_cast<uint8_t*>(ntdll) + sec[i].VirtualAddress;
        const auto size = sec[i].Misc.VirtualSize;
        // Look for  0F 05 C3  (syscall ; ret)
        for (DWORD j = 0; j + 2 < size; ++j) {
            if (start[j] == 0x0F && start[j + 1] == 0x05 && start[j + 2] == 0xC3) {
                return start + j;
            }
        }
    }
    return nullptr;
}

// Read the entire ntdll.dll from System32 on disk (clean, un-hooked copy).
bool read_ntdll_from_disk(std::vector<uint8_t>& buffer, std::wstring& error) {
    wchar_t sys[MAX_PATH]{};
    if (GetSystemDirectoryW(sys, MAX_PATH) == 0) {
        error = L"GetSystemDirectoryW failed.";
        return false;
    }

    std::wstring path = std::wstring(sys) + L"\\ntdll.dll";
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        error = L"Failed to open ntdll.dll from disk.";
        return false;
    }

    LARGE_INTEGER file_size{};
    if (!GetFileSizeEx(file, &file_size) || file_size.QuadPart == 0) {
        CloseHandle(file);
        error = L"Failed to get ntdll.dll file size.";
        return false;
    }

    buffer.resize(static_cast<size_t>(file_size.QuadPart));
    DWORD bytes_read = 0;
    BOOL ok = ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &bytes_read, nullptr);
    CloseHandle(file);

    if (!ok || bytes_read != buffer.size()) {
        error = L"Failed to read ntdll.dll from disk.";
        return false;
    }
    return true;
}

// ── Manual export walk (avoids GetProcAddress monitoring) ────────────────────

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
            // Check for forwarded export (RVA inside export directory).
            if (rva >= exp_dir.VirtualAddress && rva < exp_dir.VirtualAddress + exp_dir.Size)
                return nullptr;  // forwarded — caller must handle
            return reinterpret_cast<void*>(base + rva);
        }
    }
    return nullptr;
}

}  // namespace

// ── Public API ─────────────────────────────────────────────────────────────────

void* syscall_find_local_export(HMODULE mod, const char* name) {
    return find_local_export_impl(mod, name);
}

uint32_t syscall_get_ssn(SyscallId id) {
    return g_ssn_table[static_cast<uint32_t>(id)];
}

bool syscall_init(std::wstring& error) {
    // 1. Read clean ntdll from disk.
    std::vector<uint8_t> ntdll_disk;
    if (!read_ntdll_from_disk(ntdll_disk, error)) return false;

    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(ntdll_disk.data());
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        error = L"ntdll.dll: invalid DOS signature.";
        return false;
    }
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(ntdll_disk.data() + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) {
        error = L"ntdll.dll: invalid NT signature.";
        return false;
    }

    // 2. Extract SSNs for every syscall we need.
    for (uint32_t i = 0; i < static_cast<uint32_t>(SyscallId::Count); ++i) {
        if (!extract_ssn(ntdll_disk.data(), nt, kSyscallNames[i], g_ssn_table[i])) {
            error = L"Failed to extract SSN for: ";
            const auto* n = kSyscallNames[i];
            error.append(n, n + strlen(n));
            return false;
        }
    }

    // 3. Locate  syscall ; ret  gadget in the *in-memory* ntdll.
    g_syscall_gadget = find_syscall_gadget();
    if (!g_syscall_gadget) {
        error = L"Failed to locate syscall gadget in ntdll.";
        return false;
    }

    // 4. Discover OS-specific offsets (requires working syscalls from steps 1-3).
    if (!win_offsets_init(error)) return false;


    // Wipe the disk buffer (no need to keep it around).
    SecureZeroMemory(ntdll_disk.data(), ntdll_disk.size());
    return true;
}

// ── Wrapper implementations ────────────────────────────────────────────────────
// Each wrapper casts ssn_dispatch to the right signature with SSN prepended.
// ssn_dispatch (ASM) expects: ECX = SSN, RDX = arg1, R8 = arg2, R9 = arg3,
//                             stack = arg4, arg5, ...
// It rearranges to NT convention and executes the syscall.

NTSTATUS syscall::NtOpenProcess(PHANDLE ph, ACCESS_MASK access,
                                NT_OBJECT_ATTRIBUTES* oa, NT_CLIENT_ID* cid) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, PHANDLE, ACCESS_MASK, NT_OBJECT_ATTRIBUTES*, NT_CLIENT_ID*);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtOpenProcess)],
        ph, access, oa, cid);
}

NTSTATUS syscall::NtClose(HANDLE h) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtClose)], h);
}

NTSTATUS syscall::NtAllocateVirtualMemory(HANDLE ph, PVOID* base, ULONG_PTR zero,
                                           PSIZE_T size, ULONG type, ULONG prot) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PVOID*, ULONG_PTR, PSIZE_T, ULONG, ULONG);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtAllocateVirtualMemory)],
        ph, base, zero, size, type, prot);
}

NTSTATUS syscall::NtFreeVirtualMemory(HANDLE ph, PVOID* base, PSIZE_T size, ULONG type) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PVOID*, PSIZE_T, ULONG);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtFreeVirtualMemory)],
        ph, base, size, type);
}

NTSTATUS syscall::NtReadVirtualMemory(HANDLE ph, PVOID base, PVOID buf,
                                       SIZE_T size, PSIZE_T bytes_read) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PVOID, PVOID, SIZE_T, PSIZE_T);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtReadVirtualMemory)],
        ph, base, buf, size, bytes_read);
}

NTSTATUS syscall::NtWriteVirtualMemory(HANDLE ph, PVOID base, const void* buf,
                                        SIZE_T size, PSIZE_T bytes_written) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PVOID, const void*, SIZE_T, PSIZE_T);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtWriteVirtualMemory)],
        ph, base, buf, size, bytes_written);
}

NTSTATUS syscall::NtProtectVirtualMemory(HANDLE ph, PVOID* base, PSIZE_T size,
                                          ULONG new_prot, PULONG old_prot) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PVOID*, PSIZE_T, ULONG, PULONG);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtProtectVirtualMemory)],
        ph, base, size, new_prot, old_prot);
}

NTSTATUS syscall::NtCreateThreadEx(PHANDLE th, ACCESS_MASK access, PVOID oa,
                                    HANDLE ph, PVOID start, PVOID arg,
                                    ULONG flags, SIZE_T zero_bits,
                                    SIZE_T stack, SIZE_T max_stack, PVOID attr) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, PHANDLE, ACCESS_MASK, PVOID, HANDLE,
                                 PVOID, PVOID, ULONG, SIZE_T, SIZE_T, SIZE_T, PVOID);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtCreateThreadEx)],
        th, access, oa, ph, start, arg, flags, zero_bits, stack, max_stack, attr);
}

NTSTATUS syscall::NtWaitForSingleObject(HANDLE h, BOOLEAN alertable, PLARGE_INTEGER timeout) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, BOOLEAN, PLARGE_INTEGER);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtWaitForSingleObject)],
        h, alertable, timeout);
}

NTSTATUS syscall::NtQueryInformationProcess(HANDLE ph, ULONG cls, PVOID info,
                                             ULONG len, PULONG ret_len) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, ULONG, PVOID, ULONG, PULONG);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtQueryInformationProcess)],
        ph, cls, info, len, ret_len);
}

NTSTATUS syscall::NtQuerySystemInformation(ULONG cls, PVOID info,
                                            ULONG len, PULONG ret_len) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, ULONG, PVOID, ULONG, PULONG);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtQuerySystemInformation)],
        cls, info, len, ret_len);
}

NTSTATUS syscall::NtOpenThread(PHANDLE th, ACCESS_MASK access,
                                NT_OBJECT_ATTRIBUTES* oa, NT_CLIENT_ID* cid) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, PHANDLE, ACCESS_MASK, NT_OBJECT_ATTRIBUTES*, NT_CLIENT_ID*);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtOpenThread)],
        th, access, oa, cid);
}

NTSTATUS syscall::NtSuspendThread(HANDLE th, PULONG prev) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PULONG);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtSuspendThread)], th, prev);
}

NTSTATUS syscall::NtResumeThread(HANDLE th, PULONG prev) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PULONG);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtResumeThread)], th, prev);
}

NTSTATUS syscall::NtGetContextThread(HANDLE th, PCONTEXT ctx) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PCONTEXT);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtGetContextThread)], th, ctx);
}

NTSTATUS syscall::NtSetContextThread(HANDLE th, PCONTEXT ctx) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, HANDLE, PCONTEXT);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtSetContextThread)], th, ctx);
}

NTSTATUS syscall::NtDelayExecution(BOOLEAN alertable, PLARGE_INTEGER interval) {
    using fn = NTSTATUS(NTAPI*)(uint32_t, BOOLEAN, PLARGE_INTEGER);
    return reinterpret_cast<fn>(&ssn_dispatch)(
        g_ssn_table[static_cast<uint32_t>(SyscallId::NtDelayExecution)],
        alertable, interval);
}
