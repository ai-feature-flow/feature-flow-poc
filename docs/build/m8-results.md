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
