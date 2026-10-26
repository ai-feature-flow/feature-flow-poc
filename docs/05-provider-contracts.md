# Provider method contracts

Status: first pass, revised for review 06. Date: 2026-10-26. Deliverable 2 of
`brief.md` section 11. Companion to `docs/04-provider-interfaces.md`, which holds
the signatures. This document holds the contract for each method.

**Scope.** Every method gets: required capability, idempotency strategy, timeout
and heartbeat, error codes, and semantics where the signature alone is ambiguous.
Inputs and outputs are given by the signatures; this document states what they
*mean* and what the caller may rely on.

**Review 06 applied.** Sandbox commands are **started and polled**, not awaited
(C1): `StartCommandAsync`, `AttachCommandAsync`, `GetCommandStatusAsync`,
`CancelCommandAsync` replace `ExecuteAsync`, and `IProgress` is gone. The hook
endpoint distinguishes **unreachable from denied** (C2). `MergeResult` states
whether the branch changed (C3). Findings are keyed by scope plus fingerprint and
triage is **append-only** (C4). The inbound gateway is defined (C5), and
`GetResultAsync` does not block (C6).

---

## 1. How to read a contract

| Element | Meaning |
|---|---|
| Requires | The capability the provider must declare. The caller checks before use and fails with a named missing capability |
| Idempotency | How a retry avoids duplicating work. `key` means the caller passes a deterministic key; `none` means the call is naturally idempotent or safe to repeat |
| Timeout | The default. A step may tighten it, never loosen it beyond the policy ceiling |
| Heartbeat | Whether a call longer than 30 seconds must report progress |
| Errors | Codes beyond the common set in section 2.2. Anything not listed falls back to section 2.2 |
| Semantics | What the caller may rely on, where the signature is ambiguous |

### 1.1 Idempotency keys

A key is `hash(runId, stepId, stableInputs)`. **Stable inputs exclude anything
that changes per attempt**: no timestamps, no attempt numbers, no session
references. A resumed attempt must produce the same key as the attempt it
continues, which is what lets a retry re-attach to an existing sandbox (section
3.6) or adopt an existing branch or PR (S4).

Two mechanisms, per architecture section 7.1:

1. **Marker-based discovery.** The provider writes a stable hidden marker into
   every remote object it creates. On retry the caller searches by marker and
   adopts the existing object.
2. **Provider-side dedupe** where the remote system supports a key.

### 1.2 Timeouts

| Class of call | Default | Heartbeat |
|---|---|---|
| Metadata read | 15s | No |
| Write | 30s | No |
| Sandbox create or dispose | 5 min | Yes |
| Command execution | Step-defined, bounded by `start_to_close` | Yes |
| Artifact put or get | 5 min | Yes for large payloads |
| Review | Step-defined | Yes |

No method waits on human or external completion (section 1.3 of the interfaces
document). A step that must wait uses a durable timer and polls, or an inbound
event.

### 1.3 Error codes

| Reason | Class | Retry | Meaning |
|---|---|---|---|
| `unreachable` | Infrastructure | Yes, backoff | Network or DNS failure |
| `timeout` | Infrastructure | Yes | The call exceeded its timeout. **The work may or may not have happened** — see section 1.4 |
| `server_error` | Infrastructure | Yes | 5xx from the remote |
| `rate_limited` | Infrastructure | Yes, honour `RetryAfter` | 429 |
| `internal_error` | Infrastructure | Yes | Unexpected exception in the provider. Set by the engine's decorator, with a distinct reason so it is visible |
| `unauthenticated` | Infrastructure | Short budget, then fail | 401. Almost always a configuration fault (S9) |
| `forbidden` | Infrastructure | Short budget, then fail | 403. Same |
| `not_found` | Work | No | The object does not exist |
| `conflict` | Work | No | Remote state conflicts: merge conflict, branch already exists |
| `validation_failed` | Work | No | The request was rejected as invalid |
| `rejected` | Work | No | Branch protection or policy refused it |
| `partial` | Work | Step decides | Some of the work happened; usually reconcile then retry once |
| `capability_missing` | Work | No | The provider does not declare the needed capability |

### 1.4 The timeout ambiguity

A `timeout` on a **create** method is ambiguous: the object may have been created.
The contract for every create method is therefore:

> On `timeout`, do not retry blindly. Call the corresponding find-by-marker method
> and adopt the object if it exists. Only if it does not exist, retry the create
> with the same key.

This is why `FindByMarkerAsync` exists on the code host and why sandbox
`CreateAsync` has `AttachAsync` as its pair.

---

## 2. Sandbox provider

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `CreateAsync` | — | `key` | 5 min | Yes | `conflict` (already exists), `capability_missing` (no provider for the requested `Os`) | Creates the environment and performs the checkout. On `timeout`, call `AttachAsync` with the same key before retrying |
| `AttachAsync` | `PersistentAcrossSteps` | `key` | 30s | No | `not_found` (no sandbox for this key) | Re-attaches to a sandbox that outlived a worker crash. Returns `not_found` rather than creating one; the step decides |
| `StartCommandAsync` | — | `key` | 30s | No | `validation_failed` (unknown command) | Starts a command and returns a handle. **Does not wait for it to finish** (review 06, C1) |
| `AttachCommandAsync` | `PersistentAcrossSteps` | `key` | 30s | No | `not_found` | Re-attaches to a **running** command by key after a worker crash. This is what keeps a long command from being lost when the sandbox survives |
| `GetCommandStatusAsync` | — | none | 15s | No | `not_found` | Returns status, exit code, and output since `outputCursor`. Non-blocking. The step polls it behind a durable timer |
| `CancelCommandAsync` | — | `key` | 30s | No | `not_found` | Idempotent. Cancelling a finished command succeeds |
| `CopyInAsync` | — | none | 5 min | Yes | `not_found` (source artifact) | Places an input artifact into the sandbox |
| `CopyOutAsync` | — | none | 5 min | Yes | `not_found` (path) | Returns an `ArtifactRef`; the content goes to the artifact store, not to workflow state |
| `DisposeAsync` | — | `key` | 5 min | Yes | — | Idempotent: disposing twice succeeds. A sandbox that outlives a worker crash is **not** disposed on worker failure |

Notes:

- `CreateAsync` takes the credential from the broker, not from configuration. The
  provider never reads credentials itself.
- **Commands are started and polled, not awaited** (review 06, C1). A command may
  run for hours; awaiting it inside one call would break the "short and bounded"
  rule and would lose the command on a worker crash even when the sandbox
  survives.
- `GetCommandStatusAsync` covers agent steps and test tiers alike. The difference
  is the command and its limits, not the method.
- `StreamingOutput` means incremental output through the cursor. A provider
  without it returns output only at completion, which is conformant but coarser.

---

## 3. Agent runtime provider

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `StartAsync` | — | `key` | 30s | No | `validation_failed` (task or limits), `capability_missing` | Starts the runtime inside the sandbox. Returns a session handle; it does **not** wait for the agent to finish |
| `ResumeAsync` | `SessionResume` | `key` | 30s | No | `not_found` (session gone) | Takes the sandbox handle as well as the session reference, because the session lives inside a sandbox that may have been re-attached or recreated |
| `GetProgressAsync` | — | none | 15s | No | `not_found` | Returns session size or turn count. This is what the attempt-budget progress rule needs (section 3.7) |
| `GetResultAsync` | — | none | 30s | No | `not_found` | **The source of truth for the outcome**: outcome, structured output, produced artifact references, session reference, usage. The session-end hook is a signal, not the source of truth |
| `StopAsync` | — | `key` | 30s | No | — | Idempotent. Stops the runtime; does not dispose the sandbox |

Notes:

- A runtime without `Hooks` gets process-level control only, and the engine
  records the coarser control in the run.
- Model credentials are short-lived and scoped to the step, issued by the broker,
  preferably through a model gateway.

### 3.1 Hook endpoint contract

The engine HTTP contract the runtime calls from inside the sandbox.

| Hook | Payload | Response | Failure behaviour |
|---|---|---|---|
| Session start | run id, step id, per-step token | Ack, policy for the step | Deny: the step fails with `rejected` |
| Before tool call | tool name, arguments | Allow, or deny with a reason | Deny: the tool call is skipped and reported to the runtime |
| After tool call | tool name, result summary, token usage | Ack | — |
| Session end | outcome, session reference | Ack | — |

- Authenticated with the per-step token, scoped to this run and step's hook
  endpoint (section 5.4).
- Token and cost events arrive here, which is how per-step cost is recorded
  without parsing transcripts (section 5.5).
- Heartbeats arrive here, so a stalled agent is distinguishable from a slow one.
- **Events wake, reads confirm** (section 1.3.1 of the interfaces document). The
  session-end hook wakes the workflow; `GetResultAsync` confirms the outcome.

### 3.1.1 Unreachable is not denied

Added for review 06 (C2). If the engine is unreachable — network drop, engine
restart — treating that as a deny would deny every tool call, and the agent would
flail or give up with a wrong outcome. That repeats the prototype's network-drop
failure in a new form.

| Case | Behaviour |
|---|---|
| **Unreachable** | The hook client in the sandbox retries with backoff and **holds the tool call** — the agent pauses — for a bounded time. Past that, the session stops in a resumable state and the step classifies it as **infrastructure** |
| **Denied** | Only when the engine answers deny, or when the engine itself cannot evaluate the policy. The tool call is skipped and reported to the runtime |

So: a hook call that fails does not fail the step, and an unreachable engine is
never a policy decision.

---

## 4. Code host provider

### 4.1 Branches

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `CreateBranchAsync` | `PullRequest` | `key` | 30s | `conflict` (exists at a different SHA) | Creates from `fromRef`. On `timeout`, `FindByMarkerAsync` then adopt |

### 4.2 Pull requests

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `OpenPullRequestAsync` | `PullRequest` | `key` | 30s | `conflict`, `validation_failed` | Writes the hidden marker into the PR body. On `timeout`, find by marker then adopt |
| `UpdatePullRequestAsync` | `PullRequest` | none | 30s | `not_found`, `conflict` | Last write wins. Safe to repeat |
| `GetPullRequestAsync` | `PullRequest` | none | 15s | `not_found` | Returns state, head SHA, approvals, mergeability. This is what "the human approves on the code host and the engine waits" needs (Q5) |
| `ClosePullRequestAsync` | `PullRequest` | `key` | 30s | `not_found` | Idempotent. Used when an abort explicitly asks for it |
| `CreateRevertPullRequestAsync` | `PullRequest` | `key` | 30s | `conflict` | Serves the revert option of a partial delivery (section 7.3) |

### 4.3 Merging

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `MergeBaseForwardAsync` | `PullRequest` | none | 30s | `conflict` | Merges the integration base forward into a ticket branch. **`MergeResult` must state whether the branch changed, and the new head SHA** (review 06, C3), because review 03 decided checks rerun only if the merge changed the branch. A `conflict` routes to an agent step that resolves it, not to a human first (section 6.3) |
| `MergeAsync` | `PullRequest` | `key` | 30s | `conflict`, `rejected` (branch protection), `not_found` | `rejected` means protection or policy refused it. The method must declare the required `MergeMethods` capability |
| `EnqueueMergeAsync` | `MergeQueue` | `key` | 30s | `rejected`, `not_found` | Enqueues and returns. **It does not wait** — the engine polls `GetPullRequestAsync` or receives an inbound event |
| `GetBranchProtectionAsync` | `BranchProtection` | none | 15s | `not_found` | Read-only. Used to decide whether a human must approve on the code host |

### 4.4 Checks

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `GetCheckStatusAsync` | `CheckStatus` | none | 15s | `not_found` | Takes `RepoRef` as well as the SHA: a SHA alone is not unique across repositories. Must include checks that register late |

### 4.5 Review comments

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `ListReviewCommentsAsync` | `ReviewComments` | none | 15s | `not_found` | Returns threads, not flat comments, so a thread can be resolved |
| `AddReviewCommentAsync` | `ReviewComments` | `key` | 30s | `validation_failed`, `not_found` | Writes the marker. Used to post decline reasons |
| `ReplyToReviewCommentAsync` | `ReviewComments` | `key` | 30s | `not_found` | — |
| `ResolveReviewThreadAsync` | `ReviewComments` | `key` | 30s | `not_found` | Idempotent. Closes a thread after a fix or a decline |

### 4.6 Diffs and discovery

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `GetDiffAsync` | `PullRequest` | none | 5 min | `not_found` | Returns an `ArtifactRef`, not the diff. A large aggregate diff never enters workflow state (DP10) |
| `FindByMarkerAsync` | `PullRequest` | none | 30s | — | Searches branches and PRs by marker. Returns empty, not `not_found`, when nothing matches |

Note: `RequestAiReviewAsync` is deliberately absent. An AI review on a PR is a
**review provider** implementation composed with the code host provider.

---

## 5. CI provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `TriggerAsync` | `Trigger` | `key` | 30s | `validation_failed` (unknown tier), `not_found` | Starts a run for a tier at a commit. Returns a reference; does not wait |
| `GetRunStatusAsync` | `Trigger` | none | 15s | `not_found` | Non-blocking. Must handle checks that register late, and must report results for **queue builds** (for example the GitHub Actions `merge_group` event) |
| `ReadResultsAsync` | `Trigger` | none | 30s | `not_found`, `partial` | Structured results. `partial` means some jobs have not finished |
| `ReadLogsAsync` | `ReadLogs` | none | 5 min | `not_found` | Returns a log reference, not the log body |
| `GetArtifactsAsync` | `Artifacts` | none | 5 min | `not_found` | Returns artifact references |

Note: waiting is an engine mechanism. The step polls `GetRunStatusAsync` behind a
durable timer, or waits for an inbound event when the provider declares
`InboundEvents`.

---

## 6. Tracker provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `GetIssueAsync` | `Issues` | none | 15s | `not_found` | — |
| `SearchIssuesAsync` | `Issues` | none | 30s | — | Finds issues by marker (idempotency) and lists an epic's children. Returns empty, not `not_found`, when nothing matches |
| `CreateIssueAsync` | `Issues` | `key` | 30s | `validation_failed` | Writes the marker. On `timeout`, search by marker then adopt |
| `UpdateIssueAsync` | `Issues` | none | 30s | `not_found`, `validation_failed` | Last write wins |
| `TransitionAsync` | `Transitions` | `key` | 30s | `not_found`, `rejected` (workflow refuses the transition) | Idempotent: transitioning to the current state succeeds |
| `LinkAsync` | `Links` | `key` | 30s | `not_found`, `validation_failed` | Idempotent: an existing link of the same type succeeds |
| `CommentAsync` | `Comments` | `key` | 30s | `not_found` | — |

Notes:

- Agents have **no** tracker identity. Tracker writes go through engine steps only
  (section 4.6).
- **Field mapping is configuration, not code.** `IssueSpec` carries engine-level
  fields; the provider maps them to provider-specific fields through its own
  configuration. `CustomFields` declares support.

---

## 7. Review provider

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `ReviewAsync` | `Scopes[scope]` | none | Step-defined | Yes | `validation_failed` (unknown check), `not_found` (input artifact) | Produces structured findings for one scope |

Contract details:

- **`priorFindings` is required.** The prior findings for the same scope come from
  the ledger. Without them a verdict can change between passes on unchanged input,
  which is R4. A provider that ignores them is non-conformant.
- Findings are fingerprinted on rule, repository, path, symbol and normalised
  snippet. **Never on line numbers** (section 7.2). A provider without
  `Fingerprints` cannot guarantee a declined finding stays declined, and the
  engine records that.
- Epic scope reviews the aggregate diff (DP10).
- The same provider serves the **on-demand** check action in interactive sessions
  (section 7.4), so a check never appears for the first time after submission.
- Fix verification passes the original finding as an input, for the same reason
  `priorFindings` is required.

---

## 8. Artifact store provider

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `PutAsync` | `ContentAddressed` | By content hash | 5 min | Yes | `validation_failed` (empty) | Immutable. Putting the same content twice returns the same reference |
| `GetAsync` | — | none | 5 min | Yes | `not_found` | — |
| `GetSignedUrlAsync` | `SignedUrls` | none | 15s | — | `capability_missing` | Short TTL only |

Notes:

- Immutable and content-addressed (Q2). A new version is a new hash plus a named
  pointer, and **the pointer lives in the record store** (section 9).
- Holds specs, plans and every plan revision, diffs, transcripts, logs and
  reports. Workflow state holds references only.

---

## 9. Record store provider

| Method | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|
| `UpsertRunAsync` | By run id | 15s | — | Last write wins |
| `UpsertStepRunAsync` | By step run id | 15s | — | Last write wins |
| `AppendTimelineAsync` | none | 15s | — | **Append-only.** Never updated or deleted |
| `RecordFindingAsync` | By **scope plus fingerprint** | 15s | — | Records the finding. Keyed by run, scope, repository and fingerprint: the same fingerprint can legitimately appear at ticket and epic scope, and in later runs (review 06, C4) |
| `RecordTriageAsync` | none | 15s | — | **Append-only.** One `TriageRecord` per decision, with a supersedes link. The current triage is the latest record |
| `RecordDecisionAsync` | By decision id | 15s | — | Append-only in practice: a decision is not revised |
| `RecordSteeringAsync` | By steering id | 15s | — | Append-only. Audited |
| `RecordCostAsync` | By step run id | 15s | — | Accumulates per step run |
| `UpsertDeliveryLedgerAsync` | By repository and run | 15s | — | Which repositories delivered, and their status |
| `PutPointerAsync` | By name | 15s | — | Named pointer to an artifact. This is where pointers live |
| `ResolvePointerAsync` | none | 15s | `not_found` | — |
| `QueryAsync` | none | 30s | `validation_failed` (unknown query name) | Named queries only. The catalogue lives in the engine, in one place |

Notes:

- Relational, queried through the API (Q3). This is the long-term audit record
  (Q13).
- **Findings are keyed by scope plus fingerprint**, and **triage is append-only**
  (review 06, C4). A finding is still triaged once in effect (D8) — a declined
  finding stays declined until the code its fingerprint points at changes — but
  the history of decisions is kept rather than rejected with `conflict`.
- The workflow reads the delivery ledger through an activity, never directly
  (section 7.3).
- `org_id` is part of the key from version 1 (Q15).
- **Query catalogue ownership** (review 06, C7): a distribution may add
  **read-only** named queries over published views, shipped in its own package. No
  third-party schema changes in version 1.

---

## 10. Secrets provider

| Method | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|
| `ResolveAsync` | none | 15s | `not_found`, `forbidden` | Returns the value. Secrets are referenced from configuration, never stored in it. Values are scrubbed at the logging layer |

---

## 11. Notifications provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `SendAsync` | `Channels[channel]` | `key` | 30s | `validation_failed` (unknown channel), `not_found` | Fire and forget. A failure is infrastructure and retries |
| `PostDecisionRequestAsync` | `Interactive` | `key` | 30s | `capability_missing`, `not_found` | Posts the request and returns a message reference. **Does not wait for the answer** |

The answer arrives through an inbound endpoint, which the provider translates into
an **API decision call** — the same path as any other decision (Q4). The policy
model states which decisions may be made from chat (section 5.4).

---

## 12. Credential broker

| Method | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|
| `IssueAsync` | none | 15s | `forbidden` (scope not grantable), `validation_failed` | Returns a short-lived credential for an actor, target system and scope |
| `RefreshAsync` | none | 15s | `forbidden`, `not_found` (expired beyond refresh) | — |

Notes:

- **No provider reads credentials directly from configuration.** This is what
  keeps identity choices from leaking into provider interfaces (section 4.6).
- Agents in the sandbox get a token minted per step: one repository, minimum
  permissions, about an hour, refreshable through the hook endpoint.

---

## 13. Session surface provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `PrepareAsync` | — | `key` | 5 min | `validation_failed` | Prepares the workspace and returns how the human reaches it |

The surface **only** prepares the workspace. Submission and running checks are API
actions, not provider methods (review 05, P1):

- Submission comes from the human's CLI or MCP through the API and becomes an
  Update.
- Checks are run by the engine — validators and review providers — not by the
  surface. The human's CLI or MCP calls the API, and the engine runs the check.

Hooks and policy do not apply in the human's environment. The trust boundary is
the submission, and the human is the accountable actor in the audit record.

---

## 14. Cross-cutting rules

| Rule | Detail |
|---|---|
| Capability check | The caller checks before use and fails with a named missing capability. It never assumes success |
| Credentials | Through the broker only |
| Observability | The engine decorates every call, so providers get spans, timeouts and error classification without extra code. Providers only add attributes |
| Contract tests | One suite per interface, shipped with the engine (S5). A provider is conformant only if it passes |
| No waiting | No method waits on human or external completion |
| Timeout ambiguity | On `timeout` from a create method, find by marker before retrying |

---

## 15. Open questions

All five questions from the first pass were answered in review 06 and are applied
above:

| # | Question | Answer |
|---|---|---|
| 1 | The `InboundEvents` endpoint contract | One inbound gateway in the API: verify the signature, deduplicate by remote delivery id, translate to an engine event. Events are Signals that only wake the workflow (interfaces section 1.3.2) |
| 2 | `IProgress<CommandProgress>` | Removed. Commands are started and polled, so incremental output comes through a cursor on `GetCommandStatusAsync`, which is serialisable (section 2) |
| 3 | Should `GetResultAsync` block | No. The session-end hook wakes the workflow; `GetResultAsync` confirms; polling is the fallback if the event is lost (section 3) |
| 4 | Reject a re-triage, or append | **Append-only.** One `TriageRecord` per decision with a supersedes link; the current triage is the latest record (section 9) |
| 5 | Query catalogue ownership | A distribution may add **read-only** named queries over published views, shipped in its own package. No third-party schema changes in version 1 (section 9) |

### Still open

None carried forward. The general rule that answered several of these — **events
wake, reads confirm** — is in section 1.3.1 of the interfaces document.
