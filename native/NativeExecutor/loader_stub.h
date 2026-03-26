#pragma once

#include <windows.h>
#include <stdint.h>

// ── Context structures passed to the remote stubs ──────────────────────────────
// All pointer-sized fields are uint64_t (absolute addresses in the target).

// Hijack header — prepended to ALL stub contexts when using thread hijacking.
// If hijack_mode == 1, the stub signals completion and exits the thread cleanly
// via RtlExitUserThread instead of returning.
struct HijackHeader {
    volatile uint64_t completed;    // 0 → 1 when stub finishes
    uint64_t          result;       // stub return value (DWORD)
    uint64_t          hijack_mode;  // 0 = normal (return), 1 = hijack (signal + exit thread)
    uint64_t          fn_exit_thread; // RtlExitUserThread address in target
};

struct EnvEntry {
    uint64_t name_ptr;   // wchar_t* in target memory
    uint64_t value_ptr;  // wchar_t* in target memory
};

struct EnvApplyContext {
    HijackHeader        hdr;                // hijack header (always present)
    uint64_t fn_set_env_variable_w;         // SetEnvironmentVariableW address
    uint32_t env_count;
    uint32_t _pad;
    // Followed by env_count × EnvEntry
};

struct DllMainContext {
    HijackHeader        hdr;                // hijack header (always present)
    uint64_t image_base;
    uint64_t entry_point;                   // _DllMainCRTStartup address (0 → skip)
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
