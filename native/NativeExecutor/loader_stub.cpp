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
//
// Each stub checks hdr.hijack_mode: if set, signals completion and exits
// the thread cleanly via RtlExitUserThread.

#include "loader_stub.h"

#pragma runtime_checks("", off)

// ── env_apply_stub ─────────────────────────────────────────────────────────

__declspec(noinline) __declspec(safebuffers)
extern "C" DWORD WINAPI env_apply_stub(void* parameter) {
    auto* ctx = static_cast<EnvApplyContext*>(parameter);
    if (!ctx) return 1;

    // Helper type for clean thread exit (no spinloop, no CPU spike).
    using ExitThreadFn = void(NTAPI*)(DWORD);
    auto exit_thread = reinterpret_cast<ExitThreadFn>(
        static_cast<uintptr_t>(ctx->hdr.fn_exit_thread));

    using SetEnvFn = BOOL(WINAPI*)(const wchar_t*, const wchar_t*);
    auto set_env = reinterpret_cast<SetEnvFn>(ctx->fn_set_env_variable_w);
    if (!set_env) {
        DWORD rc = 2;
        if (ctx->hdr.hijack_mode) {
            ctx->hdr.result = rc;
            ctx->hdr.completed = 1;
            exit_thread(rc);
        }
        return rc;
    }

    auto* entries = reinterpret_cast<EnvEntry*>(ctx + 1);
    for (uint32_t i = 0; i < ctx->env_count; ++i) {
        auto* name  = reinterpret_cast<const wchar_t*>(entries[i].name_ptr);
        auto* value = reinterpret_cast<const wchar_t*>(entries[i].value_ptr);
        if (!set_env(name, value)) {
            DWORD rc = 3;
            if (ctx->hdr.hijack_mode) {
                ctx->hdr.result = rc;
                ctx->hdr.completed = 1;
                exit_thread(rc);
            }
            return rc;
        }
    }

    if (ctx->hdr.hijack_mode) {
        ctx->hdr.result = 0;
        ctx->hdr.completed = 1;
        exit_thread(0);
    }
    return 0;
}

#pragma runtime_checks("", restore)

// ── get_stub_info ──────────────────────────────────────────────────────────

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
