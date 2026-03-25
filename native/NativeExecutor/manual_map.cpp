#include "manual_map.h"
#include "loader_stub.h"
#include "syscalls.h"

#include <algorithm>
#include <cstring>
#include <filesystem>

namespace {

// ── Alignment utility ──────────────────────────────────────────────────────────

size_t align_up(size_t value, size_t alignment) {
    const size_t r = value % alignment;
    return r == 0 ? value : value + (alignment - r);
}

// ── PE file reading ────────────────────────────────────────────────────────────

struct PeFile {
    std::vector<uint8_t> raw;
    const IMAGE_DOS_HEADER*       dos = nullptr;
    const IMAGE_NT_HEADERS64*     nt  = nullptr;
    const IMAGE_SECTION_HEADER*   sections = nullptr;
};

bool read_pe_file(const std::wstring& path, PeFile& pe, std::wstring& error) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        error = L"Cannot open DLL: " + path;
        return false;
    }

    LARGE_INTEGER sz{};
    if (!GetFileSizeEx(file, &sz) || sz.QuadPart < sizeof(IMAGE_DOS_HEADER)) {
        CloseHandle(file);
        error = L"Cannot read DLL size: " + path;
        return false;
    }

    pe.raw.resize(static_cast<size_t>(sz.QuadPart));
    DWORD bytes_read = 0;
    BOOL ok = ReadFile(file, pe.raw.data(), static_cast<DWORD>(pe.raw.size()), &bytes_read, nullptr);
    CloseHandle(file);
    if (!ok || bytes_read != pe.raw.size()) {
        error = L"Failed to read DLL: " + path;
        return false;
    }

    pe.dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(pe.raw.data());
    if (pe.dos->e_magic != IMAGE_DOS_SIGNATURE) {
        error = L"Invalid DOS signature: " + path;
        return false;
    }

    pe.nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(pe.raw.data() + pe.dos->e_lfanew);
    if (pe.nt->Signature != IMAGE_NT_SIGNATURE) {
        error = L"Invalid NT signature: " + path;
        return false;
    }
    if (pe.nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64) {
        error = L"Not an x64 DLL: " + path;
        return false;
    }
    if (!(pe.nt->FileHeader.Characteristics & IMAGE_FILE_DLL)) {
        error = L"PE is not a DLL: " + path;
        return false;
    }

    pe.sections = IMAGE_FIRST_SECTION(pe.nt);
    return true;
}

// ── Export cache building ──────────────────────────────────────────────────────

void cache_exports(const PeFile& pe, std::vector<MappedExport>& out) {
    out.clear();
    const auto& dir = pe.nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (dir.VirtualAddress == 0 || dir.Size == 0) return;

    // Find the section containing the export directory.
    auto rva_to_raw = [&](uint32_t rva) -> uint32_t {
        for (WORD i = 0; i < pe.nt->FileHeader.NumberOfSections; ++i) {
            const auto& s = pe.sections[i];
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData)
                return s.PointerToRawData + (rva - s.VirtualAddress);
        }
        return 0;
    };

    const auto exp_off = rva_to_raw(dir.VirtualAddress);
    if (exp_off == 0) return;
    const auto* exp = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(pe.raw.data() + exp_off);

    const auto names_off = rva_to_raw(exp->AddressOfNames);
    const auto ords_off  = rva_to_raw(exp->AddressOfNameOrdinals);
    const auto funcs_off = rva_to_raw(exp->AddressOfFunctions);
    if (names_off == 0 || ords_off == 0 || funcs_off == 0) return;

    const auto* names = reinterpret_cast<const uint32_t*>(pe.raw.data() + names_off);
    const auto* ords  = reinterpret_cast<const uint16_t*>(pe.raw.data() + ords_off);
    const auto* funcs = reinterpret_cast<const uint32_t*>(pe.raw.data() + funcs_off);

    for (DWORD i = 0; i < exp->NumberOfNames; ++i) {
        const auto n_off = rva_to_raw(names[i]);
        if (n_off == 0) continue;
        MappedExport me;
        me.name    = reinterpret_cast<const char*>(pe.raw.data() + n_off);
        me.ordinal = ords[i];
        me.rva     = funcs[ords[i]];
        out.push_back(std::move(me));
    }
}

// ── Section mapping (local buffer) ─────────────────────────────────────────────

void map_sections_local(const PeFile& pe, std::vector<uint8_t>& image, bool include_headers) {
    const auto image_size = pe.nt->OptionalHeader.SizeOfImage;
    image.assign(image_size, 0);

    // Copy PE headers if needed (for RtlInsertInvertedFunctionTable, erased later).
    if (include_headers) {
        const auto hdr_size = (std::min)(pe.nt->OptionalHeader.SizeOfHeaders,
                                          static_cast<DWORD>(pe.raw.size()));
        memcpy(image.data(), pe.raw.data(), hdr_size);
    }

    for (WORD i = 0; i < pe.nt->FileHeader.NumberOfSections; ++i) {
        const auto& sec = pe.sections[i];
        if (sec.SizeOfRawData == 0) continue;

        const auto src = sec.PointerToRawData;
        const auto dst = sec.VirtualAddress;
        const auto len = (std::min)(sec.SizeOfRawData,
                                     static_cast<DWORD>(image.size() - dst));
        if (dst + len <= image.size() && src + len <= pe.raw.size()) {
            memcpy(image.data() + dst, pe.raw.data() + src, len);
        }
    }
}

// ── Relocations ────────────────────────────────────────────────────────────────

bool process_relocations(std::vector<uint8_t>& image, const PeFile& pe,
                          uintptr_t remote_base, std::wstring& error) {
    const auto delta = static_cast<int64_t>(remote_base) -
                       static_cast<int64_t>(pe.nt->OptionalHeader.ImageBase);
    if (delta == 0) return true;

    const auto& reloc_dir = pe.nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BASERELOC];
    if (reloc_dir.VirtualAddress == 0 || reloc_dir.Size == 0) {
        // No relocation table; if delta != 0 this is fatal (no ASLR support).
        if (pe.nt->FileHeader.Characteristics & IMAGE_FILE_RELOCS_STRIPPED) {
            error = L"DLL has stripped relocations and could not be loaded at preferred base.";
            return false;
        }
        return true;
    }

    auto* block = reinterpret_cast<const IMAGE_BASE_RELOCATION*>(
        image.data() + reloc_dir.VirtualAddress);
    const auto* end = reinterpret_cast<const uint8_t*>(block) + reloc_dir.Size;

    while (reinterpret_cast<const uint8_t*>(block) < end && block->SizeOfBlock > 0) {
        const auto entry_count =
            (block->SizeOfBlock - sizeof(IMAGE_BASE_RELOCATION)) / sizeof(uint16_t);
        const auto* entries = reinterpret_cast<const uint16_t*>(block + 1);

        for (uint32_t i = 0; i < entry_count; ++i) {
            const uint16_t type   = entries[i] >> 12;
            const uint16_t offset = entries[i] & 0x0FFF;
            const auto target = block->VirtualAddress + offset;

            if (target >= image.size()) continue;

            switch (type) {
            case IMAGE_REL_BASED_ABSOLUTE:
                break;  // padding
            case IMAGE_REL_BASED_HIGHLOW: {
                auto* ptr = reinterpret_cast<uint32_t*>(image.data() + target);
                *ptr += static_cast<uint32_t>(delta);
                break;
            }
            case IMAGE_REL_BASED_DIR64: {
                auto* ptr = reinterpret_cast<uint64_t*>(image.data() + target);
                *ptr += static_cast<uint64_t>(delta);
                break;
            }
            default:
                error = L"Unsupported relocation type: " + std::to_wstring(type);
                return false;
            }
        }

        block = reinterpret_cast<const IMAGE_BASE_RELOCATION*>(
            reinterpret_cast<const uint8_t*>(block) + block->SizeOfBlock);
    }
    return true;
}

// ── Import resolution ──────────────────────────────────────────────────────────

// Find a module among the system-loaded remote modules.
const RemoteModuleInfo* find_remote_module(const std::vector<RemoteModuleInfo>& modules,
                                            const wchar_t* name) {
    for (const auto& m : modules) {
        if (_wcsicmp(m.name.c_str(), name) == 0) return &m;
    }
    return nullptr;
}

// Resolve API Set names (api-ms-win-*, ext-ms-*) to their real implementation DLL.
// Uses our own process to resolve the mapping, then looks up the real name in the target.
const RemoteModuleInfo* resolve_api_set(const std::vector<RemoteModuleInfo>& modules,
                                         const wchar_t* api_set_name) {
    // Only handle api-ms- and ext-ms- prefixes.
    if (_wcsnicmp(api_set_name, L"api-ms-", 7) != 0 &&
        _wcsnicmp(api_set_name, L"ext-ms-", 7) != 0)
        return nullptr;

    // Use GetModuleHandleW locally — the API set resolver in our process
    // maps these to the real DLL (ucrtbase.dll, kernelbase.dll, etc.).
    HMODULE local = GetModuleHandleW(api_set_name);
    if (!local) {
        // Try loading it as data to trigger the API set resolution.
        local = LoadLibraryExW(api_set_name, nullptr,
                               LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (local) {
            // Get real name, then free.
            wchar_t real_path[MAX_PATH]{};
            GetModuleFileNameW(local, real_path, MAX_PATH);
            FreeLibrary(local);
            // Extract basename.
            const wchar_t* base = wcsrchr(real_path, L'\\');
            base = base ? base + 1 : real_path;
            return find_remote_module(modules, base);
        }
        return nullptr;
    }

    wchar_t real_path[MAX_PATH]{};
    GetModuleFileNameW(local, real_path, MAX_PATH);
    const wchar_t* base = wcsrchr(real_path, L'\\');
    base = base ? base + 1 : real_path;
    return find_remote_module(modules, base);
}

// Find a module among already manually-mapped modules.
const MappedModule* find_mapped_module(const std::vector<MappedModule>& modules,
                                        const wchar_t* name) {
    for (const auto& m : modules) {
        if (_wcsicmp(m.name.c_str(), name) == 0) return &m;
    }
    return nullptr;
}

// Resolve a single import from a mapped module (local cache lookup).
bool resolve_from_mapped(const MappedModule& mod, const char* name, uintptr_t& out) {
    for (const auto& e : mod.exports) {
        if (e.name == name) {
            out = mod.base + e.rva;
            return true;
        }
    }
    return false;
}

bool resolve_ordinal_from_mapped(const MappedModule& mod, uint16_t ordinal, uintptr_t& out) {
    for (const auto& e : mod.exports) {
        if (e.ordinal == ordinal) {
            out = mod.base + e.rva;
            return true;
        }
    }
    return false;
}

mm_status resolve_imports(
    HANDLE process,
    std::vector<uint8_t>& image,
    const PeFile& pe,
    const std::wstring& dll_directory,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::vector<MappedModule>& mapped_modules,
    std::wstring& error)
{
    const auto& import_dir = pe.nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (import_dir.VirtualAddress == 0 || import_dir.Size == 0) return MM_OK;

    auto* desc = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(
        image.data() + import_dir.VirtualAddress);

    for (; desc->Name != 0; ++desc) {
        const char* dll_name_a = reinterpret_cast<const char*>(image.data() + desc->Name);
        const size_t name_len = strlen(dll_name_a);
        std::wstring dll_name_w(dll_name_a, dll_name_a + name_len);

        // Resolve the dependency module.
        uintptr_t dep_base = 0;
        const MappedModule* mapped_dep = nullptr;
        bool use_remote = false;

        // 1. Already manually mapped?
        mapped_dep = find_mapped_module(mapped_modules, dll_name_w.c_str());
        if (mapped_dep) {
            dep_base = mapped_dep->base;
        }

        // 2. Loaded in target (system DLL)?
        if (!mapped_dep) {
            auto* remote_mod = find_remote_module(remote_modules, dll_name_w.c_str());
            // Try API Set resolution (api-ms-win-*, ext-ms-*).
            if (!remote_mod) {
                remote_mod = resolve_api_set(remote_modules, dll_name_w.c_str());
            }
            if (remote_mod) {
                dep_base = remote_mod->base;
                use_remote = true;
            }
        }

        // 3. Try to find on disk next to parent DLL and recursively map.
        if (!dep_base) {
            std::wstring dep_path = dll_directory + L"\\" + dll_name_w;
            if (GetFileAttributesW(dep_path.c_str()) != INVALID_FILE_ATTRIBUTES) {
                auto st = manual_map_remote(process, dep_path, timeout_ms,
                                             remote_modules, mapped_modules, error);
                if (st != MM_OK) return st;

                mapped_dep = find_mapped_module(mapped_modules, dll_name_w.c_str());
                if (mapped_dep) dep_base = mapped_dep->base;
            }
        }

        // 4. Search System32 and recursively manual-map if found on disk.
        if (!dep_base) {
            wchar_t sys_dir[MAX_PATH]{};
            GetSystemDirectoryW(sys_dir, MAX_PATH);
            std::wstring sys_path = std::wstring(sys_dir) + L"\\" + dll_name_w;
            if (GetFileAttributesW(sys_path.c_str()) != INVALID_FILE_ATTRIBUTES) {
                auto st = manual_map_remote(process, sys_path, timeout_ms,
                                             remote_modules, mapped_modules, error);
                if (st != MM_OK) return st;

                mapped_dep = find_mapped_module(mapped_modules, dll_name_w.c_str());
                if (mapped_dep) dep_base = mapped_dep->base;
            }
        }

        if (!dep_base) {
            error = L"Cannot resolve dependency: " + dll_name_w;
            return MM_EXECUTION_FAILED;
        }

        // Walk the ILT/IAT.
        const auto ilt_rva = desc->OriginalFirstThunk ? desc->OriginalFirstThunk : desc->FirstThunk;
        auto* ilt = reinterpret_cast<const IMAGE_THUNK_DATA64*>(image.data() + ilt_rva);
        auto* iat = reinterpret_cast<IMAGE_THUNK_DATA64*>(image.data() + desc->FirstThunk);

        for (; ilt->u1.AddressOfData != 0; ++ilt, ++iat) {
            uintptr_t resolved = 0;

            if (IMAGE_SNAP_BY_ORDINAL64(ilt->u1.Ordinal)) {
                const auto ordinal = static_cast<uint16_t>(IMAGE_ORDINAL64(ilt->u1.Ordinal));
                if (mapped_dep && !use_remote) {
                    if (!resolve_ordinal_from_mapped(*mapped_dep, ordinal, resolved)) {
                        error = L"Ordinal " + std::to_wstring(ordinal) +
                                L" not found in " + dll_name_w;
                        return MM_EXECUTION_FAILED;
                    }
                } else {
                    auto st = resolve_remote_export_ordinal(process, dep_base, ordinal,
                                                             resolved, remote_modules, error);
                    if (st != MM_OK) return st;
                }
            } else {
                const auto* hint_name = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(
                    image.data() + static_cast<uint32_t>(ilt->u1.AddressOfData));
                const char* func_name = reinterpret_cast<const char*>(hint_name->Name);

                if (mapped_dep && !use_remote) {
                    if (!resolve_from_mapped(*mapped_dep, func_name, resolved)) {
                        error = L"Export not found: ";
                        error.append(func_name, func_name + strlen(func_name));
                        error += L" in " + dll_name_w;
                        return MM_EXECUTION_FAILED;
                    }
                } else {
                    auto st = resolve_remote_export(process, dep_base, func_name,
                                                     resolved, remote_modules, error);
                    if (st != MM_OK) return st;
                }
            }

            iat->u1.Function = resolved;
        }
    }

    return MM_OK;
}

// ── Section protections ────────────────────────────────────────────────────────

DWORD section_characteristics_to_protect(DWORD ch) {
    const bool exec  = (ch & IMAGE_SCN_MEM_EXECUTE) != 0;
    const bool read  = (ch & IMAGE_SCN_MEM_READ)    != 0;
    const bool write = (ch & IMAGE_SCN_MEM_WRITE)   != 0;

    if (exec && write) return PAGE_EXECUTE_READWRITE;
    if (exec && read)  return PAGE_EXECUTE_READ;
    if (exec)          return PAGE_EXECUTE;
    if (write)         return PAGE_READWRITE;
    if (read)          return PAGE_READONLY;
    return PAGE_NOACCESS;
}

mm_status set_section_protections(HANDLE process, uintptr_t remote_base,
                                   const PeFile& pe, std::wstring& error) {
    for (WORD i = 0; i < pe.nt->FileHeader.NumberOfSections; ++i) {
        const auto& sec = pe.sections[i];
        if (sec.Misc.VirtualSize == 0) continue;

        PVOID addr = reinterpret_cast<PVOID>(remote_base + sec.VirtualAddress);
        SIZE_T size = align_up(sec.Misc.VirtualSize, 0x1000);
        ULONG old_prot = 0;
        const ULONG new_prot = section_characteristics_to_protect(sec.Characteristics);

        auto st = syscall::NtProtectVirtualMemory(process, &addr, &size, new_prot, &old_prot);
        if (!NT_SUCCESS(st)) {
            error = L"NtProtectVirtualMemory failed for section at RVA 0x" +
                    std::to_wstring(sec.VirtualAddress);
            return MM_EXECUTION_FAILED;
        }
    }
    return MM_OK;
}

}  // namespace (end anonymous namespace before execute_remote_stub)

// ── Remote stub execution ──────────────────────────────────────────────────────
// Allocate code + data pages in target, execute, wait, cleanup (zero + free).

mm_status execute_remote_stub(HANDLE process, const void* code, size_t code_size,
                               const void* context, size_t context_size,
                               uint32_t timeout_ms, std::wstring& error) {
    if (!code || code_size == 0) {
        error = L"Stub code is null or empty.";
        return MM_EXECUTION_FAILED;
    }
    if (code_size > 0x100000) {
        error = L"Stub code size suspiciously large: " + std::to_wstring(code_size);
        return MM_EXECUTION_FAILED;
    }

    // 1. Allocate code page (RW initially).
    SIZE_T code_alloc = align_up(code_size, 0x1000);
    PVOID remote_code = nullptr;
    auto st = syscall::NtAllocateVirtualMemory(process, &remote_code, 0, &code_alloc,
                                                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!NT_SUCCESS(st)) {
        error = L"Failed to allocate remote code page.";
        return MM_EXECUTION_FAILED;
    }

    // 2. Allocate data page.
    SIZE_T data_alloc = align_up(context_size, 0x1000);
    PVOID remote_data = nullptr;
    st = syscall::NtAllocateVirtualMemory(process, &remote_data, 0, &data_alloc,
                                           MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!NT_SUCCESS(st)) {
        // Cleanup code page.
        SIZE_T zero_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_code, &zero_sz, MEM_RELEASE);
        error = L"Failed to allocate remote data page.";
        return MM_EXECUTION_FAILED;
    }

    mm_status result = MM_OK;

    // 3. Write code.
    st = syscall::NtWriteVirtualMemory(process, remote_code, code, code_size, nullptr);
    if (!NT_SUCCESS(st)) {
        error = L"Failed to write remote code.";
        result = MM_EXECUTION_FAILED;
        goto cleanup;
    }

    // 4. Write context data.
    st = syscall::NtWriteVirtualMemory(process, remote_data, context, context_size, nullptr);
    if (!NT_SUCCESS(st)) {
        error = L"Failed to write remote context.";
        result = MM_EXECUTION_FAILED;
        goto cleanup;
    }

    // 5. Set code page to PAGE_EXECUTE_READ.
    {
        PVOID prot_addr = remote_code;
        SIZE_T prot_size = code_alloc;
        ULONG old_prot = 0;
        st = syscall::NtProtectVirtualMemory(process, &prot_addr, &prot_size,
                                              PAGE_EXECUTE_READ, &old_prot);
        if (!NT_SUCCESS(st)) {
            error = L"Failed to set remote code to RX.";
            result = MM_EXECUTION_FAILED;
            goto cleanup;
        }
    }

    // 6. Create remote thread.
    {
        HANDLE thread = nullptr;
        st = syscall::NtCreateThreadEx(&thread, THREAD_ALL_ACCESS, nullptr, process,
                                        remote_code, remote_data,
                                        0, 0, 0, 0, nullptr);
        if (!NT_SUCCESS(st) || !thread) {
            error = L"NtCreateThreadEx failed.";
            result = MM_EXECUTION_FAILED;
            goto cleanup;
        }

        // 7. Wait.
        LARGE_INTEGER timeout{};
        timeout.QuadPart = timeout_ms == 0
            ? static_cast<LONGLONG>(-1)  // ~infinite (max negative = very long)
            : -static_cast<LONGLONG>(timeout_ms) * 10000LL;  // ms → 100ns units, relative
        st = syscall::NtWaitForSingleObject(thread, FALSE, timeout_ms == 0 ? nullptr : &timeout);

        DWORD exit_code = 0xFFFFFFFF;
        GetExitCodeThread(thread, &exit_code);
        syscall::NtClose(thread);

        if (!NT_SUCCESS(st)) {
            error = L"Remote thread timed out or wait failed.";
            result = MM_TIMEOUT;
            goto cleanup;
        }

        if (exit_code != 0) {
            error = L"Remote stub returned error code: " + std::to_wstring(exit_code);
            result = MM_EXECUTION_FAILED;
            goto cleanup;
        }
    }

cleanup:
    // Zero both allocations and free them.
    {
        std::vector<uint8_t> zeros((std::max)(code_alloc, data_alloc), 0);

        PVOID code_prot = remote_code;
        SIZE_T code_prot_sz = code_alloc;
        ULONG old_p = 0;
        syscall::NtProtectVirtualMemory(process, &code_prot, &code_prot_sz,
                                         PAGE_READWRITE, &old_p);
        syscall::NtWriteVirtualMemory(process, remote_code, zeros.data(), code_alloc, nullptr);
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_code, &free_sz, MEM_RELEASE);

        syscall::NtWriteVirtualMemory(process, remote_data, zeros.data(), data_alloc, nullptr);
        free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_data, &free_sz, MEM_RELEASE);
    }

    return result;
}

// ── TLS handling ───────────────────────────────────────────────────────────────

namespace {

struct TlsInfo {
    uint64_t address_of_index = 0;  // VA to the TLS index slot (in mapped image)
    std::vector<uint64_t> callbacks; // VA list of TLS callbacks (in mapped image)
};

void gather_tls_info(const std::vector<uint8_t>& image, const PeFile& pe,
                      uintptr_t remote_base, TlsInfo& tls) {
    const auto& dir = pe.nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS];
    if (dir.VirtualAddress == 0 || dir.Size == 0) return;

    const auto* tls_dir = reinterpret_cast<const IMAGE_TLS_DIRECTORY64*>(
        image.data() + dir.VirtualAddress);

    // The AddressOfIndex is an absolute VA in the preferred image; adjust for actual base.
    const auto delta = static_cast<int64_t>(remote_base) -
                       static_cast<int64_t>(pe.nt->OptionalHeader.ImageBase);
    tls.address_of_index = tls_dir->AddressOfIndex + delta;

    // Walk the callback array (null-terminated array of VAs).
    if (tls_dir->AddressOfCallBacks) {
        // The callback array is in the mapped image.  Compute its offset.
        const auto cb_array_va = tls_dir->AddressOfCallBacks + delta;
        const auto cb_array_offset = cb_array_va - remote_base;
        if (cb_array_offset < image.size()) {
            const auto* cbs = reinterpret_cast<const uint64_t*>(image.data() + cb_array_offset);
            while (*cbs) {
                tls.callbacks.push_back(*cbs + delta);
                ++cbs;
            }
        }
    }
}

// ── .pdata info ────────────────────────────────────────────────────────────────

struct PdataInfo {
    uint32_t rva   = 0;
    uint32_t count = 0;
};

PdataInfo get_pdata_info(const PeFile& pe) {
    const auto& dir = pe.nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
    if (dir.VirtualAddress == 0 || dir.Size == 0) return {};
    return { dir.VirtualAddress, dir.Size / static_cast<uint32_t>(sizeof(RUNTIME_FUNCTION)) };
}

// ── Inverted function table insertion (C++ exceptions + RTTI) ───────────────────
// Directly adds the module to ntdll's KiUserInvertedFunctionTable so that
// RtlPcToFileHeader can find it. This is what RtlInsertInvertedFunctionTable
// does internally (but it's not exported by name on all Windows versions).

#pragma pack(push, 1)
struct InvFuncTableEntry {
    uint64_t FunctionTable;  // PIMAGE_RUNTIME_FUNCTION_ENTRY
    uint64_t ImageBase;
    uint32_t SizeOfImage;
    uint32_t SizeOfTable;    // .pdata byte size
};
static_assert(sizeof(InvFuncTableEntry) == 24);

struct InvFuncTableHeader {
    uint32_t Count;
    uint32_t MaxCount;       // typically 512
    uint32_t Epoch;
    uint32_t Overflow;
};
static_assert(sizeof(InvFuncTableHeader) == 16);
#pragma pack(pop)

mm_status insert_inverted_function_table(
    HANDLE process,
    uintptr_t image_base,
    uint32_t image_size,
    uintptr_t pdata_addr,
    uint32_t pdata_size,
    std::wstring& error)
{
    // 1. Find KiUserInvertedFunctionTable address.
    // ntdll is at the same address in all processes on the same boot.
    HMODULE local_ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!local_ntdll) { error = L"GetModuleHandleW(ntdll) failed."; return MM_EXECUTION_FAILED; }
    auto* table_addr = reinterpret_cast<void*>(GetProcAddress(local_ntdll, "KiUserInvertedFunctionTable"));
    if (!table_addr) { error = L"KiUserInvertedFunctionTable not found."; return MM_EXECUTION_FAILED; }

    // 2. Read the header.
    InvFuncTableHeader hdr{};
    auto st = syscall::NtReadVirtualMemory(process, table_addr, &hdr, sizeof(hdr), nullptr);
    if (!NT_SUCCESS(st)) { error = L"Failed to read inverted function table header."; return MM_EXECUTION_FAILED; }

    if (hdr.Count >= hdr.MaxCount) { error = L"Inverted function table is full."; return MM_EXECUTION_FAILED; }

    // 3. Read all entries.
    const auto entries_addr = reinterpret_cast<uint8_t*>(table_addr) + sizeof(InvFuncTableHeader);
    std::vector<InvFuncTableEntry> entries(hdr.Count);
    if (hdr.Count > 0) {
        st = syscall::NtReadVirtualMemory(process, entries_addr,
                                           entries.data(), hdr.Count * sizeof(InvFuncTableEntry), nullptr);
        if (!NT_SUCCESS(st)) { error = L"Failed to read inverted function table entries."; return MM_EXECUTION_FAILED; }
    }

    // 4. Find insertion point (sorted by ImageBase ascending).
    size_t insert_pos = 0;
    for (; insert_pos < entries.size(); ++insert_pos) {
        if (entries[insert_pos].ImageBase > image_base) break;
    }

    // 5. Insert our entry.
    InvFuncTableEntry our_entry{};
    our_entry.FunctionTable = pdata_addr;
    our_entry.ImageBase     = image_base;
    our_entry.SizeOfImage   = image_size;
    our_entry.SizeOfTable   = pdata_size;
    entries.insert(entries.begin() + insert_pos, our_entry);

    // 6. Write entries back.
    st = syscall::NtWriteVirtualMemory(process, entries_addr,
                                        entries.data(), entries.size() * sizeof(InvFuncTableEntry), nullptr);
    if (!NT_SUCCESS(st)) { error = L"Failed to write inverted function table entries."; return MM_EXECUTION_FAILED; }

    // 7. Update header (Count + Epoch).
    hdr.Count++;
    hdr.Epoch++;
    st = syscall::NtWriteVirtualMemory(process, table_addr, &hdr, sizeof(hdr), nullptr);
    if (!NT_SUCCESS(st)) { error = L"Failed to update inverted function table header."; return MM_EXECUTION_FAILED; }

    return MM_OK;
}

// ── Filename extraction ────────────────────────────────────────────────────────

std::wstring filename_from_path(const std::wstring& path) {
    const auto pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? path : path.substr(pos + 1);
}

std::wstring directory_from_path(const std::wstring& path) {
    const auto pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? L"." : path.substr(0, pos);
}

}  // namespace

// ── Public API ─────────────────────────────────────────────────────────────────

mm_status manual_map_remote(
    HANDLE process,
    const std::wstring& dll_path,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::vector<MappedModule>& mapped_modules,
    std::wstring& error)
{
    const auto dll_name = filename_from_path(dll_path);
    const auto dll_dir  = directory_from_path(dll_path);

    // ── Check if already mapped ────────────────────────────────────────────────
    for (const auto& m : mapped_modules) {
        if (_wcsicmp(m.name.c_str(), dll_name.c_str()) == 0) {
            return MM_OK;  // already done
        }
    }

    // ── Check if already loaded in target (e.g., vcruntime140.dll) ─────────────
    // If so, don't re-map — just register in mapped_modules for cross-ref.
    {
        auto* existing = find_remote_module(remote_modules, dll_name.c_str());
        if (!existing) existing = resolve_api_set(remote_modules, dll_name.c_str());
        if (existing) {
            MappedModule entry;
            entry.path = dll_path;
            entry.name = dll_name;
            entry.base = existing->base;
            entry.size = existing->size;
            // No export cache needed — imports resolve via remote export table.
            mapped_modules.push_back(std::move(entry));
            return MM_OK;
        }
    }

    // ── 1. Read PE from disk ───────────────────────────────────────────────────
    PeFile pe;
    if (!read_pe_file(dll_path, pe, error)) return MM_EXECUTION_FAILED;

    // ── 2. Cache exports (before we lose headers) ──────────────────────────────
    std::vector<MappedExport> cached_exports;
    cache_exports(pe, cached_exports);

    // ── 3. Allocate remote image (RW) ──────────────────────────────────────────
    SIZE_T image_size = pe.nt->OptionalHeader.SizeOfImage;
    PVOID remote_base = nullptr;
    auto st = syscall::NtAllocateVirtualMemory(process, &remote_base, 0, &image_size,
                                                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!NT_SUCCESS(st)) {
        error = L"NtAllocateVirtualMemory failed for image.";
        return MM_EXECUTION_FAILED;
    }

    const auto rb = reinterpret_cast<uintptr_t>(remote_base);

    // Register now so recursive dependency resolution can find us.
    MappedModule self_entry;
    self_entry.path    = dll_path;
    self_entry.name    = dll_name;
    self_entry.base    = rb;
    self_entry.size    = static_cast<uint32_t>(pe.nt->OptionalHeader.SizeOfImage);
    self_entry.exports = cached_exports;
    mapped_modules.push_back(std::move(self_entry));

    auto remove_self = [&]() {
        // On failure, remove the entry we just added.
        for (auto it = mapped_modules.begin(); it != mapped_modules.end(); ++it) {
            if (it->base == rb) { mapped_modules.erase(it); break; }
        }
    };

    // ── 4. Map sections locally ────────────────────────────────────────────────
    // Include PE headers initially (needed for RtlInsertInvertedFunctionTable).
    // Headers are erased from remote memory after exception table registration.
    std::vector<uint8_t> local_image;
    map_sections_local(pe, local_image, true);

    // ── 5. Process relocations ─────────────────────────────────────────────────
    if (!process_relocations(local_image, pe, rb, error)) {
        remove_self();
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
        return MM_EXECUTION_FAILED;
    }

    // ── 6. Resolve imports ─────────────────────────────────────────────────────
    {
        auto rstatus = resolve_imports(process, local_image, pe, dll_dir, timeout_ms,
                                        remote_modules, mapped_modules, error);
        if (rstatus != MM_OK) {
            remove_self();
            SIZE_T free_sz = 0;
            syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
            return rstatus;
        }
    }

    // ── 7. Gather TLS info (before writing image) ──────────────────────────────
    TlsInfo tls{};
    gather_tls_info(local_image, pe, rb, tls);

    // ── 8. Write image to target ───────────────────────────────────────────────
    st = syscall::NtWriteVirtualMemory(process, remote_base,
                                        local_image.data(), local_image.size(), nullptr);
    // Wipe local copy.
    SecureZeroMemory(local_image.data(), local_image.size());
    local_image.clear();

    if (!NT_SUCCESS(st)) {
        error = L"NtWriteVirtualMemory failed for image.";
        remove_self();
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
        return MM_EXECUTION_FAILED;
    }

    // ── 9. Set section protections ─────────────────────────────────────────────
    {
        auto pst = set_section_protections(process, rb, pe, error);
        if (pst != MM_OK) {
            remove_self();
            SIZE_T free_sz = 0;
            syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
            return pst;
        }
    }

    // ── 10. Register in inverted function table (C++ exceptions + RTTI) ──────
    {
        const auto pdata = get_pdata_info(pe);
        if (pdata.rva && pdata.count > 0) {
            auto inv_st = insert_inverted_function_table(
                process, rb, static_cast<uint32_t>(pe.nt->OptionalHeader.SizeOfImage),
                rb + pdata.rva,
                pdata.count * static_cast<uint32_t>(sizeof(RUNTIME_FUNCTION)),
                error);
            if (inv_st != MM_OK) {
                // Log but continue — RtlAddFunctionTable fallback in stub handles SEH.
                error.clear();
            }
        }
    }

    // ── 11. Execute DllMain via loader stub ────────────────────────────────────
    {
        // Build context.
        const auto pdata = get_pdata_info(pe);
        const auto ep = pe.nt->OptionalHeader.AddressOfEntryPoint;

        // Resolve runtime helpers from remote ntdll/kernel32 (best-effort).
        uintptr_t fn_rtl_add = 0;
        uintptr_t fn_tls_alloc = 0;
        {
            std::wstring ignore_err;
            for (const auto& mod : remote_modules) {
                if (_wcsicmp(mod.name.c_str(), L"ntdll.dll") == 0 && fn_rtl_add == 0) {
                    resolve_remote_export(process, mod.base, "RtlAddFunctionTable",
                                           fn_rtl_add, remote_modules, ignore_err);
                }
                if (_wcsicmp(mod.name.c_str(), L"kernel32.dll") == 0) {
                    if (fn_rtl_add == 0)
                        resolve_remote_export(process, mod.base, "RtlAddFunctionTable",
                                               fn_rtl_add, remote_modules, ignore_err);
                    if (fn_tls_alloc == 0)
                        resolve_remote_export(process, mod.base, "TlsAlloc",
                                               fn_tls_alloc, remote_modules, ignore_err);
                }
            }
        }

        const size_t ctx_size = sizeof(DllMainContext) +
                                tls.callbacks.size() * sizeof(uint64_t);
        std::vector<uint8_t> ctx_buf(ctx_size, 0);
        auto* ctx = reinterpret_cast<DllMainContext*>(ctx_buf.data());

        ctx->image_base              = rb;
        ctx->entry_point             = ep ? rb + ep : 0;
        ctx->fn_rtl_add_function_table = fn_rtl_add;
        ctx->pdata_base              = pdata.rva ? rb + pdata.rva : 0;
        ctx->pdata_entry_count       = pdata.count;
        ctx->fn_tls_alloc            = fn_tls_alloc;
        ctx->tls_index_addr          = tls.address_of_index;
        ctx->tls_callback_count      = static_cast<uint32_t>(tls.callbacks.size());

        auto* cb_arr = reinterpret_cast<uint64_t*>(ctx + 1);
        for (size_t i = 0; i < tls.callbacks.size(); ++i) {
            cb_arr[i] = tls.callbacks[i];
        }

        // Get the dllmain_stub code.
        const auto stub = get_stub_info(reinterpret_cast<void*>(&dllmain_stub));
        if (!stub.code || stub.size == 0) {
            error = L"Failed to locate dllmain_stub code.";
            remove_self();
            SIZE_T free_sz = 0;
            syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
            return MM_EXECUTION_FAILED;
        }

        auto exec_st = execute_remote_stub(process, stub.code, stub.size,
                                            ctx_buf.data(), ctx_buf.size(),
                                            timeout_ms, error);
        SecureZeroMemory(ctx_buf.data(), ctx_buf.size());

        if (exec_st != MM_OK) {
            remove_self();
            SIZE_T free_sz = 0;
            syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
            return exec_st;
        }
    }

    // ── 12. Erase PE headers from remote memory (stealth) ─────────────────────
    // Zero the DOS/NT/section headers.  The inverted function table and
    // RtlAddFunctionTable have already cached the .pdata information.
    {
        const auto hdr_size = pe.nt->OptionalHeader.SizeOfHeaders;
        std::vector<uint8_t> zeros(hdr_size, 0);
        syscall::NtWriteVirtualMemory(process, remote_base, zeros.data(), hdr_size, nullptr);
    }

    // Success — module stays mapped.  Wipe PE data from local memory.
    SecureZeroMemory(pe.raw.data(), pe.raw.size());
    return MM_OK;
}
