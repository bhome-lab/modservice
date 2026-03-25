// ── Position-independent loader stubs ───────────────────────────────────────
//
// Rules for every function in this file:
//   1. No global / static variable access
//   2. No string literals
//   3. No direct function calls — all via context-struct pointers
//   4. No C++ exceptions, no RTTI
//   5. Compiled with /GS- (no stack security cookies)
//   6. __declspec(noinline) to prevent folding / ICF
//   7. __declspec(safebuffers) to suppress any residual cookie emission
//
// The compiled machine code is read from our own image at runtime and copied
// verbatim to the target process, so it must work at any base address.

#include "loader_stub.h"

#pragma runtime_checks("", off)

// ── env_apply_stub ─────────────────────────────────────────────────────────────
// Thread entry point.  Parameter is EnvApplyContext* in target memory.
// Calls SetEnvironmentVariableW for each env var, returns 0 on success.

__declspec(noinline) __declspec(safebuffers)
extern "C" DWORD WINAPI env_apply_stub(void* parameter) {
    auto* ctx = static_cast<EnvApplyContext*>(parameter);
    if (!ctx) return 1;

    using SetEnvFn = BOOL(WINAPI*)(const wchar_t*, const wchar_t*);
    auto set_env = reinterpret_cast<SetEnvFn>(ctx->fn_set_env_variable_w);
    if (!set_env) return 2;

    auto* entries = reinterpret_cast<EnvEntry*>(ctx + 1);
    for (uint32_t i = 0; i < ctx->env_count; ++i) {
        auto* name  = reinterpret_cast<const wchar_t*>(entries[i].name_ptr);
        auto* value = reinterpret_cast<const wchar_t*>(entries[i].value_ptr);
        if (!set_env(name, value)) return 3;
    }
    return 0;
}

// ── dllmain_stub ───────────────────────────────────────────────────────────────
// Thread entry point.  Parameter is DllMainContext* in target memory.
// 1. Register exception handlers (.pdata) via RtlAddFunctionTable
// 2. Allocate TLS index and store it
// 3. Call TLS callbacks
// 4. Call DllMain(DLL_PROCESS_ATTACH)
// Returns 0 on success.

__declspec(noinline) __declspec(safebuffers)
extern "C" DWORD WINAPI dllmain_stub(void* parameter) {
    auto* ctx = static_cast<DllMainContext*>(parameter);
    if (!ctx) return 1;

    auto base = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->image_base));

    // 1. Register .pdata for SEH exception unwinding via RtlAddFunctionTable.
    //    The host also inserts into KiUserInvertedFunctionTable for RTTI support.
    if (ctx->fn_rtl_add_function_table && ctx->pdata_base) {
        using AddFnTable = BOOLEAN(WINAPI*)(PRUNTIME_FUNCTION, DWORD, DWORD64);
        auto add_fn = reinterpret_cast<AddFnTable>(static_cast<uintptr_t>(ctx->fn_rtl_add_function_table));
        add_fn(reinterpret_cast<PRUNTIME_FUNCTION>(static_cast<uintptr_t>(ctx->pdata_base)),
               ctx->pdata_entry_count,
               ctx->image_base);
    }

    // 2. Allocate TLS index.
    if (ctx->fn_tls_alloc && ctx->tls_index_addr) {
        using TlsAllocFn = DWORD(WINAPI*)();
        auto tls_alloc = reinterpret_cast<TlsAllocFn>(static_cast<uintptr_t>(ctx->fn_tls_alloc));
        DWORD idx = tls_alloc();
        *reinterpret_cast<DWORD*>(static_cast<uintptr_t>(ctx->tls_index_addr)) = idx;
    }

    // 3. Call TLS callbacks.
    auto* tls_ptrs = reinterpret_cast<uint64_t*>(ctx + 1);
    for (uint32_t i = 0; i < ctx->tls_callback_count; ++i) {
        using TlsCallback = void(WINAPI*)(PVOID, DWORD, PVOID);
        auto cb = reinterpret_cast<TlsCallback>(static_cast<uintptr_t>(tls_ptrs[i]));
        cb(base, DLL_PROCESS_ATTACH, nullptr);
    }

    // 4. Call DllMain.
    if (ctx->entry_point) {
        using DllMainFn = BOOL(WINAPI*)(HMODULE, DWORD, LPVOID);
        auto entry = reinterpret_cast<DllMainFn>(static_cast<uintptr_t>(ctx->entry_point));
        BOOL ok = entry(static_cast<HMODULE>(base), DLL_PROCESS_ATTACH, nullptr);
        if (!ok) return 4;
    }

    return 0;
}

#pragma runtime_checks("", restore)

// ── get_stub_info ──────────────────────────────────────────────────────────────
// Locate a function's boundaries via RtlLookupFunctionEntry.
// In Debug/incremental-link builds, &func points to a JMP thunk — we follow it.

static void* resolve_jmp_thunk(void* addr) {
    auto* p = static_cast<uint8_t*>(addr);
    // E9 xx xx xx xx  =  relative JMP (incremental-link thunk)
    if (p[0] == 0xE9) {
        int32_t offset;
        memcpy(&offset, p + 1, 4);
        return p + 5 + offset;
    }
    // FF 25 xx xx xx xx  =  indirect JMP [rip+disp32]
    if (p[0] == 0xFF && p[1] == 0x25) {
        int32_t offset;
        memcpy(&offset, p + 2, 4);
        void** target = reinterpret_cast<void**>(p + 6 + offset);
        return *target;
    }
    return addr;
}

StubInfo get_stub_info(void* func_addr) {
    // Follow potential incremental-link JMP thunk.
    void* resolved = resolve_jmp_thunk(func_addr);

    // Find our module base and .pdata section.
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            static_cast<LPCWSTR>(resolved), &self))
        return {nullptr, 0};

    const auto base = reinterpret_cast<uintptr_t>(self);
    const auto func_rva = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(resolved) - base);

    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(self);
    const auto* nt  = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
        reinterpret_cast<const uint8_t*>(self) + dos->e_lfanew);
    const auto& pdata_dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
    if (pdata_dir.VirtualAddress == 0) return {nullptr, 0};

    const auto* entries = reinterpret_cast<const RUNTIME_FUNCTION*>(
        reinterpret_cast<const uint8_t*>(self) + pdata_dir.VirtualAddress);
    const auto count = pdata_dir.Size / static_cast<DWORD>(sizeof(RUNTIME_FUNCTION));

    // Find the maximum EndAddress for all .pdata entries that belong to this function,
    // and the smallest BeginAddress of the NEXT function to use as an upper bound.
    uint32_t func_end = func_rva;
    uint32_t next_func = UINT32_MAX;

    for (DWORD i = 0; i < count; ++i) {
        // Entries whose BeginAddress matches ours (primary or chained entries).
        if (entries[i].BeginAddress >= func_rva && entries[i].EndAddress > func_end) {
            // This entry's range extends our function.
            func_end = entries[i].EndAddress;
        }
        // Track the next function's start (smallest BeginAddress > our end).
        if (entries[i].BeginAddress > func_rva && entries[i].BeginAddress < next_func) {
            next_func = entries[i].BeginAddress;
        }
    }

    // Use the max of (our entries' EndAddress) and (up to next function start).
    // For split functions, we need the distance to the next different function.
    uint32_t size = func_end - func_rva;
    if (size == 0) return {nullptr, 0};

    return {
        reinterpret_cast<const void*>(base + func_rva),
        static_cast<size_t>(size)
    };
}
