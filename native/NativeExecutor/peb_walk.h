#pragma once

#include "mm_executor.h"
#include "syscalls.h"

#include <string>
#include <vector>

// ── Remote module info ─────────────────────────────────────────────────────────

struct RemoteModuleInfo {
    std::wstring name;       // BaseDllName  (e.g., L"kernel32.dll")
    std::wstring full_path;  // FullDllName  (e.g., L"C:\\Windows\\System32\\kernel32.dll")
    uintptr_t    base = 0;
    uint32_t     size = 0;
};

// Walk the remote PEB → Ldr → InLoadOrderModuleList.
mm_status enumerate_remote_modules(
    HANDLE process,
    std::vector<RemoteModuleInfo>& modules,
    std::wstring& error);

// Resolve a named export from a remote module by reading its export table.
// |modules| is the full module list — needed only for forwarded-export chains.
mm_status resolve_remote_export(
    HANDLE process,
    uintptr_t module_base,
    const char* export_name,
    uintptr_t& resolved_address,
    const std::vector<RemoteModuleInfo>& modules,
    std::wstring& error);

// Resolve by ordinal.
mm_status resolve_remote_export_ordinal(
    HANDLE process,
    uintptr_t module_base,
    uint16_t ordinal,
    uintptr_t& resolved_address,
    const std::vector<RemoteModuleInfo>& modules,
    std::wstring& error);
