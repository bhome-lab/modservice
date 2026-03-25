// ── Comprehensive C++ Feature Test for Manual Mapping ──────────────────────────
// This DLL exercises every major C++ language/runtime feature to verify
// correct behavior when loaded via manual mapping (no LoadLibrary).
//
// Each test writes a result line to MODSERVICE_TEST_OUTPUT.
// Format: "TEST_NAME:PASS" or "TEST_NAME:FAIL:reason"

#include <windows.h>
#include <cstdio>
#include <string>
#include <stdexcept>
#include <mutex>
#include <vector>
#include <functional>
#include <atomic>

namespace {

// ── Output helper ──────────────────────────────────────────────────────────────

HANDLE g_output = INVALID_HANDLE_VALUE;
std::mutex g_output_mutex;

void report(const char* test_name, bool pass, const char* detail = nullptr) {
    std::lock_guard lock(g_output_mutex);
    if (g_output == INVALID_HANDLE_VALUE) return;

    char buf[512];
    int len;
    if (pass) {
        len = _snprintf_s(buf, sizeof(buf), _TRUNCATE, "%s:PASS\r\n", test_name);
    } else {
        len = _snprintf_s(buf, sizeof(buf), _TRUNCATE, "%s:FAIL:%s\r\n",
                          test_name, detail ? detail : "unknown");
    }
    if (len > 0) {
        DWORD written;
        WriteFile(g_output, buf, static_cast<DWORD>(len), &written, nullptr);
    }
}

// ── 1. Static constructor / destructor ─────────────────────────────────────────

struct StaticInit {
    int value;
    StaticInit() : value(42) {}
    ~StaticInit() { value = 0; }
};

static StaticInit g_static_init;

void test_static_constructor() {
    report("STATIC_CTOR", g_static_init.value == 42, "value != 42");
}

// ── 2. C++ exceptions (throw / catch) ──────────────────────────────────────────

// C++ exception tests — wrapped in SEH to prevent process crash.

static bool test_throw_int() {
    try { throw 42; }
    catch (int v) { return v == 42; }
    catch (...) { return false; }
    return false;
}

static bool test_throw_catchall() {
    try { throw 42; }
    catch (...) { return true; }
    return false;
}

static bool test_throw_runtime_error() {
    try { throw std::runtime_error("test-exception"); }
    catch (const std::runtime_error& e) { return std::string(e.what()) == "test-exception"; }
    catch (...) { return false; }
    return false;
}

static bool test_catch_crt_throw() {
    try {
        std::vector<int> v;
        v.at(999);
    } catch (const std::exception&) { return true; }
    catch (...) { return false; }
    return false;
}

// SEH wrappers must be in separate functions (no C++ objects with dtors).
static bool seh_wrap_crt()     { __try { return test_catch_crt_throw(); }   __except(1) { return false; } }
static bool seh_wrap_int()     { __try { return test_throw_int(); }         __except(1) { return false; } }
static bool seh_wrap_all()     { __try { return test_throw_catchall(); }    __except(1) { return false; } }
static bool seh_wrap_runtime() { __try { return test_throw_runtime_error(); } __except(1) { return false; } }

void test_cpp_exceptions() {
    report("CPP_CATCH_CRT_THROW",    seh_wrap_crt(),     "can't catch CRT-originated exception");
    report("CPP_THROW_INT",          seh_wrap_int(),     "throw int / catch int failed");
    report("CPP_CATCH_ALL",          seh_wrap_all(),     "throw int / catch(...) failed");
    report("CPP_THROW_RUNTIME_ERROR", seh_wrap_runtime(), "throw runtime_error failed");
}

// ── 3. RTTI (dynamic_cast, typeid) ─────────────────────────────────────────────

struct Base { virtual ~Base() = default; };
struct Derived : Base { int marker = 99; };

void test_rtti() {
    Derived d;
    Base* bp = &d;

    // dynamic_cast
    auto* dp = dynamic_cast<Derived*>(bp);
    bool dc_ok = (dp != nullptr && dp->marker == 99);

    // typeid
    bool ti_ok = (typeid(*bp) == typeid(Derived));

    report("RTTI_DYNAMIC_CAST", dc_ok, "dynamic_cast returned null or wrong object");
    report("RTTI_TYPEID", ti_ok, "typeid mismatch");
}

// ── 4. SEH (__try / __except) ──────────────────────────────────────────────────
// Note: SEH and C++ exceptions cannot mix in the same function on MSVC.
// Put SEH test in a separate function compiled with /EHa if needed,
// or use a helper.

#pragma warning(push)
#pragma warning(disable: 4611)  // setjmp and C++ destructors

static bool seh_test_helper() {
    __try {
        int* p = nullptr;
        *p = 42;  // trigger access violation
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return true;  // caught
    }
    return false;
}

#pragma warning(pop)

void test_seh() {
    bool caught = seh_test_helper();
    report("SEH", caught, "SEH exception not caught");
}

// ── 5. Security cookie (/GS) ──────────────────────────────────────────────────

extern "C" uintptr_t __security_cookie;

void test_security_cookie() {
    // Default cookie on x64 is 0x00002B992DDFA232. After init it should differ.
    bool initialized = (__security_cookie != 0x00002B992DDFA232ULL &&
                        __security_cookie != 0);
    report("SECURITY_COOKIE", initialized, "cookie not initialized");
}

// ── 6. std::mutex ──────────────────────────────────────────────────────────────

void test_mutex() {
    std::mutex m;
    bool locked = m.try_lock();
    if (locked) m.unlock();
    report("STD_MUTEX", locked, "try_lock failed");
}

// ── 7. std::vector + heap allocation ───────────────────────────────────────────

void test_heap() {
    bool ok = false;
    try {
        std::vector<int> v;
        for (int i = 0; i < 1000; ++i) v.push_back(i);
        ok = (v.size() == 1000 && v[999] == 999);
    } catch (...) {
        ok = false;
    }
    report("HEAP_ALLOC", ok, "vector allocation or access failed");
}

// ── 8. std::function / lambdas ─────────────────────────────────────────────────

void test_lambda() {
    int captured = 7;
    std::function<int()> fn = [&]() { return captured * 6; };
    report("LAMBDA", fn() == 42, "lambda returned wrong value");
}

// ── 9. Atomic operations ──────────────────────────────────────────────────────

void test_atomics() {
    std::atomic<int> val(0);
    val.fetch_add(10);
    val.fetch_sub(3);
    report("ATOMICS", val.load() == 7, "atomic result wrong");
}

// ── 10. Cross-module import (from DepModule) ──────────────────────────────────

extern "C" __declspec(dllimport) int DepModuleGetValue();

void test_cross_module() {
    int val = DepModuleGetValue();
    report("CROSS_MODULE_IMPORT", val == 42, "DepModuleGetValue returned wrong value");
}

}  // namespace

// ── DllMain ────────────────────────────────────────────────────────────────────

extern "C" __declspec(dllexport) int RunAllTests() {
    // Can be called after DllMain to run tests that need the CRT fully initialized.
    test_static_constructor();
    test_security_cookie();
    test_mutex();
    test_heap();
    test_lambda();
    test_atomics();
    test_cross_module();
    // Run exception-dependent tests last (may crash if exceptions aren't fully supported).
    test_seh();
    test_rtti();
    test_cpp_exceptions();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        wchar_t path[MAX_PATH] = {};
        if (GetEnvironmentVariableW(L"MODSERVICE_TEST_OUTPUT", path, MAX_PATH) > 0) {
            g_output = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ,
                                    nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        }

        report("DLLMAIN_ATTACH", true);

        // Run all tests from DllMain context (CRT is initialized by _DllMainCRTStartup).
        RunAllTests();

        if (g_output != INVALID_HANDLE_VALUE) {
            CloseHandle(g_output);
            g_output = INVALID_HANDLE_VALUE;
        }
    }
    return TRUE;
}
