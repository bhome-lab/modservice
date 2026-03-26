#include "manual_map.h"
#include "loader_stub.h"
#include "syscalls.h"
#include "win_offsets.h"
#include "sysinfo_parser.h"

#include <algorithm>
#include <cstring>
#include <filesystem>
#include <intrin.h>

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
                if (target + sizeof(uint32_t) > image.size()) continue;
                auto* ptr = reinterpret_cast<uint32_t*>(image.data() + target);
                *ptr += static_cast<uint32_t>(delta);
                break;
            }
            case IMAGE_REL_BASED_DIR64: {
                if (target + sizeof(uint64_t) > image.size()) continue;
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

// ── API Set v6 structures (Windows 10+) ──────────────────────────────────────

struct ApiSetNamespace {
    uint32_t Version;
    uint32_t Size;
    uint32_t Flags;
    uint32_t Count;
    uint32_t EntryOffset;
    uint32_t HashOffset;
    uint32_t HashFactor;
};

struct ApiSetNamespaceEntry {
    uint32_t Flags;
    uint32_t NameOffset;
    uint32_t NameLength;
    uint32_t HashedLength;
    uint32_t ValueOffset;
    uint32_t ValueCount;
};

struct ApiSetValueEntry {
    uint32_t Flags;
    uint32_t NameOffset;
    uint32_t NameLength;
    uint32_t ValueOffset;
    uint32_t ValueLength;
};

struct ApiSetHashEntry {
    uint32_t Hash;
    uint32_t Index;
};

// Resolve an API set name to its host DLL name by parsing PEB.ApiSetMap directly.
// No LoadLibrary/GetModuleHandle calls — pure data reads, no side effects.
std::wstring resolve_api_set_name(const wchar_t* api_set_name) {
    if (_wcsnicmp(api_set_name, L"api-", 4) != 0 &&
        _wcsnicmp(api_set_name, L"ext-", 4) != 0)
        return {};

    // Strip .dll extension if present.
    std::wstring name(api_set_name);
    if (name.size() > 4 && _wcsicmp(name.c_str() + name.size() - 4, L".dll") == 0)
        name.resize(name.size() - 4);

    // Read ApiSetMap from our PEB using dynamically-discovered offset.
    const auto& off = win_offsets();
    if (!off.api_set_available) return {};

    auto* peb_base = reinterpret_cast<const uint8_t*>(
        reinterpret_cast<void*>(__readgsqword(0x60)));
    auto* ns = *reinterpret_cast<const ApiSetNamespace* const*>(peb_base + off.peb_api_set_map);
    if (!ns) return {};

    auto ns_base = reinterpret_cast<uintptr_t>(ns);

    // Find hashed length (up to last hyphen).
    size_t hashed_chars = name.size();
    for (size_t i = name.size(); i > 0; --i) {
        if (name[i - 1] == L'-') { hashed_chars = i - 1; break; }
    }

    // Compute hash.
    uint32_t hash = 0;
    for (size_t i = 0; i < hashed_chars; ++i) {
        wchar_t c = name[i];
        if (c >= L'A' && c <= L'Z') c += 32;
        hash = hash * ns->HashFactor + static_cast<uint32_t>(c);
    }

    // Binary search the hash table.
    auto* hash_entries = reinterpret_cast<const ApiSetHashEntry*>(ns_base + ns->HashOffset);
    int lo = 0, hi = static_cast<int>(ns->Count) - 1;
    const ApiSetNamespaceEntry* found_entry = nullptr;

    while (lo <= hi) {
        int mid = (lo + hi) >> 1;
        if (hash < hash_entries[mid].Hash)       hi = mid - 1;
        else if (hash > hash_entries[mid].Hash)  lo = mid + 1;
        else {
            auto* entries = reinterpret_cast<const ApiSetNamespaceEntry*>(ns_base + ns->EntryOffset);
            const auto& candidate = entries[hash_entries[mid].Index];
            auto* cand_name = reinterpret_cast<const wchar_t*>(ns_base + candidate.NameOffset);
            size_t cand_chars = candidate.HashedLength / sizeof(wchar_t);
            if (hashed_chars == cand_chars &&
                _wcsnicmp(name.c_str(), cand_name, cand_chars) == 0)
                found_entry = &candidate;
            break;
        }
    }

    if (!found_entry || found_entry->ValueCount == 0) return {};

    // Select the default value entry (last one with NameLength == 0).
    auto* values = reinterpret_cast<const ApiSetValueEntry*>(ns_base + found_entry->ValueOffset);
    const ApiSetValueEntry* selected = &values[0];

    if (selected->ValueLength == 0) return {};
    auto* host = reinterpret_cast<const wchar_t*>(ns_base + selected->ValueOffset);
    return std::wstring(host, selected->ValueLength / sizeof(wchar_t));
}

// Resolve API Set names (api-ms-win-*, ext-ms-*) to their real implementation DLL.
// Parses PEB.ApiSetMap directly — no LoadLibrary, no GetModuleHandle, no ETW events.
const RemoteModuleInfo* resolve_api_set(const std::vector<RemoteModuleInfo>& modules,
                                         const wchar_t* api_set_name) {
    if (_wcsnicmp(api_set_name, L"api-ms-", 7) != 0 &&
        _wcsnicmp(api_set_name, L"ext-ms-", 7) != 0)
        return nullptr;

    std::wstring host_dll = resolve_api_set_name(api_set_name);
    if (host_dll.empty()) return nullptr;

    return find_remote_module(modules, host_dll.c_str());
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
    uint32_t target_pid,
    std::vector<uint8_t>& image,
    const PeFile& pe,
    const std::wstring& dll_directory,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::vector<MappedModule>& mapped_modules,
    std::wstring& error,
    uint32_t depth)
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
                auto st = manual_map_remote(process, target_pid, dep_path, timeout_ms,
                                             remote_modules, mapped_modules, error, depth + 1);
                if (st != MM_OK) return st;

                mapped_dep = find_mapped_module(mapped_modules, dll_name_w.c_str());
                if (mapped_dep) dep_base = mapped_dep->base;
            }
        }

        // 4. Search System32 and recursively manual-map if found on disk.
        //    Skip api-ms-win-* / ext-ms-* stub DLLs — they are API set forwarders,
        //    not real DLLs.  If API set resolution (step 2) didn't find the target,
        //    the DLL simply isn't loaded in the target and must be mapped as a real DLL.
        if (!dep_base &&
            _wcsnicmp(dll_name_w.c_str(), L"api-ms-", 7) != 0 &&
            _wcsnicmp(dll_name_w.c_str(), L"ext-ms-", 7) != 0) {
            wchar_t sys_dir[MAX_PATH]{};
            GetSystemDirectoryW(sys_dir, MAX_PATH);
            std::wstring sys_path = std::wstring(sys_dir) + L"\\" + dll_name_w;
            if (GetFileAttributesW(sys_path.c_str()) != INVALID_FILE_ATTRIBUTES) {
                auto st = manual_map_remote(process, target_pid, sys_path, timeout_ms,
                                             remote_modules, mapped_modules, error, depth + 1);
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

// ── Shared dependency resolution ──────────────────────────────────────────────
// Used by both resolve_imports and resolve_delay_imports.

struct DepResolution {
    uintptr_t           base = 0;
    const MappedModule* mapped = nullptr;
    bool                use_remote = false;
};

DepResolution resolve_dependency(
    HANDLE process,
    uint32_t target_pid,
    const std::wstring& dll_name_w,
    const std::wstring& dll_directory,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::vector<MappedModule>& mapped_modules,
    std::wstring& error,
    uint32_t depth)
{
    DepResolution dep;

    // 1. Already manually mapped?
    dep.mapped = find_mapped_module(mapped_modules, dll_name_w.c_str());
    if (dep.mapped) { dep.base = dep.mapped->base; return dep; }

    // 2. Loaded in target (system DLL)?
    auto* remote_mod = find_remote_module(remote_modules, dll_name_w.c_str());
    if (!remote_mod) remote_mod = resolve_api_set(remote_modules, dll_name_w.c_str());
    if (remote_mod) {
        dep.base = remote_mod->base;
        dep.use_remote = true;
        return dep;
    }

    // 3. Try to find on disk next to parent DLL.
    {
        std::wstring dep_path = dll_directory + L"\\" + dll_name_w;
        if (GetFileAttributesW(dep_path.c_str()) != INVALID_FILE_ATTRIBUTES) {
            auto st = manual_map_remote(process, target_pid, dep_path, timeout_ms,
                                         remote_modules, mapped_modules, error, depth + 1);
            if (st != MM_OK) return dep;
            dep.mapped = find_mapped_module(mapped_modules, dll_name_w.c_str());
            if (dep.mapped) dep.base = dep.mapped->base;
            return dep;
        }
    }

    // 4. Search System32 (skip api-ms-/ext-ms- stub DLLs).
    if (_wcsnicmp(dll_name_w.c_str(), L"api-ms-", 7) != 0 &&
        _wcsnicmp(dll_name_w.c_str(), L"ext-ms-", 7) != 0) {
        wchar_t sys_dir[MAX_PATH]{};
        GetSystemDirectoryW(sys_dir, MAX_PATH);
        std::wstring sys_path = std::wstring(sys_dir) + L"\\" + dll_name_w;
        if (GetFileAttributesW(sys_path.c_str()) != INVALID_FILE_ATTRIBUTES) {
            auto st = manual_map_remote(process, target_pid, sys_path, timeout_ms,
                                         remote_modules, mapped_modules, error, depth + 1);
            if (st != MM_OK) return dep;
            dep.mapped = find_mapped_module(mapped_modules, dll_name_w.c_str());
            if (dep.mapped) dep.base = dep.mapped->base;
        }
    }

    return dep;
}

// ── Delay-load import resolution ────────────────────────────────────────────

mm_status resolve_delay_imports(
    HANDLE process,
    uint32_t target_pid,
    std::vector<uint8_t>& image,
    const PeFile& pe,
    const std::wstring& dll_directory,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::vector<MappedModule>& mapped_modules,
    std::wstring& error,
    uint32_t depth)
{
    const auto& delay_dir = pe.nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];
    if (delay_dir.VirtualAddress == 0 || delay_dir.Size == 0) return MM_OK;

    struct DelayLoadDescriptor {
        DWORD grAttrs;
        DWORD rvaDLLName;
        DWORD rvaHmod;
        DWORD rvaIAT;
        DWORD rvaINT;
        DWORD rvaBoundIAT;
        DWORD rvaUnloadIAT;
        DWORD dwTimeStamp;
    };
    static_assert(sizeof(DelayLoadDescriptor) == 32);

    auto* desc = reinterpret_cast<const DelayLoadDescriptor*>(
        image.data() + delay_dir.VirtualAddress);

    for (; desc->rvaDLLName != 0; ++desc) {
        const char* dll_name_a = reinterpret_cast<const char*>(image.data() + desc->rvaDLLName);
        const size_t name_len = strlen(dll_name_a);
        std::wstring dll_name_w(dll_name_a, dll_name_a + name_len);

        auto dep = resolve_dependency(process, target_pid, dll_name_w, dll_directory, timeout_ms,
                                       remote_modules, mapped_modules, error, depth);
        if (!dep.base) {
            error = L"Cannot resolve delay-load dependency: " + dll_name_w;
            return MM_EXECUTION_FAILED;
        }

        // Walk INT (name table) and patch IAT (address table).
        auto* ilt = reinterpret_cast<const IMAGE_THUNK_DATA64*>(image.data() + desc->rvaINT);
        auto* iat = reinterpret_cast<IMAGE_THUNK_DATA64*>(image.data() + desc->rvaIAT);

        for (; ilt->u1.AddressOfData != 0; ++ilt, ++iat) {
            uintptr_t resolved = 0;

            if (IMAGE_SNAP_BY_ORDINAL64(ilt->u1.Ordinal)) {
                const auto ordinal = static_cast<uint16_t>(IMAGE_ORDINAL64(ilt->u1.Ordinal));
                if (dep.mapped && !dep.use_remote) {
                    if (!resolve_ordinal_from_mapped(*dep.mapped, ordinal, resolved)) {
                        error = L"Delay-load ordinal " + std::to_wstring(ordinal) +
                                L" not found in " + dll_name_w;
                        return MM_EXECUTION_FAILED;
                    }
                } else {
                    auto st = resolve_remote_export_ordinal(process, dep.base, ordinal,
                                                             resolved, remote_modules, error);
                    if (st != MM_OK) return st;
                }
            } else {
                const auto* hint_name = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(
                    image.data() + static_cast<uint32_t>(ilt->u1.AddressOfData));
                const char* func_name = reinterpret_cast<const char*>(hint_name->Name);

                if (dep.mapped && !dep.use_remote) {
                    if (!resolve_from_mapped(*dep.mapped, func_name, resolved)) {
                        error = L"Delay-load export not found: ";
                        error.append(func_name, func_name + strlen(func_name));
                        error += L" in " + dll_name_w;
                        return MM_EXECUTION_FAILED;
                    }
                } else {
                    auto st = resolve_remote_export(process, dep.base, func_name,
                                                     resolved, remote_modules, error);
                    if (st != MM_OK) return st;
                }
            }

            iat->u1.Function = resolved;
        }

        // Patch the HMODULE store so the delay-load helper thinks the DLL is loaded.
        if (desc->rvaHmod != 0) {
            auto* hmod_slot = reinterpret_cast<uint64_t*>(image.data() + desc->rvaHmod);
            *hmod_slot = dep.base;
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

// ── SYSTEM_THREAD_INFORMATION layout (x64) ──────────────────────────────────
// +0x00 KernelTime      LARGE_INTEGER  (8)
// +0x08 UserTime        LARGE_INTEGER  (8)
// +0x10 CreateTime      LARGE_INTEGER  (8)
// +0x18 WaitTime        ULONG          (4)
// +0x1C (padding)                      (4)
// +0x20 StartAddress    PVOID          (8)
// +0x28 ClientId        CLIENT_ID      (16)  — UniqueProcess(8) + UniqueThread(8)
// +0x38 Priority        LONG           (4)
// +0x3C BasePriority    LONG           (4)
// +0x40 ContextSwitches ULONG          (4)
// +0x44 ThreadState     ULONG          (4)   — 5 = Waiting
// +0x48 WaitReason      ULONG          (4)
// Total: 0x50 bytes

// ── Thread enumeration for hijacking ────────────────────────────────────────

// Read the remote PEB.LoaderLock address and the owning thread ID.
DWORD get_loader_lock_owner(HANDLE process) {
    const auto& off = win_offsets();

    NT_PROCESS_BASIC_INFORMATION pbi{};
    syscall::NtQueryInformationProcess(process, 0, &pbi, sizeof(pbi), nullptr);
    if (pbi.PebBaseAddress == 0) return 0;

    uint64_t loader_lock_ptr = 0;
    syscall::NtReadVirtualMemory(process,
        reinterpret_cast<PVOID>(pbi.PebBaseAddress + off.peb_loader_lock),
        &loader_lock_ptr, sizeof(loader_lock_ptr), nullptr);
    if (loader_lock_ptr == 0) return 0;

    uint64_t owning_thread = 0;
    syscall::NtReadVirtualMemory(process,
        reinterpret_cast<PVOID>(loader_lock_ptr + off.cs_owning_thread),
        &owning_thread, sizeof(owning_thread), nullptr);

    return static_cast<DWORD>(owning_thread);
}

DWORD find_target_thread(HANDLE process, uint32_t target_pid) {
    DWORD loader_lock_tid = get_loader_lock_owner(process);

    auto proc_info = query_process_threads(target_pid);
    if (!proc_info) return 0;

    DWORD best_tid = 0;
    uint32_t best_score = 0;

    for (const auto& ti : proc_info->threads) {
        DWORD tid = static_cast<DWORD>(ti.unique_thread_id);

        // Skip threads that hold the loader lock.
        if (tid == loader_lock_tid) continue;

        // Only consider threads in Waiting state (5).
        if (ti.thread_state != 5) continue;

        // Score by wait reason preference:
        // WrUserRequest(5)=best, WrQueue(4)=good, WrDelayExecution(6)=good,
        // WrLpcReceive(9)=ok, others=less preferred
        uint32_t score = 1;
        if (ti.wait_reason == 5 || ti.wait_reason == 4 || ti.wait_reason == 6)
            score = 100;
        else if (ti.wait_reason == 9 || ti.wait_reason == 13)
            score = 50;

        // Prefer longer waits (more idle).
        score += (ti.wait_time > 10000) ? 10 : (ti.wait_time / 1000);

        if (score > best_score) {
            best_score = score;
            best_tid = tid;
        }
    }

    return best_tid;
}

}  // namespace (end anonymous namespace before execute_remote_stub)

// ── Remote stub execution via thread hijacking ──────────────────────────────
// Allocates code + data pages, hijacks an existing thread, waits for
// completion via polling, restores context, and cleans up.

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

    NT_PROCESS_BASIC_INFORMATION _pbi{};
    syscall::NtQueryInformationProcess(process, 0, &_pbi, sizeof(_pbi), nullptr);

    // The context struct has HijackHeader at the start. Set hijack_mode = 1
    // so the stub signals completion and exits the thread cleanly via
    // RtlExitUserThread (no spinloop, no TerminateThread).
    std::vector<uint8_t> ctx_copy(context_size);
    memcpy(ctx_copy.data(), context, context_size);
    auto* hdr = reinterpret_cast<HijackHeader*>(ctx_copy.data());
    hdr->completed = 0;
    hdr->result = 0;
    hdr->hijack_mode = 1;

    // Resolve RtlExitUserThread from the target's ntdll for clean thread exit.
    {
        HMODULE local_ntdll = GetModuleHandleW(L"ntdll.dll");
        auto* local_fn = syscall_find_local_export(local_ntdll, "RtlExitUserThread");
        // Compute the remote address assuming ntdll is at the same base in the target
        // (ntdll is always loaded at the same address system-wide on a given boot).
        hdr->fn_exit_thread = local_fn ? reinterpret_cast<uint64_t>(local_fn) : 0;
    }

    // 1. Allocate code page.
    SIZE_T code_alloc = align_up(code_size, 0x1000);
    PVOID remote_code = nullptr;
    auto st = syscall::NtAllocateVirtualMemory(process, &remote_code, 0, &code_alloc,
                                                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!NT_SUCCESS(st)) { error = L"Failed to allocate remote code page."; return MM_EXECUTION_FAILED; }

    // 2. Allocate data page.
    SIZE_T data_alloc = align_up(context_size, 0x1000);
    PVOID remote_data = nullptr;
    st = syscall::NtAllocateVirtualMemory(process, &remote_data, 0, &data_alloc,
                                           MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!NT_SUCCESS(st)) {
        SIZE_T z = 0; syscall::NtFreeVirtualMemory(process, &remote_code, &z, MEM_RELEASE);
        error = L"Failed to allocate remote data page."; return MM_EXECUTION_FAILED;
    }

    mm_status result = MM_OK;

    // 3. Write code (the real stub directly — no wrapper).
    st = syscall::NtWriteVirtualMemory(process, remote_code, code, code_size, nullptr);
    if (!NT_SUCCESS(st)) { error = L"Failed to write stub code."; result = MM_EXECUTION_FAILED; goto cleanup; }

    // 4. Write context data (with hijack_mode set).
    st = syscall::NtWriteVirtualMemory(process, remote_data, ctx_copy.data(), ctx_copy.size(), nullptr);
    if (!NT_SUCCESS(st)) { error = L"Failed to write context."; result = MM_EXECUTION_FAILED; goto cleanup; }
    SecureZeroMemory(ctx_copy.data(), ctx_copy.size());

    // 5. Set code page to RX.
    { PVOID p = remote_code; SIZE_T s = code_alloc; ULONG o = 0;
      st = syscall::NtProtectVirtualMemory(process, &p, &s, PAGE_EXECUTE_READ, &o);
      if (!NT_SUCCESS(st)) { error = L"Failed to set RX."; result = MM_EXECUTION_FAILED; goto cleanup; } }

    // 6. Execute via NtCreateThreadEx with direct stub execution.
    // The thread runs the stub code directly. On completion (hijack_mode=1),
    // the stub signals via HijackHeader.completed and exits cleanly via
    // RtlExitUserThread — no spinloop, no TerminateThread.
    {
        HANDLE hThread = nullptr;
        st = syscall::NtCreateThreadEx(&hThread, THREAD_ALL_ACCESS, nullptr, process,
                                        remote_code,  // start address = stub entry point
                                        remote_data,  // parameter = context (HijackHeader at start)
                                        0, 0, 0, 0, nullptr);
        if (!NT_SUCCESS(st) || !hThread) {
            error = L"NtCreateThreadEx failed.";
            result = MM_EXECUTION_FAILED;
            goto cleanup;
        }
        ScopedNtHandle thread_handle(hThread);

        // Poll for completion via HijackHeader.
        {
            const uint32_t poll_interval_ms = 1;
            const uint32_t max_polls = (timeout_ms == 0 ? 60000 : timeout_ms) / poll_interval_ms;
            bool completed = false;

            for (uint32_t p = 0; p < max_polls; ++p) {
                HijackHeader poll_hdr{};
                syscall::NtReadVirtualMemory(process, remote_data,
                                              &poll_hdr, sizeof(poll_hdr), nullptr);
                if (poll_hdr.completed == 1) {
                    if (poll_hdr.result != 0) {
                        error = L"Remote stub returned error code: " +
                                std::to_wstring(poll_hdr.result);
                        result = MM_EXECUTION_FAILED;
                    }
                    completed = true;
                    break;
                }
                LARGE_INTEGER delay{};
                delay.QuadPart = -10000LL; // 1ms
                syscall::NtDelayExecution(FALSE, &delay);
            }

            if (!completed) {
                error = L"Remote stub timed out.";
                result = MM_TIMEOUT;
            }
        }

        // Wait for the thread to exit cleanly (RtlExitUserThread).
        // Short timeout — the stub already signaled completion, so exit is imminent.
        LARGE_INTEGER wait_timeout{};
        wait_timeout.QuadPart = -50000000LL; // 5 seconds
        syscall::NtWaitForSingleObject(thread_handle.get(), FALSE, &wait_timeout);
    }

cleanup:
    // Zero and free both allocations.
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

namespace {

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

// Suspend all threads in the target process (for atomic table modification).
// Returns thread handles (caller must resume + close).
std::vector<HANDLE> suspend_target_threads(HANDLE /*process*/, uint32_t target_pid) {
    std::vector<HANDLE> suspended;

    auto proc_info = query_process_threads(target_pid);
    if (!proc_info) return suspended;

    for (const auto& ti : proc_info->threads) {
        HANDLE hThread = nullptr;
        NT_OBJECT_ATTRIBUTES oa{};
        oa.Length = sizeof(oa);
        NT_CLIENT_ID cid{};
        cid.UniqueThread = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(ti.unique_thread_id));
        auto st = syscall::NtOpenThread(&hThread, THREAD_SUSPEND_RESUME, &oa, &cid);
        if (NT_SUCCESS(st) && hThread) {
            ULONG prev = 0;
            syscall::NtSuspendThread(hThread, &prev);
            suspended.push_back(hThread);
        }
    }

    return suspended;
}

void resume_and_close_threads(std::vector<HANDLE>& threads) {
    for (auto h : threads) {
        syscall::NtResumeThread(h, nullptr);
        syscall::NtClose(h);
    }
    threads.clear();
}

mm_status insert_inverted_function_table(
    HANDLE process,
    uint32_t target_pid,
    uintptr_t image_base,
    uint32_t image_size,
    uintptr_t pdata_addr,
    uint32_t pdata_size,
    std::wstring& error)
{
    // 1. Find KiUserInvertedFunctionTable via manual export walk (avoids GetProcAddress monitoring).
    HMODULE local_ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!local_ntdll) { error = L"ntdll not loaded."; return MM_EXECUTION_FAILED; }
    auto* table_addr = syscall_find_local_export(local_ntdll, "KiUserInvertedFunctionTable");
    if (!table_addr) { error = L"KiUserInvertedFunctionTable not found."; return MM_EXECUTION_FAILED; }

    // 2. Suspend all target threads for atomicity.
    //    For self-injection, this would deadlock — skip it.
    bool is_self_proc = (target_pid == GetCurrentProcessId());
    auto suspended = is_self_proc ? std::vector<HANDLE>{} : suspend_target_threads(process, target_pid);

    // 3. Read the header.
    InvFuncTableHeader hdr{};
    auto st = syscall::NtReadVirtualMemory(process, table_addr, &hdr, sizeof(hdr), nullptr);
    if (!NT_SUCCESS(st)) {
        resume_and_close_threads(suspended);
        error = L"Failed to read inverted function table header.";
        return MM_EXECUTION_FAILED;
    }

    if (hdr.Count >= hdr.MaxCount) {
        resume_and_close_threads(suspended);
        error = L"Inverted function table is full.";
        return MM_EXECUTION_FAILED;
    }

    // 4. Read all entries.
    const auto entries_addr = reinterpret_cast<uint8_t*>(table_addr) + sizeof(InvFuncTableHeader);
    std::vector<InvFuncTableEntry> entries(hdr.Count);
    if (hdr.Count > 0) {
        st = syscall::NtReadVirtualMemory(process, entries_addr,
                                           entries.data(), hdr.Count * sizeof(InvFuncTableEntry), nullptr);
        if (!NT_SUCCESS(st)) {
            resume_and_close_threads(suspended);
            error = L"Failed to read inverted function table entries.";
            return MM_EXECUTION_FAILED;
        }
    }

    // 5. Find insertion point (sorted by ImageBase ascending).
    size_t insert_pos = 0;
    for (; insert_pos < entries.size(); ++insert_pos) {
        if (entries[insert_pos].ImageBase > image_base) break;
    }

    // 6. Insert our entry.
    InvFuncTableEntry our_entry{};
    our_entry.FunctionTable = pdata_addr;
    our_entry.ImageBase     = image_base;
    our_entry.SizeOfImage   = image_size;
    our_entry.SizeOfTable   = pdata_size;
    entries.insert(entries.begin() + insert_pos, our_entry);

    // 7. Write entries back.
    st = syscall::NtWriteVirtualMemory(process, entries_addr,
                                        entries.data(), entries.size() * sizeof(InvFuncTableEntry), nullptr);
    if (!NT_SUCCESS(st)) {
        resume_and_close_threads(suspended);
        error = L"Failed to write inverted function table entries.";
        return MM_EXECUTION_FAILED;
    }

    // 8. Update header (Count + Epoch).
    hdr.Count++;
    hdr.Epoch++;
    st = syscall::NtWriteVirtualMemory(process, table_addr, &hdr, sizeof(hdr), nullptr);

    // 9. Resume all threads.
    resume_and_close_threads(suspended);

    if (!NT_SUCCESS(st)) {
        error = L"Failed to update inverted function table header.";
        return MM_EXECUTION_FAILED;
    }

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
    uint32_t target_pid,
    const std::wstring& dll_path,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::vector<MappedModule>& mapped_modules,
    std::wstring& error,
    uint32_t depth)
{
    const auto dll_name = filename_from_path(dll_path);

    if (depth > 16) {
        error = L"Dependency recursion limit exceeded for: " + dll_name;
        return MM_EXECUTION_FAILED;
    }
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
        auto rstatus = resolve_imports(process, target_pid, local_image, pe, dll_dir, timeout_ms,
                                        remote_modules, mapped_modules, error, depth);
        if (rstatus != MM_OK) {
            remove_self();
            SIZE_T free_sz = 0;
            syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
            return rstatus;
        }
    }

    // ── 6b. Resolve delay-load imports ─────────────────────────────────────────
    {
        auto dstatus = resolve_delay_imports(process, target_pid, local_image, pe, dll_dir,
                                              timeout_ms, remote_modules, mapped_modules,
                                              error, depth);
        if (dstatus != MM_OK) {
            remove_self();
            SIZE_T free_sz = 0;
            syscall::NtFreeVirtualMemory(process, &remote_base, &free_sz, MEM_RELEASE);
            return dstatus;
        }
    }

    // ── 6c. Zero bound import directory (stale timestamps) ──────────────────
    {
        auto* local_nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(
            local_image.data() + pe.dos->e_lfanew);
        local_nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BOUND_IMPORT].VirtualAddress = 0;
        local_nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BOUND_IMPORT].Size = 0;
    }

    // ── 7. Write image to target ──────────────────────────────────────────────
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
                process, target_pid, rb, static_cast<uint32_t>(pe.nt->OptionalHeader.SizeOfImage),
                rb + pdata.rva,
                pdata.count * static_cast<uint32_t>(sizeof(RUNTIME_FUNCTION)),
                error);
            if (inv_st != MM_OK) {
                // Log but continue — RtlAddFunctionTable fallback in stub handles SEH.
                error.clear();
            }
        }
    }

    // ── 11. Execute _DllMainCRTStartup via loader stub ─────────────────────────
    //    The CRT entry point handles ALL initialization: security cookie, CRT
    //    init, TLS setup, static constructors, and the user's DllMain.
    //    SEH is already registered via insert_inverted_function_table (step 10),
    //    so no RtlAddFunctionTable call needed (avoids dynamic function table
    //    enumeration by anti-cheats).
    {
        const auto ep = pe.nt->OptionalHeader.AddressOfEntryPoint;

        std::vector<uint8_t> ctx_buf(sizeof(DllMainContext), 0);
        auto* ctx = reinterpret_cast<DllMainContext*>(ctx_buf.data());

        ctx->image_base  = rb;
        ctx->entry_point = ep ? rb + ep : 0;

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
    // The CRT's _ValidateImageBase checks for DOS/NT signatures at the module
    // base during security cookie operations and _IsNonwritableInCurrentImage.
    // We preserve just the minimum fields needed by the CRT and fill everything
    // else with random bytes (not zeros — a zeroed page at allocation base is
    // itself a scannable pattern).
    {
        const auto hdr_size = pe.nt->OptionalHeader.SizeOfHeaders;
        const auto e_lfanew = pe.dos->e_lfanew;

        // Fill with random bytes to avoid zero-page detection.
        std::vector<uint8_t> scrubbed(hdr_size);
        for (size_t i = 0; i < hdr_size; ++i)
            scrubbed[i] = static_cast<uint8_t>(__rdtsc() ^ (i * 7));

        // IMAGE_DOS_HEADER.e_magic ('MZ') at offset 0 — required by _ValidateImageBase.
        scrubbed[0] = 'M'; scrubbed[1] = 'Z';
        // IMAGE_DOS_HEADER.e_lfanew at offset 0x3C
        memcpy(scrubbed.data() + 0x3C, &e_lfanew, sizeof(e_lfanew));
        // IMAGE_NT_HEADERS64.Signature ('PE') at e_lfanew — required by _ValidateImageBase.
        scrubbed[e_lfanew] = 'P'; scrubbed[e_lfanew + 1] = 'E';
        scrubbed[e_lfanew + 2] = 0;  scrubbed[e_lfanew + 3] = 0;
        // IMAGE_FILE_HEADER.Machine at e_lfanew + 4
        {
            WORD machine = IMAGE_FILE_MACHINE_AMD64;
            memcpy(scrubbed.data() + e_lfanew + 4, &machine, sizeof(WORD));
        }
        // IMAGE_FILE_HEADER.NumberOfSections at e_lfanew + 6
        memcpy(scrubbed.data() + e_lfanew + 6,
               &pe.nt->FileHeader.NumberOfSections, sizeof(WORD));
        // IMAGE_OPTIONAL_HEADER64.Magic at e_lfanew + 0x18
        scrubbed[e_lfanew + 0x18] = 0x0B; scrubbed[e_lfanew + 0x19] = 0x02;
        // IMAGE_OPTIONAL_HEADER64.ImageBase at e_lfanew + 0x30
        {
            uint64_t relocated_base = rb;
            memcpy(scrubbed.data() + e_lfanew + 0x30, &relocated_base, sizeof(uint64_t));
        }
        // OptionalHeader starts at e_lfanew + sizeof(Signature) + sizeof(IMAGE_FILE_HEADER) = e_lfanew + 0x18
        constexpr DWORD kOptHdrBase = sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER); // 0x18
        // IMAGE_OPTIONAL_HEADER64.SizeOfImage at e_lfanew + 0x18 + offsetof(..., SizeOfImage)
        constexpr DWORD kSizeOfImageOff = kOptHdrBase + static_cast<DWORD>(offsetof(IMAGE_OPTIONAL_HEADER64, SizeOfImage));
        memcpy(scrubbed.data() + e_lfanew + kSizeOfImageOff,
               &pe.nt->OptionalHeader.SizeOfImage, sizeof(DWORD));
        // IMAGE_OPTIONAL_HEADER64.SizeOfHeaders at e_lfanew + 0x18 + offsetof(..., SizeOfHeaders)
        constexpr DWORD kSizeOfHdrsOff = kOptHdrBase + static_cast<DWORD>(offsetof(IMAGE_OPTIONAL_HEADER64, SizeOfHeaders));
        memcpy(scrubbed.data() + e_lfanew + kSizeOfHdrsOff,
               &pe.nt->OptionalHeader.SizeOfHeaders, sizeof(DWORD));

        // Preserve section headers for _IsNonwritableInCurrentImage, but scrub
        // section names (EDR scanners look for ".text", ".rdata", ".data" etc.).
        const auto sec_offset = reinterpret_cast<const uint8_t*>(pe.sections) - pe.raw.data();
        const auto num_sections = pe.nt->FileHeader.NumberOfSections;
        const auto sec_size = num_sections * sizeof(IMAGE_SECTION_HEADER);
        if (sec_offset + sec_size <= hdr_size) {
            memcpy(scrubbed.data() + sec_offset,
                   pe.raw.data() + sec_offset, sec_size);
            // Zero out section names (first 8 bytes of each IMAGE_SECTION_HEADER).
            // CRT only uses VirtualAddress, SizeOfRawData, and Characteristics.
            for (WORD i = 0; i < num_sections; ++i) {
                auto* sec = reinterpret_cast<IMAGE_SECTION_HEADER*>(
                    scrubbed.data() + sec_offset + i * sizeof(IMAGE_SECTION_HEADER));
                memset(sec->Name, 0, sizeof(sec->Name));
            }
        }

        syscall::NtWriteVirtualMemory(process, remote_base, scrubbed.data(), hdr_size, nullptr);
    }

    // Success — module stays mapped.  Wipe PE data from local memory.
    SecureZeroMemory(pe.raw.data(), pe.raw.size());
    return MM_OK;
}
