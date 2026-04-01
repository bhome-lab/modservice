#include "mm_executor.h"
#include "syscalls.h"
#include "win_offsets.h"
#include "peb_walk.h"
#include "loader_stub.h"

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

namespace {

// ── Error formatting ───────────────────────────────────────────────────────────

void write_error_text(const std::wstring& msg, wchar_t* buf, uint32_t cap, uint32_t* written) {
    if (written) *written = 0;
    if (!buf || cap == 0) return;
    const auto n = static_cast<uint32_t>((std::min)(msg.size(), static_cast<size_t>(cap - 1)));
    if (n > 0) memcpy(buf, msg.data(), n * sizeof(wchar_t));
    buf[n] = L'\0';
    if (written) *written = n;
}

void write_error(const std::wstring& msg, uint16_t* buf, uint32_t cap, uint32_t* written) {
    write_error_text(msg, reinterpret_cast<wchar_t*>(buf), cap, written);
}

std::wstring to_wstring(const mm_u16_view& view) {
    if (!view.ptr || view.len == 0) return {};
    return std::wstring(reinterpret_cast<const wchar_t*>(view.ptr), view.len);
}

size_t align_up(size_t v, size_t a) {
    const size_t r = v % a;
    return r == 0 ? v : v + (a - r);
}

// ── Privilege ──────────────────────────────────────────────────────────────────

void enable_debug_privilege() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token))
        return;
    TOKEN_PRIVILEGES tp{};
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    if (LookupPrivilegeValueW(nullptr, SE_DEBUG_NAME, &tp.Privileges[0].Luid))
        AdjustTokenPrivileges(token, FALSE, &tp, 0, nullptr, nullptr);
    CloseHandle(token);
}

// ── Target identity validation ─────────────────────────────────────────────────

uint64_t get_process_create_time(HANDLE process) {
    FILETIME ct{}, et{}, kt{}, ut{};
    if (!GetProcessTimes(process, &ct, &et, &kt, &ut)) return 0;
    ULARGE_INTEGER v{};
    v.LowPart = ct.dwLowDateTime;
    v.HighPart = ct.dwHighDateTime;
    return v.QuadPart;
}

std::wstring get_process_image_path(HANDLE process) {
    std::wstring buf(512, L'\0');
    DWORD len = static_cast<DWORD>(buf.size());
    while (!QueryFullProcessImageNameW(process, 0, buf.data(), &len)) {
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) return {};
        buf.resize(buf.size() * 2);
        len = static_cast<DWORD>(buf.size());
    }
    buf.resize(len);
    return buf;
}

mm_status validate_target_identity(HANDLE process, const mm_execute_request* req, std::wstring& error) {
    if (req->process_create_time_utc_100ns != 0) {
        const auto actual = get_process_create_time(process);
        if (actual != req->process_create_time_utc_100ns) {
            error = L"Target process identity changed.";
            return MM_TARGET_CHANGED;
        }
    }
    const auto expected = to_wstring(req->exe_path);
    if (!expected.empty()) {
        const auto actual = get_process_image_path(process);
        if (actual.empty() || _wcsicmp(actual.c_str(), expected.c_str()) != 0) {
            error = L"Target executable path changed.";
            return MM_TARGET_CHANGED;
        }
    }
    return MM_OK;
}

// ── Options validation ─────────────────────────────────────────────────────────

bool validate_options(const mm_execute_request* req, std::wstring& error) {
    for (uint32_t i = 0; i < req->option_count; ++i) {
        if (to_wstring(req->options[i].name).empty()) {
            error = L"Executor option name cannot be empty.";
            return false;
        }
    }
    return true;
}

// ── Remote stub execution ──────────────────────────────────────────────────────
// Allocates code + data pages in the target, runs a thread, waits for
// completion via HijackHeader polling, then zeros + frees everything.

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

    std::vector<uint8_t> ctx_copy(context_size);
    memcpy(ctx_copy.data(), context, context_size);
    auto* hdr = reinterpret_cast<HijackHeader*>(ctx_copy.data());
    hdr->completed = 0;
    hdr->result = 0;
    hdr->hijack_mode = 1;

    // Resolve RtlExitUserThread for clean thread exit.
    {
        HMODULE local_ntdll = GetModuleHandleW(L"ntdll.dll");
        auto* local_fn = syscall_find_local_export(local_ntdll, "RtlExitUserThread");
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

    // 3. Write code.
    st = syscall::NtWriteVirtualMemory(process, remote_code, code, code_size, nullptr);
    if (!NT_SUCCESS(st)) { error = L"Failed to write stub code."; result = MM_EXECUTION_FAILED; goto cleanup; }

    // 4. Write context data.
    st = syscall::NtWriteVirtualMemory(process, remote_data, ctx_copy.data(), ctx_copy.size(), nullptr);
    if (!NT_SUCCESS(st)) { error = L"Failed to write context."; result = MM_EXECUTION_FAILED; goto cleanup; }
    SecureZeroMemory(ctx_copy.data(), ctx_copy.size());

    // 5. Set code page to RX.
    { PVOID p = remote_code; SIZE_T s = code_alloc; ULONG o = 0;
      st = syscall::NtProtectVirtualMemory(process, &p, &s, PAGE_EXECUTE_READ, &o);
      if (!NT_SUCCESS(st)) { error = L"Failed to set RX."; result = MM_EXECUTION_FAILED; goto cleanup; } }

    // 6. Execute via NtCreateThreadEx.
    {
        HANDLE hThread = nullptr;
        st = syscall::NtCreateThreadEx(&hThread, THREAD_ALL_ACCESS, nullptr, process,
                                        remote_code, remote_data,
                                        0, 0, 0, 0, nullptr);
        if (!NT_SUCCESS(st) || !hThread) {
            error = L"NtCreateThreadEx failed.";
            result = MM_EXECUTION_FAILED;
            goto cleanup;
        }
        ScopedNtHandle thread_handle(hThread);

        // Poll for completion.
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

        LARGE_INTEGER wait_timeout{};
        wait_timeout.QuadPart = -50000000LL; // 5 seconds
        syscall::NtWaitForSingleObject(thread_handle.get(), FALSE, &wait_timeout);
    }

cleanup:
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

// ── Environment apply via shellcode ────────────────────────────────────────────

mm_status apply_environment_remote(
    HANDLE process,
    const mm_execute_request* request,
    uint32_t timeout_ms,
    const std::vector<RemoteModuleInfo>& remote_modules,
    std::wstring& error)
{
    if (request->env_count == 0) return MM_OK;

    // 1. Resolve SetEnvironmentVariableW from remote kernel32.
    uintptr_t fn_set_env = 0;
    for (const auto& mod : remote_modules) {
        if (_wcsicmp(mod.name.c_str(), L"kernel32.dll") == 0) {
            auto st = resolve_remote_export(process, mod.base, "SetEnvironmentVariableW",
                                             fn_set_env, remote_modules, error);
            if (st != MM_OK) return st;
            break;
        }
    }
    if (!fn_set_env) {
        error = L"Failed to resolve SetEnvironmentVariableW.";
        return MM_EXECUTION_FAILED;
    }

    // 2. Allocate and write string data.
    struct StringAlloc { PVOID remote; SIZE_T size; };
    std::vector<StringAlloc> string_allocs;
    std::vector<uint64_t> name_ptrs;
    std::vector<uint64_t> value_ptrs;

    for (uint32_t i = 0; i < request->env_count; ++i) {
        const auto name  = to_wstring(request->env[i].name);
        const auto value = to_wstring(request->env[i].value);
        if (name.empty()) {
            error = L"Environment variable name cannot be empty.";
            return MM_INVALID_ARGUMENT;
        }

        SIZE_T name_bytes = (name.size() + 1) * sizeof(wchar_t);
        SIZE_T name_alloc = align_up(name_bytes, 0x1000);
        PVOID remote_name = nullptr;
        auto st = syscall::NtAllocateVirtualMemory(process, &remote_name, 0, &name_alloc,
                                                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!NT_SUCCESS(st)) { error = L"Failed to allocate env name."; return MM_EXECUTION_FAILED; }
        syscall::NtWriteVirtualMemory(process, remote_name, name.c_str(), name_bytes, nullptr);
        string_allocs.push_back({remote_name, name_alloc});
        name_ptrs.push_back(reinterpret_cast<uint64_t>(remote_name));

        SIZE_T val_bytes = (value.size() + 1) * sizeof(wchar_t);
        SIZE_T val_alloc = align_up(val_bytes, 0x1000);
        PVOID remote_val = nullptr;
        st = syscall::NtAllocateVirtualMemory(process, &remote_val, 0, &val_alloc,
                                               MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!NT_SUCCESS(st)) { error = L"Failed to allocate env value."; return MM_EXECUTION_FAILED; }
        syscall::NtWriteVirtualMemory(process, remote_val, value.c_str(), val_bytes, nullptr);
        string_allocs.push_back({remote_val, val_alloc});
        value_ptrs.push_back(reinterpret_cast<uint64_t>(remote_val));
    }

    // 3. Build context.
    const size_t ctx_size = sizeof(EnvApplyContext) + request->env_count * sizeof(EnvEntry);
    std::vector<uint8_t> ctx_buf(ctx_size, 0);
    auto* ctx = reinterpret_cast<EnvApplyContext*>(ctx_buf.data());
    ctx->fn_set_env_variable_w = fn_set_env;
    ctx->env_count = request->env_count;

    auto* entries = reinterpret_cast<EnvEntry*>(ctx + 1);
    for (uint32_t i = 0; i < request->env_count; ++i) {
        entries[i].name_ptr  = name_ptrs[i];
        entries[i].value_ptr = value_ptrs[i];
    }

    // 4. Get env_apply_stub code.
    const auto stub = get_stub_info(reinterpret_cast<void*>(&env_apply_stub));
    if (!stub.code || stub.size == 0) {
        error = L"Failed to locate env_apply_stub code.";
    }

    mm_status result = MM_OK;
    if (stub.code && stub.size > 0) {
        result = execute_remote_stub(process, stub.code, stub.size,
                                      ctx_buf.data(), ctx_buf.size(), timeout_ms, error);
    } else {
        result = MM_EXECUTION_FAILED;
    }

    // 5. Cleanup string allocations.
    for (auto& sa : string_allocs) {
        std::vector<uint8_t> zeros(sa.size, 0);
        syscall::NtWriteVirtualMemory(process, sa.remote, zeros.data(), sa.size, nullptr);
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &sa.remote, &free_sz, MEM_RELEASE);
    }

    SecureZeroMemory(ctx_buf.data(), ctx_buf.size());
    return result;
}

// ── LoadLibrary-based DLL injection ────────────────────────────────────────────
// Writes the DLL path into the target process and creates a remote thread
// at LoadLibraryW to load the DLL.

mm_status load_library_remote(
    HANDLE process,
    const std::wstring& dll_path,
    uint32_t timeout_ms,
    uintptr_t fn_load_library,
    std::wstring& error)
{
    // 1. Allocate remote buffer for the DLL path string.
    const SIZE_T path_bytes = (dll_path.size() + 1) * sizeof(wchar_t);
    SIZE_T path_alloc = align_up(path_bytes, 0x1000);
    PVOID remote_path = nullptr;
    auto st = syscall::NtAllocateVirtualMemory(process, &remote_path, 0, &path_alloc,
                                                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!NT_SUCCESS(st)) {
        error = L"Failed to allocate remote path buffer for: " + dll_path;
        return MM_EXECUTION_FAILED;
    }

    // 2. Write the DLL path.
    st = syscall::NtWriteVirtualMemory(process, remote_path, dll_path.c_str(), path_bytes, nullptr);
    if (!NT_SUCCESS(st)) {
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_path, &free_sz, MEM_RELEASE);
        error = L"Failed to write DLL path for: " + dll_path;
        return MM_EXECUTION_FAILED;
    }

    // 3. Create remote thread at LoadLibraryW.
    HANDLE hThread = nullptr;
    st = syscall::NtCreateThreadEx(&hThread, THREAD_ALL_ACCESS, nullptr, process,
                                    reinterpret_cast<PVOID>(fn_load_library),
                                    remote_path,
                                    0, 0, 0, 0, nullptr);
    if (!NT_SUCCESS(st) || !hThread) {
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_path, &free_sz, MEM_RELEASE);
        error = L"NtCreateThreadEx failed for LoadLibraryW: " + dll_path;
        return MM_EXECUTION_FAILED;
    }
    ScopedNtHandle thread_handle(hThread);

    // 4. Wait for LoadLibraryW to complete.
    {
        LARGE_INTEGER wait_timeout{};
        const uint32_t effective_timeout = timeout_ms == 0 ? 60000 : timeout_ms;
        wait_timeout.QuadPart = -static_cast<int64_t>(effective_timeout) * 10000LL;
        st = syscall::NtWaitForSingleObject(thread_handle.get(), FALSE, &wait_timeout);
    }

    mm_status result = MM_OK;
    if (st == 0x00000102 /* STATUS_TIMEOUT */) {
        error = L"LoadLibraryW timed out for: " + dll_path;
        result = MM_TIMEOUT;
    }

    // 5. Free the path buffer.
    {
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &remote_path, &free_sz, MEM_RELEASE);
    }

    return result;
}

// ── Main execution flow ────────────────────────────────────────────────────────

mm_status execute_remote(const mm_execute_request* request, std::wstring& error) {
    // 0. Initialize the syscall layer (once).
    {
        static bool initialized = false;
        static bool init_ok = false;
        static std::wstring init_error;
        if (!initialized) {
            init_ok = syscall_init(init_error);
            initialized = true;
        }
        if (!init_ok) {
            error = L"Syscall init failed: " + init_error;
            return MM_EXECUTION_FAILED;
        }
    }

    enable_debug_privilege();

    // 1. Open target process via syscall.
    HANDLE raw_handle = nullptr;
    {
        NT_OBJECT_ATTRIBUTES oa{};
        oa.Length = sizeof(oa);
        NT_CLIENT_ID cid{};
        cid.UniqueProcess = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(request->pid));
        auto st = syscall::NtOpenProcess(
            &raw_handle,
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
            PROCESS_QUERY_LIMITED_INFORMATION |
            PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
            &oa, &cid);
        if (!NT_SUCCESS(st) || !raw_handle) {
            error = L"NtOpenProcess failed (pid " + std::to_wstring(request->pid) + L").";
            return MM_TARGET_NOT_FOUND;
        }
    }
    ScopedNtHandle process(raw_handle);

    // 2. Validate target identity.
    auto status = validate_target_identity(process.get(), request, error);
    if (status != MM_OK) return status;

    // 3. Enumerate remote modules (PEB walk) — used for export resolution + env apply.
    std::vector<RemoteModuleInfo> remote_modules;
    status = enumerate_remote_modules(process.get(), remote_modules, error);
    if (status != MM_OK) return status;

    // 4. Apply environment variables via shellcode.
    if (request->env_count > 0) {
        status = apply_environment_remote(process.get(), request, request->timeout_ms,
                                           remote_modules, error);
        if (status != MM_OK) return status;
    }

    // 6. Resolve LoadLibraryW from remote kernel32.
    uintptr_t fn_load_library = 0;
    for (const auto& mod : remote_modules) {
        if (_wcsicmp(mod.name.c_str(), L"kernel32.dll") == 0) {
            status = resolve_remote_export(process.get(), mod.base, "LoadLibraryW",
                                            fn_load_library, remote_modules, error);
            if (status != MM_OK) return status;
            break;
        }
    }
    if (!fn_load_library) {
        error = L"Failed to resolve LoadLibraryW in target process.";
        return MM_EXECUTION_FAILED;
    }

    // 7. Load each module via LoadLibraryW.
    for (uint32_t i = 0; i < request->module_count; ++i) {
        const auto module_path = to_wstring(request->modules[i]);
        if (module_path.empty()) {
            status = MM_INVALID_ARGUMENT;
            error = L"Module path cannot be empty.";
            break;
        }

        status = load_library_remote(process.get(), module_path, request->timeout_ms,
                                      fn_load_library, error);
        if (status != MM_OK) break;
    }

    return status;
}

}  // namespace

// ── Public ABI ─────────────────────────────────────────────────────────────────

extern "C" mm_status MM_CALL mm_execute(
    const mm_execute_request* request,
    uint16_t* error_buffer,
    uint32_t error_buffer_capacity,
    uint32_t* error_buffer_written)
{
    if (!request) {
        write_error(L"Request cannot be null.", error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }
    if (request->pid == 0 || !request->modules || request->module_count == 0) {
        write_error(L"Request must include a pid and at least one module.",
                    error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }
    if (request->env_count > 0 && !request->env) {
        write_error(L"Request environment pointer cannot be null when env_count is nonzero.",
                    error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }
    if (request->option_count > 0 && !request->options) {
        write_error(L"Request options pointer cannot be null when option_count is nonzero.",
                    error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }

    std::wstring error;
    if (!validate_options(request, error)) {
        write_error(error, error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }

    const auto status = execute_remote(request, error);
    write_error(error, error_buffer, error_buffer_capacity, error_buffer_written);
    return status;
}

extern "C" int32_t MM_CALL mm_validate_offsets(
    uint16_t* error_buffer,
    uint32_t error_buffer_capacity,
    uint32_t* error_buffer_written)
{
    // Force initialization if not already done.
    if (!win_offsets().initialized) {
        std::wstring init_error;
        if (!syscall_init(init_error)) {
            write_error(init_error, error_buffer, error_buffer_capacity, error_buffer_written);
            return -1;
        }
    }

    const auto& off = win_offsets();
    int32_t result = 0;
    if (!off.ssn_pattern_validated)    result |= 1;
    if (!off.api_set_available)        result |= 2;
    if (off.spi_unique_process_id == 0) result |= 16;

    if (result != 0) {
        std::wstring detail = L"Offset validation failures: ";
        if (result & 1)  detail += L"SSN ";
        if (result & 2)  detail += L"ApiSet ";
        if (result & 16) detail += L"SPI ";
        write_error(detail, error_buffer, error_buffer_capacity, error_buffer_written);
    }

    return result;
}
