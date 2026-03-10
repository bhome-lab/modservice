#include <windows.h>

#include <string>

namespace {

std::wstring read_environment(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) {
        return std::wstring();
    }

    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0) {
        return std::wstring();
    }

    value.resize(written);
    return value;
}

void write_text_file(const std::wstring& path, const std::wstring& text) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }

    DWORD written = 0;
    WriteFile(file, text.data(), static_cast<DWORD>(text.size() * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(file);
}

}  // namespace

int wmain() {
    Sleep(2000);

    for (int attempt = 0; attempt < 300; ++attempt) {
        const auto persist_path = read_environment(L"MODSERVICE_TESTAPP_PERSIST_PATH");
        if (!persist_path.empty()) {
            const auto marker = read_environment(L"MODSERVICE_SAMPLE_MARKER");
            write_text_file(persist_path, marker.empty() ? L"missing-marker" : marker);
            Sleep(1000);
            return 0;
        }

        Sleep(100);
    }

    return 1;
}
