#include <windows.h>

#include <string>

// Import from DepModule (tests cross-module dependency resolution).
extern "C" __declspec(dllimport) int DepModuleGetValue();

namespace {

void append_line(const wchar_t* line) {
    wchar_t output_path[MAX_PATH] = {};
    const DWORD size = GetEnvironmentVariableW(L"MODSERVICE_SAMPLE_OUTPUT", output_path, MAX_PATH);
    if (size == 0 || size >= MAX_PATH) {
        return;
    }

    HANDLE file = CreateFileW(output_path, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }

    const std::wstring text = std::wstring(line) + L"\r\n";
    DWORD written = 0;
    WriteFile(file, text.data(), static_cast<DWORD>(text.size() * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(file);
}

}  // namespace

extern "C" __declspec(dllexport) int SampleModuleTouch() {
    return 45;
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // Verify DepModule is accessible (proves dependency was manually mapped).
        const int dep_val = DepModuleGetValue();

        wchar_t marker[256] = {};
        if (GetEnvironmentVariableW(L"MODSERVICE_SAMPLE_MARKER", marker, 256) > 0) {
            // Append marker + dep value to output.
            std::wstring line = std::wstring(marker) + L":" + std::to_wstring(dep_val);
            append_line(line.c_str());
        } else {
            append_line(L"missing-marker-v5");
        }
    }

    return TRUE;
}
