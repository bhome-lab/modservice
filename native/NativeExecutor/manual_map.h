#pragma once

#include "mm_executor.h"
#include "peb_walk.h"

#include <string>
#include <vector>

// ── Mapped-module tracking ─────────────────────────────────────────────────────

struct MappedExport {
    std::string name;
    uint16_t    ordinal;
    uint32_t    rva;
};

struct MappedModule {
    std::wstring path;          // full path of the DLL on disk
    std::wstring name;          // filename only (e.g., L"SampleModule.dll")
    uintptr_t    base = 0;     // base address in the target process
    uint32_t     size = 0;     // SizeOfImage
    std::vector<MappedExport> exports;  // cached export table (for cross-module resolution)
};

// ── Manual-map a DLL into a remote process ─────────────────────────────────────
// Recursively maps unresolved non-system dependencies.
// |remote_modules| is the PEB module list (already-loaded system DLLs).
// |mapped_modules| is the list of previously manually-mapped user DLLs (in/out).

mm_status manual_map_remote(
    HANDLE process,
    const std::wstring& dll_path,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::vector<MappedModule>& mapped_modules,
    std::wstring& error);

// Execute a position-independent stub in the target process.
// Allocates code + data pages, runs a thread, waits, then zeros + frees everything.
mm_status execute_remote_stub(
    HANDLE process,
    const void* code, size_t code_size,
    const void* context, size_t context_size,
    uint32_t timeout_ms,
    std::wstring& error);
