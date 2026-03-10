# Mod Service Plan

## Overview

Build a headless `.NET 10` Windows Service that:

- watches for target process starts
- resolves config-driven rules
- syncs DLLs from GitHub releases by tag
- passes an ordered list of absolute DLL paths plus resolved environment variables and executor option strings to a private native executor DLL that can itself be sourced from GitHub and loaded from the local cache

The managed service owns configuration, matching, GitHub sync, local DLL storage, cleanup, and live reconfiguration.

The native DLL is a simple synchronous executor:
- no internal persistent state
- no worker threads
- no callbacks
- no version negotiation
- all work happens on the caller thread
- all state lives in the request and native stack

## Goals

- Run as a Windows Service
- Target `.NET 10`
- Keep the native executor as a private DLL with a fixed simple ABI
- Match targets from config only
- Support multiple DLL sources per rule
- Auto-update DLLs by tracking GitHub releases by tag
- Allow the native executor DLL itself to be resolved from a GitHub source
- Support public and private GitHub repos
- Keep GitHub config minimal
- Use immutable DLL revision paths so locked old DLLs do not block new versions
- Remove stale DLLs only when all stale DLLs are unlocked

## Main Components

### `ModService.Host`
- `.NET 10` Worker Service
- Uses `AddWindowsService`
- Integrates with SCM recovery and Event Log
- Hosts all background services

### `ConfigManager`
- Loads JSON config
- Validates config
- Watches config with `reloadOnChange`
- Debounces rapid changes
- Keeps last-known-good config if new config is invalid

### `ProcessMonitor`
- Watches process start/stop events
- Produces process snapshots for rule evaluation
- Uses `ManagementEventWatcher` plus periodic reconciliation

### `RuleEngine`
- Evaluates `excludes` first
- Evaluates `rules` in file order
- First matching rule wins
- Expands bindings into a final ordered DLL path list

### `GitHubSyncService`
- Polls GitHub releases by tag
- Detects new or changed `.dll` assets
- Downloads new DLL revisions
- Updates source manifests to point to current revisions
- Applies to both rule-resolved module DLLs and the native executor DLL asset

### `SessionCoordinator`
- Resolves one rule for a process
- Snapshots the resolved DLL list and selected environment variables
- Calls the currently loaded native executor DLL
- Tracks active sessions until process exit

### `NativeExecutor`
- Private native DLL
- Loaded dynamically from the local cache
- Can be sourced from the same GitHub source system as module DLLs
- Only one executor revision is active at a time
- Exposes one simple synchronous ABI call
- Does not own matching, config, GitHub, caching, or cleanup

## Matching Rules

### Evaluation order
1. Evaluate `excludes`
2. Evaluate `rules` in config order
3. First matching rule wins

### Match types
- `process`
  - Case-insensitive glob against process basename
  - Supports `*` and `?`
- `path`
  - Case-insensitive substring match against normalized full executable path
- `env`
  - Optional second-stage match
  - Evaluated only after `process` and/or `path` match
  - Operators:
    - `exists`
    - `equals`

### Matching behavior
- Environment variable names are case-insensitive on Windows
- Environment variable values are case-sensitive by default
- Config order is implicit priority

## GitHub Source Model

### Source definition
Each source contains only:
- `id`
- `repo` in `owner/repo` form
- `tag`

### Source behavior
- A source follows a tag, not a pinned release id
- Poll `GET /repos/{owner}/{repo}/releases/tags/{tag}`
- Detect changes by:
  - asset id
  - asset name
  - `updated_at`
  - size
- Pull newly uploaded or replaced `.dll` assets automatically

### Authentication
- Public repos require no auth
- Private repos use one service-level GitHub credential
- Credential is stored outside config using DPAPI

### Executor resolution
- Add a top-level `executor` selector in config
- It resolves exactly one DLL asset from one source
- The executor asset uses the same source sync and immutable revision storage model as module DLLs
- The service loads the current executor revision from cache and switches to a newer revision when safe

### Scope boundary
- GitHub sources can provide both rule-resolved module DLLs and the native executor DLL
- The service still owns all matching, sync, orchestration, cleanup, and process observation
- The executor remains a private implementation detail with a fixed ABI expected by the service

### Download behavior
- For private assets, use the GitHub release asset API
- Send:
  - `Authorization`
  - `X-GitHub-Api-Version`
  - `Accept: application/octet-stream`
- Follow redirects
- Do not rely on `browser_download_url` for private assets

## DLL Storage Model

### Revision paths
Store each DLL revision in an immutable content-based path, for example:

`%ProgramData%\ModService\cache\sources\<sourceId>\<assetName>\<sha256>\<assetName>`

### Rules
- Never overwrite an existing DLL file path
- New content always gets a new revision path
- Each source manifest marks one revision per asset as `current`
- Older revisions become `stale`

### Why this is required
Target processes may keep old DLL files locked. Immutable revision paths allow new sessions to use new DLLs immediately without waiting for old locked files to be released.

The same rule applies to the executor DLL. When the executor updates, the service loads the new revision from a new path instead of overwriting the old one.

## Cleanup Strategy

Cleanup is global and all-or-nothing.

### Rule
1. Collect all stale DLL files across all sources
2. If there are no stale files, do nothing
3. If any stale file is locked, do not touch any stale files
4. If none of the stale files are locked, delete all stale files and prune empty directories

### Cleanup triggers
- Service startup
- After GitHub sync
- After process exit
- Periodic background sweep

### Result
- Logical state remains latest-only
- Old revisions stay only while locks exist
- Once all stale files are unlocked, the next cleanup removes all of them together

### Executor-specific note
- A stale executor revision counts the same as any other stale DLL
- If the service still has an old executor revision loaded, that file is locked and blocks cleanup
- The service must unload the old executor before cleanup can remove it

## Multiple Source Inclusion

### Rule bindings
Each rule contains a `bindings` array.

Each binding:
- references one source
- may define `include` globs
- may define `exclude` globs

If a binding has no filters, it contributes all `.dll` assets from that source.

### Merge semantics
- Expand bindings in config order
- Sort asset names within each binding
- Deduplicate exact duplicate source-assets from the same source
- Same DLL basename from different sources is allowed
- Final list is based on full absolute paths, not basenames

### Important implication
Two different sources may both contribute `foo.dll` without conflict, because each DLL has a distinct absolute path.

## Config Example

```json
{
  "executor": {
    "source": "core",
    "asset": "injector.dll",
    "options": [
      { "name": "mode", "value": "safe-smoke" }
    ]
  },
  "sources": [
    { "id": "core", "repo": "owner/core-mods", "tag": "stable" },
    { "id": "extras", "repo": "owner/extra-mods", "tag": "stable" }
  ],
  "excludes": [
    { "process": "launcher*.exe" }
  ],
  "rules": [
    {
      "process": "game*.exe",
      "path": "\\Games\\Game\\",
      "env": [
        { "name": "MOD_PROFILE", "op": "equals", "value": "main" }
      ],
      "executorOptions": [
        { "name": "profile", "value": "main" }
      ],
      "bindings": [
        { "source": "core" },
        {
          "source": "extras",
          "include": ["ui*.dll", "addon-*.dll"],
          "exclude": ["*-test.dll"]
        }
      ]
    }
  ]
}
```

## Native ABI

The native DLL is a simple executor. The service expects one fixed ABI and there is no version negotiation.

If the executor DLL is updated from GitHub, the new asset must still implement the same ABI expected by the service.

### ABI properties
- synchronous
- stateless
- no handles
- no callbacks
- no background threads
- loaded dynamically from a cached file path
- no retained pointers after return
- all work happens on the caller thread

### C ABI

```c
#include <stdint.h>

#if defined(_WIN32)
  #define MM_CALL __cdecl
  #define MM_API __declspec(dllexport)
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

MM_API mm_status MM_CALL mm_execute(
    const mm_execute_request* request,
    uint16_t* error_buffer,
    uint32_t error_buffer_capacity,
    uint32_t* error_buffer_written);
```

### ABI rules
- `modules` contains an ordered list of absolute DLL paths
- `env` contains a resolved list of environment variable name/value pairs selected by the service
- `options` contains a resolved list of executor option name/value pairs selected by the service
- Native code must not retain pointers after `mm_execute` returns
- `error_buffer` is optional
- All validation failures return `MM_INVALID_ARGUMENT`
- If PID identity no longer matches `pid + process_create_time`, return `MM_TARGET_CHANGED`

## Managed Interop Notes

- Load the executor DLL dynamically with `NativeLibrary.Load` from the current cached executor path
- Bind `mm_execute` from the loaded DLL with `NativeLibrary.GetExport` and a managed function pointer or delegate
- Build unmanaged arrays for module paths, environment variables, and executor options for each call
- Free unmanaged memory immediately after return
- When a new executor revision becomes current, swap to it only between calls, then unload the old executor
- Log `mm_status` and optional error text in managed code

## Hot Reload Behavior

### Config reload
- Config is watched with `reloadOnChange`
- Changes are validated before activation
- Invalid config is rejected
- Service continues using the last-known-good config

### Session behavior
- Existing sessions continue using the DLL list resolved when they started
- New sessions use the latest valid config and latest current source revisions
- If the executor revision changes, the service switches executors only between native calls

## Runtime Flow

1. Service starts
2. Config loads and validates
3. Source manifests restore from disk
4. The current executor DLL is resolved from cache and loaded
5. Cleanup runs
6. GitHub polling begins
7. Process monitoring begins
8. A process starts
9. `RuleEngine` resolves one rule
10. Rule bindings expand into one deterministic ordered DLL path list and the service resolves the environment variable list
11. `SessionCoordinator` calls `mm_execute(...)`
12. Process exits
13. Session state is released
14. Cleanup later removes stale DLLs only when none are locked

## Implementation Order

1. Service host, logging, config schema, validation, hot reload
2. Rule engine and process monitoring
3. GitHub sync by tag, DPAPI credential store, source manifests
4. Immutable revision storage and global lock-aware cleanup
5. Dynamic executor loading, managed/native interop wrapper, and session coordinator
6. Admin control surface for:
   - `status`
   - `reload`
   - `validate-config`
   - credential setup

## Non-Goals for V1

- Tray UI
- Historical DLL version retention
- Per-source token config in JSON
- Partial cleanup when some stale DLLs are unlocked
- Native-side process matching
- Native-side logging callbacks
- Native-side retained state or background workers
