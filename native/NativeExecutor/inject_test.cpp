// ── Standalone injection test ────────────────────────────────────────────────
// Usage: inject_test.exe <target_exe_name> [module_path]
//
// Finds the target process by name, injects SampleModule (or specified module),
// and reports success/failure. Uses mm_execute API directly.
//
// For testing: inject_test.exe PathOfExile.exe

#include "mm_executor.h"
#include <windows.h>
#include <tlhelp32.h>
#include <cstdio>
#include <string>

struct ProcessInfo {
    DWORD pid;
    FILETIME create_time;
    std::wstring exe_path;
};

bool find_process(const wchar_t* name, ProcessInfo& out) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;

    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    bool found = false;

    if (Process32FirstW(snap, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, name) == 0) {
                out.pid = pe.th32ProcessID;

                HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pe.th32ProcessID);
                if (hProc) {
                    FILETIME ct{}, et{}, kt{}, ut{};
                    GetProcessTimes(hProc, &ct, &et, &kt, &ut);
                    out.create_time = ct;

                    DWORD path_len = MAX_PATH;
                    wchar_t path_buf[MAX_PATH]{};
                    QueryFullProcessImageNameW(hProc, 0, path_buf, &path_len);
                    out.exe_path = path_buf;

                    CloseHandle(hProc);
                }
                found = true;
                break;
            }
        } while (Process32NextW(snap, &pe));
    }

    CloseHandle(snap);
    return found;
}

int wmain(int argc, wchar_t* argv[]) {
    if (argc < 2) {
        wprintf(L"Usage: inject_test.exe <process_name> [module_path]\n");
        wprintf(L"Example: inject_test.exe PathOfExile.exe\n");
        return 1;
    }

    const wchar_t* target_name = argv[1];

    // Find target process.
    ProcessInfo proc{};
    if (!find_process(target_name, proc)) {
        wprintf(L"[ERROR] Process not found: %s\n", target_name);
        return 1;
    }

    ULARGE_INTEGER ct_val{};
    ct_val.LowPart = proc.create_time.dwLowDateTime;
    ct_val.HighPart = proc.create_time.dwHighDateTime;

    wprintf(L"[INFO] Found process: %s (PID %u)\n", proc.exe_path.c_str(), proc.pid);
    wprintf(L"[INFO] Create time: %llu\n", ct_val.QuadPart);

    // Determine module paths — find artifacts relative to our exe.
    wchar_t self_path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, self_path, MAX_PATH);
    std::wstring self_dir(self_path);
    {
        auto pos = self_dir.find_last_of(L"\\/");
        if (pos != std::wstring::npos) self_dir.resize(pos);
    }
    // Navigate up to the artifacts root: self is in artifacts/native/NativeExecutor/x64/Debug
    // We need artifacts/native/SampleModule/x64/Debug and artifacts/native/DepModule/x64/Debug.
    std::wstring artifacts_base = self_dir + L"\\..\\..";

    std::wstring module_path;
    if (argc >= 3) {
        module_path = argv[2];
    } else {
        module_path = artifacts_base + L"\\SampleModule\\x64\\Debug\\SampleModule.dll";
    }

    wprintf(L"[INFO] Module: %s\n", module_path.c_str());

    if (GetFileAttributesW(module_path.c_str()) == INVALID_FILE_ATTRIBUTES) {
        wprintf(L"[ERROR] Module file not found: %s\n", module_path.c_str());
        return 1;
    }

    // Also check for DepModule.dll (dependency of SampleModule).
    std::wstring dep_path = artifacts_base + L"\\DepModule\\x64\\Debug\\DepModule.dll";
    bool has_dep = (GetFileAttributesW(dep_path.c_str()) != INVALID_FILE_ATTRIBUTES);

    // Set up an output file for the sample module to write to.
    wchar_t temp_path[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp_path);
    std::wstring output_file = std::wstring(temp_path) + L"inject_test_output.txt";

    // Delete any previous output.
    DeleteFileW(output_file.c_str());

    wprintf(L"[INFO] Output file: %s\n", output_file.c_str());
    wprintf(L"[INFO] Injecting...\n");

    // Build the request.
    // Modules: DepModule first (if it exists), then SampleModule.
    mm_u16_view module_views[2]{};
    uint32_t module_count = 0;

    if (has_dep) {
        module_views[module_count].ptr = reinterpret_cast<const uint16_t*>(dep_path.c_str());
        module_views[module_count].len = static_cast<uint32_t>(dep_path.size());
        module_count++;
    }
    module_views[module_count].ptr = reinterpret_cast<const uint16_t*>(module_path.c_str());
    module_views[module_count].len = static_cast<uint32_t>(module_path.size());
    module_count++;

    // Environment: set output path and marker.
    std::wstring env_name_output = L"MODSERVICE_SAMPLE_OUTPUT";
    std::wstring env_name_marker = L"MODSERVICE_SAMPLE_MARKER";
    std::wstring env_val_marker  = L"poe-inject-test";

    mm_env_var env_vars[2]{};
    env_vars[0].name.ptr  = reinterpret_cast<const uint16_t*>(env_name_output.c_str());
    env_vars[0].name.len  = static_cast<uint32_t>(env_name_output.size());
    env_vars[0].value.ptr = reinterpret_cast<const uint16_t*>(output_file.c_str());
    env_vars[0].value.len = static_cast<uint32_t>(output_file.size());

    env_vars[1].name.ptr  = reinterpret_cast<const uint16_t*>(env_name_marker.c_str());
    env_vars[1].name.len  = static_cast<uint32_t>(env_name_marker.size());
    env_vars[1].value.ptr = reinterpret_cast<const uint16_t*>(env_val_marker.c_str());
    env_vars[1].value.len = static_cast<uint32_t>(env_val_marker.size());

    mm_u16_view exe_path_view{};
    exe_path_view.ptr = reinterpret_cast<const uint16_t*>(proc.exe_path.c_str());
    exe_path_view.len = static_cast<uint32_t>(proc.exe_path.size());

    mm_execute_request request{};
    request.pid = proc.pid;
    request.process_create_time_utc_100ns = ct_val.QuadPart;
    request.exe_path = exe_path_view;
    request.modules = module_views;
    request.module_count = module_count;
    request.env = env_vars;
    request.env_count = 2;
    request.options = nullptr;
    request.option_count = 0;
    request.timeout_ms = 10000;

    // Execute.
    wchar_t error_buf[1024]{};
    uint32_t error_written = 0;
    auto status = mm_execute(
        &request,
        reinterpret_cast<uint16_t*>(error_buf),
        1024,
        &error_written);

    if (status == MM_OK) {
        wprintf(L"[OK] Injection succeeded!\n");

        // Check output file.
        Sleep(500);
        HANDLE hFile = CreateFileW(output_file.c_str(), GENERIC_READ, FILE_SHARE_READ,
                                    nullptr, OPEN_EXISTING, 0, nullptr);
        if (hFile != INVALID_HANDLE_VALUE) {
            char buf[512]{};
            DWORD bytes_read = 0;
            ReadFile(hFile, buf, sizeof(buf) - 1, &bytes_read, nullptr);
            CloseHandle(hFile);

            wprintf(L"[OK] Output file contents (raw bytes: %u):\n", bytes_read);
            // It's UTF-16 LE.
            auto* wbuf = reinterpret_cast<wchar_t*>(buf);
            wprintf(L"  \"%s\"\n", wbuf);

            // Verify expected content.
            std::wstring content(wbuf);
            if (content.find(L"poe-inject-test:42") != std::wstring::npos) {
                wprintf(L"[PASS] Output matches expected \"poe-inject-test:42\"\n");
            } else {
                wprintf(L"[WARN] Output doesn't match expected. Got: %s\n", wbuf);
            }
        } else {
            wprintf(L"[WARN] Output file not created (module DllMain may not have run yet).\n");
        }

        DeleteFileW(output_file.c_str());
    } else {
        wprintf(L"[FAIL] Injection failed with status %d\n", static_cast<int>(status));
        if (error_written > 0) {
            wprintf(L"[FAIL] Error: %s\n", error_buf);
        }
    }

    wprintf(L"\n[INFO] Check if target process is still running...\n");
    Sleep(2000);

    ProcessInfo check{};
    if (find_process(target_name, check) && check.pid == proc.pid) {
        wprintf(L"[OK] Target process is still alive (PID %u). No crash!\n", check.pid);
    } else {
        wprintf(L"[FAIL] Target process appears to have crashed or exited!\n");
        return 2;
    }

    return (status == MM_OK) ? 0 : 1;
}
