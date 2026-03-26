#include "sysinfo_parser.h"
#include "win_offsets.h"
#include "syscalls.h"

#include <vector>

std::optional<SystemProcessInfo> query_process_threads(uint32_t target_pid) {
    const auto& off = win_offsets();

    ULONG buf_size = 2 * 1024 * 1024;
    std::vector<uint8_t> buf(buf_size);
    ULONG ret_len = 0;
    auto st = syscall::NtQuerySystemInformation(5 /*SystemProcessInformation*/,
                                                 buf.data(), buf_size, &ret_len);
    if (!NT_SUCCESS(st)) return std::nullopt;

    auto* proc = buf.data();
    while (true) {
        auto pid = *reinterpret_cast<const uint64_t*>(proc + off.spi_unique_process_id);
        if (static_cast<uint32_t>(pid) == target_pid) {
            SystemProcessInfo result;
            result.unique_process_id = pid;
            result.thread_count = *reinterpret_cast<const uint32_t*>(proc + off.spi_number_of_threads);

            auto* thread_base = proc + off.spi_threads_offset;
            result.threads.reserve(result.thread_count);
            for (uint32_t t = 0; t < result.thread_count; ++t) {
                auto* ti = thread_base + t * off.sti_stride;
                SystemThreadInfo tinfo;
                tinfo.wait_time        = *reinterpret_cast<const uint32_t*>(ti + off.sti_wait_time);
                tinfo.unique_thread_id = *reinterpret_cast<const uint64_t*>(ti + off.sti_client_id_thread);
                tinfo.thread_state     = *reinterpret_cast<const uint32_t*>(ti + off.sti_thread_state);
                tinfo.wait_reason      = *reinterpret_cast<const uint32_t*>(ti + off.sti_wait_reason);
                result.threads.push_back(tinfo);
            }
            return result;
        }

        auto next = *reinterpret_cast<const uint32_t*>(proc);
        if (next == 0) break;
        proc += next;
    }
    return std::nullopt;
}
