#include "mm_executor.h"
#include <windows.h>
#include <cstdio>
#include <string>

int wmain() {
    // Launch notepad as target.
    STARTUPINFOW si{}; si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    wchar_t cmd[] = L"notepad.exe";
    if (!CreateProcessW(nullptr, cmd, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) {
        wprintf(L"Failed to launch notepad\n");
        return 1;
    }
    Sleep(2000);
    wprintf(L"Notepad PID: %u\n", pi.dwProcessId);

    FILETIME ct{}, et{}, kt{}, ut{};
    GetProcessTimes(pi.hProcess, &ct, &et, &kt, &ut);
    ULARGE_INTEGER ct_val{};
    ct_val.LowPart = ct.dwLowDateTime;
    ct_val.HighPart = ct.dwHighDateTime;

    wchar_t exe_path[MAX_PATH]{};
    DWORD path_len = MAX_PATH;
    QueryFullProcessImageNameW(pi.hProcess, 0, exe_path, &path_len);

    wchar_t self_path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, self_path, MAX_PATH);
    std::wstring self_dir(self_path);
    auto pos = self_dir.find_last_of(L"\\/");
    if (pos != std::wstring::npos) self_dir.resize(pos);

    // self_dir = artifacts/native/NativeExecutor/x64/Debug
    // Go up 3 levels to artifacts/native/, then into DepModule/SampleModule
    wchar_t dep_full[MAX_PATH]{}, mod_full[MAX_PATH]{};
    GetFullPathNameW((self_dir + L"\\..\\..\\..\\DepModule\\x64\\Debug\\DepModule.dll").c_str(), MAX_PATH, dep_full, nullptr);
    GetFullPathNameW((self_dir + L"\\..\\..\\..\\SampleModule\\x64\\Debug\\SampleModule.dll").c_str(), MAX_PATH, mod_full, nullptr);

    wprintf(L"Dep: %s\nMod: %s\nExe: %s\n", dep_full, mod_full, exe_path);

    std::wstring dep_str(dep_full), mod_str(mod_full), exe_str(exe_path);
    mm_u16_view modules[2];
    modules[0].ptr = reinterpret_cast<const uint16_t*>(dep_str.c_str());
    modules[0].len = static_cast<uint32_t>(dep_str.size());
    modules[1].ptr = reinterpret_cast<const uint16_t*>(mod_str.c_str());
    modules[1].len = static_cast<uint32_t>(mod_str.size());

    mm_u16_view exe_view;
    exe_view.ptr = reinterpret_cast<const uint16_t*>(exe_str.c_str());
    exe_view.len = static_cast<uint32_t>(exe_str.size());

    mm_execute_request req{};
    req.pid = pi.dwProcessId;
    req.process_create_time_utc_100ns = ct_val.QuadPart;
    req.exe_path = exe_view;
    req.modules = modules;
    req.module_count = 2;
    req.timeout_ms = 10000;

    wprintf(L"\nCalling mm_execute (no env vars)...\n");
    fflush(stdout);

    wchar_t err[512]{};
    uint32_t err_len = 0;
    auto st = mm_execute(&req, reinterpret_cast<uint16_t*>(err), 512, &err_len);

    wprintf(L"Result: %d\n", (int)st);
    if (err_len > 0) wprintf(L"Error: %s\n", err);

    Sleep(1000);
    DWORD exit_code = 0;
    GetExitCodeProcess(pi.hProcess, &exit_code);
    if (exit_code == STILL_ACTIVE)
        wprintf(L"Notepad still alive!\n");
    else
        wprintf(L"Notepad CRASHED (exit code %u)\n", exit_code);

    TerminateProcess(pi.hProcess, 0);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (st == MM_OK) ? 0 : 1;
}
