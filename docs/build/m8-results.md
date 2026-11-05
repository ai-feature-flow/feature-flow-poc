# M8 results: team profile and operations

## Foundations

What M8's parallel builders start from, built on `m8-foundations` (W0) before them: the backward-compatibility code
removed, the organisation keys not in version 1 refused, and the shared files the builders would otherwise all edit
prepared. No provider implementations.

**What was built**

| Area | Change |
|---|---|
| No backward compatibility (LOG 263) | All 21 Temporal patch markers in `RunWorkflow*.cs` deleted with the old-history branches they guarded; today's behaviour kept. The interpreter's `RunOptions.BranchFromPattern` and `Steering`, which the shell set from markers, deleted with their old paths: the pre-M7 skip refusal (LOG 207), the pre-M7 `auto_resolved` text (LOG 258), and the pre-M7 patch and go-to checks. `artifact.validate`'s path for a scope without consumed artifacts deleted (LOG 272). `project_session_pre_m7` (LOG 240's pre-session path) and the tests of old paths deleted |
| Histories | Every other history broke on replay (a marker is a recorded command) and was re-recorded from the integration suite with `FF_RECORD_HISTORIES`. The marker-presence tests became one table: each history holds the activity, signal, update or recorded text of the path it covers (LOG 271) |
| Organisation keys (LOG 268 to 270) | `library_overrides`, `library_inputs`, `timeouts` and `policy.toolsets` refused whole: "`<key>` is not supported in version 1; remove it". Their parsing and model fields removed (LOG 273). `role_mapping` stays refused until the Agents builder lifts it with its consumer (LOG 267) |
| Store contract suites | `RecordStoreContract` (runs, waits, timeline, answers, locks, agent step records) and `ArtifactStoreContract` (content addressing, round trips, `not_found`, signed URLs), subclassed for SQLite and the filesystem store (LOG 274) |
| Provider scaffolding | New `FeatureFlow.Providers.S3` and its tests project. Postgres, S3, Kubernetes, CopilotCli and Jira each have an empty `Factories` list, registered by all three hosts (LOG 275) |

**Checks**

| Check | Shows |
|---|---|
| Replay of 19 re-recorded histories | Today's workflow replays every recorded path |
| Each history takes the path it covers (14 rows) | Every covered path has a history recorded on it, so a change to that path's commands (activities, signals, updates, children) fails a replay. A `text` row proves only that the history took the path: replay compares commands, not activity inputs, so a change confined to an input or a record's text is caught by the unit and integration tests instead |
| Each key not in version 1 is refused alone, whatever it holds | The four refusals, with malformed content still refused only for the key |
| `RecordStoreContract`, `ArtifactStoreContract` on SQLite and the filesystem | The solo profile's stores meet the suites the Postgres and S3 providers must pass |
| Architecture test | The S3 project is in the reference rule table and obeys it |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| Replay | The workflow skips the `clean_up` activity: `ticket_clean_up` fails with a nondeterminism error |
| Path table | Run against the old `family_parent` (recorded before `EnsureWorkBranches`): its row fails |
| Key refusal | The refusal's error removed: the test fails |
| Record store suite | Timeline `INSERT OR REPLACE`; a late "open" replaces an answered question; runs listed oldest first: the three matching tests fail |
| Artifact store suite | A put truncated at 1 MB; a signed URL to the store's root: the two matching tests fail |
| Architecture test | S3 dropped from the rule table, and Jira pointed at `Engine`: it fails naming both |

**Deviations and judgement calls:** LOG 271 to 275.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | No sandbox or agent runtime contract suite exists; the Docker sandbox is covered only by the agent integration tests. The Agents builder writes `SandboxContract` (with the network checks) once, for Docker and Kubernetes |
| 2 | `docs/02` section 8's provider decorator (spans, timeouts, classification) does not exist; the Operations builder adds it in `ProviderComposer` |
| 3 | Library sources are resolved to a directory in three places (`ApiApp.Runs`, `ApiApp.Decisions`, `StartupChecks`); the `git` source needs one resolver used by all three |
| 4 | `ChildRunTests`' artifact-index order raced once in the full integration runs (it passed 5 of 5 alone). The workflow starts a child before it records the step that produced the parent's artifact, so under load the child's records can land first. New runs took the same order before M8. Fix in the workflow (record before starting children, re-recording the histories that breaks), not by loosening the assertion |

## Operations

The Jira tracker, OpenTelemetry tracing with the engine's provider decorator, stuck-run detection, and startup resolving
every secret reference (M7 follow-up 4), built on `m8-operations` (W3).

**What was built**

| Area | Change |
|---|---|
| Jira tracker | `tracker` kind `jira` over Jira Cloud's REST API v3 (`FeatureFlow.Providers.Jira`): `status_map` to Jira status names, transitions chosen by the status they lead to, parents (a subtask under a non-epic), typed links, plain-text descriptions and comments, idempotency by a hashed key label (issues) and a hidden comment property (comments). The secret holds `<account email>:<API token>`. A search reads the issues Jira's index has not caught up with (LOG 301, 302) |
| Provider decorator | Composition wraps every provider instance in one generic proxy that keeps all its interfaces: a span per call, its type's timeout, and classification (`timeout`; a `ProviderException` keeps its error; any other exception is recorded on the span and re-thrown, so an action still stops for a person) (LOG 303, 304). Closes Foundations follow-up 2 |
| Tracing | `observability.otlp_endpoint` is read, no longer refused, and traces are exported there with OTLP over gRPC. Temporal's tracing interceptor is on every client and worker with W3C propagation set explicitly: a run is one trace from the API's start through its activities, each provider call inside its activity (LOG 305) |
| Stuck runs | A check beside each orchestration worker, every minute (fixed timings, LOG 270): runs without timeline progress for 15 minutes are looked up in Temporal; an activity failing with infrastructure errors is `stalled`, and after 30 minutes nothing alive with nobody awaited is `stuck`. Each shows as an open wait of that kind, notified through `RunNotifier`, closed with `resumed` only when the run moves or ends. It never changes a run; one run's fault does not stop a pass (LOG 306, 307). M7 follow-up 14's `stalled` and `stuck` |
| Secret references | Every host resolves `api.auth.token_ref`, `agent_worker.token_key_ref` and every provider setting ending in `_ref` before it connects to Temporal, and stops naming each key and reference that does not resolve or is empty, never a value (LOG 308). M7 follow-up 4 |

**Checks**

| Check | Shows |
|---|---|
| `JiraTrackerContract` (the tracker contract against an in-memory Jira whose index lags) and `JiraTrackerTests` (32) | The contract, error classification, status reading, transitions the workflow does not offer, subtasks, link direction, markers, a retried create after out-of-order indexing |
| `JiraTrackerLiveContract` (Live, sandbox site from environment variables) | The contract and a `blocks` link's direction on a real site: 10 of 10, twice. Every issue labelled `feature-flow-test` and closed; none left open (checked by JQL) |
| `ObservabilityTests` (9) | A span per call with its tags; a timeout even when the provider ignores its token; exceptions classified; the caller's cancellation still throws; every interface kept; composition decorates; spans reach an OTLP gRPC endpoint |
| `ObservabilityRunTests` (integration) | A dry-run ticket's activities are one trace with the API's start, and `record_store.Append` is a span inside `RunActivity:Record` |
| `StuckRunsTests` (7), `RunNotifierTests` (1 new) | The rules for stalled and stuck; their waits are messages, and closing is `resumed` |
| `StuckRunTests` (integration, 2) | A tests action unreachable four times shows `stalled` once with its reason, then `resumed`, and the run completes; with no agent worker the planner's step shows `stuck`, an agent worker coming up closes it with `resumed`, and the run completes |
| `SecretCheckTests` (5), `ConfigurationTests` (4 new) | References found at any depth; failures named without values; a host stops before Temporal; the `observability` key read and checked |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| Jira retried create | Written before the fix (the first Live run made a duplicate issue): it failed; passes with the gap read |
| Jira lagging index | Forward reading off (`MaxMisses = 0`): two contract tests fail |
| Decorator | Not applied in composition; no OTLP exporter; caller's span not restored; no `WaitAsync` (the timeout test hangs); exceptions not caught: each fails its test |
| One trace per run | Without the explicit propagator: one run spread over 39 traces |
| Stuck rules | Heartbeating recovery ignored; a condition wait not counted alive; a failing workflow task ignored; the notifier treating `stalled` as a decision: each fails its test |
| Stuck waits close once the run moves | The first version closed only on a change of kind: the stuck integration test failed with the wait still open |
| Secret check | Not called at host start; no walk into nested settings: each fails its test |

**Deviations and judgement calls:** LOG 301 to 308. From `docs/02` section 8: traces only, no metrics or logs export
(LOG 305); spans per activity stand for steps; stuck runs are found from the record store and Temporal's pending work
rather than heartbeat age alone (LOG 306); `stuck` is shown as a position beside `stalled` (LOG 307). Timeouts are per
provider type, not per call (LOG 304).

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | Metrics and logs export (`docs/02` section 8 lists step duration, retries, tokens and more) |
| 2 | The GitHub Issues tracker reads only above the newest listed issue (LOG 184); Jira showed indexes can list a later issue first. Give it LOG 302's gap read |
| 3 | M7 follow-up 14's reminders on open waits are not built |
| 4 | The console lists `stalled` and `stuck` under "Decisions" (with no controls); a heading for positions would read better |
| 5 | M7's `RunNotifierTests` hanging-chat budget test failed twice (calls outside 1 to 3) in its project's runs at load average 19, and passed in the 21 runs after; on `m8` it passed 6 of 6. It times a 1.2 s budget by the wall clock, so CPU starvation can starve its first send; whether this branch's extra test classes add to it was not settled |
| 6 | When the payload codec (W1) lands, its `key_ref` joins `StartupChecks.SecretReferences` if it is not a provider setting |
| 7 | With several orchestration replicas, each runs the stuck-run check and may send its own `stalled`, `stuck` or `resumed` for the same stretch (R3 F2). A single runner, such as a fixed-id workflow, if duplicates matter |

**Fix round (review R3)**

| Finding | Fix | Test | Mutation that fails it |
|---|---|---|---|
| F1: the decorator made a provider's exception `internal_error`, retried for ever, where M5 stops for a person | The decorator records the exception on the span and re-throws; a `ProviderException` keeps its error (docs/06 s1, LOG 303) | `TestingActionTests`: the unexpected-exception and network-fault tests also run with every provider decorated, as composition does (`ActionWorld(decorated: true)`); `ObservabilityTests` checks the re-throw and the span | Re-throw replaced by `internal_error`: the decorated unexpected-exception case fails, the undecorated one passes |
| F2: several orchestration replicas each send `stalled`/`stuck`/`resumed` | No singleton or lock (coordinator: R3's option b). LOG 307 says each replica may send its own message; docs/07 says one replica avoids it; follow-up 7 | None (a documented limit) | None |
| F3: a check that missed the cause closed the wait with a false `resumed`, and it never reopened | A wait closes only when the run has moved (its last timeline sequence changed) or ended | `StuckRunsRecordTests` (record store and notifications real, Temporal's answer scripted): a missed check leaves the wait open and posts nothing | Close also when nothing is found: the test fails |
| F4: one run's error stopped the pass | Caught and logged per run | `StuckRunsRecordTests`: a run whose look-up throws, listed first, does not stop the next | No per-run catch: the test fails |
| R3 M21: already in the status, untested | Test added | `An_issue_already_in_the_status_is_left_as_it_is` (no transitions call) | Skip removed: fails |
| R3 M22: the "not mapped" refusal, untested | Test added | `A_status_the_map_does_not_name_is_refused_before_Jira_is_asked` | Unmapped passed through as a Jira name: fails |
| R3 M23: transition by name passed | The fake's transitions are named "Move to X", unlike their statuses | `A_transition_is_chosen_by_the_status_it_leads_to` | Chosen by name only: fails |
| R3 M24: one wait per stretch, untested | Test added | `StuckRunsRecordTests`: once the run moves, the old wait closes and the new stretch opens `stalled-2` | Id without its stretch: three tests fail |
| R3 M1: the timeout test hung rather than failed | The two timeout tests bound the call to 10 s | Same tests | `WaitAsync` removed: they fail in 10 s |
| R3 note: VCS timeout per call, git's per command | One line in LOG 304 | None | None |
