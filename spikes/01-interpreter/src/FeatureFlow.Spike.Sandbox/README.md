# FeatureFlow.Spike.Sandbox

Fake sandbox + fake agent runtime + fake hook endpoint for **Spike 1**
(`docs/08-risks-and-spikes.md` 2.1). It is a **standalone .NET 9 web process**,
deliberately not part of `FeatureFlow.Spike.sln`, because the point of the spike is
that the sandbox **outlives a worker crash** (`docs/01-architecture-first-pass.md`
3.6, mechanism 3: sandbox re-attachment by idempotency key).

Spike code, not production code.

## Run

```bash
dotnet run --project spikes/01-interpreter/src/FeatureFlow.Spike.Sandbox -- --port 5199
# or
dotnet run --project spikes/01-interpreter/src/FeatureFlow.Spike.Sandbox -- --urls http://127.0.0.1:5199
```

`--port` defaults to `5199`. `--urls` overrides it entirely.

## Behaviour

- **Time-based, not tick-based.** `elapsedSeconds`, `sessionTurns` and `lastCommit`
  are computed from the command's start timestamp on every read. There is no
  background timer, so state is correct even if nobody polls.
- **Deterministic commit ids.** `lastCommit` is the first 7 hex chars of
  `SHA256("<stepId>:<commitIndex>")`, where `commitIndex = floor(progressSeconds / commitEverySeconds)`.
  No randomness, no GUIDs — the harness compares commits across attempts to decide
  whether progress happened (`docs/01` 3.7).
- **Thread-safe.** `ConcurrentDictionary` for handles, idempotency keys and the
  hook-down set; a lock guards command creation and per-command mutation.
- **Logging.** One line per request on stdout, prefixed `SANDBOX `, with a UTC
  timestamp, method, path, status and duration.
- No signal handlers are installed; the process is unaffected by unrelated
  processes being killed.

## Endpoints

All JSON, camelCase, `application/json`.

### `GET /health`

```json
{ "ok": true }
```

### `POST /commands`

Request:

```json
{ "stepId": "implement", "idempotencyKey": "sbx-implement", "sessionRef": null,
  "durationSeconds": 30, "produceCommits": true, "commitEverySeconds": 5 }
```

Response (200):

```json
{ "handle": "cmd-1", "status": "running", "sessionRef": "sess-1",
  "lastCommit": null, "sessionTurns": 0, "elapsedSeconds": 0, "sandboxKey": "sbx-implement" }
```

**Idempotency is the whole point.** If `idempotencyKey` already has a command, the
existing command is returned unchanged (200) and **no second command is started** —
this is sandbox re-attachment by idempotency key. If `sessionRef` is supplied and
matches the existing command's session, the command resumes that session
(mechanism 2) and `resumeCount` increments. Defaults when omitted:
`durationSeconds` 30, `produceCommits` true, `commitEverySeconds` 5.

### `GET /commands/{handle}`

```json
{ "handle": "cmd-1", "status": "running", "sessionRef": "sess-1",
  "lastCommit": "abc1234", "sessionTurns": 7, "elapsedSeconds": 12.4,
  "sandboxKey": "sbx-implement", "connectionBroken": false, "resumeCount": 0 }
```

`status` is `"completed"` once `elapsedSeconds >= durationSeconds`. While running,
`sessionTurns` grows with time and, when `produceCommits` is true, `lastCommit`
changes every `commitEverySeconds`. Those two are the progress evidence for the
attempt rule (`docs/01` 3.7). Returns 503 `{"reason":"unreachable"}` while a
`break-connection` window is active, and 404 `{"reason":"unknown_handle"}` for an
unknown handle.

### `POST /commands/{handle}/break-connection?seconds=20`

Simulates breaking the worker-to-sandbox connection mid-command. While the window
is active, `GET /commands/{handle}` returns **503** `{"reason":"unreachable"}`.
**The command keeps running** — elapsed time, turns and commits keep advancing, and
`GET /admin/state` shows it. After `seconds` it recovers to 200. Default 20s.

```json
{ "handle": "cmd-1", "brokenForSeconds": 20, "brokenUntil": "2026-10-27T04:28:05.8088064+00:00" }
```

### `POST /commands/{handle}/stall`

Freezes `sessionTurns` and `lastCommit` from that moment. The command keeps running
and still completes on time, so `elapsedSeconds` keeps advancing with no progress.
This drives the "3 consecutive resumes with no progress" test (`docs/01` 3.7).
Returns the current status body.

### `POST /commands/{handle}/fail-hook`

Marks the hook endpoint for this command's `stepId` as down.

```json
{ "handle": "cmd-1", "stepId": "implement" }
```

### `GET /hook/{stepId}`

200 `{"ok":true}` normally. After `fail-hook` for a command with the same
`stepId`, returns **503** `{"reason":"unreachable"}`. This is the "make the hook
endpoint unreachable mid-step" injection: the agent pauses, the session stops in a
resumable state, and the step must classify it as **infrastructure**, not as a
denied tool call (R19).

### `POST /admin/reset`

Clears all commands and hook state. Returns `{"ok":true}`.

### `GET /admin/state`

```json
{ "commands": [ { "handle": "cmd-1", "status": "running", "...": "..." } ],
  "hookDownStepIds": [ "implement" ] }
```

## Verified

Exercised with `curl` against a running instance on port 5199:

- two `POST /commands` with the same `idempotencyKey` return the same handle
  (`cmd-1`) and `/admin/state` shows exactly one command; 20 concurrent POSTs with
  one key also yield one command;
- during `break-connection`, `GET /commands/{handle}` is 503 while
  `elapsedSeconds` advances (35.93 → 45.95 → 57.97) and `lastCommit` changes;
- after `stall`, `sessionTurns` stays 7 and `lastCommit` stays `b5c90b4` while
  `elapsedSeconds` goes 7.04 → 15.06 → 23.07;
- `fail-hook` flips `/hook/implement` from 200 `{"ok":true}` to 503
  `{"reason":"unreachable"}`, leaving other step ids at 200.
