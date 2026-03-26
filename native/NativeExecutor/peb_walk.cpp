#include "peb_walk.h"
#include "win_offsets.h"

#include <algorithm>
#include <cstring>
#include <intrin.h>

namespace {

// ── Helpers ────────────────────────────────────────────────────────────────────

std::wstring read_remote_unicode_string(HANDLE process, uint16_t length, uint64_t buffer_ptr) {
    if (length == 0 || buffer_ptr == 0) return {};
    const auto char_count = length / sizeof(wchar_t);
    std::wstring result(char_count, L'\0');
    syscall::NtReadVirtualMemory(process, reinterpret_cast<PVOID>(buffer_ptr),
                                  result.data(), length, nullptr);
    return result;
}

// Read the remote export directory and arrays from a module.
struct RemoteExportInfo {
    IMAGE_EXPORT_DIRECTORY dir{};
    uint32_t dir_rva  = 0;
    uint32_t dir_size = 0;
    std::vector<uint32_t> name_rvas;
    std::vector<uint16_t> ordinals;
    std::vector<uint32_t> func_rvas;
};

mm_status read_remote_exports(HANDLE process, uintptr_t base, RemoteExportInfo& out,
                               std::wstring& error) {
    IMAGE_DOS_HEADER dos{};
    auto st = syscall::NtReadVirtualMemory(process, reinterpret_cast<PVOID>(base),
                                            &dos, sizeof(dos), nullptr);
    if (!NT_SUCCESS(st) || dos.e_magic != IMAGE_DOS_SIGNATURE) {
        error = L"Invalid DOS header in remote module.";
        return MM_EXECUTION_FAILED;
    }

    IMAGE_NT_HEADERS64 nt{};
    st = syscall::NtReadVirtualMemory(process, reinterpret_cast<PVOID>(base + dos.e_lfanew),
                                       &nt, sizeof(nt), nullptr);
    if (!NT_SUCCESS(st) || nt.Signature != IMAGE_NT_SIGNATURE) {
        error = L"Invalid NT header in remote module.";
        return MM_EXECUTION_FAILED;
    }

    const auto& exp_dir = nt.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (exp_dir.VirtualAddress == 0 || exp_dir.Size == 0) {
        error = L"Module has no export directory.";
        return MM_EXECUTION_FAILED;
    }

    out.dir_rva  = exp_dir.VirtualAddress;
    out.dir_size = exp_dir.Size;

    st = syscall::NtReadVirtualMemory(process, reinterpret_cast<PVOID>(base + exp_dir.VirtualAddress),
                                       &out.dir, sizeof(out.dir), nullptr);
    if (!NT_SUCCESS(st)) {
        error = L"Failed to read export directory.";
        return MM_EXECUTION_FAILED;
    }

    out.name_rvas.resize(out.dir.NumberOfNames);
    out.ordinals.resize(out.dir.NumberOfNames);
    out.func_rvas.resize(out.dir.NumberOfFunctions);

    if (out.dir.NumberOfNames > 0) {
        syscall::NtReadVirtualMemory(process,
            reinterpret_cast<PVOID>(base + out.dir.AddressOfNames),
            out.name_rvas.data(), out.name_rvas.size() * sizeof(uint32_t), nullptr);
        syscall::NtReadVirtualMemory(process,
            reinterpret_cast<PVOID>(base + out.dir.AddressOfNameOrdinals),
            out.ordinals.data(), out.ordinals.size() * sizeof(uint16_t), nullptr);
    }
    if (out.dir.NumberOfFunctions > 0) {
        syscall::NtReadVirtualMemory(process,
            reinterpret_cast<PVOID>(base + out.dir.AddressOfFunctions),
            out.func_rvas.data(), out.func_rvas.size() * sizeof(uint32_t), nullptr);
    }
    return MM_OK;
}

// Check if an RVA falls inside the export directory (indicating a forwarded export).
bool is_forwarded(const RemoteExportInfo& info, uint32_t func_rva) {
    return func_rva >= info.dir_rva && func_rva < info.dir_rva + info.dir_size;
}

// Resolve a forwarded export string like "ntdll.RtlAllocateHeap" or "api-ms-win-*.FuncName".
mm_status resolve_forward(HANDLE process, uintptr_t module_base, uint32_t fwd_rva,
                           const std::vector<RemoteModuleInfo>& modules,
                           uintptr_t& resolved, std::wstring& error) {
    char fwd_str[256]{};
    syscall::NtReadVirtualMemory(process, reinterpret_cast<PVOID>(module_base + fwd_rva),
                                  fwd_str, sizeof(fwd_str) - 1, nullptr);
    fwd_str[255] = '\0';

    char* dot = strchr(fwd_str, '.');
    if (!dot) {
        error = L"Malformed forward string.";
        return MM_EXECUTION_FAILED;
    }
    *dot = '\0';
    const char* fwd_dll_a  = fwd_str;
    const char* fwd_func_a = dot + 1;

    // Build wide DLL name with .dll extension.
    std::wstring fwd_dll(fwd_dll_a, fwd_dll_a + strlen(fwd_dll_a));
    fwd_dll += L".dll";

    // Direct name lookup.
    for (const auto& mod : modules) {
        if (_wcsicmp(mod.name.c_str(), fwd_dll.c_str()) == 0) {
            if (fwd_func_a[0] == '#') {
                uint16_t ord = static_cast<uint16_t>(atoi(fwd_func_a + 1));
                return resolve_remote_export_ordinal(process, mod.base, ord, resolved, modules, error);
            }
            return resolve_remote_export(process, mod.base, fwd_func_a, resolved, modules, error);
        }
    }

    // API Set resolution via PEB.ApiSetMap (no LoadLibrary/GetModuleHandle).
    if (_wcsnicmp(fwd_dll.c_str(), L"api-ms-", 7) == 0 ||
        _wcsnicmp(fwd_dll.c_str(), L"ext-ms-", 7) == 0) {
        // Parse ApiSetMap directly from our PEB.
        std::wstring api_name(fwd_dll);
        // Strip .dll extension.
        if (api_name.size() > 4 && _wcsicmp(api_name.c_str() + api_name.size() - 4, L".dll") == 0)
            api_name.resize(api_name.size() - 4);

        const auto& woff = win_offsets();
        if (!woff.api_set_available) goto api_set_done;

        {
        auto* peb = reinterpret_cast<const uint8_t*>(
            reinterpret_cast<void*>(__readgsqword(0x60)));
        struct ApiNs { uint32_t V, S, F, C, EO, HO, HF; };
        struct ApiNsEntry { uint32_t F, NO, NL, HL, VO, VC; };
        struct ApiValEntry { uint32_t F, NO, NL, VO, VL; };
        struct ApiHash { uint32_t H, I; };

        auto* ns = *reinterpret_cast<const ApiNs* const*>(peb + woff.peb_api_set_map);
        if (ns) {
            auto ns_base = reinterpret_cast<uintptr_t>(ns);
            size_t hashed_chars = api_name.size();
            for (size_t i = api_name.size(); i > 0; --i)
                if (api_name[i - 1] == L'-') { hashed_chars = i - 1; break; }

            uint32_t hash = 0;
            for (size_t i = 0; i < hashed_chars; ++i) {
                wchar_t c = api_name[i];
                if (c >= L'A' && c <= L'Z') c += 32;
                hash = hash * ns->HF + static_cast<uint32_t>(c);
            }

            auto* hashes = reinterpret_cast<const ApiHash*>(ns_base + ns->HO);
            int lo = 0, hi = static_cast<int>(ns->C) - 1;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                if (hash < hashes[mid].H) hi = mid - 1;
                else if (hash > hashes[mid].H) lo = mid + 1;
                else {
                    auto* entries = reinterpret_cast<const ApiNsEntry*>(ns_base + ns->EO);
                    const auto& e = entries[hashes[mid].I];
                    auto* cn = reinterpret_cast<const wchar_t*>(ns_base + e.NO);
                    if (hashed_chars == e.HL / 2 &&
                        _wcsnicmp(api_name.c_str(), cn, hashed_chars) == 0 && e.VC > 0) {
                        auto* vals = reinterpret_cast<const ApiValEntry*>(ns_base + e.VO);
                        if (vals[0].VL > 0) {
                            auto* host = reinterpret_cast<const wchar_t*>(ns_base + vals[0].VO);
                            std::wstring host_name(host, vals[0].VL / sizeof(wchar_t));
                            for (const auto& mod : modules) {
                                if (_wcsicmp(mod.name.c_str(), host_name.c_str()) == 0) {
                                    if (fwd_func_a[0] == '#') {
                                        uint16_t ord = static_cast<uint16_t>(atoi(fwd_func_a + 1));
                                        return resolve_remote_export_ordinal(process, mod.base, ord, resolved, modules, error);
                                    }
                                    return resolve_remote_export(process, mod.base, fwd_func_a, resolved, modules, error);
                                }
                            }
                        }
                    }
                    break;
                }
            }
        }
        } // end extra scope
    }
    api_set_done:

    error = L"Forwarded-export module not loaded: " + fwd_dll;
    return MM_EXECUTION_FAILED;
}

}  // namespace

// ── Public implementation ──────────────────────────────────────────────────────

mm_status enumerate_remote_modules(HANDLE process,
                                    std::vector<RemoteModuleInfo>& modules,
                                    std::wstring& error) {
    modules.clear();

    // 1. Get PEB address.
    NT_PROCESS_BASIC_INFORMATION pbi{};
    auto st = syscall::NtQueryInformationProcess(process, 0 /*ProcessBasicInformation*/,
                                                  &pbi, sizeof(pbi), nullptr);
    if (!NT_SUCCESS(st)) {
        error = L"NtQueryInformationProcess failed.";
        return MM_EXECUTION_FAILED;
    }

    // 2. Read PEB.Ldr pointer (offset 0x18).
    uint64_t ldr_addr = 0;
    st = syscall::NtReadVirtualMemory(process,
            reinterpret_cast<PVOID>(pbi.PebBaseAddress + offsetof(NT_PEB, Ldr)),
            &ldr_addr, sizeof(ldr_addr), nullptr);
    if (!NT_SUCCESS(st) || ldr_addr == 0) {
        error = L"Failed to read PEB.Ldr.";
        return MM_EXECUTION_FAILED;
    }

    // 3. Read InLoadOrderModuleList head (LIST_ENTRY at Ldr + 0x10).
    const uint64_t list_head_addr = ldr_addr + offsetof(NT_PEB_LDR_DATA, InLoadOrderModuleList);
    uint64_t first_flink = 0;
    st = syscall::NtReadVirtualMemory(process, reinterpret_cast<PVOID>(list_head_addr),
                                       &first_flink, sizeof(first_flink), nullptr);
    if (!NT_SUCCESS(st) || first_flink == 0) {
        error = L"Failed to read Ldr.InLoadOrderModuleList.Flink.";
        return MM_EXECUTION_FAILED;
    }

    // 4. Walk the linked list.
    uint64_t current = first_flink;
    for (int safety = 0; safety < 4096 && current != list_head_addr; ++safety) {
        NT_LDR_DATA_TABLE_ENTRY_REMOTE entry{};
        st = syscall::NtReadVirtualMemory(process, reinterpret_cast<PVOID>(current),
                                           &entry, sizeof(entry), nullptr);
        if (!NT_SUCCESS(st)) break;

        if (entry.DllBase != 0) {
            RemoteModuleInfo info;
            info.base = static_cast<uintptr_t>(entry.DllBase);
            info.size = entry.SizeOfImage;
            info.name      = read_remote_unicode_string(process, entry.BaseDllName_Length,
                                                         entry.BaseDllName_Buffer);
            info.full_path = read_remote_unicode_string(process, entry.FullDllName_Length,
                                                         entry.FullDllName_Buffer);
            if (!info.name.empty()) {
                modules.push_back(std::move(info));
            }
        }

        current = entry.InLoadOrder_Flink;
    }

    if (modules.empty()) {
        error = L"No modules found in target PEB.";
        return MM_EXECUTION_FAILED;
    }
    return MM_OK;
}

mm_status resolve_remote_export(HANDLE process, uintptr_t module_base,
                                 const char* export_name, uintptr_t& resolved,
                                 const std::vector<RemoteModuleInfo>& modules,
                                 std::wstring& error) {
    RemoteExportInfo info;
    auto st = read_remote_exports(process, module_base, info, error);
    if (st != MM_OK) return st;

    for (uint32_t i = 0; i < info.dir.NumberOfNames; ++i) {
        char name[256]{};
        syscall::NtReadVirtualMemory(process,
            reinterpret_cast<PVOID>(module_base + info.name_rvas[i]),
            name, sizeof(name) - 1, nullptr);
        name[255] = '\0';

        if (strcmp(name, export_name) == 0) {
            const uint32_t func_rva = info.func_rvas[info.ordinals[i]];
            if (is_forwarded(info, func_rva)) {
                return resolve_forward(process, module_base, func_rva, modules, resolved, error);
            }
            resolved = module_base + func_rva;
            return MM_OK;
        }
    }

    error = L"Export not found: ";
    error.append(export_name, export_name + strlen(export_name));
    return MM_EXECUTION_FAILED;
}

mm_status resolve_remote_export_ordinal(HANDLE process, uintptr_t module_base,
                                         uint16_t ordinal, uintptr_t& resolved,
                                         const std::vector<RemoteModuleInfo>& modules,
                                         std::wstring& error) {
    RemoteExportInfo info;
    auto st = read_remote_exports(process, module_base, info, error);
    if (st != MM_OK) return st;

    const uint32_t index = ordinal - static_cast<uint16_t>(info.dir.Base);
    if (index >= info.dir.NumberOfFunctions) {
        error = L"Ordinal out of range.";
        return MM_EXECUTION_FAILED;
    }

    const uint32_t func_rva = info.func_rvas[index];
    if (is_forwarded(info, func_rva)) {
        return resolve_forward(process, module_base, func_rva, modules, resolved, error);
    }
    resolved = module_base + func_rva;
    return MM_OK;
}
