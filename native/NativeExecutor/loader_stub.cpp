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
//
// 1. Register .pdata via RtlAddFunctionTable (SEH support).
// 2. Call _DllMainCRTStartup (the PE entry point), which internally handles:
//    - Security cookie initialization
//    - CRT heap/stdio/locale init
//    - TLS setup (TlsAlloc, TLS callbacks)
//    - C/C++ static constructors (_initterm)
//    - User DllMain(DLL_PROCESS_ATTACH)
//
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

    // 2. Call _DllMainCRTStartup.  The CRT handles all initialization including
    //    TLS, security cookies, static constructors, and the user's DllMain.
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
// Locate a function's boundaries via its RUNTIME_FUNCTION entries in .pdata.
// In Debug/incremental-link builds, &func points to a JMP thunk — we follow it.

static void* resolve_jmp_thunk(void* addr) {
    auto* p = static_cast<uint8_t*>(addr);
    if (p[0] == 0xE9) {
        int32_t offset;
        memcpy(&offset, p + 1, 4);
        return p + 5 + offset;
    }
    if (p[0] == 0xFF && p[1] == 0x25) {
        int32_t offset;
        memcpy(&offset, p + 2, 4);
        void** target = reinterpret_cast<void**>(p + 6 + offset);
        return *target;
    }
    return addr;
}

StubInfo get_stub_info(void* func_addr) {
    void* resolved = resolve_jmp_thunk(func_addr);

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

    uint32_t func_end = func_rva;
    for (DWORD i = 0; i < count; ++i) {
        if (entries[i].BeginAddress >= func_rva && entries[i].EndAddress > func_end) {
            func_end = entries[i].EndAddress;
        }
    }

    uint32_t size = func_end - func_rva;
    if (size == 0) return {nullptr, 0};

    return {
        reinterpret_cast<const void*>(base + func_rva),
        static_cast<size_t>(size)
    };
}
