#include "win_offsets.h"
#include "syscalls.h"

#include <intrin.h>
#include <cstring>
#include <vector>

// ── Global singleton ────────────────────────────────────────────────────────────

static WinOffsets g_offsets{};

const WinOffsets& win_offsets() { return g_offsets; }

namespace {

// ── RTL_CRITICAL_SECTION offset (SDK-defined, compile-time) ─────────────────

uint32_t discover_cs_owning_thread() {
    // RTL_CRITICAL_SECTION is a documented public struct.
    // Use offsetof on the SDK typedef to get the real value.
    // DebugInfo(8) + LockCount(4) + RecursionCount(4) = OwningThread at 0x10.
    static_assert(offsetof(RTL_CRITICAL_SECTION, OwningThread) == 0x10,
                  "RTL_CRITICAL_SECTION layout changed — update win_offsets");
    return static_cast<uint32_t>(offsetof(RTL_CRITICAL_SECTION, OwningThread));
}

// ── SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION discovery ────────

bool discover_spi_offsets(WinOffsets& off, std::wstring& error) {
    // Expected x64 Win10+ layout. We validate against our own process.
    constexpr uint32_t kSpiNumberOfThreads = 0x04;
    constexpr uint32_t kSpiUniqueProcessId = 0x50;
    constexpr uint32_t kSpiThreadsOffset   = 0x100;
    constexpr uint32_t kStiStride          = 0x50;
    constexpr uint32_t kStiWaitTime        = 0x18;
    constexpr uint32_t kStiClientIdThread  = 0x30;
    constexpr uint32_t kStiClientIdProcess = 0x28;
    constexpr uint32_t kStiThreadState     = 0x44;
    constexpr uint32_t kStiWaitReason      = 0x48;

    const uint32_t our_pid = GetCurrentProcessId();

    // Query SystemProcessInformation.
    ULONG buf_size = 2 * 1024 * 1024;
    std::vector<uint8_t> buf(buf_size);
    ULONG ret_len = 0;
    auto st = syscall::NtQuerySystemInformation(5 /*SystemProcessInformation*/,
                                                 buf.data(), buf_size, &ret_len);
    if (!NT_SUCCESS(st)) {
        error = L"NtQuerySystemInformation failed during offset discovery.";
        return false;
    }

    // Walk entries and find our own process.
    auto* proc = buf.data();
    bool found = false;
    while (true) {
        auto candidate_pid = *reinterpret_cast<const uint64_t*>(proc + kSpiUniqueProcessId);
        if (static_cast<uint32_t>(candidate_pid) == our_pid) {
            found = true;

            // Validate thread count is reasonable.
            auto thread_count = *reinterpret_cast<const uint32_t*>(proc + kSpiNumberOfThreads);
            if (thread_count == 0 || thread_count > 10000) {
                error = L"SPI offset validation failed: thread count " +
                        std::to_wstring(thread_count) + L" at offset 0x04 is unreasonable.";
                return false;
            }

            // Validate first thread's ClientId.UniqueProcess matches our PID.
            auto* thread_base = proc + kSpiThreadsOffset;
            auto thread_owner_pid = *reinterpret_cast<const uint64_t*>(thread_base + kStiClientIdProcess);
            if (static_cast<uint32_t>(thread_owner_pid) != our_pid) {
                error = L"SPI offset validation failed: first thread ClientId.UniqueProcess (" +
                        std::to_wstring(thread_owner_pid) + L") != our PID (" +
                        std::to_wstring(our_pid) + L").";
                return false;
            }

            // Validate first thread's UniqueThread is nonzero.
            auto first_tid = *reinterpret_cast<const uint64_t*>(thread_base + kStiClientIdThread);
            if (first_tid == 0) {
                error = L"SPI offset validation failed: first thread UniqueThread is zero.";
                return false;
            }

            // Validate ThreadState is in range [0,8] for the first thread.
            auto first_state = *reinterpret_cast<const uint32_t*>(thread_base + kStiThreadState);
            if (first_state > 8) {
                error = L"SPI offset validation failed: first thread state " +
                        std::to_wstring(first_state) + L" is out of range.";
                return false;
            }

            break;
        }

        auto next = *reinterpret_cast<const uint32_t*>(proc);
        if (next == 0) break;
        proc += next;
    }

    if (!found) {
        error = L"SPI offset validation failed: could not find our own process (PID " +
                std::to_wstring(our_pid) + L") in SystemProcessInformation.";
        return false;
    }

    // All validations passed — commit the offsets.
    off.spi_number_of_threads = kSpiNumberOfThreads;
    off.spi_unique_process_id = kSpiUniqueProcessId;
    off.spi_threads_offset    = kSpiThreadsOffset;
    off.sti_stride            = kStiStride;
    off.sti_wait_time         = kStiWaitTime;
    off.sti_client_id_thread  = kStiClientIdThread;
    off.sti_thread_state      = kStiThreadState;
    off.sti_wait_reason       = kStiWaitReason;
    return true;
}

// ── PEB offset discovery ────────────────────────────────────────────────────

bool discover_peb_offsets(WinOffsets& off, std::wstring& error) {
    constexpr uint32_t kPebApiSetMap   = 0x68;
    constexpr uint32_t kPebLoaderLock  = 0x110;

    auto* peb = reinterpret_cast<const uint8_t*>(
        reinterpret_cast<void*>(__readgsqword(0x60)));

    // Validate ApiSetMap: pointer at PEB+0x68 should point to a valid API Set namespace.
    auto* api_set_ptr = *reinterpret_cast<const void* const*>(peb + kPebApiSetMap);
    if (!api_set_ptr) {
        error = L"PEB offset validation failed: ApiSetMap at PEB+0x68 is null.";
        return false;
    }

    // Read version field (first DWORD of the ApiSetNamespace).
    auto api_version = *reinterpret_cast<const uint32_t*>(api_set_ptr);
    if (api_version < 2 || api_version > 8) {
        error = L"PEB offset validation failed: ApiSetMap version " +
                std::to_wstring(api_version) + L" is not in expected range [2,8].";
        return false;
    }

    off.peb_api_set_map  = kPebApiSetMap;
    off.api_set_version  = api_version;
    off.api_set_available = (api_version == 6);

    // Validate LoaderLock: pointer at PEB+0x110 should be a valid CRITICAL_SECTION.
    auto* loader_lock = *reinterpret_cast<const RTL_CRITICAL_SECTION* const*>(peb + kPebLoaderLock);
    if (!loader_lock) {
        error = L"PEB offset validation failed: LoaderLock at PEB+0x110 is null.";
        return false;
    }

    // A valid CRITICAL_SECTION has LockCount initialized (typically -1 when unlocked).
    // Just check the pointer is readable by verifying the struct has plausible values.
    // LockSemaphore at offset 0x10 should be a valid handle or null.
    // RecursionCount at offset 0x0C should be 0 or small positive.
    auto recursion = loader_lock->RecursionCount;
    if (recursion < 0 || recursion > 1000) {
        error = L"PEB offset validation failed: LoaderLock RecursionCount " +
                std::to_wstring(recursion) + L" is unreasonable.";
        return false;
    }

    off.peb_loader_lock = kPebLoaderLock;
    return true;
}

// ── Stack spoof return address discovery ─────────────────────────────────────
// Scans the first N bytes of a function to find the first CALL instruction.
// Returns the address immediately after the CALL (the return address a callee
// would see on the stack).

// Compute the length of a ModRM-addressed operand (excluding the opcode/prefix bytes).
// Returns the number of extra bytes after the ModRM byte (SIB + displacement).
uint32_t modrm_operand_length(uint8_t modrm) {
    uint8_t mod = modrm >> 6;
    uint8_t rm  = modrm & 0x07;
    uint32_t extra = 0;
    if (mod == 3) return 0;            // register-direct, no memory operand
    if (rm == 4) extra++;              // SIB byte present
    if (mod == 1) extra += 1;          // disp8
    else if (mod == 2) extra += 4;     // disp32
    else if (mod == 0 && rm == 5) extra += 4; // RIP-relative disp32
    return extra;
}

void* find_call_return_addr(void* func_start, size_t max_scan) {
    auto* p = static_cast<const uint8_t*>(func_start);
    const auto* end = p + max_scan;

    while (p < end) {
        // ── CALL instructions (our target) ──────────────────────────────
        // Check for CALL before consuming REX prefixes so REX+E8 doesn't
        // get swallowed as a generic REX instruction.

        // Peek past any REX prefix to find the actual opcode.
        const uint8_t* op_ptr = p;
        bool has_rex = false;
        if ((op_ptr[0] & 0xF0) == 0x40 && op_ptr + 1 < end) {
            // Could be REX prefix — but only if next byte is a valid opcode, not PUSH r64.
            // REX 0x40-0x4F followed by 0x50-0x5F = PUSH/POP r64 (valid 2-byte).
            // REX followed by E8/FF etc = prefixed CALL.
            uint8_t next = op_ptr[1];
            if (next >= 0x50 && next <= 0x5F) {
                // This is PUSH/POP r64 with REX — handle as 2-byte below.
            } else {
                has_rex = true;
                op_ptr = p + 1;
            }
        }

        // E8 rel32 — CALL rel32 (5 bytes, or 6 with REX)
        if (op_ptr[0] == 0xE8) {
            auto* ret = op_ptr + 5;
            if (ret <= end) return const_cast<uint8_t*>(ret);
        }

        // FF /2 — CALL r/m64
        if (op_ptr[0] == 0xFF && op_ptr + 2 <= end) {
            uint8_t modrm = op_ptr[1];
            uint8_t reg_field = (modrm >> 3) & 0x07;
            if (reg_field == 2) { // /2 = CALL
                uint32_t len = 2 + modrm_operand_length(modrm);
                auto* ret = op_ptr + len;
                if (ret <= end) return const_cast<uint8_t*>(ret);
            }
        }

        // ── Skip known instruction patterns ─────────────────────────────

        // REX-prefixed instructions (40-4F prefix byte)
        if ((p[0] & 0xF0) == 0x40 && p + 2 <= end) {
            uint8_t op = p[1];

            // REX + PUSH/POP r64 (50-5F) — 2 bytes total
            if (op >= 0x50 && op <= 0x5F) { p += 2; continue; }

            // REX + 83 ModRM imm8 (e.g., sub rsp, imm8) — 4 bytes
            if (op == 0x83 && p + 4 <= end) { p += 4; continue; }
            // REX + 81 ModRM imm32 — 7 bytes
            if (op == 0x81 && p + 7 <= end) { p += 7; continue; }

            // REX + op + ModRM (89/8B=MOV, 85=TEST, 8D=LEA, 3B=CMP, 33=XOR, 23=AND, 0B=OR, 2B=SUB, 03=ADD)
            if (p + 3 <= end) {
                uint8_t modrm = p[2];
                uint32_t extra = modrm_operand_length(modrm);
                uint32_t len = 3 + extra;
                if (p + len <= end) { p += len; continue; }
            }
            break; // Can't decode
        }

        // 85 xx — TEST r/m32, r32 (2+ bytes)
        if (p[0] == 0x85 && p + 2 <= end) {
            p += 2 + modrm_operand_length(p[1]); continue;
        }

        // 33 xx — XOR r32, r/m32 (2+ bytes)
        if (p[0] == 0x33 && p + 2 <= end) {
            p += 2 + modrm_operand_length(p[1]); continue;
        }

        // 89 xx — MOV r/m32, r32 (2+ bytes)
        if (p[0] == 0x89 && p + 2 <= end) {
            p += 2 + modrm_operand_length(p[1]); continue;
        }

        // 8B xx — MOV r32, r/m32 (2+ bytes)
        if (p[0] == 0x8B && p + 2 <= end) {
            p += 2 + modrm_operand_length(p[1]); continue;
        }

        // 3B xx — CMP r32, r/m32 (2+ bytes)
        if (p[0] == 0x3B && p + 2 <= end) {
            p += 2 + modrm_operand_length(p[1]); continue;
        }

        // 50-57 — PUSH r32/r64 (1 byte)
        if (p[0] >= 0x50 && p[0] <= 0x57) { p += 1; continue; }

        // 58-5F — POP r32/r64 (1 byte)
        if (p[0] >= 0x58 && p[0] <= 0x5F) { p += 1; continue; }

        // 70-7F xx — Jcc rel8 (2 bytes)
        if (p[0] >= 0x70 && p[0] <= 0x7F && p + 2 <= end) { p += 2; continue; }

        // EB xx — JMP rel8 (2 bytes)
        if (p[0] == 0xEB && p + 2 <= end) { p += 2; continue; }

        // 90 — NOP (1 byte)
        if (p[0] == 0x90) { p += 1; continue; }

        // CC — INT3 (1 byte)
        if (p[0] == 0xCC) { p += 1; continue; }

        // 0F 1F xx — multi-byte NOP (3+ bytes)
        if (p[0] == 0x0F && p[1] == 0x1F && p + 3 <= end) {
            p += 3 + modrm_operand_length(p[2]); continue;
        }

        // 0F 84/85 rel32 — JZ/JNZ rel32 (6 bytes)
        if (p[0] == 0x0F && (p[1] == 0x84 || p[1] == 0x85) && p + 6 <= end) { p += 6; continue; }

        // 66 prefix — operand size override, skip and retry
        if (p[0] == 0x66) { p += 1; continue; }

        // Can't decode — give up.
        break;
    }

    return nullptr;
}

bool discover_spoof_addrs(WinOffsets& off, std::wstring& error) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) { error = L"kernel32.dll not loaded."; return false; }

    // Use manual export walk to avoid GetProcAddress monitoring.
    auto* btt = syscall_find_local_export(k32, "BaseThreadInitThunk");
    if (!btt) { error = L"BaseThreadInitThunk not found."; return false; }

    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) { error = L"ntdll.dll not loaded."; return false; }

    auto* ruts = syscall_find_local_export(ntdll, "RtlUserThreadStart");
    if (!ruts) { error = L"RtlUserThreadStart not found."; return false; }

    // Scan for the first CALL instruction in each function.
    off.btt_call_addr = find_call_return_addr(btt, 64);
    if (!off.btt_call_addr) {
        error = L"Failed to find CALL instruction in BaseThreadInitThunk.";
        return false;
    }

    off.ruts_call_addr = find_call_return_addr(ruts, 64);
    if (!off.ruts_call_addr) {
        error = L"Failed to find CALL instruction in RtlUserThreadStart.";
        return false;
    }

    return true;
}

// ── SSN pattern validation ──────────────────────────────────────────────────

bool validate_ssn_pattern(WinOffsets& off, std::wstring& error) {
    // Read a known syscall stub from the in-memory ntdll and check the pattern.
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) { error = L"ntdll.dll not loaded for SSN validation."; return false; }

    auto* stub = static_cast<const uint8_t*>(syscall_find_local_export(ntdll, "NtClose"));
    if (!stub) { error = L"NtClose not found for SSN validation."; return false; }

    // Primary: 4C 8B D1 B8 (mov r10, rcx; mov eax, imm32)
    if (stub[0] == 0x4C && stub[1] == 0x8B && stub[2] == 0xD1 && stub[3] == 0xB8) {
        off.ssn_pattern_validated = true;
        return true;
    }

    // Alternative: 49 89 CA B8 (mov r10, rcx alternate encoding; mov eax, imm32)
    if (stub[0] == 0x49 && stub[1] == 0x89 && stub[2] == 0xCA && stub[3] == 0xB8) {
        off.ssn_pattern_validated = true;
        return true;
    }

    error = L"Unrecognized ntdll syscall stub pattern: " +
            std::to_wstring(stub[0]) + L" " + std::to_wstring(stub[1]) + L" " +
            std::to_wstring(stub[2]) + L" " + std::to_wstring(stub[3]);
    return false;
}

}  // namespace

// ── Public API ──────────────────────────────────────────────────────────────────

bool win_offsets_init(std::wstring& error) {
    if (g_offsets.initialized) return true;

    // RTL_CRITICAL_SECTION — compile-time, always succeeds.
    g_offsets.cs_owning_thread = discover_cs_owning_thread();

    // SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION.
    if (!discover_spi_offsets(g_offsets, error)) return false;

    // PEB offsets (ApiSetMap, LoaderLock).
    if (!discover_peb_offsets(g_offsets, error)) return false;

    // Stack spoof return addresses.
    if (!discover_spoof_addrs(g_offsets, error)) return false;

    // SSN pattern.
    if (!validate_ssn_pattern(g_offsets, error)) return false;

    g_offsets.initialized = true;
    return true;
}
