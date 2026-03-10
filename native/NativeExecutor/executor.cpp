#include "mm_executor.h"

#include <windows.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

namespace {

constexpr uint32_t kRemoteErrorCapacity = 1024;

struct RemoteEnvironmentVariable {
    const wchar_t* name;
    const wchar_t* value;
};

struct RemotePayload {
    uint32_t env_count;
    const RemoteEnvironmentVariable* env;
    uint32_t status;
    uint32_t error_capacity;
    uint32_t error_written;
    wchar_t* error_buffer;
};

struct ScopedHandle {
    HANDLE value = nullptr;

    ScopedHandle() = default;
    explicit ScopedHandle(HANDLE handle) : value(handle) {}

    ~ScopedHandle() {
        if (value != nullptr && value != INVALID_HANDLE_VALUE) {
            CloseHandle(value);
        }
    }

    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;

    ScopedHandle(ScopedHandle&& other) noexcept : value(other.value) {
        other.value = nullptr;
    }

    ScopedHandle& operator=(ScopedHandle&& other) noexcept {
        if (this != &other) {
            if (value != nullptr && value != INVALID_HANDLE_VALUE) {
                CloseHandle(value);
            }
            value = other.value;
            other.value = nullptr;
        }

        return *this;
    }

    HANDLE get() const {
        return value;
    }

    explicit operator bool() const {
        return value != nullptr && value != INVALID_HANDLE_VALUE;
    }
};

std::wstring to_wstring(const mm_u16_view& view) {
    if (view.ptr == nullptr || view.len == 0) {
        return std::wstring();
    }

    return std::wstring(reinterpret_cast<const wchar_t*>(view.ptr), view.len);
}

void write_error_text(const std::wstring& message, wchar_t* buffer, uint32_t capacity, uint32_t* written) {
    if (written != nullptr) {
        *written = 0;
    }

    if (buffer == nullptr || capacity == 0) {
        return;
    }

    const auto count = static_cast<uint32_t>((std::min)(message.size(), static_cast<size_t>(capacity - 1)));
    if (count > 0) {
        memcpy(buffer, message.data(), count * sizeof(wchar_t));
    }

    buffer[count] = L'\0';
    if (written != nullptr) {
        *written = count;
    }
}

void write_error(const std::wstring& message, uint16_t* buffer, uint32_t capacity, uint32_t* written) {
    write_error_text(message, reinterpret_cast<wchar_t*>(buffer), capacity, written);
}

std::wstring format_system_error(DWORD error) {
    LPWSTR raw = nullptr;
    const DWORD size = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        error,
        0,
        reinterpret_cast<LPWSTR>(&raw),
        0,
        nullptr);

    std::wstring message;
    if (size > 0 && raw != nullptr) {
        message.assign(raw, size);
        LocalFree(raw);
    }

    return message;
}

uint64_t get_process_create_time(HANDLE process) {
    FILETIME create_time{};
    FILETIME exit_time{};
    FILETIME kernel_time{};
    FILETIME user_time{};
    if (!GetProcessTimes(process, &create_time, &exit_time, &kernel_time, &user_time)) {
        return 0;
    }

    ULARGE_INTEGER value{};
    value.LowPart = create_time.dwLowDateTime;
    value.HighPart = create_time.dwHighDateTime;
    return value.QuadPart;
}

std::wstring get_process_image_path(HANDLE process) {
    std::wstring buffer(512, L'\0');
    DWORD length = static_cast<DWORD>(buffer.size());
    while (!QueryFullProcessImageNameW(process, 0, buffer.data(), &length)) {
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
            return std::wstring();
        }

        buffer.resize(buffer.size() * 2);
        length = static_cast<DWORD>(buffer.size());
    }

    buffer.resize(length);
    return buffer;
}

std::wstring get_module_path(HMODULE module) {
    std::wstring buffer(MAX_PATH, L'\0');
    DWORD written = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
    while (written == buffer.size()) {
        buffer.resize(buffer.size() * 2);
        written = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
    }

    if (written == 0) {
        return std::wstring();
    }

    buffer.resize(written);
    return buffer;
}

bool validate_options(const mm_execute_request* request, std::wstring& error) {
    for (uint32_t index = 0; index < request->option_count; ++index) {
        const auto name = to_wstring(request->options[index].name);
        if (name.empty()) {
            error = L"Executor option name cannot be empty.";
            return false;
        }
    }

    return true;
}

void enable_debug_privilege() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) {
        return;
    }

    ScopedHandle token_handle(token);
    TOKEN_PRIVILEGES privileges{};
    privileges.PrivilegeCount = 1;
    privileges.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    if (LookupPrivilegeValueW(nullptr, SE_DEBUG_NAME, &privileges.Privileges[0].Luid)) {
        AdjustTokenPrivileges(token_handle.get(), FALSE, &privileges, 0, nullptr, nullptr);
    }
}

size_t align_up(size_t value, size_t alignment) {
    const size_t remainder = value % alignment;
    return remainder == 0 ? value : value + (alignment - remainder);
}

bool build_remote_payload(
    const mm_execute_request* request,
    uintptr_t remote_base,
    std::vector<BYTE>& buffer,
    std::wstring& error) {
    size_t total = align_up(sizeof(RemotePayload), alignof(RemoteEnvironmentVariable));
    const size_t env_offset = total;
    total += static_cast<size_t>(request->env_count) * sizeof(RemoteEnvironmentVariable);

    for (uint32_t index = 0; index < request->env_count; ++index) {
        const auto name = to_wstring(request->env[index].name);
        if (name.empty()) {
            error = L"Environment variable name cannot be empty.";
            return false;
        }

        const auto value = to_wstring(request->env[index].value);
        total = align_up(total, sizeof(wchar_t));
        total += (name.size() + 1) * sizeof(wchar_t);
        total += (value.size() + 1) * sizeof(wchar_t);
    }

    total = align_up(total, sizeof(wchar_t));
    const size_t error_offset = total;
    total += static_cast<size_t>(kRemoteErrorCapacity) * sizeof(wchar_t);

    buffer.assign(total, 0);

    auto* payload = reinterpret_cast<RemotePayload*>(buffer.data());
    payload->env_count = request->env_count;
    payload->env = request->env_count == 0
        ? nullptr
        : reinterpret_cast<const RemoteEnvironmentVariable*>(remote_base + env_offset);
    payload->status = MM_OK;
    payload->error_capacity = kRemoteErrorCapacity;
    payload->error_written = 0;
    payload->error_buffer = reinterpret_cast<wchar_t*>(remote_base + error_offset);

    auto* remote_env = reinterpret_cast<RemoteEnvironmentVariable*>(buffer.data() + env_offset);
    size_t cursor = env_offset + static_cast<size_t>(request->env_count) * sizeof(RemoteEnvironmentVariable);

    for (uint32_t index = 0; index < request->env_count; ++index) {
        const auto name = to_wstring(request->env[index].name);
        const auto value = to_wstring(request->env[index].value);

        cursor = align_up(cursor, sizeof(wchar_t));
        const size_t name_bytes = (name.size() + 1) * sizeof(wchar_t);
        memcpy(buffer.data() + cursor, name.c_str(), name_bytes);
        remote_env[index].name = reinterpret_cast<const wchar_t*>(remote_base + cursor);
        cursor += name_bytes;

        const size_t value_bytes = (value.size() + 1) * sizeof(wchar_t);
        memcpy(buffer.data() + cursor, value.c_str(), value_bytes);
        remote_env[index].value = reinterpret_cast<const wchar_t*>(remote_base + cursor);
        cursor += value_bytes;
    }

    return true;
}

bool read_remote_payload(HANDLE process, void* remote_payload, std::vector<BYTE>& buffer, std::wstring& error) {
    if (!ReadProcessMemory(process, remote_payload, buffer.data(), buffer.size(), nullptr)) {
        error = L"ReadProcessMemory failed: " + format_system_error(GetLastError());
        return false;
    }

    return true;
}

bool write_remote_payload(HANDLE process, void* remote_payload, const std::vector<BYTE>& buffer, std::wstring& error) {
    if (!WriteProcessMemory(process, remote_payload, buffer.data(), buffer.size(), nullptr)) {
        error = L"WriteProcessMemory failed: " + format_system_error(GetLastError());
        return false;
    }

    return true;
}

std::wstring get_payload_error(const std::vector<BYTE>& buffer, uintptr_t remote_payload) {
    const auto* payload = reinterpret_cast<const RemotePayload*>(buffer.data());
    if (payload->error_written == 0) {
        return std::wstring();
    }

    const auto error_offset = static_cast<size_t>(reinterpret_cast<uintptr_t>(payload->error_buffer) - remote_payload);
    return std::wstring(reinterpret_cast<const wchar_t*>(buffer.data() + error_offset), payload->error_written);
}

HMODULE find_remote_module_by_path(DWORD pid, const std::wstring& module_path) {
    ScopedHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    if (!snapshot) {
        return nullptr;
    }

    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Module32FirstW(snapshot.get(), &entry)) {
        return nullptr;
    }

    do {
        if (_wcsicmp(entry.szExePath, module_path.c_str()) == 0) {
            return entry.hModule;
        }
    } while (Module32NextW(snapshot.get(), &entry));

    return nullptr;
}

LPTHREAD_START_ROUTINE find_remote_proc_address(DWORD pid, const wchar_t* module_name, const char* proc_name) {
    const HMODULE local_module = GetModuleHandleW(module_name);
    if (local_module == nullptr) {
        return nullptr;
    }

    const FARPROC local_proc = GetProcAddress(local_module, proc_name);
    if (local_proc == nullptr) {
        return nullptr;
    }

    ScopedHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    if (!snapshot) {
        return nullptr;
    }

    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Module32FirstW(snapshot.get(), &entry)) {
        return nullptr;
    }

    do {
        if (_wcsicmp(entry.szModule, module_name) == 0) {
            const auto offset = reinterpret_cast<const BYTE*>(local_proc) - reinterpret_cast<const BYTE*>(local_module);
            return reinterpret_cast<LPTHREAD_START_ROUTINE>(reinterpret_cast<BYTE*>(entry.hModule) + offset);
        }
    } while (Module32NextW(snapshot.get(), &entry));

    return nullptr;
}

mm_status wait_for_thread(HANDLE thread, uint32_t timeout_ms, const wchar_t* action, std::wstring& error) {
    const DWORD wait_result = WaitForSingleObject(thread, timeout_ms == 0 ? INFINITE : timeout_ms);
    if (wait_result == WAIT_OBJECT_0) {
        return MM_OK;
    }

    if (wait_result == WAIT_TIMEOUT) {
        error = L"Timed out waiting for " + std::wstring(action) + L".";
        return MM_TIMEOUT;
    }

    error = L"WaitForSingleObject failed for " + std::wstring(action) + L": " + format_system_error(GetLastError());
    return MM_EXECUTION_FAILED;
}

mm_status inject_remote_library(
    HANDLE process,
    DWORD pid,
    const std::wstring& module_path,
    uint32_t timeout_ms,
    std::wstring& error) {
    const SIZE_T bytes = (module_path.size() + 1) * sizeof(wchar_t);
    void* remote_path = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remote_path == nullptr) {
        error = L"VirtualAllocEx failed for remote module path: " + format_system_error(GetLastError());
        return MM_EXECUTION_FAILED;
    }

    auto free_remote_path = [&]() {
        if (remote_path != nullptr) {
            VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
            remote_path = nullptr;
        }
    };

    if (!WriteProcessMemory(process, remote_path, module_path.c_str(), bytes, nullptr)) {
        error = L"WriteProcessMemory failed for remote module path: " + format_system_error(GetLastError());
        free_remote_path();
        return MM_EXECUTION_FAILED;
    }

    const auto load_library = find_remote_proc_address(pid, L"kernel32.dll", "LoadLibraryW");
    if (load_library == nullptr) {
        error = L"Could not resolve remote LoadLibraryW.";
        free_remote_path();
        return MM_EXECUTION_FAILED;
    }

    ScopedHandle thread(CreateRemoteThread(process, nullptr, 0, load_library, remote_path, 0, nullptr));
    if (!thread) {
        error = L"CreateRemoteThread failed for remote LoadLibraryW: " + format_system_error(GetLastError());
        free_remote_path();
        return MM_EXECUTION_FAILED;
    }

    const auto status = wait_for_thread(thread.get(), timeout_ms, L"remote LoadLibraryW", error);
    if (status != MM_OK) {
        free_remote_path();
        return status;
    }

    DWORD exit_code = 0;
    if (!GetExitCodeThread(thread.get(), &exit_code) || exit_code == 0) {
        error = L"Remote LoadLibraryW failed for '" + module_path + L"'.";
        free_remote_path();
        return MM_EXECUTION_FAILED;
    }

    free_remote_path();
    return MM_OK;
}

mm_status unload_remote_library(
    HANDLE process,
    DWORD pid,
    HMODULE remote_module,
    uint32_t timeout_ms,
    std::wstring& error) {
    const auto free_library = find_remote_proc_address(pid, L"kernel32.dll", "FreeLibrary");
    if (free_library == nullptr) {
        error = L"Could not resolve remote FreeLibrary.";
        return MM_EXECUTION_FAILED;
    }

    ScopedHandle thread(CreateRemoteThread(process, nullptr, 0, free_library, remote_module, 0, nullptr));
    if (!thread) {
        error = L"CreateRemoteThread failed for remote FreeLibrary: " + format_system_error(GetLastError());
        return MM_EXECUTION_FAILED;
    }

    const auto status = wait_for_thread(thread.get(), timeout_ms, L"remote FreeLibrary", error);
    if (status != MM_OK) {
        return status;
    }

    DWORD exit_code = 0;
    if (!GetExitCodeThread(thread.get(), &exit_code) || exit_code == 0) {
        error = L"Remote FreeLibrary failed.";
        return MM_EXECUTION_FAILED;
    }

    return MM_OK;
}

mm_status validate_target_identity(HANDLE process, const mm_execute_request* request, std::wstring& error) {
    const auto actual_create_time = get_process_create_time(process);
    if (request->process_create_time_utc_100ns != 0 && actual_create_time != request->process_create_time_utc_100ns) {
        error = L"Target process identity changed.";
        return MM_TARGET_CHANGED;
    }

    const auto expected_exe_path = to_wstring(request->exe_path);
    if (!expected_exe_path.empty()) {
        const auto actual_exe_path = get_process_image_path(process);
        if (actual_exe_path.empty() || _wcsicmp(actual_exe_path.c_str(), expected_exe_path.c_str()) != 0) {
            error = L"Target executable path changed.";
            return MM_TARGET_CHANGED;
        }
    }

    return MM_OK;
}

mm_status execute_remote(const mm_execute_request* request, std::wstring& error) {
    enable_debug_privilege();

    ScopedHandle process(OpenProcess(
        PROCESS_CREATE_THREAD |
        PROCESS_QUERY_INFORMATION |
        PROCESS_QUERY_LIMITED_INFORMATION |
        PROCESS_VM_OPERATION |
        PROCESS_VM_WRITE |
        PROCESS_VM_READ,
        FALSE,
        request->pid));
    if (!process) {
        error = L"OpenProcess failed: " + format_system_error(GetLastError());
        return MM_TARGET_NOT_FOUND;
    }

    auto status = validate_target_identity(process.get(), request, error);
    if (status != MM_OK) {
        return status;
    }

    HMODULE local_module = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&mm_execute),
            &local_module)) {
        error = L"GetModuleHandleExW failed: " + format_system_error(GetLastError());
        return MM_EXECUTION_FAILED;
    }

    const auto self_path = get_module_path(local_module);
    if (self_path.empty()) {
        error = L"Failed to resolve executor module path.";
        return MM_EXECUTION_FAILED;
    }

    status = inject_remote_library(process.get(), request->pid, self_path, request->timeout_ms, error);
    if (status != MM_OK) {
        return status;
    }

    const HMODULE remote_module = find_remote_module_by_path(request->pid, self_path);
    if (remote_module == nullptr) {
        error = L"Failed to locate executor module in target process.";
        return MM_EXECUTION_FAILED;
    }

    std::vector<BYTE> payload_buffer;
    if (!build_remote_payload(request, 0, payload_buffer, error)) {
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        return MM_INVALID_ARGUMENT;
    }

    void* remote_payload = VirtualAllocEx(process.get(), nullptr, payload_buffer.size(), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remote_payload == nullptr) {
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        error = L"VirtualAllocEx failed for remote payload: " + format_system_error(GetLastError());
        return MM_EXECUTION_FAILED;
    }

    auto free_payload = [&]() {
        if (remote_payload != nullptr) {
            VirtualFreeEx(process.get(), remote_payload, 0, MEM_RELEASE);
            remote_payload = nullptr;
        }
    };

    if (!build_remote_payload(request, reinterpret_cast<uintptr_t>(remote_payload), payload_buffer, error)) {
        free_payload();
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        return MM_INVALID_ARGUMENT;
    }

    if (!write_remote_payload(process.get(), remote_payload, payload_buffer, error)) {
        free_payload();
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        return MM_EXECUTION_FAILED;
    }

    const FARPROC local_remote_apply = GetProcAddress(local_module, "mm_remote_apply");
    if (local_remote_apply == nullptr) {
        free_payload();
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        error = L"Failed to resolve mm_remote_apply export.";
        return MM_EXECUTION_FAILED;
    }

    const auto remote_apply = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        reinterpret_cast<BYTE*>(remote_module) +
        (reinterpret_cast<const BYTE*>(local_remote_apply) - reinterpret_cast<const BYTE*>(local_module)));

    ScopedHandle remote_thread(CreateRemoteThread(process.get(), nullptr, 0, remote_apply, remote_payload, 0, nullptr));
    if (!remote_thread) {
        free_payload();
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        error = L"CreateRemoteThread failed for mm_remote_apply: " + format_system_error(GetLastError());
        return MM_EXECUTION_FAILED;
    }

    status = wait_for_thread(remote_thread.get(), request->timeout_ms, L"remote environment apply", error);
    if (status != MM_OK) {
        free_payload();
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        return status;
    }

    if (!read_remote_payload(process.get(), remote_payload, payload_buffer, error)) {
        free_payload();
        std::wstring unload_error;
        unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
        return MM_EXECUTION_FAILED;
    }

    status = static_cast<mm_status>(reinterpret_cast<const RemotePayload*>(payload_buffer.data())->status);
    error = get_payload_error(payload_buffer, reinterpret_cast<uintptr_t>(remote_payload));

    if (status == MM_OK) {
        for (uint32_t index = 0; index < request->module_count; ++index) {
            const auto module_path = to_wstring(request->modules[index]);
            if (module_path.empty()) {
                status = MM_INVALID_ARGUMENT;
                error = L"Module path cannot be empty.";
                break;
            }

            status = inject_remote_library(process.get(), request->pid, module_path, request->timeout_ms, error);
            if (status != MM_OK) {
                break;
            }
        }
    }

    free_payload();
    std::wstring unload_error;
    const auto unload_status = unload_remote_library(process.get(), request->pid, remote_module, request->timeout_ms, unload_error);
    if (status == MM_OK && unload_status != MM_OK) {
        status = unload_status;
        error = unload_error;
    }

    return status;
}

bool apply_remote_environment(const RemotePayload* payload, mm_status& status, std::wstring& error) {
    if (payload->env_count > 0 && payload->env == nullptr) {
        status = MM_INVALID_ARGUMENT;
        error = L"Remote payload env array cannot be null.";
        return false;
    }

    for (uint32_t index = 0; index < payload->env_count; ++index) {
        const auto& item = payload->env[index];
        if (item.name == nullptr || item.name[0] == L'\0' || item.value == nullptr) {
            status = MM_INVALID_ARGUMENT;
            error = L"Remote environment variable is invalid.";
            return false;
        }

        if (!SetEnvironmentVariableW(item.name, item.value)) {
            status = MM_EXECUTION_FAILED;
            error = L"Failed to set remote environment variable '" + std::wstring(item.name) + L"': " + format_system_error(GetLastError());
            return false;
        }
    }

    status = MM_OK;
    error.clear();
    return true;
}

}  // namespace

extern "C" __declspec(dllexport) DWORD WINAPI mm_remote_apply(void* parameter) {
    auto* payload = static_cast<RemotePayload*>(parameter);
    if (payload == nullptr) {
        return MM_INVALID_ARGUMENT;
    }

    mm_status status = MM_OK;
    std::wstring error;
    apply_remote_environment(payload, status, error);
    payload->status = status;
    write_error_text(error, payload->error_buffer, payload->error_capacity, &payload->error_written);
    return status;
}

extern "C" mm_status MM_CALL mm_execute(
    const mm_execute_request* request,
    uint16_t* error_buffer,
    uint32_t error_buffer_capacity,
    uint32_t* error_buffer_written) {
    if (request == nullptr) {
        write_error(L"Request cannot be null.", error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }

    if (request->pid == 0 || request->modules == nullptr || request->module_count == 0) {
        write_error(L"Request must include a pid and at least one module.", error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }

    if (request->env_count > 0 && request->env == nullptr) {
        write_error(L"Request environment pointer cannot be null when env_count is nonzero.", error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }

    if (request->option_count > 0 && request->options == nullptr) {
        write_error(L"Request options pointer cannot be null when option_count is nonzero.", error_buffer, error_buffer_capacity, error_buffer_written);
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
