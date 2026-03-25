# Manual Mapper — Feature Matrix

## Stealth Features

| Feature | Status | Detail |
|---------|--------|--------|
| Indirect syscalls | Done | SSN from clean ntdll on disk, `syscall;ret` gadget in ntdll .text |
| Syscall return address in ntdll | Done | Call stack shows ntdll, not our module |
| PEB module walk (no Toolhelp32) | Done | NtQueryInformationProcess → PEB → Ldr → InLoadOrderModuleList |
| Remote export table parsing | Done | IMAGE_EXPORT_DIRECTORY via NtReadVirtualMemory |
| Forwarded export + API Set resolution | Done | Follows `DllName.Func` chains, resolves `api-ms-win-*` / `ext-ms-*` |
| No executor self-injection | Done | Env vars applied via PIC shellcode stub |
| PE headers erased | Done | First SizeOfHeaders bytes zeroed after inverted table registration |
| All temp buffers zeroed + freed | Done | Code pages, context pages, env strings — all wiped |
| Never PAGE_EXECUTE_READWRITE | Done | RW → write → per-section RX/RO/RW |
| No PEB module registration | Done | Mapped DLLs invisible to Toolhelp32 / EnumProcessModules |
| Recursive DLLs also invisible | Done | Verified: no new modules in PEB after injection |
| Thread creation via NtCreateThreadEx | Done | Indirect syscall, not CreateRemoteThread |
| Duplicate DLL detection | Done | Already-loaded DLLs reused from PEB, not re-mapped |

## C++ Feature Support

| Feature | Status | Notes |
|---------|--------|-------|
| DllMain / _DllMainCRTStartup | Pass | AddressOfEntryPoint called via loader stub |
| Static constructors (`_initterm`) | Pass | CRT walks .CRT$XCA-XCZ table |
| Security cookie (`__security_init_cookie`) | Pass | Randomized by CRT, verified != default |
| Heap allocation (CRT malloc/new) | Pass | std::vector with 1000 elements |
| std::mutex | Pass | SRWLock-based, no loader dependency |
| std::function / lambdas | Pass | Capturing lambda tested |
| std::atomic | Pass | fetch_add / fetch_sub / load |
| Cross-module imports | Pass | DepModule→SampleModule chain verified |
| SEH (__try / __except) | Pass | Null-deref caught via __C_specific_handler |
| RTTI dynamic_cast | Pass | Base*→Derived* succeeds (self-relative RTTI) |
| RTTI typeid | Pass | typeid comparison matches |
| Catching CRT-thrown exceptions | Pass* | std::vector::at out_of_range caught (TestApp only) |
| C++ throw from mapped module | Limitation | `_CxxThrowException` calls `RtlPcToFileHeader` which walks PEB LDR list — mapped modules not registered. Use SEH for error handling in mapped DLLs. |

*CRT-thrown exceptions work when the CRT is already loaded in the target (TestApp). When CRT is recursively mapped (Notepad), the catch side can't resolve because both throw and catch modules lack PEB LDR registration.

## Stealth Verification Results

```
TestApp:
  Injection status: 0
  DLLs executed: True (output='stealth-test:42')
  STEALTH: No injected DLLs visible in module list
  No new modules appeared in PEB

Notepad:
  Injection status: 0
  DLLs executed: True (output='stealth-test:42')
  STEALTH: No injected DLLs visible in module list
  No new modules appeared in PEB
```

## Known Limitations (Stealth Trade-offs)

| Limitation | Impact | Rationale |
|-----------|--------|-----------|
| C++ throw/catch from mapped code | Use SEH instead | Fixing requires PEB LDR registration which breaks stealth |
| Implicit TLS (thread_local) | Use TlsAlloc/TlsGetValue | Implicit TLS needs LdrpHandleTlsData (undocumented, breaks stealth) |
| DLL_THREAD_ATTACH/DETACH | Not delivered | Module not in PEB → loader skips thread notifications |
| GetModuleHandle(self) | Returns NULL | Module not in PEB. Use `__ImageBase` instead |
| FindResource/LoadResource | Fails | PE headers erased. Use custom .rsrc parser if needed |

## Test Matrix

| Target | Config | Injection | Env Apply | Cross-Module | Stealth | xUnit |
|--------|--------|-----------|-----------|-------------|---------|-------|
| TestApp.exe | Debug | Pass | Pass | Pass (DepModule→SampleModule) | Pass | 3/3 |
| TestApp.exe | Release | Pass | Pass | Pass | Pass | — |
| notepad.exe | Release | Pass | Pass | Pass | Pass | — |
| testhost.exe (self) | Debug | Pass | Pass | Pass | — | 3/3 |

## File Layout

```
native/NativeExecutor/
  executor.cpp                 — Integration (syscall init → PEB walk → env apply → mapping loop)
  mm_executor.h                — Public ABI (unchanged)
  syscalls.h / syscalls.cpp    — Indirect syscall layer (SSN extraction + C++ wrappers)
  syscall_stub.asm             — x64 MASM indirect syscall trampoline
  peb_walk.h / peb_walk.cpp    — PEB traversal + remote export parsing + API Set resolution
  manual_map.h / manual_map.cpp — PE manual mapper (sections, relocs, imports, TLS, protections)
  loader_stub.h / loader_stub.cpp — PIC loader stubs (env apply, DllMain invoke)

native/DepModule/              — Test dependency DLL (DepModuleGetValue()=42)
native/CppFeatureTest/         — C++ feature test DLL (15 tests)
native/SampleModule/           — Test DLL importing from DepModule
native/TestApp/                — Test target process

scripts/
  build-native.ps1             — Builds all native projects (Debug/Release)
  test-cpp-features.ps1        — C++ feature tests on TestApp + Notepad
  test-stealth-verify.ps1      — Stealth verification (module list check)
  test-multi-target.ps1        — Multi-process injection test
```
