#include <windows.h>

#include <string>

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
    return 42;
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        wchar_t marker[256] = {};
        if (GetEnvironmentVariableW(L"MODSERVICE_SAMPLE_MARKER", marker, 256) > 0) {
            append_line(marker);
        } else {
            append_line(L"missing-marker");
        }
    }

    return TRUE;
}
