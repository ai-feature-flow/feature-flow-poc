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

## Stores

The team profile's stores and the recovery and confidentiality around them, built on `m8-stores` (W1): a Postgres record
store, an artifact store on the S3 API, the payload codec, the reconcile command, the `git` library source, and the fix
for Foundations follow-up 4 (the artifact index's order).

**What was built**

| Area | Change |
|---|---|
| Postgres record store | `record_store` kind `postgres`, setting `connection_ref` (the secret holding the connection string). The SQLite store's tables in Postgres, `jsonb` bodies, `org_id` first in every key; one numbered schema script applied at first use under an advisory lock (LOG 281) |
| S3 artifact store | `artifact_store` kind `s3`: `bucket`, `region`, `endpoint` (MinIO, path-style), `access_key_ref` with `secret_key_ref`, or the AWS SDK's credential chain. Objects `sha256/<hex>`, written once; signed URLs are presigned GETs (LOG 283) |
| Payload codec | `temporal.payload_codec: { name: aes_gcm, key_ref }`. AES-256-GCM over each whole payload, and failure messages through encoded attributes. All hosts connect through `TemporalConnection`, so the API and both workers use it (LOG 284) |
| Reconcile | `<host> reconcile --installation ... --organisation ...` on any of the three hosts: one pass over the run workflows on the installation's task queue, writing the run records the store lacks or shows open when Temporal has closed them (LOG 285) |
| `git` library source | `source: git` with `repository` and `ref` (a branch or tag, else the default branch). `LibrarySources.OpenAsync` resolves every source for run start, answers and startup checks: a fresh shallow clone, read and deleted (LOG 286). `package` stays refused (LOG 287) |
| Artifact index order | The workflow records what it holds before it starts a child, so a parent's artifacts reach the index before its child's. Six histories with children re-recorded (LOG 288) |
| Test containers | `tests/Providers/Shared/Container.cs` starts Postgres (`postgres:16-alpine`) and MinIO (`cgr.dev/chainguard/minio`, LOG 282) with the Docker CLI, one per test assembly, in the Integration category |

**Checks**

| Check | Shows |
|---|---|
| `RecordStoreContract`, `FindingsLedgerContract`, `SteeringRecordsContract` on Postgres (19) | The Postgres store keeps every record as the SQLite store does |
| Postgres's own (5) | The connection comes from the named secret; a wrong password is `unauthenticated`, a closed port `unreachable`, a missing secret `not_found`; four stores starting together apply the schema once; every table has `org_id`; a newer schema is refused |
| `ArtifactStoreContract` on MinIO (7) | Content addressing, 12 MB round trip, `not_found`, a signed URL read with no credentials |
| S3's own (5) | Keys from the named secrets; one key reference alone refused; wrong key, missing bucket, closed port classified; an unsigned URL refused and a signed one expiring; an artifact written once |
| Codec unit tests (6) and reader tests (4) | Round trip; no plaintext; tamper and wrong key refused; the key secret's form; failure messages encrypted; `payload_codec` needs a known name and a `key_ref` |
| Codec in a run (2) | With the codec every payload in every history of a ticket run is `binary/encrypted` and none holds the run's ticket id; the history replays with the key and not without. Without the codec the same walk finds the ticket id |
| Reconcile (1) | Two run records deleted (a completed run and an open one) and a third run terminated outside the engine: the command rebuilds all three (`completed`, `running`, `incomplete`) with their template, requester, parent and repositories, counts only its own queue's runs, and a second pass writes nothing |
| `git` source (5 Hosting, 6 Model, 1 integration) | A branch read as it is now, a tag as it was, the default branch; nothing left in temp; a missing ref or repository named; a non-library refused; startup checks fetch; each source takes only its keys; a run's lock pins the branch as it was at run start while the next run sees a push |
| `ChildRunTests` index order | Reproduced W0's failure by delaying the parent's record activity 3 s; passes with the fix and the delay |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| Postgres contracts | A closing wait replaces the body; runs listed oldest first; the timeline replaced on conflict: the matching contract test fails each time |
| `org_id` in every table | `locks` created without `org_id`: the schema test fails |
| S3 | A signed URL without the existence check; the TTL ignored; a put without the HEAD: the `not_found`, expiry and written-once tests fail |
| Codec | Hosts connect without the codec's converter: the run test fails. Failure attributes not encoded; encode passes payloads through: the unit tests fail |
| Reconcile | The list not scoped to the task queue; terminated read as `completed`; only missing records rebuilt; every record rewritten: the test fails each time |
| `git` source | The `ref` ignored; the clone kept; startup ignoring the problem: the tag, temp and startup tests fail |
| Index order | Without the fix, the injected delay fails `ChildRunTests` as W0 saw; the six old histories fail replay against the new workflow |
| Test containers | A pull's progress on standard error mixed into the container id, with an image not yet pulled: all 24 Postgres tests fail, as they did on the PR's first CI run |

**Deviations and judgement calls:** LOG 281 to 288.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | A private `git` library repository is cloned with the host's own Git credentials (LOG 286), not the credential broker. If adopters want the GitHub App to read it, the broker issues a read-only token for the library repository |
| 2 | The payload codec has no key id, so a key cannot be rotated without losing old histories' readability (LOG 284) |
| 3 | Reconcile rebuilds only run records; a restored database also lacks newer waits and timelines, which the runs write again only as they change (LOG 285) |
| 4 | docs/07's `temporal.auth`, `temporal.tls` and `expected_retention` are not read by the installation reader yet; a Temporal that needs TLS or an API key (Temporal Cloud) cannot be configured |
| 5 | `RunNotifierTests.A_batch_that_meets_a_hanging_chat_stops_at_its_budget_and_drops_the_rest` asserts a batch ends within 3 s of wall clock; it failed once in a full default run on this machine under load and passed 3 of 3 alone. Not changed here |

**Fix round (review R1)**

| Finding | Fix | Test | Mutation that fails it |
|---|---|---|---|
| F1: an answer skipped a `git` library that did not clone, so its approval rules dropped out and the engine's default let an approver answer a steerer's decision | Libraries are read only when they can change the outcome (no organisation rule for the type, not an intervention); then any that does not open refuses the answer or session action: 503 "library X cannot be read now; try again" (LOG 289) | `epic.escalation` with `std` as a directory: approver 403; with `std` from a git source that does not clone: 503, nothing recorded or delivered | Skipping the unreadable library again: the approver gets 200. Re-check: with an organisation rule `*` (or for an intervention) an unclonable library does not block the answer (200); reading always fails that test, never reading fails the 503 one. The memory store's newer-record rule has its own test |
| F2: Postgres's `jsonb` refuses NUL, a retryable `server_error`, so a record with outside text could retry for ever | An escaped NUL is written as U+FFFD; a literal backslash-u0000 is kept (LOG 290) | Contract: a run whose detail holds NUL and a literal `\u0000` is written and reads back intact, NUL kept or replaced (SQLite and Postgres) | No replacement (22P05); a naive replace that also rewrites the literal text |
| F3: reconcile's "running", stamped now, could overwrite the run's own newer `completed`; so could a late activity | A run record replaces only one no newer than itself, in all three stores; reconcile dates an open run at its workflow run's start (LOG 290) | Contract: an older write leaves the newer record, an equal-time write replaces it. Integration: the run's `completed` written between reconcile's read and its write survives | The rule removed in Postgres or SQLite; `<` for `<=`; reconcile stamping now |
| F4: a `repository` URL with a password would reach logs and errors | The organisation reader refuses a URL with a password, or any user name over http(s); an SSH user name is allowed (LOG 289) | Three refused URLs; `ssh://git@...` read | The check disabled: the three rows fail |

R1's "passes by construction" notes need no control: one organisation per installation makes `org_id` isolation untestable
in version 1 (the keys are checked), and the codec's constructor already refuses any key but 32 bytes. R1's design notes
on lost commit acknowledgements (duplicated appends), the lock not pinning the library's commit, symlinks in a library
clone and unchecked S3 read hashes are follow-ups, not changed here.

## Packaging

Built on `m8-packaging` (W4): the three hosts as images, the k3d deployment, a template a team copies, deployment notes,
and CI for every test type of `docs/09` section 4.2. The M6 epic on the cluster (done-when 1) is the integration step's;
this makes it possible.

**What was built**

| Area | Change |
|---|---|
| Clean stop (M6 follow-up 7) | A host handles SIGTERM and SIGINT itself, stops its workers and endpoints, logs `Stopped` and exits 0. ASP.NET's handler had taken SIGTERM in the API and the agent worker and stopped only the endpoint, so they never exited (LOG 312) |
| Images | `deploy/images/host.Dockerfile`, one for the three hosts (`HOST`): ASP.NET runtime, git, tini as process 1, the image's unprivileged user, std in `/opt/feature-flow/library/std`, the sandbox client in the agent worker's. No configuration or secret baked in (LOG 311) |
| Engine manifests | `deploy/kubernetes`: the three deployments (read-only root, no privilege), services, one account with rights only in the sandboxes' namespace, network policies: ingress denied by default, sandboxes reach only DNS, the agent worker and the public internet (LOG 314). The sandboxes' namespace enforces Pod Security's `baseline`, so no pod there escapes the policies through the host's network, privilege or paths (R4 F1) |
| k3d profile | `deploy/k3d`: Temporal's dev server, Postgres and MinIO in the cluster, each on a volume and reachable only from the hosts (LOG 315). `up.sh <config folder>` creates the cluster with its own kubeconfig file, builds and imports the images, generates the infrastructure secrets once and makes the ConfigMap and Secret (LOG 313); `down.sh` deletes it. `smoke/` is a trial configuration: Postgres, MinIO, the payload codec and the Kubernetes sandbox (checked at startup by all three hosts), with fake providers outside the cluster (LOG 317) |
| Distribution template | `deploy/distribution-template`: the small-company profile's installation and organisation (with `role_mapping` and an optional `observability.otlp_endpoint`), an example secrets file, a Kustomize overlay for a team's own cluster, a README |
| Deployment notes | `deploy/README.md`: the solo developer, k3d, and AWS (EKS, RDS, S3, Temporal's Helm chart) as configuration |
| CI | A `Cluster` category and job: k3d, `up.sh deploy/k3d/smoke`, the `Cluster` tests. `ci.yml` names where each test type runs (LOG 316) |

Where each test type of `docs/09` section 4.2 runs (done-when 2):

| Type | CI job (tests) |
|---|---|
| Validation, re-drive, readable form | Default (`Validation.Tests`, `Testing.Workflows.Tests` over `library/std/tests`, `ReadableFormTests` golden files) |
| Replay | Default (`Replay.Tests`) |
| Failure injection | Integration (`ContinueAsNewTests`, `AgentResumeTests`, `Sp1*`, `StepFailureTests`, `HostProcessTests`) |
| Run-time rules | Default (`Engine.Tests`) and integration (`PolicyRunTests`, `StepStartCheckTests`) |
| Provider contracts | Default (in-process providers), integration (Docker; Postgres and MinIO in containers), cluster (the Kubernetes sandbox) |
| Merge race | Integration (`MergeRaceTests`, `EngineMergeRaceTests`) |
| Agent reporting | Integration (`AgentContractTests`) |
| Pre-submit parity | Integration (`SessionRunTests`) |
| The deployment itself | Cluster (`Cluster/DeploymentTests`) |

**Checks**

| Check | Shows |
|---|---|
| `HostProcessTests` (3) | Each built host, as a process, exits 0 within 20 s of SIGTERM, having logged `Stopped` |
| `DeploymentTests`: a sandbox's probe | A pod in the sandboxes' namespace reaches the agent worker's endpoint and the internet, and not Temporal, Postgres, MinIO or the API |
| `DeploymentTests`: hosts only | A pod labelled as a host reaches all six targets (the method sees what is open); a plain pod in the engine's namespace reaches only the API and the internet |
| `DeploymentTests`: clean stop (3) | Deleting each host's pod takes under 20 s (the grace period is 30) and its log ends `Stopped` |
| `DeploymentTests`: Pod Security | With the hosts' account, a host-network pod in the sandboxes' namespace is refused by Pod Security; a plain one is accepted (server dry run) |
| `DeploymentConfigurationTests` (2, default) | `deploy/k3d/smoke` and the distribution template's configuration pass the startup checks and compose with every host's factories (settings, role_mapping's runtimes), so they cannot drift from the providers |
| `ClusterRunTests` | A std ticket run started through the API's pod completes across the three pods, recorded in Postgres and MinIO; every payload of its Temporal history is `binary/encrypted` (checked by hand once) |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| `HostProcessTests` | The old `Console.CancelKeyPress` handler: all three fail (the API and agent worker hang; the orchestration worker exits without stopping). Its limit (R4): with no activity in flight, a host that exits without waiting for its workers to drain passes too; the drain is not tested |
| Sandbox probe | The sandboxes' policy deleted: the sandbox reaches the API, Temporal and the stores |
| Hosts only | The services' policy opened to every pod: a plain pod reaches Temporal, Postgres and MinIO |
| Clean stop | The API image built without the fix: no `Stopped`, and the pod waits out its grace period |
| The account's rights | The role without reading network policies: the API crash-loops (the Kubernetes sandbox's startup check) |
| Configuration | The template's `connection_ref` renamed: "unknown setting"; its reviewer mapped to an absent runtime: the role_mapping check. Each fails the test |
| Pod Security | The namespace's `baseline` label removed: the host-network pod is accepted, and the test fails |
| tini | Without it, a host that fails at startup spins at full CPU forever as process 1; with it, it exits 134 |

**Deviations and judgement calls:** LOG 311 to 318.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | A host exits when Temporal or a store is not reachable at startup, and Kubernetes restarts it: on a fresh k3d cluster the hosts start before Postgres is ready (four restarts), and a new pod's first connection can be refused while k3s applies its network policies. A host could retry at startup instead (docs/02 section 7) |
| 2 | Temporal Cloud needs `temporal.tls` and `temporal.auth` in the installation (docs/07 section 2 shows them; nothing reads them) |
| 3 | No image is published: a team builds the images at a release and pushes them to its registry |
| 4 | `cgr.dev/chainguard/minio:latest` is not pinned, so a cluster run is not exactly reproducible |

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
| 5 | Fixed in the R3 round: M7's hanging-chat budget test failed with four sends in a 1.2 s budget of 500 ms sends (`Actual: 4`). A send the budget had cut short could finish a moment before the clock read the deadline (timers and clocks differ by up to a tick), so one more started. `RunNotifier` now ends the batch when the budget cut a send short. Without that, 3 of 20 runs of `RunNotifierTests` failed; with it, 0 of 20, and 0 of 20 of the Engine tests |
| 6 | Done in the merge of W1's stores: `temporal.payload_codec.key_ref` is among the references startup resolves |
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
| Flake (follow-up 5): M7's hanging-chat budget test, `Actual: 4` | A send the budget cut short ends the batch (`RunNotifier`) | `RunNotifierTests.A_batch_that_meets_a_hanging_chat...` | Fix removed: 3 of 20 runs fail; with it 0 of 20 |

## Agents

The sandbox's network (M4 follow-up 12), the Kubernetes pod sandbox, the Copilot CLI runtime and `role_mapping`, built on
`m8-agents` (W2).

**What was built**

| Area | Change |
|---|---|
| Sandbox network | A sandbox reaches the agent worker (`agent_worker.urls` and the model proxy) and the internet, nothing else on its host or cluster. Docker: root's iptables rules inside the container at creation, the agent with no capability to change them (LOG 291). Kubernetes: `sandbox-network-policy.yaml`, checked present at startup (LOG 294) |
| `SandboxContract` | New contract suite: sandboxes found by key and gone when disposed; commands as the agent's user, polled, re-attached, cancelled, unforgeable; copies; read-only paths; the engine's home; and the network checks. Run on Docker (Integration) and Kubernetes (new category `Cluster`, LOG 297) |
| `ShellSandbox` | The command machinery both sandboxes share, in `Providers.Abstractions` (LOG 292). It fixed `CancelCommandAsync`, which had never signalled |
| Kubernetes pod sandbox | `KubernetesSandbox` (KubernetesClient): a pod per sandbox, CPU and memory limits, no service account token, `/ff` an `emptyDir` (LOG 294) |
| Copilot CLI runtime | `CopilotCliRuntime`, from Spike 2's provider on Claude Code's pattern. The model through the one proxy as a custom Anthropic provider with the step's token; settings and hooks the agent cannot change; reports through the engine's MCP tools (LOG 293, 296). `HookPolicy` reads Copilot's names; the hook command answers Copilot with a JSON decision |
| `role_mapping` | The refusal is lifted. Resolved into the run's policy at start; each step runs on its role's runtime instance and model, recorded on the run (LOG 295) |
| Image | `feature-flow-sandbox:m8`: iptables, tini, curl and Copilot CLI 1.0.105 added |

**Checks**

| Check | Shows |
|---|---|
| `SandboxContract` on Docker, 12 tests | The contract, and that the sandbox reaches the worker's port and github.com, but not another port of the host, a service on the host's loopback, or a neighbouring container; nor after the agent tries to flush the rules |
| `SandboxContract` on k3d, 13 tests | The same for a pod: the stand-in agent worker in another namespace and github.com reached; a stand-in store, the Kubernetes API, the node and the Docker host not. The provider refuses to start where the policy is absent |
| Agent integration tests (25), with the network closed | The worker, hooks and proxy still reach every M4 path |
| `RoleMappingTests` | An implementer runs on `claude_code` with `claude-haiku-4-5`, a reviewer on a second instance with that instance's own Opus model, read from the requests the scripted model received |
| `CopilotStepTests`, 4 tests | The real Copilot CLI in the sandbox, scripted model behind the real proxy: a step commits, is denied `git push` with the reason, reports through `engine-report`; SP5 C2 to C4 (no model or GitHub credential in any process environment, the home or the transcript), N1 (model unreachable: stops resumably, then completes), N2 (key refused: stops for a person) |
| `LiveCopilotTests`, 4 tests | SP5 for Copilot CLI with the real model, which SP5 did not test: C1 a real step committed to the fixture's work branch; C2 to C4 held, and Copilot keeps even its provider key out of its tools' shells; N1 two sessions ended `model_unreachable`, no attempt used, nothing reported; N2 a 401 stopped the step for a person. No reference left on the fixture |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| Docker network | No rules at creation: the host's other port is reached (2 checks fail). No allow entry: the worker is not reached. The agent run as root: it flushes the rules |
| Kubernetes network | The policy selecting no pods: the store is reached. The agent-worker rule removed, or without `namespaceSelector`: the worker is not reached. An unlabelled pod reaches all four forbidden endpoints, so each forbidden check can fail |
| Cancel | The old `kill -TERM --` line: the cancel check fails |
| `role_mapping` | The workflow ignoring the mapping, the worker ignoring the step's instance, Claude Code ignoring the task's model: each fails `RoleMappingTests` |
| Copilot hooks | No `PreToolUse` hook: the push is attempted. The exit-2 denial for Copilot: the agent sees no reason. `HookPolicy` without `engine-*` or `path`: 4 tests fail |

**Review R2 fix round:** F1 every Docker attach re-closes a network found open; F2 the Copilot test tries to remove, replace and switch off the engine's hooks; F3 the proxy records each request's model; F4 seccomp and no privilege escalation for the pod, `no_new_privs` for the agent on both platforms; F5 the pod's memory request is below its limit. Each with a control in the PR's report.

**Deviations and judgement calls:** LOG 291 to 297.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | One model proxy serves every runtime (LOG 293). A runtime on a different upstream (an OpenAI-compatible router) needs a proxy per runtime instance and a second wire format |
| 2 | Copilot CLI's handling of the hook's `continue: false` when the engine is unreachable is untested: the MCP path ends the runtime itself, the hook path may let the call through (as "unreachable is not denied" requires) without stopping the session |
| 3 | The agent can add its own files (skills, agents) to Copilot's sticky home between sessions of a step. They run in the same sandbox with the same rights; only `settings.json` and `mcp-config.json` are protected |
| 4 | `CopilotCliRuntime` repeats some of `ClaudeCodeRuntime` (the `FF_` environment, the result's marker and report file); a shared helper would need a home in `Providers.Abstractions` |
| 5 | No sweeper removes sandbox pods (or containers) whose run has closed: a run terminated, or an activity never run again, leaves its pod. Its memory request is 256Mi, below its limit, until one exists (review R2, F5) |
| 6 | The model a step runs on is the runtime's choice: the proxy records the model each request names but does not refuse another (review R2, F3). Pinning it at the proxy needs each runtime's background models too |

## Integration

Worker W5, branch `m8-integration`, from `m8` at `30319c8` (PRs #25 to #31 merged). Temporal on port 8633 for local
runs; k3d cluster `ff-w5`. Judgement calls LOG 321 to 326, unreviewed.

### Cross-piece checks

On the merged `m8` before any change (Debug, `-m:4`).

| Check | Result |
|---|---|
| Default suite | 1,382 passed, 0 failed (24 projects) |
| Integration suite (Temporal :8633, Docker) | 168 passed, 0 failed (Integration 116, Postgres 26, S3 12, Docker 14) |
| The three host images and the sandbox image | Built by `up.sh` |
| `deploy/k3d/up.sh deploy/k3d/smoke` from no cluster | Up in 2 min 20 s; each host restarted 4 times while Postgres started (Packaging follow-up 1) |
| `Cluster` suite against it | 21 passed (Integration 7, Kubernetes 14) |
| M4 follow-up 12 on the deployed cluster, by hand, before the live run | A pod in `feature-flow-sandboxes` (the sandbox image) is refused by Temporal, Postgres, MinIO and the API, and reaches the agent worker and github.com |
| The same during the live run, from a real sandbox pod the agent worker made, as root | Refused by Temporal, Postgres, MinIO, the API and the Kubernetes API; reaches the agent worker and github.com; no token, key or secret variable in its environment |

Controls for the hand check: with the sandboxes' egress policy deleted, the probe reaches the API (Temporal and the
stores stay refused by their own ingress policy, a second layer); with that policy deleted too, it reaches all four.
Re-applying `deploy/k3d` restores the refusals.

### Seams found and fixed

Each fix is its own commit, with a test and a control (a mutation that made the test fail, then restored).

| # | Seam | Fix | Test (control) |
|---|---|---|---|
| 1 | The `git` library source reads only a repository's root, and std lives at `library/std` of the engine's repository, so the done-when's "std from Git" could not be configured | A `git` source takes an optional `path`, as `directory` does; absolute or `..` refused (LOG 321) | `GitLibrarySourceTests.A_library_in_a_folder_...` (the resolver ignoring `path` fails it); two `OrganisationTests` rows (the reader's check removed fails both) |
| 2 | In the cluster every model request failed 502 for ten minutes with nothing in any log: this machine's network re-signs the model API's TLS and the image trusts only public roots (DR20 holds for a process on a laptop, not an image) | The model proxy writes the cause to its log and its 502 body (the step's transcript). `deploy/README.md` shows a deployment adding a CA bundle through `SSL_CERT_FILE`; no engine setting (LOG 322) | `EndpointTests.With_the_model_unreachable_...` (the old body fails it). In the pod: `UntrustedRoot` without the bundle, the model API reached with it |
| 3 | Done-when 2: docs/09's failure injection names "Temporal unavailable", which no test covered; CI's logs gave only per-assembly totals; the integration job built a sandbox image tagged `:m4` that no test used | `TemporalOutageTests` stops and restarts its own dev server under a waiting run; CI lists every test it runs; the job builds `:m8` (LOG 323) | The host's workers stopped 12 s after start (a worker that does not come back): the test fails. It also asserts an answer during the outage is refused, so the outage is real |
| 4 | A CI flake on this PR, "the command ... was lost" (`DockerSandboxContractTests`): `ShellSandbox`'s status read could see a finished command, reaped but with its exit code not yet written, as lost | A command runs while the start script waiting on it lives, which writes the code before it ends (LOG 324). Shared by the Docker and Kubernetes sandboxes | Reproduced with a 1 s gap injected before the code is written: every Docker contract test failed; with the fix and the gap, 14 of 14 pass. Two contract tests (Docker and Kubernetes): a command whose process has gone, with its code not yet written (held open by pointing the recorded process id at no process), reads as running and then ended (the old status script fails it: "lost"); a command killed with its waiter reads as lost (a status that never says lost fails it) |
| 5 | A person approving the epic's ticket plan ratifies its work list, but in the team profile nothing let them read it: the artifacts are in MinIO inside the cluster and the timeline shows only references. I read them from MinIO by hand | `GET /runs/{id}/artifacts/{reference}` and `ff run artifact`: an artifact the run itself recorded, for a viewer of the run; a child's is read through the child (review R5, F1); one the store no longer holds is 404 (R5, F2) (LOG 325) | `ApiTests.A_viewer_reads_the_artifacts_its_run_recorded_...` (without the index check, without the viewer check, serving the whole tree, or a missing object as an error: each fails it). Live: the epic's work list read through the CLI; the same reference through a child run refused |
| 6 | Seam 2's ten minutes of 502s showed nowhere in the run: each session ended resumably, so no activity failed and the stuck-run check saw nothing; the timeline said only "design started" (coordinator's decision after the report) | The agent worker records `model_unreachable` and signals the run, which shows `model_unreachable <step>: <cause>` on its timeline, once per step and cause (LOG 326) | `EndpointTests.A_model_unreachable_is_signalled_to_the_run_once_per_step_and_cause` (without the once-per-cause check it fails); `AgentCredentialTests.With_the_model_unreachable_...` now asserts one such line across at least three failed attempts (without the check, or with the workflow ignoring the signal, it fails) |

CI flake after review R5: `StuckRunTests.An_activity_no_worker_takes_...` found no `stuck` wait when the run's own record
write was still in flight (a started activity counts as alive). Reproduced by delaying that write 3 s; the test now waits
until the agent step is the only pending activity, and passes with the delay.

Not seams: the GitHub App broker needed its `repositories` list (my configuration); the first CI run's Cluster job
failed when the runner's k3s node restarted during the image builds (the next run passed).

### Done-when 1: the M6 epic on the small-company profile in k3d (complete)

**Setup.** Cluster `ff-w5` from `deploy/k3d/up.sh` with an installation copied from the distribution template (kept
outside the repository): Postgres, MinIO and Temporal's dev server in the cluster with the three hosts; the Kubernetes
pod sandbox; Claude Code through the model proxy on the product owner's subscription; GitHub, GitHub Actions and the
existing GitHub App; Jira on the sandbox site; the payload codec; std from
`ai-feature-flow/feature-flow-poc` at `m8-integration`, `path: library/std` (seam 1); `role_mapping` with
`reviewer` on `sonnet` and `default` on `haiku`; the API's local token, actor `w5-operator` with all four roles (OIDC is
tested since M7; there is no identity provider in the cluster). Every approval rule `auto: false`, so a person answered
each decision. The hosts were given a CA bundle by hand for this machine's TLS inspection (seam 2).

**Tracker.** A Jira epic FWFEED-115 ("widget query helpers") and three tasks under it, FWFEED-116 (find by name),
FWFEED-117 (count by prefix) and FWFEED-118 (sorted names), in M6's shape: each with acceptance criteria and its own
new files; all labelled `feature-flow-test`. Parent branch `ff/m8-live/epic-base` from `main` (`d033998`). Started with
`--continue-as-new-events 200`.

**F1 control** (`run-625fd895d5fd`): the same start without `--parent-branch` was refused at run start, check 23
(an engine merge into the default branch `main`); no branch was made.

**The run** (`run-85af56149b09`, **completed**, 06:25 to 07:36 UTC, 71 minutes, including the ten minutes of seam 2,
a question that waited twelve minutes unseen, and the approvals):

| Step | What happened |
|---|---|
| `baseline` | Both tiers in GitHub Actions at `d033998`: unit 6 passed, integration 1 passed |
| `design`, `validate_design` | Jira moved FWFEED-115 to In Progress. Sessions 1 to 3 hit seam 2 (502s); after the bundle the agent worker restarted, the step re-attached its sandbox and session 4 wrote the requirements and contract. `requirement_inheritance` and `requirement_support` valid |
| `tickets.plan`, `tickets.validate` | A valid plan at the first attempt: three items with `tracker_issue` FWFEED-116 to 118 |
| `tickets.approve` | Read with `ff run artifact`'s route by hand (seam 5), approved by `w5-operator` |
| `tickets.children_0..2` | Three ticket runs, two at a time, each in its own sandbox pods: baseline, plan validated against the epic's, spec approved by `w5-operator`, implement on `haiku`, tests in CI, five reviewers on `sonnet` (role_mapping, read from each session's start), satisfaction, PR into the epic branch, checks, catch-up, **engine squash merge**, Jira to Done. Each handed over once or twice (`continued_as_new`) |
| Ticket FWFEED-116 | After catching up with FWFEED-117's merge, the retest failed in CI: both tickets had declared `public static class WidgetQueries` (CS0101). The repair step renamed its class (`ebee5e9`), the tests passed (14), the five reviewers re-reviewed, and it merged |
| Ticket FWFEED-118 | Its implementer asked through `ask_human` which of the design's class name and the ticket's own-file rule to keep; `w5-operator` answered "a new file with its own class" through `ff run answer` |
| hand-over | The epic handed over once (at 224 events, after its children ended) |
| `verify` | Tests at the epic head `26e803b`: unit 17 passed, integration 1 passed; five reviewers, 3 new findings and 1 re-raised, none blocking |
| `trace` | `requirement_trace` valid |
| `deliver` | PR #197; checks passed; **merge commit `020295e`** into `ff/m8-live/epic-base`, by Feature Flow; Jira moved FWFEED-115 to Done |

Checked afterwards, outside the engine:

- GitHub: `020295e` has parents `d033998` (the base) and `26e803b` (the epic head), author Feature Flow, trailer
  `Feature-Flow-Merge: run-85af56149b09/deliver.merge`. Below it, three single-parent squash commits, one per ticket.
  The change against `main` is exactly three new source and three new test files. `main` stayed `d033998`. PRs #194
  to #196 closed with their squash commits, #197 merged.
- Jira: FWFEED-115 to 118 Done, each moved by the tracker (tickets through In Progress and In Review).
- Temporal (port-forwarded): the tree's nine histories (four workflows and five hand-overs) hold 1,004 payloads, every
  one `binary/encrypted`; no ticket key, ticket name or branch name appears in them as text.
- Postgres: the tree's 4 run records, 757 timeline rows, 81 artifact records, 10 answer records and 8 findings. MinIO:
  407 objects.

### Done-when 2: every test type of docs/09 section 4.2 runs in CI

Read from the logs of CI run 41982380701 on this PR (`148edbb`), which list every test (seam 3): default job 1,388
passed, integration 169, cluster 21; none failed or skipped.

| Type | Job: tests (count in that run) |
|---|---|
| Validation | Default: `StdLibraryTests` 20, `MutationTests` 37, `RunStartCheckTests` 17, `EffectivePolicyTests` 4 |
| Re-drive | Default: `WorkflowTestRunnerTests` 37 (std's workflow test files) |
| Readable form | Default: `ReadableFormTests` 4 |
| Replay | Default: `ReplayTests` 35 |
| Failure injection | Integration: worker crash `AgentResumeTests` 2; lost sandbox and hook endpoint unreachable `AgentBoundaryTests` 10; Temporal unavailable `TemporalOutageTests` 1 (new); continue-as-new with children and open decisions `ContinueAsNewTests` 1, `EpicRunTests` 1; `StepFailureTests` 1. Default: stale patch `PatchTests` 18; hook endpoint unreachable `SandboxClientTests` 14 |
| Run-time rules | Default: `Engine.Tests` 278. Integration: `PolicyRunTests` 4, `StepStartCheckTests` 3 |
| Provider contracts | Default: `Providers.Contracts.Tests` 72 and each provider's own. Integration: Postgres (record store 12, ledger 5, steering 4), S3 7, Docker sandbox 14. Cluster: Kubernetes sandbox 14 |
| Merge race | Integration: `MergeRaceTests` 4, `EngineMergeRaceTests` 5 |
| Agent reporting | Integration: `AgentContractTests` 5 (`report`, `ask_human`), `AgentResumeTests` (`ask_human` across a worker restart), `AgentBoundaryTests` (`checkpoint`). `propose_patch` is not an agent tool in version 1 (M4 deviation 5, LOG 264): a step's proposed patch is tested in default (`PatchTests`) and an agent's proposal through a run in integration (`SteeringRunTests`) |
| Pre-submit parity | Integration: `SessionRunTests` 6 |
| The deployment | Cluster: `DeploymentTests` 6, `ClusterRunTests` 1. Integration: `HostProcessTests` 3 |

### Tests run

On `m8-integration` at the end (Debug, `-m:4`, Temporal :8633, Docker): default suite **1,389 passed**, 0 failed (24
projects; replay 35); integration **170 passed**, 0 failed (Integration 117, Postgres 26, S3 12, Docker 15). Cluster
suite on `ff-w5` before the live run: 21 passed; the Kubernetes sandbox contract again after seam 4: 15 passed. Build: 0
warnings. CI on the pull request: green on every push since the runner fault, with the Cluster job. After seam 6: `Engine.Agent.Tests` 88 passed, replay 35 passed, integration 170 passed again.

### Deviations

- The live installation, its secrets and the CA bundle are outside the repository (LOG 266). The bundle was added to
  the three deployments with `kubectl patch`, as `deploy/README.md` now describes.
- The API used the local token (coordinator's decision): `w5-operator`'s approvals and answer are the authenticated
  person's, recorded as such. No notifications were configured, so the agent's question was found by looking.
- The agent worker restarted twice during the run (the CA bundle, then the image with these fixes after the run had
  completed); the design step re-attached its sandbox and went on.

### Clean-up

Deleted on the fixture after the run: the branches `ff/m8-live/epic-base` (`020295e`), `ff/epic/widget_queries`
(`26e803b`), the three `ff/ticket/*`, and four `refs/feature-flow/wip/*` references the run left. The fixture has only
`main` (`d033998`) and no open pull request. FWFEED-115 to 118 are Done (closed). The k3d cluster `ff-w5` is deleted.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Done (seam 6, LOG 326): a model the agent worker cannot reach now shows on the run's timeline. It is still not a `stalled` position or a notification |
| 2 | A host whose Temporal worker task faults keeps running without it (`Composition.Run` does not watch the task), so a pod stays alive polling nothing. The host could exit and let Kubernetes restart it |
| 3 | `GET /runs/{id}/findings` checks only that the caller is authenticated, not that it may see the run, as the run view and the artifact read do |
| 4 | A completed run leaves its work branches and `refs/feature-flow/wip/*` references on the code host (deleted by hand, as in M6) |
| 5 | `up.sh` does not check that `k3d image import` succeeded (the failed CI run printed an error and went on) |
| 6 | `TemporalOutageTests` covers a run waiting on a decision; an activity running or completing during an outage (heartbeat, completion retry) is not tested (review R5) |

## Consolidated follow-ups

"From" names the follow-ups table of a section of this note and its row: Foundations, Stores, Packaging, Operations,
Agents, Integration; M7 is `m7-results.md`'s consolidated follow-ups. Closed during M8 and not listed: Foundations 1 to
4 (the sandbox contract, the provider decorator, one library resolver, the artifact index's order), Stores 5 and
Operations 5 (the notifier's budget flake), Operations 6 (the codec key's reference checked at startup), and M7's 1 (the
sandbox's network, proven on the cluster), 2 (each organisation key decided, LOG 267 to 270), 4 (startup resolves every
secret reference), 14's `stalled` and `stuck`, 18 (`teams.<name>.repositories` in docs/07) and 21's "detached hosts
ignore SIGTERM" (LOG 312). Dropped by the product owner (LOG 264): M7's 3, 6, 7 and 9.

| # | Follow-up | From |
|---|---|---|
| 1 | Temporal Cloud cannot be configured: the installation reads no `temporal.tls`, `temporal.auth` or `expected_retention` | Stores 4, Packaging 2 |
| 2 | A host exits when Temporal or a store is unreachable at startup (four restarts on a fresh k3d cluster); it could retry. And a host whose worker task faults keeps running without it | Packaging 1, Integration 2 |
| 3 | No image is published; `cgr.dev/chainguard/minio:latest` is not pinned; `up.sh` does not check that its images imported | Packaging 3, 4, Integration 5 |
| 4 | The payload codec has no key id, so its key cannot be rotated without losing old histories | Stores 2 |
| 5 | Reconcile rebuilds only run records, not waits and timelines | Stores 3 |
| 6 | A private `git` library is cloned with the host's own Git credentials, not the broker | Stores 1 |
| 7 | Store design notes from review R1: duplicated appends on a lost commit acknowledgement; the lock does not pin a library's commit; symlinks in a library clone; S3 reads are not checked against their hash | Stores, R1 notes |
| 8 | Metrics and logs export (docs/02 section 8 lists step duration, retries, tokens) | Operations 1 |
| 9 | The GitHub Issues tracker reads only above the newest listed issue; give it Jira's gap read (LOG 302) | Operations 2 |
| 10 | Reminders on open waits (an agent's question waited twelve minutes unseen in the live run, with no notifications configured) | Operations 3, M7 14 |
| 11 | The console lists `stalled` and `stuck` under "Decisions"; the console's OIDC sign-in; the ledger and a triage in the console | Operations 4, M7 15 |
| 12 | Several orchestration replicas each send their own `stalled`, `stuck` and `resumed` | Operations 7 |
| 13a | A Temporal outage during a running or completing activity is not tested | Integration 6 |
| 13 | A model the agent worker cannot reach shows on the timeline (LOG 326) but is not a `stalled` position or a notification | Integration 1 |
| 14 | One model proxy serves every runtime; a runtime on another upstream needs a proxy per runtime instance. The proxy records but does not pin each request's model | Agents 1, 6 |
| 15 | Copilot CLI: the hook's `continue: false` with the engine unreachable is untested; the agent can add its own skills or agents to Copilot's sticky home; `CopilotCliRuntime` repeats part of `ClaudeCodeRuntime` | Agents 2 to 4 |
| 16 | No sweeper removes sandbox pods or containers whose run has closed | Agents 5 |
| 17 | `GET /runs/{id}/findings` does not check that the caller may see the run | Integration 3 |
| 18 | A completed run leaves its work branches and `refs/feature-flow/wip/*` on the code host; a run's clean-up could also delete its session branches | Integration 4, M7 12 |
| 19 | `merge.require_checks` locked on refuses only a literal `false` at run start | M7 5 |
| 20 | F1 at patch time: a patch adding an engine merge into a default branch is caught only at merge time | M7 8 |
| 21 | Role checks on containers' inner agent steps; protected branches of a parallel; a twice-sent library override records twice | M7 10 |
| 22 | Pre-submit checks of agent steps | M7 11 |
| 23 | A load check refusing `checks_from` naming a step other than `artifact.validate`; checks capped and cancelled at run end; a test of the `submitting` guard; a gentler re-open of a session that failed to open | M7 13 |
| 24 | A woken run is chosen among the 200 most recently updated runs; a Slack message updated when answered | M7 16 |
| 25 | A decline from a triage with `post_declines: false` is still posted when the pull request later opens | M7 17 |
| 26 | A plan's `artifact.import` stops for a person on a type error; type-check the artifacts on a person's decision answer | M7 19, 20 |
| 27 | Still open from M6: `verification_satisfaction` and the trace checks; the work-item budget's overshoot; `EpicRunTests`' per-child durations; a live hand-over during a CI wait; the Copilot findings source unseen; M5 follow-ups 3 and 7; watch activities per run; parent counts lag a child's; `wait.condition` on a tracker issue | M7 21 |
