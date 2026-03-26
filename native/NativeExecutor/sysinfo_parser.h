#pragma once

#include <stdint.h>
#include <optional>
#include <vector>

struct SystemThreadInfo {
    uint64_t unique_thread_id;
    uint32_t wait_time;
    uint32_t thread_state;
    uint32_t wait_reason;
};

struct SystemProcessInfo {
    uint64_t unique_process_id;
    uint32_t thread_count;
    std::vector<SystemThreadInfo> threads;
};

// Query NtQuerySystemInformation(SystemProcessInformation) and parse the entry
// for the given PID using dynamically-discovered offsets from win_offsets().
// Returns nullopt if the process is not found or the query fails.
std::optional<SystemProcessInfo> query_process_threads(uint32_t target_pid);
