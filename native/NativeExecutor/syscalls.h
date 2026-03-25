#pragma once

#include <windows.h>
#include <stdint.h>

#include <string>

// ── NT type definitions ────────────────────────────────────────────────────────

typedef LONG NTSTATUS;
#define NT_SUCCESS(s) (((NTSTATUS)(s)) >= 0)

#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0)
#endif

struct NT_UNICODE_STRING {
    USHORT Length;
    USHORT MaximumLength;
    PWSTR  Buffer;
};

struct NT_OBJECT_ATTRIBUTES {
    ULONG           Length;
    HANDLE          RootDirectory;
    NT_UNICODE_STRING* ObjectName;
    ULONG           Attributes;
    PVOID           SecurityDescriptor;
    PVOID           SecurityQualityOfService;
};

struct NT_CLIENT_ID {
    HANDLE UniqueProcess;
    HANDLE UniqueThread;
};

// Minimal PEB layout (x64). Only the fields we need for Ldr access.
struct NT_PEB {
    uint8_t  Reserved1[2];         // +0x000
    uint8_t  BeingDebugged;        // +0x002
    uint8_t  Reserved2[1];         // +0x003
    uint8_t  Padding1[4];          // +0x004
    void*    Reserved3[2];         // +0x008  (Mutant, ImageBaseAddress)
    void*    Ldr;                  // +0x018  → NT_PEB_LDR_DATA*
};
static_assert(offsetof(NT_PEB, Ldr) == 0x18);

struct NT_PEB_LDR_DATA {
    uint32_t   Length;              // +0x000
    uint8_t    Initialized;         // +0x004
    uint8_t    Pad1[3];             // +0x005
    void*      SsHandle;            // +0x008
    LIST_ENTRY InLoadOrderModuleList; // +0x010
};
static_assert(offsetof(NT_PEB_LDR_DATA, InLoadOrderModuleList) == 0x10);

// Remote-safe LDR entry — uses fixed-width types for cross-process reading.
#pragma pack(push, 1)
struct NT_LDR_DATA_TABLE_ENTRY_REMOTE {
    // LIST_ENTRY InLoadOrderLinks
    uint64_t InLoadOrder_Flink;     // +0x000
    uint64_t InLoadOrder_Blink;     // +0x008
    // LIST_ENTRY InMemoryOrderLinks
    uint64_t InMemoryOrder_Flink;   // +0x010
    uint64_t InMemoryOrder_Blink;   // +0x018
    // LIST_ENTRY InInitializationOrderLinks
    uint64_t InInitOrder_Flink;     // +0x020
    uint64_t InInitOrder_Blink;     // +0x028
    uint64_t DllBase;               // +0x030
    uint64_t EntryPoint;            // +0x038
    uint32_t SizeOfImage;           // +0x040
    uint32_t _Pad0;                 // +0x044
    // UNICODE_STRING FullDllName
    uint16_t FullDllName_Length;     // +0x048
    uint16_t FullDllName_MaxLen;     // +0x04A
    uint32_t _Pad1;                  // +0x04C
    uint64_t FullDllName_Buffer;     // +0x050
    // UNICODE_STRING BaseDllName
    uint16_t BaseDllName_Length;     // +0x058
    uint16_t BaseDllName_MaxLen;     // +0x05A
    uint32_t _Pad2;                  // +0x05C
    uint64_t BaseDllName_Buffer;     // +0x060
};
#pragma pack(pop)
static_assert(sizeof(NT_LDR_DATA_TABLE_ENTRY_REMOTE) == 0x68);

struct NT_PROCESS_BASIC_INFORMATION {
    NTSTATUS ExitStatus;           // +0x000
    uint32_t _Pad0;                // +0x004
    uint64_t PebBaseAddress;       // +0x008
    uint64_t AffinityMask;         // +0x010
    int32_t  BasePriority;         // +0x018
    uint32_t _Pad1;                // +0x01C
    uint64_t UniqueProcessId;      // +0x020
    uint64_t InheritedFromPid;     // +0x028
};
static_assert(sizeof(NT_PROCESS_BASIC_INFORMATION) == 0x30);

// ── Syscall table ──────────────────────────────────────────────────────────────

enum class SyscallId : uint32_t {
    NtOpenProcess = 0,
    NtClose,
    NtAllocateVirtualMemory,
    NtFreeVirtualMemory,
    NtReadVirtualMemory,
    NtWriteVirtualMemory,
    NtProtectVirtualMemory,
    NtCreateThreadEx,
    NtWaitForSingleObject,
    NtQueryInformationProcess,
    Count
};

// Must be called once before any syscall:: function.
bool syscall_init(std::wstring& error);

// The trampoline and shared state (set per-call by each wrapper).
extern "C" uint32_t  g_syscall_ssn;
extern "C" void*     g_syscall_gadget;
extern "C" NTSTATUS  indirect_syscall_stub();

// ── Syscall wrappers ───────────────────────────────────────────────────────────

namespace syscall {

NTSTATUS NtOpenProcess(PHANDLE ProcessHandle, ACCESS_MASK DesiredAccess,
                       NT_OBJECT_ATTRIBUTES* ObjectAttributes, NT_CLIENT_ID* ClientId);

NTSTATUS NtClose(HANDLE Handle);

NTSTATUS NtAllocateVirtualMemory(HANDLE ProcessHandle, PVOID* BaseAddress,
                                  ULONG_PTR ZeroBits, PSIZE_T RegionSize,
                                  ULONG AllocationType, ULONG Protect);

NTSTATUS NtFreeVirtualMemory(HANDLE ProcessHandle, PVOID* BaseAddress,
                              PSIZE_T RegionSize, ULONG FreeType);

NTSTATUS NtReadVirtualMemory(HANDLE ProcessHandle, PVOID BaseAddress,
                              PVOID Buffer, SIZE_T BufferSize, PSIZE_T BytesRead);

NTSTATUS NtWriteVirtualMemory(HANDLE ProcessHandle, PVOID BaseAddress,
                               const void* Buffer, SIZE_T BufferSize, PSIZE_T BytesWritten);

NTSTATUS NtProtectVirtualMemory(HANDLE ProcessHandle, PVOID* BaseAddress,
                                 PSIZE_T RegionSize, ULONG NewProtect, PULONG OldProtect);

NTSTATUS NtCreateThreadEx(PHANDLE ThreadHandle, ACCESS_MASK DesiredAccess,
                           PVOID ObjectAttributes, HANDLE ProcessHandle,
                           PVOID StartRoutine, PVOID Argument,
                           ULONG CreateFlags, SIZE_T ZeroBits,
                           SIZE_T StackSize, SIZE_T MaximumStackSize,
                           PVOID AttributeList);

NTSTATUS NtWaitForSingleObject(HANDLE Handle, BOOLEAN Alertable, PLARGE_INTEGER Timeout);

NTSTATUS NtQueryInformationProcess(HANDLE ProcessHandle, ULONG InfoClass,
                                    PVOID Info, ULONG InfoLength, PULONG ReturnLength);

}  // namespace syscall

// ── Utility ────────────────────────────────────────────────────────────────────

struct ScopedNtHandle {
    HANDLE value = nullptr;

    ScopedNtHandle() = default;
    explicit ScopedNtHandle(HANDLE h) : value(h) {}
    ~ScopedNtHandle() { reset(); }

    ScopedNtHandle(const ScopedNtHandle&) = delete;
    ScopedNtHandle& operator=(const ScopedNtHandle&) = delete;
    ScopedNtHandle(ScopedNtHandle&& o) noexcept : value(o.value) { o.value = nullptr; }
    ScopedNtHandle& operator=(ScopedNtHandle&& o) noexcept {
        if (this != &o) { reset(); value = o.value; o.value = nullptr; }
        return *this;
    }

    void reset() {
        if (value && value != INVALID_HANDLE_VALUE) { syscall::NtClose(value); value = nullptr; }
    }

    HANDLE get() const { return value; }
    explicit operator bool() const { return value && value != INVALID_HANDLE_VALUE; }
};
