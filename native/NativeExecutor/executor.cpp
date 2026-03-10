#include "mm_executor.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

namespace {

struct SavedEnvironmentVariable {
    std::wstring name;
    std::wstring value;
    bool existed;
};

std::wstring to_wstring(const mm_u16_view& view) {
    if (view.ptr == nullptr || view.len == 0) {
        return std::wstring();
    }

    return std::wstring(reinterpret_cast<const wchar_t*>(view.ptr), view.len);
}

void write_error(const std::wstring& message, uint16_t* buffer, uint32_t capacity, uint32_t* written) {
    if (written != nullptr) {
        *written = 0;
    }

    if (buffer == nullptr || capacity == 0) {
        return;
    }

    const auto count = static_cast<uint32_t>((std::min)(static_cast<size_t>(capacity - 1), message.size()));
    if (count > 0) {
        memcpy(buffer, message.data(), count * sizeof(wchar_t));
    }

    buffer[count] = 0;
    if (written != nullptr) {
        *written = count;
    }
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

uint64_t get_current_process_create_time() {
    FILETIME create_time{};
    FILETIME exit_time{};
    FILETIME kernel_time{};
    FILETIME user_time{};

    if (!GetProcessTimes(GetCurrentProcess(), &create_time, &exit_time, &kernel_time, &user_time)) {
        return 0;
    }

    ULARGE_INTEGER value{};
    value.LowPart = create_time.dwLowDateTime;
    value.HighPart = create_time.dwHighDateTime;
    return value.QuadPart;
}

bool save_and_set_environment(const mm_execute_request* request, std::vector<SavedEnvironmentVariable>& saved, std::wstring& error) {
    for (uint32_t index = 0; index < request->env_count; ++index) {
        const auto& item = request->env[index];
        const auto name = to_wstring(item.name);
        const auto value = to_wstring(item.value);

        if (name.empty()) {
            error = L"Environment variable name cannot be empty.";
            return false;
        }

        DWORD required = GetEnvironmentVariableW(name.c_str(), nullptr, 0);
        SavedEnvironmentVariable original{ name, std::wstring(), required != 0 };
        if (original.existed) {
            std::wstring buffer(required, L'\0');
            GetEnvironmentVariableW(name.c_str(), &buffer[0], required);
            if (!buffer.empty() && buffer.back() == L'\0') {
                buffer.pop_back();
            }

            original.value = std::move(buffer);
        }

        if (!SetEnvironmentVariableW(name.c_str(), value.c_str())) {
            error = L"Failed to set environment variable '" + name + L"': " + format_system_error(GetLastError());
            return false;
        }

        saved.push_back(std::move(original));
    }

    return true;
}

void restore_environment(const std::vector<SavedEnvironmentVariable>& saved) {
    for (auto iterator = saved.rbegin(); iterator != saved.rend(); ++iterator) {
        if (iterator->existed) {
            SetEnvironmentVariableW(iterator->name.c_str(), iterator->value.c_str());
        } else {
            SetEnvironmentVariableW(iterator->name.c_str(), nullptr);
        }
    }
}

}  // namespace

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

    const DWORD current_pid = GetCurrentProcessId();
    if (request->pid != current_pid) {
        write_error(L"Safe test executor only supports the current process (local-only smoke path).", error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_TARGET_NOT_FOUND;
    }

    const auto current_create_time = get_current_process_create_time();
    if (request->process_create_time_utc_100ns != 0 && request->process_create_time_utc_100ns != current_create_time) {
        write_error(L"Target process identity changed.", error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_TARGET_CHANGED;
    }

    std::vector<SavedEnvironmentVariable> saved_environment;
    std::wstring error;
    if (!save_and_set_environment(request, saved_environment, error)) {
        restore_environment(saved_environment);
        write_error(error, error_buffer, error_buffer_capacity, error_buffer_written);
        return MM_INVALID_ARGUMENT;
    }

    std::vector<HMODULE> loaded_modules;
    loaded_modules.reserve(request->module_count);

    for (uint32_t index = 0; index < request->module_count; ++index) {
        const auto module_path = to_wstring(request->modules[index]);
        if (module_path.empty()) {
            restore_environment(saved_environment);
            write_error(L"Module path cannot be empty.", error_buffer, error_buffer_capacity, error_buffer_written);
            return MM_INVALID_ARGUMENT;
        }

        HMODULE module = LoadLibraryW(module_path.c_str());
        if (module == nullptr) {
            const auto failure = L"LoadLibraryW failed for '" + module_path + L"': " + format_system_error(GetLastError());
            restore_environment(saved_environment);
            write_error(failure, error_buffer, error_buffer_capacity, error_buffer_written);
            return MM_EXECUTION_FAILED;
        }

        loaded_modules.push_back(module);
    }

    for (auto iterator = loaded_modules.rbegin(); iterator != loaded_modules.rend(); ++iterator) {
        FreeLibrary(*iterator);
    }

    restore_environment(saved_environment);
    write_error(L"", error_buffer, error_buffer_capacity, error_buffer_written);
    return MM_OK;
}
