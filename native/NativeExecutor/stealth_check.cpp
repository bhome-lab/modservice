// Stealth verification: checks if injected modules are visible via standard APIs
#include <windows.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <cstdio>
#include <string>
#include <vector>

#pragma comment(lib, "psapi.lib")

int wmain(int argc, wchar_t* argv[]) {
    if (argc < 2) { wprintf(L"Usage: stealth_check.exe <process_name>\n"); return 1; }

    // Find PID
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W pe{}; pe.dwSize = sizeof(pe);
    DWORD pid = 0;
    if (Process32FirstW(snap, &pe)) do {
        if (_wcsicmp(pe.szExeFile, argv[1]) == 0) { pid = pe.th32ProcessID; break; }
    } while (Process32NextW(snap, &pe));
    CloseHandle(snap);
    if (!pid) { wprintf(L"Process not found\n"); return 1; }

    HANDLE hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!hProc) { wprintf(L"OpenProcess failed\n"); return 1; }

    // 1. Check PEB module list (EnumProcessModules)
    wprintf(L"\n=== PEB Module List (EnumProcessModulesEx) ===\n");
    HMODULE mods[2048];
    DWORD needed = 0;
    EnumProcessModulesEx(hProc, mods, sizeof(mods), &needed, LIST_MODULES_ALL);
    int mod_count = needed / sizeof(HMODULE);
    bool found_sample = false, found_dep = false;
    for (int i = 0; i < mod_count; i++) {
        wchar_t name[MAX_PATH]{};
        GetModuleBaseNameW(hProc, mods[i], name, MAX_PATH);
        if (_wcsicmp(name, L"SampleModule.dll") == 0) found_sample = true;
        if (_wcsicmp(name, L"DepModule.dll") == 0) found_dep = true;
    }
    wprintf(L"Total modules in PEB: %d\n", mod_count);
    wprintf(L"SampleModule.dll in PEB: %s\n", found_sample ? L"YES (VISIBLE!)" : L"NO (stealthy)");
    wprintf(L"DepModule.dll in PEB: %s\n", found_dep ? L"YES (VISIBLE!)" : L"NO (stealthy)");

    // 2. Check thread list for our injected thread
    wprintf(L"\n=== Thread List (CreateToolhelp32Snapshot) ===\n");
    HANDLE tsnap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    THREADENTRY32 te{}; te.dwSize = sizeof(te);
    int thread_count = 0;
    if (Thread32First(tsnap, &te)) do {
        if (te.th32OwnerProcessID == pid) thread_count++;
    } while (Thread32Next(tsnap, &te));
    CloseHandle(tsnap);
    wprintf(L"Thread count: %d\n", thread_count);

    // 3. Scan memory regions for suspicious private executable memory
    wprintf(L"\n=== Memory Regions (Private + Executable) ===\n");
    MEMORY_BASIC_INFORMATION mbi{};
    uintptr_t addr = 0;
    int private_exec_count = 0;
    while (VirtualQueryEx(hProc, reinterpret_cast<PVOID>(addr), &mbi, sizeof(mbi))) {
        if (mbi.Type == MEM_PRIVATE && mbi.State == MEM_COMMIT) {
            bool executable = (mbi.Protect == PAGE_EXECUTE ||
                             mbi.Protect == PAGE_EXECUTE_READ ||
                             mbi.Protect == PAGE_EXECUTE_READWRITE ||
                             mbi.Protect == PAGE_EXECUTE_WRITECOPY);
            if (executable) {
                wprintf(L"  [PRIVATE+EXEC] 0x%llX size=0x%llX protect=0x%X\n",
                       (uint64_t)mbi.BaseAddress, (uint64_t)mbi.RegionSize, mbi.Protect);
                private_exec_count++;
            }
        }
        addr = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
        if (addr == 0) break;
    }
    wprintf(L"Private executable regions: %d\n", private_exec_count);

    // 4. Check if PE headers are scrubbed at known private regions
    wprintf(L"\n=== PE Header Check (MZ signature scan in private exec) ===\n");
    addr = 0;
    int pe_headers_found = 0;
    while (VirtualQueryEx(hProc, reinterpret_cast<PVOID>(addr), &mbi, sizeof(mbi))) {
        if (mbi.Type == MEM_PRIVATE && mbi.State == MEM_COMMIT && mbi.RegionSize >= 0x1000) {
            uint16_t magic = 0;
            SIZE_T read = 0;
            ReadProcessMemory(hProc, mbi.BaseAddress, &magic, 2, &read);
            if (read == 2 && magic == 0x5A4D) { // 'MZ'
                wprintf(L"  [MZ FOUND] at 0x%llX (type=%s, protect=0x%X)\n",
                       (uint64_t)mbi.BaseAddress,
                       mbi.Type == MEM_IMAGE ? L"IMAGE" : mbi.Type == MEM_MAPPED ? L"MAPPED" : L"PRIVATE",
                       mbi.Protect);
                pe_headers_found++;
            }
        }
        addr = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
        if (addr == 0) break;
    }
    wprintf(L"PE headers in private memory: %d\n", pe_headers_found);

    wprintf(L"\n=== Summary ===\n");
    wprintf(L"PEB visible: %s\n", (found_sample || found_dep) ? L"FAIL" : L"PASS (not in PEB)");
    wprintf(L"PE headers hidden: %s\n", pe_headers_found == 0 ? L"PASS" : L"PARTIAL (MZ stubs preserved for CRT)");

    CloseHandle(hProc);
    return 0;
}
