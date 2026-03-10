#pragma once

#include <stdint.h>

#if defined(_WIN32)
  #define MM_CALL __cdecl
  #if defined(NATIVEEXECUTOR_EXPORTS)
    #define MM_API __declspec(dllexport)
  #else
    #define MM_API __declspec(dllimport)
  #endif
#else
  #define MM_CALL
  #define MM_API
#endif

typedef enum mm_status {
    MM_OK = 0,
    MM_INVALID_ARGUMENT = 1,
    MM_TARGET_NOT_FOUND = 2,
    MM_TARGET_CHANGED = 3,
    MM_TIMEOUT = 4,
    MM_EXECUTION_FAILED = 5
} mm_status;

typedef struct mm_u16_view {
    const uint16_t* ptr;
    uint32_t len;
} mm_u16_view;

typedef struct mm_env_var {
    mm_u16_view name;
    mm_u16_view value;
} mm_env_var;

typedef struct mm_option {
    mm_u16_view name;
    mm_u16_view value;
} mm_option;

typedef struct mm_execute_request {
    uint32_t pid;
    uint64_t process_create_time_utc_100ns;
    mm_u16_view exe_path;
    const mm_u16_view* modules;
    uint32_t module_count;
    const mm_env_var* env;
    uint32_t env_count;
    const mm_option* options;
    uint32_t option_count;
    uint32_t timeout_ms;
} mm_execute_request;

extern "C" MM_API mm_status MM_CALL mm_execute(
    const mm_execute_request* request,
    uint16_t* error_buffer,
    uint32_t error_buffer_capacity,
    uint32_t* error_buffer_written);
