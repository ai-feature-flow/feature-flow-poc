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
| Each history takes the path it covers (14 rows) | A change to any covered path fails a replay rather than passing unseen |
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
