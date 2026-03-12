# REST API Design

## Scope

This design covers the two requested features:

1. Graceful handling when GitHub returns an API rate limit response.
2. A local HTTP API for querying sources and DLLs with glob filters.

This is a v1 read-only API. It does not proxy GitHub and it does not expose file download or mutation endpoints.

## Goals

- Keep the service usable when GitHub sync is temporarily rate-limited.
- Serve query results from local manifests and current runtime state.
- Reuse the existing source model (`source.id`, `repo`, `tag`) and current DLL manifest model.
- Keep the HTTP surface small and stable.

## Transport

- Base path: `/api/v1`
- Bind address: configurable
- Default URL: `http://127.0.0.1:5047`
- Response format: `application/json`

## Configuration

Add a new `http` section under `ModService`:

```json
{
  "ModService": {
    "http": {
      "listenUrl": "http://127.0.0.1:5047"
    }
  }
}
```

Notes:

- The HTTP API is always enabled in v1.
- `listenUrl` remains configurable and may be loopback or a non-loopback bind.
- If `listenUrl` is omitted, use `http://127.0.0.1:5047`.

## Glob Semantics

All glob filters follow the existing `GlobPattern` behavior:

- case-insensitive
- `*` matches zero or more characters
- `?` matches exactly one character
- missing or empty glob parameter means `*`
- DLL matching uses the logical asset name from the manifest
- archive entry paths are normalized to `/`

Examples:

- `Sample*.dll`
- `bin/*.dll`
- `**` is not special in v1; it behaves as two `*` characters

## Source Of Truth

- Source queries use the effective configuration plus manifest status.
- DLL queries use current manifest assets only.
- Query endpoints do not call GitHub.
- When the service is rate-limited by GitHub, query endpoints continue serving the last successful local state.

## Endpoints

### `GET /api/v1/status`

Returns overall service state, including configuration status, refresh status, cleanup status, source summaries, and GitHub sync health.

Response shape:

```json
{
  "service": {
    "startedAtUtc": "2026-03-12T08:00:00Z"
  },
  "configuration": {
    "version": 3,
    "hasConfiguration": true,
    "usingLastKnownGoodConfiguration": false,
    "validationErrors": []
  },
  "refresh": {
    "inProgress": false,
    "queuedCount": 0,
    "lastReason": "scheduled",
    "lastStartedAtUtc": "2026-03-12T08:10:00Z",
    "lastCompletedAtUtc": "2026-03-12T08:10:02Z",
    "lastSummary": "Synced 1 source(s), downloaded 0 asset(s), stale files 0, locked stale files 0.",
    "lastError": null
  },
  "github": {
    "state": "ready",
    "rateLimit": null
  },
  "executor": {
    "path": "C:\\ProgramData\\ModService\\cache\\sources\\repo\\direct\\...\\NativeExecutor.dll"
  },
  "cleanup": {
    "staleFileCount": 0,
    "lockedFileCount": 0,
    "deleted": false
  },
  "sources": [
    {
      "sourceId": "repo",
      "repo": "owner/repo",
      "tag": "latest",
      "syncedAtUtc": "2026-03-12T08:10:02Z",
      "assetCount": 2,
      "status": "Ready"
    }
  ]
}
```

`github.state` values:

- `ready`
- `rate_limited`
- `error`

When rate-limited:

```json
{
  "github": {
    "state": "rate_limited",
    "rateLimit": {
      "scope": "core",
      "limit": 60,
      "remaining": 0,
      "resetAtUtc": "2026-03-12T08:22:14Z",
      "retryAfterSeconds": 180,
      "message": "GitHub API rate limit exceeded."
    }
  }
}
```

### `GET /api/v1/sources`

Returns configured sources with manifest status.

Query parameters:

- `glob`: optional, matches `sourceId`, default `*`

Example:

- `/api/v1/sources?glob=repo*`

Response:

```json
{
  "items": [
    {
      "sourceId": "repo",
      "repo": "owner/repo",
      "tag": "latest",
      "syncedAtUtc": "2026-03-12T08:10:02Z",
      "assetCount": 2,
      "status": "Ready"
    }
  ]
}
```

Behavior:

- `200 OK` with an empty `items` array when no sources match the glob.
- Sorted by `sourceId` ascending, case-insensitive.

### `GET /api/v1/sources/{sourceId}/dlls`

Returns current DLLs for a single source.

Query parameters:

- `glob`: optional, matches logical DLL asset name, default `*`

Examples:

- `/api/v1/sources/repo/dlls`
- `/api/v1/sources/repo/dlls?glob=Sample*.dll`
- `/api/v1/sources/repo/dlls?glob=bin/*.dll`

Response:

```json
{
  "sourceId": "repo",
  "items": [
    {
      "assetName": "SampleModule.dll",
      "fullPath": "C:\\ProgramData\\ModService\\cache\\sources\\repo\\direct\\...\\SampleModule.dll",
      "sha256": "6f5d...",
      "size": 12345,
      "updatedAtUtc": "2026-03-12T08:09:59Z"
    }
  ]
}
```

Behavior:

- `404 Not Found` when `sourceId` does not exist in the effective configuration.
- `200 OK` with an empty `items` array when the source exists but no DLLs match the glob.
- Sorted by `assetName` ascending, case-insensitive.

### `GET /api/v1/dlls`

Returns a flattened DLL view across all sources.

Query parameters:

- `sourceGlob`: optional, matches `sourceId`, default `*`
- `glob`: optional, matches logical DLL asset name, default `*`

Examples:

- `/api/v1/dlls`
- `/api/v1/dlls?sourceGlob=repo*&glob=*Module*.dll`

Response:

```json
{
  "items": [
    {
      "sourceId": "repo",
      "assetName": "SampleModule.dll",
      "fullPath": "C:\\ProgramData\\ModService\\cache\\sources\\repo\\direct\\...\\SampleModule.dll",
      "sha256": "6f5d...",
      "size": 12345,
      "updatedAtUtc": "2026-03-12T08:09:59Z"
    }
  ]
}
```

Behavior:

- `200 OK` with an empty `items` array when nothing matches.
- Sorted by `sourceId`, then `assetName`, both case-insensitive.

## Error Model

Non-success responses use this shape:

```json
{
  "error": {
    "code": "source_not_found",
    "message": "Source 'missing' was not found.",
    "details": null
  }
}
```

Error codes for v1:

- `source_not_found`
- `invalid_configuration`
- `internal_error`

Status mapping:

- `404` for unknown source id
- `503` when no effective configuration exists and the endpoint cannot answer safely from local state
- `500` for unexpected server errors

## GitHub Rate Limit Handling

### Detection

Treat GitHub responses as rate-limit events when either of these is true:

- HTTP `429`
- HTTP `403` and `X-RateLimit-Remaining: 0`

Capture these headers when present:

- `X-RateLimit-Limit`
- `X-RateLimit-Remaining`
- `X-RateLimit-Reset`
- `Retry-After`
- `X-RateLimit-Resource`

### Runtime Behavior

When a rate limit is hit:

- do not clear manifests
- do not mark sources empty
- do not delete cached DLLs
- record the rate-limit state in runtime status
- skip new GitHub calls until the computed backoff expires
- continue serving REST queries from the last successful local state

Backoff rule:

- use `Retry-After` when present
- otherwise use `X-RateLimit-Reset`
- if neither header is present, use a conservative fallback of 5 minutes

### Status Semantics

While the service is in backoff:

- `GET /api/v1/status` returns `github.state = "rate_limited"`
- `GET /api/v1/sources`, `GET /api/v1/sources/{sourceId}/dlls`, and `GET /api/v1/dlls` still return `200` using cached local data

After the backoff window expires:

- the next scheduled or manual refresh may try GitHub again
- a successful sync clears the rate-limit status

## Non-Goals For V1

- auth tokens or API keys for the HTTP API
- asset file download endpoints
- pagination
- manual refresh or mutation endpoints
- historical stale-revision browsing

## Recommended Implementation Order

1. Add `HttpApiConfiguration` to the config model and validate loopback-only binding.
2. Add a runtime GitHub sync status object that can represent `ready`, `rate_limited`, and generic `error`.
3. Teach the GitHub client and refresh worker to recognize rate-limit responses and apply backoff without discarding local state.
4. Add the HTTP host and the four read-only endpoints above.
5. Add tests for:
   - source glob filtering
   - DLL glob filtering
   - unknown source handling
   - rate-limit detection and backoff
   - status payload when rate-limited
