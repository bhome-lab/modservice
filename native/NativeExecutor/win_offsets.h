#pragma once

#include <windows.h>
#include <stdint.h>
#include <string>

// All OS-version-dependent offsets, discovered once at init time.
// After win_offsets_init() succeeds, all fields are immutable.
struct WinOffsets {
    // ── SYSTEM_PROCESS_INFORMATION ──
    uint32_t spi_number_of_threads;      // Offset of NumberOfThreads
    uint32_t spi_unique_process_id;      // Offset of UniqueProcessId
    uint32_t spi_threads_offset;         // Byte offset from entry start to first SYSTEM_THREAD_INFORMATION

    // ── SYSTEM_THREAD_INFORMATION ──
    uint32_t sti_stride;                 // sizeof one thread entry
    uint32_t sti_wait_time;              // Offset of WaitTime
    uint32_t sti_client_id_thread;       // Offset of ClientId.UniqueThread
    uint32_t sti_thread_state;           // Offset of ThreadState
    uint32_t sti_wait_reason;            // Offset of WaitReason

    // ── PEB ──
    uint32_t peb_api_set_map;            // Offset of ApiSetMap in PEB
    uint32_t peb_loader_lock;            // Offset of LoaderLock in PEB

    // ── RTL_CRITICAL_SECTION ──
    uint32_t cs_owning_thread;           // Offset of OwningThread

    // ── API Set ──
    uint32_t api_set_version;            // Discovered schema version
    bool     api_set_available;          // Whether API Set schema v6 was found and usable

    // ── SSN ──
    bool     ssn_pattern_validated;      // Whether the ntdll syscall stub pattern was recognized

    bool     initialized;
};

// Initialize all offsets by probing the running OS. Call once (from syscall_init).
// Returns false with error message on fatal failure.
bool win_offsets_init(std::wstring& error);

// Access the global offsets (valid only after successful init).
const WinOffsets& win_offsets();
