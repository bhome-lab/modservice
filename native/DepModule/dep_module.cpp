#include "dep_module.h"
#include <windows.h>

extern "C" __declspec(dllexport) int DepModuleGetValue() {
    return 42;
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // Intentionally minimal — existence proves the DLL was loaded.
    }
    return TRUE;
}
