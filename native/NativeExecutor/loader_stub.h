#pragma once

#include <windows.h>
#include <stdint.h>

// ── Context structures passed to the remote stubs ──────────────────────────────
// All pointer-sized fields are uint64_t (absolute addresses in the target).

struct EnvEntry {
    uint64_t name_ptr;   // wchar_t* in target memory
    uint64_t value_ptr;  // wchar_t* in target memory
};

struct EnvApplyContext {
    uint64_t fn_set_env_variable_w;  // SetEnvironmentVariableW address
    uint32_t env_count;
    uint32_t _pad;
    // Followed by env_count × EnvEntry
};

struct DllMainContext {
    uint64_t image_base;
    uint64_t entry_point;               // _DllMainCRTStartup address (0 → skip)
    uint64_t fn_rtl_add_function_table;  // RtlAddFunctionTable (0 → skip)
    uint64_t pdata_base;                // image_base + .pdata VirtualAddress
    uint32_t pdata_entry_count;         // number of RUNTIME_FUNCTION entries
    uint32_t _pad0;
};

// ── Loader stub function pointers ──────────────────────────────────────────────
// These are compiled as normal C++ but are position-independent: they reference
// no globals, no string literals, and call only through context-struct pointers.
// They live in the NativeExecutor.dll image and are copied as raw bytes to the
// target process at injection time.

extern "C" {
    DWORD WINAPI env_apply_stub(void* parameter);
    DWORD WINAPI dllmain_stub(void* parameter);
}

// Return the start address and byte size of a specific stub function by
// looking up its RUNTIME_FUNCTION entry in our own .pdata section.
struct StubInfo {
    const void* code;
    size_t      size;
};

StubInfo get_stub_info(void* func_addr);
